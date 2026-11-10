using LucidStream.Core.Streams;

namespace LucidStream.Core.Dsp;

/// <summary>
/// The bandpower stream: every 250 ms (125 samples at 500 Hz) the PSD of the last 2 s of raw EEG
/// (Hann window, mean removed, 0.5 Hz bins) gives absolute power per band (uV^2) and relative power
/// (band / 1-45 Hz total). Output order per channel: 5 absolute bands, then 5 relative bands.
/// A window containing gap-filled samples yields NaN.
/// </summary>
public sealed class BandPowerExtractor
{
    public const double WindowSeconds = 2.0;
    public const double HopSeconds = 0.25;

    private readonly int _channels;
    private readonly int _window;
    private readonly int _hop;
    private readonly float[][] _ring;
    private int _pos;
    private long _count;
    private readonly SpectralWindow _spectrum;
    private readonly double[] _psd;
    private readonly float[] _segment;

    public BandPowerExtractor(int channels, double fs)
    {
        _channels = channels;
        _window = (int)Math.Round(WindowSeconds * fs);
        _hop = (int)Math.Round(HopSeconds * fs);
        _ring = Enumerable.Range(0, channels).Select(_ => new float[_window]).ToArray();
        _spectrum = new SpectralWindow(_window, fs, Bands.TotalHi);
        _psd = new double[_spectrum.LastBin - _spectrum.FirstBin + 1];
        _segment = new float[_window];
    }

    public int OutputCount => _channels * Bands.All.Length * 2;

    /// <summary>Add one interleaved sample. Returns a feature vector when a hop completes, else null.</summary>
    public float[]? Add(ReadOnlySpan<float> sample)
    {
        for (var c = 0; c < _channels; c++) _ring[c][_pos] = sample[c];
        _pos = (_pos + 1) % _window;
        _count++;
        if (_count < _window || _count % _hop != 0) return null;

        var result = new float[OutputCount];
        var o = 0;
        for (var c = 0; c < _channels; c++)
        {
            // Unroll the ring into chronological order.
            var src = _ring[c];
            var tail = _window - _pos;
            src.AsSpan(_pos, tail).CopyTo(_segment);
            src.AsSpan(0, _pos).CopyTo(_segment.AsSpan(tail));

            if (!_spectrum.Compute(_segment, _psd))
            {
                for (var k = 0; k < Bands.All.Length * 2; k++) result[o++] = float.NaN;
                continue;
            }
            var total = _spectrum.BandPower(_psd, Bands.TotalLo, Bands.TotalHi);
            var abs = Bands.All.Select(b => _spectrum.BandPower(_psd, b.Lo, b.Hi)).ToArray();
            foreach (var a in abs) result[o++] = (float)a;
            foreach (var a in abs) result[o++] = total > 0 ? (float)(a / total) : float.NaN;
        }
        return result;
    }
}

/// <summary>
/// The quality stream, once per second: per-channel impedance (kOhm, from the device; NaN if unknown) and a
/// 0-1 signal-quality index from the last 1 s of raw EEG.
/// The index is a documented heuristic, not a validated measure: it multiplies factors for flatline,
/// saturation, line-noise share, very large amplitude, gap-filled samples and impedance above 20 kOhm.
/// </summary>
public sealed class QualityExtractor
{
    public const double FlatlineStdUv = 0.5;
    /// <summary>Patch input range is +/-300 mV and the EEG is DC-coupled, so only values near the rail count as saturated.</summary>
    public const double SaturationUv = 295_000;
    public const double LargeArtifactStdUv = 150;
    public const double GoodImpedanceKohm = 20;
    public const double BadImpedanceKohm = 100;

    private readonly int _channels;
    private readonly int _n;
    private readonly float[][] _buf;
    private int _filled;
    private readonly SpectralWindow _spectrum;
    private readonly double _lineHz;
    private readonly double[] _psd;

    public QualityExtractor(int channels, double fs, double lineHz)
    {
        _channels = channels;
        _n = (int)Math.Round(fs);
        _buf = Enumerable.Range(0, channels).Select(_ => new float[_n]).ToArray();
        _lineHz = lineHz;
        _spectrum = new SpectralWindow(_n, fs, lineHz + 3);
        _psd = new double[_spectrum.LastBin - _spectrum.FirstBin + 1];
    }

    public int OutputCount => _channels * 2;

    /// <summary>Add one interleaved sample. Returns [imp..., quality...] each full second, else null.</summary>
    public float[]? Add(ReadOnlySpan<float> sample, IReadOnlyList<double?> impedanceKohm)
    {
        for (var c = 0; c < _channels; c++) _buf[c][_filled] = sample[c];
        _filled++;
        if (_filled < _n) return null;
        _filled = 0;

        var result = new float[OutputCount];
        for (var c = 0; c < _channels; c++)
        {
            var imp = c < impedanceKohm.Count ? impedanceKohm[c] : null;
            result[c] = imp.HasValue ? (float)imp.Value : float.NaN;
            result[_channels + c] = (float)Score(_buf[c], imp);
        }
        return result;
    }

    private double Score(float[] x, double? impedance)
    {
        var nan = x.Count(float.IsNaN);
        if (nan > x.Length / 2) return 0;
        var valid = x.Where(v => !float.IsNaN(v)).Select(v => (double)v).ToArray();
        var mean = valid.Average();
        var std = Math.Sqrt(valid.Sum(v => (v - mean) * (v - mean)) / valid.Length);
        if (std < FlatlineStdUv) return 0;

        var q = 1.0;
        q *= 1.0 - (double)nan / x.Length;
        var sat = valid.Count(v => Math.Abs(v) >= SaturationUv) / (double)valid.Length;
        q *= Math.Clamp(1 - 10 * sat, 0, 1);
        if (std > LargeArtifactStdUv) q *= 0.5;

        if (nan == 0 && _spectrum.Compute(x, _psd))
        {
            var line = _spectrum.BandPower(_psd, _lineHz - 2, _lineHz + 2.01);
            var eeg = _spectrum.BandPower(_psd, Bands.TotalLo, Bands.TotalHi);
            var ratio = line / Math.Max(1e-9, line + eeg);
            if (ratio > 0.3) q *= Math.Clamp(1 - (ratio - 0.3) / 0.5, 0, 1);
        }

        if (impedance.HasValue)
        {
            var imp = impedance.Value;
            if (imp > GoodImpedanceKohm)
                q *= Math.Clamp(1 - (imp - GoodImpedanceKohm) / (BadImpedanceKohm - GoodImpedanceKohm), 0, 1);
        }
        return Math.Clamp(q, 0, 1);
    }
}