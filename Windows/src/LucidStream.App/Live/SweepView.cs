using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LucidStream.App.Live;

/// <summary>
/// Oscilloscope-style live waveform, drawn the way the Lucidify Cloud viewer draws it: a green cursor sweeps left to
/// right, new signal is written behind it in white, and the previous sweep stays visible (dimmed) ahead of it. One
/// row per channel, each pixel showing the min-max of its samples, with the scale printed on each row.
/// </summary>
public sealed class SweepView : FrameworkElement
{
    private static readonly Brush Background = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1));
    private static readonly Pen TracePen = Frozen(new Pen(Brushes.White, 1.15) { LineJoin = PenLineJoin.Round });
    private static readonly Pen PreviousPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(97, 255, 255, 255)), 1.15) { LineJoin = PenLineJoin.Round });
    private static readonly Pen CursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x4C)), 2));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(184, 255, 255, 255)));
    private static readonly Typeface Face = new("Segoe UI");

    private readonly string[] _labels = { "AFz", "Fp2" };
    private readonly double[] _scale = { double.NaN, double.NaN };
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrame;
    private double _head = -1;
    private float[] _buf = Array.Empty<float>();

    public SweepView()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public LiveSignal? Signal { get; set; }
    public bool Running { get; set; }
    public double WindowSeconds { get; set; } = 10;

    /// <summary>Fixed half-range in microvolts per row, or null for automatic.</summary>
    public double? FixedMicrovolts { get; set; } = null;

    public double Gain { get; set; } = 1;
    public WaveSignal Mode { get; set; } = WaveSignal.Display;

    public void ResetView()
    {
        _head = -1;
        _scale[0] = _scale[1] = double.NaN;
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (Running && IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var rows = _labels.Length;
        var rowH = h / rows;
        for (var r = 0; r <= rows; r++) dc.DrawLine(GridPen, new Point(0, r * rowH), new Point(w, r * rowH));
        for (var i = 0; i <= 10; i++) dc.DrawLine(GridPen, new Point(i * w / 10, 0), new Point(i * w / 10, h));

        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var signal = Signal;
        var newest = signal?.Head ?? 0;
        if (signal == null || newest <= 0)
        {
            dc.DrawText(Text("Start streaming to see the live signal.", 14, LabelBrush, dip), new Point(18, 20));
            return;
        }

        // Smooth cursor: advance with the clock between deliveries, never past the newest data plus a few frames.
        var now = _frameClock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.25, now - _lastFrame);
        _lastFrame = now;
        var target = newest + Math.Min(signal.SecondsSinceHead, 0.06) * LiveSignal.Rate;
        if (_head < 0 || Math.Abs(target - _head) > LiveSignal.Rate) _head = target;
        else if (Running) _head = Math.Min(target, Math.Max(_head + dt * LiveSignal.Rate, target - 0.2 * LiveSignal.Rate));

        var ws = Math.Max(1, (long)Math.Round(WindowSeconds * LiveSignal.Rate));
        var headSample = (long)_head;
        var cycleStart = headSample / ws * ws;
        var phase = headSample - cycleStart;
        var cursorX = phase / (double)ws * w;
        var pxW = Math.Max(1, (int)Math.Round(w));
        if (_buf.Length != 2 * ws) _buf = new float[2 * ws];

        for (var ch = 0; ch < rows; ch++)
        {
            // Previous sweep and current sweep: [cycleStart - ws, cycleStart + ws).
            signal.Read(Mode, ch, cycleStart - ws, _buf);
            if (Mode == WaveSignal.Raw) RemoveMean(_buf);
            var scale = Scale(ch, phase, ws);
            var cy = ch * rowH + rowH / 2;
            var k = rowH * 0.42 / scale;
            var current = new StreamGeometry();
            var previous = new StreamGeometry();
            using (var cur = current.Open())
            using (var prev = previous.Open())
            {
                int lastCur = -2, lastPrev = -2;
                for (var px = 0; px < pxW; px++)
                {
                    var p0 = (long)Math.Floor(px / (double)pxW * ws);
                    var p1 = Math.Max(p0 + 1, (long)Math.Floor((px + 1) / (double)pxW * ws));
                    var ahead = p0 > phase;
                    var offset = ahead ? 0 : ws; // index into _buf
                    float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
                    for (var s = p0; s < p1 && s < ws; s++)
                    {
                        if (!ahead && s > phase) break;
                        var v = _buf[offset + s];
                        if (float.IsNaN(v)) continue;
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                    if (float.IsPositiveInfinity(mn)) continue;
                    var y1 = cy - mx * k;
                    var y2 = cy - mn * k;
                    if (ahead) Segment(prev, ref lastPrev, px, y1, y2);
                    else Segment(cur, ref lastCur, px, y1, y2);
                }
            }
            current.Freeze();
            previous.Freeze();
            dc.PushClip(new RectangleGeometry(new Rect(0, ch * rowH, w, rowH)));
            dc.DrawGeometry(null, PreviousPen, previous);
            dc.DrawGeometry(null, TracePen, current);
            dc.Pop();
            var label = $"{_labels[ch]}  \u00B1{scale.ToString(scale < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} \u00B5V";
            dc.DrawText(Text(label, 12, LabelBrush, dip), new Point(9, ch * rowH + 5));
        }

        dc.DrawLine(CursorPen, new Point(cursorX, 0), new Point(cursorX, h));
    }

    /// <summary>Raw EEG is DC-coupled (offsets of many millivolts); center it so the signal is visible.</summary>
    private static void RemoveMean(float[] buf)
    {
        double sum = 0;
        var n = 0;
        foreach (var v in buf)
        {
            if (float.IsNaN(v)) continue;
            sum += v;
            n++;
        }
        if (n == 0) return;
        var mean = (float)(sum / n);
        for (var i = 0; i < buf.Length; i++) buf[i] -= mean;
    }

    private static void Segment(StreamGeometryContext g, ref int last, int px, double y1, double y2)
    {
        if (last == px - 1) g.LineTo(new Point(px, y1), true, false);
        else
        {
            g.BeginFigure(new Point(px, (y1 + y2) / 2), false, false);
            g.LineTo(new Point(px, y1), true, false);
        }
        if (Math.Abs(y2 - y1) > 0.01) g.LineTo(new Point(px, y2), true, false);
        last = px;
    }

    /// <summary>Row half-range in microvolts: fixed, or the 99th percentile of what is on screen (eased), like the Cloud viewer.</summary>
    private double Scale(int ch, long phase, long ws)
    {
        double baseUv;
        if (FixedMicrovolts is double fixedUv) baseUv = fixedUv;
        else
        {
            var vals = new List<float>(1600);
            var stride = Math.Max(1, (int)(2 * ws / 1500));
            for (var i = 0; i < _buf.Length; i += stride)
            {
                var s = i < ws ? i : i - ws;
                if (i < ws && s <= phase) continue; // previous sweep only ahead of the cursor
                if (i >= ws && s > phase) continue;  // current sweep only behind it
                var v = _buf[i];
                if (!float.IsNaN(v)) vals.Add(Math.Abs(v));
            }
            var target = 1.0;
            if (vals.Count > 0)
            {
                vals.Sort();
                target = Math.Max(1, vals[Math.Min(vals.Count - 1, (int)(vals.Count * 0.99))] * 1.25);
            }
            _scale[ch] = double.IsNaN(_scale[ch]) ? target : _scale[ch] + (target - _scale[ch]) * 0.12;
            baseUv = _scale[ch];
        }
        return baseUv / Math.Max(0.01, Gain);
    }

    private static FormattedText Text(string s, double size, Brush brush, double dip) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, dip);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}