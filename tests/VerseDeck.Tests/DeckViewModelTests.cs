using System.Globalization;

namespace VerseDeck.Tests;

public class DeckViewModelTests
{
    [Fact]
    public async Task Groups_FollowCategoryOrder_AndSkipEmpty()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(
            ["Flight", "Navigation", "Scan", "Combat", "Utility", "Emergency", "Systems"],
            h.Shell.Deck.Groups.Select(g => g.Category));
        Assert.Equal(16, h.Shell.Deck.Groups.Sum(g => g.Tiles.Count));
    }

    [Fact]
    public async Task Groups_UnknownCategories_ComeLast_Alphabetically()
    {
        await using var h = await Harness.CreateAsync();
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");
        await h.Session.SaveButtonAsync(lights with { Category = "Zeta" });
        await h.Session.SaveButtonAsync(h.Session.Buttons.First(b => b.Name == "Cargo") with { Category = "Alfa" });

        Assert.Equal(["Alfa", "Zeta"], h.Shell.Deck.Groups.Select(g => g.Category).TakeLast(2));
    }

    [Fact]
    public async Task Tile_ShowsKeyWithModifiers_AndFirstPhrases()
    {
        await using var h = await Harness.CreateAsync();

        var tile = h.Tile("Flight Ready");

        Assert.Equal("Alt+R", tile.KeyText);
        Assert.Equal("Accent", tile.AccentKey);
        Assert.Equal("Danger", h.Tile("Eject").AccentKey);
        Assert.Equal("Positive", h.Tile("Scan Mode").AccentKey);
        Assert.False(string.IsNullOrWhiteSpace(tile.VoiceSummary));
    }

    [Fact]
    public async Task Press_InNormalMode_ExecutesButton_AndFlashesTile()
    {
        await using var h = await Harness.CreateAsync();
        var tile = h.Tile("Lights");

        await h.Shell.Deck.PressCommand.ExecuteAsync(tile);

        Assert.Equal("L", Assert.Single(h.Sender.Sent).Key);
        Assert.True(h.Tile("Lights").JustSent);
        Assert.False(h.Shell.StatusIsError);

        h.Ui.Fire(TimeSpan.FromMilliseconds(400));
        Assert.False(h.Tile("Lights").JustSent);
    }

    [Fact]
    public async Task Press_InEditMode_SelectsWithoutExecuting()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));

        Assert.Empty(h.Sender.Sent);
        Assert.True(h.Tile("Lights").IsSelected);
        Assert.Equal("Lights", h.Shell.Editor.Name);
    }

    [Fact]
    public async Task Press_SenderFails_ReportsErrorInStatus()
    {
        await using var h = await Harness.CreateAsync();
        h.Sender.ThrowOnSend = true;

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));

        Assert.True(h.Shell.StatusIsError);
        Assert.Contains("send failed", h.Shell.StatusText);
    }

    [Fact]
    public async Task SessionChanged_RebuildsGroups_KeepingSelection()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));

        await h.Session.SaveButtonAsync(h.Session.Buttons.First(b => b.Name == "Cargo") with { Name = "Bodega" });

        Assert.True(h.Tile("Lights").IsSelected);
        Assert.Equal("Bodega", h.Tile("Bodega").Name);
    }

    [Fact]
    public async Task InitialSelection_IsLandingGear()
    {
        await using var h = await Harness.CreateAsync();

        Assert.True(h.Tile("Landing Gear").IsSelected);
        Assert.Equal("Landing Gear", h.Shell.Editor.Name);
    }
}

