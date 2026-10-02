using VerseDeck.App.Services;

namespace VerseDeck.Tests;

public class DeckSessionTests
{
    private static async Task<DeckSession> LoadedSessionAsync(TempDatabase db)
    {
        var session = new DeckSession(await db.CreateAsync());
        await session.LoadAsync();
        return session;
    }

    [Fact]
    public async Task Load_SelectsActiveProfile_AndItsButtons()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);

        Assert.Equal("Global", session.ActiveProfile!.Name);
        Assert.Equal(16, session.Buttons.Count);
        Assert.Single(session.Profiles);
    }

    [Fact]
    public async Task SaveProfile_NewName_ClonesButtonsAndPhrases()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);
        var sourceId = session.ActiveProfile!.Id;
        var sourcePhrases = session.VoiceCommands.Count;

        var created = await session.SaveProfileAsync("Combate", "Aegis Gladius");

        Assert.NotEqual(sourceId, created.Id);
        Assert.Equal(created.Id, session.ActiveProfile!.Id);
        Assert.Equal(16, session.Buttons.Count);
        Assert.All(session.Buttons, b => Assert.Equal(created.Id, b.ProfileId));
        Assert.Equal(sourcePhrases, session.VoiceCommands.Count);
    }

    [Fact]
    public async Task SaveProfile_ExistingName_UpdatesShip_WithoutCloning()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);

        await session.SaveProfileAsync("global", "Drake Cutlass Black");

        Assert.Single(session.Profiles);
        Assert.Equal("Drake Cutlass Black", session.ActiveProfile!.ShipName);
        Assert.Equal(16, session.Buttons.Count);
    }

    [Fact]
    public async Task VoiceCommands_OnlyIncludeActiveProfile()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);
        await session.SaveProfileAsync("Combate", "Aegis Gladius");

        var activeButtonIds = session.Buttons.Select(b => b.Id).ToHashSet();

        Assert.NotEmpty(session.VoiceCommands);
        Assert.All(session.VoiceCommands, v => Assert.Contains(v.ButtonId, activeButtonIds));
    }

    [Fact]
    public async Task ActivateProfile_SwitchesButtons()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);
        var globalId = session.ActiveProfile!.Id;
        await session.SaveProfileAsync("Combate", "Aegis Gladius");

        await session.ActivateProfileAsync(globalId);

        Assert.Equal("Global", session.ActiveProfile!.Name);
        Assert.All(session.Buttons, b => Assert.Equal(globalId, b.ProfileId));
    }

    [Fact]
    public async Task SaveVoicePhrase_SamePhraseTwice_UpdatesInsteadOfDuplicating()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);
        var button = session.Buttons.First(b => b.Name == "Lights");
        var before = session.VoiceCommands.Count;

        await session.SaveVoicePhraseAsync(button.Id, "Dame Luz", 0.5);
        await session.SaveVoicePhraseAsync(button.Id, "dame luz", 0.7);

        Assert.Equal(before + 1, session.VoiceCommands.Count);
        Assert.Equal(0.7, session.VoiceCommands.Single(v => v.Phrase == "dame luz").MinimumConfidence, 3);
    }

    [Fact]
    public async Task EveryMutation_RaisesChangedOnce()
    {
        await using var db = new TempDatabase();
        var session = await LoadedSessionAsync(db);
        var raised = 0;
        session.Changed += (_, _) => raised++;
        var button = session.Buttons.First(b => b.Name == "Lights");

        async Task ExpectOneAsync(Func<Task> mutation)
        {
            raised = 0;
            await mutation();
            Assert.Equal(1, raised);
        }

        await ExpectOneAsync(() => session.SaveButtonAsync(button with { Name = "Luces" }));
        await ExpectOneAsync(() => session.SaveVoicePhraseAsync(button.Id, "dame luz", 0.5));
        await ExpectOneAsync(() => session.DeleteButtonAsync(button.Id));
        await ExpectOneAsync(() => session.SaveSettingsAsync(session.Settings with { CommandSoundEnabled = false }));
        await ExpectOneAsync(() => session.SaveProfileAsync("Combate", "Aegis Gladius"));
        await ExpectOneAsync(() => session.ActivateProfileAsync(session.Profiles.First(p => p.Name == "Global").Id));
    }
}
