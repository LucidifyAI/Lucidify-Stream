using System;
using System.ComponentModel;
using System.Windows;
using LucidStream.App.ViewModels;
using LucidStream.Core.Common;

namespace LucidStream.App.Cloud;

/// <summary>
/// Lucidify Cloud next to the live signal: the local trace straight from the patch on top, the Cloud viewer below,
/// and the recording controls. Closing hides the window; the page (and the sign-in it holds) stays alive so Cloud
/// recording keeps uploading.
/// </summary>
public partial class CloudWindow : Window
{
    private readonly CloudViewModel _vm;
    private CloudPageBridge? _bridge;
    private bool _allowClose;

    public CloudWindow(CloudViewModel vm, MainViewModel main)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // The local sweep mirrors the main window's live view and its settings.
        LocalSweep.Signal = main.Live;
        LocalSweep.Running = main.IsStreaming;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsStreaming)) LocalSweep.Running = main.IsStreaming;
        };
        main.LiveViewReset += LocalSweep.ResetView;
        IsVisibleChanged += (_, _) => ApplyLiveView(main.Settings.LiveView);
        ApplyLiveView(main.Settings.LiveView);
        var icon = Branding.CreateWindowIcon();
        if (icon != null) Icon = icon;
        SourceInitialized += (_, _) => Branding.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await StartPageAsync();
        Closing += OnClosing;
        vm.Confirm = message => MessageBox.Show(this, message, "Record to Lucidify Cloud", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private async System.Threading.Tasks.Task StartPageAsync()
    {
        try
        {
            _bridge = await CloudPageBridge.CreateAsync(Web, _vm.ViewerUrl, _vm.FirebaseJsVersion);
            var attached = false;
            Web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                WebStatus.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess) { Log.Warn($"The Cloud page did not load ({e.WebErrorStatus})."); return; }
                if (attached) return;
                attached = true;
                _vm.AttachPage(_bridge);
            };
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            WebStatus.Text = "The Cloud page needs the Microsoft Edge WebView2 Runtime, which Windows 10 and 11 normally include. " +
                             "Install it from Microsoft (search \"WebView2 Runtime\") and reopen this window.\n\n" + ex.Message;
            Log.Error($"Cloud page: {ex.Message}");
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _bridge?.Reload();

    /// <summary>Close for real (app exit).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(Hide));
    }

    // ------------------------------------------------------------------ local sweep

    private void ApplyLiveView(LucidStream.Core.Engine.LiveViewSettings v)
    {
        LocalSweep.WindowSeconds = v.WaveWindowSeconds;
        LocalSweep.FixedMicrovolts = v.Amplitude == "auto" ? null
            : double.TryParse(v.Amplitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var uv) ? uv : 50;
        LocalSweep.Gain = v.Gain;
        LocalSweep.Mode = v.Signal switch { "raw" => Live.WaveSignal.Raw, "filtered" => Live.WaveSignal.Filtered, _ => Live.WaveSignal.Display };
    }
}