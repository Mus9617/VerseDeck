using VerseDeck.App;
using VerseDeck.App.Services;

namespace VerseDeck.Tests;

public class ThemeServiceTests
{
    [Theory]
    [InlineData("Drake Cutlass Black", ThemeId.Drake)]
    [InlineData("drake cutlass", ThemeId.Drake)]
    [InlineData("  Drake Cutter ", ThemeId.Drake)]
    [InlineData("Origin 890 Jump", ThemeId.Origin)]
    [InlineData("Aegis Gladius", ThemeId.Aegis)]
    [InlineData("Anvil Carrack", ThemeId.Anvil)]
    [InlineData("RSI Polaris", ThemeId.RSI)]
    [InlineData("Gatac Syulen", ThemeId.Gatac)]
    [InlineData("MISC Freelancer", ThemeId.MISC)]
    [InlineData("Argo MOLE", ThemeId.Neutral)]
    [InlineData("Drakestone", ThemeId.Neutral)]
    [InlineData("Drake,Origin X", ThemeId.Neutral)]
    [InlineData("2 Fast", ThemeId.Neutral)]
    [InlineData("Mi nave", ThemeId.Neutral)]
    [InlineData("", ThemeId.Neutral)]
    [InlineData(null, ThemeId.Neutral)]
    public void ThemeFor_UsesManufacturerPrefix(string? ship, ThemeId expected)
    {
        Assert.Equal(expected, ShipCatalog.ThemeFor(ship));
    }

    [Fact]
    public void Update_Auto_FollowsShip()
    {
        var applied = new List<ThemeId>();
        var service = new ThemeService(applied.Add);

        service.Update(ThemeService.Auto, "Drake Cutlass Black");
        service.Update(ThemeService.Auto, "Origin 300i");

        Assert.Equal([ThemeId.Drake, ThemeId.Origin], applied);
        Assert.Equal(ThemeId.Origin, service.Current);
    }

    [Fact]
    public void Update_FixedTheme_IgnoresShip()
    {
        var applied = new List<ThemeId>();
        var service = new ThemeService(applied.Add);

        service.Update("Aegis", "Drake Cutlass Black");

        Assert.Equal([ThemeId.Aegis], applied);
    }

    [Theory]
    [InlineData("ColdBlue")]
    [InlineData("")]
    [InlineData("NoExiste")]
    public void Update_LegacyOrUnknownSetting_BehavesAsAuto(string setting)
    {
        var applied = new List<ThemeId>();
        var service = new ThemeService(applied.Add);

        service.Update(setting, "Anvil Carrack");

        Assert.Equal([ThemeId.Anvil], applied);
    }

    [Fact]
    public void Update_SameTheme_DoesNotReapply()
    {
        var applied = new List<ThemeId>();
        var service = new ThemeService(applied.Add);

        service.Update(ThemeService.Auto, "Drake Cutlass Black");
        service.Update(ThemeService.Auto, "Drake Corsair");

        Assert.Single(applied);
    }

    [Fact]
    public void FirstUpdate_AppliesNeutral_EvenThoughItIsTheDefault()
    {
        var applied = new List<ThemeId>();
        var service = new ThemeService(applied.Add);

        service.Update(ThemeService.Auto, "Argo MOLE");

        Assert.Equal([ThemeId.Neutral], applied);
    }
}
