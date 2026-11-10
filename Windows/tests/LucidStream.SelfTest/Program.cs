using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LucidStream.Core.Cloud;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Dsp;
using LucidStream.Core.Engine;
using LucidStream.Core.Lsl;
using LucidStream.Core.Recording;
using LucidStream.Core.Streams;
using LucidStream.Core.Ws;

// Lucid-Stream self-tests. Covers the spec's test table items that can run without a patch:
// framing, features, filters, Origin/Host/token rules, signed allowlist, gap handling, the WebSocket
// handshake and close codes end to end, reconnect/resume, replay parity, and an LSL round trip when liblsl is present.

var t = new Harness();
var quiet = !args.Contains("--verbose");
if (!quiet) Log.Written += e => Console.WriteLine("   log: " + e);
LslNative.Initialize();

t.Section("Binary frame codec");
{
    var dev = DeviceInfo.LucidPro("SN1", "fw", 25);
    var cat = new StreamCatalog(dev);
    var f = new StreamFrame { Def = cat[StreamKeys.EegRaw], Seq = 7, Index = 1203380, Timestamps = Enumerable.Range(0, 20).Select(i => 51240.12 + i / 500.0).ToArray(), Data = Enumerable.Range(0, 40).Select(i => (float)i).ToArray(), HasNaN = true, Replay = true };
    var bytes = BinaryFrameCodec.Encode(f);
    t.Check("size = 32 + 4*20*2", bytes.Length == 32 + 160);
    t.Check("magic LCDF", Encoding.ASCII.GetString(bytes, 0, 4) == "LCDF");
    var d = BinaryFrameCodec.Decode(bytes);
    t.Check("header fields round-trip", d.StreamId == 0 && d.Seq == 7 && d.Index == 1203380 && Math.Abs(d.T0 - 51240.12) < 1e-9 && d.Samples == 20 && d.Channels == 2);
    t.Check("flags NaN|replay", d.Flags == 3);
    t.Check("samples round-trip", d.Data.SequenceEqual(f.Data!));
}

