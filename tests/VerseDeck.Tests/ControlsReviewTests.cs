using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Game;
using VerseDeck.Input;

namespace VerseDeck.Tests;

/// <summary>Cases found by the branch review: hostile input, shutdown, watcher recovery and relinking.</summary>
public class ControlsReviewTests
{
    private static readonly GameActionCatalog Catalog = GameActionCatalog.Load();

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"versedeck-review-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("kb1_+")]
    [InlineData("kb1_ + ")]
    [InlineData("kb1_++")]
    [InlineData("js1_+")]
    [InlineData("kb99999999999_x")]
    [InlineData("kb\u0661_x")]
    public void ScInput_OddInput_NeverThrows_AndIsNotSendable(string raw)
    {
        var input = ScInput.Parse(raw);

        Assert.False(input.TryToKeyPress(60, out _));
    }

    [Theory]
    [InlineData("kb1_mouse_left")]
    [InlineData("kb1_vk_5b")]
    [InlineData("kb1_lctrl+vk_5b")]
    [InlineData("kb1_vk_0")]
    [InlineData("kb1_lalt+lalt+n")]
    public void ScInput_NamesThatAreNotOrdinaryKeys_AreNotSendable(string raw)
    {
        Assert.False(ScInput.Parse(raw).TryToKeyPress(60, out _));
    }

