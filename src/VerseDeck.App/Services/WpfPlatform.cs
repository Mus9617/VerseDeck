using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VerseDeck.App.Services;

public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}

public sealed class DispatcherUiScheduler : IUiScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherUiScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new Handle(timer);
    }

    private sealed class Handle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;

    /// <summary>Matches the system title bar to the theme so a dark deck does not sit under a white bar.</summary>
    public static void SetDark(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
