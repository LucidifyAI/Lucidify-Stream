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
    private float[]? _lastA, _lastB;
    private bool _allowClose;

    public CloudWindow(CloudViewModel vm, MainViewModel main)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        main.TraceUpdated += DrawTrace;
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

    // ------------------------------------------------------------------ local trace

    private void TraceCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastA != null && _lastB != null) DrawTrace(_lastA, _lastB);
    }

    private void DrawTrace(float[] afz, float[] fp2)
    {
        _lastA = afz;
        _lastB = fp2;
        if (!IsVisible) return;
        var w = TraceCanvas.ActualWidth;
        var h = TraceCanvas.ActualHeight;
        if (w < 10 || h < 10) return;
        AfzLine.Points = MainWindow.Lane(afz, w, h * 0.28, h * 0.22);
        Fp2Line.Points = MainWindow.Lane(fp2, w, h * 0.72, h * 0.22);
    }
}