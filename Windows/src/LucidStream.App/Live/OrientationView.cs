using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace LucidStream.App.Live;

/// <summary>
/// Head orientation from the patch accelerometer, drawn like a mirror in front of the wearer: the patch as a plate
/// with its axes, plus "nod" (forward/back) and "tilt" (left/right) in degrees. At the upright pose the axes are drawn
/// with Y (green) straight up, X (red) horizontal and Z (blue) pointing out of the forehead toward the viewer; nodding
/// and tilting turn the drawing the same way the head turns, as seen in a mirror.
///
/// Everything is shown relative to an upright reference: which patch direction points up, and which points forward,
/// when the wearer sits straight. The default comes from wearing patch PEEG-00642 upright (14 Nov 2026): Y read -1 g,
/// so -Y is up, and the patch lies flat on the forehead, so Z is taken as forward. <see cref="SetUprightFromCurrent"/>
/// replaces it with the current pose. Gravity fixes "up" exactly; "forward" is the patch Z axis with its vertical part
/// removed. Turning about the vertical cannot be measured (no gyroscope or compass).
/// The window passes in an already smoothed gravity vector (about 0.5 s), in g, in patch axes.
/// </summary>
public sealed class OrientationView : FrameworkElement
{
    private static readonly Brush Background = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush PlateFill = Frozen(new SolidColorBrush(Color.FromArgb(60, 0x18, 0x98, 0xA7)));
    private static readonly Pen PlatePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(190, 0x38, 0xC6, 0xD6)), 1.2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen VerticalPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen GroundPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(184, 255, 255, 255)));
    private static readonly Brush FaintBrush = Frozen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)));
    private static readonly Brush[] AxisBrushes =
    {
        Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55))),
        Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64))),
        Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0x8D, 0xFF)))
    };
    private static readonly Pen[] AxisPens = AxisBrushes.Select(b => Frozen(new Pen(b, 2.2) { StartLineCap = PenLineCap.Round })).ToArray();
    private static readonly string[] AxisNames = { "X", "Y", "Z" };
    private static readonly Typeface Face = new("Segoe UI");

    // Camera in front of the wearer, looking back at them and down 12 degrees, mirrored so the wearer's right is on the
    // right of the screen. Forward (out of the forehead) points toward the viewer.
    private static readonly double ViewPitch = 12 * Math.PI / 180;

    // How the patch axes are drawn in the head frame (x right, y forward, z up) at the upright pose:
    // X horizontal (to the wearer's left, so X, Y, Z stay right-handed), Y up, Z forward out of the forehead.
    private static readonly Vec DrawX = new(-1, 0, 0), DrawY = new(0, 0, 1), DrawZ = new(0, 1, 0);
    private static readonly Brush FlashBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x38, 0xC6, 0xD6)));

    // Upright reference, in patch axes: the patch directions that point up, forward and to the wearer's right.
    private static readonly Vec DefaultUp = new(0, -1, 0), DefaultForward = new(0, 0, 1);
    private Vec _refUp = DefaultUp, _refForward = DefaultForward, _refRight = DefaultForward.Cross(DefaultUp);
    private bool _referenceSet;
    private string _flash = "";
    private DateTime _flashUntil;

    private double _x, _y, _z;
    private bool _has;

    /// <summary>Smoothed accelerometer reading in g (patch axes).</summary>
    public void SetGravity(double x, double y, double z)
    {
        _x = x;
        _y = y;
        _z = z;
        _has = true;
        InvalidateVisual();
    }

    public void Clear()
    {
        _has = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Take the current pose as upright. Returns the gravity reading used (patch axes, g), or null when there is no
    /// reading yet. Forward stays the patch Z axis, levelled; if Z is nearly vertical the previous forward is kept.
    /// </summary>
    public (double X, double Y, double Z)? SetUprightFromCurrent()
    {
        var mag = Math.Sqrt(_x * _x + _y * _y + _z * _z);
        if (!_has || double.IsNaN(mag) || mag < 0.05) return null;
        var up = new Vec(_x / mag, _y / mag, _z / mag);
        var forward = Level(new Vec(0, 0, 1), up) ?? Level(_refForward, up) ?? Level(new Vec(1, 0, 0), up)!.Value;
        _refUp = up;
        _refForward = forward;
        _refRight = forward.Cross(up);
        _referenceSet = true;
        Flash("Upright set");
        return (_x, _y, _z);
    }

    /// <summary>Back to the default reference (Y down, Z out of the forehead).</summary>
    public void ResetReference()
    {
        _refUp = DefaultUp;
        _refForward = DefaultForward;
        _refRight = DefaultForward.Cross(DefaultUp);
        _referenceSet = false;
        Flash("Default upright");
    }

    /// <summary>Show a short confirmation in the view for 1.5 s (the view redraws with every new reading).</summary>
    private void Flash(string text)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(1.5);
        InvalidateVisual();
    }

    /// <summary><paramref name="v"/> with its component along <paramref name="up"/> removed, normalized; null if nearly parallel.</summary>
    private static Vec? Level(Vec v, Vec up)
    {
        var d = v.Dot(up);
        var h = new Vec(v.X - d * up.X, v.Y - d * up.Y, v.Z - d * up.Z);
        var n = h.Length;
        return n < 0.2 ? null : h.Scale(1 / n);
    }

    /// <summary>A patch-axis vector in the head frame (x right, y forward, z up) of the reference pose.</summary>
    private Vec ToHead(Vec p) => new(p.Dot(_refRight), p.Dot(_refForward), p.Dot(_refUp));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var mag = Math.Sqrt(_x * _x + _y * _y + _z * _z);
        if (!_has || double.IsNaN(mag) || mag < 0.05)
        {
            dc.DrawText(Text("No accelerometer data yet.", 12, LabelBrush, dip), new Point(10, 8));
            return;
        }

        // Measured "up" in the head frame; upright gives (0, 0, 1). r turns the head frame into the world.
        var up = ToHead(new Vec(_x / mag, _y / mag, _z / mag));
        var r = Rotation.ToVertical(up);
        Vec World(Vec head) => r.Apply(head);

        var cx = w / 2;
        var cy = h * 0.52;
        var s = Math.Min(w, h) * 0.28;
        Point P(Vec v)
        {
            var (sx, sy, _) = Project(v);
            return new Point(cx + sx * s, cy - sy * s);
        }

        // World reference: a ring on the ground plane and the vertical.
        var ring = new StreamGeometry();
        using (var g = ring.Open())
        {
            for (var i = 0; i <= 48; i++)
            {
                var a = i * 2 * Math.PI / 48;
                var p = P(new Vec(1.25 * Math.Cos(a), 1.25 * Math.Sin(a), -0.9));
                if (i == 0) g.BeginFigure(p, false, true);
                else g.LineTo(p, true, false);
            }
        }
        ring.Freeze();
        dc.DrawGeometry(null, GroundPen, ring);
        dc.DrawLine(VerticalPen, P(new Vec(0, 0, -0.9)), P(new Vec(0, 0, 1.5)));

        // The patch as a plate in its X-Y plane: upright, it stands vertical on the forehead, facing the viewer.
        var corners = new[] { (1.0, 0.62), (-1.0, 0.62), (-1.0, -0.62), (1.0, -0.62) }
            .Select(c => P(World(new Vec(DrawX.X * c.Item1 + DrawY.X * c.Item2, DrawX.Y * c.Item1 + DrawY.Y * c.Item2, DrawX.Z * c.Item1 + DrawY.Z * c.Item2))))
            .ToArray();
        var plate = new StreamGeometry();
        using (var g = plate.Open())
        {
            g.BeginFigure(corners[0], true, true);
            for (var i = 1; i < corners.Length; i++) g.LineTo(corners[i], true, false);
        }
        plate.Freeze();
        dc.DrawGeometry(PlateFill, PlatePen, plate);

        // Patch axes, farthest first so nearer arrows are drawn on top.
        var axes = new[] { World(DrawX), World(DrawY), World(DrawZ) };
        var origin = P(new Vec(0, 0, 0));
        foreach (var i in Enumerable.Range(0, 3).OrderByDescending(j => Project(axes[j]).Depth))
        {
            var tip = P(axes[i].Scale(1.3));
            var full = 1.3 * s;
            var shown = (tip - origin).Length;
            if (Project(axes[i]).Depth < 0 && shown < 0.45 * full)
            {
                // Pointing mostly at the viewer: the "out of the screen" symbol, a ring with a dot.
                dc.DrawEllipse(null, new Pen(AxisBrushes[i], 1.6), origin, 6.5, 6.5);
                dc.DrawEllipse(AxisBrushes[i], null, origin, 2.2, 2.2);
            }
            Arrow(dc, origin, tip, AxisPens[i], AxisBrushes[i]);
            var label = Text(AxisNames[i], 11, AxisBrushes[i], dip);
            var lp = shown < 0.45 * full ? new Point(origin.X + 9, origin.Y + 3) : P(axes[i].Scale(1.55));
            dc.DrawText(label, new Point(lp.X - label.Width / 2, lp.Y - label.Height / 2));
        }

        // Nod: forward positive (the forward axis tips down). Tilt: right positive (the right axis tips down).
        var nod = Math.Atan2(-up.Y, Math.Sqrt(up.X * up.X + up.Z * up.Z)) * 180 / Math.PI;
        var tilt = Math.Atan2(-up.X, up.Z) * 180 / Math.PI;
        dc.DrawText(Text($"Nod {Math.Abs(nod):0}\u00B0 {(nod >= 0 ? "forward" : "back")}", 11, LabelBrush, dip), new Point(8, 6));
        dc.DrawText(Text($"Tilt {Math.Abs(tilt):0}\u00B0 {(tilt >= 0 ? "right" : "left")}", 11, LabelBrush, dip), new Point(8, 21));
        var refText = Text(_referenceSet ? "upright set" : "default upright", 10, FaintBrush, dip);
        dc.DrawText(refText, new Point(w - refText.Width - 8, 6));
        var g1 = Text($"{mag:0.00} g", 11, LabelBrush, dip);
        dc.DrawText(g1, new Point(w - g1.Width - 8, h - g1.Height - 6));
        if (DateTime.UtcNow < _flashUntil)
        {
            var f = Text(_flash, 12, FlashBrush, dip);
            dc.DrawText(f, new Point((w - f.Width) / 2, h - f.Height - 6));
        }
    }

    private static void Arrow(DrawingContext dc, Point from, Point to, Pen pen, Brush fill)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 5)
        {
            // Pointing at or away from the viewer.
            dc.DrawEllipse(fill, null, to, 3.5, 3.5);
            return;
        }
        var ux = dx / len;
        var uy = dy / len;
        const double head = 7, half = 3.6;
        var b = new Point(to.X - ux * head, to.Y - uy * head);
        dc.DrawLine(pen, from, b);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(to, true, true);
            g.LineTo(new Point(b.X - uy * half, b.Y + ux * half), true, false);
            g.LineTo(new Point(b.X + uy * half, b.Y - ux * half), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(fill, null, tri);
    }

    /// <summary>
    /// World (x wearer's right, y forward, z up) to screen, as a mirror in front of the wearer seen from slightly above:
    /// X right, Y up, Depth larger = farther from the viewer (forward points toward the viewer).
    /// </summary>
    private static (double X, double Y, double Depth) Project(Vec v) =>
        (v.X, v.Z * Math.Cos(ViewPitch) - v.Y * Math.Sin(ViewPitch), -v.Y * Math.Cos(ViewPitch) - v.Z * Math.Sin(ViewPitch));

    private readonly record struct Vec(double X, double Y, double Z)
    {
        public Vec Scale(double k) => new(X * k, Y * k, Z * k);
        public double Dot(Vec o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vec Cross(Vec o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>3x3 rotation, row-major.</summary>
    private readonly struct Rotation
    {
        private readonly double[] _m;
        private Rotation(double[] m) => _m = m;

        public Vec Apply(Vec v) => new(
            _m[0] * v.X + _m[1] * v.Y + _m[2] * v.Z,
            _m[3] * v.X + _m[4] * v.Y + _m[5] * v.Z,
            _m[6] * v.X + _m[7] * v.Y + _m[8] * v.Z);

        /// <summary>The smallest rotation taking unit vector <paramref name="a"/> to (0, 0, 1) (Rodrigues).</summary>
        public static Rotation ToVertical(Vec a)
        {
            var c = a.Z;
            if (c < -0.9999) return new Rotation(new double[] { 1, 0, 0, 0, -1, 0, 0, 0, -1 }); // upside down: half turn about X
            // v = a x (0,0,1) = (a.Y, -a.X, 0); R = I + K + K^2 / (1 + c), K = cross-product matrix of v.
            double vx = a.Y, vy = -a.X;
            var k = 1 / (1 + c);
            return new Rotation(new[]
            {
                1 - vy * vy * k, vx * vy * k, vy,
                vx * vy * k, 1 - vx * vx * k, -vx,
                -vy, vx, 1 - (vx * vx + vy * vy) * k
            });
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double dip) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, dip);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}