using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;
using VerseDeck.Core.Models;
using VerseDeck.Speech;

namespace VerseDeck.Tests;

public class CopilotServiceTests
{
    private static readonly ResponsePack Sobria = ResponsePack.LoadAll().First(p => p.Id == "sobria");

    private static DeckButton Button(Harness h, string name) => h.Session.Buttons.First(b => b.Name == name);

    private static async Task<Harness> EnabledAsync()
    {
        var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        return h;
    }

    private static async Task PressAsync(Harness h, string module)
    {
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(module));
        await h.Copilot.Pending;
    }

    private static string? PhraseOf(Harness h, string wavPath)
    {
        return Sobria.Actions.Values.SelectMany(v => v)
            .Concat(Sobria.Failed).Concat(Sobria.NoKey).Concat(Sobria.Greeting)
            .Concat(["Perfil Combate.", CopilotService.TestPhrase, "Minar, hecho.", "Minar.", "A sus pies."])
            .FirstOrDefault(text => h.PhraseCache.TryGet(SpeechFixture.Voice, text)?.Path == wavPath);
    }

    [Fact]
    public async Task Disabled_ByDefault_SaysNothing_AndKeepsTheBeep()
    {
        await using var h = await Harness.CreateAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CommandSoundEnabled = true });

        await PressAsync(h, "Lights");

        Assert.Empty(h.Player.Played);
        Assert.Empty(h.Tts.Synthesized);
        Assert.Equal(1, h.Audio.CommandPlays);
    }

    [Fact]
    public async Task Enabled_AnswersAModulePress_WithItsPhrase()
    {
        await using var h = await EnabledAsync();

        await PressAsync(h, "Landing Gear");

        var played = Assert.Single(h.Player.Played);
        Assert.Contains(PhraseOf(h, played.Path), Sobria.Actions["landing_gear"]);
        Assert.Equal(0.8, played.Volume, 3);
        Assert.Equal("N", Assert.Single(h.Sender.Sent).Key);
    }

    [Fact]
    public async Task Answering_SilencesTheCommandBeep()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CommandSoundEnabled = true });

        await PressAsync(h, "Lights");

        Assert.Equal(0, h.Audio.CommandPlays);
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task CachedPhrase_IsPlayedWithoutCallingTheEngineAgain()
    {
        await using var h = await EnabledAsync();
        await PressAsync(h, "Star Map");
        var synthesized = h.Tts.Synthesized.Count;

        await PressAsync(h, "Star Map");

        Assert.Equal(synthesized, h.Tts.Synthesized.Count);
        Assert.Equal(2, h.Player.Played.Count);
        Assert.Equal(h.Player.Played[0].Path, h.Player.Played[1].Path);
    }

    [Fact]
    public async Task NoVoiceInstalled_SaysNothing()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        await h.EnableCopilotAsync();

        await PressAsync(h, "Lights");

        Assert.Empty(h.Player.Played);
        Assert.Null(h.Copilot.ActiveVoice);
    }

    [Fact]
    public async Task QuickMute_SilencesEverything_AndStopsPlayback()
    {
        await using var h = await EnabledAsync();

        h.Shell.IsMuted = true;
        await PressAsync(h, "Lights");

        Assert.Empty(h.Player.Played);
        Assert.True(h.Copilot.Muted);
        Assert.True(h.Player.Stops >= 1);

        h.Shell.IsMuted = false;
        await PressAsync(h, "Lights");
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task VoiceOnly_IgnoresDeckClicks_ButAnswersVoiceCommands()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVoiceOnly = true, VoiceActivationMode = "PushToTalk" });

        await PressAsync(h, "Lights");
        Assert.Empty(h.Player.Played);

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var lights = Button(h, "Lights");
        h.Voice.Raise(h.Session.VoiceCommands.First(v => v.ButtonId == lights.Id), lights);
        await h.Shell.Voice.Pending;
        await h.Copilot.Pending;

        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task MutedCategory_StaysQuiet()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotMutedCategories = "Combat, systems" });

        await PressAsync(h, "Weapons");
        await PressAsync(h, "Lights");
        Assert.Empty(h.Player.Played);

        await PressAsync(h, "Landing Gear");
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task ModuleSetToSilent_StaysQuiet_AndKeepsTheBeep()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CommandSoundEnabled = true });
        await h.Session.SaveButtonAsync(Button(h, "Lights") with { Response = "-" });

        await PressAsync(h, "Lights");

        Assert.Empty(h.Player.Played);
        Assert.Equal(1, h.Audio.CommandPlays);
    }

    [Fact]
    public async Task ModuleWithItsOwnText_SaysThatText()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveButtonAsync(Button(h, "Landing Gear") with { Response = "A sus pies." });

        await PressAsync(h, "Landing Gear");

        Assert.Equal("A sus pies.", PhraseOf(h, h.Player.Played.Single().Path));
    }

    [Fact]
    public async Task EngineFailure_NeverAffectsThePress()
    {
        await using var h = await EnabledAsync();
        h.Tts.Throw = true;

        await PressAsync(h, "Lights");

        Assert.Equal("L", Assert.Single(h.Sender.Sent).Key);
        Assert.Empty(h.Player.Played);
        Assert.False(h.Shell.StatusIsError);
        Assert.Contains(h.Log.Lines, line => line.Contains("Copilot could not say"));
    }

    [Fact]
    public async Task SlowEngine_DoesNotDelayThePress()
    {
        await using var h = await EnabledAsync();
        foreach (var phrase in Sobria.Actions["headlights"])
        {
            h.Tts.Hold(phrase);
        }

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));

        Assert.Equal("L", Assert.Single(h.Sender.Sent).Key);
        Assert.Empty(h.Player.Played);
        Assert.False(h.Copilot.Pending.IsCompleted);

        foreach (var phrase in Sobria.Actions["headlights"])
        {
            h.Tts.Release(phrase);
        }

        await h.Copilot.Pending;
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task Burst_OnlyTheLastAnswerSounds_EvenIfAnOlderOneFinishesLater()
    {
        await using var h = await EnabledAsync();
        foreach (var phrase in Sobria.Actions["headlights"])
        {
            h.Tts.Hold(phrase);
        }

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        var slow = h.Copilot.Pending;
        await PressAsync(h, "Star Map");

        foreach (var phrase in Sobria.Actions["headlights"])
        {
            h.Tts.Release(phrase);
        }

        await slow;

        Assert.Equal("Mapa estelar.", PhraseOf(h, Assert.Single(h.Player.Played).Path));
    }

    [Fact]
    public async Task SendFailure_SaysTheFailurePhrase()
    {
        await using var h = await EnabledAsync();
        h.Sender.ThrowOnSend = true;

        await PressAsync(h, "Lights");

        Assert.Equal("No he podido enviarlo.", PhraseOf(h, Assert.Single(h.Player.Played).Path));
    }

    [Fact]
    public async Task LinkedModuleWithoutKey_SendsNothing_AndSaysSo()
    {
        await using var h = await EnabledAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        await PressAsync(h, "Doors");

        Assert.Empty(h.Sender.Sent);
        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("Esa acción no tiene tecla en el juego.", PhraseOf(h, Assert.Single(h.Player.Played).Path));
    }

    [Fact]
    public async Task LinkedModuleWithoutKey_SendsNothing_EvenWithTheCopilotOff()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        await PressAsync(h, "Doors");

        Assert.Empty(h.Sender.Sent);
        Assert.True(h.Shell.StatusIsError);
        Assert.Empty(h.Player.Played);
    }

    [Fact]
    public async Task ProfileChange_IsAnnounced_ButTheFirstLoadIsNot()
    {
        await using var h = await EnabledAsync();
        Assert.Empty(h.Player.Played);

        await h.Session.SaveProfileAsync("Combate", "Aegis Gladius");
        await h.Copilot.Pending;

        Assert.Equal("Perfil Combate.", PhraseOf(h, Assert.Single(h.Player.Played).Path));
    }

    [Fact]
    public async Task Greeting_IsSpokenAtStart_OnlyWhenAskedFor()
    {
        await using var h = await Harness.CreateAsync(initialize: false);
        await h.Session.LoadAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotEnabled = true, CopilotVoice = VoiceCatalog.DefaultVoiceId, CopilotGreeting = true });

        await h.Shell.InitializeAsync();
        await h.Copilot.Pending;
        await h.Shell.CopilotStart;

        Assert.Equal("Sistemas listos.", PhraseOf(h, h.Player.Played.First().Path));
    }

    [Fact]
    public async Task NoGreeting_ByDefault()
    {
        await using var h = await Harness.CreateAsync(initialize: false);
        await h.Session.LoadAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotEnabled = true, CopilotVoice = VoiceCatalog.DefaultVoiceId });

        await h.Shell.InitializeAsync();
        await h.Shell.CopilotStart;

        Assert.Empty(h.Player.Played);
    }

    [Fact]
    public async Task WarmUp_RendersEveryMissingPhrase_Once()
    {
        await using var h = await EnabledAsync();

        await h.Copilot.WarmUpAsync();
        var first = h.Tts.Synthesized.Count;
        await h.Copilot.WarmUpAsync();

        Assert.True(first >= 16);
        Assert.Equal(first, h.Tts.Synthesized.Count);
        Assert.Equal(first, h.Copilot.CachedPhrases);
        Assert.Equal(h.Tts.Synthesized.Count, h.Tts.Synthesized.Distinct().Count());
        Assert.Empty(h.Player.Played);
        Assert.Equal("", h.Copilot.WarmUpStatus);
    }

    [Fact]
    public async Task ChangingPersonality_ChangesTheWording()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotPack = "militar" });

        await PressAsync(h, "Star Map");

        Assert.NotNull(h.PhraseCache.TryGet(SpeechFixture.Voice, "Carta estelar, recibido."));
        Assert.Null(h.PhraseCache.TryGet(SpeechFixture.Voice, "Mapa estelar."));
    }

    [Fact]
    public async Task Volume_ReachesThePlayer()
    {
        await using var h = await EnabledAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVolume = 0.35 });

        await PressAsync(h, "Lights");

        Assert.Equal(0.35, h.Player.Played.Single().Volume, 3);
    }

    [Fact]
    public async Task WhileSpeaking_AlwaysListeningMode_IgnoresWhatTheMicrophoneHears()
    {
        await using var h = await EnabledAsync();
        h.Tts.Seconds = 2;
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var lights = Button(h, "Lights");
        var command = h.Session.VoiceCommands.First(v => v.ButtonId == lights.Id);

        await PressAsync(h, "Star Map");
        h.Now += TimeSpan.FromSeconds(1);
        h.Voice.Raise(command, lights);
        await h.Shell.Voice.Pending;
        Assert.Single(h.Sender.Sent);

        h.Now += TimeSpan.FromSeconds(1.5);
        h.Voice.Raise(command, lights);
        await h.Shell.Voice.Pending;
        Assert.Equal(2, h.Sender.Sent.Count);
    }

    [Fact]
    public async Task WhileSpeaking_PushToTalk_StillAcceptsCommands()
    {
        await using var h = await EnabledAsync();
        h.Tts.Seconds = 2;
        h.Shell.Voice.Mode = "PushToTalk";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var lights = Button(h, "Lights");

        await PressAsync(h, "Star Map");
        h.Now += TimeSpan.FromSeconds(1);
        h.Voice.Raise(h.Session.VoiceCommands.First(v => v.ButtonId == lights.Id), lights);
        await h.Shell.Voice.Pending;

        Assert.Equal(2, h.Sender.Sent.Count);
    }

    [Fact]
    public async Task UnrelatedSettingChange_DoesNotRestartTheSpeechRecogniser()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var starts = h.Voice.StartCount;

        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVolume = 0.5 });
        await h.Session.SaveSettingsAsync(h.Session.Settings with { Theme = "Drake" });
        await h.Shell.Voice.Pending;

        Assert.Equal(starts, h.Voice.StartCount);
    }

    [Fact]
    public async Task ClearCache_RemovesThePhrases()
    {
        await using var h = await EnabledAsync();
        await PressAsync(h, "Lights");

        h.Copilot.ClearCache();

        Assert.Equal(0, h.Copilot.CachedPhrases);
    }
}

