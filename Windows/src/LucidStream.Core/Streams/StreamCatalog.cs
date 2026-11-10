using LucidStream.Core.Devices;

namespace LucidStream.Core.Streams;

public enum StreamScope { Raw, Processed }

/// <summary>One output stream, identical over LSL and WebSocket. Order in the catalog defines the WebSocket stream id.</summary>
public sealed record StreamDef(
    byte Id,
    string Key,
    string LslSuffix,
    string LslType,
    double Rate,
    string[] Channels,
    string Unit,
    bool IsString,
    StreamScope Scope)
{
    public int ChannelCount => Channels.Length;
    public bool IsIrregular => Rate <= 0;
}

public static class StreamKeys
{
    public const string EegRaw = "eeg.raw";
    public const string EegFiltered = "eeg.filtered";
    public const string Acc = "acc";
    public const string BandPower = "bandpower";
    public const string Quality = "quality";
    public const string Status = "status";
    public const string Markers = "markers";

    /// <summary>Reserved for licensed Lucidify outputs; never served in v1.</summary>
    public static readonly string[] Reserved = { "lucidify.lsi", "lucidify.sleepstage", "lucidify.delirium" };
}

public static class Bands
{
    public static readonly (string Name, double Lo, double Hi)[] All =
    {
        ("delta", 1, 4), ("theta", 4, 8), ("alpha", 8, 13), ("beta", 13, 30), ("gamma", 30, 45)
    };
    public const double TotalLo = 1;
    public const double TotalHi = 45;
}

/// <summary>The fixed v1 stream catalog for a given device.</summary>
public sealed class StreamCatalog
{
    public IReadOnlyList<StreamDef> Streams { get; }

    public StreamCatalog(DeviceInfo device)
    {
        var eeg = device.EegLabels;
        var bp = new List<string>();
        foreach (var ch in eeg)
        {
            foreach (var b in Bands.All) bp.Add($"{ch}_{b.Name}_abs");
            foreach (var b in Bands.All) bp.Add($"{ch}_{b.Name}_rel");
        }
        var quality = eeg.Select(c => $"{c}_impedance").Concat(eeg.Select(c => $"{c}_quality")).ToArray();

        Streams = new List<StreamDef>
        {
            new(0, StreamKeys.EegRaw, "EEG", "EEG", device.EegRate, eeg, "microvolts", false, StreamScope.Raw),
            new(1, StreamKeys.EegFiltered, "EEGFiltered", "EEG", device.EegRate, eeg, "microvolts", false, StreamScope.Raw),
            new(2, StreamKeys.Acc, "ACC", "Accelerometer", device.AccelRate, new[] { "X", "Y", "Z" }, "g", false, StreamScope.Raw),
            new(3, StreamKeys.BandPower, "BandPower", "Lucidify.BandPower", 4, bp.ToArray(), "uV^2 (abs), fraction (rel)", false, StreamScope.Processed),
            new(4, StreamKeys.Quality, "Quality", "Lucidify.Quality", 1, quality, "kOhm (impedance), 0-1 (quality)", false, StreamScope.Processed),
            new(5, StreamKeys.Status, "Status", "Lucidify.Status", 0, new[] { "status" }, "json", true, StreamScope.Processed),
            new(6, StreamKeys.Markers, "Markers", "Markers", 0, new[] { "marker" }, "label", true, StreamScope.Processed),
        };
    }

    public StreamDef? Find(string key) => Streams.FirstOrDefault(s => s.Key == key);
    public StreamDef this[string key] => Find(key) ?? throw new KeyNotFoundException(key);
}