using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LucidStream.App.Ble;
using LucidStream.App.ViewModels;
using LucidStream.Core.Common;
using LucidStream.Core.Engine;

namespace LucidStream.App;

public partial class MainWindow : Window
{
    private const double LaneMicrovolts = 60;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings, AppContext.BaseDirectory, $"Lucid-Stream {typeof(MainWindow).Assembly.GetName().Version} (Windows)",
            s => new PatchBleSource(s.Ble));
        _vm.TraceUpdated += DrawTrace;
        _vm.OpenUrlRequested += OpenUrl;
        DataContext = _vm;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => _vm.Tick();
        _timer.Start();

        _tray = CreateTrayIcon();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ tray: keep streaming with the window closed

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Lucid-Stream", null, (_, _) => ShowFromTray());
        menu.Items.Add("Stop streaming and exit", null, async (_, _) => await ExitAsync());
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Lucid-Stream",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowFromTray();
        return icon;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_vm.IsStreaming)
        {
            // Keep streaming in the background; the tray icon brings the window back.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _tray.ShowBalloonTip(4000, "Lucid-Stream is still streaming", "Use the tray icon to open it or to stop streaming and exit.", System.Windows.Forms.ToolTipIcon.Info);
                _trayHintShown = true;
            }
            return;
        }
        e.Cancel = true;
        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        try { await _vm.DisposeAsync(); }
        catch (Exception ex) { Log.Error($"Shutdown: {ex.Message}"); }
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ UI handlers

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open the browser: {ex.Message}"); }
    }

    private void BrowseReplay_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a Lucid-Stream session folder",
            InitialDirectory = _vm.Settings.RecordFolder
        };
        if (dialog.ShowDialog(this) == true) _vm.ReplayFolder = dialog.FolderName;
    }

    private void MarkerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SendMarkerCommand.CanExecute(null)) _vm.SendMarkerCommand.Execute(null);
    }

    // ------------------------------------------------------------------ live trace

    private float[]? _lastFp1, _lastFp2;

    private void TraceCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastFp1 != null && _lastFp2 != null) DrawTrace(_lastFp1, _lastFp2);
    }

    private void DrawTrace(float[] fp1, float[] fp2)
    {
        _lastFp1 = fp1;
        _lastFp2 = fp2;
        var w = TraceCanvas.ActualWidth;
        var h = TraceCanvas.ActualHeight;
        if (w < 10 || h < 10) return;
        Fp1Line.Points = Lane(fp1, w, h * 0.28, h * 0.22);
        Fp2Line.Points = Lane(fp2, w, h * 0.72, h * 0.22);
    }

    private static PointCollection Lane(float[] data, double width, double mid, double halfHeight)
    {
        const int step = 2; // 1000 points for 4 s is plenty
        var pts = new PointCollection(data.Length / step + 1);
        for (var i = 0; i < data.Length; i += step)
        {
            var v = data[i];
            var y = float.IsNaN(v) ? mid : mid - Math.Clamp(v / LaneMicrovolts, -1, 1) * halfHeight;
            pts.Add(new Point(i * width / (data.Length - 1), y));
        }
        pts.Freeze();
        return pts;
    }
}