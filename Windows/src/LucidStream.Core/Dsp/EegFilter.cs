namespace LucidStream.Core.Dsp;

/// <summary>
/// The eeg.filtered stream: causal 0.5-40 Hz band-pass (2nd-order Butterworth high-pass,
/// 4th-order Butterworth low-pass) plus a line-noise notch (60 Hz default, 50 Hz selectable, Q = 30).
/// Runs per channel, sample by sample. A NaN input (gap fill) outputs NaN and resets the filter state.
/// </summary>
public sealed class EegFilter
{
    public const double HighPassHz = 0.5;
    public const double LowPassHz = 40;
    public const double NotchQ = 30;
    public const int HighPassOrder = 2;
    public const int LowPassOrder = 4;

    private readonly Biquad[][] _chains;
    private readonly bool[] _primed;

    public double NotchHz { get; }

    public EegFilter(int channels, double fs, double notchHz)
    {
        NotchHz = notchHz;
        _chains = new Biquad[channels][];
        _primed = new bool[channels];
        for (var c = 0; c < channels; c++)
        {
            _chains[c] = new[]
            {
                Biquad.HighPass(fs, HighPassHz, Math.Sqrt(0.5)),
                Biquad.LowPass(fs, LowPassHz, 0.54119610),
                Biquad.LowPass(fs, LowPassHz, 1.30656296),
                Biquad.Notch(fs, notchHz, NotchQ),
            };
        }
    }

    /// <summary>Filter interleaved samples in place into <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int channels)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var c = i % channels;
            var x = input[i];
            if (float.IsNaN(x))
            {
                foreach (var b in _chains[c]) b.Reset();
                _primed[c] = false;
                output[i] = float.NaN;
                continue;
            }
            if (!_primed[c])
            {
                // Start from the first value to avoid a large DC step response at session start or after a gap.
                Prime(c, x);
                _primed[c] = true;
            }
            double y = x;
            foreach (var b in _chains[c]) y = b.Process(y);
            output[i] = (float)y;
        }
    }

    private void Prime(int c, double x)
    {
        // Settle the high-pass on the first value: run it on a constant input so its output decays to ~0.
        for (var k = 0; k < 3000; k++)
        {
            double y = x;
            foreach (var b in _chains[c]) y = b.Process(y);
        }
    }

    public string Describe() => $"high-pass {HighPassHz} Hz IIR order {HighPassOrder}; low-pass {LowPassHz} Hz IIR order {LowPassOrder}; notch {NotchHz} Hz Q {NotchQ}";
}