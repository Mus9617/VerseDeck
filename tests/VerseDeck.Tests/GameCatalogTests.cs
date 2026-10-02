using VerseDeck.Core.Models;
using VerseDeck.Game;

namespace VerseDeck.Tests;

public class GameActionCatalogTests
{
    private static readonly GameActionCatalog Catalog = GameActionCatalog.Load();

    [Fact]
    public void Load_Has26Actions_WithUniqueIds()
    {
        Assert.Equal("4.10", Catalog.GameVersion);
        Assert.Equal(26, Catalog.Actions.Count);
        Assert.Equal(26, Catalog.Actions.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Catalog.Actions, a => Assert.NotEmpty(a.ActionNames));
        Assert.All(Catalog.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Label)));
    }

    [Fact]
    public void EveryDefaultInput_IsSendable()
    {
        var broken = Catalog.Actions
            .Where(a => a.DefaultInput is not null && !ScInput.Parse($"kb1_{a.DefaultInput}").TryToKeyPress(a.PressMs, out _))
            .Select(a => a.Id);

        Assert.Empty(broken);
    }

    [Fact]
    public void EveryPressMs_IsBetween20And2000()
    {
        Assert.All(Catalog.Actions, a => Assert.InRange(a.PressMs, 20, KeyPressAction.MaxPressDurationMs));
    }

    [Fact]
    public void PresetLinks_PointToExistingActions()
    {
        string[] presets = ["Flight Ready", "Power Toggle", "Engines Toggle", "Landing Gear", "Lights", "Radar Ping", "Scan Mode", "Quantum Mode", "Shields", "Weapons", "Doors", "Eject", "Self Destruct"];

        Assert.All(presets, name => Assert.NotNull(Catalog.Find(Catalog.PresetLinkFor(name))));
    }

    [Theory]
    [InlineData("Cargo")]
    [InlineData("Star Map")]
    [InlineData("Comms")]
    [InlineData("Minar")]
    public void PresetLinkFor_UnknownModule_IsNull(string module)
    {
        Assert.Null(Catalog.PresetLinkFor(module));
    }
}

public class BindingResolverTests
{
    private static readonly GameActionCatalog Catalog = GameActionCatalog.Load();

    private static GameRebind Rebind(string map, string action, string input, int multiTap = 1)
    {
        return new GameRebind(map, action, ScInput.Parse(input), multiTap);
    }

    private static string Text(Resolution resolution)
    {
        return resolution.Action is null ? "-" : string.Join("+", resolution.Action.Modifiers.Append(resolution.Action.Key));
    }

    [Fact]
    public void NoRebind_UsesDefault()
    {
        var resolution = BindingResolver.Resolve("flight_ready", Catalog, []);

        Assert.Equal(BindStatus.Default, resolution.Status);
        Assert.Equal("RAlt+R", Text(resolution));
        Assert.Equal(60, resolution.Action!.PressDurationMs);
    }

    [Fact]
    public void KeyboardRebind_Wins_WithTheCatalogPressDuration()
    {
        var rebinds = ActionMapsReader.Read(Fixture.RealActionMaps).Rebinds;

        var resolution = BindingResolver.Resolve("autoland", Catalog, rebinds);

        Assert.Equal(BindStatus.YourKey, resolution.Status);
        Assert.Equal("LCtrl+N", Text(resolution));
        Assert.Equal(1000, resolution.Action!.PressDurationMs);
    }

    [Fact]
    public void AliasActionName_IsRecognised()
    {
        var resolution = BindingResolver.Resolve("power_thrusters", Catalog, [Rebind("spaceship_power", "v_power_toggle_group_1", "kb1_f9")]);

        Assert.Equal(BindStatus.YourKey, resolution.Status);
        Assert.Equal("F9", Text(resolution));
    }

    [Fact]
    public void RebindUnderARenamedMap_IsStillRecognised()
    {
        var resolution = BindingResolver.Resolve("headlights", Catalog, [Rebind("spaceship_lights", "v_lights", "kb1_f9")]);

        Assert.Equal("F9", Text(resolution));
    }

    [Fact]
    public void JoystickRebind_IsIgnored()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_landing_system", "js1_button4")]);

