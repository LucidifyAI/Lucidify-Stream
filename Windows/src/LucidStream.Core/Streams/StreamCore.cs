using LucidStream.Core.Common;
using LucidStream.Core.Devices;
using LucidStream.Core.Dsp;

namespace LucidStream.Core.Streams;

/// <summary>Receives every frame and gap notice the stream core produces.</summary>
public interface IStreamSink
{
    void OnFrame(StreamFrame frame);
    void OnGap(GapNotice gap);
}

/// <summary>
/// The stream core: one timebase, one sample index and one set of feature computations for every output.
///
/// Timestamp rule (spec, "Timing"): the newest sample of each device packet is stamped with
/// (arrival time on the LSL clock - fixed BLE latency); earlier samples step back at the nominal rate.
/// No smoothing or dejittering is applied. Raw values are never altered.
///
/// Gaps (needs a device sample counter): up to 1 s is filled with NaN so the sample clock stays continuous;
/// longer gaps are not filled, the index jumps by the missing count, and a gap notice is sent.
/// </summary>
public sealed class StreamCore
{
    public const int EegChunkSamples = 20;          // 40 ms at 500 Hz
    public const double MaxFilledGapSeconds = 1.0;
    public const double StatusIntervalSeconds = 10;

    private readonly object _gate = new();
    private readonly List<IStreamSink> _sinks = new();
    private readonly DeviceInfo _device;
    private readonly bool _replay;
    private readonly double _latency;
    private readonly int _ch;
    private readonly double _fs;
    private readonly EegFilter _filter;
    private BandPowerExtractor _bandPower;
    private QualityExtractor _quality;
    private readonly double _lineHz;
    private readonly Func<DeviceStatus> _status;

    private readonly Dictionary<string, (uint Seq, long Index)> _counters = new();

    private readonly float[] _pendRaw, _pendFilt;
    private readonly double[] _pendTs;
    private int _pendCount;
    private bool _pendNaN;
    private long _pendStartIndex;
    private long _nextRawIndex;

    private long? _expectedCounter;
    private double _lastEegTs = double.NegativeInfinity;

    public StreamCatalog Catalog { get; }
    public long DroppedSamples { get; private set; }
    public long ReceivedSamples { get; private set; }
    public double SessionStart { get; } = Clock.Now;

    public StreamCore(DeviceInfo device, bool replay, double lineHz, double bleLatencySeconds, Func<DeviceStatus> statusProvider)
    {
        _device = device;
        _replay = replay;
        _latency = bleLatencySeconds;
        _status = statusProvider;
        _ch = device.EegLabels.Length;
        _fs = device.EegRate;
        _lineHz = lineHz;
        Catalog = new StreamCatalog(device);
        _filter = new EegFilter(_ch, _fs, lineHz);
        _bandPower = new BandPowerExtractor(_ch, _fs);
        _quality = new QualityExtractor(_ch, _fs, lineHz);
        _pendRaw = new float[EegChunkSamples * _ch];
        _pendFilt = new float[EegChunkSamples * _ch];
        _pendTs = new double[EegChunkSamples];
    }

    public string FilterDescription => _filter.Describe();

    public void AddSink(IStreamSink sink) { lock (_gate) _sinks.Add(sink); }
    public void RemoveSink(IStreamSink sink) { lock (_gate) _sinks.Remove(sink); }

    // ---------------------------------------------------------------- EEG

    public void OnEeg(EegBlock block)
    {
        if (block.Channels != _ch || block.SampleCount == 0) return;
        lock (_gate)
        {
            var n = block.SampleCount;
            ReceivedSamples += n;
            var tLast = block.ReceivedAt - _latency;
            var t0 = tLast - (n - 1) / _fs;

            if (block.DeviceCounter is long counter)
            {
                if (_expectedCounter is long expected)
                {
                    var missing = counter - expected;
                    if (missing > 0) HandleGap(missing, t0);
                    else if (missing < 0) Log.Warn($"Device sample counter went backwards by {-missing}; resynchronizing.");
                }
                _expectedCounter = counter + n;
            }

            var ts = new double[n];
            for (var i = 0; i < n; i++)
            {
                ts[i] = t0 + i / _fs;
                // Keep timestamps strictly increasing even if packets arrive bunched up.
                if (ts[i] <= _lastEegTs) ts[i] = _lastEegTs + 1e-6;
                _lastEegTs = ts[i];
            }
            Append(block.Samples, ts);
        }
    }

    private void HandleGap(long missing, double nextT0)
    {
        DroppedSamples += missing;
        var maxFill = (long)(MaxFilledGapSeconds * _fs);
        if (missing <= maxFill)
        {
            var from = _nextRawIndex + _pendCount;
            var nan = new float[missing * _ch];
            Array.Fill(nan, float.NaN);
            var ts = new double[missing];
            for (var i = 0; i < missing; i++)
            {
                ts[i] = nextT0 - (missing - i) / _fs;
                if (ts[i] <= _lastEegTs) ts[i] = _lastEegTs + 1e-6;
                _lastEegTs = ts[i];
            }
            Append(nan, ts);
            Gap(new GapNotice(StreamKeys.EegRaw, from, missing, "ble_packet_loss", true));
            Gap(new GapNotice(StreamKeys.EegFiltered, from, missing, "ble_packet_loss", true));
        }
        else
        {
            FlushPending();
            var from = _nextRawIndex;
            _nextRawIndex += missing;
            // Features must not span the hole.
            _bandPower = new BandPowerExtractor(_ch, _fs);
            _quality = new QualityExtractor(_ch, _fs, _lineHz);
            var reset = new float[_ch];
            Array.Fill(reset, float.NaN);
            _filter.Process(reset, new float[_ch], _ch);
            Gap(new GapNotice(StreamKeys.EegRaw, from, missing, "ble_packet_loss", false));
            Gap(new GapNotice(StreamKeys.EegFiltered, from, missing, "ble_packet_loss", false));
            Log.Warn($"Gap of {missing} samples ({missing / _fs:0.0} s); not filled.");
        }
    }

