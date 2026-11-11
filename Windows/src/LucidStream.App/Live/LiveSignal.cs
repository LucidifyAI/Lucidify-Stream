using System;
using System.Diagnostics;

namespace LucidStream.App.Live;

/// <summary>What the waveform shows.</summary>
public enum WaveSignal
{
    /// <summary>As the Cloud viewer shows it: slow drift removed (0.3 s), then smoothed below about 15 Hz.</summary>
    Display,

    /// <summary>The app's filtered EEG stream (0.5-40 Hz band-pass and line-noise notch).</summary>
    Filtered,

    /// <summary>The raw EEG, with its DC offset taken out for display.</summary>
    Raw
}

/// <summary>
/// The last couple of minutes of AFz and Fp2 for the live views, indexed by sample number (gaps stay as NaN).
/// Filled from the stream frames on the acquisition thread; read by the views on the UI thread.
/// </summary>
public sealed class LiveSignal
{
    public const double Rate = 500;
    public const int Capacity = 1 << 16; // about 131 s
    private const int Mask = Capacity - 1;

    private readonly object _gate = new();
    private readonly float[][] _raw = { new float[Capacity], new float[Capacity] };
    private readonly float[][] _filtered = { new float[Capacity], new float[Capacity] };
    private readonly float[][] _display = { new float[Capacity], new float[Capacity] };
    private long _rawHead, _filteredHead;
    private readonly double[] _dc = new double[2], _low = new double[2];
    private readonly bool[] _started = new bool[2];
    private long _headTicks;

    public LiveSignal() => Reset();

    /// <summary>One past the newest sample index received.</summary>
    public long Head
    {
        get { lock (_gate) return _rawHead; }
    }

    /// <summary>Seconds since the newest samples arrived (for a smooth sweep between deliveries).</summary>
    public double SecondsSinceHead => (Stopwatch.GetTimestamp() - System.Threading.Interlocked.Read(ref _headTicks)) / (double)Stopwatch.Frequency;

    public void Reset()
    {
        lock (_gate)
        {
            foreach (var a in _raw) Array.Fill(a, float.NaN);
            foreach (var a in _filtered) Array.Fill(a, float.NaN);
            foreach (var a in _display) Array.Fill(a, float.NaN);
            _rawHead = _filteredHead = 0;
            Array.Clear(_started);
        }
    }

    /// <summary>Raw EEG frame: interleaved AFz, Fp2 in microvolts, starting at sample <paramref name="index"/>.</summary>
    public void AddRaw(long index, float[] data, int count)
    {
        // Cloud-viewer display filter: one-pole DC tracker (0.3 s) then a one-pole low-pass at 15 Hz.
        const double dt = 1 / Rate;
        var dcAlpha = 1 - Math.Exp(-1 / (Rate * 0.3));
        var rc = 1 / (2 * Math.PI * 15);
        var lpAlpha = dt / (rc + dt);
        lock (_gate)
        {
            if (index > _rawHead) Blank(_raw, _rawHead, index, alsoDisplay: true);
            for (var i = 0; i < count; i++)
            {
                var at = (int)((index + i) & Mask);
                for (var ch = 0; ch < 2; ch++)
                {
                    var v = data[i * 2 + ch];
                    _raw[ch][at] = v;
                    if (float.IsNaN(v))
                    {
                        _display[ch][at] = float.NaN;
                        _started[ch] = false;
                        continue;
                    }
                    if (!_started[ch])
                    {
                        _dc[ch] = v;
                        _low[ch] = 0;
                        _started[ch] = true;
                    }
                    _dc[ch] += dcAlpha * (v - _dc[ch]);
                    _low[ch] += lpAlpha * (v - _dc[ch] - _low[ch]);
                    _display[ch][at] = (float)_low[ch];
                }
            }
            _rawHead = Math.Max(_rawHead, index + count);
        }
        System.Threading.Interlocked.Exchange(ref _headTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>Filtered EEG frame, same layout as <see cref="AddRaw"/>.</summary>
    public void AddFiltered(long index, float[] data, int count)
    {
        lock (_gate)
        {
            if (index > _filteredHead) Blank(_filtered, _filteredHead, index, alsoDisplay: false);
            for (var i = 0; i < count; i++)
            {
                var at = (int)((index + i) & Mask);
                _filtered[0][at] = data[i * 2];
                _filtered[1][at] = data[i * 2 + 1];
            }
            _filteredHead = Math.Max(_filteredHead, index + count);
        }
    }

    private void Blank(float[][] rings, long from, long to, bool alsoDisplay)
    {
        var n = Math.Min(to - from, Capacity);
        for (long k = to - n; k < to; k++)
        {
            var at = (int)(k & Mask);
            rings[0][at] = rings[1][at] = float.NaN;
            if (alsoDisplay) _display[0][at] = _display[1][at] = float.NaN;
        }
        if (alsoDisplay) Array.Clear(_started);
    }

    /// <summary>Copy samples [start, start + dest.Length) of one channel; NaN where there is no data.</summary>
    public void Read(WaveSignal signal, int channel, long start, Span<float> dest)
    {
        lock (_gate)
        {
            var ring = (signal switch { WaveSignal.Raw => _raw, WaveSignal.Filtered => _filtered, _ => _display })[channel];
            var head = signal == WaveSignal.Filtered ? _filteredHead : _rawHead;
            var oldest = head - Capacity;
            for (var i = 0; i < dest.Length; i++)
            {
                var k = start + i;
                dest[i] = k < 0 || k >= head || k < oldest ? float.NaN : ring[(int)(k & Mask)];
            }
        }
    }
}