t.Section("CGX Patch packets");
{
    // Values chosen around the sign boundary and band edges, where a plain 24-bit read goes wrong.
    var values = new[] { 0, 8, -8, 1016, 1024, -1024, 131064, 131072, -131072, 5_000_000, -5_000_000 };
    var okRound = true;
    var noFF = true;
    var buf = new byte[3];
    foreach (var v in values)
    {
        CgxPatchFormat.Encode24(v, buf);
        if (buf[0] == 0xFF || buf[1] == 0xFF || buf[2] == 0xFF || ((buf[0] | buf[1] | buf[2]) & 1) != 0) noFF = false;
        var pkt = new byte[36];
        pkt[0] = 0xFF;
        buf.CopyTo(pkt, 2);
        if (CgxPatchFormat.Sample24(pkt, 0, CgxPacking.Cgx7Bit) != v) okRound = false;
    }
    t.Check("CGX 7-bit packing round-trips signed values", okRound);
    t.Check("encoded bytes never 0xFF and always even", noFF);
    var minus8 = new byte[36];
    CgxPatchFormat.Encode24(-8, minus8.AsSpan(2));
    t.Check($"-8 counts read plainly is {CgxPatchFormat.Sample24(minus8, 0, CgxPacking.Plain24)} (the Android/Cloud decode error)",
        CgxPatchFormat.Sample24(minus8, 0, CgxPacking.Plain24) == -65794);

    byte[] Packet(int counter, double afzUv, double fp2Uv, double impOhm = 10_000, double accZg = 1.0, double volts = 4.0, int sd = 1)
    {
        var p = new byte[36];
        p[0] = 0xFF;
        p[1] = (byte)(counter & 0x7F);
        void Put(int ch, int v24) => CgxPatchFormat.Encode24(v24, p.AsSpan(2 + ch * 3));
        Put(0, (int)Math.Round(afzUv / CgxPatchFormat.MicrovoltsPerCount));
        Put(1, (int)Math.Round(fp2Uv / CgxPatchFormat.MicrovoltsPerCount));
        var imp = (int)Math.Round((impOhm - CgxPatchFormat.ImpedanceOffsetOhms) / CgxPatchFormat.ImpedanceOhmsPerCount);
        Put(2, imp);
        Put(3, imp);
        Put(9, (int)Math.Round(accZg / CgxPatchFormat.AccelGPerCount * 256));
        p[32] = 0x12;
        p[33] = (byte)Math.Round(volts * 25);
        p[35] = (byte)sd;
        return p;
    }

    var d = CgxPatchFormat.Decode(Packet(5, 25.0, -40.0), CgxPacking.Cgx7Bit);
    t.Check($"EEG scale: AFz {d.AfzUv:0.00} uV, Fp2 {d.Fp2Uv:0.00} uV (within 0.4 uV)", Math.Abs(d.AfzUv - 25) < 0.4 && Math.Abs(d.Fp2Uv + 40) < 0.4);
    t.Check($"impedance {d.ImpedanceAfzOhm:0} ohm near 10 kOhm", Math.Abs(d.ImpedanceAfzOhm - 10_000) < 250);
    t.Check($"accelerometer Z {d.AccZ:0.000} g, battery {d.BatteryVolts:0.00} V, SD {d.SdCard}", Math.Abs(d.AccZ - 1) < 0.01 && Math.Abs(d.BatteryVolts - 4.0) < 0.01 && d.SdCard == 1);

    var parser = new CgxStreamParser();
    var eeg = new List<EegBlock>();
    var acc = new List<AccelBlock>();
    var stat = new List<DeviceStatus>();
    var raw = new List<CgxRawBlock>();
    parser.Eeg += eeg.Add;
    parser.Accel += acc.Add;
    parser.Status += stat.Add;
    parser.Raw += raw.Add;

    // 20 packets, split at awkward places, with junk in front.
    var stream = new List<byte> { 0x12, 0x34 };
    for (var i = 0; i < 20; i++) stream.AddRange(Packet(i, 10 + i, -10 - i));
    var bytes = stream.ToArray();
    parser.Feed(bytes.AsSpan(0, 50), 100.0);
    parser.Feed(bytes.AsSpan(50, 301), 100.1);
    parser.Feed(bytes.AsSpan(351), 100.2);
    var samples = eeg.Sum(b => b.SampleCount);
    t.Check($"framing across splits: {samples} samples, {parser.BytesDiscarded} junk bytes skipped", samples == 20 && parser.BytesDiscarded == 2);
    t.Check("sample indexes continuous", IsContiguous(eeg));
    t.Check("EEG channel order AFz, Fp2", Math.Abs(eeg[0].Samples[0] - 10) < 0.4 && Math.Abs(eeg[0].Samples[1] + 10) < 0.4);
    t.Check("raw packets kept unchanged", raw.Sum(r => r.Count) == 20 && raw[0].Packets.AsSpan(0, 36).SequenceEqual(Packet(0, 10, -10)));
    t.Check("accelerometer averaged to 50 Hz", acc.Sum(a => a.SampleCount) == 2 && Math.Abs(acc[0].Samples[2] - 1) < 0.01);

    // Drop counters 20-24: five lost packets, then a broken packet that contains 0xFF.
    var broken = Packet(25, 0, 0);
    broken[10] = 0xFF;
    eeg.Clear();
    parser.Feed(broken.AsSpan(0, 20).ToArray().Concat(Packet(25, 1, 1)).Concat(Packet(26, 1, 1)).ToArray(), 100.3);
    t.Check($"lost packets from the counter: {parser.PacketsLost}", parser.PacketsLost == 5);
    t.Check("index jumps over the loss", eeg.Count == 1 && eeg[0].DeviceCounter == 25 && eeg[0].SampleCount == 2);

    // A Bluetooth drop of 2 s: the 7-bit counter wraps, so the gap is sized from time.
    parser.NoteLinkLost();
    eeg.Clear();
    parser.Feed(Packet((27 + 1000) % 128, 1, 1), 102.3);
    t.Check($"long outage sized from time: index {eeg[0].DeviceCounter} (expected 1027)", eeg[0].DeviceCounter == 1027);

    // Status after one second of packets.
    var p2 = new CgxStreamParser();
    var st = new List<DeviceStatus>();
    p2.Status += st.Add;
    var sec = new List<byte>();
    for (var i = 0; i < 500; i++) sec.AddRange(Packet(i, 50_000, 350_000 * (i % 2 == 0 ? 1 : 0.9), impOhm: 15_000, volts: 3.88, sd: 0));
    p2.Feed(sec.ToArray(), 10);
    t.Check("status once per second", st.Count == 1);
    var s0 = st[0];
    t.Check($"status battery {s0.BatteryPercent}% ({s0.BatteryVolts} V), SD {s0.SdCardPresent}", s0.BatteryPercent == 58 && s0.SdCardPresent == false);
    t.Check($"status impedance {s0.ImpedanceKohm[0]} kOhm", Math.Abs(s0.ImpedanceKohm[0]!.Value - 15) < 0.3);
    t.Check($"contact from DC offset: AFz {s0.DcOffsetMv[0]} mV ok, Fp2 {s0.DcOffsetMv[1]} mV flagged", s0.Contact[0] && !s0.Contact[1]);
}

static bool IsContiguous(List<EegBlock> blocks)
{
    long next = 0;
    foreach (var b in blocks)
    {
        if (b.DeviceCounter != next) return false;
        next += b.SampleCount;
    }
    return true;
}

