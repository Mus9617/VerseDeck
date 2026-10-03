using VerseDeck.App;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Data;
using VerseDeck.Game;
using VerseDeck.Speech;

namespace VerseDeck.Tests;

public class FullCatalogTests
{
    private static readonly GameActionCatalog Catalog = GameActionCatalog.Load();

    [Fact]
    public void EveryDefaultKey_CanBeSent_AsOnePress()
    {
        foreach (var action in Catalog.Actions.Where(a => a.DefaultInput is not null))
        {
            Assert.True(ScInput.Parse($"kb1_{action.DefaultInput}").TryToKeyPress(action.PressMs, out var press), action.Id);
            Assert.InRange(press.PressDurationMs, 1, 2000);
        }
    }

    [Fact]
    public void EveryAction_HasAGroup()
    {
        Assert.All(Catalog.Actions, a => Assert.NotEqual("General", a.Group));
    }

    [Fact]
    public void BothControlTowerRequests_FollowTheSameRebind()
    {
        GameRebind[] rebinds = [new("spaceship_general", "v_atc_request", ScInput.Parse("kb1_ralt+n"), 1)];

        foreach (var id in new[] { "atc_landing", "atc_takeoff", "request_landing" })
        {
            var resolution = BindingResolver.Resolve(id, Catalog, rebinds);
            Assert.Equal(BindStatus.YourKey, resolution.Status);
            Assert.Equal("N", resolution.Action!.Key);
            Assert.Equal(["RAlt"], resolution.Action.Modifiers);
        }
    }

    [Fact]
    public void ControlTowerModules_AreLinkedByTheirPreset()
    {
        Assert.Equal("atc_landing", Catalog.PresetLinkFor("Hangar Request"));
        Assert.Equal("atc_takeoff", Catalog.PresetLinkFor("Takeoff Request"));
    }
}

public class CallsignTests
{
    [Theory]
    [InlineData("Gatac Syulen", "Syulen")]
    [InlineData("Aegis Avenger Titan", "Avenger Titan")]
    [InlineData("Starter Ship", "")]
    [InlineData("Starter Shipyard", "Starter Shipyard")]
    [InlineData("Consolidated Outland Mustang Alpha", "Mustang Alpha")]
    [InlineData("Originals Rock", "Originals Rock")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Callsign_DropsTheMaker(string? ship, string callsign)
    {
        Assert.Equal(callsign, ShipCatalog.Callsign(ship));
    }

    [Fact]
    public void ShipList_IsLarge_AndHasNoRepeats()
    {
        Assert.True(ShipCatalog.Names.Count > 150);
        Assert.Equal(ShipCatalog.Names.Count, ShipCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Gatac Syulen", ShipCatalog.Names);
    }

    [Fact]
    public void ShipPlaceholder_IsFilled_AndHasAFallback()
    {
        var pack = ResponsePack.LoadAll().Single(p => p.Id == "militar");
        var selector = new ResponseSelector(pack, new Random(1));
        var hangar = new DeckButton(1, 1, "Hangar Request", "comms", "#fff", "Flight", new KeyPressAction("N", ["LAlt"], 60), false, true);

        selector.Callsign = "Syulen";
        Assert.All(selector.AllFor([hangar]).Where(p => p.Contains("Control") || p.Contains("Torre")), p => Assert.DoesNotContain("{nave}", p));
        Assert.Contains(selector.AllFor([hangar]), p => p.Contains("Syulen"));

        selector.Callsign = string.Empty;
        Assert.Contains(selector.AllFor([hangar]), p => p.Contains("Nave"));
    }
}

public class ControlTowerTests
{
    [Fact]
    public async Task NewDeck_HasBothRequests_WithTheirPhrases()
    {
        await using var h = await Harness.CreateAsync();

        var hangar = h.Session.Buttons.Single(b => b.Name == "Hangar Request");
        var takeoff = h.Session.Buttons.Single(b => b.Name == "Takeoff Request");
        Assert.Equal("LAlt+N", ActionText.Display(hangar.Action));
        Assert.Equal(string.Empty, hangar.GameAction);
        Assert.Contains(h.Session.VoiceCommands, v => v.ButtonId == hangar.Id && v.Phrase == "pedir hangar");
        Assert.Contains(h.Session.VoiceCommands, v => v.ButtonId == takeoff.Id && v.Phrase == "pedir despegue");
    }

    [Fact]
    public async Task OldDatabase_GetsThemOnce_AndDeletedOnesStayDeleted()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var profile = (await repository.GetProfilesAsync()).Single(p => p.IsActive);
        var hangar = (await repository.GetButtonsAsync(profile.Id)).Single(b => b.Name == "Hangar Request");
        await repository.DeleteButtonAsync(hangar.Id);

        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();

        var names = (await again.GetButtonsAsync(profile.Id)).Select(b => b.Name).ToList();
        Assert.DoesNotContain("Hangar Request", names);
        Assert.Single(names, n => n == "Takeoff Request");
    }

    [Fact]
    public async Task DatabaseFromBeforeTheTower_GetsBothModules()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var profile = (await repository.GetProfilesAsync()).Single(p => p.IsActive);
        foreach (var button in (await repository.GetButtonsAsync(profile.Id)).Where(b => b.Name is "Hangar Request" or "Takeoff Request"))
        {
            await repository.DeleteButtonAsync(button.Id);
        }

        await db.ExecuteAsync("DELETE FROM Settings WHERE Key = 'AtcModulesV1';");
        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();

        var names = (await again.GetButtonsAsync(profile.Id)).Select(b => b.Name).ToList();
        Assert.Single(names, n => n == "Hangar Request");
        Assert.Single(names, n => n == "Takeoff Request");
    }

