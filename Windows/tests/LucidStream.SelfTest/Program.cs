using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    // Fp1 rel alpha is index 5 + 2; Fp2 rel theta is 10 + 5 + 1.
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
    var doc = new AllowlistDocument { Version = 3, Origins = { "https://Listen.Example.com/", "http://insecure.example.com" }, DevOrigins = { "http://localhost:8766" } };
    var prod = new OriginPolicy(doc, developerMode: false);
    var dev = new OriginPolicy(doc, developerMode: true);
    t.Check("allowlisted https origin accepted (normalized)", prod.IsAllowed("https://listen.example.com"));
    t.Check("same host, other port rejected", !prod.IsAllowed("https://listen.example.com:8443"));
    t.Check("look-alike host rejected", !prod.IsAllowed("https://listen.example.com.evil.net"));
    t.Check("http origin in prod list ignored", !prod.IsAllowed("http://insecure.example.com"));
    t.Check("'null' origin rejected", !prod.IsAllowed("null"));
    t.Check("dev origin rejected outside developer mode", !prod.IsAllowed("http://localhost:8766"));
    t.Check("dev origin accepted in developer mode", dev.IsAllowed("http://localhost:8766"));
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
    var launch = store.CreateLaunchToken();
    t.Check("launch token scopes raw+processed", launch.Scopes.SetEquals(new[] { Scopes.ReadRaw, Scopes.ReadProcessed }));
    t.Check("launch rejected without allowlisted browser origin", store.Check(launch.Token, false, true, out _) == TokenCheck.WrongContext);
    t.Check("launch rejected off loopback (LAN)", store.Check(launch.Token, true, false, out _) == TokenCheck.WrongContext);
    clock.Advance(TimeSpan.FromMinutes(3));
    t.Check("unredeemed launch token expires after 2 min", store.Check(launch.Token, true, true, out _) == TokenCheck.Expired);

    var l2 = store.CreateLaunchToken();
    t.Check("fresh launch token accepted", store.Check(l2.Token, true, true, out _) == TokenCheck.Ok);
    clock.Advance(TimeSpan.FromHours(2));
    t.Check("redeemed launch token stays valid while listening", store.Check(l2.Token, true, true, out _) == TokenCheck.Ok);
    store.RevokeLaunchTokens();
    t.Check("Stop listening revokes it", store.Check(l2.Token, true, true, out _) == TokenCheck.Invalid);

    var code = store.CreatePairingCode(new[] { Scopes.ReadProcessed });
    t.Check("pairing code is 6 digits", code.Code.Length == 6 && code.Code.All(char.IsDigit));
    var paired = store.RedeemPairingCode(code.Code, "matlab");
    t.Check("pairing code redeems once", paired != null && store.RedeemPairingCode(code.Code, "again") == null);
    t.Check("paired token works without browser origin", store.Check(paired!.Token, false, false, out _) == TokenCheck.Ok);
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
        DeveloperMode = true, RecordSession = false, LslEnabled = false, WebSocketPort = FreePort(), DevPagePort = FreePort(),
        BleLatencyMs = 40
    };
    var appDir = AppContext.BaseDirectory;
    await using var engine = new LucidStreamEngine(settings, appDir, "Lucid-Stream selftest");
    await engine.ConnectAsync(new SimulatedSource());
    await engine.StartStreamingAsync();
    var port = engine.ActivePort!.Value;
    var uri = new Uri($"ws://localhost:{port}/v1/stream");
    const string devOrigin = "http://localhost:8766";

    var url = engine.CreateListenUrl();
    var token = url.Split("#token=")[1].Split('&')[0];
    t.Check("launch URL carries token and port in the fragment", url.Contains($"&port={port}") && url.StartsWith("http://localhost:") && url.Contains('#'));

    // Non-allowlisted origin -> 4005 before any data
    using (var bad = new ClientWebSocket())
    {
        bad.Options.SetRequestHeader("Origin", "https://not-approved.example");
        await bad.ConnectAsync(uri, default);
        var (closeCode, gotData) = await WaitClose(bad, sendHello: JsonHello(token));
        t.Check($"non-allowlisted origin closed with 4005 (got {closeCode})", closeCode == 4005 && !gotData);
    }

    // Launch token without browser origin -> 4001
    using (var native = new ClientWebSocket())
    {
        await native.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(native, JsonHello(token));
        t.Check($"launch token from non-browser client -> 4001 (got {closeCode})", closeCode == 4001);
    }

    // Wrong schema -> 4002
    using (var oldSchema = new ClientWebSocket())
    {
        oldSchema.Options.SetRequestHeader("Origin", devOrigin);
        await oldSchema.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(oldSchema, $"{{\"type\":\"hello\",\"schema\":\"2.0\",\"token\":\"{token}\"}}");
        t.Check($"unsupported schema -> 4002 (got {closeCode})", closeCode == 4002);
    }

    // Allowed origin + launch token -> welcome, binary + JSON data
    var listen = new ClientWebSocket();
    listen.Options.SetRequestHeader("Origin", devOrigin);
    await listen.ConnectAsync(uri, default);
    await SendText(listen, JsonHello(token));
    var welcome = JsonDocument.Parse((await ReceiveText(listen))!).RootElement;
    t.Check("welcome received", welcome.GetProperty("type").GetString() == "welcome");
    t.Check("welcome scopes read:processed + read:raw", welcome.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).OrderBy(s => s).SequenceEqual(new[] { "read:processed", "read:raw" }));
    t.Check("welcome mode live, 7 streams", welcome.GetProperty("mode").GetString() == "live" && welcome.GetProperty("streams").GetArrayLength() == 7);

    await SendText(listen, "{\"type\":\"subscribe\",\"streams\":[\"eeg.raw\"],\"encoding\":\"binary\"}");
    await SendText(listen, "{\"type\":\"subscribe\",\"streams\":[\"bandpower\"],\"encoding\":\"json\"}");
    BinaryFrameCodec.Decoded? firstBinary = null;
    JsonElement? bandJson = null;
    long lastRawIndex = -1;
    var sawStatus = false;
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline && (firstBinary == null || bandJson == null || !sawStatus))
    {
        var (isText, payload) = await Receive(listen);
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
    await SendText(listen, $"{{\"type\":\"time_sync\",\"client_send\":{timeBefore.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

    // marker without write:markers -> error, connection stays
    await SendText(listen, "{\"type\":\"marker\",\"label\":\"x\"}");

    // Reconnect with the same token: new connection supersedes old (4009), resume fills from buffer
    await Task.Delay(300);
    var listen2 = new ClientWebSocket();
    listen2.Options.SetRequestHeader("Origin", devOrigin);
    await listen2.ConnectAsync(uri, default);
    await SendText(listen2, $"{{\"type\":\"hello\",\"schema\":\"1.0\",\"token\":\"{token}\",\"resume_from\":{{\"eeg.raw\":{lastRawIndex}}}}}");
    var (oldClose, _) = await WaitClose(listen, null);
    t.Check($"older connection superseded with 4009 (got {oldClose})", oldClose == 4009);
    var w2 = JsonDocument.Parse((await ReceiveText(listen2))!).RootElement;
    t.Check("reconnect gets same session id", w2.GetProperty("session").GetString() == welcome.GetProperty("session").GetString());
    await SendText(listen2, "{\"type\":\"subscribe\",\"streams\":[\"eeg.raw\"],\"encoding\":\"binary\"}");
    BinaryFrameCodec.Decoded? firstAfterResume = null;
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline && firstAfterResume == null)
    {
        var (isText, payload) = await Receive(listen2);
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

    // Stop listening -> 4007
    engine.StopListening();
    var (stopClose, _) = await WaitClose(listen2, null);
    t.Check($"Stop listening closes the page with 4007 (got {stopClose})", stopClose == 4007);

    // Expired/revoked token on reconnect -> 4001 (revoked)
    using (var after = new ClientWebSocket())
    {
        after.Options.SetRequestHeader("Origin", devOrigin);
        await after.ConnectAsync(uri, default);
        var (closeCode, _) = await WaitClose(after, JsonHello(token));
        t.Check($"revoked launch token cannot reconnect (got {closeCode})", closeCode == 4001);
    }

    // Developer Listen page served with CSP
    using (var http = new HttpClient())
    {
        var resp = await http.GetAsync(engine.DevPageUrl);
        t.Check("dev Listen page served", resp.IsSuccessStatusCode);
        t.Check("dev page has strict CSP", resp.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("script-src 'self'"));
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