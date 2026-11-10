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
    /// <summary>WebSocket port. 8765 by default; if busy the next free port is used and passed in the launch URL.</summary>
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
    /// Approved hosted Listen page. TODO(product): set to the real approved HTTPS page; its origin must be in config/allowlist.json.
    /// </summary>
    public string ListenUrl { get; set; } = "https://listen.example.com/listen";

    /// <summary>Developer mode: dev sources (simulator, replay), dev origins, and the bundled Listen page on DevPagePort.</summary>
    public bool DeveloperMode { get; set; }
    public int DevPagePort { get; set; } = ProtocolInfo.DefaultDevPagePort;

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