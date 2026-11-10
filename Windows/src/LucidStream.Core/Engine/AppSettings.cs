using System.Text.Json;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Recording;
using LucidStream.Core.Streams;
using LucidStream.Core.Ws;

namespace LucidStream.Core.Engine;

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