using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;
using VerseDeck.Data;
using VerseDeck.Game;
using VerseDeck.Input;
using VerseDeck.Speech;
using VerseDeck.Voice;

namespace VerseDeck.App;

public partial class App : Application
{
    // VERSEDECK_DATA_DIR points the app at a throwaway data folder, for trying things without touching the real deck.
    private static readonly string AppDataPath = Environment.GetEnvironmentVariable("VERSEDECK_DATA_DIR") is { Length: > 0 } overridePath
        ? overridePath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VerseDeck Companion");
    private static readonly string CrashLogPath = Path.Combine(AppDataPath, "crash.log");
    private static readonly string DebugLogPath = Path.Combine(AppDataPath, "debug.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(AppDataPath);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LogCrash("App startup");

        var window = new MainWindow(CreateShell());
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }

    private ShellViewModel CreateShell()
    {
        var log = new DebugLog(DebugLogPath);
        var repository = new SqliteVerseDeckRepository(Path.Combine(AppDataPath, "versedeck.db"));
        var session = new DeckSession(repository);
        var inputSender = new WindowsInputSender();
        var dialogs = new WpfDialogService();
        var audio = new AudioFeedback();
        var ui = new DispatcherUiScheduler(Dispatcher);
        var catalog = GameActionCatalog.Load();
        var controlSync = new ControlSync(session, GameInstallLocator.ForThisMachine(), catalog, new FileWatch(), ui, log, () => DateTimeOffset.Now);
        var executor = new ButtonExecutor(inputSender, repository, dialogs, audio, log, () => session.Settings);

        var voiceStore = new VoiceStore(Path.Combine(AppDataPath, "voices"));
        var voiceInstaller = new VoiceInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, voiceStore);
        voiceInstaller.CleanLeftovers();
        var speechGuard = new SpeechGuard();
        var copilot = new CopilotService(
            session,
            executor,
            new SherpaTtsEngine(voiceStore),
            new PhraseCache(Path.Combine(AppDataPath, "voice-cache")),
            new WavAudioPlayer(),
            VoiceCatalog.Load(),
            voiceStore,
            ResponsePack.LoadAll(),
            speechGuard,
            log,
            () => DateTimeOffset.Now);

        return new ShellViewModel(new ShellServices(
            session,
            repository,
            executor,
            dialogs,
            audio,
            log,
            ui,
            new WindowsSpeechCommandService(),
            new PttInputMonitor(),
            new MobileLink(repository, inputSender, log),
            new ThemeService(ApplyTheme),
            () => OpenDebugConsole(log),
            controlSync,
            catalog,
            copilot,
            speechGuard,
            voiceInstaller,
            DrainInput: inputSender.WhenIdleAsync));
    }

    private void ApplyTheme(ThemeId theme)
    {
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
        if (MainWindow is not null)
        {
            TitleBar.SetDark(MainWindow, theme != ThemeId.Origin);
        }
    }

    private static void OpenDebugConsole(IDebugLog log)
    {
        log.Write("Debug console opened");
        var escapedPath = log.Path.Replace("'", "''");
        var command = $"Write-Host 'VerseDeck debug log: {escapedPath}'; Get-Content -LiteralPath '{escapedPath}' -Tail 80 -Wait";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true
        });
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
