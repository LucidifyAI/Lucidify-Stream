namespace LucidStream.Core.Dsp;

/// <summary>Second-order IIR section (RBJ audio-EQ cookbook), transposed direct form II, double precision.</summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public static Biquad LowPass(double fs, double f0, double q)
    {
        var w = 2 * Math.PI * f0 / fs; var c = Math.Cos(w); var alpha = Math.Sin(w) / (2 * q);
        return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
    }

    public static Biquad HighPass(double fs, double f0, double q)
    {
        var w = 2 * Math.PI * f0 / fs; var c = Math.Cos(w); var alpha = Math.Sin(w) / (2 * q);
        return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
    }

    public static Biquad Notch(double fs, double f0, double q)
    {
        var w = 2 * Math.PI * f0 / fs; var c = Math.Cos(w); var alpha = Math.Sin(w) / (2 * q);
        return new Biquad(1, -2 * c, 1, 1 + alpha, -2 * c, 1 - alpha);
    }

    public double Process(double x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() { _z1 = 0; _z2 = 0; }
}