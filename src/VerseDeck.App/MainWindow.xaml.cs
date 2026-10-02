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
        Loaded += async (_, _) => await _shell.InitializeAsync();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        // Voice and the mobile server must stop before the process exits.
        e.Cancel = true;
        await _shell.ShutdownAsync();
        _shutdownComplete = true;

        // Close cannot be called from inside the Closing handler when shutdown finishes synchronously.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
