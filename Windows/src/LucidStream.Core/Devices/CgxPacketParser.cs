namespace LucidStream.Core.Devices;

/// <summary>
/// Bluetooth settings for the LucidPro (CGX) patch.
/// TODO(protocol): fill these in from CGX's protocol document or from the existing Lucidify Windows/Android app.
/// </summary>
public sealed record PatchBleSettings
{
    /// <summary>Advertised name prefix used to find patches while scanning.</summary>
    public string NamePrefix { get; init; } = "CGX";

    /// <summary>GATT service that carries EEG data. Empty until the CGX protocol is known.</summary>
    public string DataServiceUuid { get; init; } = "";

    /// <summary>Notify characteristic delivering EEG/accelerometer packets.</summary>
    public string DataCharacteristicUuid { get; init; } = "";

    /// <summary>Optional write characteristic for start/stop and impedance-check commands.</summary>
    public string CommandCharacteristicUuid { get; init; } = "";

    /// <summary>Command bytes (hex) that start streaming, if the patch needs one.</summary>
    public string StartCommandHex { get; init; } = "";

    /// <summary>Accelerometer sample rate. TODO(protocol): confirm with CGX.</summary>
    public double AccelRateHz { get; init; } = 25;

    /// <summary>How long to scan for a patch before giving up.</summary>
    public int ScanSeconds { get; init; } = 15;

    public bool IsConfigured => DataServiceUuid.Length > 0 && DataCharacteristicUuid.Length > 0;
}

/// <summary>
/// Placeholder parser for CGX patch packets.
///
/// What it needs (from CGX or the existing Lucidify app code):
///   - packet layout: header/sync bytes, packet counter, samples per packet, sample byte order (24-bit, big or little endian),
///   - ADC-to-microvolt scale factor and channel order (Fp1, Fp2),
///   - where accelerometer, battery and impedance values live and their scales,
///   - whether a hardware sample counter or device timestamp is included.
/// Once known, implement TryParse and set <see cref="IsImplemented"/> to true. Everything downstream
/// (timestamps, gap detection, LSL, WebSocket, recording) already works from the EegBlock it returns.
/// </summary>
public sealed class CgxPacketParser : IPatchPacketParser
{
    /// <summary>Set to true once TryParse decodes real CGX packets.</summary>
    public static bool IsImplemented => false;

    public bool TryParse(ReadOnlySpan<byte> payload, double receivedAt, out EegBlock? eeg, out AccelBlock? accel, out DeviceStatus? status)
    {
        eeg = null;
        accel = null;
        status = null;
        return false;
    }

    /// <summary>Helper for the eventual implementation: signed 24-bit big-endian integer.</summary>
    public static int ReadInt24BigEndian(ReadOnlySpan<byte> b) => ((b[0] << 24) | (b[1] << 16) | (b[2] << 8)) >> 8;

    /// <summary>Helper for the eventual implementation: signed 24-bit little-endian integer.</summary>
    public static int ReadInt24LittleEndian(ReadOnlySpan<byte> b) => ((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) >> 8;
}