public class CopilotViewModelTests
{
    private static VoiceRowViewModel Row(Harness h, string id) => h.Shell.Copilot.Voices.Single(v => v.Voice.Id == id);

    [Fact]
    public async Task Initial_ShowsSixVoices_DefaultInstalledButNothingSelected_AndCopilotOff()
    {
        await using var h = await Harness.CreateAsync();
        var copilot = h.Shell.Copilot;

        Assert.Equal(6, copilot.Voices.Count);
        Assert.True(Row(h, "piper-davefx").IsInstalled);
        Assert.False(Row(h, "kokoro-dora").IsInstalled);
        Assert.DoesNotContain(copilot.Voices, v => v.IsSelected);
        Assert.False(copilot.Enabled);
        Assert.Equal("sobria", copilot.SelectedPack!.Id);
        Assert.Equal(3, copilot.Packs.Count);
        Assert.Contains("MB", Row(h, "piper-davefx").Details);
        Assert.Equal(8, copilot.Categories.Count);
        Assert.All(copilot.Categories, c => Assert.True(c.Speaks));
    }

    [Fact]
    public async Task Switches_AreSavedAsTheyChange()
    {
        await using var h = await Harness.CreateAsync();
        var copilot = h.Shell.Copilot;

        copilot.Enabled = true;
        await copilot.Pending;
        copilot.VoiceOnly = true;
        await copilot.Pending;
        copilot.Greeting = true;
        await copilot.Pending;
        copilot.Volume = 0.4;
        await copilot.Pending;
        copilot.SelectedPack = copilot.Packs.Single(p => p.Id == "militar");
        await copilot.Pending;

        var settings = h.Session.Settings;
        Assert.True(settings.CopilotEnabled);
        Assert.True(settings.CopilotVoiceOnly);
        Assert.True(settings.CopilotGreeting);
        Assert.Equal(0.4, settings.CopilotVolume, 3);
        Assert.Equal("militar", settings.CopilotPack);
        Assert.Contains("recibido", copilot.PackSample);
    }

