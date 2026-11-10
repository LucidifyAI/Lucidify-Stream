using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LucidStream.Core.Ws;

public sealed record WsServerOptions
{
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>LAN mode: bind this IPv4 address in addition to localhost. Null = localhost only.</summary>
    public string? LanAddress { get; init; }
    public int MaxClients { get; init; } = 4;
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public double ResumeBufferSeconds { get; init; } = 5;
}

/// <summary>What the server tells a client about the session in welcome.</summary>
public sealed record WelcomeContext(DeviceInfo Device, DeviceStatus Status, ReplayDescriptor? Replay, string StatusJson);

public sealed record ClientSnapshot(string Name, string Origin, string Kind, string Scopes, DateTimeOffset Connected, long FramesSent, long FramesDropped, bool Loopback);

/// <summary>
/// The WebSocket API server (spec, "WebSocket API specification"): localhost first, optional LAN,
/// Origin allowlist and Host checks before any message is read, launch and paired tokens, JSON or binary
/// data frames, resume after reconnect, and per-client backpressure that never slows acquisition.
/// </summary>
public sealed class WsHub : IStreamSink, IAsyncDisposable
{
    private readonly StreamCatalog _catalog;
    private readonly Func<WelcomeContext> _welcome;
    private readonly TokenStore _tokens;
    private readonly OriginPolicy _policy;
    private readonly WsServerOptions _opt;
    private readonly Action<ClientMarker>? _onMarker;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _historyGate = new();
    private readonly Dictionary<string, LinkedList<StreamFrame>> _history = new();
    private WebApplication? _app;

    public int Port => _opt.Port;
    public bool LanEnabled => _opt.LanAddress != null;
    public event Action? ClientsChanged;

    public WsHub(StreamCatalog catalog, Func<WelcomeContext> welcome, TokenStore tokens, OriginPolicy policy, WsServerOptions options, Action<ClientMarker>? onMarker = null)
    {
        _catalog = catalog;
        _welcome = welcome;
        _tokens = tokens;
        _policy = policy;
        _opt = options;
        _onMarker = onMarker;
        _tokens.Revoked += OnTokensRevoked;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(_opt.Port);
            if (_opt.LanAddress != null) k.Listen(IPAddress.Parse(_opt.LanAddress), _opt.Port);
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.Map(ProtocolInfo.Path, HandleAsync);
        app.MapGet("/", () => Results.Text($"Lucid-Stream WebSocket API. Connect a WebSocket to {ProtocolInfo.Path}.", "text/plain"));
        await app.StartAsync();
        _app = app;
        Log.Info($"WebSocket server on ws://localhost:{_opt.Port}{ProtocolInfo.Path}" + (_opt.LanAddress != null ? $" and ws://{_opt.LanAddress}:{_opt.Port}{ProtocolInfo.Path} (LAN)" : ""));
    }

    public IReadOnlyList<ClientSnapshot> Clients() => _sessions.Values.Select(s => s.Snapshot()).ToList();

    // ------------------------------------------------------------------ connection handling