t.Section("Cloud recording (fake Cloud server)");
{
    var cloud = await FakeCloud.StartAsync();
    var root = Path.Combine(Path.GetTempPath(), "lucidstream-cloud-" + Guid.NewGuid().ToString("N")[..8]);
    var api = new CloudApi(cloud.BaseUrl, _ => Task.FromResult("test-token"));
    var target = new CloudTarget("org1", "grp1", "subj1", "Test Org", "Patient 7");
    var rec = CloudRecorder.Start(api, target, root, new PlainProtector());
    string? bound = null;
    rec.SessionBound += (sid, _) => bound = sid;

    byte[] Pkt(int counter)
    {
        var p = new byte[36];
        p[0] = 0xFF;
        p[1] = (byte)(counter & 0x7F);
        p[33] = 100;
        p[35] = 1;
        return p;
    }
    CgxRawBlock Block(long first, int n) => new(Enumerable.Range(0, n).SelectMany(i => Pkt((int)(first + i))).ToArray(), first, 0);

    // Device indexes start at 1000; 6000 packets = 12 s, then a 100-packet loss, then 1000 more.
    for (long i = 0; i < 6000; i += 100) rec.OnRaw(Block(1000 + i, 100));
    for (long i = 6100; i < 7100; i += 100) rec.OnRaw(Block(1000 + i, 100));
    rec.AddMarker("eyes closed");
    rec.Stop();
    var finished = await rec.WaitFinishedAsync(TimeSpan.FromSeconds(20));
    var st = rec.Status;
    t.Check($"session created and bound ({bound})", bound == "sess-1" && cloud.SessionInits == 1);
    t.Check($"finalized ({st.State}, {st.Problem})", finished && st.State == CloudRecState.Done && cloud.Finalized);
    var chunks = cloud.Chunks.Values.OrderBy(c => c.Seq).ToList();
    t.Check($"chunks: {string.Join(" ", chunks.Select(c => $"{c.Seq}:{c.First}+{c.Count}"))}",
        chunks.Count == 4 && chunks[0].First == 0 && chunks[0].Count == 2500 && chunks[1].First == 2500 && chunks[2].First == 5000 && chunks[2].Count == 1000
        && chunks[3].First == 6100 && chunks[3].Count == 1000);
    t.Check("payload is the raw packets, verified by SHA-256", chunks.All(c => c.Verified) && chunks[0].Payload.Length == 2500 * 36 && chunks[0].Payload[36] == 0xFF);
    t.Check("times from sample index (tStartMs of chunk 3 = 12200)", chunks[3].TStartMs == 12200 && chunks[3].TEndMs == 14200);
    t.Check("upload never sends the bearer token to the signed URL", !cloud.PutSawAuthorization);
    t.Check("idempotency keys on init/complete", cloud.IdempotencyKeys.Any(k => k.EndsWith(":0:init")) && cloud.IdempotencyKeys.Any(k => k.EndsWith(":0:complete")));
    t.Check("marker posted as a session event", cloud.Events.Contains("eyes closed"));
    t.Check("spool removed after finalize", !Directory.EnumerateDirectories(root).Any());
    await rec.DisposeAsync();

    // A recording left behind by a closed app is finished on the next start.
    cloud.Reset();
    var rec2 = CloudRecorder.Start(new CloudApi(cloud.BaseUrl, _ => throw new CloudApiException(401, "signed out")), target, root, new PlainProtector());
    for (long i = 0; i < 3000; i += 100) rec2.OnRaw(Block(i, 100));
    await Task.Delay(300);
    await rec2.DisposeAsync();
    var left = CloudSpool.Unfinished(root).ToList();
    t.Check("interrupted recording stays in the spool", left.Count == 1 && cloud.Chunks.Count == 0);
    var rec3 = CloudRecorder.Resume(api, left[0], new PlainProtector());
    var finished3 = await rec3.WaitFinishedAsync(TimeSpan.FromSeconds(20));
    t.Check($"resumed and finalized ({rec3.Status.State}, {cloud.Chunks.Count} chunk)", finished3 && rec3.Status.State == CloudRecState.Done && cloud.Chunks.Count == 1 && cloud.FinalSeq == 1);
    await rec3.DisposeAsync();
    await cloud.DisposeAsync();
    try { Directory.Delete(root, true); } catch { }
}

t.Section("Band power and filters");
{
    var bp = new BandPowerExtractor(2, 500);
    float[]? last = null;
    for (var i = 0; i < 1500; i++)
    {
        var s = new[] { (float)(20 * Math.Sin(2 * Math.PI * 10 * i / 500.0)), (float)(20 * Math.Sin(2 * Math.PI * 6 * i / 500.0)) };
        last = bp.Add(s) ?? last;
    }
    t.Check("features produced", last != null && last.Length == 20);
    // Channel 1 rel alpha is index 5 + 2; channel 2 rel theta is 10 + 5 + 1.
    t.Check($"10 Hz sine -> alpha rel {last![7]:0.000} > 0.95", last[7] > 0.95);
    t.Check($"6 Hz sine -> theta rel {last[16]:0.000} > 0.95", last[16] > 0.95);
    var expectedAbs = 20.0 * 20.0 / 2.0; // sine power
    t.Check($"alpha abs {last[2]:0.0} uV^2 near {expectedAbs}", Math.Abs(last[2] - expectedAbs) / expectedAbs < 0.1);

    double Gain(double hz)
    {
        var flt = new EegFilter(1, 500, 60);
        var input = Enumerable.Range(0, 5000).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / 500.0)).ToArray();
        var output = new float[input.Length];
        flt.Process(input, output, 1);
        var tail = output.Skip(3000).ToArray();
        return Math.Sqrt(tail.Average(v => v * v)) / Math.Sqrt(0.5);
    }
    t.Check($"10 Hz passes (gain {Gain(10):0.00})", Math.Abs(20 * Math.Log10(Gain(10))) < 1);
    t.Check($"60 Hz notch (gain {Gain(60):0.0000})", 20 * Math.Log10(Gain(60)) < -30);
    t.Check($"0.1 Hz drift removed (gain {Gain(0.1):0.00})", Gain(0.1) < 0.2);
}

