using System.Windows;
using System;
using System.Threading;
using System.Windows.Threading;
using LucidStream.Core.Common;

namespace LucidStream.App;

public partial class App : Application
{
    // Per Windows user session: another user signed in on the same PC can run their own copy.
    private const string InstanceName = @"Local\LucidStream.App.SingleInstance";
    private const string ShowSignalName = @"Local\LucidStream.App.ShowWindow";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One copy at a time: the patch, the WebSocket port and the Cloud upload can only have one owner.
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (Exception) { /* the other copy is still starting or closing */ }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            try
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.BringToFront()));
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "Single instance" };
        listener.Start();

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        if (_instance != null)
        {
            try { _instance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unexpected error: {e.Exception.Message}");
        MessageBox.Show(e.Exception.Message, "Lucid-Stream", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}