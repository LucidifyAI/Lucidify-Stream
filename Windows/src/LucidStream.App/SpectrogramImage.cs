using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LucidStream.App.Live;

namespace LucidStream.App;

/// <summary>
/// Spectrogram drawn like the Lucidify Cloud viewer: black plot, jet colors (blue = low power through cyan, green,
/// yellow to red = high), frequency labels on the left, elapsed time along the bottom. It builds from the left when
/// streaming starts and scrolls once the chosen time window is full.
/// </summary>
public sealed class SpectrogramImage : FrameworkElement
{
    private const double PlotLeft = 46, PlotRight = 10, PlotTop = 8, PlotBottom = 24;
    private static readonly Brush Background = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Pen BorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(77, 255, 255, 255)), 1));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1));
    private static readonly Brush AxisBrush = Frozen(new SolidColorBrush(Color.FromArgb(184, 255, 255, 255)));
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly int[] Jet = BuildJet();

    private WriteableBitmap? _bitmap;
    private double _startSeconds, _spanSeconds = 60;

    public SpectrogramImage()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public SpectrogramComputer? Computer { get; set; }
    public int Channel { get; set; }
    public string Label { get; set; } = "";
    public Brush LabelBrush { get; set; } = AxisBrush;
    public double WindowSeconds { get; set; } = 600;
    public double MaxHz { get; set; } = 30;

    /// <summary>Shifts the color range: positive values make the picture brighter (dB).</summary>
    public double BrightnessDb { get; set; }

    /// <summary>Above 1 lifts quiet areas, below 1 deepens them.</summary>
    public double Gamma { get; set; } = 1.3;

    /// <summary>Rebuild the image from the computed columns (after new data or a settings change).</summary>
    public void Refresh()
    {
        _bitmap = Build();
        InvalidateVisual();
    }

    private WriteableBitmap? Build()
    {
        var comp = Computer;
        if (comp == null) return null;
        var cols = comp.Columns(Channel);
        var hop = comp.Params.HopSamples;
        var slots = Math.Max(1, (int)Math.Ceiling(WindowSeconds * LiveSignal.Rate / hop));
        _spanSeconds = slots * hop / LiveSignal.Rate;
        if (cols.Count == 0 || comp.Origin < 0)
        {
            _startSeconds = 0;
            return null;
        }

        // Builds from the left until the window is full, then the newest column stays at the right edge.
        var latest = cols[^1].Center;
        var start = comp.Origin;
        if (latest - start >= (long)slots * hop) start = latest - (long)(slots - 1) * hop;
        _startSeconds = Math.Max(0, start / LiveSignal.Rate);

        var rows = Math.Max(2, Math.Min(cols[^1].Db.Length, (int)Math.Floor(MaxHz / comp.BinHz) + 1));
        var visible = new List<(int X, float[] Db)>();
        for (var i = cols.Count - 1; i >= 0; i--)
        {
            var x = (int)Math.Round((cols[i].Center - start) / (double)hop);
            if (x < 0) break;
            if (x < slots) visible.Add((x, cols[i].Db));
        }

        // Color range from what is on screen (5th to 99.7th percentile), shifted by the brightness setting.
        var sample = new List<float>(8192);
        var step = Math.Max(1, visible.Count * rows / 8000);
        var n = 0;
        foreach (var (_, db) in visible)
            for (var r = 1; r < rows; r++)
                if (n++ % step == 0 && !float.IsNaN(db[r])) sample.Add(db[r]);
        if (sample.Count < 10) return null;
        sample.Sort();
        var lo = sample[(int)(sample.Count * 0.05)] - BrightnessDb;
        var hi = sample[Math.Min(sample.Count - 1, (int)(sample.Count * 0.997))] - BrightnessDb;
        if (hi - lo < 6) hi = lo + 6;
        var invGamma = 1 / Math.Max(0.1, Gamma);

        var pixels = new int[slots * rows];
        var black = unchecked((int)0xFF000000);
        Array.Fill(pixels, black);
        foreach (var (x, db) in visible)
        {
            for (var r = 0; r < rows; r++)
            {
                var v = db[r];
                if (float.IsNaN(v)) continue;
                var t = Math.Clamp((v - lo) / (hi - lo), 0, 1);
                t = Math.Pow(t, invGamma);
                pixels[(rows - 1 - r) * slots + x] = Jet[(int)(t * 255)];
            }
        }
        var bmp = new WriteableBitmap(slots, rows, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, slots, rows), pixels, slots * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 80 || h < 50) return;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var plot = new Rect(PlotLeft, PlotTop, Math.Max(1, w - PlotLeft - PlotRight), Math.Max(1, h - PlotTop - PlotBottom));
        if (_bitmap != null) dc.DrawImage(_bitmap, plot);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i <= 4; i++)
        {
            var hz = MaxHz * (1 - i / 4.0);
            var y = plot.Top + i * plot.Height / 4;
            if (i > 0 && i < 4) dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text($"{hz.ToString(hz < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} Hz", 11, AxisBrush, dip);
            dc.DrawText(label, new Point(4, Math.Clamp(y - label.Height / 2, 0, h - label.Height)));
        }
        for (var i = 0; i <= 5; i++)
        {
            var t = _startSeconds + _spanSeconds * i / 5;
            var x = plot.Left + plot.Width * i / 5;
            var label = Text(Clock(t), 11, AxisBrush, dip);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left, w - label.Width - 2), plot.Bottom + 5));
        }
        dc.DrawRectangle(null, BorderPen, new Rect(plot.Left + 0.5, plot.Top + 0.5, plot.Width - 1, plot.Height - 1));
        if (Label.Length > 0) dc.DrawText(Text(Label, 12, LabelBrush, dip), new Point(plot.Left + 8, plot.Top + 5));
        if (_bitmap == null)
            dc.DrawText(Text("Builds up here once streaming starts.", 12, AxisBrush, dip), new Point(plot.Left + 8, plot.Top + 24));
    }

    private static string Clock(double seconds)
    {
        var s = (int)Math.Max(0, Math.Round(seconds));
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private static FormattedText Text(string s, double size, Brush brush, double dip) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, dip);

    /// <summary>The classic jet map: dark blue, blue, cyan, green, yellow, red, dark red.</summary>
    private static int[] BuildJet()
    {
        var p = new int[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            double R(double c) => Math.Clamp(1.5 - Math.Abs(4 * t - c), 0, 1);
            var r = R(3);
            var g = R(2);
            var b = R(1);
            p[i] = unchecked((int)0xFF000000) | ((int)(r * 255) << 16) | ((int)(g * 255) << 8) | (int)(b * 255);
        }
        return p;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}