using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LucidStream.Core.Recording;

namespace LucidStream.Core.Cloud;

/// <summary>Encrypts spooled patient data at rest.</summary>
public interface ISpoolProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI, current user: only this Windows account on this PC can read the spool.</summary>
public sealed class DpapiProtector : ISpoolProtector
{
    public byte[] Protect(byte[] data) => Run(data, protect: true);
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();
            const int uiForbidden = 0x1;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, uiForbidden, ref output);
            if (!ok) throw new CryptographicException($"Windows data protection failed ({Marshal.GetLastWin32Error()}).");
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            handle.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}

/// <summary>No encryption. For tests and non-Windows builds only.</summary>
public sealed class PlainProtector : ISpoolProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>One Cloud recording, saved next to its chunks so an interrupted upload can finish later.</summary>
public sealed class SpoolSession
{
    public string LocalSessionId { get; set; } = "";
    public string OrgId { get; set; } = "";
    public string SubOrgId { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string OrgName { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public long StartedAtEpochMs { get; set; }
    public string? CloudSessionId { get; set; }
    public int NextSeq { get; set; }
    public int? FinalSeqExclusive { get; set; }
    public long? EndedAtEpochMs { get; set; }
    public bool Finalized { get; set; }
    public List<CloudEvent> PendingEvents { get; set; } = new();
}

public sealed class SpoolChunk
{
    public int Seq { get; set; }
    public long FirstSampleIndex { get; set; }
    public int SampleCount { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? ChunkId { get; set; }
    public bool PutDone { get; set; }
    public bool Verified { get; set; }
    public bool Failed { get; set; }
    public string? LastError { get; set; }
    public long TStartMs => FirstSampleIndex * 1000 / CloudRecorder.SampleRateHz;
    public long TEndMs => (FirstSampleIndex + SampleCount) * 1000 / CloudRecorder.SampleRateHz;
}

/// <summary>
/// On-disk spool, one folder per recording under %LOCALAPPDATA%\LucidStream\cloud: session.json, and per chunk an
/// encrypted payload (.bin) plus its upload state (.json). A payload is deleted once Cloud has verified it.
/// </summary>
public sealed class CloudSpool
{
    private readonly ISpoolProtector _protector;
    private readonly object _io = new();

    public CloudSpool(string folder, ISpoolProtector protector)
    {
        Folder = folder;
        _protector = protector;
        Directory.CreateDirectory(folder);
    }

    public string Folder { get; }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LucidStream", "cloud");

    public static ISpoolProtector DefaultProtector => OperatingSystem.IsWindows() ? new DpapiProtector() : new PlainProtector();

    /// <summary>Recordings in the spool root that have not been finalized.</summary>
    public static IEnumerable<string> Unfinished(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var file = Path.Combine(dir, "session.json");
            SpoolSession? s = null;
            try { if (File.Exists(file)) s = JsonSerializer.Deserialize<SpoolSession>(File.ReadAllText(file), SessionJson.Options); }
            catch { /* unreadable: leave it for inspection */ }
            if (s is { Finalized: false }) yield return dir;
        }
    }

    public SpoolSession LoadSession() =>
        JsonSerializer.Deserialize<SpoolSession>(File.ReadAllText(Path.Combine(Folder, "session.json")), SessionJson.Options)
        ?? throw new InvalidDataException("Empty session.json");

    public void SaveSession(SpoolSession s)
    {
        lock (_io) WriteAtomic(Path.Combine(Folder, "session.json"), JsonSerializer.SerializeToUtf8Bytes(s, SessionJson.Options));
    }

    public SpoolChunk WriteChunk(int seq, long firstSampleIndex, int sampleCount, byte[] payload)
    {
        var chunk = new SpoolChunk
        {
            Seq = seq,
            FirstSampleIndex = firstSampleIndex,
            SampleCount = sampleCount,
            Bytes = payload.Length,
            Sha256 = Convert.ToBase64String(SHA256.HashData(payload)),
        };
        lock (_io)
        {
            WriteAtomic(BinPath(seq), _protector.Protect(payload));
            SaveChunkCore(chunk);
        }
        return chunk;
    }

    public void SaveChunk(SpoolChunk c)
    {
        lock (_io)
        {
            SaveChunkCore(c);
            if (c.Verified && File.Exists(BinPath(c.Seq))) File.Delete(BinPath(c.Seq));
        }
    }

    public List<SpoolChunk> Chunks()
    {
        lock (_io)
        {
            var list = new List<SpoolChunk>();
            foreach (var f in Directory.GetFiles(Folder, "chunk-*.json"))
            {
                try { if (JsonSerializer.Deserialize<SpoolChunk>(File.ReadAllText(f), SessionJson.Options) is { } c) list.Add(c); }
                catch { /* unreadable metadata: skipped */ }
            }
            return list.OrderBy(c => c.Seq).ToList();
        }
    }

    /// <summary>The payload, decrypted and checked against its SHA-256.</summary>
    public byte[] ReadPayload(SpoolChunk c)
    {
        byte[] data;
        lock (_io) data = _protector.Unprotect(File.ReadAllBytes(BinPath(c.Seq)));
        if (data.Length != c.Bytes || Convert.ToBase64String(SHA256.HashData(data)) != c.Sha256)
            throw new InvalidDataException($"Spooled chunk {c.Seq} failed its integrity check.");
        return data;
    }

    public void DeleteAll()
    {
        lock (_io)
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (Exception ex) { Common.Log.Warn($"Could not remove the Cloud spool folder: {ex.Message}"); }
        }
    }

    private void SaveChunkCore(SpoolChunk c) => WriteAtomic(JsonPath(c.Seq), JsonSerializer.SerializeToUtf8Bytes(c, SessionJson.Options));

    private string BinPath(int seq) => Path.Combine(Folder, $"chunk-{seq:D8}.bin");
    private string JsonPath(int seq) => Path.Combine(Folder, $"chunk-{seq:D8}.json");

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}