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

        Assert.Equal(16, (await ActiveButtonsAsync(repository)).Count);
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
        Assert.Equal(16, buttons.Count);
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

        Assert.Equal(15, (await ActiveButtonsAsync(repository)).Count);
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
}