    [Fact]
    public async Task AskingForAHangar_SendsOnePress_AndTheTowerAnswersWithTheShip()
    {
        await using var h = await Harness.CreateAsync();
        await h.Session.SaveProfileAsync(h.Session.ActiveProfile!.Name, "Gatac Syulen");
        await h.EnableCopilotAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotPack = "militar" });
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var hangar = h.Session.Buttons.Single(b => b.Name == "Hangar Request");

        h.Voice.Raise(h.Session.VoiceCommands.First(v => v.ButtonId == hangar.Id), hangar);
        await h.Shell.Voice.Pending;
        await h.Copilot.Pending;

        var press = Assert.Single(h.Sender.Sent);
        Assert.Equal("N", press.Key);
        Assert.Contains(h.Log.Lines, l => l.Contains("Copilot said") && l.Contains("Syulen"));
    }
}

public class AnimationTests
{
    [Fact]
    public async Task Neon_BreathesOnlyWhileTheWindowIsInFront_AndTheSettingIsOn()
    {
        await using var h = await Harness.CreateAsync();

        // Opened behind the game: nothing moves until the window is really in front.
        Assert.False(h.Shell.AnimationsActive);
        h.Shell.IsWindowActive = true;
        Assert.True(h.Shell.AnimationsActive);

        h.Shell.IsWindowActive = false;
        Assert.False(h.Shell.AnimationsActive);

        h.Shell.IsWindowActive = true;
        h.Shell.IsMinimized = true;
        Assert.False(h.Shell.AnimationsActive);

        h.Shell.IsMinimized = false;
        h.Shell.Settings.Animations = false;
        await h.Shell.Settings.Pending;
        Assert.False(h.Shell.AnimationsActive);
        Assert.False(h.Session.Settings.Animations);
    }
}

public class HeardBlinkTests
{
    [Fact]
    public async Task HeaderBlinks_WhenSomethingIsHeard_ThenSettles()
    {
        await using var h = await Harness.CreateAsync();
        Assert.False(h.Shell.Voice.JustHeard);

        h.Voice.RaiseHeard("vamos alla", RecognitionOutcome.Discarded);

        Assert.True(h.Shell.Voice.JustHeard);
        Assert.Equal(1, h.Ui.Fire(TimeSpan.FromMilliseconds(600)));
        Assert.False(h.Shell.Voice.JustHeard);
    }
}