    [Fact]
    public async Task CategoryToggle_IsSavedAsAList()
    {
        await using var h = await Harness.CreateAsync();
        var copilot = h.Shell.Copilot;

        copilot.Categories.Single(c => c.Name == "Combat").Speaks = false;
        await copilot.Pending;
        copilot.Categories.Single(c => c.Name == "Scan").Speaks = false;
        await copilot.Pending;

        Assert.Equal("Scan,Combat", h.Session.Settings.CopilotMutedCategories);
        Assert.False(copilot.Categories.Single(c => c.Name == "Combat").Speaks);

        copilot.Categories.Single(c => c.Name == "Combat").Speaks = true;
        await copilot.Pending;
        Assert.Equal("Scan", h.Session.Settings.CopilotMutedCategories);
    }

    [Fact]
    public async Task Install_FirstVoice_BecomesTheActiveVoice()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        var row = Row(h, "piper-claude-mx");

        await row.InstallCommand.ExecuteAsync(null);

        Assert.True(row.IsInstalled);
        Assert.True(row.IsSelected);
        Assert.False(row.IsBusy);
        Assert.Equal("piper-claude-mx", h.Session.Settings.CopilotVoice);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Install_SecondVoice_DoesNotReplaceTheChosenOne()
    {
        await using var h = await Harness.CreateAsync();
        await Row(h, "piper-davefx").UseCommand.ExecuteAsync(null);

        await Row(h, "piper-claude-mx").InstallCommand.ExecuteAsync(null);

        Assert.Equal("piper-davefx", h.Session.Settings.CopilotVoice);
        Assert.True(Row(h, "piper-claude-mx").IsInstalled);
        Assert.False(Row(h, "piper-claude-mx").IsSelected);
    }

