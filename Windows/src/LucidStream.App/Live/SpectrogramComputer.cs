using System;
using System.Collections.Generic;

namespace LucidStream.App.Live;

/// <summary>Spectrogram settings (the Cloud viewer's defaults: 2 s Hann FFT, 87.5 % overlap, 0-30 Hz).</summary>
public sealed record SpectrogramParams(double FftSeconds = 2, double OverlapPercent = 87.5, double LineHz = 60, bool NotchLine = true)
{
    public int WindowSamples => (int)Math.Round(FftSeconds * LiveSignal.Rate);
    public int HopSamples => Math.Max(1, (int)Math.Round(WindowSamples * (1 - OverlapPercent / 100)));
}

/// <summary>One spectrogram column: power in dB from 0 Hz up, centered on sample <see cref="Center"/>.</summary>
public sealed record SpectrogramColumn(long Center, float[] Db);

/// <summary>
/// Short-time spectra of the raw EEG (mean removed, Hann window, zero-padded FFT), one column per hop for each
/// channel, kept for as long as the longest view window. Runs on the UI thread a few times a second.
/// </summary>
public sealed class SpectrogramComputer
{
    public const double MaxHistorySeconds = 30 * 60;

    private readonly List<SpectrogramColumn>[] _columns = { new(), new() };
    private SpectrogramParams _p = new();
    private long _nextEnd = -1;
    private double[] _window = Array.Empty<double>();
    private int _nfft;

    public SpectrogramParams Params => _p;
    public double BinHz => LiveSignal.Rate / _nfft;
    public IReadOnlyList<SpectrogramColumn> Columns(int channel) => _columns[channel];

    /// <summary>Sample index of the first column after a reset (the start of the time axis).</summary>
    public long Origin { get; private set; } = -1;

    public SpectrogramComputer() => Configure(new SpectrogramParams());

    public void Configure(SpectrogramParams p)
    {
        _p = p;
        var n = p.WindowSamples;
        _nfft = 1;
        while (_nfft < n) _nfft <<= 1;
        _window = new double[n];
        for (var i = 0; i < n; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        Clear();
    }

    public void Clear()
    {
        foreach (var c in _columns) c.Clear();
        _nextEnd = -1;
        Origin = -1;
    }

    /// <summary>Add every column that the newly received data completes. Returns true when something was added.</summary>
    public bool Update(LiveSignal signal)
    {
        var head = signal.Head;
        var n = _p.WindowSamples;
        if (head < n) return false;
        if (_nextEnd < 0 || head - _nextEnd > LiveSignal.Capacity - n)
        {
            // First data, or fell far behind: start at the newest complete window.
            _nextEnd = head;
            if (Origin < 0) Origin = head - n / 2;
        }
        var added = false;
        var seg = new float[n];
        var re = new double[_nfft];
        var im = new double[_nfft];
        var maxKeep = (int)(MaxHistorySeconds * LiveSignal.Rate / _p.HopSamples) + 2;
        while (_nextEnd <= head)
        {
            var start = _nextEnd - n;
            for (var ch = 0; ch < 2; ch++)
            {
                signal.Read(WaveSignal.Raw, ch, start, seg);
                var col = Spectrum(seg, re, im);
                var list = _columns[ch];
                list.Add(new SpectrogramColumn(start + n / 2, col));
                if (list.Count > maxKeep) list.RemoveRange(0, list.Count - maxKeep);
            }
            _nextEnd += _p.HopSamples;
            added = true;
        }
        return added;
    }

    private float[] Spectrum(float[] seg, double[] re, double[] im)
    {
        var bins = _nfft / 2 + 1;
        var db = new float[bins];
        double mean = 0;
        foreach (var v in seg)
        {
            if (float.IsNaN(v))
            {
                Array.Fill(db, float.NaN);
                return db;
            }
            mean += v;
        }
        mean /= seg.Length;
        Array.Clear(re);
        Array.Clear(im);
        double wp = 0;
        for (var i = 0; i < seg.Length; i++)
        {
            re[i] = (seg[i] - mean) * _window[i];
            wp += _window[i] * _window[i];
        }
        Fft(re, im);
        var scale = 2.0 / (LiveSignal.Rate * wp);
        for (var k = 0; k < bins; k++) db[k] = (float)(10 * Math.Log10((re[k] * re[k] + im[k] * im[k]) * scale + 1e-9));
        if (_p.NotchLine && _p.LineHz > 0)
        {
            // Bridge the mains-hum bins (and its harmonic) so line noise does not paint a bright stripe.
            for (var h = 1; h <= 2; h++)
            {
                var f = _p.LineHz * h;
                var lo = (int)Math.Floor((f - 1.5) / BinHz);
                var hi = (int)Math.Ceiling((f + 1.5) / BinHz);
                if (lo < 1 || hi >= bins - 1) continue;
                for (var k = lo; k <= hi; k++) db[k] = db[lo - 1] + (db[hi + 1] - db[lo - 1]) * (k - lo + 1) / (hi - lo + 2);
            }
        }
        return db;
    }

    /// <summary>In-place radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}