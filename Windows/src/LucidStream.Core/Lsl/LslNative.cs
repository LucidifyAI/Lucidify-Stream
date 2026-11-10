using System.Reflection;
using System.Runtime.InteropServices;
using LucidStream.Core.Common;

namespace LucidStream.Core.Lsl;

/// <summary>
/// Minimal P/Invoke surface of the liblsl C API (outlets only). Signatures follow lsl/outlet.h,
/// lsl/streaminfo.h and lsl/xml.h. The native library is lsl.dll on Windows (liblsl.so elsewhere).
/// </summary>
public static class LslNative
{
    private const string Lib = "lsl";

    public const int CftFloat32 = 1;
    public const int CftString = 3;

    /// <summary>True once liblsl has been found and loaded.</summary>
    public static bool IsAvailable { get; private set; }

    public static int LibraryVersion { get; private set; }

    private static bool _initialized;

    /// <summary>
    /// Try to load liblsl. Looks next to the app, in a lib\ folder, and at LUCIDSTREAM_LIBLSL.
    /// Call once at startup, before any timestamp is taken, so the clock never switches source mid-session.
    /// </summary>
    public static bool Initialize()
    {
        if (_initialized) return IsAvailable;
        _initialized = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LslNative).Assembly, Resolve);
        }
        catch (InvalidOperationException) { /* resolver already set */ }

        try
        {
            LibraryVersion = lsl_library_version();
            IsAvailable = true;
            Log.Info($"liblsl {LibraryVersion / 100}.{LibraryVersion % 100} loaded; LSL output available.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            IsAvailable = false;
            Log.Warn("liblsl not found (lsl.dll). LSL output is disabled; WebSocket streaming still works. Run tools\\get-liblsl.ps1.");
        }
        return IsAvailable;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("LUCIDSTREAM_LIBLSL");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
        var baseDir = AppContext.BaseDirectory;
        foreach (var file in new[] { "lsl.dll", "liblsl.so", "liblsl.dylib", "liblsl64.dll" })
        {
            candidates.Add(Path.Combine(baseDir, file));
            candidates.Add(Path.Combine(baseDir, "lib", file));
        }
        foreach (var c in candidates)
        {
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
        }
        return NativeLibrary.TryLoad(name, assembly, path, out var handle) ? handle : IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_library_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern double lsl_local_clock();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_create_streaminfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        int channelCount, double nominalSrate, int channelFormat,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_streaminfo(IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_desc(IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_append_child(IntPtr e, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_append_child_value(IntPtr e,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_create_outlet(IntPtr info, int chunkSize, int maxBuffered);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_outlet(IntPtr outlet);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_have_consumers(IntPtr outlet);

    // data_elements is "unsigned long": 32-bit on Windows, 64-bit on Linux. Two entry points keep both correct.
    [DllImport(Lib, EntryPoint = "lsl_push_chunk_ftnp", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int PushChunkFtnp32(IntPtr outlet, float* data, uint dataElements, double* timestamps, int pushthrough);

    [DllImport(Lib, EntryPoint = "lsl_push_chunk_ftnp", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int PushChunkFtnp64(IntPtr outlet, float* data, ulong dataElements, double* timestamps, int pushthrough);

    public static unsafe int PushChunk(IntPtr outlet, ReadOnlySpan<float> data, ReadOnlySpan<double> timestamps)
    {
        fixed (float* d = data)
        fixed (double* t = timestamps)
        {
            return OperatingSystem.IsWindows()
                ? PushChunkFtnp32(outlet, d, (uint)data.Length, t, 1)
                : PushChunkFtnp64(outlet, d, (ulong)data.Length, t, 1);
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lsl_push_sample_strtp(IntPtr outlet, IntPtr[] data, double timestamp, int pushthrough);

    /// <summary>Push one single-channel string sample (markers, status JSON).</summary>
    public static int PushString(IntPtr outlet, string value, double timestamp)
    {
        var p = Marshal.StringToCoTaskMemUTF8(value);
        try { return lsl_push_sample_strtp(outlet, new[] { p }, timestamp, 1); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}