public class PuenteReviewTests
{
    [Fact]
    public async Task OnePresetLinkedAlready_TheOtherIsStillAdded_InEveryProfile()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var global = (await repository.GetProfilesAsync()).Single(p => p.IsActive);
        var second = await repository.SaveProfileAsync(new Profile(0, "Carga", "MISC Hull C", "Carga", false));
        foreach (var button in (await repository.GetButtonsAsync(global.Id)).Where(b => b.Name is "Hangar Request" or "Takeoff Request"))
        {
            await repository.DeleteButtonAsync(button.Id);
        }

        // The player had linked a module of their own to the hangar request.
        await repository.SaveButtonAsync(new DeckButton(0, global.Id, "Torre", "comms", "#fff", "Flight", new KeyPressAction("N", ["LAlt"], 60), false, true, "atc_landing"));
        await db.ExecuteAsync("DELETE FROM Settings WHERE Key = 'AtcModulesV1';");

        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();

        var globalNames = (await again.GetButtonsAsync(global.Id)).Select(b => b.Name).ToList();
        Assert.DoesNotContain("Hangar Request", globalNames);
        Assert.Contains("Takeoff Request", globalNames);
        var secondNames = (await again.GetButtonsAsync(second.Id)).Select(b => b.Name).ToList();
        Assert.Contains("Hangar Request", secondNames);
        Assert.Contains("Takeoff Request", secondNames);
    }

    [Fact]
    public async Task ExampleChecklists_AskTheTowerThroughItsModules()
    {
        await using var h = await Harness.CreateAsync();
        var hangar = h.Session.Buttons.Single(b => b.Name == "Hangar Request");
        var takeoff = h.Session.Buttons.Single(b => b.Name == "Takeoff Request");

        Assert.Equal(hangar.Id, h.Session.Checklists.Single(c => c.Name == "Aterrizaje").Steps[0].ButtonId);
        Assert.Equal(takeoff.Id, h.Session.Checklists.Single(c => c.Name == "Prevuelo").Steps[2].ButtonId);
    }

    [Fact]
    public async Task GivingTheStarterProfileAShip_RendersTheTowerPhrasesAhead()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotPack = "militar" });
        await h.Copilot.WarmUpAsync();
        Assert.DoesNotContain(h.Tts.Synthesized, t => t.Contains("Syulen"));

        await h.Session.SaveProfileAsync(h.Session.ActiveProfile!.Name, "Gatac Syulen");
        for (var i = 0; i < 50 && !h.Tts.Synthesized.Any(t => t.Contains("Syulen")); i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains(h.Tts.Synthesized, t => t.Contains("aquí Syulen"));
    }
}

