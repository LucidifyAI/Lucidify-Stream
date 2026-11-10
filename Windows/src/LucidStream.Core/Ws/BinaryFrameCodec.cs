using System.Buffers.Binary;
using LucidStream.Core.Streams;

namespace LucidStream.Core.Ws;

/// <summary>
/// Binary data frame (spec, "Binary data frame"): little-endian, 32-byte header, then float32 samples interleaved by channel.
///
///  0  4  magic "LCDF"
///  4  1  frame version (1)
///  5  1  stream id (catalog order in welcome)
///  6  2  flags: bit 0 = contains NaN fill, bit 1 = replay
///  8  4  sequence number per stream
/// 12  8  index of first sample since session start
/// 20  8  timestamp of first sample, LSL clock seconds
/// 28  2  sample count
/// 30  2  channel count
/// 32  .. float32 samples
/// </summary>
public static class BinaryFrameCodec
{
    public const int HeaderSize = 32;
    public const byte Version = 1;
    public static ReadOnlySpan<byte> Magic => "LCDF"u8;

    public const ushort FlagNaN = 1;
    public const ushort FlagReplay = 2;

    public static byte[] Encode(StreamFrame f)
    {
        if (f.Data == null) throw new ArgumentException("binary frames carry numeric streams only");
        var channels = f.Def.ChannelCount;
        var samples = f.SampleCount;
        var buf = new byte[HeaderSize + 4 * samples * channels];
        var s = buf.AsSpan();
        Magic.CopyTo(s);
        s[4] = Version;
        s[5] = f.Def.Id;
        ushort flags = 0;
        if (f.HasNaN) flags |= FlagNaN;
        if (f.Replay) flags |= FlagReplay;
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], f.Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(s[12..], (ulong)f.Index);
        BinaryPrimitives.WriteDoubleLittleEndian(s[20..], f.T0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)samples);
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], (ushort)channels);
        var o = HeaderSize;
        foreach (var v in f.Data)
        {
            BinaryPrimitives.WriteSingleLittleEndian(s[o..], v);
            o += 4;
        }
        return buf;
    }

    public sealed record Decoded(byte StreamId, ushort Flags, uint Seq, long Index, double T0, int Samples, int Channels, float[] Data);

    public static Decoded Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < HeaderSize || !s[..4].SequenceEqual(Magic)) throw new FormatException("not an LCDF frame");
        if (s[4] != Version) throw new FormatException($"unsupported frame version {s[4]}");
        var samples = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
        var data = new float[samples * channels];
        for (var i = 0; i < data.Length; i++) data[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(HeaderSize + 4 * i)..]);
        return new Decoded(s[5], BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(s[12..]), BinaryPrimitives.ReadDoubleLittleEndian(s[20..]), samples, channels, data);
    }
}