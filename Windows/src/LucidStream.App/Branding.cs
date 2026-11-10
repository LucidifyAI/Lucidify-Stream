using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LucidStream.App;

/// <summary>
/// Lucidify look outside the XAML: the mark as window and tray icon, and a dark Windows title bar.
/// The mark's geometry matches the LogoMark resource in App.xaml (traced from the brand PNG, 382 x 478 units).
/// </summary>
internal static class Branding
{
    private static readonly (int X, int Y)[] Nodes =
    {
        (190, 15), (94, 58), (287, 58), (240, 116), (141, 117), (153, 196), (228, 196), (16, 229),
        (366, 229), (128, 307), (253, 307), (15, 360), (365, 360), (284, 444), (97, 445), (191, 462)
    };

    private static readonly (int A, int B)[] Edges =
    {
        (0, 1), (0, 2), (1, 4), (2, 3), (4, 3), (1, 7), (2, 8), (4, 5), (3, 6), (5, 6), (5, 7), (6, 8), (5, 9),
        (6, 10), (9, 10), (9, 11), (10, 12), (7, 11), (8, 12), (11, 14), (12, 13), (14, 15), (13, 15), (9, 15), (10, 15)
    };

    private static readonly System.Drawing.Color Teal = System.Drawing.Color.FromArgb(0x18, 0x98, 0xA7);

    /// <summary>The mark drawn at the given pixel size. Lines are thickened at small sizes so the icon stays legible.</summary>
    public static Bitmap DrawMark(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        const float w = 382f, h = 478f;
        var scale = (size - 2) / h;
        var ox = (size - w * scale) / 2f;
        var oy = 1f;
        var penWidth = Math.Max(14f * scale, size <= 32 ? 1.8f : 1.2f);
        var radius = Math.Max(16f * scale, size <= 32 ? 1.9f : 1.4f);
        using var pen = new System.Drawing.Pen(Teal, penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(Teal);
        PointF P(int i) => new(ox + Nodes[i].X * scale, oy + Nodes[i].Y * scale);
        foreach (var (a, b) in Edges) g.DrawLine(pen, P(a), P(b));
        for (var i = 0; i < Nodes.Length; i++)
        {
            var p = P(i);
            g.FillEllipse(brush, p.X - radius, p.Y - radius, radius * 2, radius * 2);
        }
        return bmp;
    }

    /// <summary>Tray icon. The handle lives for the app's lifetime.</summary>
    public static Icon CreateTrayIcon()
    {
        try
        {
            using var bmp = DrawMark(32);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>Window and taskbar icon.</summary>
    public static BitmapSource? CreateWindowIcon()
    {
        try
        {
            using var bmp = DrawMark(64);
            var hbitmap = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hbitmap); }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Dark title bar (Windows 10 20H1 and later) and, on Windows 11, a caption color matching the header.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
            var caption = 0x00010101;  // COLORREF 0x00BBGGRR: #010101, the header color
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            var text = 0x00FBF8F8;     // #F8F8FB
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (Exception) { /* cosmetic only */ }
    }

    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}