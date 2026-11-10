using System.Windows;
using System.Windows.Threading;
using LucidStream.Core.Common;

namespace LucidStream.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unexpected error: {e.Exception.Message}");
        MessageBox.Show(e.Exception.Message, "Lucid-Stream", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}