    private void Append(float[] samples, double[] ts)
    {
        var n = ts.Length;
        var filtered = new float[samples.Length];
        _filter.Process(samples, filtered, _ch);
        var impedance = _status().ImpedanceKohm;

        for (var i = 0; i < n; i++)
        {
            if (_pendCount == 0) _pendStartIndex = _nextRawIndex;
            var sample = samples.AsSpan(i * _ch, _ch);
            sample.CopyTo(_pendRaw.AsSpan(_pendCount * _ch));
            filtered.AsSpan(i * _ch, _ch).CopyTo(_pendFilt.AsSpan(_pendCount * _ch));
            _pendTs[_pendCount] = ts[i];
            for (var c = 0; c < _ch; c++) if (float.IsNaN(sample[c])) _pendNaN = true;
            _pendCount++;
            if (_pendCount == EegChunkSamples) FlushPending();

            var bp = _bandPower.Add(sample);
            if (bp != null) EmitNumeric(StreamKeys.BandPower, bp, new[] { ts[i] }, bp.Any(float.IsNaN));

            var q = _quality.Add(sample, impedance);
            if (q != null) EmitNumeric(StreamKeys.Quality, q, new[] { ts[i] }, false);
        }
    }

    private void FlushPending()
    {
        if (_pendCount == 0) return;
        var n = _pendCount;
        var ts = _pendTs.AsSpan(0, n).ToArray();
        var raw = _pendRaw.AsSpan(0, n * _ch).ToArray();
        var filt = _pendFilt.AsSpan(0, n * _ch).ToArray();
        var start = _pendStartIndex;
        EmitAt(StreamKeys.EegRaw, start, raw, ts, _pendNaN);
        EmitAt(StreamKeys.EegFiltered, start, filt, ts, _pendNaN);
        _nextRawIndex = start + n;
        _pendCount = 0;
        _pendNaN = false;
    }

    // ---------------------------------------------------------------- accelerometer, markers, status

    public void OnAccel(AccelBlock block)
    {
        if (block.SampleCount == 0) return;
        lock (_gate)
        {
            var n = block.SampleCount;
            var rate = _device.AccelRate > 0 ? _device.AccelRate : 25;
            var tLast = block.ReceivedAt - _latency;
            var ts = new double[n];
            for (var i = 0; i < n; i++) ts[i] = tLast - (n - 1 - i) / rate;
            EmitNumeric(StreamKeys.Acc, block.Samples, ts, false);
        }
    }

    /// <summary>Emit a marker now (or at a given LSL time). Returns its timestamp.</summary>
    public double AddMarker(string text, double? at = null)
    {
        lock (_gate)
        {
            var t = at ?? Clock.Now;
            EmitText(StreamKeys.Markers, text, t);
            return t;
        }
    }

    public void PublishStatus(string json)
    {
        lock (_gate) EmitText(StreamKeys.Status, json, Clock.Now);
    }

    /// <summary>Flush any partial EEG chunk (call before stopping).</summary>
    public void Flush()
    {
        lock (_gate) FlushPending();
    }

    // ---------------------------------------------------------------- emit helpers

    private void EmitNumeric(string key, float[] data, double[] ts, bool hasNaN)
    {
        var (_, index) = Counter(key);
        EmitAt(key, index, data, ts, hasNaN);
    }

    private void EmitAt(string key, long index, float[] data, double[] ts, bool hasNaN)
    {
        var (seq, _) = Counter(key);
        var frame = new StreamFrame
        {
            Def = Catalog[key], Seq = seq, Index = index, Timestamps = ts, Data = data, HasNaN = hasNaN, Replay = _replay
        };
        _counters[key] = (seq + 1, index + ts.Length);
        Dispatch(frame);
    }

    private void EmitText(string key, string text, double t)
    {
        var (seq, index) = Counter(key);
        var frame = new StreamFrame
        {
            Def = Catalog[key], Seq = seq, Index = index, Timestamps = new[] { t }, Text = text, Replay = _replay
        };
        _counters[key] = (seq + 1, index + 1);
        Dispatch(frame);
    }

    private (uint Seq, long Index) Counter(string key) => _counters.TryGetValue(key, out var c) ? c : (0u, 0L);

    private void Dispatch(StreamFrame frame)
    {
        foreach (var s in _sinks)
        {
            try { s.OnFrame(frame); }
            catch (Exception ex) { Log.Error($"{s.GetType().Name} failed on {frame.Def.Key}: {ex.Message}"); }
        }
    }

    private void Gap(GapNotice gap)
    {
        foreach (var s in _sinks)
        {
            try { s.OnGap(gap); }
            catch (Exception ex) { Log.Error($"{s.GetType().Name} failed on gap: {ex.Message}"); }
        }
    }
}