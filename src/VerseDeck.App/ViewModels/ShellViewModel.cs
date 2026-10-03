using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Game;
using VerseDeck.Speech;

namespace VerseDeck.App.ViewModels;

public sealed partial class MobileLinkViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly IMobileLink _link;
    private readonly IStatusSink _status;

    [ObservableProperty]
    private LinkState _state;

    [ObservableProperty]
    private string _url = "Servidor detenido";

    [ObservableProperty]
    private string _pinText = string.Empty;

    [ObservableProperty]
    private string _devicesText = "Dispositivos conectados: 0";

    [ObservableProperty]
    private byte[]? _qrPng;

    public MobileLinkViewModel(DeckSession session, IMobileLink link, IStatusSink status)
    {
        _session = session;
        _link = link;
        _status = status;
        _session.Changed += (_, _) => PinText = $"PIN de emparejamiento: {_session.Settings.PairingPin}";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var settings = _session.Settings;
        try
        {
            await _link.StartAsync(settings.MobilePort, settings.PairingPin);
        }
        catch (Exception ex)
        {
            State = LinkState.Offline;
            _status.Error($"No se pudo iniciar el servidor movil en el puerto {settings.MobilePort}: {ex.Message}");
            return;
        }

        State = LinkState.Online;
        Url = _link.Url;
        QrPng = CreateQr(_link.Url);
        Refresh();
        _status.Info("Panel movil LAN activo");
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _link.StopAsync();
        State = LinkState.Offline;
        Url = "Servidor detenido";
        QrPng = null;
        Refresh();
        _status.Info("Servidor movil detenido");
    }

    public void Refresh()
    {
        DevicesText = $"Dispositivos conectados: {(_link.IsRunning ? _link.ConnectedCount : 0)}";
    }

    private static byte[] CreateQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(8);
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly IStatusSink _status;
    private readonly Action _openDebugConsole;
    private bool _syncing;

    [ObservableProperty]
    private bool _welcomeSound = true;

    [ObservableProperty]
    private bool _commandSound = true;

    [ObservableProperty]
    private string _theme = ThemeService.Auto;

    public SettingsViewModel(DeckSession session, ThemeService themes, IStatusSink status, Action openDebugConsole)
    {
        _session = session;
        _status = status;
        _openDebugConsole = openDebugConsole;
        ThemeChoices = themes.Choices;
        _session.Changed += (_, _) => Sync();
    }

    public IReadOnlyList<string> ThemeChoices { get; }

    /// <summary>The last save started by a setting change, so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void OpenDebugConsole() => _openDebugConsole();

    partial void OnWelcomeSoundChanged(bool value) => QueueSave();

    partial void OnCommandSoundChanged(bool value) => QueueSave();

    partial void OnThemeChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (!_syncing)
        {
            Pending = SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _session.SaveSettingsAsync(_session.Settings with
            {
                Theme = Theme ?? ThemeService.Auto,
                WelcomeSoundEnabled = WelcomeSound,
                CommandSoundEnabled = CommandSound
            });
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var settings = _session.Settings;
            WelcomeSound = settings.WelcomeSoundEnabled;
            CommandSound = settings.CommandSoundEnabled;
            Theme = ThemeChoices.FirstOrDefault(c => c.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? ThemeService.Auto;
        }
        finally
        {
            _syncing = false;
        }
    }
}

public sealed record ShellServices(
    DeckSession Session,
    IVerseDeckRepository Repository,
    ButtonExecutor Executor,
    IDialogService Dialogs,
    IAudioFeedback Audio,
    IDebugLog Log,
    IUiScheduler Ui,
    IVoiceCommandService Voice,
    IPttMonitor Ptt,
    IMobileLink Mobile,
    ThemeService Themes,
    Action OpenDebugConsole,
    ControlSync ControlSync,
    GameActionCatalog Catalog,
    CopilotService Copilot,
    SpeechGuard SpeechGuard,
    IVoiceInstaller VoiceInstaller,
    Func<DateTimeOffset>? Clock = null,
    Func<Task>? DrainInput = null,
    VerseDeck.Voice.IPhraseChecker? PhraseChecker = null,
    Func<string>? ExportFolder = null);

