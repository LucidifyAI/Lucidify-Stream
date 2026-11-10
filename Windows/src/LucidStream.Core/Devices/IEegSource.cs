namespace LucidStream.Core.Devices;

/// <summary>
/// Anything that produces patch data: the real LucidPro over Bluetooth, the simulator, or a replayed recording.
/// Events may fire on any thread.
/// </summary>
public interface IEegSource : IAsyncDisposable
{
    /// <summary>Short name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Available after <see cref="StartAsync"/> completes.</summary>
    DeviceInfo? Info { get; }

    /// <summary>True for replayed recordings; every output is then marked as replay.</summary>
    bool IsReplay { get; }

    /// <summary>For replay: identifier and original start time of the recording.</summary>
    ReplayDescriptor? Replay { get; }

    DeviceStatus Status { get; }

    event Action<EegBlock>? EegReceived;
    event Action<AccelBlock>? AccelReceived;
    event Action<DeviceStatus>? StatusChanged;
    event Action<SourceMarker>? MarkerReceived;

    /// <summary>Connect and begin delivering data.</summary>
    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}

public sealed record ReplayDescriptor(string SourceId, DateTimeOffset RecordedUtc, bool Loop);

/// <summary>
/// Turns raw notification bytes from the patch into EEG/accelerometer blocks.
/// The CGX packet format is not yet specified for Lucid-Stream; see CgxPacketParser.
/// </summary>
public interface IPatchPacketParser
{
    /// <summary>Parse one notification payload. Returns false if the bytes are not a data packet.</summary>
    bool TryParse(ReadOnlySpan<byte> payload, double receivedAt, out EegBlock? eeg, out AccelBlock? accel, out DeviceStatus? status);
}