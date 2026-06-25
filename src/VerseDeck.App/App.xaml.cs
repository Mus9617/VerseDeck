using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VerseDeck.App;

public partial class App : Application
{
    private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VerseDeck Companion");
    private static readonly string CrashLogPath = Path.Combine(AppDataPath, "crash.log");
    private static readonly string DebugLogPath = Path.Combine(AppDataPath, "debug.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(AppDataPath);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LogCrash("App startup");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash($"Dispatcher exception: {e.Exception}");
        MessageBox.Show(
            $"VerseDeck ha detectado un error y lo ha guardado en:\n{CrashLogPath}\n\n{e.Exception.Message}",
            "VerseDeck Companion",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash($"Unhandled exception terminating={e.IsTerminating}: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash($"Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void LogCrash(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath, line);
            File.AppendAllText(DebugLogPath, line);
        }
        catch
        {
            // Last-resort crash logging must never throw.
        }
    }
}
