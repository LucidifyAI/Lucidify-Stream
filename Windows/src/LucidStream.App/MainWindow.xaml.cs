using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LucidStream.App.Ble;
using LucidStream.App.Live;
using LucidStream.App.ViewModels;
using LucidStream.Core.Common;
using LucidStream.Core.Engine;

namespace LucidStream.App;

public partial class MainWindow : Window
{
    internal const double LaneMicrovolts = 60;
    private readonly MainViewModel _vm;
    private readonly SpectrogramComputer _spectrogram = new();
    private bool _viewSettingsLoaded;
    private readonly DispatcherTimer _timer;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings, AppContext.BaseDirectory, $"Lucid-Stream {typeof(MainWindow).Assembly.GetName().Version} (Windows)",
            s => new PatchSource(s.Ble));
        Sweep.Signal = _vm.Live;
        SpecAfz.Computer = _spectrogram;
        SpecFp2.Computer = _spectrogram;
        SpecAfz.LabelBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xC6, 0xD6));
        SpecFp2.LabelBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0x73, 0xED));
        LoadViewSettings(settings.LiveView);
        _vm.LiveViewReset += () =>
        {
            _spectrogram.Clear();
            Sweep.ResetView();
            SpecAfz.Refresh();
            SpecFp2.Refresh();
        };
        _vm.OpenUrlRequested += OpenUrl;
        _vm.Cloud.OpenRequested += OpenCloud;
        DataContext = _vm;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) =>
        {
            _vm.Tick();
            Sweep.Running = _vm.IsStreaming;
            if (_vm.IsStreaming && _spectrogram.Update(_vm.Live) && ++_specTicks % 2 == 0) RefreshSpectrograms();
        };
        _timer.Start();

        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SettingsDrawer.Visibility == Visibility.Visible)
            {
                SetSettingsOpen(false);
                e.Handled = true;
            }
        };

        var icon = Branding.CreateWindowIcon();
        if (icon != null) Icon = icon;
        SourceInitialized += (_, _) => Branding.ApplyDarkTitleBar(this);
    }

    // ------------------------------------------------------------------ lifetime: the app runs only while its window is open

    /// <summary>Bring the window forward (a second copy of the app was started).</summary>
    internal void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        // Closing the window ends everything: streaming, the patch connection and any Cloud recording.
        // Cancel this close, shut down cleanly with the window still showing, then close for real.
        // (WPF forbids Close while the window is closing, so this runs once the handler returns.)
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Func<Task>(ExitAsync));
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        Title = "Lucid-Stream - closing...";
        IsEnabled = false;
        Cursor = Cursors.Wait;
        if (_vm.Cloud.IsRecording) Log.Info("Closing: stopping the Cloud recording and finishing its upload (up to a few seconds).");
        try { await _vm.DisposeAsync(); }
        catch (Exception ex) { Log.Error($"Shutdown: {ex.Message}"); }
        _cloudWindow?.CloseForExit();
        // Shutdown closes the window; OnClosing lets it through because _exiting is set.
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ Lucidify Cloud window

    private Cloud.CloudWindow? _cloudWindow;

    private void OpenCloud()
    {
        _cloudWindow ??= new Cloud.CloudWindow(_vm.Cloud, _vm);
        _cloudWindow.Show();
        if (_cloudWindow.WindowState == WindowState.Minimized) _cloudWindow.WindowState = WindowState.Normal;
        _cloudWindow.Activate();
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

    // ------------------------------------------------------------------ live views: settings under each card

    private int _specTicks;

    private void RefreshSpectrograms()
    {
        if (SpecAfzBorder.Visibility == Visibility.Visible) SpecAfz.Refresh();
        if (SpecFp2Border.Visibility == Visibility.Visible) SpecFp2.Refresh();
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem { Tag: string t } c && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = c;
                return;
            }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string t } ? t : "";

    private static double Num(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    private void LoadViewSettings(LiveViewSettings v)
    {
        Select(WaveWindowBox, Inv(v.WaveWindowSeconds));
        Select(AmplitudeBox, v.Amplitude);
        GainSlider.Value = v.Gain;
        Select(SignalBox, v.Signal);
        Select(SpecWindowBox, Inv(v.SpecWindowSeconds));
        MaxHzSlider.Value = v.SpecMaxHz;
        Select(FftBox, Inv(v.SpecFftSeconds));
        Select(OverlapBox, Inv(v.SpecOverlapPercent));
        BrightnessSlider.Value = v.SpecBrightnessDb;
        GammaSlider.Value = v.SpecGamma;
        NotchBox.IsChecked = v.SpecNotch;
        Select(SpecChannelsBox, v.SpecChannels);
        _viewSettingsLoaded = true;
        ApplyViewSettings(save: false);
    }

    private void ViewSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewSettingsLoaded) ApplyViewSettings(save: true);
    }

    private void ApplyViewSettings(bool save)
    {
        var v = _vm.Settings.LiveView;
        v.WaveWindowSeconds = Num(TagOf(WaveWindowBox), 10);
        v.Amplitude = TagOf(AmplitudeBox) is { Length: > 0 } amp ? amp : "auto";
        v.Gain = Math.Round(GainSlider.Value, 2);
        v.Signal = TagOf(SignalBox) is { Length: > 0 } sig ? sig : "display";
        v.SpecWindowSeconds = Num(TagOf(SpecWindowBox), 600);
        v.SpecMaxHz = Math.Round(MaxHzSlider.Value);
        v.SpecFftSeconds = Num(TagOf(FftBox), 2);
        v.SpecOverlapPercent = Num(TagOf(OverlapBox), 87.5);
        v.SpecBrightnessDb = Math.Round(BrightnessSlider.Value);
        v.SpecGamma = Math.Round(GammaSlider.Value, 2);
        v.SpecNotch = NotchBox.IsChecked == true;
        v.SpecChannels = TagOf(SpecChannelsBox) is { Length: > 0 } chs ? chs : "both";

        Sweep.WindowSeconds = v.WaveWindowSeconds;
        Sweep.FixedMicrovolts = v.Amplitude == "auto" ? null : Num(v.Amplitude, 50);
        Sweep.Gain = v.Gain;
        Sweep.Mode = v.Signal switch { "raw" => WaveSignal.Raw, "filtered" => WaveSignal.Filtered, _ => WaveSignal.Display };
        GainText.Text = $"{v.Gain:0.00}x";
        WaveCaption.Text = $"{v.WaveWindowSeconds:0} s sweep, " + v.Signal switch
        {
            "raw" => "raw (offset removed)",
            "filtered" => "filtered 0.5-40 Hz",
            _ => "display filtered, as in Cloud"
        };

        var p = new SpectrogramParams(v.SpecFftSeconds, v.SpecOverlapPercent, _vm.Settings.LineFrequencyHz, v.SpecNotch);
        if (p != _spectrogram.Params)
        {
            // New FFT settings: recompute from what the live buffer still holds (about the last 2 minutes).
            _spectrogram.Configure(p);
            _spectrogram.Update(_vm.Live);
        }
        foreach (var s in new[] { SpecAfz, SpecFp2 })
        {
            s.WindowSeconds = v.SpecWindowSeconds;
            s.MaxHz = v.SpecMaxHz;
            s.BrightnessDb = v.SpecBrightnessDb;
            s.Gamma = v.SpecGamma;
        }
        SpecAfzBorder.Visibility = v.SpecChannels == "fp2" ? Visibility.Collapsed : Visibility.Visible;
        SpecFp2Border.Visibility = v.SpecChannels == "afz" ? Visibility.Collapsed : Visibility.Visible;
        SpecRowA.Height = v.SpecChannels == "fp2" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SpecRowB.Height = v.SpecChannels == "afz" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SpecGap.Height = new GridLength(v.SpecChannels == "both" ? 8 : 0);
        MaxHzText.Text = $"{v.SpecMaxHz:0} Hz";
        BrightnessText.Text = $"{(v.SpecBrightnessDb > 0 ? "+" : "")}{v.SpecBrightnessDb:0} dB";
        GammaText.Text = v.SpecGamma.ToString("0.00", CultureInfo.InvariantCulture);
        SpecCaption.Text = $"raw EEG, {v.SpecFftSeconds:0} s FFT, {v.SpecOverlapPercent:0.#} % overlap";
        RefreshSpectrograms();
        if (save) _vm.Settings.Save();
    }

    // ------------------------------------------------------------------ settings drawer

    private void Settings_Click(object sender, RoutedEventArgs e) => SetSettingsOpen(SettingsDrawer.Visibility != Visibility.Visible);
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SetSettingsOpen(false);
    private void SettingsDim_MouseDown(object sender, MouseButtonEventArgs e) => SetSettingsOpen(false);

    private void SetSettingsOpen(bool open)
    {
        SettingsDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SettingsDim.Visibility = SettingsDrawer.Visibility;
    }

    internal static PointCollection Lane(float[] data, double width, double mid, double halfHeight, double microvolts = LaneMicrovolts)
    {
        // About two points per pixel is plenty, whatever the window length.
        var step = Math.Max(1, (int)(data.Length / Math.Max(200, width * 2)));
        var pts = new PointCollection(data.Length / step + 1);
        for (var i = 0; i < data.Length; i += step)
        {
            var v = data[i];
            var y = float.IsNaN(v) ? mid : mid - Math.Clamp(v / microvolts, -1, 1) * halfHeight;
            pts.Add(new Point(i * width / (data.Length - 1), y));
        }
        pts.Freeze();
        return pts;
    }
}