    private async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("WebSocket upgrade required.");
            return;
        }

        var local = ctx.Connection.LocalIpAddress;
        var loopback = local != null && IPAddress.IsLoopback(local);
        var host = ctx.Request.Headers.Host.ToString();
        var hostOk = loopback
            ? OriginPolicy.IsLoopbackHostAllowed(host, _opt.Port)
            : _opt.LanAddress != null && OriginPolicy.IsLanHostAllowed(host, _opt.LanAddress, _opt.Port);
        var origin = ctx.Request.Headers.Origin.ToString();
        var hasOrigin = !string.IsNullOrEmpty(origin);
        var originAllowed = hasOrigin && _policy.IsAllowed(origin);

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var remote = $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}";

        if (!hostOk)
        {
            Log.Warn($"Rejected connection from {remote}: Host '{host}' not allowed.");
            await CloseAsync(ws, CloseCodes.HostNotAllowed, "host not allowed");
            return;
        }
        if (hasOrigin && !originAllowed)
        {
            Log.Warn($"Rejected connection: origin '{origin}' is not on the allowlist.");
            await CloseAsync(ws, CloseCodes.OriginNotAllowed, "origin not allowed");
            return;
        }

        // ---- hello
        JsonElement hello;
        try
        {
            using var cts = new CancellationTokenSource(_opt.HelloTimeout);
            var text = await ReceiveTextAsync(ws, cts.Token);
            if (text == null) return;
            hello = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception)
        {
            await CloseAsync(ws, CloseCodes.InvalidToken, "expected hello");
            return;
        }

        if (Str(hello, "type") != "hello") { await CloseAsync(ws, CloseCodes.InvalidToken, "expected hello"); return; }
        var schema = Str(hello, "schema") ?? "1.0";
        if (!int.TryParse(schema.Split('.')[0], out var major) || major != ProtocolInfo.SchemaMajor)
        {
            await CloseAsync(ws, CloseCodes.UnsupportedSchema, $"unsupported schema {schema}");
            return;
        }
        var clientName = Str(hello, "client") ?? (hasOrigin ? origin : remote);

        TokenInfo? token;
        string? newlyIssued = null;
        var pairingCode = Str(hello, "pairing_code");
        if (!string.IsNullOrEmpty(pairingCode) && string.IsNullOrEmpty(Str(hello, "token")))
        {
            token = _tokens.RedeemPairingCode(pairingCode, clientName);
            if (token == null) { await CloseAsync(ws, CloseCodes.InvalidToken, "invalid or expired pairing code"); return; }
            newlyIssued = token.Token;
            Log.Info($"Paired client '{clientName}' with scopes {string.Join(" ", token.Scopes)}.");
        }
        else
        {
            var check = _tokens.Check(Str(hello, "token"), originAllowed, loopback, out token);
            switch (check)
            {
                case TokenCheck.Expired: await CloseAsync(ws, CloseCodes.TokenExpired, "token expired"); return;
                case TokenCheck.Invalid:
                case TokenCheck.WrongContext: await CloseAsync(ws, CloseCodes.InvalidToken, "invalid token"); return;
            }
        }

        var others = _sessions.Count(kv => kv.Key != token!.Token);
        if (others >= _opt.MaxClients) { await CloseAsync(ws, CloseCodes.TooManyClients, "too many clients"); return; }

        var session = new ClientSession(ws, token!, hasOrigin ? origin : "(none)", clientName, loopback, _opt.ResumeBufferSeconds, _catalog);
        session.ResumeFrom = ParseResume(hello);
        if (_sessions.TryGetValue(token!.Token, out var previous))
        {
            Log.Info($"'{clientName}' reconnected; closing its older connection.");
            _ = previous.CloseAsync(CloseCodes.Superseded, "superseded by a newer connection");
        }
        _sessions[token.Token] = session;
        ClientsChanged?.Invoke();

        try
        {
            var welcomeCtx = _welcome();
            await session.SendTextNowAsync(BuildWelcome(token, newlyIssued, welcomeCtx));
            // Current device status right away; later updates arrive on change and every 10 s.
            session.Enqueue(new OutMessage(Encoding.UTF8.GetBytes(welcomeCtx.StatusJson), true, null, 0, 0));
            session.StartWriter();
            await ReadLoopAsync(session);
            await session.FinishAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<string, ClientSession>(token.Token, session));
            ClientsChanged?.Invoke();
        }
    }

    private async Task ReadLoopAsync(ClientSession s)
    {
        while (s.Socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(s.Socket, s.Cancellation);
            if (text == null) return;
            JsonElement msg;
            try { msg = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { s.Enqueue(Error(0, "invalid JSON")); continue; }

            switch (Str(msg, "type"))
            {
                case "subscribe":
                    if (!await SubscribeAsync(s, msg)) return;
                    break;
                case "unsubscribe":
                    foreach (var k in Keys(msg)) s.Unsubscribe(k);
                    break;
                case "marker":
                    if (!s.Token.Scopes.Contains(Scopes.WriteMarkers)) { s.Enqueue(Error(CloseCodes.ScopeNotGranted, "write:markers not granted")); break; }
                    var label = Str(msg, "label");
                    if (string.IsNullOrWhiteSpace(label)) { s.Enqueue(Error(0, "marker needs a label")); break; }
                    msg.TryGetProperty("value", out var value);
                    _onMarker?.Invoke(new ClientMarker(label, value.ValueKind == JsonValueKind.Undefined ? null : value.GetRawText(), s.Name));
                    break;
                case "time_sync":
                    s.Enqueue(Json(w =>
                    {
                        w.WriteString("type", "time_sync");
                        if (msg.TryGetProperty("client_send", out var cs)) { w.WritePropertyName("client_send"); cs.WriteTo(w); }
                        w.WriteNumber("server_time", Clock.Now);
                        w.WriteNumber("utc_ms", Clock.UtcMs);
                    }));
                    break;
                default:
                    s.Enqueue(Error(0, "unknown message type"));
                    break;
            }
        }
    }

    private async Task<bool> SubscribeAsync(ClientSession s, JsonElement msg)
    {
        var encoding = Str(msg, "encoding") == "binary" ? FrameEncoding.Binary : FrameEncoding.Json;
        var keys = Keys(msg).ToList();
        foreach (var key in keys)
        {
            var def = _catalog.Find(key);
            if (def == null)
            {
                await s.CloseAsync(CloseCodes.UnknownStream, $"unknown stream '{key}'");
                return false;
            }
            var needed = def.Scope == StreamScope.Raw ? Scopes.ReadRaw : Scopes.ReadProcessed;
            if (!s.Token.Scopes.Contains(needed))
            {
                await s.CloseAsync(CloseCodes.ScopeNotGranted, $"{needed} not granted for '{key}'");
                return false;
            }
        }

        lock (_historyGate)
        {
            foreach (var key in keys)
            {
                var def = _catalog[key];
                var enc = def.IsString ? FrameEncoding.Json : encoding;
                if (s.ResumeFrom.TryGetValue(key, out var last) && _history.TryGetValue(key, out var hist) && hist.Count > 0)
                {
                    var missingFrom = last + 1;
                    var oldest = hist.First!.Value.Index;
                    if (oldest > missingFrom)
                        s.Enqueue(GapMessage(new GapNotice(key, missingFrom, oldest - missingFrom, "resume_window_exceeded", false)));
                    foreach (var f in hist.Where(f => f.EndIndexExclusive > missingFrom))
                        s.EnqueueFrame(f, enc);
                    s.ResumeFrom.Remove(key);
                }
                s.Subscribe(key, enc);
            }
        }
        s.Enqueue(Json(w =>
        {
            w.WriteString("type", "subscribed");
            w.WriteStartArray("streams");
            foreach (var k in s.SubscribedKeys) w.WriteStringValue(k);
            w.WriteEndArray();
            w.WriteString("encoding", encoding == FrameEncoding.Binary ? "binary" : "json");
        }));
        return true;
    }

    // ------------------------------------------------------------------ fan-out (IStreamSink)

    public void OnFrame(StreamFrame frame)
    {
        if (frame.Def.Key == StreamKeys.Status && frame.Text != null)
        {
            // Status goes to every client whether or not it subscribed.
            var bytes = Encoding.UTF8.GetBytes(frame.Text);
            foreach (var s in _sessions.Values) s.Enqueue(new OutMessage(bytes, true, null, 0, 0));
            return;
        }

        lock (_historyGate)
        {
            if (!_history.TryGetValue(frame.Def.Key, out var hist)) _history[frame.Def.Key] = hist = new LinkedList<StreamFrame>();
            hist.AddLast(frame);
            while (hist.First != null && hist.First.Value.T0 < frame.T0 - _opt.ResumeBufferSeconds) hist.RemoveFirst();

            foreach (var s in _sessions.Values)
            {
                if (s.TryGetEncoding(frame.Def.Key, out var enc)) s.EnqueueFrame(frame, enc);
            }
        }
    }

    public void OnGap(GapNotice gap)
    {
        var msg = GapMessage(gap);
        foreach (var s in _sessions.Values)
            if (s.TryGetEncoding(gap.StreamKey, out _)) s.Enqueue(msg);
    }

    private void OnTokensRevoked(IReadOnlyList<string> tokens)
    {
        foreach (var t in tokens)
            if (_sessions.TryGetValue(t, out var s)) _ = s.CloseAsync(CloseCodes.TokenExpired, "session ended");
    }

    public async ValueTask DisposeAsync()
    {
        _tokens.Revoked -= OnTokensRevoked;
        foreach (var s in _sessions.Values) await s.CloseAsync(CloseCodes.TokenExpired, "streaming stopped");
        if (_app != null)
        {
            using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2))) await _app.StopAsync(stopCts.Token);
            await _app.DisposeAsync();
        }
        Log.Info("WebSocket server stopped.");
    }

    // ------------------------------------------------------------------ messages

    private OutMessage BuildWelcome(TokenInfo token, string? newlyIssuedToken, WelcomeContext ctx)
    {
        return Json(w =>
        {
            w.WriteString("type", "welcome");
            w.WriteString("schema", ProtocolInfo.SchemaVersion);
            w.WriteString("session", token.SessionId);
            w.WriteString("mode", ctx.Replay != null ? "replay" : "live");
            if (newlyIssuedToken != null) w.WriteString("token", newlyIssuedToken);
            w.WriteStartArray("scopes");
            foreach (var sc in token.Scopes.OrderBy(x => x)) w.WriteStringValue(sc);
            w.WriteEndArray();
            w.WriteStartObject("device");
            w.WriteString("model", ctx.Device.Model);
            w.WriteString("serial", ctx.Device.Serial);
            w.WriteString("firmware", ctx.Device.Firmware);
            if (ctx.Status.BatteryPercent is int b) w.WriteNumber("battery_pct", b); else w.WriteNull("battery_pct");
            w.WriteString("reference", ctx.Device.Reference);
            w.WriteEndObject();
            w.WriteStartObject("clock");
            w.WriteNumber("lsl_time", Clock.Now);
            w.WriteNumber("utc_ms", Clock.UtcMs);
            w.WriteEndObject();
            w.WriteStartArray("streams");
            foreach (var d in _catalog.Streams)
            {
                w.WriteStartObject();
                w.WriteNumber("id", d.Id);
                w.WriteString("key", d.Key);
                w.WriteNumber("srate", d.Rate);
                w.WriteStartArray("channels");
                foreach (var c in d.Channels) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteString("unit", d.Unit);
                w.WriteString("scope", d.Scope == StreamScope.Raw ? Scopes.ReadRaw : Scopes.ReadProcessed);
                w.WriteBoolean("binary", !d.IsString);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("reserved");
            foreach (var r in StreamKeys.Reserved) w.WriteStringValue(r);
            w.WriteEndArray();
            if (ctx.Replay != null)
            {
                w.WriteStartObject("replay");
                w.WriteString("source", ctx.Replay.SourceId);
                w.WriteString("recorded_utc", ctx.Replay.RecordedUtc.ToString("o"));
                w.WriteBoolean("loop", ctx.Replay.Loop);
                w.WriteEndObject();
            }
            w.WriteString("intended_use", "Research use only. Not for diagnosis or clinical decision-making.");
        });
    }

    internal static OutMessage GapMessage(GapNotice g) => Json(w =>
    {
        w.WriteString("type", "gap");
        w.WriteString("stream", g.StreamKey);
        w.WriteNumber("from_index", g.FromIndex);
        w.WriteNumber("count", g.Count);
        w.WriteString("reason", g.Reason);
        w.WriteBoolean("filled", g.Filled);
    });

    private static OutMessage Error(int code, string message) => Json(w =>
    {
        w.WriteString("type", "error");
        if (code != 0) w.WriteNumber("code", code);
        w.WriteString("message", message);
    });

    internal static OutMessage Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return new OutMessage(buffer.WrittenSpan.ToArray(), true, null, 0, 0);
    }

    internal static byte[] EncodeJsonFrame(StreamFrame f)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "data");
            w.WriteString("stream", f.Def.Key);
            w.WriteNumber("seq", f.Seq);
            w.WriteNumber("index", f.Index);
            w.WriteNumber("t0", f.T0);
            w.WriteNumber("srate", f.Def.Rate);
            if (f.Replay) w.WriteBoolean("replay", true);
            w.WriteStartArray("data");
            if (f.Text != null) w.WriteStringValue(f.Text);
            else if (f.Data != null)
            {
                var ch = f.Def.ChannelCount;
                for (var i = 0; i < f.SampleCount; i++)
                {
                    w.WriteStartArray();
                    for (var c = 0; c < ch; c++)
                    {
                        var v = f.Data[i * ch + c];
                        if (float.IsNaN(v) || float.IsInfinity(v)) w.WriteNullValue(); else w.WriteNumberValue(v);
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, long> ParseResume(JsonElement hello)
    {
        var d = new Dictionary<string, long>();
        if (hello.TryGetProperty("resume_from", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var p in r.EnumerateObject())
                if (p.Value.TryGetInt64(out var v)) d[p.Name] = v;
        return d;
    }

    private static IEnumerable<string> Keys(JsonElement msg)
    {
        if (msg.TryGetProperty("streams", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) yield return e.GetString()!;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 64 * 1024) throw new InvalidDataException("message too large");
            if (r.EndOfMessage)
            {
                if (r.MessageType != WebSocketMessageType.Text) { ms.SetLength(0); continue; }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    internal static async Task CloseAsync(WebSocket ws, int code, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync((WebSocketCloseStatus)code, reason, cts.Token);
        }
        catch (Exception) { /* client already gone */ }
    }
}

public sealed record ClientMarker(string Label, string? ValueJson, string ClientName);

internal enum FrameEncoding { Json, Binary }

internal sealed record OutMessage(byte[] Payload, bool IsText, string? StreamKey, long Index, int Count);

/// <summary>One connected client: its subscriptions and a bounded send queue that drops the oldest frames when the client is slow.</summary>
internal sealed class ClientSession
{
    private readonly Channel<OutMessage> _queue;
    private readonly ConcurrentDictionary<string, FrameEncoding> _subs = new();
    private readonly ConcurrentDictionary<string, (long From, long Count)> _dropped = new();
    private readonly CancellationTokenSource _writerCts = new();
    private readonly CancellationTokenSource _readCts = new();
    private Task? _writer;
    private long _framesSent, _framesDropped;
    private int _closing;

    public WebSocket Socket { get; }
    public TokenInfo Token { get; }
    public string Origin { get; }
    public string Name { get; }
    public bool Loopback { get; }
    public DateTimeOffset Connected { get; } = DateTimeOffset.Now;
    public Dictionary<string, long> ResumeFrom { get; set; } = new();
    public CancellationToken Cancellation => _readCts.Token;
    public IEnumerable<string> SubscribedKeys => _subs.Keys;

    public ClientSession(WebSocket ws, TokenInfo token, string origin, string name, bool loopback, double bufferSeconds, StreamCatalog catalog)
    {
        Socket = ws; Token = token; Origin = origin; Name = name; Loopback = loopback;
        var framesPerSecond = catalog.Streams.Where(s => !s.IsIrregular)
            .Sum(s => s.Key is StreamKeys.EegRaw or StreamKeys.EegFiltered ? s.Rate / StreamCore.EegChunkSamples : Math.Max(1, s.Rate));
        var capacity = Math.Max(400, (int)(framesPerSecond * bufferSeconds) + 50);
        _queue = Channel.CreateBounded<OutMessage>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            OnDropped);
    }

    public void Subscribe(string key, FrameEncoding enc) => _subs[key] = enc;
    public void Unsubscribe(string key) => _subs.TryRemove(key, out _);
    public bool TryGetEncoding(string key, out FrameEncoding enc) => _subs.TryGetValue(key, out enc);

    public void Enqueue(OutMessage m) => _queue.Writer.TryWrite(m);

    public void EnqueueFrame(StreamFrame f, FrameEncoding enc)
    {
        var bytes = enc == FrameEncoding.Binary && f.Data != null ? FrameCache.Binary(f) : FrameCache.Json(f);
        _queue.Writer.TryWrite(new OutMessage(bytes, enc != FrameEncoding.Binary || f.Data == null, f.Def.Key, f.Index, f.SampleCount));
    }

    private void OnDropped(OutMessage m)
    {
        Interlocked.Increment(ref _framesDropped);
        if (m.StreamKey == null) return;
        _dropped.AddOrUpdate(m.StreamKey, (m.Index, m.Count), (_, d) => (Math.Min(d.From, m.Index), d.Count + m.Count));
    }

    public Task SendTextNowAsync(OutMessage m) => Socket.SendAsync(m.Payload, WebSocketMessageType.Text, true, CancellationToken.None);

    public void StartWriter() => _writer = Task.Run(RunWriterAsync);

    private async Task RunWriterAsync()
    {
        try
        {
            await foreach (var m in _queue.Reader.ReadAllAsync(_writerCts.Token))
            {
                await Socket.SendAsync(m.Payload, m.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, _writerCts.Token);
                Interlocked.Increment(ref _framesSent);
                if (!_dropped.IsEmpty)
                {
                    foreach (var key in _dropped.Keys.ToList())
                    {
                        if (!_dropped.TryRemove(key, out var d)) continue;
                        var gap = WsHub.GapMessage(new GapNotice(key, d.From, d.Count, "client_backpressure", false));
                        await Socket.SendAsync(gap.Payload, WebSocketMessageType.Text, true, _writerCts.Token);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Stop the writer without aborting the socket: complete the queue and let the last send finish.
    /// (Cancelling a pending WebSocket send or receive aborts the connection, so the close frame would never go out.)
    /// </summary>
    private async Task DrainWriterAsync()
    {
        _queue.Writer.TryComplete();
        var w = _writer;
        if (w == null) return;
        if (await Task.WhenAny(w, Task.Delay(2000)) != w) _writerCts.Cancel();
        try { await w; } catch { }
    }

    /// <summary>The client closed or went away: finish the close handshake.</summary>
    public async Task FinishAsync()
    {
        await DrainWriterAsync();
        if (Socket.State == WebSocketState.CloseReceived)
        {
            try { await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    /// <summary>Server-initiated close with a spec close code. The read loop then receives the client's close reply and ends.</summary>
    public async Task CloseAsync(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return;
        await DrainWriterAsync();
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch { }
        // If the client never answers, stop waiting after 5 s.
        _readCts.CancelAfter(TimeSpan.FromSeconds(5));
    }

    public ClientSnapshot Snapshot() => new(Name, Origin, Token.Kind == TokenKind.Launch ? "Listen" : "Paired",
        string.Join(" ", Token.Scopes.OrderBy(s => s)), Connected, Interlocked.Read(ref _framesSent), Interlocked.Read(ref _framesDropped), Loopback);
}

/// <summary>Encode each frame at most once per encoding, however many clients receive it.</summary>
internal static class FrameCache
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StreamFrame, byte[]> JsonCache = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StreamFrame, byte[]> BinaryCache = new();

    public static byte[] Json(StreamFrame f) => JsonCache.GetValue(f, WsHub.EncodeJsonFrame);
    public static byte[] Binary(StreamFrame f) => BinaryCache.GetValue(f, BinaryFrameCodec.Encode);
}