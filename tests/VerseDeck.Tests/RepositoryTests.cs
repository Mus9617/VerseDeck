using System.Globalization;
using VerseDeck.Core.Models;
using VerseDeck.Data;

namespace VerseDeck.Tests;

public class RepositoryTests
{
    private static async Task<IReadOnlyList<DeckButton>> ActiveButtonsAsync(SqliteVerseDeckRepository repository)
    {
        var profile = (await repository.GetProfilesAsync()).First(p => p.IsActive);
        return await repository.GetButtonsAsync(profile.Id);
    }

    [Fact]
    public async Task Initialize_SeedsSixteenDefaultButtons_Once()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        await repository.InitializeAsync();

        Assert.Equal(18, (await ActiveButtonsAsync(repository)).Count);
    }

    [Fact]
    public async Task DeletedDefaultButton_DoesNotReappear_AfterReinitialize()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var lights = (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights");

        await repository.DeleteButtonAsync(lights.Id);
        await repository.InitializeAsync();

        Assert.DoesNotContain(await ActiveButtonsAsync(repository), b => b.Name == "Lights");
    }

    [Fact]
    public async Task RenamedDefaultButton_IsNotDuplicated_AfterReinitialize()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var lights = (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights");

        await repository.SaveButtonAsync(lights with { Name = "Luces" });
        await repository.InitializeAsync();

        var buttons = await ActiveButtonsAsync(repository);
        Assert.Equal(18, buttons.Count);
        Assert.DoesNotContain(buttons, b => b.Name == "Lights");
    }

    [Fact]
    public async Task LegacyDatabaseWithButtons_IsMarkedSeeded_WithoutAddingButtons()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var cargo = (await ActiveButtonsAsync(repository)).First(b => b.Name == "Cargo");
        await repository.DeleteButtonAsync(cargo.Id);
        await db.ExecuteAsync("DELETE FROM Settings WHERE Key='DefaultDeckSeededV1'");

        await repository.InitializeAsync();

        // The default deck is not added to an old one; only the two control tower modules are.
        Assert.Equal(17,(await ActiveButtonsAsync(repository)).Count);
        Assert.Equal("Done", await db.ScalarAsync("SELECT Value FROM Settings WHERE Key='DefaultDeckSeededV1'"));
    }

    [Fact]
    public async Task Settings_RoundTrip_UnderSpanishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            await using var db = new TempDatabase();
            var repository = await db.CreateAsync();
            var settings = await repository.GetSettingsAsync();
            Assert.Equal(0.40, settings.VoiceMinimumConfidence, 3);

            await repository.SaveSettingsAsync(settings with { VoiceMinimumConfidence = 0.55 });

            Assert.Equal(0.55, (await repository.GetSettingsAsync()).VoiceMinimumConfidence, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Settings_OutOfRangeConfidence_FallsBackToDefault()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        await db.ExecuteAsync("UPDATE Settings SET Value='40' WHERE Key='VoiceMinimumConfidence'");

        Assert.Equal(0.40, (await repository.GetSettingsAsync()).VoiceMinimumConfidence, 3);
    }

    [Fact]
    public async Task LegacyDatabase_WithEveryModuleDeleted_IsNotReseeded()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        foreach (var button in await ActiveButtonsAsync(repository))
        {
            await repository.DeleteButtonAsync(button.Id);
        }

        await db.ExecuteAsync("DELETE FROM Settings WHERE Key='DefaultDeckSeededV1'");

        await repository.InitializeAsync();

        Assert.Empty(await ActiveButtonsAsync(repository));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("F5")]
    [InlineData("Hyper")]
    public void Validate_NonModifierKeyAsModifier_Throws(string modifier)
    {
        Assert.Throws<InvalidOperationException>(() => new KeyPressAction("C", ["Ctrl", modifier], 60).Validate());
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("control")]
    [InlineData("SHIFT")]
    [InlineData("Alt")]
    public void Validate_RealModifier_IsAccepted(string modifier)
    {
        Assert.Equal("C", new KeyPressAction("C", [modifier], 60).Validate().Key);
    }

    [Theory]
    [InlineData("RAlt")]
    [InlineData("lctrl")]
    [InlineData("RShift")]
    public void Validate_SidedModifier_IsAccepted(string modifier)
    {
        Assert.Equal("C", new KeyPressAction("C", [modifier], 60).Validate().Key);
    }

    [Fact]
    public void Validate_Duration2000_IsAccepted_2001_Throws()
    {
        Assert.Equal(2000, new KeyPressAction("C", [], 2000).Validate().PressDurationMs);
        Assert.Throws<InvalidOperationException>(() => new KeyPressAction("C", [], 2001).Validate());
    }

    [Fact]
    public async Task GameAction_RoundTrips()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var lights = (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights");

        await repository.SaveButtonAsync(lights with { GameAction = "headlights" });

        Assert.Equal("headlights", (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights").GameAction);
        Assert.Equal("", (await ActiveButtonsAsync(repository)).First(b => b.Name == "Cargo").GameAction);
    }

    [Fact]
    public async Task GameFolder_RoundTrips()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        Assert.Equal("", (await repository.GetSettingsAsync()).GameFolder);

        await repository.SaveSettingsAsync((await repository.GetSettingsAsync()) with { GameFolder = @"D:\rsi\StarCitizen\LIVE" });

        Assert.Equal(@"D:\rsi\StarCitizen\LIVE", (await repository.GetSettingsAsync()).GameFolder);
    }

    [Fact]
    public async Task DatabaseWithoutGameActionColumn_IsUpgraded_KeepingButtonsAndPhrases()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var phrases = (await repository.GetVoiceCommandsAsync()).Count;
        await db.ExecuteAsync("ALTER TABLE Buttons DROP COLUMN GameAction");

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        var buttons = await ActiveButtonsAsync(repository);
        Assert.Equal(18, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("", b.GameAction));
        Assert.Equal(phrases, (await repository.GetVoiceCommandsAsync()).Count);
    }

    [Fact]
    public async Task Response_RoundTrips()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var lights = (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights");

        await repository.SaveButtonAsync(lights with { Response = "Luces.|Hecho, luces." });

        Assert.Equal("Luces.|Hecho, luces.", (await ActiveButtonsAsync(repository)).First(b => b.Name == "Lights").Response);
        Assert.Equal("", (await ActiveButtonsAsync(repository)).First(b => b.Name == "Cargo").Response);
    }

    [Fact]
    public async Task CopilotSettings_RoundTrip_UnderSpanishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            await using var db = new TempDatabase();
            var repository = await db.CreateAsync();

            await repository.SaveSettingsAsync((await repository.GetSettingsAsync()) with
            {
                CopilotEnabled = true,
                CopilotVoice = "piper-davefx",
                CopilotPack = "militar",
                CopilotVolume = 0.65,
                CopilotVoiceOnly = true,
                CopilotMutedCategories = "Combat,Scan",
                CopilotGreeting = true
            });

            var settings = await repository.GetSettingsAsync();
            Assert.True(settings.CopilotEnabled);
            Assert.Equal("piper-davefx", settings.CopilotVoice);
            Assert.Equal("militar", settings.CopilotPack);
            Assert.Equal(0.65, settings.CopilotVolume, 3);
            Assert.True(settings.CopilotVoiceOnly);
            Assert.Equal("Combat,Scan", settings.CopilotMutedCategories);
            Assert.True(settings.CopilotGreeting);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task FreshDatabase_HasCopilotDisabled()
    {
        await using var db = new TempDatabase();
        var settings = await (await db.CreateAsync()).GetSettingsAsync();

        Assert.False(settings.CopilotEnabled);
        Assert.False(settings.CopilotGreeting);
        Assert.Equal("", settings.CopilotVoice);
        Assert.Equal("sobria", settings.CopilotPack);
        Assert.Equal(0.8, settings.CopilotVolume, 3);
    }

    [Fact]
    public async Task DatabaseWithoutResponseColumn_IsUpgraded()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        await db.ExecuteAsync("ALTER TABLE Buttons DROP COLUMN Response");

        await repository.InitializeAsync();

        var buttons = await ActiveButtonsAsync(repository);
        Assert.Equal(18, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("", b.Response));
    }
}