public class TowerDialogueTests
{
    private static async Task<(Harness H, DeckButton Button)> TowerAsync(string module)
    {
        var h = await Harness.CreateAsync();
        await h.Session.SaveProfileAsync(h.Session.ActiveProfile!.Name, "Gatac Syulen");
        await h.EnableCopilotAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotPack = "militar" });
        h.Tts.Seconds = 0.2;
        return (h, h.Session.Buttons.Single(b => b.Name == module));
    }

    [Fact]
    public async Task TakeoffRequest_TheCopilotAsks_ThenTheTowerAnswersOverTheRadio()
    {
        var (h, takeoff) = await TowerAsync("Takeoff Request");
        await using var _ = h;

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Takeoff Request"));
        await h.Copilot.Pending;

        Assert.Single(h.Sender.Sent);
        var said = h.Log.Lines.Where(l => l.Contains(" said '")).ToList();
        Assert.Equal(2, said.Count);
        Assert.Contains("Copilot said", said[0]);
        Assert.Contains("Tower said", said[1]);
        Assert.Contains("Syulen", said[1]);
        Assert.Contains("salida", said[1]);
        Assert.Equal(2, h.Player.Played.Select(p => p.Path).Distinct().Count());
    }

    [Fact]
    public async Task AnotherOrderInBetween_SilencesTheTower()
    {
        var (h, hangar) = await TowerAsync("Hangar Request");
        await using var _ = h;

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Hangar Request"));
        var dialogue = h.Copilot.Pending;
        await Task.Delay(50);
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        await dialogue;
        await h.Copilot.Pending;

        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("Tower said"));
    }

    [Fact]
    public async Task OtherModules_GetNoTower()
    {
        var (h, _) = await TowerAsync("Hangar Request");
        await using var __ = h;

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        await h.Copilot.Pending;

        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("Tower said"));
    }

    [Fact]
    public async Task WarmUp_RendersTheTowerAnswersToo()
    {
        var (h, _) = await TowerAsync("Hangar Request");
        await using var __ = h;

        await h.Copilot.WarmUpAsync();

        Assert.Contains(h.Tts.Synthesized, t => t.StartsWith("Recibido, Syulen"));
    }
}

public class RadioEffectTests
{
    [Fact]
    public void Radio_AddsSquelch_StaysInRange_AndIsRepeatable()
    {
        var samples = Enumerable.Range(0, 22050).Select(i => (float)Math.Sin(i * 2 * Math.PI * 440 / 22050) * 0.9f).ToArray();
        var audio = new SpeechAudio(samples, 22050);

        var radio = RadioEffect.Apply(audio);

        Assert.True(radio.Samples.Length > samples.Length);
        Assert.All(radio.Samples, s => Assert.InRange(s, -1f, 1f));
        Assert.Equal(radio.Samples, RadioEffect.Apply(audio).Samples);
    }
}

public class HoldTests
{
    [Fact]
    public async Task QuantumMode_HoldsTheKey_OnANewDeck()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(1000, h.Session.Buttons.Single(b => b.Name == "Quantum Mode").Action.PressDurationMs);
    }

    [Fact]
    public async Task OldQuantumTap_IsUpgraded_ButAChangedOneIsLeftAlone()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        await db.ExecuteAsync("UPDATE Buttons SET PressDurationMs=60 WHERE Name='Quantum Mode'; UPDATE Buttons SET PressDurationMs=60, ActionKey='F9' WHERE Name='Star Map'; DELETE FROM Settings WHERE Key='AtcPhrasesV2';");

        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();
        var profile = (await again.GetProfilesAsync()).Single(p => p.IsActive);
        var buttons = await again.GetButtonsAsync(profile.Id);

        Assert.Equal(1000, buttons.Single(b => b.Name == "Quantum Mode").Action.PressDurationMs);
        Assert.Equal(60, buttons.Single(b => b.Name == "Star Map").Action.PressDurationMs);
    }

    [Fact]
    public async Task OldTowerModules_GetTheNewPhrases_Once()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        await db.ExecuteAsync("DELETE FROM VoiceCommands WHERE Phrase IN ('salida de hangar', 'abrir hangar'); DELETE FROM Settings WHERE Key='AtcPhrasesV2';");

        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();
        await again.InitializeAsync();

        var phrases = (await again.GetVoiceCommandsAsync()).Select(v => v.Phrase).ToList();
        Assert.Single(phrases, p => p == "salida de hangar");
        Assert.Single(phrases, p => p == "abrir hangar");
    }

    [Fact]
    public async Task Editor_SavesTheChosenHold()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Star Map"));
        var editor = h.Shell.Editor;

        editor.Hold = VerseDeck.App.ViewModels.ModuleEditorViewModel.HoldChoices.Single(c => c.Ms == 1500);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1500, h.Session.Buttons.Single(b => b.Name == "Star Map").Action.PressDurationMs);
    }
}