public class ModuleEditorViewModelTests
{
    private static async Task<Harness> EditingAsync(string name)
    {
        var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(name));
        return h;
    }

    [Theory]
    [InlineData("ñ")]
    [InlineData("F99")]
    [InlineData("")]
    public async Task Save_UnsupportedKey_ReportsError_AndDoesNotPersist(string key)
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.Key = key;

        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("L", h.Session.Buttons.First(b => b.Name == "Lights").Action.Key);
    }

    [Fact]
    public async Task Save_UnsupportedModifier_ReportsError()
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.Modifiers = "Ctrl, Hyper";

        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Empty(h.Session.Buttons.First(b => b.Name == "Lights").Action.Modifiers);
    }

    [Fact]
    public async Task Save_EmptyName_ReportsError()
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.Name = "  ";

        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Contains(h.Session.Buttons, b => b.Name == "Lights");
    }

    [Fact]
    public async Task Save_PersistsEveryField()
    {
        await using var h = await EditingAsync("Lights");
        var editor = h.Shell.Editor;
        editor.Name = "Luces";
        editor.Category = "Combat";
        editor.Icon = "radar";
        editor.Frame = "Cyan";
        editor.Key = "f5";
        editor.Modifiers = "Ctrl, Shift";
        editor.RequiresConfirmation = true;

        await editor.SaveCommand.ExecuteAsync(null);

        var saved = h.Session.Buttons.First(b => b.Name == "Luces");
        Assert.Equal("Combat", saved.Category);
        Assert.Equal("radar", saved.Icon);
        Assert.Equal("f5", saved.Action.Key);
        Assert.Equal(["Ctrl", "Shift"], saved.Action.Modifiers);
        Assert.True(saved.RequiresConfirmation);
        Assert.Equal("Accent", h.Tile("Luces").AccentKey);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Create_DefaultsKeyToF13_WhenEmpty_AndSelectsNewModule()
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.Name = "Minar";
        h.Shell.Editor.Key = "";

        await h.Shell.Editor.CreateCommand.ExecuteAsync(null);

        Assert.Equal(17, h.Session.Buttons.Count);
        Assert.Equal("F13", h.Session.Buttons.First(b => b.Name == "Minar").Action.Key);
        Assert.Contains(h.Session.Buttons, b => b.Name == "Lights");
        Assert.True(h.Tile("Minar").IsSelected);
    }

    [Fact]
    public async Task Delete_Declined_KeepsModule()
    {
        await using var h = await EditingAsync("Lights");
        h.Dialogs.Answer = false;

        await h.Shell.Editor.DeleteCommand.ExecuteAsync(null);

        Assert.Contains(h.Session.Buttons, b => b.Name == "Lights");
    }

    [Fact]
    public async Task Delete_Confirmed_RemovesModuleAndPhrases()
    {
        await using var h = await EditingAsync("Lights");
        var id = h.Session.Buttons.First(b => b.Name == "Lights").Id;

        await h.Shell.Editor.DeleteCommand.ExecuteAsync(null);

        Assert.DoesNotContain(h.Session.Buttons, b => b.Name == "Lights");
        Assert.DoesNotContain(await h.Repository.GetVoiceCommandsAsync(), v => v.ButtonId == id);
    }

    [Theory]
    [InlineData("5", 0.98)]
    [InlineData("0", 0.10)]
    [InlineData("0.55", 0.55)]
    public async Task AddPhrase_ClampsConfidence_Between010And098(string text, double expected)
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.NewPhrase = "dame luz";
        h.Shell.Editor.Confidence = text;

        await h.Shell.Editor.AddPhraseCommand.ExecuteAsync(null);

        Assert.Equal(expected, h.Session.VoiceCommands.Single(v => v.Phrase == "dame luz").MinimumConfidence, 3);
    }

    [Fact]
    public async Task AddPhrase_ParsesCommaDecimal_UnderSpanishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            await using var h = await EditingAsync("Lights");
            h.Shell.Editor.NewPhrase = "dame luz";
            h.Shell.Editor.Confidence = "0,55";

            await h.Shell.Editor.AddPhraseCommand.ExecuteAsync(null);

            Assert.Equal(0.55, h.Session.VoiceCommands.Single(v => v.Phrase == "dame luz").MinimumConfidence, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task AddPhrase_Empty_ReportsError()
    {
        await using var h = await EditingAsync("Lights");
        h.Shell.Editor.NewPhrase = " ";

        await h.Shell.Editor.AddPhraseCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Phrases_ListTheSelectedModulePhrases()
    {
        await using var h = await EditingAsync("Lights");

        Assert.Contains(h.Shell.Editor.Phrases, p => p.StartsWith("activar luces"));
        Assert.DoesNotContain(h.Shell.Editor.Phrases, p => p.StartsWith("eject"));
    }
}

public class ProfileViewModelTests
{
    [Fact]
    public async Task Save_NewName_CreatesProfile_AndActivatesIt()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Profile.Name = "Combate";
        h.Shell.Profile.ShipName = "Aegis Gladius";

        await h.Shell.Profile.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Combate", h.Session.ActiveProfile!.Name);
        Assert.Equal(2, h.Shell.Profile.Profiles.Count);
        Assert.Equal("Combate", h.Shell.Profile.SelectedProfile!.Name);
        Assert.Equal("COMBATE", h.Shell.ProfileTitle);
    }

    [Theory]
    [InlineData("", "Aegis Gladius")]
    [InlineData("Combate", " ")]
    public async Task Save_EmptyNameOrShip_ReportsError(string name, string ship)
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Profile.Name = name;
        h.Shell.Profile.ShipName = ship;

        await h.Shell.Profile.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Single(h.Session.Profiles);
    }

    [Fact]
    public async Task Load_SwitchesActiveProfile()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Profile.Name = "Combate";
        h.Shell.Profile.ShipName = "Aegis Gladius";
        await h.Shell.Profile.SaveCommand.ExecuteAsync(null);

        h.Shell.Profile.SelectedProfile = h.Shell.Profile.Profiles.First(p => p.Name == "Global");
        await h.Shell.Profile.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Global", h.Session.ActiveProfile!.Name);
        Assert.Equal("Global", h.Shell.Profile.Name);
    }

    [Fact]
    public async Task Activity_ListsRecentCommands_NewestFirst()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        await h.Shell.Activity.RefreshAsync();

        Assert.Contains("Lights", h.Shell.Activity.Entries.First());
    }
}