t.Section("Origin and Host policy");
{
    var doc = new AllowlistDocument { Version = 3, Origins = { "https://App.Example.com/", "http://insecure.example.com" }, DevOrigins = { "http://localhost:8766" } };
    var prod = new OriginPolicy(doc, developerMode: false);
    var dev = new OriginPolicy(doc, developerMode: true);
    t.Check("allowlisted https origin accepted (normalized)", prod.IsAllowed("https://app.example.com"));
    t.Check("same host, other port rejected", !prod.IsAllowed("https://app.example.com:8443"));
    t.Check("look-alike host rejected", !prod.IsAllowed("https://app.example.com.evil.net"));
    t.Check("http origin in prod list ignored", !prod.IsAllowed("http://insecure.example.com"));
    t.Check("'null' origin rejected", !prod.IsAllowed("null"));
    t.Check("dev origin rejected outside developer mode", !prod.IsAllowed("http://localhost:8766"));
    t.Check("dev origin accepted in developer mode", dev.IsAllowed("http://localhost:8766"));
    var user = new OriginPolicy(new AllowlistDocument(), false, new[] { "https://lab.example.org/", "http://localhost:5173", "http://lab.example.org" });
    t.Check("user-allowed https origin accepted", user.IsAllowed("https://lab.example.org"));
    t.Check("user-allowed http://localhost origin accepted", user.IsAllowed("http://localhost:5173"));
    t.Check("user http origin off localhost ignored", !user.IsAllowed("http://lab.example.org"));
    t.Check("nothing allowed by default", !new OriginPolicy(new AllowlistDocument(), false).IsAllowed("https://lab.example.org"));
    var pasted = new OriginPolicy(new AllowlistDocument(), false, new[] { "https://www.stationantiques.ca/audiostudio" });
    t.Check("pasted page address allows its site, with and without www", pasted.IsAllowed("https://stationantiques.ca") && pasted.IsAllowed("https://www.stationantiques.ca"));
    t.Check("bare host name means https", OriginPolicy.UserEntryToOrigins("lab.example.org").Contains("https://lab.example.org"));
    pasted.SetUserOrigins(Array.Empty<string>());
    t.Check("list changes apply right away", !pasted.IsAllowed("https://stationantiques.ca"));
    t.Check("Host localhost:8765 ok", OriginPolicy.IsLoopbackHostAllowed("localhost:8765", 8765));
    t.Check("Host [::1]:8765 ok", OriginPolicy.IsLoopbackHostAllowed("[::1]:8765", 8765));
    t.Check("rebinding Host attacker.com:8765 rejected", !OriginPolicy.IsLoopbackHostAllowed("attacker.com:8765", 8765));
    t.Check("wrong port rejected", !OriginPolicy.IsLoopbackHostAllowed("localhost:9999", 8765));
}

t.Section("Signed allowlist");
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pem = key.ExportSubjectPublicKeyInfoPem();
    var json = Encoding.UTF8.GetBytes("{\"version\":2,\"origins\":[\"https://a.example\"]}");
    var sig = Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256));
    t.Check("valid signature verifies", AllowlistLoader.Verify(json, sig, pem));
    var tampered = Encoding.UTF8.GetBytes("{\"version\":2,\"origins\":[\"https://evil.example\"]}");
    t.Check("tampered list rejected", !AllowlistLoader.Verify(tampered, sig, pem));
    t.Check("placeholder key refuses updates", !AllowlistLoader.TryInstallUpdate(json, sig));
}

t.Section("Tokens");
{
    var clock = new FakeTime();
    var store = new TokenStore(clock);
    var code = store.CreatePairingCode(new[] { Scopes.ReadProcessed });
    t.Check("pairing code is 6 digits", code.Code.Length == 6 && code.Code.All(char.IsDigit));
    var paired = store.RedeemPairingCode(code.Code, "matlab");
    t.Check("pairing code redeems once", paired != null && store.RedeemPairingCode(code.Code, "again") == null);
    t.Check("paired token keeps the chosen scopes", paired!.Scopes.SetEquals(new[] { Scopes.ReadProcessed }));
    t.Check("paired token valid for reconnecting", store.Check(paired.Token, out _) == TokenCheck.Ok);
    clock.Advance(TimeSpan.FromHours(2));
    t.Check("paired token still valid while streaming", store.Check(paired.Token, out _) == TokenCheck.Ok);
    var expiredCode = store.CreatePairingCode(new[] { Scopes.ReadRaw });
    clock.Advance(TimeSpan.FromMinutes(6));
    t.Check("unused pairing code expires after 5 min", store.RedeemPairingCode(expiredCode.Code, "late") == null);
    store.Revoke(paired.Token);
    t.Check("revoked token rejected", store.Check(paired.Token, out _) == TokenCheck.Invalid);
}

