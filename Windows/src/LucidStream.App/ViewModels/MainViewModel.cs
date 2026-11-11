using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Engine;
using LucidStream.Core.Lsl;
using LucidStream.Core.Streams;
using LucidStream.Core.Ws;

namespace LucidStream.App.ViewModels;

public enum SourceKind { Patch, Simulated, Replay }

public sealed record SourceOption(string Name, SourceKind Kind);

public sealed class StreamToggle : ObservableObject
{
    private bool _enabled;
    public required string Key { get; init; }
    public required string Label { get; init; }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

public sealed class BandRow : ObservableObject
{
    private double _percent;
    public required string Name { get; init; }
    public double Percent
    {
        get => _percent;
        set { if (Set(ref _percent, value)) { OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(BarValue)); } }
    }
    public string Text => double.IsNaN(Percent) ? "-" : $"{Percent:0}%";
    /// <summary>Progress bars reject NaN, so unknown shows as empty.</summary>
    public double BarValue => double.IsNaN(Percent) ? 0 : Math.Clamp(Percent, 0, 100);
}

public sealed record ClientRow(string Name, string Origin, string Kind, string Scopes, string Since, long Frames, long Dropped);

/// <summary>
/// All window logic for Lucid-Stream. Deliberately free of WPF types: the window marshals timer ticks in,
/// and draws the trace from <see cref="TraceUpdated"/>. UI-thread affinity comes from the SynchronizationContext
/// captured at construction.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Func<AppSettings, IEegSource> _patchFactory;
    private readonly LucidStreamEngine _engine;
    private readonly object _traceGate = new();
    private readonly float[] _traceFp1 = new float[TraceSamples], _traceFp2 = new float[TraceSamples];
    private int _tracePos;
    private float[]? _latestBands;
    private const int TraceSamples = 5000; // 10 s at 500 Hz, for the Cloud window's local trace

    /// <summary>The live signal for the main window's waveform and spectrogram.</summary>
    public LucidStream.App.Live.LiveSignal Live { get; } = new();

    public AppSettings Settings => _engine.Settings;

    public MainViewModel(AppSettings settings, string appDirectory, string appName, Func<AppSettings, IEegSource> patchFactory)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _patchFactory = patchFactory;
        _engine = new LucidStreamEngine(settings, appDirectory, appName);
        Cloud = new CloudViewModel(_engine, _ui);

        LslStreams = new ObservableCollection<StreamToggle>(new[]
        {
            (StreamKeys.EegRaw, "Raw EEG"), (StreamKeys.EegFiltered, "Filtered EEG"), (StreamKeys.Acc, "Accelerometer"),
            (StreamKeys.BandPower, "Band power"), (StreamKeys.Quality, "Signal quality"), (StreamKeys.Status, "Device status"),
            (StreamKeys.Markers, "Markers")
        }.Select(s => new StreamToggle { Key = s.Item1, Label = s.Item2, Enabled = settings.LslStreams.Contains(s.Item1) }));
        foreach (var t in LslStreams) t.PropertyChanged += (_, _) => SaveStreamSelection();

        Bands = new ObservableCollection<BandRow>(Bands_.Select(b => new BandRow { Name = b }));
        MarkerPresets = new ObservableCollection<string>(settings.MarkerPresets);
        LanAddresses = new ObservableCollection<string>(LucidStreamEngine.LocalIPv4Addresses());
        RefreshSourceOptions();

        ConnectCommand = new RelayCommand(_ => ConnectAsync(), _ => _engine.State == EngineState.Disconnected && SelectedSource != null, ShowError);
        DisconnectCommand = new RelayCommand(_ => _engine.DisconnectAsync(), _ => _engine.State is EngineState.Connected or EngineState.Streaming, ShowError);
        StartStreamingCommand = new RelayCommand(_ => StartStreamingAsync(), _ => _engine.State == EngineState.Connected, ShowError);
        StopStreamingCommand = new RelayCommand(_ => _engine.StopStreamingAsync(), _ => _engine.State == EngineState.Streaming, ShowError);
        PairCommand = RelayCommand.Sync(_ => CreatePairingCode(), _ => _engine.State == EngineState.Streaming, ShowError);
        SendMarkerCommand = RelayCommand.Sync(_ => { if (_engine.AddMarker(MarkerText) != null) MarkerText = ""; }, _ => IsStreaming && !string.IsNullOrWhiteSpace(MarkerText), ShowError);
        PresetMarkerCommand = RelayCommand.Sync(p => _engine.AddMarker(p as string ?? ""), _ => IsStreaming, ShowError);
        OpenRecordingsCommand = RelayCommand.Sync(_ => OpenFolder(_engine.RecordingFolder ?? Settings.RecordFolder), null, ShowError);
        ExportLogCommand = RelayCommand.Sync(_ => ExportLog(), null, ShowError);
        AllCommands = new[] { ConnectCommand, DisconnectCommand, StartStreamingCommand, StopStreamingCommand, PairCommand, SendMarkerCommand, PresetMarkerCommand };

        _engine.StateChanged += _ => Post(OnStateChanged);
        _engine.DeviceStatusChanged += s => Post(() => ApplyStatus(s));
        _engine.ClientsChanged += () => Post(RefreshClients);
        _engine.FrameProduced += OnFrame;
        Log.Written += e => Post(() =>
        {
            LogLines.Insert(0, e.ToString());
            while (LogLines.Count > 300) LogLines.RemoveAt(LogLines.Count - 1);
        });
        foreach (var e in Log.Snapshot().Reverse()) LogLines.Add(e.ToString());
        OnStateChanged();
    }

    private static readonly string[] Bands_ = Core.Streams.Bands.All.Select(b => b.Name).ToArray();

    // ------------------------------------------------------------------ bindable state

    public ObservableCollection<SourceOption> SourceOptions { get; } = new();
    private SourceOption? _selectedSource;
    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set { if (Set(ref _selectedSource, value)) { OnPropertyChanged(nameof(IsReplaySelected)); RaiseAll(); } }
    }
    public bool IsReplaySelected => SelectedSource?.Kind == SourceKind.Replay;

    private string _replayFolder = "";
    public string ReplayFolder { get => _replayFolder; set => Set(ref _replayFolder, value); }
    private bool _loopReplay;
    public bool LoopReplay { get => _loopReplay; set => Set(ref _loopReplay, value); }

    private string _stateText = "Not connected";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public bool IsStreaming => _engine.State == EngineState.Streaming;
    public bool IsNotStreaming => !IsStreaming;
    public bool IsDisconnected => _engine.State == EngineState.Disconnected;
    public bool IsLinked => !IsDisconnected;
    public bool IsReplayActive => _engine.Source?.IsReplay == true;

    private string _deviceName = "-", _firmware = "-", _battery = "-", _fp1 = "-", _fp2 = "-";
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    public string Battery { get => _battery; private set => Set(ref _battery, value); }
    public string Fp1Contact { get => _fp1; private set => Set(ref _fp1, value); }
    public string Fp2Contact { get => _fp2; private set => Set(ref _fp2, value); }

    // Compact header status (icons): link "up" / "lost" / "off", battery voltage, contact "good" / "poor" / "none".
    private string _linkState = "off", _linkText = "Not connected", _batteryVolts = "", _batteryGlyph = "\uE850",
        _contactAfz = "none", _contactFp2 = "none";
    public string LinkState { get => _linkState; private set => Set(ref _linkState, value); }
    public string LinkText { get => _linkText; private set => Set(ref _linkText, value); }
    public string LinkTooltip => IsDisconnected ? "No patch connected" : $"{DeviceName}: {LinkText}";
    public string BatteryVolts { get => _batteryVolts; private set => Set(ref _batteryVolts, value); }
    public string BatteryGlyph { get => _batteryGlyph; private set => Set(ref _batteryGlyph, value); }
    public string ContactAfz { get => _contactAfz; private set => Set(ref _contactAfz, value); }
    public string ContactFp2 { get => _contactFp2; private set => Set(ref _contactFp2, value); }
    public string ContactTooltip => $"AFz: {Fp1Contact}\nFp2: {Fp2Contact}";

    // Outputs
    public bool LslAvailable => LslNative.IsAvailable;
    public string LslHint => LslNative.IsAvailable ? "Streams appear in LabRecorder on this network." : "LSL is off: lsl.dll is missing. Run tools\\get-liblsl.ps1 in the project folder (or the Build action), then rebuild and restart.";
    public bool LslEnabled { get => Settings.LslEnabled; set { Settings.LslEnabled = value; Save(); OnPropertyChanged(); } }
    public ObservableCollection<StreamToggle> LslStreams { get; }
    public string Port
    {
        get => Settings.WebSocketPort.ToString(CultureInfo.InvariantCulture);
        set { if (int.TryParse(value, out var p) && p is > 1023 and < 65536) { Settings.WebSocketPort = p; Save(); } OnPropertyChanged(); }
    }
    public bool LanEnabled { get => Settings.LanEnabled; set { Settings.LanEnabled = value; Save(); OnPropertyChanged(); } }
    public ObservableCollection<string> LanAddresses { get; }
    public string LanAddress { get => Settings.LanAddress; set { Settings.LanAddress = value ?? ""; Save(); OnPropertyChanged(); } }
    public bool RecordSession { get => Settings.RecordSession; set { Settings.RecordSession = value; Save(); OnPropertyChanged(); } }
    public string RecordFolder { get => Settings.RecordFolder; set { Settings.RecordFolder = value; Save(); OnPropertyChanged(); } }
    public IReadOnlyList<string> LineFrequencies { get; } = new[] { "60 Hz", "50 Hz" };
    public string LineFrequency
    {
        get => Settings.LineFrequencyHz >= 55 ? "60 Hz" : "50 Hz";
        set { Settings.LineFrequencyHz = value == "50 Hz" ? 50 : 60; Save(); OnPropertyChanged(); }
    }

    // Developer
    public bool DeveloperMode
    {
        get => Settings.DeveloperMode;
        set { Settings.DeveloperMode = value; Save(); OnPropertyChanged(); RefreshSourceOptions(); }
    }
    /// <summary>Browser origins allowed to connect, one per line.</summary>
    public string AllowedOrigins
    {
        get => string.Join(Environment.NewLine, Settings.AllowedOrigins);
        set
        {
            // Store what browsers actually send (scheme, host, port), so a pasted page address works.
            Settings.AllowedOrigins = (value ?? "").Split(new[] { '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(OriginPolicy.UserEntryToOrigins).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
            _engine.ApplyAllowedOrigins();
            Log.Info(Settings.AllowedOrigins.Count == 0 ? "No web pages are allowed to connect."
                : "Web pages allowed to connect: " + string.Join(", ", Settings.AllowedOrigins));
            OnPropertyChanged();
        }
    }
    public string BleLatencyMs
    {
        get => Settings.BleLatencyMs.ToString(CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and < 1000) { Settings.BleLatencyMs = v; Save(); } OnPropertyChanged(); }
    }
    public string StudyCode { get => Settings.StudyCode; set { Settings.StudyCode = value ?? ""; Save(); OnPropertyChanged(); } }

    // Clients
    public ObservableCollection<ClientRow> Clients { get; } = new();
    private bool _pairRaw, _pairProcessed = true, _pairMarkers;
    public bool PairRaw { get => _pairRaw; set => Set(ref _pairRaw, value); }
    public bool PairProcessed { get => _pairProcessed; set => Set(ref _pairProcessed, value); }
    public bool PairMarkers { get => _pairMarkers; set => Set(ref _pairMarkers, value); }
    private string _pairingCode = "";
    public string PairingCode { get => _pairingCode; private set => Set(ref _pairingCode, value); }
    private string _pairingHint = "";
    public string PairingHint { get => _pairingHint; private set => Set(ref _pairingHint, value); }

    // Markers
    public ObservableCollection<string> MarkerPresets { get; }
    private string _markerText = "";
    public string MarkerText { get => _markerText; set { if (Set(ref _markerText, value)) SendMarkerCommand.Raise(); } }

    // Live view
    public ObservableCollection<BandRow> Bands { get; }
    private string _statusLine = "";
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
    public ObservableCollection<string> LogLines { get; } = new();
    private string _error = "";
    public string Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Raised on the UI thread with the last 4 s of filtered EEG (Fp1, Fp2), oldest first.</summary>
    public event Action<float[], float[]>? TraceUpdated;

    /// <summary>The window opens URLs (default browser) when asked.</summary>
    public event Action<string>? OpenUrlRequested;

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand StartStreamingCommand { get; }
    public RelayCommand StopStreamingCommand { get; }
    public RelayCommand PairCommand { get; }
    public RelayCommand SendMarkerCommand { get; }
    public RelayCommand PresetMarkerCommand { get; }
    public RelayCommand OpenRecordingsCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    private readonly RelayCommand[] AllCommands;

    // ------------------------------------------------------------------ actions

    private async Task ConnectAsync()
    {
        Error = "";
        IEegSource source = SelectedSource!.Kind switch
        {
            SourceKind.Simulated => new SimulatedSource(Settings.SimulatedPacketLoss),
            SourceKind.Replay => new ReplaySource(RequireReplayFolder(), LoopReplay),
            _ => _patchFactory(Settings),
        };
        await _engine.ConnectAsync(source);
    }

    private string RequireReplayFolder()
    {
        if (string.IsNullOrWhiteSpace(ReplayFolder) || !File.Exists(Path.Combine(ReplayFolder, "session.json")))
            throw new InvalidOperationException("Choose a Lucid-Stream session folder (it contains session.json).");
        return ReplayFolder;
    }

    private async Task StartStreamingAsync()
    {
        Error = "";
        var s = _engine.DeviceStatus;
        if (!IsReplayActive && s.Contact.Any(c => !c))
            Log.Warn("An electrode reports poor skin contact. Streaming anyway; check the patch.");
        await _engine.StartStreamingAsync();
    }

    private void CreatePairingCode()
    {
        var scopes = new List<string>();
        if (PairProcessed) scopes.Add(Scopes.ReadProcessed);
        if (PairRaw) scopes.Add(Scopes.ReadRaw);
        if (PairMarkers) scopes.Add(Scopes.WriteMarkers);
        if (scopes.Count == 0) throw new InvalidOperationException("Choose at least one permission for the paired app.");
        var code = _engine.CreatePairingCode(scopes);
        PairingCode = code.Code;
        PairingHint = $"Enter this code in the other app within 5 minutes. Grants: {string.Join(", ", scopes)}." +
                      (Settings.LanEnabled ? "" : " LAN mode is off, so only apps on this PC can pair.");
        Log.Info($"Pairing code created ({string.Join(" ", scopes)}).");
    }

    private void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void ExportLog()
    {
        var path = Path.Combine(Settings.RecordFolder, $"lucid-stream-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Settings.RecordFolder);
        File.WriteAllLines(path, Log.Snapshot().Select(e => e.ToString()));
        Log.Info($"Log exported to {path}");
    }

    // ------------------------------------------------------------------ engine callbacks

    private void OnFrame(StreamFrame f)
    {
        // Acquisition thread: copy what the live view needs; the UI timer picks it up.
        if (f.Def.Key == StreamKeys.EegFiltered && f.Data != null)
        {
            lock (_traceGate)
            {
                for (var i = 0; i < f.SampleCount; i++)
                {
                    _traceFp1[_tracePos] = f.Data[i * 2];
                    _traceFp2[_tracePos] = f.Data[i * 2 + 1];
                    _tracePos = (_tracePos + 1) % TraceSamples;
                }
            }
            Live.AddFiltered(f.Index, f.Data, f.SampleCount);
        }
        else if (f.Def.Key == StreamKeys.EegRaw && f.Data != null)
        {
            Live.AddRaw(f.Index, f.Data, f.SampleCount);
        }
        else if (f.Def.Key == StreamKeys.BandPower && f.Data != null)
        {
            Volatile.Write(ref _latestBands, f.Data);
        }
    }

    /// <summary>Lucidify Cloud: sign-in, patient, recording and delay.</summary>
    public CloudViewModel Cloud { get; }

    /// <summary>Streaming (re)started: clear the live views.</summary>
    public event Action? LiveViewReset;

    private void ResetLiveView()
    {
        lock (_traceGate)
        {
            Array.Fill(_traceFp1, float.NaN);
            Array.Fill(_traceFp2, float.NaN);
            _tracePos = 0;
        }
        Live.Reset();
        LiveViewReset?.Invoke();
    }

    /// <summary>Called by the window about 15 times a second on the UI thread.</summary>

    public void Tick()
    {
        Cloud.Tick();
        if (!IsStreaming) return;
        float[] a = new float[TraceSamples], b = new float[TraceSamples];
        lock (_traceGate)
        {
            var tail = TraceSamples - _tracePos;
            Array.Copy(_traceFp1, _tracePos, a, 0, tail); Array.Copy(_traceFp1, 0, a, tail, _tracePos);
            Array.Copy(_traceFp2, _tracePos, b, 0, tail); Array.Copy(_traceFp2, 0, b, tail, _tracePos);
        }
        TraceUpdated?.Invoke(a, b);

        var bands = Volatile.Read(ref _latestBands);
        if (bands != null)
        {
            var n = Bands_.Length;
            for (var i = 0; i < n; i++)
            {
                var v1 = bands[n + i];
                var v2 = bands[3 * n + i];
                var vals = new[] { v1, v2 }.Where(v => !float.IsNaN(v)).ToArray();
                Bands[i].Percent = vals.Length == 0 ? double.NaN : vals.Average() * 100;
            }
        }

        var core = _engine.Core;
        if (core != null)
        {
            StatusLine = $"ws://localhost:{_engine.ActivePort}/v1/stream" + (_engine.LslActive ? " + LSL" : "") +
                         $"   |   {core.ReceivedSamples / 500.0:0} s received, {core.DroppedSamples} samples dropped" +
                         (_engine.RecordingFolder != null ? "   |   recording" : "") + (IsReplayActive ? "   |   REPLAY" : "");
        }
        RefreshClients();
    }

    private void OnStateChanged()
    {
        StateText = _engine.State switch
        {
            EngineState.Disconnected => "Not connected",
            EngineState.Connecting => "Connecting",
            EngineState.Connected => IsReplayActive ? "Replay ready" : "Patch connected",
            EngineState.Streaming => IsReplayActive ? "Streaming (replay)" : "Streaming",
            _ => ""
        };
        if (_engine.State == EngineState.Disconnected)
        {
            DeviceName = Firmware = Battery = Fp1Contact = Fp2Contact = "-";
            StatusLine = "";
            LinkState = "off";
            BatteryVolts = "";
            BatteryGlyph = "\uE850";
            ContactAfz = ContactFp2 = "none";
        }
        else if (_engine.Source?.Info is { } info)
        {
            DeviceName = $"{info.Model} {info.Serial}";
            Firmware = info.Firmware;
            ApplyStatus(_engine.DeviceStatus);
        }
        if (IsStreaming && !_wasStreaming) ResetLiveView();
        _wasStreaming = IsStreaming;
        if (!IsStreaming)
        {
            PairingCode = "";
            PairingHint = "";
            foreach (var b in Bands) b.Percent = double.NaN;
        }
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsNotStreaming));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsLinked));
        UpdateLinkText();
        OnPropertyChanged(nameof(LinkTooltip));
        OnPropertyChanged(nameof(IsReplayActive));
        RefreshClients();
        RaiseAll();
    }

    private void ApplyStatus(DeviceStatus s)
    {
        // While the patch is out of range, say so in the header; streaming carries on and resumes by itself.
        var lost = !s.Connected && _engine.State is EngineState.Connected or EngineState.Streaming && !IsReplayActive;
        if (lost) StateText = _engine.State == EngineState.Streaming ? "Streaming - patch out of range, reconnecting" : "Waiting for the patch";
        if (lost != _patchLost)
        {
            _patchLost = lost;
            if (!lost) OnStateChanged();
        }
        Battery = s.BatteryPercent is int b ? (s.BatteryVolts is double v ? $"Battery {b}% ({v:0.00} V)" : $"Battery {b}%") : "Battery: not reported yet";
        Fp1Contact = Contact(s, 0);
        Fp2Contact = Contact(s, 1);

        if (_engine.State is EngineState.Connected or EngineState.Streaming)
            LinkState = s.Connected || IsReplayActive ? "up" : "lost";
        UpdateLinkText();
        BatteryVolts = s.Connected && s.BatteryVolts is double volts ? $"{volts:0.00} V" : s.Connected && s.BatteryPercent is int pct ? $"{pct}%" : "";
        // Segoe MDL2 Battery0..Battery9 are U+E850..U+E859, Battery10 is U+E83F.
        var level = s.BatteryPercent is int p2 ? (int)Math.Round(Math.Clamp(p2, 0, 100) / 10.0) : 0;
        BatteryGlyph = level >= 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
        ContactAfz = ContactState(s, 0);
        ContactFp2 = ContactState(s, 1);
        OnPropertyChanged(nameof(ContactTooltip));
        OnPropertyChanged(nameof(LinkTooltip));
    }

    /// <summary>The one status shown in the header, next to the Bluetooth icon.</summary>
    private void UpdateLinkText()
    {
        LinkText = _engine.State switch
        {
            EngineState.Disconnected => "Not connected",
            EngineState.Connecting => "Connecting...",
            EngineState.Connected when LinkState == "lost" => "Waiting for the patch",
            EngineState.Connected => IsReplayActive ? "Replay ready" : "Connected",
            EngineState.Streaming when LinkState == "lost" => "Out of range, reconnecting",
            EngineState.Streaming => IsReplayActive ? "Streaming (replay)" : "Streaming",
            _ => ""
        };
    }

    private static string ContactState(DeviceStatus s, int ch) =>
        !s.Connected ? "none" : ch < s.Contact.Length && s.Contact[ch] ? "good" : "poor";

    private bool _wasStreaming;

    private bool _patchLost;

    private static string Contact(DeviceStatus s, int ch)
    {
        if (!s.Connected) return "-";
        var ok = ch < s.Contact.Length && s.Contact[ch];
        var imp = ch < s.ImpedanceKohm.Length ? s.ImpedanceKohm[ch] : null;
        var text = ok ? "Good contact" : "Poor contact";
        return imp is double k ? $"{text}, {k:0.0} kOhm" : text;
    }

    private void RefreshClients()
    {
        var rows = _engine.Clients().Select(c => new ClientRow(c.Name, c.Origin, c.Kind, c.Scopes, c.Connected.ToLocalTime().ToString("HH:mm:ss"), c.FramesSent, c.FramesDropped)).ToList();
        if (rows.SequenceEqual(Clients)) return;
        Clients.Clear();
        foreach (var r in rows) Clients.Add(r);
    }

    private void RefreshSourceOptions()
    {
        var previous = SelectedSource?.Kind;
        SourceOptions.Clear();
        SourceOptions.Add(new SourceOption("LucidPro patch (USB dongle or Bluetooth)", SourceKind.Patch));
        if (Settings.DeveloperMode)
        {
            SourceOptions.Add(new SourceOption("Simulated patch (developer)", SourceKind.Simulated));
            SourceOptions.Add(new SourceOption("Replay a recording (developer)", SourceKind.Replay));
        }
        SelectedSource = SourceOptions.FirstOrDefault(o => o.Kind == previous) ?? SourceOptions[0];
    }

    private void SaveStreamSelection()
    {
        Settings.LslStreams = LslStreams.Where(s => s.Enabled).Select(s => s.Key).ToList();
        Save();
    }

    private void Save() => Settings.Save();

    private void ShowError(Exception ex)
    {
        Error = ex.Message;
        Log.Error(ex.Message);
    }

    private void RaiseAll()
    {
        if (AllCommands == null) return;
        foreach (var c in AllCommands) c.Raise();
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    public async Task StopStreamingAsync() => await _engine.StopStreamingAsync();

    public async ValueTask DisposeAsync()
    {
        Settings.Save();
        await Cloud.DisposeAsync();
        await _engine.DisposeAsync();
    }
}