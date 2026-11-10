namespace LucidStream.Core.Devices;

/// <summary>Static facts about the connected patch (or the recording being replayed).</summary>
public sealed record DeviceInfo(
    string Model,
    string Serial,
    string Firmware,
    double EegRate,
    double AccelRate,
    string[] EegLabels,
    string Reference)
{
    /// <summary>
    /// The patch digitizes two EEG channels, which CGX names AFz and Fp2, both against the patch's reference electrode.
    /// CGX's documents do not name the reference site, so it is reported as unspecified rather than guessed.
    /// </summary>
    public static DeviceInfo LucidPro(string serial, string firmware, double accelRate) =>
        new("LucidPro", serial, firmware, 500, accelRate, new[] { "AFz", "Fp2" }, PatchReference);

    public const string PatchReference = "patch reference electrode (site not specified by CGX)";
}

/// <summary>
/// A block of EEG samples as delivered by the device, interleaved by channel (AFz, Fp2, AFz, Fp2, ...), in microvolts.
/// </summary>
/// <param name="Samples">Interleaved samples, length = sampleCount * channels.</param>
/// <param name="Channels">Channel count (2 for the patch).</param>
/// <param name="ReceivedAt">LSL clock time when the packet arrived at the host.</param>
/// <param name="DeviceCounter">Hardware sample counter of the first sample, if the device provides one. Used to detect lost packets.</param>
public sealed record EegBlock(float[] Samples, int Channels, double ReceivedAt, long? DeviceCounter)
{
    public int SampleCount => Samples.Length / Channels;
}

/// <summary>A block of accelerometer samples, interleaved X, Y, Z, in g.</summary>
public sealed record AccelBlock(float[] Samples, double ReceivedAt)
{
    public int SampleCount => Samples.Length / 3;
}

/// <summary>Live device state reported by the source. Values are null when the device does not report them.</summary>
public sealed record DeviceStatus
{
    public bool Connected { get; init; }
    public int? BatteryPercent { get; init; }
    public double?[] ImpedanceKohm { get; init; } = { null, null };
    public bool[] Contact { get; init; } = { false, false };
    public int? SignalStrengthDbm { get; init; }
    public double? BatteryVolts { get; init; }
    public bool? SdCardPresent { get; init; }

    /// <summary>Per-channel DC offset in millivolts over the last second (the patch is DC-coupled).</summary>
    public double?[] DcOffsetMv { get; init; } = { null, null };

    /// <summary>Packets lost on the link since connecting, from the patch's packet counter.</summary>
    public long PacketsLost { get; init; }
    public string? Message { get; init; }
}

/// <summary>A marker that came from the source itself (for example, a marker replayed from a recording).</summary>
public sealed record SourceMarker(string Label, double ReceivedAt);