t.Section("Stream core: chunking, timestamps, gaps");
{
    var dev = DeviceInfo.LucidPro("SN1", "fw", 25);
    var core = new StreamCore(dev, false, 60, 0.04, () => new DeviceStatus { ImpedanceKohm = new double?[] { 10, 10 } });
    var sink = new CollectSink();
    core.AddSink(sink);
    double now = 1000;
    long counter = 0;
    void Packet(int n, long? c = null)
    {
        now += n / 500.0;
        core.OnEeg(new EegBlock(Enumerable.Repeat(1f, n * 2).ToArray(), 2, now, c ?? counter));
        counter = (c ?? counter) + n;
    }
    for (var i = 0; i < 10; i++) Packet(10);
    var raw = sink.Frames.Where(f => f.Def.Key == StreamKeys.EegRaw).ToList();
    t.Check("100 samples -> five 20-sample chunks", raw.Count == 5 && raw.All(f => f.SampleCount == 20));
    t.Check("indexes contiguous", raw.Select(f => f.Index).SequenceEqual(new long[] { 0, 20, 40, 60, 80 }));
    var ts = raw.SelectMany(f => f.Timestamps).ToArray();
    t.Check("timestamps strictly increasing", ts.Zip(ts.Skip(1)).All(p => p.Second > p.First));
    t.Check("newest sample = arrival - latency", Math.Abs(ts[^1] - (now - 0.04)) < 1e-9);

    // 40-sample loss -> NaN fill + gap notice
    counter += 40;
    for (var i = 0; i < 6; i++) Packet(10);
    raw = sink.Frames.Where(f => f.Def.Key == StreamKeys.EegRaw).ToList();
    var gaps = sink.Gaps.Where(g => g.StreamKey == StreamKeys.EegRaw).ToList();
    t.Check("short gap reported as filled", gaps.Count == 1 && gaps[0].FromIndex == 100 && gaps[0].Count == 40 && gaps[0].Filled);
    var all = raw.SelectMany(f => f.Data!).ToArray();
    t.Check("40 samples x 2 ch NaN-filled", all.Count(float.IsNaN) == 80);
    t.Check("NaN frames flagged", raw.Any(f => f.HasNaN));
    t.Check("index stays continuous across fill", raw.Last().EndIndexExclusive == 200);

    // 2 s loss -> not filled, index jumps
    counter += 1000;
    Packet(10);
    for (var i = 0; i < 2; i++) Packet(10);
    gaps = sink.Gaps.Where(g => g.StreamKey == StreamKeys.EegRaw).ToList();
    raw = sink.Frames.Where(f => f.Def.Key == StreamKeys.EegRaw).ToList();
    t.Check("long gap reported unfilled", gaps.Count == 2 && !gaps[1].Filled && gaps[1].Count == 1000);
    t.Check("index jumps by 1000 after long gap", raw.Last().EndIndexExclusive == 200 + 1000 + 20 && raw.Last().Index == 1200);
    t.Check("dropped sample count", core.DroppedSamples == 1040);
}

