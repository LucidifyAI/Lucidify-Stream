using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Lsl;
using LucidStream.Core.Recording;
using LucidStream.Core.Streams;
using LucidStream.Core.Ws;

namespace LucidStream.Core.Engine;

public enum EngineState { Disconnected, Connecting, Connected, Streaming }

/// <summary>
/// Everything Lucid-Stream does, independent of the window: holds one patch connection, runs the stream core,
/// and fans frames out to LSL, the WebSocket server, the session file and the live view.
/// The Windows app and the command-line host are thin shells around this class.
/// </summary>
public sealed class LucidStreamEngine : IAsyncDisposable, IStreamSink
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IEegSource? _source;
    private volatile StreamCore? _core;
    private LslPublisher? _lsl;
    private WsHub? _hub;
    private SessionWriter? _recorder;
    private DevPageServer? _devPage;
    private Timer? _statusTimer;
    private OriginPolicy? _policy;

    public AppSettings Settings { get; }
    public string AppDirectory { get; }
    public string AppName { get; }
    public TokenStore Tokens { get; } = new();

    public EngineState State { get; private set; } = EngineState.Disconnected;
    public IEegSource? Source => _source;
    public DeviceStatus DeviceStatus => _source?.Status ?? new DeviceStatus();
    public StreamCore? Core => _core;
    public int? ActivePort => _hub?.Port;
    public bool LslActive => _lsl != null;
    public string? RecordingFolder => _recorder?.Folder;
    public string? DevPageUrl => _devPage?.Url;
    public bool Listening => Tokens.HasActiveLaunchToken;

    public event Action<EngineState>? StateChanged;
    public event Action<DeviceStatus>? DeviceStatusChanged;
    public event Action? ClientsChanged;
    /// <summary>Every frame, for the live view. Raised on the acquisition thread; marshal to the UI yourself.</summary>
    public event Action<StreamFrame>? FrameProduced;
    public event Action<GapNotice>? GapProduced;

    public LucidStreamEngine(AppSettings settings, string appDirectory, string appName)
    {
        Settings = settings;
        AppDirectory = appDirectory;
        AppName = appName;
        LslNative.Initialize();
    }

    // ------------------------------------------------------------------ device

    public async Task ConnectAsync(IEegSource source, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (_source != null) await DisconnectCoreAsync();
            SetState(EngineState.Connecting);
            _source = source;
            source.StatusChanged += OnSourceStatus;
            source.EegReceived += OnSourceEeg;
            source.AccelReceived += OnSourceAccel;
            source.MarkerReceived += OnSourceMarker;
            try
            {
                await source.StartAsync(ct);
            }
            catch
            {
                Detach(source);
                _source = null;
                SetState(EngineState.Disconnected);
                throw;
            }
            SetState(EngineState.Connected);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        if (State == EngineState.Streaming) await StopStreamingCoreAsync();
        if (_source != null)
        {
            var s = _source;
            await s.StopAsync();
            Detach(s);
            await s.DisposeAsync();
            _source = null;
        }
        SetState(EngineState.Disconnected);
    }

    private void Detach(IEegSource s)
    {
        s.StatusChanged -= OnSourceStatus;
        s.EegReceived -= OnSourceEeg;
        s.AccelReceived -= OnSourceAccel;
        s.MarkerReceived -= OnSourceMarker;
    }

    private void OnSourceEeg(EegBlock b) => _core?.OnEeg(b);
    private void OnSourceAccel(AccelBlock b) => _core?.OnAccel(b);
    private void OnSourceMarker(SourceMarker m) => _core?.AddMarker(m.Label);

    private void OnSourceStatus(DeviceStatus s)
    {
        DeviceStatusChanged?.Invoke(s);
        PublishStatus();
    }

    // ------------------------------------------------------------------ streaming

    public async Task StartStreamingAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State == EngineState.Streaming) return;
            if (_source?.Info == null) throw new InvalidOperationException("Connect a patch first.");
            var info = _source.Info;
            var core = new StreamCore(info, _source.IsReplay, Settings.LineFrequencyHz, Settings.BleLatencyMs / 1000.0, () => DeviceStatus);
            core.AddSink(this);

            if (Settings.RecordSession)
            {
                try { _recorder = new SessionWriter(Settings.RecordFolder, info, _source.IsReplay, AppName); core.AddSink(_recorder); }
                catch (Exception ex) { Log.Error($"Recording could not start: {ex.Message}"); }
            }

            if (Settings.LslEnabled)
            {
                if (LslNative.IsAvailable)
                {
                    try
                    {
                        _lsl = new LslPublisher(core.Catalog, info, Settings.LslStreams, AppName, core.FilterDescription,
                            Settings.BleLatencyMs / 1000.0, _source.Replay, Settings.StudyCode);
                        core.AddSink(_lsl);
                    }
                    catch (Exception ex) { Log.Error($"LSL could not start: {ex.Message}"); }
                }
                else Log.Warn("LSL is enabled but liblsl was not found; streaming over WebSocket only.");
            }

            var allowlist = AllowlistLoader.Load(AppDirectory);
            _policy = new OriginPolicy(allowlist, Settings.DeveloperMode);
            _hub = await StartHubAsync(core);
            core.AddSink(_hub);

            if (Settings.DeveloperMode)
            {
                try
                {
                    _devPage = new DevPageServer(Settings.DevPagePort, Path.Combine(AppDirectory, "web", "listen-dev"));
                    await _devPage.StartAsync();
                }
                catch (Exception ex) { Log.Warn($"Developer Listen page could not start: {ex.Message}"); _devPage = null; }
            }

            _core = core;
            _statusTimer = new Timer(_ => PublishStatus(), null, TimeSpan.Zero, TimeSpan.FromSeconds(StreamCore.StatusIntervalSeconds));
            SetState(EngineState.Streaming);
            Log.Info("Streaming started.");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<WsHub> StartHubAsync(StreamCore core)
    {
        string? lan = null;
        if (Settings.LanEnabled)
        {
            lan = string.IsNullOrWhiteSpace(Settings.LanAddress) ? LocalIPv4Addresses().FirstOrDefault() : Settings.LanAddress;
            if (lan == null) Log.Warn("LAN mode is on but no network address was found; serving localhost only.");
            else Log.Warn($"LAN mode is on: paired clients on the network can connect at {lan}. Use trusted networks only.");
        }

        Exception? last = null;
        for (var port = Settings.WebSocketPort; port < Settings.WebSocketPort + 10; port++)
        {
            var hub = new WsHub(core.Catalog,
                () => new WelcomeContext(_source!.Info!, DeviceStatus, _source.Replay, BuildStatusJson()),
                Tokens, _policy!, new WsServerOptions { Port = port, LanAddress = lan, MaxClients = Settings.MaxClients },
                OnClientMarker);
            try
            {
                await hub.StartAsync();
                hub.ClientsChanged += () => ClientsChanged?.Invoke();
                if (port != Settings.WebSocketPort) Log.Warn($"Port {Settings.WebSocketPort} was busy; using {port}. The Listen link carries the new port.");
                return hub;
            }
            catch (Exception ex)
            {
                last = ex;
                await hub.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"No free port from {Settings.WebSocketPort}: {last?.Message}");
    }

    public async Task StopStreamingAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopStreamingCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopStreamingCoreAsync()
    {
        if (State != EngineState.Streaming) return;
        var core = _core;
        _core = null;
        _statusTimer?.Dispose();
        _statusTimer = null;
        core?.Flush();
        Tokens.RevokeAll();
        if (_hub != null) { await _hub.DisposeAsync(); _hub = null; }
        if (_devPage != null) { await _devPage.DisposeAsync(); _devPage = null; }
        _lsl?.Dispose(); _lsl = null;
        _recorder?.Dispose(); _recorder = null;
        SetState(_source != null ? EngineState.Connected : EngineState.Disconnected);
        ClientsChanged?.Invoke();
        Log.Info("Streaming stopped.");
    }

    // ------------------------------------------------------------------ Listen, pairing, markers

    /// <summary>Mint a launch token and build the Listen URL. The token travels only in the URL fragment.</summary>
    public string CreateListenUrl()
    {
        if (State != EngineState.Streaming || _hub == null) throw new InvalidOperationException("Start streaming before Listen.");
        var token = Tokens.CreateLaunchToken();
        var baseUrl = Settings.DeveloperMode && _devPage != null ? _devPage.Url : Settings.ListenUrl;
        ClientsChanged?.Invoke();
        return $"{baseUrl.Split('#')[0]}#token={Uri.EscapeDataString(token.Token)}&port={_hub.Port}";
    }

    public void StopListening()
    {
        Tokens.RevokeLaunchTokens();
        ClientsChanged?.Invoke();
    }

    public PairingCode CreatePairingCode(IEnumerable<string> scopes) => Tokens.CreatePairingCode(scopes);

    public IReadOnlyList<ClientSnapshot> Clients() => _hub?.Clients() ?? Array.Empty<ClientSnapshot>();

    /// <summary>Operator marker from the app (preset button or free text).</summary>
    public double? AddMarker(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var core = _core;
        if (core == null) { Log.Warn("Markers are recorded only while streaming."); return null; }
        var t = core.AddMarker(label.Trim());
        Log.Info($"Marker: {label.Trim()}");
        return t;
    }

    private void OnClientMarker(ClientMarker m)
    {
        var text = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["label"] = m.Label,
            ["value"] = m.ValueJson == null ? null : JsonDocument.Parse(m.ValueJson).RootElement,
            ["source"] = m.ClientName
        });
        _core?.AddMarker(text);
    }

    // ------------------------------------------------------------------ status

    public string BuildStatusJson()
    {
        var s = DeviceStatus;
        var info = _source?.Info;
        var core = _core;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "status",
            ["connected"] = s.Connected,
            ["mode"] = _source?.IsReplay == true ? "replay" : "live",
            ["serial"] = info?.Serial,
            ["firmware"] = info?.Firmware,
            ["battery_pct"] = s.BatteryPercent,
            ["contact"] = s.Contact,
            ["impedance_kohm"] = s.ImpedanceKohm,
            ["ble_rssi_dbm"] = s.SignalStrengthDbm,
            ["received_samples"] = core?.ReceivedSamples,
            ["dropped_samples"] = core?.DroppedSamples,
            ["lsl_time"] = Clock.Now,
            ["utc_ms"] = Clock.UtcMs,
        });
    }

    private void PublishStatus()
    {
        try { _core?.PublishStatus(BuildStatusJson()); }
        catch (Exception ex) { Log.Warn($"Status update failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ IStreamSink (live view tap)

    void IStreamSink.OnFrame(StreamFrame frame) => FrameProduced?.Invoke(frame);
    void IStreamSink.OnGap(GapNotice gap) => GapProduced?.Invoke(gap);

    // ------------------------------------------------------------------ helpers

    private void SetState(EngineState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public static IReadOnlyList<string> LocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel && n.NetworkInterfaceType != NetworkInterfaceType.Ppp)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}