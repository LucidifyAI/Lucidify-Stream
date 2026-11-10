namespace LucidStream.Core.Dsp;

/// <summary>
/// One-sided power spectral density of a Hann-windowed, mean-removed segment, evaluated only at the bins
/// that are needed (a direct DFT with precomputed tables; cheap for 2 channels at 500 Hz).
/// PSD units: uV^2/Hz. Bin spacing df = fs / N.
/// </summary>
public sealed class SpectralWindow
{
    private readonly int _n;
    private readonly double _fs;
    private readonly double[] _window;
    private readonly double _windowPower;
    private readonly int _firstBin, _lastBin;
    private readonly double[][] _cos, _sin;
    private readonly double[] _scratch;

    public double BinHz => _fs / _n;
    public int FirstBin => _firstBin;
    public int LastBin => _lastBin;

    public SpectralWindow(int n, double fs, double maxHz)
    {
        _n = n;
        _fs = fs;
        _window = new double[n];
        for (var i = 0; i < n; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        _windowPower = _window.Sum(w => w * w);
        _firstBin = 1;
        _lastBin = Math.Min(n / 2, (int)Math.Ceiling(maxHz / BinHz));
        var bins = _lastBin - _firstBin + 1;
        _cos = new double[bins][];
        _sin = new double[bins][];
        for (var b = 0; b < bins; b++)
        {
            var k = b + _firstBin;
            _cos[b] = new double[n];
            _sin[b] = new double[n];
            for (var i = 0; i < n; i++)
            {
                var ph = 2 * Math.PI * k * i / n;
                _cos[b][i] = Math.Cos(ph) * _window[i];
                _sin[b][i] = Math.Sin(ph) * _window[i];
            }
        }
        _scratch = new double[n];
    }

    /// <summary>Compute PSD for bins FirstBin..LastBin into psd (indexed by bin - FirstBin). Returns false if the segment has NaN.</summary>
    public bool Compute(ReadOnlySpan<float> segment, Span<double> psd)
    {
        if (segment.Length != _n) throw new ArgumentException("segment length");
        double mean = 0;
        for (var i = 0; i < _n; i++)
        {
            var v = segment[i];
            if (float.IsNaN(v)) return false;
            mean += v;
        }
        mean /= _n;
        for (var i = 0; i < _n; i++) _scratch[i] = segment[i] - mean;

        var scale = 2.0 / (_fs * _windowPower);
        for (var b = 0; b < _cos.Length; b++)
        {
            double re = 0, im = 0;
            var cb = _cos[b]; var sb = _sin[b];
            for (var i = 0; i < _n; i++)
            {
                re += _scratch[i] * cb[i];
                im -= _scratch[i] * sb[i];
            }
            psd[b] = (re * re + im * im) * scale;
        }
        return true;
    }

    /// <summary>Integrate PSD over [lo, hi) Hz.</summary>
    public double BandPower(ReadOnlySpan<double> psd, double lo, double hi)
    {
        double sum = 0;
        for (var b = 0; b < psd.Length; b++)
        {
            var f = (b + _firstBin) * BinHz;
            if (f >= lo && f < hi) sum += psd[b];
        }
        return sum * BinHz;
    }
}