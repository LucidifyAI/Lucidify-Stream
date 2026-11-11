using System.Text.Json;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Recording;
using LucidStream.Core.Streams;
using LucidStream.Core.Ws;

namespace LucidStream.Core.Engine;

/// <summary>Live view options. Defaults match the Lucidify Cloud viewer.</summary>
public sealed class LiveViewSettings
{
    // Waveform
    public double WaveWindowSeconds { get; set; } = 10;

    /// <summary>"auto" or a half-range in microvolts per row.</summary>
    public string Amplitude { get; set; } = "auto";

    public double Gain { get; set; } = 1;

    /// <summary>"display" (as in the Cloud viewer), "filtered" (0.5-40 Hz) or "raw".</summary>
    public string Signal { get; set; } = "display";

    // Spectrogram
    public double SpecWindowSeconds { get; set; } = 600;
    public double SpecMaxHz { get; set; } = 30;
    public double SpecFftSeconds { get; set; } = 2;
    public double SpecOverlapPercent { get; set; } = 87.5;
    public double SpecBrightnessDb { get; set; }
    public double SpecGamma { get; set; } = 1.3;
    public bool SpecNotch { get; set; } = true;

    /// <summary>"both", "afz" or "fp2".</summary>
    public string SpecChannels { get; set; } = "both";
}

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\LucidStream\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>WebSocket port. 8765 by default; if busy the next free port is used and logged.</summary>
    public int WebSocketPort { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>LAN mode (off by default): also accept paired clients on this IPv4 address.</summary>
    public bool LanEnabled { get; set; }
    public string LanAddress { get; set; } = "";

    public int MaxClients { get; set; } = 4;

    public bool LslEnabled { get; set; } = true;
    public List<string> LslStreams { get; set; } = new()
    {
        StreamKeys.EegRaw, StreamKeys.EegFiltered, StreamKeys.Acc, StreamKeys.BandPower,
        StreamKeys.Quality, StreamKeys.Status, StreamKeys.Markers
    };

    /// <summary>Mains frequency for the notch: 60 (Americas) or 50.</summary>
    public double LineFrequencyHz { get; set; } = 60;

    public bool RecordSession { get; set; } = true;
    public string RecordFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lucid-Stream", "Sessions");

    /// <summary>
    /// Web pages allowed to connect from a browser, as origins (https://app.example.org; http only for localhost).
    /// Apps outside a browser (Python, MATLAB, native code) send no Origin and need only a pairing code.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>The Lucidify Cloud viewer shown in the Cloud window; it also provides sign-in and the patient picker.</summary>
    public string CloudViewerUrl { get; set; } = "https://lucidify-cloud.web.app/cloud_session_viewer.html";

    /// <summary>Firebase JS SDK version the Cloud viewer loads (the app asks the page's Firebase session for tokens).</summary>
    public string CloudFirebaseJsVersion { get; set; } = "10.7.1";

    /// <summary>How the live waveform and spectrogram are shown (the settings under each view).</summary>
    public LiveViewSettings LiveView { get; set; } = new();

    /// <summary>Developer mode: the simulated patch and replay sources.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Fixed BLE delivery latency subtracted from arrival times. TODO(validation): measure (spec, Timing).</summary>
    public double BleLatencyMs { get; set; } = 40;

    /// <summary>Optional opaque study code written into LSL metadata. Never a name or MRN.</summary>
    public string StudyCode { get; set; } = "";

    public List<string> MarkerPresets { get; set; } = new() { "eyes open", "eyes closed", "task start", "task end", "stimulus on", "stimulus off" };

    public PatchBleSettings Ble { get; set; } = new();

    public double SimulatedPacketLoss { get; set; }

    // -------------------------------------------------------------- persistence

    public static string DefaultPath => Path.Combine(AllowlistLoader.UpdateFolder, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SessionJson.Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read ({ex.Message}); using defaults.");
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SessionJson.Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}