        Assert.Equal(BindStatus.Default, resolution.Status);
        Assert.Equal("N", Text(resolution));
    }

    [Fact]
    public void KeyboardAndJoystickRebinds_UseTheKeyboardOne()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog,
        [
            Rebind("spaceship_movement", "v_toggle_landing_system", "js1_button4"),
            Rebind("spaceship_movement", "v_toggle_landing_system", "kb1_g")
        ]);

        Assert.Equal(BindStatus.YourKey, resolution.Status);
        Assert.Equal("G", Text(resolution));
    }

    [Fact]
    public void BlankRebind_IsNoKey_WithoutAction()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_landing_system", "kb1_ ")]);

        Assert.Equal(BindStatus.NoKey, resolution.Status);
        Assert.Null(resolution.Action);
    }

    [Fact]
    public void MouseRebind_IsNotSendable_WithoutAction()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_landing_system", "kb1_mouse4")]);

        Assert.Equal(BindStatus.NotSendable, resolution.Status);
        Assert.Null(resolution.Action);
    }

    [Fact]
    public void MultiTapRebind_IsNotSendable()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_landing_system", "kb1_g", multiTap: 2)]);

        Assert.Equal(BindStatus.NotSendable, resolution.Status);
        Assert.Null(resolution.Action);
    }

    [Fact]
    public void UnknownKeyRebind_IsNotSendable()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_landing_system", "kb1_hyperkey")]);

        Assert.Equal(BindStatus.NotSendable, resolution.Status);
        Assert.Null(resolution.Action);
    }

    [Fact]
    public void NoDefaultAndNoRebind_IsNoKey()
    {
        var resolution = BindingResolver.Resolve("doors_toggle", Catalog, []);

        Assert.Equal(BindStatus.NoKey, resolution.Status);
        Assert.Null(resolution.Action);
    }

    [Fact]
    public void NoDefault_ButRebound_IsYourKey()
    {
        var resolution = BindingResolver.Resolve("doors_toggle", Catalog, [Rebind("spaceship_general", "v_toggle_all_doors", "kb1_f8")]);

        Assert.Equal(BindStatus.YourKey, resolution.Status);
        Assert.Equal("F8", Text(resolution));
    }

    [Fact]
    public void SameKeyAsAnotherRebindInSameMap_IsConflict()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("spaceship_movement", "v_toggle_vtol", "kb1_n")]);

        Assert.Equal(BindStatus.Conflict, resolution.Status);
        Assert.Equal("N", Text(resolution));
    }

    [Fact]
    public void SameKeyInAnotherMap_IsNotConflict()
    {
        var resolution = BindingResolver.Resolve("landing_gear", Catalog, [Rebind("player", "something", "kb1_n")]);

        Assert.Equal(BindStatus.Default, resolution.Status);
    }

    [Fact]
    public void FileOnlyAction_ResolvesByMapAndName()
    {
        var rebinds = ActionMapsReader.Read(Fixture.RealActionMaps).Rebinds;

        var resolution = BindingResolver.Resolve("spaceship_movement/v_toggle_relative_mouse_mode", Catalog, rebinds);

        Assert.Equal(BindStatus.YourKey, resolution.Status);
        Assert.Equal("COMMA", Text(resolution));
        Assert.Equal(60, resolution.Action!.PressDurationMs);
    }

    [Theory]
    [InlineData("no_such_action")]
    [InlineData("spaceship_movement/v_gone")]
    [InlineData("")]
    public void UnknownAction_IsNoKey(string gameAction)
    {
        var resolution = BindingResolver.Resolve(gameAction, Catalog, ActionMapsReader.Read(Fixture.RealActionMaps).Rebinds);

        Assert.Equal(BindStatus.NoKey, resolution.Status);
        Assert.Null(resolution.Action);
    }
}

public sealed class GameInstallLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"versedeck-locator-{Guid.NewGuid():N}");

    public GameInstallLocatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Channel(string relative)
    {
        var folder = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.Combine(folder, "user", "client", "0"));
        return folder;
    }

    private void WriteLog(params string[] folders)
    {
        var lines = folders.Select(f => $"{{ \"t\":\"2026-10-02 14:30:20.250\", \"[main][info] \": \"[Launcher::launch] Launching Star Citizen LIVE from ({f.Replace(@"\", @"\\")})\"  }},");
        File.WriteAllLines(Path.Combine(_root, "logs", "log.log"), lines);
    }

    private GameInstallLocator Locator(params string[] driveRoots) => new(Path.Combine(_root, "logs"), driveRoots);

    [Fact]
    public void ConfiguredFolder_Wins()
    {
        var configured = Channel("configured");
        WriteLog(Channel("launcher"));

        var install = Locator().Locate(configured);

        Assert.Equal(configured, install!.ChannelFolder);
        Assert.Equal("Ajuste", install.Source);
    }

    [Fact]
    public void ConfiguredFolder_Invalid_ReturnsNull_EvenIfTheLauncherKnowsOne()
    {
        WriteLog(Channel("launcher"));

        Assert.Null(Locator().Locate(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void LauncherLog_LastLiveLaunchLine_IsUsed()
    {
        var older = Channel("older");
        var newer = Channel("newer");
        WriteLog(older, newer);

        var install = Locator().Locate(null);

        Assert.Equal(newer, install!.ChannelFolder);
        Assert.Equal("Lanzador", install.Source);
    }

    [Fact]
    public void LauncherLog_PointingToMissingFolder_FallsBackToDrives()
    {
        WriteLog(Path.Combine(_root, "uninstalled"));
        var standard = Channel(@"drive\Program Files\Roberts Space Industries\StarCitizen\LIVE");

        var install = Locator(Path.Combine(_root, "drive")).Locate("");

        Assert.Equal(standard, install!.ChannelFolder);
        Assert.Equal("Ruta habitual", install.Source);
    }

    [Fact]
    public void NothingFound_ReturnsNull()
    {
        Assert.Null(Locator(Path.Combine(_root, "empty-drive")).Locate(null));
    }

    [Fact]
    public void MissingLauncherLogFolder_IsNotAnError()
    {
        var locator = new GameInstallLocator(Path.Combine(_root, "no-logs-here"), []);

        Assert.Null(locator.Locate(null));
    }

    [Fact]
    public void ActionMapsPath_IsUnderProfilesDefault()
    {
        var configured = Channel("configured");

        Assert.Equal(
            Path.Combine(configured, "user", "client", "0", "Profiles", "default", "actionmaps.xml"),
            Locator().Locate(configured)!.ActionMapsPath);
    }
}