    [Fact]
    public void Reader_OneOddRebind_DoesNotHideTheOthers()
    {
        var path = TempFile(Fixture.ActionMapsXml(Fixture.Map("spaceship_general", ("v_flightready", "kb1_f9"), ("v_other", "js1_+"))));
        try
        {
            var file = ActionMapsReader.Read(path);

            Assert.True(file.Ok, file.Error);
            Assert.Equal(2, file.Rebinds.Count);
            Assert.Equal("F9", file.Rebinds[0].Input.Key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reader_DocumentWithDtd_IsRejected()
    {
        var path = TempFile("<!DOCTYPE ActionMaps [<!ENTITY x \"kb1_f9\">]>" + Fixture.ActionMapsXml(Fixture.Map("m", ("a", "&x;"))));
        try
        {
            Assert.False(ActionMapsReader.Read(path).Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolver_SameActionNameInTwoMaps_PrefersTheCatalogMap()
    {
        GameRebind[] rebinds =
        [
            new("vehicle_general", "mobiglas", ScInput.Parse("kb1_f5"), 1),
            new("player", "mobiglas", ScInput.Parse("kb1_f6"), 1)
        ];

        Assert.Equal("F6", BindingResolver.Resolve("mobiglas", Catalog, rebinds).Action!.Key);
    }

    [Fact]
    public void Locator_LauncherPathWithParentheses_IsDetected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"versedeck-locator-{Guid.NewGuid():N}");
        try
        {
            var channel = Path.Combine(root, "Program Files (x86)", "RSI", "StarCitizen", "LIVE");
            Directory.CreateDirectory(Path.Combine(channel, "user", "client", "0"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllText(
                Path.Combine(root, "logs", "log.log"),
                $"{{ \"t\":\"x\", \"[main][info] \": \"[Launcher::launch] Launching Star Citizen LIVE from ({channel.Replace(@"\", @"\\")})\"  }},");

            var install = new GameInstallLocator(Path.Combine(root, "logs"), []).Locate(null);

            Assert.Equal(channel, install?.ChannelFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sender_WhenIdle_WaitsUntilTheHeldKeyIsReleased()
    {
        var batches = new List<string>();
        var sender = new WindowsInputSender(strokes =>
        {
            lock (batches)
            {
                batches.Add(strokes[0].KeyUp ? "up" : "down");
            }
        }, virtualKey => virtualKey);

        var press = sender.SendAsync(new KeyPressAction("BACKSPACE", [], 300));
        await sender.WhenIdleAsync();

        Assert.Equal(["down", "up"], batches);
        await press;
    }

    [Fact]
    public async Task Sender_WhenIdle_WithNothingInFlight_ReturnsAtOnce()
    {
        var sender = new WindowsInputSender(_ => { }, virtualKey => virtualKey);

        await sender.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Shutdown_WaitsForInputToDrain()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.ShutdownAsync();

        Assert.Equal(1, h.InputDrains);
    }

    [Fact]
    public void RealFileWatch_MissingFolder_ReturnsNoWatcher()
    {
        var watch = new FileWatch().Watch(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "actionmaps.xml"), () => { }, () => { });

        Assert.Null(watch);
    }

    [Fact]
    public async Task RealFileWatch_ReportsAChangeToTheFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"versedeck-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "actionmaps.xml");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var watch = new FileWatch().Watch(path, () => changed.TrySetResult(), () => { });
            Assert.NotNull(watch);

            await File.WriteAllTextAsync(path, "<ActionMaps/>");

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class ControlSyncReviewTests : IAsyncLifetime
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
        _sync = new ControlSync(_session, new GameInstallLocator("none", []), GameActionCatalog.Load(), _watch, _ui, new FakeLog(), () => DateTimeOffset.Now);
    }

    public async Task DisposeAsync()
    {
        _game.Dispose();
        await _db.DisposeAsync();
    }

    private DeckButton Button(string name) => _session.Buttons.First(b => b.Name == name);

    [Fact]
    public async Task Watcher_ThatCouldNotStart_IsRetriedOnTheNextSync()
    {
        _watch.FailNextWatch = true;

        await _sync.SyncAsync();
        await _sync.SyncAsync();
        await _sync.SyncAsync();

        // The first sync could not watch; the second one did; the third had nothing left to do.
        Assert.Equal(1, _watch.WatchCount);
        Assert.Equal(_game.ActionMapsPath, _watch.WatchedPath);
    }

    [Fact]
    public async Task Watcher_ThatIsLost_IsRecreated()
    {
        await _sync.SyncAsync();

        _watch.Lose();
        _ui.Fire(ControlSync.Debounce);
        await _sync.Pending;

        Assert.Equal(2, _watch.WatchCount);
    }

    [Fact]
    public async Task FileRemovedAfterAGoodRead_KeepsThePlayersKeys()
    {
        await _session.SaveButtonAsync(Button("Landing Gear") with { GameAction = "autoland" });
        await _sync.Pending;
        Assert.Equal("LCtrl+N", ActionText.Display(Button("Landing Gear").Action));

        File.Delete(_game.ActionMapsPath);
        await _sync.SyncAsync();

        Assert.Equal("LCtrl+N", ActionText.Display(Button("Landing Gear").Action));
        Assert.Equal(5, _sync.Rebinds.Count);
    }

    [Fact]
    public async Task OddRebindInTheFile_DoesNotStopTheSync()
    {
        _game.Write(Fixture.ActionMapsXml(Fixture.Map("spaceship_general", ("v_flightready", "kb1_f9"), ("v_other", "js1_+"))));

        await _session.SaveButtonAsync(Button("Flight Ready") with { GameAction = "flight_ready" });
        await _sync.Pending;

        Assert.False(_sync.Failed);
        Assert.Equal("F9", ActionText.Display(Button("Flight Ready").Action));
    }
}

public class ControlsViewModelReviewTests
{
    [Fact]
    public async Task Editor_RelinkingALongPressModule_ToAnActionWithoutKey_DropsTheLongHold()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        Assert.Equal(600, h.Session.Buttons.First(b => b.Name == "Quantum Mode").Action.PressDurationMs);
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Quantum Mode"));
        var editor = h.Shell.Editor;

        editor.SelectedGameAction = editor.GameActionChoices.Single(c => c.Id == "doors_toggle");
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = h.Session.Buttons.First(b => b.Name == "Quantum Mode");
        Assert.Equal("doors_toggle", saved.GameAction);
        Assert.Equal("B", saved.Action.Key);
        Assert.Equal(60, saved.Action.PressDurationMs);
    }

    [Fact]
    public async Task Editor_UnlinkingALongPressModule_ReturnsToAShortPress()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Self Destruct"));
        var editor = h.Shell.Editor;

        editor.SelectedGameAction = editor.GameActionChoices[0];
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(60, h.Session.Buttons.First(b => b.Name == "Self Destruct").Action.PressDurationMs);
    }

    [Fact]
    public async Task Editor_SavingALinkedModuleAgain_KeepsItsLongPress()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Self Destruct"));

        h.Shell.Editor.Name = "Autodestruccion";
        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2000, h.Session.Buttons.First(b => b.Name == "Autodestruccion").Action.PressDurationMs);
    }

    [Theory]
    [InlineData("COMMA")]
    [InlineData("backslash")]
    public async Task PushToTalk_PunctuationName_IsRejected_BecauseItCannotBePolled(string binding)
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Voice.PttBinding = binding;

        await h.Shell.Voice.SaveSettingsCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("F13", h.Session.Settings.PushToTalkBinding);
    }
}
