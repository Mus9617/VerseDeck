using VerseDeck.Core.Models;
using VerseDeck.MobileServer;

namespace VerseDeck.App.Services;

/// <summary>Access to the interface thread, so view models never touch the dispatcher.</summary>
public interface IUiScheduler
{
    void Post(Action action);

    /// <summary>Runs the action once after the delay unless the returned handle is disposed first.</summary>
    IDisposable After(TimeSpan delay, Action action);
}

public interface IPttMonitor
{
    event EventHandler<bool>? PressedChanged;
    void Start(string deviceType, string binding);
    void Stop();
    bool AnyInputPressed();
    bool TryDetectPressed(out PttBinding binding);
}

public interface IMobileLink
{
    bool IsRunning { get; }
    string Url { get; }
    int ConnectedCount { get; }
    Task StartAsync(int port, string pin);
    Task StopAsync();
}

public sealed class MobileLink : IMobileLink
{
    private readonly MobilePanelServer _server;

    public MobileLink(IVerseDeckRepository repository, IInputSender inputSender, IDebugLog log)
    {
        _server = new MobilePanelServer(repository, inputSender);
        _server.Diagnostic += (_, message) => log.Write($"Mobile: {message}");
    }

    public bool IsRunning => _server.IsRunning;
    public string Url { get; private set; } = string.Empty;
    public int ConnectedCount => _server.ConnectedDevices.Count;

    public async Task StartAsync(int port, string pin)
    {
        await _server.StartAsync(port, pin);
        Url = $"http://{_server.LocalIpAddress}:{port}";
    }

    public async Task StopAsync()
    {
        await _server.StopAsync();
        Url = string.Empty;
    }
}
