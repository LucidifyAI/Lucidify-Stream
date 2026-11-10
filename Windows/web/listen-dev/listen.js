// Lucidify Listen: reference web client for the Lucid-Stream WebSocket API (schema 1.x).
// Shows the full same-device flow: token from the URL fragment, explain-then-connect on a user gesture,
// hello/welcome/subscribe, binary + JSON frames, reconnect with resume, close-code handling, Web Audio sonification.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);
  const BANDS = ['delta', 'theta', 'alpha', 'beta', 'gamma'];
  const VOICES = { delta: 110, theta: 164.81, alpha: 220, beta: 329.63, gamma: 440 }; // A minor-ish stack

  // ---- 1. Read the launch token from the fragment, then remove it from the address bar and history.
  const params = new URLSearchParams(location.hash.slice(1));
  const token = params.get('token');
  const port = Number(params.get('port')) || 8765;
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  if (!token) { $('noToken').classList.remove('hidden'); return; }
  $('intro').classList.remove('hidden');

  // ---- 2. Browser permission for reaching the app on this device (Chrome Local Network Access).
  (async () => {
    for (const name of ['loopback-network', 'local-network-access']) {
      try {
        const st = await navigator.permissions.query({ name });
        if (st.state === 'denied') $('denied').classList.remove('hidden');
        st.onchange = () => $('denied').classList.toggle('hidden', st.state !== 'denied');
        return;
      } catch (_) { /* permission name not supported by this browser */ }
    }
  })();

  let ws = null, audio = null, stopped = false, retryMs = 500, streamsById = {}, sessionId = null;
  const lastIndex = {};               // stream key -> last sample index received (for resume)
  const rel = Object.fromEntries(BANDS.map((b) => [b, 0]));
  const scope = { n: 2500, fp1: new Float32Array(2500), fp2: new Float32Array(2500), pos: 0 };

  $('start').addEventListener('click', () => {
    $('intro').classList.add('hidden');
    $('live').classList.remove('hidden');
    audio = makeSynth();
    buildBars();
    connect();
    requestAnimationFrame(draw);
  });
  $('stop').addEventListener('click', () => {
    stopped = true;
    if (ws) ws.close(1000, 'stopped by user');
    if (audio) audio.ctx.suspend();
    setConn('Stopped', true);
  });
  $('volume').addEventListener('input', (e) => { if (audio) audio.master.gain.setTargetAtTime(Number(e.target.value) * 0.25, audio.ctx.currentTime, 0.05); });

  // ---- 3. Connect, hello, subscribe.
  function connect() {
    setConn('Connecting');
    ws = new WebSocket(`ws://localhost:${port}/v1/stream`);
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => {
      const hello = { type: 'hello', schema: '1.0', token, client: 'lucidify-listen-dev/0.1' };
      if (Object.keys(lastIndex).length) hello.resume_from = lastIndex;
      ws.send(JSON.stringify(hello));
    };
    ws.onmessage = (ev) => (typeof ev.data === 'string' ? onJson(JSON.parse(ev.data)) : onBinary(ev.data));
    ws.onclose = (ev) => onClose(ev.code);
  }

  function onJson(m) {
    switch (m.type) {
      case 'welcome':
        retryMs = 500;
        streamsById = Object.fromEntries(m.streams.map((s) => [s.id, s]));
        if (sessionId && sessionId !== m.session) for (const k in lastIndex) delete lastIndex[k];
        sessionId = m.session;
        $('device').textContent = `${m.device.model} ${m.device.serial}`;
        $('mode').classList.toggle('hidden', m.mode !== 'replay');
        setConn('Live');
        ws.send(JSON.stringify({ type: 'subscribe', streams: ['eeg.filtered'], encoding: 'binary' }));
        ws.send(JSON.stringify({ type: 'subscribe', streams: ['bandpower', 'quality'], encoding: 'json' }));
        break;
      case 'data':
        lastIndex[m.stream] = m.index + m.data.length - 1;
        if (m.stream === 'bandpower') onBandPower(m.data[0]);
        break;
      case 'status':
        $('battery').textContent = m.battery_pct != null ? `Battery ${m.battery_pct}%` : '';
        if (m.connected === false) setConn('Patch disconnected', true);
        else if (ws && ws.readyState === 1) setConn('Live');
        break;
      case 'gap':
        if (m.reason === 'ble_packet_loss' && !m.filled) message(`Signal dropped for ${(m.count / 500).toFixed(1)} s.`);
        break;
      case 'error':
        console.warn('Lucid-Stream:', m.message);
        break;
    }
  }

  // Binary frame: 32-byte little-endian header, then float32 samples interleaved by channel.
  function onBinary(buf) {
    const v = new DataView(buf);
    if (v.getUint32(0, false) !== 0x4c434446) return; // "LCDF"
    const id = v.getUint8(5), index = Number(v.getBigUint64(12, true));
    const n = v.getUint16(28, true), ch = v.getUint16(30, true);
    const s = streamsById[id];
    if (!s) return;
    lastIndex[s.key] = index + n - 1;
    if (s.key !== 'eeg.filtered') return;
    const data = new Float32Array(buf, 32, n * ch);
    for (let i = 0; i < n; i++) {
      scope.fp1[scope.pos] = data[i * ch];
      scope.fp2[scope.pos] = data[i * ch + 1];
      scope.pos = (scope.pos + 1) % scope.n;
    }
  }

  // ---- 4. Close codes from the spec.
  function onClose(code) {
    if (stopped) return;
    const final = {
      4001: 'This link is no longer valid. Tap Listen in Lucid-Stream again.',
      4005: 'This page is not approved to connect to Lucid-Stream.',
      4006: 'Connection refused by Lucid-Stream.',
      4007: 'Listening ended in Lucid-Stream. Tap Listen to start again.',
      4009: 'Listening continued in another tab.',
    }[code];
    if (final) { setConn('Ended', true); message(final); return; }
    setConn('Reconnecting', true);
    setTimeout(connect, retryMs);
    retryMs = Math.min(8000, retryMs * 2);
  }

  // ---- 5. Sound: one soft voice per band; each voice's loudness follows that band's relative power.
  function makeSynth() {
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    const master = ctx.createGain();
    master.gain.value = Number($('volume').value) * 0.25;
    const lp = ctx.createBiquadFilter();
    lp.type = 'lowpass'; lp.frequency.value = 1800;
    master.connect(lp).connect(ctx.destination);
    const voices = {};
    for (const b of BANDS) {
      const osc = ctx.createOscillator();
      osc.type = b === 'gamma' ? 'triangle' : 'sine';
      osc.frequency.value = VOICES[b];
      const g = ctx.createGain(); g.gain.value = 0;
      osc.connect(g).connect(master); osc.start();
      voices[b] = { osc, g };
    }
    return { ctx, master, voices };
  }

  function onBandPower(row) {
    // Row layout per channel: 5 absolute bands, then 5 relative bands (Fp1 then Fp2).
    BANDS.forEach((b, i) => {
      const a = row[5 + i], c = row[15 + i];
      const vals = [a, c].filter((x) => x !== null && Number.isFinite(x));
      if (vals.length) rel[b] = vals.reduce((p, q) => p + q, 0) / vals.length;
    });
    if (audio) {
      const t = audio.ctx.currentTime;
      for (const b of BANDS) audio.voices[b].g.gain.setTargetAtTime(Math.min(1, rel[b] * 1.5), t, 0.4);
      // Alpha also lifts its own pitch slightly, so relaxing is audible as a rising tone.
      audio.voices.alpha.osc.frequency.setTargetAtTime(VOICES.alpha * (1 + 0.25 * rel.alpha), t, 0.6);
    }
    for (const b of BANDS) {
      $(`bar-${b}`).style.width = `${Math.round(rel[b] * 100)}%`;
      $(`val-${b}`).textContent = `${Math.round(rel[b] * 100)}%`;
    }
  }

  // ---- UI helpers
  function buildBars() {
    const host = $('bars');
    for (const b of BANDS) {
      const label = document.createElement('span'); label.textContent = b;
      const bar = document.createElement('div'); bar.className = 'bar';
      const fill = document.createElement('div'); fill.id = `bar-${b}`; bar.appendChild(fill);
      const val = document.createElement('span'); val.id = `val-${b}`; val.className = 'muted small'; val.textContent = '0%';
      host.append(label, bar, val);
    }
  }

  function draw() {
    const c = $('scope'), g = c.getContext('2d');
    const w = c.width = c.clientWidth * devicePixelRatio, h = c.height = c.clientHeight * devicePixelRatio;
    g.clearRect(0, 0, w, h);
    const css = getComputedStyle(document.documentElement);
    const lanes = [[scope.fp1, css.getPropertyValue('--fp1'), h * 0.28], [scope.fp2, css.getPropertyValue('--fp2'), h * 0.72]];
    const scaleUv = 60; // +/- 60 uV per lane
    for (const [buf, color, mid] of lanes) {
      g.strokeStyle = color.trim(); g.lineWidth = 1.5 * devicePixelRatio; g.beginPath();
      for (let x = 0; x < scope.n; x += 2) {
        const v = buf[(scope.pos + x) % scope.n];
        const y = mid - (Number.isFinite(v) ? Math.max(-1, Math.min(1, v / scaleUv)) : 0) * h * 0.2;
        const px = (x / scope.n) * w;
        x === 0 ? g.moveTo(px, y) : g.lineTo(px, y);
      }
      g.stroke();
    }
    if (!stopped) requestAnimationFrame(draw);
  }

  function setConn(text, off) { const el = $('conn'); el.textContent = text; el.classList.toggle('off', !!off); }
  function message(text) { $('message').textContent = text; }
})();