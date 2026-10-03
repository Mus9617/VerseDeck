using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;
using VerseDeck.Core.Models;
using VerseDeck.Speech;
using VerseDeck.Voice;

namespace VerseDeck.Tests;

public class VoiceDoctorTests
{
    private static long Id(Harness h, string module) => h.Session.Buttons.First(b => b.Name == module).Id;

    private static async Task EditAsync(Harness h, string module)
    {
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(module));
    }

    [Fact]
    public async Task TestPhrase_UnderstoodAsThisModule_IsOk()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.StartsWith("Bien", h.Shell.Editor.PhraseTestResult);
        Assert.Contains("dame luz", h.Checker.Grammars.Single());
        Assert.Contains("activar luces", h.Checker.Grammars.Single());
    }

    [Fact]
    public async Task TestPhrase_HeardAsAnotherModulesPhrase_IsConfused()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Self Destruct");
        h.Checker.HeardAs["self destruct"] = new PhraseHit("shields off", 0.46, false);
        h.Shell.Editor.NewPhrase = "self destruct";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.Contains("Se confunde con Shields", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task TestPhrase_HeardAsAnotherPhraseOfTheSameModule_IsOk()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Landing Gear");
        h.Checker.HeardAs["gear up"] = new PhraseHit("gear down", 0.66, false);
        h.Shell.Editor.NewPhrase = "gear up";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.StartsWith("Bien", h.Shell.Editor.PhraseTestResult);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("vamos a por ellos", true)]
    public async Task TestPhrase_NotHeardOrDiscarded_IsNotUnderstood(string? heard, bool discarded)
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Checker.HeardAs["dame luz"] = new PhraseHit(heard, 0.7, discarded);
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.StartsWith("No la entiende", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task TestPhrase_WithoutAnyVoice_ExplainsWhatIsNeeded()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        await EditAsync(h, "Lights");
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.Contains("Descarga una voz", h.Shell.Editor.PhraseTestResult);
        Assert.Equal(0, h.Checker.Calls);
    }

    [Fact]
    public async Task TestPhrase_CheckerFailure_IsReported_NotThrown()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Checker.Fail = new InvalidOperationException("Windows no tiene instalado un reconocedor");
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.Contains("No se pudo comprobar", h.Shell.Editor.PhraseTestResult);
        Assert.Contains("reconocedor", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task TestPhrase_ReleasesTheVoiceModelAfterwards()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.True(h.Tts.Unloads >= 1);
    }

    [Fact]
    public async Task WorstVoiceWins_WhenSeveralVoicesAreInstalled()
    {
        await using var h = await Harness.CreateAsync();
        FakeVoiceInstaller.Put(h.VoiceStore, VoiceCatalog.Load().Find("piper-claude-mx")!);
        h.Checker.HeardAsByVoice[("piper-claude-mx", "dame luz")] = new PhraseHit("activar armas", 0.5, false);
        await EditAsync(h, "Lights");
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.Contains("Se confunde con Weapons", h.Shell.Editor.PhraseTestResult);
        Assert.Equal(2, h.Checker.Calls);
    }

    [Fact]
    public async Task CheckAll_ListsEveryEnabledPhrase_ProblemsFirst()
    {
        await using var h = await Harness.CreateAsync();
        h.Checker.HeardAs["self destruct"] = new PhraseHit("shields off", 0.46, false);
        h.Checker.HeardAs["eyectar"] = new PhraseHit(null, 0, false);

        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);

        var results = h.Shell.Voice.CheckResults;
        Assert.Equal(h.Session.VoiceCommands.Select(c => c.Phrase).Distinct().Count(), results.Count);
        Assert.Equal(VerdictKind.Confused, results[0].Kind);
        Assert.Equal("self destruct", results[0].Phrase);
        Assert.Equal("Shields", results[0].ConfusedWith);
        Assert.Equal(VerdictKind.NotUnderstood, results[1].Kind);
        Assert.All(results.Skip(2), r => Assert.Equal(VerdictKind.Ok, r.Kind));
        Assert.Contains("2 de", h.Shell.Voice.CheckStatus);
        Assert.False(h.Shell.Voice.IsChecking);
    }

    [Fact]
    public async Task CheckAll_Cancel_StopsCleanly()
    {
        await using var h = await Harness.CreateAsync();
        h.Checker.Hold = new TaskCompletionSource();

        var running = h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);
        Assert.True(h.Shell.Voice.IsChecking);
        h.Shell.Voice.CancelCheckCommand.Execute(null);
        await running;

        Assert.False(h.Shell.Voice.IsChecking);
        Assert.Contains("cancelada", h.Shell.Voice.CheckStatus);
        Assert.Empty(h.Shell.Voice.CheckResults);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task CheckAll_WithoutVoice_ShowsTheReason()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);

        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Contains("Descarga una voz", h.Shell.Voice.CheckStatus);
    }

    [Fact]
    public async Task CheckAll_RendersEachPhraseOnce_ThenReusesTheCache()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);
        var synthesized = h.Tts.Synthesized.Count;
        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);

        Assert.Equal(synthesized, h.Tts.Synthesized.Count);
        Assert.Equal(h.Session.VoiceCommands.Select(c => c.Phrase).Distinct().Count(), synthesized);
    }
}