    [Fact]
    public async Task Install_SharedModel_MarksBothVoicesInstalled()
    {
        await using var h = await Harness.CreateAsync();

        await Row(h, "piper-sharvard-0").InstallCommand.ExecuteAsync(null);

        Assert.True(Row(h, "piper-sharvard-1").IsInstalled);
    }

    [Fact]
    public async Task Install_Failure_ShowsError_AndLeavesTheVoiceUninstalled()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        h.VoiceInstaller.Fail = new InvalidDataException("SHA-256 distinto");
        var row = Row(h, "piper-davefx");

        await row.InstallCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Contains("SHA-256", h.Shell.StatusText);
        Assert.False(row.IsInstalled);
        Assert.False(row.IsBusy);
        Assert.Equal("", h.Session.Settings.CopilotVoice);
    }

    [Fact]
    public async Task Install_Cancel_StopsCleanly()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        h.VoiceInstaller.Hold = new TaskCompletionSource();
        var row = Row(h, "piper-davefx");

        var install = row.InstallCommand.ExecuteAsync(null);
        Assert.True(row.IsBusy);
        row.CancelCommand.Execute(null);
        await install;

        Assert.False(row.IsBusy);
        Assert.False(row.IsInstalled);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Use_NotInstalledVoice_IsRefused()
    {
        await using var h = await Harness.CreateAsync();

        await Row(h, "kokoro-dora").UseCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("", h.Session.Settings.CopilotVoice);
    }

    [Fact]
    public async Task Test_SpeaksEvenWhenTheCopilotIsOff()
    {
        await using var h = await Harness.CreateAsync();
        await Row(h, "piper-davefx").UseCommand.ExecuteAsync(null);

        await h.Shell.Copilot.TestCommand.ExecuteAsync(null);

        Assert.Single(h.Player.Played);
        Assert.NotNull(h.PhraseCache.TryGet(SpeechFixture.Voice, CopilotService.TestPhrase));
    }

    [Fact]
    public async Task Test_WithoutVoice_ExplainsWhy()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);

        await h.Shell.Copilot.TestCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Empty(h.Player.Played);
    }

    [Fact]
    public async Task StatusLine_ExplainsWhatIsMissing()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        Assert.Contains("Sin voz", h.Shell.Copilot.StatusLine);

        await Row(h, "piper-davefx").InstallCommand.ExecuteAsync(null);
        Assert.Contains("Activa el copiloto", h.Shell.Copilot.StatusLine);

        h.Shell.Copilot.Enabled = true;
        await h.Shell.Copilot.Pending;
        Assert.StartsWith("Activo", h.Shell.Copilot.StatusLine);
    }

    [Fact]
    public async Task Editor_SavesTheThreeResponseModes()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        var editor = h.Shell.Editor;
        Assert.Equal(ModuleEditorViewModel.ResponseFromPack, editor.SelectedResponseMode);

        editor.SelectedResponseMode = ModuleEditorViewModel.ResponseNone;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("-", h.Session.Buttons.First(b => b.Name == "Lights").Response);
        Assert.Equal(ModuleEditorViewModel.ResponseNone, editor.SelectedResponseMode);

        editor.SelectedResponseMode = ModuleEditorViewModel.ResponseCustom;
        editor.ResponseText = " Luces. | Hecho. ";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Luces. | Hecho.", h.Session.Buttons.First(b => b.Name == "Lights").Response);
        Assert.Equal("Luces. | Hecho.", editor.ResponseText);

        editor.SelectedResponseMode = ModuleEditorViewModel.ResponseFromPack;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", h.Session.Buttons.First(b => b.Name == "Lights").Response);
    }

    [Fact]
    public async Task Editor_CustomResponseLeftEmpty_IsRejected()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        h.Shell.Editor.SelectedResponseMode = ModuleEditorViewModel.ResponseCustom;
        h.Shell.Editor.ResponseText = "  ";

        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("", h.Session.Buttons.First(b => b.Name == "Lights").Response);
    }

    [Fact]
    public async Task ClonedProfile_KeepsModuleResponses()
    {
        await using var h = await Harness.CreateAsync();
        await h.Session.SaveButtonAsync(h.Session.Buttons.First(b => b.Name == "Lights") with { Response = "-" });

        await h.Session.SaveProfileAsync("Combate", "Aegis Gladius");

        Assert.Equal("-", h.Session.Buttons.First(b => b.Name == "Lights").Response);
    }
}
