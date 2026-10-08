using System.Windows;
using System.Windows.Threading;
using ImmersiveSBS.Theme;

namespace ImmersiveSBS;

/// <summary>A tray application: no main window, one instance per user session.</summary>
public partial class App : Application
{
    private static readonly Mutex SingleInstance = new(false, @"Local\ImmersiveSBS.{5C1E6E52-4B7B-4E0B-9C1F-3F1D2B7A9E61}");
    private AppController? controller;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("App: unhandled exception: " + args.ExceptionObject);

        try
        {
            ownsMutex = SingleInstance.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance was killed; the mutex is ours now.
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            MessageBox.Show("ImmersiveSBS is already running. Its icon is in the notification area.", "ImmersiveSBS");
            Shutdown(1);
            return;
        }

        // Before any window: the settings window is themed.
        ThemeManager.Apply(this);

        controller = new AppController();
        controller.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("App: unhandled exception on the UI thread: " + e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        if (ownsMutex) SingleInstance.ReleaseMutex();
        base.OnExit(e);
    }
}