public class RecognitionHistoryTests
{
    private static async Task<Harness> ListeningAsync()
    {
        var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        return h;
    }

    private static async Task HearAsync(Harness h, string module)
    {
        var button = h.Session.Buttons.First(b => b.Name == module);
        h.Voice.Raise(h.Session.VoiceCommands.First(v => v.ButtonId == button.Id), button);
        await h.Shell.Voice.Pending;
    }

    [Fact]
    public async Task ExecutedCommand_IsRecorded()
    {
        await using var h = await ListeningAsync();

        await HearAsync(h, "Lights");

        var row = Assert.Single(h.Shell.Voice.History);
        Assert.Equal(RecognitionOutcome.Executed, row.Outcome);
        Assert.Equal("Ejecutado", row.OutcomeText);
        Assert.Equal("Positive", row.OutcomeKey);
    }

    [Fact]
    public async Task EngineRejections_AreRecorded_WithTheirReason()
    {
        await using var h = await ListeningAsync();

        h.Voice.RaiseHeard("vamos a por ellos", RecognitionOutcome.Discarded);
        h.Voice.RaiseHeard("activar luces", RecognitionOutcome.LowConfidence, 0.21);

        Assert.Equal(RecognitionOutcome.LowConfidence, h.Shell.Voice.History[0].Outcome);
        Assert.Equal("Descartado: confianza baja", h.Shell.Voice.History[0].OutcomeText);
        Assert.Equal("Descartado: sonaba a conversacion", h.Shell.Voice.History[1].OutcomeText);
    }

    [Fact]
    public async Task Repeat_IsRecorded()
    {
        await using var h = await ListeningAsync();

        await HearAsync(h, "Lights");
        await HearAsync(h, "Lights");

        Assert.Equal(RecognitionOutcome.Repeated, h.Shell.Voice.History[0].Outcome);
        Assert.Single(h.Sender.Sent);
    }

    [Fact]
    public async Task CopilotSpeaking_IsRecorded()
    {
        await using var h = await ListeningAsync();
        await h.EnableCopilotAsync();
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Star Map"));
        await h.Copilot.Pending;

        await HearAsync(h, "Lights");

        Assert.Equal(RecognitionOutcome.CopilotSpeaking, h.Shell.Voice.History[0].Outcome);
    }

    [Fact]
    public async Task DeletedModule_IsRecorded()
    {
        await using var h = await ListeningAsync();
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");
        var command = h.Session.VoiceCommands.First(v => v.ButtonId == lights.Id);
        await h.Session.DeleteButtonAsync(lights.Id);
        await h.Shell.Voice.Pending;

        h.Voice.Raise(command, lights);
        await h.Shell.Voice.Pending;

        Assert.Equal(RecognitionOutcome.ModuleGone, h.Shell.Voice.History[0].Outcome);
    }

    [Fact]
    public async Task FailedSend_IsRecorded()
    {
        await using var h = await ListeningAsync();
        h.Sender.ThrowOnSend = true;

        await HearAsync(h, "Lights");

        Assert.Equal(RecognitionOutcome.Failed, h.Shell.Voice.History[0].Outcome);
        Assert.Equal("Danger", h.Shell.Voice.History[0].OutcomeKey);
    }

    [Fact]
    public async Task History_KeepsTheLatestTwenty_NewestFirst()
    {
        await using var h = await ListeningAsync();

        for (var i = 0; i < 25; i++)
        {
            h.Voice.RaiseHeard($"frase {i}", RecognitionOutcome.Discarded);
        }

        Assert.Equal(VoiceViewModel.HistorySize, h.Shell.Voice.History.Count);
        Assert.Equal("frase 24", h.Shell.Voice.History[0].Text);
        Assert.Equal("frase 5", h.Shell.Voice.History[^1].Text);
    }
}
