using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Game;

namespace VerseDeck.Tests;

public sealed class FakeFileWatch : IFileWatch
{
    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private Action? _changed;

    public string? WatchedPath { get; private set; }
    public int WatchCount { get; private set; }

    public IDisposable Watch(string path, Action changed)
    {
        WatchedPath = path;
        WatchCount++;
        _changed = changed;
        return new Handle(() => _changed = null);
    }

    public void Trigger() => _changed?.Invoke();
}

/// <summary>A throwaway Star Citizen channel folder containing only what VerseDeck reads.</summary>
public sealed class FakeGameFolder : IDisposable
{
    public FakeGameFolder(bool withActionMaps = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"versedeck-game-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(ActionMapsPath)!);
        if (withActionMaps)
        {
            File.Copy(Fixture.RealActionMaps, ActionMapsPath);
        }
    }

    public string Root { get; }
    public string ActionMapsPath => Path.Combine(Root, "user", "client", "0", "Profiles", "default", "actionmaps.xml");

    public void Write(string xml) => File.WriteAllText(ActionMapsPath, xml);

    public string Snapshot()
    {
        return string.Join("|", Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p)
            .Select(p => File.Exists(p) ? $"{p}:{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : p));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed class ControlSyncTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private readonly FakeGameFolder _game = new();
    private readonly FakeFileWatch _watch = new();
    private readonly FakeScheduler _ui = new();
    private DeckSession _session = null!;
    private ControlSync _sync = null!;

    public async Task InitializeAsync()
    {
        _session = new DeckSession(await _db.CreateAsync());
        await _session.LoadAsync();
        await _session.SaveSettingsAsync(_session.Settings with { GameFolder = _game.Root });
        _sync = CreateSync();
    }

    public async Task DisposeAsync()
    {
        _game.Dispose();
        await _db.DisposeAsync();
    }

    private ControlSync CreateSync()
    {
        var locator = new GameInstallLocator(Path.Combine(_game.Root, "no-launcher-logs"), []);
        return new ControlSync(_session, locator, GameActionCatalog.Load(), _watch, _ui, new FakeLog(), () => new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    }

    private DeckButton Button(string name) => _session.Buttons.First(b => b.Name == name);

    private static string KeyOf(DeckButton button) => ActionText.Display(button.Action);

    private async Task LinkAsync(string module, string gameAction)
    {
        await _session.SaveButtonAsync(Button(module) with { GameAction = gameAction });
        await _sync.Pending;
    }

    [Fact]
    public async Task Sync_AppliesResolvedKey_ToLinkedModule()
    {
        Assert.Equal("Alt+R", KeyOf(Button("Flight Ready")));

        await LinkAsync("Flight Ready", "flight_ready");
        await _sync.SyncAsync();

        Assert.Equal("RAlt+R", KeyOf(Button("Flight Ready")));
        Assert.Equal(BindStatus.Default, _sync.StatusOf(Button("Flight Ready").Id));
    }

    [Fact]
    public async Task Sync_UsesThePlayersRebind()
    {
        await LinkAsync("Landing Gear", "autoland");

        Assert.Equal("LCtrl+N", KeyOf(Button("Landing Gear")));
        Assert.Equal(1000, Button("Landing Gear").Action.PressDurationMs);
        Assert.Equal(BindStatus.YourKey, _sync.StatusOf(Button("Landing Gear").Id));
    }

    [Fact]
    public async Task Sync_LeavesManualModulesUntouched()
    {
        await LinkAsync("Flight Ready", "flight_ready");

        Assert.Equal("L", KeyOf(Button("Lights")));
        Assert.Equal("K", KeyOf(Button("Doors")));
        Assert.Null(_sync.StatusOf(Button("Lights").Id));
        Assert.Single(_sync.Links);
    }

    [Fact]
    public async Task Sync_NoKeyOrNotSendable_KeepsPreviousKey_AndReportsStatus()
    {
        _game.Write(Fixture.ActionMapsXml(Fixture.Map("spaceship_movement", ("v_toggle_landing_system", "kb1_mouse4"))));

        await LinkAsync("Doors", "doors_toggle");
        await LinkAsync("Landing Gear", "landing_gear");

        Assert.Equal("K", KeyOf(Button("Doors")));
        Assert.Equal(BindStatus.NoKey, _sync.StatusOf(Button("Doors").Id));
        Assert.Equal("N", KeyOf(Button("Landing Gear")));
        Assert.Equal(BindStatus.NotSendable, _sync.StatusOf(Button("Landing Gear").Id));
    }

    [Fact]
    public async Task Sync_NothingChanged_DoesNotRaiseSessionChanged()
    {
        await LinkAsync("Flight Ready", "flight_ready");
        var raised = 0;
        _session.Changed += (_, _) => raised++;

        await _sync.SyncAsync();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Sync_SeveralModules_ReloadsSessionOnce()
    {
        // Link without the service listening, so the first sync has three modules to update.
        var repository = await _db.CreateAsync();
        await repository.SaveButtonAsync(Button("Flight Ready") with { GameAction = "flight_ready" });
        await repository.SaveButtonAsync(Button("Eject") with { GameAction = "eject" });
        await repository.SaveButtonAsync(Button("Self Destruct") with { GameAction = "self_destruct" });
        var session = new DeckSession(repository);
        await session.LoadAsync();
        var sync = new ControlSync(session, new GameInstallLocator("none", []), GameActionCatalog.Load(), _watch, _ui, new FakeLog(), () => DateTimeOffset.Now);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        await sync.SyncAsync();

        Assert.Equal(1, raised);
        Assert.Equal("RAlt+Y", ActionText.Display(session.Buttons.First(b => b.Name == "Eject").Action));
    }

    [Fact]
    public async Task Sync_AppliesLongPressDuration()
    {
        await LinkAsync("Self Destruct", "self_destruct");

        Assert.Equal("BACKSPACE", KeyOf(Button("Self Destruct")));
        Assert.Equal(2000, Button("Self Destruct").Action.PressDurationMs);
    }

    [Fact]
    public async Task Sync_NoInstall_ReportsIt_AndTouchesNothing()
    {
        await _session.SaveSettingsAsync(_session.Settings with { GameFolder = "" });
        await _sync.Pending;

        await LinkAsync("Flight Ready", "flight_ready");
        await _sync.SyncAsync();

        Assert.Null(_sync.Install);
        Assert.Contains("No se ha encontrado", _sync.FileStatus);
        Assert.Equal("Alt+R", KeyOf(Button("Flight Ready")));
    }

    [Fact]
    public async Task Sync_ConfiguredFolderIsNotAGameFolder_ReportsIt_AndTouchesNothing()
    {
        await _session.SaveSettingsAsync(_session.Settings with { GameFolder = Path.GetTempPath() });
        await _sync.Pending;

        await LinkAsync("Flight Ready", "flight_ready");

        Assert.Null(_sync.Install);
        Assert.Contains("carpeta", _sync.FileStatus);
        Assert.Equal("Alt+R", KeyOf(Button("Flight Ready")));
    }

    [Fact]
    public async Task Sync_InstallWithoutActionMapsFile_UsesDefaults()
    {
        File.Delete(_game.ActionMapsPath);

        await LinkAsync("Flight Ready", "flight_ready");

        Assert.Equal("RAlt+R", KeyOf(Button("Flight Ready")));
        Assert.Empty(_sync.Rebinds);
    }

    [Fact]
    public async Task Sync_HalfWrittenFile_KeepsLastGoodRebinds_AndModules()
    {
        await LinkAsync("Landing Gear", "autoland");
        _game.Write("<ActionMaps><ActionProfiles version=\"1\"><actionmap name=\"spaceship_mov");

        await _sync.SyncAsync();

        Assert.Equal(5, _sync.Rebinds.Count);
        Assert.Equal("LCtrl+N", KeyOf(Button("Landing Gear")));
        Assert.Contains("incompleto", _sync.FileStatus);
        Assert.Equal(BindStatus.YourKey, _sync.StatusOf(Button("Landing Gear").Id));
    }

    [Fact]
    public async Task Sync_UnreadableFileOnFirstRead_AppliesNothing()
    {
        _game.Write("<ActionMaps><Act");

        await LinkAsync("Flight Ready", "flight_ready");

        Assert.Equal("Alt+R", KeyOf(Button("Flight Ready")));
    }

    [Fact]
    public async Task FileChange_SyncsAfterDebounce_Once_ForBurstOfEvents()
    {
        await LinkAsync("Landing Gear", "autoland");
        Assert.Equal(_game.ActionMapsPath, _watch.WatchedPath);
        _game.Write(Fixture.ActionMapsXml(Fixture.Map("spaceship_movement", ("v_autoland", "kb1_f9"))));

        _watch.Trigger();
        _watch.Trigger();
        _watch.Trigger();
        Assert.Equal("LCtrl+N", KeyOf(Button("Landing Gear")));

        Assert.Equal(1, _ui.Fire(ControlSync.Debounce));
        await _sync.Pending;

        Assert.Equal("F9", KeyOf(Button("Landing Gear")));
    }

    [Fact]
    public async Task Sync_WatchesTheFileOnce_AcrossRepeatedSyncs()
    {
        await _sync.SyncAsync();
        await _sync.SyncAsync();

        Assert.Equal(1, _watch.WatchCount);
    }

    [Fact]
    public async Task Sync_NeverWritesUnderTheGameFolder()
    {
        var before = _game.Snapshot();

        await LinkAsync("Flight Ready", "flight_ready");
        await LinkAsync("Landing Gear", "autoland");
        await _sync.SyncAsync();

        Assert.Equal(before, _game.Snapshot());
    }

    [Fact]
    public async Task Links_ListEveryLinkedModule_WithLabelAndKeyText()
    {
        await LinkAsync("Flight Ready", "flight_ready");
        await LinkAsync("Lights", "spaceship_movement/v_toggle_relative_mouse_mode");

        var flight = _sync.Links.Single(l => l.ModuleName == "Flight Ready");
        var custom = _sync.Links.Single(l => l.ModuleName == "Lights");
        Assert.Equal("Preparar vuelo", flight.ActionLabel);
        Assert.Equal("RAlt+R", flight.KeyText);
        Assert.Equal("spaceship_movement/v_toggle_relative_mouse_mode", custom.ActionLabel);
        Assert.Equal("COMMA", custom.KeyText);
        Assert.Equal(BindStatus.YourKey, custom.Status);
    }

    [Fact]
    public async Task ProfileSwitch_RecomputesLinksForTheNewProfile()
    {
        await LinkAsync("Flight Ready", "flight_ready");
        var global = _session.ActiveProfile!.Id;
        await _session.SaveProfileAsync("Combate", "Aegis Gladius");
        await _sync.Pending;
        Assert.Single(_sync.Links);

        await _session.SaveButtonAsync(Button("Flight Ready") with { GameAction = "" });
        await _sync.Pending;
        Assert.Empty(_sync.Links);

        await _session.ActivateProfileAsync(global);
        await _sync.Pending;
        Assert.Single(_sync.Links);
    }
}