t.Section("WebSocket server end to end");
{
    var settings = new AppSettings
    {
        DeveloperMode = true, RecordSession = false, LslEnabled = false, WebSocketPort = FreePort(),
        BleLatencyMs = 40, AllowedOrigins = { "http://localhost:5173" }
    };
    var appDir = AppContext.BaseDirectory;
    await using var engine = new LucidStreamEngine(settings, appDir, "Lucid-Stream selftest");
    await engine.ConnectAsync(new SimulatedSource());
    await engine.StartStreamingAsync();
    var port = engine.ActivePort!.Value;
    var uri = new Uri($"ws://localhost:{port}/v1/stream");
    const string appOrigin = "http://localhost:5173";
    string PairHello(string code) => $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"pairing_code\":\"{code}\",\"client\":\"selftest-browser\"}}";

    // Non-allowlisted origin -> 4005 before any data, even with a valid pairing code
    var unusedCode = engine.CreatePairingCode(new[] { Scopes.ReadRaw });
    using (var bad = new ClientWebSocket())
    {
        bad.Options.SetRequestHeader("Origin", "https://not-approved.example");
        await bad.ConnectAsync(uri, default);
        var (closeCode, gotData) = await WaitClose(bad, sendHello: PairHello(unusedCode.Code));
        t.Check($"non-allowlisted origin closed with 4005 (got {closeCode})", closeCode == 4005 && !gotData);
    }

    // Unknown token -> 4001
    using (var native = new ClientWebSocket())
    {
        await native.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(native, JsonHello("not-a-token"));
        t.Check($"unknown token -> 4001 (got {closeCode})", closeCode == 4001);
    }

    // Wrong schema -> 4002
    using (var oldSchema = new ClientWebSocket())
    {
        oldSchema.Options.SetRequestHeader("Origin", appOrigin);
        await oldSchema.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(oldSchema, "{\"type\":\"hello\",\"schema\":\"2.0\",\"token\":\"x\"}");
        t.Check($"unsupported schema -> 4002 (got {closeCode})", closeCode == 4002);
    }

    // Browser app on an allowed origin pairs -> welcome with token, binary + JSON data
    var browserCode = engine.CreatePairingCode(new[] { Scopes.ReadRaw, Scopes.ReadProcessed });
    var app1 = new ClientWebSocket();
    app1.Options.SetRequestHeader("Origin", appOrigin);
    await app1.ConnectAsync(uri, default);
    await SendText(app1, PairHello(browserCode.Code));
    var welcome = JsonDocument.Parse((await ReceiveText(app1))!).RootElement;
    t.Check("welcome received", welcome.GetProperty("type").GetString() == "welcome");
    var token = welcome.GetProperty("token").GetString()!;
    t.Check("client listed as a browser app", engine.Clients().Any(c => c.Kind == "Browser" && c.Origin == appOrigin));
    t.Check("welcome scopes read:processed + read:raw", welcome.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).OrderBy(s => s).SequenceEqual(new[] { "read:processed", "read:raw" }));
    t.Check("welcome mode live, 7 streams", welcome.GetProperty("mode").GetString() == "live" && welcome.GetProperty("streams").GetArrayLength() == 7);

    await SendText(app1, "{\"type\":\"subscribe\",\"streams\":[\"eeg.raw\"],\"encoding\":\"binary\"}");
    await SendText(app1, "{\"type\":\"subscribe\",\"streams\":[\"bandpower\"],\"encoding\":\"json\"}");
    BinaryFrameCodec.Decoded? firstBinary = null;
    JsonElement? bandJson = null;
    long lastRawIndex = -1;
    var sawStatus = false;
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline && (firstBinary == null || bandJson == null || !sawStatus))
    {
        var (isText, payload) = await Receive(app1);
        if (!isText) { var d = BinaryFrameCodec.Decode(payload); firstBinary ??= d; lastRawIndex = d.Index + d.Samples - 1; }
        else
        {
            var j = JsonDocument.Parse(payload).RootElement;
            var type = j.GetProperty("type").GetString();
            if (type == "data" && j.GetProperty("stream").GetString() == "bandpower") bandJson = j.Clone();
            if (type == "status") sawStatus = true;
        }
    }
    t.Check("binary eeg.raw frame: 20 samples x 2 ch", firstBinary is { Samples: 20, Channels: 2, StreamId: 0 });
    t.Check("JSON bandpower frame: 1 row x 20 values", bandJson?.GetProperty("data")[0].GetArrayLength() == 20);
    t.Check("status message delivered", sawStatus);

    var timeBefore = Clock.Now;
    await SendText(app1, $"{{\"type\":\"time_sync\",\"client_send\":{timeBefore.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

    // marker without write:markers -> error, connection stays
    await SendText(app1, "{\"type\":\"marker\",\"label\":\"x\"}");

    // Reconnect with the same token: new connection supersedes old (4009), resume fills from buffer
    await Task.Delay(300);
    var app2 = new ClientWebSocket();
    app2.Options.SetRequestHeader("Origin", appOrigin);
    await app2.ConnectAsync(uri, default);
    await SendText(app2, $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"token\":\"{token}\",\"resume_from\":{{\"eeg.raw\":{lastRawIndex}}}}}");
    var (oldClose, _) = await WaitClose(app1, null);
    t.Check($"older connection superseded with 4009 (got {oldClose})", oldClose == 4009);
    var w2 = JsonDocument.Parse((await ReceiveText(app2))!).RootElement;
    t.Check("reconnect gets same session id", w2.GetProperty("session").GetString() == welcome.GetProperty("session").GetString());
    await SendText(app2, "{\"type\":\"subscribe\",\"streams\":[\"eeg.raw\"],\"encoding\":\"binary\"}");
    BinaryFrameCodec.Decoded? firstAfterResume = null;
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline && firstAfterResume == null)
    {
        var (isText, payload) = await Receive(app2);
        if (!isText) firstAfterResume = BinaryFrameCodec.Decode(payload);
    }
    t.Check($"resume continues right after last index (next index {firstAfterResume?.Index}, last {lastRawIndex})",
        firstAfterResume != null && firstAfterResume.Index <= lastRawIndex + 1 && firstAfterResume.Index + firstAfterResume.Samples > lastRawIndex + 1);

    // Paired processed-only client: raw subscription refused with 4004
    var pc = engine.CreatePairingCode(new[] { Scopes.ReadProcessed });
    using (var research = new ClientWebSocket())
    {
        await research.ConnectAsync(uri, default);
        await SendText(research, $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"pairing_code\":\"{pc.Code}\",\"client\":\"matlab\"}}");
        var w = JsonDocument.Parse((await ReceiveText(research))!).RootElement;
        t.Check("pairing code exchanged for a token in welcome", w.TryGetProperty("token", out var tok) && tok.GetString()!.Length > 20);
        var (closeCode, _) = await WaitClose(research, "{\"type\":\"subscribe\",\"streams\":[\"eeg.raw\"]}");
        t.Check($"processed-only token subscribing raw -> 4004 (got {closeCode})", closeCode == 4004);
    }

    // Unknown stream -> 4003 ; reserved Lucidify stream also refused
    var pc2 = engine.CreatePairingCode(new[] { Scopes.ReadProcessed, Scopes.ReadRaw });
    using (var c3 = new ClientWebSocket())
    {
        await c3.ConnectAsync(uri, default);
        await SendText(c3, $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"pairing_code\":\"{pc2.Code}\"}}");
        await ReceiveText(c3);
        var (closeCode, _) = await WaitClose(c3, "{\"type\":\"subscribe\",\"streams\":[\"lucidify.lsi\"]}");
        t.Check($"reserved stream lucidify.lsi -> 4003 (got {closeCode})", closeCode == 4003);
    }

    // Revoking the token closes the connection with 4007, and it cannot reconnect (4001)
    engine.Tokens.Revoke(token);
    var (stopClose, _) = await WaitClose(app2, null);
    t.Check($"revoked token closes the connection with 4007 (got {stopClose})", stopClose == 4007);
    using (var after = new ClientWebSocket())
    {
        after.Options.SetRequestHeader("Origin", appOrigin);
        await after.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(after, JsonHello(token));
        t.Check($"revoked token cannot reconnect (got {closeCode})", closeCode == 4001);
    }
    await engine.StopStreamingAsync();
}