public sealed partial class ShellViewModel : ObservableObject, IStatusSink
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ShellServices _services;
    private IDisposable? _refreshTimer;

    [ObservableProperty]
    private string _section = "Deck";

    [ObservableProperty]
    private string _statusText = "Inicializando";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private string _profileTitle = string.Empty;

    [ObservableProperty]
    private string _shipTitle = string.Empty;

    [ObservableProperty]
    private bool _isMuted;

    public ShellViewModel(ShellServices services)
    {
        _services = services;
        Deck = new DeckViewModel(services.Session, services.Executor, this, services.Ui, services.ControlSync);
        Editor = new ModuleEditorViewModel(services.Session, services.Dialogs, this, services.ControlSync, services.Catalog);
        Controls = new ControlsViewModel(services.Session, services.ControlSync, services.Catalog, this);
        Profile = new ProfileViewModel(services.Session, this);
        Activity = new ActivityViewModel(services.Repository);
        Voice = new VoiceViewModel(services.Session, services.Voice, services.Ptt, services.Executor, this, services.Ui, services.Log, services.Clock ?? (() => DateTimeOffset.Now), services.SpeechGuard);
        Copilot = new CopilotViewModel(services.Session, services.Copilot, services.VoiceInstaller, this);
        var clock = services.Clock ?? (() => DateTimeOffset.Now);
        Checklists = new ChecklistRunner(services.Session, services.Executor, services.Copilot);
        Timers = new TimerService(services.Ui, clock, services.Copilot, services.Audio);
        Aboard = new AboardViewModel(
            services.Session,
            Checklists,
            Timers,
            new LogbookService(services.Repository, services.Session, clock),
            services.Copilot,
            services.Ui,
            clock,
            this,
            services.ExportFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Voice.CompanionHandler = Aboard.HandleAsync;
        if (services.PhraseChecker is not null)
        {
            var doctor = new VoiceDoctor(services.Session, services.Copilot, services.PhraseChecker, services.Log);
            Voice.Doctor = doctor;
            Editor.Doctor = doctor;
        }

        // A linked module whose game action has no usable key must not fire the key it had before.
        // Only when the player's binds were actually read: without them the status is a guess.
        Func<DeckButton, string?> block = button =>
            services.ControlSync.HasGoodRead && services.ControlSync.StatusOf(button.Id) is BindStatus.NoKey or BindStatus.NotSendable
                ? "Esa accion no tiene tecla utilizable en el juego. Revisa la seccion Controles."
                : null;
        services.Executor.BlockReason = block;
        services.Mobile.BlockReason = block;
        services.Copilot.Changed += (_, _) => IsMuted = services.Copilot.Muted;
        Mobile = new MobileLinkViewModel(services.Session, services.Mobile, this);
        Settings = new SettingsViewModel(services.Session, services.Themes, this, services.OpenDebugConsole);

        Deck.SelectedButtonChanged += (_, button) => Editor.Load(button);
        Editor.RequestSelect = Deck.Select;
        Voice.RequestSelect = Deck.Select;
        services.Session.Changed += (_, _) => OnSessionChanged();
        services.Executor.Sent += (_, _) => _ = Activity.RefreshAsync();

        // Phone presses from the last seconds before the panel stops would otherwise wait for another event.
        Mobile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MobileLinkViewModel.State) && Mobile.State == LinkState.Offline)
            {
                _ = Activity.RefreshAsync();
            }
        };
    }

    public DeckViewModel Deck { get; }
    public ModuleEditorViewModel Editor { get; }
    public ProfileViewModel Profile { get; }
    public ActivityViewModel Activity { get; }
    public VoiceViewModel Voice { get; }
    public MobileLinkViewModel Mobile { get; }
    public SettingsViewModel Settings { get; }
    public ControlsViewModel Controls { get; }
    public CopilotViewModel Copilot { get; }
    public AboardViewModel Aboard { get; }
    public ChecklistRunner Checklists { get; }
    public TimerService Timers { get; }

    /// <summary>The work of the last periodic tick, so tests can wait for it.</summary>
    public Task RefreshTick { get; private set; } = Task.CompletedTask;

    /// <summary>The copilot's greeting and phrase warm-up, started at launch and never awaited by the interface.</summary>
    public Task CopilotStart { get; private set; } = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        try
        {
            await _services.Session.LoadAsync();
            await _services.ControlSync.Pending;
            Voice.SyncFromSettings();
            if (_services.Session.Settings.WelcomeSoundEnabled)
            {
                _services.Audio.PlayWelcome();
            }

            await Activity.RefreshAsync();
            await Aboard.InitializeAsync();
            ScheduleRefresh();
            CopilotStart = _services.Copilot.StartAsync();
            _services.Log.Write("App initialized");
            Info("Sistema listo");
        }
        catch (Exception ex)
        {
            _services.Log.Write($"Initialization failed: {ex}");
            Error($"No se pudo iniciar: {ex.Message}");
        }
    }

    public async Task ShutdownAsync()
    {
        _refreshTimer?.Dispose();
        Copilot.CancelAll();
        await Voice.StopCommand.ExecuteAsync(null);
        await Mobile.StopCommand.ExecuteAsync(null);

        // A long press may still be holding its key; closing now would leave it down in Windows.
        if (_services.DrainInput is not null)
        {
            await _services.DrainInput();
        }

        _services.Log.Write("App closing");
    }

    [RelayCommand]
    private void Navigate(string section) => Section = section;

    partial void OnIsMutedChanged(bool value) => _services.Copilot.Muted = value;

    // The countdown only ticks while its section is on screen.
    partial void OnSectionChanged(string value) => Aboard.IsVisible = value == "Abordo";

    public void Info(string message)
    {
        StatusIsError = false;
        StatusText = message;
    }

    public void Error(string message)
    {
        StatusIsError = true;
        StatusText = message;
    }

    private void OnSessionChanged()
    {
        var profile = _services.Session.ActiveProfile;
        ProfileTitle = (profile?.Name ?? string.Empty).ToUpperInvariant();
        ShipTitle = (profile?.ShipName ?? string.Empty).ToUpperInvariant();
        _services.Themes.Update(_services.Session.Settings.Theme, profile?.ShipName);
        _ = Activity.RefreshAsync();
    }

    // Presses from the phone are logged by the server, so the activity list is polled, but only while the
    // phone panel is on: clicks and voice already refresh it through events, and the game needs the CPU.
    private void ScheduleRefresh()
    {
        _refreshTimer = _services.Ui.After(RefreshInterval, () =>
        {
            RefreshTick = Mobile.State == LinkState.Online ? RefreshForPhoneAsync() : Task.CompletedTask;
            ScheduleRefresh();
        });
    }

    private async Task RefreshForPhoneAsync()
    {
        await Activity.RefreshAsync();
        Mobile.Refresh();
    }
}
