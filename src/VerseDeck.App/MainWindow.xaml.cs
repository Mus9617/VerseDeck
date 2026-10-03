using System.ComponentModel;
using System.Windows;
using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;

namespace VerseDeck.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private bool _shutdownComplete;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
        SourceInitialized += (_, _) => TitleBar.SetDark(this, true);
        Loaded += async (_, _) =>
        {
            _shell.IsWindowActive = IsActive;
            await _shell.InitializeAsync();
        };
        Closing += OnClosing;
        StateChanged += (_, _) => _shell.IsMinimized = WindowState == WindowState.Minimized;
        Activated += (_, _) => _shell.IsWindowActive = true;
        Deactivated += (_, _) => _shell.IsWindowActive = false;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        // Voice and the mobile server must stop before the process exits.
        e.Cancel = true;
        try
        {
            await _shell.ShutdownAsync();
        }
        finally
        {
            // A failed shutdown must not leave a window that can never be closed.
            _shutdownComplete = true;
        }

        // Close cannot be called from inside the Closing handler when shutdown finishes synchronously.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