t.Section("Recording and replay parity");
{
    var folder = Path.Combine(Path.GetTempPath(), "lucidstream-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    var settings = new AppSettings { RecordSession = true, RecordFolder = folder, LslEnabled = false, WebSocketPort = FreePort(), BleLatencyMs = 40 };
    string session;
    var live = new List<float>();
    await using (var engine = new LucidStreamEngine(settings, AppContext.BaseDirectory, "selftest"))
    {
        engine.FrameProduced += f => { if (f.Def.Key == StreamKeys.EegRaw) lock (live) live.AddRange(f.Data!); };
        await engine.ConnectAsync(new SimulatedSource(packetLossProbability: 0.01, seed: 7));
        await engine.StartStreamingAsync();
        await Task.Delay(400);
        engine.AddMarker("eyes closed");
        await Task.Delay(2200);
        session = engine.RecordingFolder!;
        await engine.StopStreamingAsync();
    }
    var reader = new SessionReader(session);
    var recorded = reader.Eeg().SelectMany(r => r.Values).ToArray();
    t.Check($"recorded samples == streamed samples ({recorded.Length / 2})", recorded.Length == live.Count);
    t.Check("recorded values identical to eeg.raw (NaN-aware)", recorded.Zip(live).All(p => p.First.Equals(p.Second)));
    t.Check("marker recorded", reader.Markers().Any(m => m.Text == "eyes closed"));

    var replayed = new List<float>();
    var replayFlag = false;
    var settings2 = new AppSettings { RecordSession = false, LslEnabled = false, WebSocketPort = FreePort(), BleLatencyMs = 40 };
    await using (var engine = new LucidStreamEngine(settings2, AppContext.BaseDirectory, "selftest"))
    {
        engine.FrameProduced += f => { if (f.Def.Key == StreamKeys.EegRaw) lock (replayed) { replayed.AddRange(f.Data!); replayFlag |= f.Replay; } };
        await engine.ConnectAsync(new ReplaySource(session, loop: false));
        await engine.StartStreamingAsync();
        var waitUntil = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < waitUntil) { await Task.Delay(200); lock (replayed) if (replayed.Count >= recorded.Length) break; }
        await Task.Delay(200);
        engine.Core?.Flush();
        await engine.StopStreamingAsync();
    }
    t.Check("replayed frames marked replay", replayFlag);
    var n = Math.Min(replayed.Count, recorded.Length);
    t.Check($"replay reproduces raw values ({n / 2} of {recorded.Length / 2} samples compared)", n >= recorded.Length * 0.98 && recorded.Take(n).Zip(replayed.Take(n)).All(p => p.First.Equals(p.Second)));
    try { Directory.Delete(folder, true); } catch { }
}

if (LslNative.IsAvailable)
{
    t.Section("LSL outlets");
    var settings = new AppSettings { RecordSession = false, LslEnabled = true, WebSocketPort = FreePort(), BleLatencyMs = 40 };
    await using var engine = new LucidStreamEngine(settings, AppContext.BaseDirectory, "selftest");
    await engine.ConnectAsync(new SimulatedSource());
    await engine.StartStreamingAsync();
    t.Check("LSL outlets open", engine.LslActive);
    var probe = Environment.GetEnvironmentVariable("LUCIDSTREAM_LSL_PROBE");
    if (!string.IsNullOrEmpty(probe))
    {
        // Optional cross-check with an external inlet (for example a pylsl script) that writes its findings to this path.
        Console.WriteLine($"   streaming for 8 s so an external LSL inlet can check; results -> {probe}");
        await Task.Delay(8000);
    }
    await engine.StopStreamingAsync();
}
else
{
    Console.WriteLine("\n(skipping LSL outlet checks: liblsl not found)");
}

return t.Finish();

// ---------------------------------------------------------------------- helpers

static string JsonHello(string token) => $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"token\":\"{token}\",\"client\":\"selftest\"}}";

static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

static Task SendText(ClientWebSocket ws, string s) => ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, default);

static async Task<(bool IsText, byte[] Payload)> Receive(ClientWebSocket ws)
{
    var buf = new byte[65536];
    using var ms = new MemoryStream();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var r = await ws.ReceiveAsync(buf, cts.Token);
        if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed: " + ws.CloseStatus);
        ms.Write(buf, 0, r.Count);
        if (r.EndOfMessage) return (r.MessageType == WebSocketMessageType.Text, ms.ToArray());
    }
}

static async Task<string?> ReceiveText(ClientWebSocket ws)
{
    while (true)
    {
        var (isText, p) = await Receive(ws);
        if (isText) return Encoding.UTF8.GetString(p);
    }
}

static async Task<(int Code, bool GotData)> WaitClose(ClientWebSocket ws, string? sendHello)
{
    var gotData = false;
    try
    {
        if (sendHello != null && ws.State == WebSocketState.Open) await SendText(ws, sendHello);
    }
    catch (WebSocketException) { }
    var buf = new byte[65536];
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType == WebSocketMessageType.Binary) gotData = true;
            else if (Encoding.UTF8.GetString(buf, 0, r.Count).Contains("\"type\":\"data\"")) gotData = true;
        }
    }
    catch (Exception) { }
    return ((int?)ws.CloseStatus ?? -1, gotData);
}

/// <summary>Minimal stand-in for the Lucidify Cloud session API (same routes and fields as OrgApi.kt).</summary>
sealed class FakeCloud : IAsyncDisposable
{
    public sealed class Chunk
    {
        public int Seq; public long First; public int Count; public long TStartMs, TEndMs; public string Sha = ""; public byte[] Payload = Array.Empty<byte>(); public bool Verified;
    }

    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    public string BaseUrl { get; private set; } = "";
    public int SessionInits;
    public bool Finalized;
    public int FinalSeq = -1;
    public bool PutSawAuthorization;
    public System.Collections.Concurrent.ConcurrentDictionary<string, Chunk> Chunks = new();
    public System.Collections.Concurrent.ConcurrentBag<string> IdempotencyKeys = new();
    public System.Collections.Concurrent.ConcurrentBag<string> Events = new();

    public void Reset() { SessionInits = 0; Finalized = false; FinalSeq = -1; Chunks.Clear(); }

