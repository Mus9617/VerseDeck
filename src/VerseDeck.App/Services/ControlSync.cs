using System.IO;
using VerseDeck.Core.Models;
using VerseDeck.Game;

namespace VerseDeck.App.Services;

public interface IFileWatch
{
    /// <summary>Calls back, possibly from another thread and several times per save, when the file changes.</summary>
    IDisposable Watch(string path, Action changed);
}

public sealed class FileWatch : IFileWatch
{
    public IDisposable Watch(string path, Action changed)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder))
        {
            return new Nothing();
        }

        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };
        watcher.Changed += (_, _) => changed();
        watcher.Created += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed record ModuleLink(long ButtonId, string ModuleName, string GameAction, string ActionLabel, BindStatus Status, string KeyText, string Detail);

/// <summary>
/// Keeps modules linked to a game action on the key the player really has. Only ever reads the game folder.
/// </summary>
public sealed class ControlSync
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly DeckSession _session;
    private readonly GameInstallLocator _locator;
    private readonly GameActionCatalog _catalog;
    private readonly IFileWatch _watch;
    private readonly IUiScheduler _ui;
    private readonly IDebugLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private IDisposable? _watcher;
    private IDisposable? _debounce;
    private string? _watchedPath;
    private bool _hasGoodRead;
    private bool _running;
    private bool _again;

    public ControlSync(DeckSession session, GameInstallLocator locator, GameActionCatalog catalog, IFileWatch watch, IUiScheduler ui, IDebugLog log, Func<DateTimeOffset> clock)
    {
        _session = session;
        _locator = locator;
        _catalog = catalog;
        _watch = watch;
        _ui = ui;
        _log = log;
        _clock = clock;
        _session.Changed += (_, _) => Pending = SyncAsync();
    }

    public GameInstall? Install { get; private set; }
    public string FileStatus { get; private set; } = "Sin leer";
    public IReadOnlyList<GameRebind> Rebinds { get; private set; } = [];
    public IReadOnlyList<ModuleLink> Links { get; private set; } = [];

    /// <summary>The last sync started by a session or file change, so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public event EventHandler? Changed;

    public BindStatus? StatusOf(long buttonId) => Links.FirstOrDefault(l => l.ButtonId == buttonId)?.Status;

    public async Task SyncAsync()
    {
        // Saving a module raises a session change, which asks for another sync: run it after this one.
        if (_running)
        {
            _again = true;
            return;
        }

        _running = true;
        try
        {
            do
            {
                _again = false;
                await SyncCoreAsync();
            }
            while (_again);
        }
        catch (Exception ex)
        {
            _log.Write($"Controls sync failed: {ex}");
            FileStatus = $"No se pudo sincronizar: {ex.Message}";
        }
        finally
        {
            _running = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SyncCoreAsync()
    {
        var configured = _session.Settings.GameFolder;
        Install = _locator.Locate(configured);
        WatchFile(Install?.ActionMapsPath);

        var canApply = false;
        if (Install is null)
        {
            FileStatus = string.IsNullOrWhiteSpace(configured)
                ? "No se ha encontrado Star Citizen en este equipo."
                : "La carpeta indicada no es una carpeta de Star Citizen (LIVE).";
        }
        else if (!File.Exists(Install.ActionMapsPath))
        {
            // The game only writes the file once the player changes a bind.
            Rebinds = [];
            _hasGoodRead = true;
            canApply = true;
            FileStatus = "Sin rebinds: el juego aun no ha creado actionmaps.xml.";
        }
        else
        {
            var file = ActionMapsReader.Read(Install.ActionMapsPath);
            if (file.Ok)
            {
                Rebinds = file.Rebinds;
                _hasGoodRead = true;
                canApply = true;
                FileStatus = $"Leido a las {_clock():HH:mm:ss}";
            }
            else
            {
                // Probably caught mid-write: keep what was read before and wait for the next change.
                FileStatus = file.Error ?? "No se pudo leer actionmaps.xml.";
            }
        }

        var links = new List<ModuleLink>();
        var updates = new List<DeckButton>();
        foreach (var button in _session.Buttons.Where(b => !string.IsNullOrWhiteSpace(b.GameAction)))
        {
            var resolution = BindingResolver.Resolve(button.GameAction, _catalog, Rebinds);
            var usable = _hasGoodRead && resolution.Action is not null;
            var effective = usable ? resolution.Action! : button.Action;
            links.Add(new ModuleLink(
                button.Id,
                button.Name,
                button.GameAction,
                _catalog.Find(button.GameAction)?.Label ?? button.GameAction,
                resolution.Status,
                ActionText.Display(effective),
                resolution.Detail));

            if (canApply && resolution.Action is not null && !SameAction(button.Action, resolution.Action))
            {
                updates.Add(button with { Action = resolution.Action });
            }
        }

        Links = links;
        if (updates.Count > 0)
        {
            _log.Write($"Controls sync updated {updates.Count} module(s): {string.Join(", ", updates.Select(u => $"{u.Name} => {ActionText.Display(u.Action)}"))}");
            await _session.SaveButtonsAsync(updates);
        }
    }

    private void WatchFile(string? path)
    {
        if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _watcher?.Dispose();
        _watchedPath = path;
        _watcher = path is null ? null : _watch.Watch(path, () => _ui.Post(OnFileChanged));
    }

    // The game writes the file in several steps; wait until it has been quiet for a moment.
    private void OnFileChanged()
    {
        _debounce?.Dispose();
        _debounce = _ui.After(Debounce, () => Pending = SyncAsync());
    }

    private static bool SameAction(KeyPressAction a, KeyPressAction b)
    {
        return a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase)
            && a.Modifiers.SequenceEqual(b.Modifiers, StringComparer.OrdinalIgnoreCase)
            && a.PressDurationMs == b.PressDurationMs;
    }
}
