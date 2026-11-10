using System.Globalization;
using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Streams;

namespace LucidStream.Core.Lsl;

/// <summary>
/// One LSL outlet per enabled stream. Names, types, source ids and metadata follow the spec's
/// "LSL stream specification" section and the XDF EEG meta-data recommendation.
/// </summary>
public sealed class LslPublisher : IStreamSink, IDisposable
{
    public const string SchemaVersion = "1.0.0";
    public const string IntendedUse = "Research use only. Not for diagnosis or clinical decision-making.";

    private readonly Dictionary<string, (IntPtr Info, IntPtr Outlet)> _outlets = new();
    private readonly object _gate = new();
    private bool _disposed;

    public IReadOnlyCollection<string> Keys => _outlets.Keys;

    public LslPublisher(StreamCatalog catalog, DeviceInfo device, IEnumerable<string> enabledKeys,
        string appName, string filterDescription, double bleLatencySeconds, ReplayDescriptor? replay, string? studyCode)
    {
        if (!LslNative.IsAvailable) throw new InvalidOperationException("liblsl is not available.");
        var enabled = new HashSet<string>(enabledKeys);
        foreach (var def in catalog.Streams.Where(s => enabled.Contains(s.Key)))
        {
            var name = $"{device.Model}_{Sanitize(device.Serial)}_{def.LslSuffix}";
            var sourceId = $"lucidify:{device.Serial}:{def.Key}" + (replay != null ? ":replay" : "");
            var rate = def.IsIrregular ? 0.0 : def.Rate;
            var format = def.IsString ? LslNative.CftString : LslNative.CftFloat32;
            var info = LslNative.lsl_create_streaminfo(name, def.LslType, def.ChannelCount, rate, format, sourceId);
            if (info == IntPtr.Zero) { Log.Error($"LSL: could not create stream info for {def.Key}"); continue; }
            WriteDesc(info, def, device, appName, filterDescription, bleLatencySeconds, replay, studyCode);
            // chunk_size 0 = one chunk per push (we already push 40 ms chunks); 360 s of buffering for slow inlets.
            var outlet = LslNative.lsl_create_outlet(info, 0, 360);
            if (outlet == IntPtr.Zero) { LslNative.lsl_destroy_streaminfo(info); Log.Error($"LSL: could not open outlet {name}"); continue; }
            _outlets[def.Key] = (info, outlet);
        }
        Log.Info($"LSL outlets open: {string.Join(", ", _outlets.Keys)}");
    }

    public void OnFrame(StreamFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || !_outlets.TryGetValue(frame.Def.Key, out var o)) return;
            if (frame.Text != null) LslNative.PushString(o.Outlet, frame.Text, frame.T0);
            else if (frame.Data != null) LslNative.PushChunk(o.Outlet, frame.Data, frame.Timestamps);
        }
    }

    // LSL has no gap message: the XDF importer sees the timestamp jump. NaN-filled samples travel as NaN.
    public void OnGap(GapNotice gap) { }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (info, outlet) in _outlets.Values)
            {
                LslNative.lsl_destroy_outlet(outlet);
                LslNative.lsl_destroy_streaminfo(info);
            }
            _outlets.Clear();
        }
        Log.Info("LSL outlets closed.");
    }

    private static void WriteDesc(IntPtr info, StreamDef def, DeviceInfo device, string appName, string filterDescription,
        double latency, ReplayDescriptor? replay, string? studyCode)
    {
        var desc = LslNative.lsl_get_desc(info);
        Child(desc, "manufacturer", "Lucidify (hardware: CGX)");
        Child(desc, "model", device.Model);
        Child(desc, "serial_number", device.Serial);
        Child(desc, "firmware", device.Firmware);
        Child(desc, "app", appName);
        Child(desc, "schema_version", SchemaVersion);
        Child(desc, "stream_key", def.Key);
        Child(desc, "intended_use", IntendedUse);
        Child(desc, "ble_latency_correction_ms", (latency * 1000).ToString("0.###", CultureInfo.InvariantCulture));

        var channels = LslNative.lsl_append_child(desc, "channels");
        foreach (var label in def.Channels)
        {
            var ch = LslNative.lsl_append_child(channels, "channel");
            Child(ch, "label", label);
            Child(ch, "unit", ChannelUnit(def, label));
            Child(ch, "type", ChannelType(def));
        }

        if (def.Key is StreamKeys.EegRaw or StreamKeys.EegFiltered)
        {
            var reference = LslNative.lsl_append_child(desc, "reference");
            Child(reference, "label", device.Reference);
            Child(reference, "subtracted", "Yes");
            var acq = LslNative.lsl_append_child(desc, "acquisition");
            Child(acq, "resolution_bits", "24");
            Child(acq, "hardware_bandwidth", "0-131 Hz");
            var filtering = LslNative.lsl_append_child(desc, "filtering");
            Child(filtering, "software", def.Key == StreamKeys.EegRaw ? "none" : filterDescription);
        }
        if (def.Key == StreamKeys.BandPower)
        {
            var m = LslNative.lsl_append_child(desc, "method");
            Child(m, "description", "Hann-windowed periodogram of raw EEG, 2 s window, 250 ms hop, mean removed; relative = band / 1-45 Hz total");
            foreach (var b in Bands.All) Child(m, b.Name, $"{b.Lo}-{b.Hi} Hz");
        }
        if (def.Key == StreamKeys.Quality)
        {
            Child(desc, "method", "impedance from device; quality index is a heuristic (flatline, saturation, line noise, amplitude, gaps, impedance)");
        }
        if (replay != null)
        {
            var r = LslNative.lsl_append_child(desc, "replay");
            Child(r, "source", replay.SourceId);
            Child(r, "recorded_utc", replay.RecordedUtc.ToString("o"));
        }
        if (!string.IsNullOrWhiteSpace(studyCode))
        {
            var s = LslNative.lsl_append_child(desc, "subject");
            Child(s, "id", studyCode);
        }
    }

    private static string ChannelUnit(StreamDef def, string label) => def.Key switch
    {
        StreamKeys.BandPower => label.EndsWith("_abs") ? "microvolts^2" : "fraction",
        StreamKeys.Quality => label.EndsWith("_impedance") ? "kiloohms" : "index",
        StreamKeys.EegRaw or StreamKeys.EegFiltered => "microvolts",
        _ => def.Unit
    };

    private static string ChannelType(StreamDef def) => def.Key switch
    {
        StreamKeys.EegRaw or StreamKeys.EegFiltered => "EEG",
        StreamKeys.Acc => "ACC",
        _ => "MISC"
    };

    private static void Child(IntPtr parent, string name, string value) => LslNative.lsl_append_child_value(parent, name, value);

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
}