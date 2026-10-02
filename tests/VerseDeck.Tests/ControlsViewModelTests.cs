using VerseDeck.App.Services;

namespace VerseDeck.Tests;

public class ControlsViewModelTests
{
    private static string KeyOf(Harness h, string module) => ActionText.Display(h.Session.Buttons.First(b => b.Name == module).Action);

    private static async Task EditAsync(Harness h, string module)
    {
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(module));
    }

    [Fact]
    public async Task Initialize_ShowsDetectedFolder_AndRebinds()
    {
        await using var h = await Harness.CreateAsync();
        var controls = h.Shell.Controls;

        Assert.Contains(h.Game!.Root, controls.GameFolderText);
        Assert.Contains("Lanzador", controls.GameFolderText);
        Assert.StartsWith("Leido a las", controls.FileStatus);
        Assert.Equal(5, controls.Rebinds.Count);
        Assert.Contains("4.10", controls.CatalogVersion);
        Assert.Empty(controls.Links);
    }

    [Fact]
    public async Task Rebinds_MarkThoseOutsideTheCatalog()
    {
        await using var h = await Harness.CreateAsync();
        var rebinds = h.Shell.Controls.Rebinds;

        Assert.True(rebinds.Single(r => r.Action == "v_autoland").InCatalog);
        Assert.False(rebinds.Single(r => r.Action == "v_lock_rotation").InCatalog);
        Assert.Equal("kb1_lctrl+n", rebinds.Single(r => r.Action == "v_autoland").Input);
    }

    [Fact]
    public async Task Upgrade_ChangesNoModuleKey_UntilTheUserLinks()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal("Alt+R", KeyOf(h, "Flight Ready"));
        Assert.Equal("K", KeyOf(h, "Doors"));
        Assert.All(h.Session.Buttons, b => Assert.Equal("", b.GameAction));
    }

    [Fact]
    public async Task PresetPreview_ListsThirteenModules_WithKeyChanges()
    {
        await using var h = await Harness.CreateAsync();
        var preview = h.Shell.Controls.PresetPreview;

        Assert.Equal(13, preview.Count);
        Assert.True(h.Shell.Controls.HasPresetPreview);
        var flight = preview.Single(p => p.Module == "Flight Ready");
        Assert.Equal("Alt+R", flight.CurrentKey);
        Assert.Equal("RAlt+R", flight.NewKey);
        Assert.Equal("Preparar vuelo", flight.Action);
        Assert.Equal("sin tecla en el juego", preview.Single(p => p.Module == "Doors").NewKey);
    }

    [Fact]
    public async Task LinkPresets_LinksAndSyncs_AndEmptiesPreview()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        Assert.Equal("RAlt+R", KeyOf(h, "Flight Ready"));
        Assert.Equal("RAlt+Y", KeyOf(h, "Eject"));
        Assert.Equal("BACKSPACE", KeyOf(h, "Self Destruct"));
        Assert.Equal(2000, h.Session.Buttons.First(b => b.Name == "Self Destruct").Action.PressDurationMs);
        Assert.Equal("K", KeyOf(h, "Doors"));
        Assert.Empty(h.Shell.Controls.PresetPreview);
        Assert.False(h.Shell.Controls.HasPresetPreview);
        Assert.Equal(13, h.Shell.Controls.Links.Count);
        Assert.Equal("Danger", h.Shell.Controls.Links.Single(l => l.Module == "Doors").StatusKey);
        Assert.Equal("Positive", h.Shell.Controls.Links.Single(l => l.Module == "Flight Ready").StatusKey);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task LinkPresets_LeavesStarMapCargoCommsManual()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        Assert.All(["Star Map", "Cargo", "Comms"], name => Assert.Equal("", h.Session.Buttons.First(b => b.Name == name).GameAction));
        Assert.Equal("J", KeyOf(h, "Cargo"));
    }

    [Fact]
    public async Task LinkPresets_Twice_DoesNothingTheSecondTime()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        var raised = 0;
        h.Session.Changed += (_, _) => raised++;

        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task LinkedPress_SendsTheGameKey_Once()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Flight Ready"));

        var sent = Assert.Single(h.Sender.Sent);
        Assert.Equal("R", sent.Key);
        Assert.Equal(["RAlt"], sent.Modifiers);
    }

    [Fact]
    public async Task SaveFolder_Invalid_ReportsError_AndKeepsSetting()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Controls.FolderInput = Path.GetTempPath();

        await h.Shell.Controls.SaveFolderCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("", h.Session.Settings.GameFolder);
        Assert.Contains("Lanzador", h.Shell.Controls.GameFolderText);
    }

    [Fact]
    public async Task SaveFolder_Valid_IsUsed_AndEmpty_ReturnsToAutoDetection()
    {
        await using var h = await Harness.CreateAsync();
        using var other = new FakeGameFolder(withActionMaps: false);
        h.Shell.Controls.FolderInput = $"\"{other.Root}\"";

        await h.Shell.Controls.SaveFolderCommand.ExecuteAsync(null);

        Assert.Equal(other.Root, h.Session.Settings.GameFolder);
        Assert.Contains("Ajuste", h.Shell.Controls.GameFolderText);
        Assert.Empty(h.Shell.Controls.Rebinds);

        h.Shell.Controls.FolderInput = "";
        await h.Shell.Controls.SaveFolderCommand.ExecuteAsync(null);

        Assert.Contains("Lanzador", h.Shell.Controls.GameFolderText);
        Assert.Equal(5, h.Shell.Controls.Rebinds.Count);
    }

    [Fact]
    public async Task NoInstall_ShowsMessage_AndDeckStillWorks()
    {
        await using var h = await Harness.CreateAsync(withGame: false);

        Assert.Equal("No detectada", h.Shell.Controls.GameFolderText);
        Assert.Contains("No se ha encontrado", h.Shell.Controls.FileStatus);
        Assert.False(h.Shell.StatusIsError);

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        Assert.Equal("L", Assert.Single(h.Sender.Sent).Key);

        await h.Shell.Controls.SyncCommand.ExecuteAsync(null);
        Assert.True(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Editor_LinkingModule_AppliesGameKey()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Flight Ready");
        var editor = h.Shell.Editor;
        Assert.False(editor.IsLinked);

        editor.SelectedGameAction = editor.GameActionChoices.Single(c => c.Id == "flight_ready");
        Assert.True(editor.IsLinked);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("flight_ready", h.Session.Buttons.First(b => b.Name == "Flight Ready").GameAction);
        Assert.Equal("RAlt+R", KeyOf(h, "Flight Ready"));
        Assert.Equal("RAlt+R", h.Tile("Flight Ready").KeyText);
        Assert.Equal("R", editor.Key);
        Assert.Equal("RAlt", editor.Modifiers);
        Assert.True(editor.IsLinked);
    }

    [Fact]
    public async Task Editor_LinkedModule_IgnoresWhateverIsTypedInTheKeyBox()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Flight Ready");
        var editor = h.Shell.Editor;
        editor.SelectedGameAction = editor.GameActionChoices.Single(c => c.Id == "flight_ready");
        editor.Key = "ñ";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(h.Shell.StatusIsError);
        Assert.Equal("RAlt+R", KeyOf(h, "Flight Ready"));
    }

    [Fact]
    public async Task Editor_Unlinking_KeepsLastKey_AndAllowsManualEdit()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        await EditAsync(h, "Flight Ready");
        var editor = h.Shell.Editor;

        editor.SelectedGameAction = editor.GameActionChoices[0];
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", h.Session.Buttons.First(b => b.Name == "Flight Ready").GameAction);
        Assert.Equal("RAlt+R", KeyOf(h, "Flight Ready"));

        editor.Key = "F5";
        editor.Modifiers = "";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("F5", KeyOf(h, "Flight Ready"));
        Assert.Equal(60, h.Session.Buttons.First(b => b.Name == "Flight Ready").Action.PressDurationMs);
    }

    [Fact]
    public async Task Editor_Choices_IncludeCatalogAndFileOnlyKeyboardActions()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        var ids = h.Shell.Editor.GameActionChoices.Select(c => c.Id).ToList();

        Assert.Equal("", ids[0]);
        Assert.Contains("headlights", ids);
        Assert.Contains("spaceship_movement/v_toggle_relative_mouse_mode", ids);
        Assert.Contains("player_input_optical_tracking/foip_pushtotalk_proximity", ids);
        Assert.DoesNotContain("spaceship_movement/v_strafe_forward", ids);
        Assert.DoesNotContain("spaceship_movement/v_autoland", ids);
    }

    [Fact]
    public async Task Tile_ShowsWarning_ForLinkWithoutUsableKey()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        Assert.Equal("SIN TECLA", h.Tile("Doors").Warning);
        Assert.Equal("", h.Tile("Flight Ready").Warning);
        Assert.Equal("", h.Tile("Cargo").Warning);
    }

    [Fact]
    public async Task FileChange_UpdatesModule_WithoutRestart()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        h.Game!.Write(Fixture.ActionMapsXml(Fixture.Map("spaceship_general", ("v_flightready", "kb1_f9"), ("v_toggle_all_doors", "kb1_f8"))));

        h.Watch.Trigger();
        h.Ui.Fire(ControlSync.Debounce);
        await h.ControlSync.Pending;

        Assert.Equal("F9", KeyOf(h, "Flight Ready"));
        Assert.Equal("F8", KeyOf(h, "Doors"));
        Assert.Equal("", h.Tile("Doors").Warning);
        Assert.Equal("F9", h.Tile("Flight Ready").KeyText);
        Assert.Equal(2, h.Shell.Controls.Rebinds.Count);
    }

    [Fact]
    public async Task CloneProfile_KeepsLinks()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);
        h.Shell.Profile.Name = "Combate";
        h.Shell.Profile.ShipName = "Aegis Gladius";

        await h.Shell.Profile.SaveCommand.ExecuteAsync(null);
        await h.ControlSync.Pending;

        Assert.Equal("Combate", h.Session.ActiveProfile!.Name);
        Assert.Equal("flight_ready", h.Session.Buttons.First(b => b.Name == "Flight Ready").GameAction);
        Assert.Equal(13, h.Shell.Controls.Links.Count);
    }

    [Fact]
    public async Task Navigate_ToControls()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.NavigateCommand.Execute("Controles");

        Assert.Equal("Controles", h.Shell.Section);
    }
}
