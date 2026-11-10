namespace LucidStream.Core.Streams;

/// <summary>
/// One chunk of one stream, as produced by the stream core and fanned out to LSL, WebSocket and the session file.
/// Numeric streams carry <see cref="Data"/> (interleaved by channel); string streams carry <see cref="Text"/>.
/// </summary>
public sealed class StreamFrame
{
    public required StreamDef Def { get; init; }

    /// <summary>Per-stream frame sequence number.</summary>
    public required uint Seq { get; init; }

    /// <summary>Index of the first sample since the session started (per stream).</summary>
    public required long Index { get; init; }

    /// <summary>LSL-clock timestamp of every sample.</summary>
    public required double[] Timestamps { get; init; }

    public float[]? Data { get; init; }

    public string? Text { get; init; }

    public bool HasNaN { get; init; }

    public bool Replay { get; init; }

    public int SampleCount => Timestamps.Length;
    public double T0 => Timestamps[0];
    public long EndIndexExclusive => Index + SampleCount;
}

/// <summary>Reported when samples are missing from a stream.</summary>
public sealed record GapNotice(string StreamKey, long FromIndex, long Count, string Reason, bool Filled);