    public static async Task<FakeCloud> StartAsync()
    {
        var f = new FakeCloud();
        var port = FreePortStatic();
        var b = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls($"http://127.0.0.1:{port}");
        b.Logging.ClearProviders();
        var app = b.Build();
        f._app = app;
        f.BaseUrl = $"http://127.0.0.1:{port}";
        const string S = "/v1/org/{org}/subjects/{subj}/session";

        async Task<System.Text.Json.Nodes.JsonObject> Body(Microsoft.AspNetCore.Http.HttpContext c)
        {
            if (c.Request.Headers.TryGetValue("Idempotency-Key", out var k)) f.IdempotencyKeys.Add(k.ToString());
            return (System.Text.Json.Nodes.JsonObject)(await System.Text.Json.Nodes.JsonNode.ParseAsync(c.Request.Body))!;
        }
        Microsoft.AspNetCore.Http.IResult Json(object o) => Microsoft.AspNetCore.Http.Results.Json(o);
        bool Authed(Microsoft.AspNetCore.Http.HttpContext c) => c.Request.Headers.Authorization.ToString() == "Bearer test-token";

        app.MapPost(S + "/init", async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            if (!Authed(c)) return Microsoft.AspNetCore.Http.Results.StatusCode(401);
            await Body(c);
            Interlocked.Increment(ref f.SessionInits);
            return Json(new { sessionId = "sess-1", chunkSeconds = 5, sessionStartEpochMs = 1, reused = false });
        });
        app.MapPost(S + "/{sid}/chunk/init", async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            var j = await Body(c);
            var key = j["clientChunkKey"]!.GetValue<string>();
            var ch = f.Chunks.GetOrAdd(key, _ => new Chunk());
            ch.Seq = j["seq"]!.GetValue<int>(); ch.First = j["firstSampleIndex"]!.GetValue<long>(); ch.Count = j["sampleCount"]!.GetValue<int>();
            ch.TStartMs = j["tStartMs"]!.GetValue<long>(); ch.TEndMs = j["tEndMs"]!.GetValue<long>(); ch.Sha = j["sha256"]!.GetValue<string>();
            return Json(new { chunkId = "c" + ch.Seq, gcsPath = "x", signedPutUrl = $"{f.BaseUrl}/put/{Uri.EscapeDataString(key)}?X-Goog-Signature=abc", uploadRequired = true, alreadyVerified = false });
        });
        app.MapPut("/put/{key}", async (Microsoft.AspNetCore.Http.HttpContext c, string key) =>
        {
            if (c.Request.Headers.ContainsKey("Authorization")) f.PutSawAuthorization = true;
            using var ms = new MemoryStream();
            await c.Request.Body.CopyToAsync(ms);
            f.Chunks[key].Payload = ms.ToArray();
            return Microsoft.AspNetCore.Http.Results.Ok();
        });
        app.MapPost(S + "/{sid}/chunk/{cid}/complete", async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            var j = await Body(c);
            var ch = f.Chunks[j["clientChunkKey"]!.GetValue<string>()];
            var sha = Convert.ToBase64String(SHA256.HashData(ch.Payload));
            ch.Verified = sha == ch.Sha;
            return Json(new { status = ch.Verified ? "verified" : "mismatch", verified = ch.Verified, bytes = ch.Payload.Length, sha256 = sha });
        });
        app.MapGet(S + "/{sid}/coverage", () =>
        {
            var through = -1L;
            foreach (var ch in f.Chunks.Values.Where(x => x.Verified).OrderBy(x => x.First)) if (ch.First <= through + 1) through = Math.Max(through, ch.First + ch.Count - 1);
            return Json(new { contiguousThroughSample = through, verifiedRanges = Array.Empty<object>(), missingRanges = Array.Empty<object>(), finalized = f.Finalized });
        });
        app.MapPost(S + "/{sid}/finalization-intent", async (Microsoft.AspNetCore.Http.HttpContext c) => { await Body(c); return Json(new { status = "ok" }); });
        app.MapPost(S + "/{sid}/finalize", async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            var j = await Body(c);
            f.FinalSeq = j["finalSeqExclusive"]!.GetValue<int>();
            f.Finalized = true;
            return Json(new { status = "finalized", finalized = true, manifestPath = "m" });
        });
        app.MapGet(S + "/{sid}/manifest", () => Json(new { finalized = f.Finalized, chunks = f.Chunks.Values.Select(x => new { seq = x.Seq }).ToArray() }));
        app.MapPost(S + "/{sid}/events", async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            var j = await Body(c);
            foreach (var e in j["events"]!.AsArray()) f.Events.Add(e!["label"]!.GetValue<string>());
            return Json(new { results = j["events"]!.AsArray().Select(_ => new { status = "created" }).ToArray() });
        });
        await app.StartAsync();
        return f;
    }

    private static int FreePortStatic()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

sealed class FakeTime : ITimeSource
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
    public void Advance(TimeSpan d) => UtcNow += d;
}

sealed class CollectSink : IStreamSink
{
    public List<StreamFrame> Frames { get; } = new();
    public List<GapNotice> Gaps { get; } = new();
    public void OnFrame(StreamFrame frame) => Frames.Add(frame);
    public void OnGap(GapNotice gap) => Gaps.Add(gap);
}

sealed class Harness
{
    private int _pass, _fail;
    public void Section(string name) => Console.WriteLine($"\n== {name}");
    public void Check(string name, bool ok)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    }
    public int Finish()
    {
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}