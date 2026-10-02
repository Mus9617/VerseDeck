using VerseDeck.App;
using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;
using VerseDeck.Core.Models;
using VerseDeck.Data;

namespace VerseDeck.Tests;

public sealed class FakeScheduler : IUiScheduler
{
    private sealed class Timer(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }

    private readonly List<Timer> _timers = [];

    public void Post(Action action) => action();

    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new Timer(delay, action);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Fires the pending timers with exactly this delay, once each.</summary>
    public int Fire(TimeSpan delay)
    {
        var due = _timers.Where(t => !t.Cancelled && t.Delay == delay).ToList();
        foreach (var timer in due)
        {
            _timers.Remove(timer);
            timer.Action();
        }

        return due.Count;
    }
}

public sealed class FakeVoiceService : IVoiceCommandService
{
    public bool CanStart { get; set; } = true;
    public bool ThrowOnStart { get; set; }
    public int StartCount { get; private set; }
    public IReadOnlyList<VoiceCommand> LastCommands { get; private set; } = [];
    public bool IsRunning { get; private set; }
    public bool Paused { get; private set; }
    public bool GateOpen { get; private set; }
    public TimeSpan? LastGrace { get; private set; }

    public event EventHandler<VoiceRecognizedEventArgs>? CommandRecognized;
    public event EventHandler<string>? Diagnostic;

    public Task StartAsync(IReadOnlyList<VoiceCommand> commands, IReadOnlyList<DeckButton> buttons, CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("no microphone");
        }

        StartCount++;
        LastCommands = commands;
        IsRunning = CanStart && commands.Count > 0;
        Paused = false;
        Diagnostic?.Invoke(this, "started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public void PauseRecognition() => Paused = true;
    public void ResumeRecognition() => Paused = false;

    public void SetInputGate(bool isOpen, TimeSpan? releaseGrace = null)
    {
        GateOpen = isOpen;
        LastGrace = releaseGrace;
    }

    public void Raise(VoiceCommand command, DeckButton button) => CommandRecognized?.Invoke(this, new VoiceRecognizedEventArgs(command, button, 0.9));

    public void Dispose()
    {
    }
}

public sealed class FakePttMonitor : IPttMonitor
{
    public bool Running { get; private set; }
    public string? Device { get; private set; }
    public string? Binding { get; private set; }
    public bool AnyPressed { get; set; }
    public PttBinding? Detected { get; set; }

    public event EventHandler<bool>? PressedChanged;

    public void Start(string deviceType, string binding)
    {
        Running = true;
        Device = deviceType;
        Binding = binding;
    }

    // The real monitor reports a release when it stops while the button is held.
    public bool HeldWhenStopped { get; set; }

    public void Stop()
    {
        Running = false;
        if (HeldWhenStopped)
        {
            PressedChanged?.Invoke(this, false);
        }
    }

    public bool AnyInputPressed() => AnyPressed;

    public bool TryDetectPressed(out PttBinding binding)
    {
        binding = Detected ?? default;
        return Detected is not null;
    }

    public void Raise(bool pressed) => PressedChanged?.Invoke(this, pressed);
}

public sealed class FakeMobileLink : IMobileLink
{
    public bool ThrowOnStart { get; set; }
    public bool IsRunning { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public int ConnectedCount { get; set; }
    public string? LastPin { get; private set; }

    public Task StartAsync(int port, string pin)
    {
        if (ThrowOnStart)
        {
            throw new IOException("address already in use");
        }

        IsRunning = true;
        LastPin = pin;
        Url = $"http://192.168.1.10:{port}";
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        Url = string.Empty;
        return Task.CompletedTask;
    }
}

/// <summary>A full shell over a temporary database with every external dependency faked.</summary>
public sealed class Harness : IAsyncDisposable
{
    public TempDatabase Db { get; } = new();
    public FakeInputSender Sender { get; } = new();
    public FakeDialogService Dialogs { get; } = new();
    public FakeAudio Audio { get; } = new();
    public FakeLog Log { get; } = new();
    public FakeScheduler Ui { get; } = new();
    public FakeVoiceService Voice { get; } = new();
    public FakePttMonitor Ptt { get; } = new();
    public FakeMobileLink Mobile { get; } = new();
    public List<ThemeId> AppliedThemes { get; } = [];
    public int DebugConsoleOpens { get; private set; }
    public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    public SqliteVerseDeckRepository Repository { get; private set; } = null!;
    public DeckSession Session { get; private set; } = null!;
    public ShellViewModel Shell { get; private set; } = null!;

    public static async Task<Harness> CreateAsync(bool initialize = true)
    {
        var harness = new Harness();
        harness.Repository = await harness.Db.CreateAsync();
        harness.Session = new DeckSession(harness.Repository);
        var executor = new ButtonExecutor(harness.Sender, harness.Repository, harness.Dialogs, harness.Audio, harness.Log, () => harness.Session.Settings);
        harness.Shell = new ShellViewModel(new ShellServices(
            harness.Session,
            harness.Repository,
            executor,
            harness.Dialogs,
            harness.Audio,
            harness.Log,
            harness.Ui,
            harness.Voice,
            harness.Ptt,
            harness.Mobile,
            new ThemeService(harness.AppliedThemes.Add),
            () => harness.DebugConsoleOpens++,
            () => harness.Now));
        if (initialize)
        {
            await harness.Shell.InitializeAsync();
        }

        return harness;
    }

    public ModuleTileViewModel Tile(string name) => Shell.Deck.Groups.SelectMany(g => g.Tiles).First(t => t.Name == name);

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
