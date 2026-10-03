using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Data;

namespace VerseDeck.Tests;

public class ChecklistPersistenceTests
{
    [Fact]
    public async Task ExamplesAreSeeded_OnceOnly()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var profile = (await repository.GetProfilesAsync()).Single(p => p.IsActive);
        var seeded = await repository.GetChecklistsAsync(profile.Id);
        Assert.Equal(["Aterrizaje", "Prevuelo"], seeded.Select(c => c.Name).Order());

        // A player who deletes an example does not get it back on the next start.
        await repository.DeleteChecklistAsync(seeded[0].Id);
        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();

        Assert.Single(await again.GetChecklistsAsync(profile.Id));
    }

    [Fact]
    public async Task SeededSteps_PointAtTheProfilesModules()
    {
        await using var h = await Harness.CreateAsync();
        var preflight = h.Session.Checklists.Single(c => c.Name == "Prevuelo");

        var linked = preflight.Steps.Where(s => s.ButtonId is not null).Select(s => h.Session.Buttons.Single(b => b.Id == s.ButtonId).Name);
        Assert.Contains("Lights", linked);
        Assert.Contains(preflight.Steps, s => s.ButtonId is null);
        Assert.Equal(Enumerable.Range(1, preflight.Steps.Count), preflight.Steps.Select(s => s.Position));
    }

    [Fact]
    public async Task OldDatabase_WithoutTheTables_IsMigrated()
    {
        await using var db = new TempDatabase();
        await db.CreateAsync();
        await db.ExecuteAsync("DROP TABLE ChecklistSteps; DROP TABLE Checklists; DROP TABLE LogbookNotes; DELETE FROM Settings WHERE Key = 'ChecklistsSeededV1';");

        var repository = new SqliteVerseDeckRepository(db.Path);
        await repository.InitializeAsync();
        var profile = (await repository.GetProfilesAsync()).Single(p => p.IsActive);

        Assert.Equal(2, (await repository.GetChecklistsAsync(profile.Id)).Count);
        Assert.Empty(await repository.GetNotesAsync(10));
    }

    [Fact]
    public async Task SaveAndReload_KeepsStepsInOrder_AndEditsReplaceThem()
    {
        await using var h = await Harness.CreateAsync();
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");

        var saved = await h.Session.SaveChecklistAsync(new Checklist(0, 0, "  Salto  ", [new(0, 1, "Uno", lights.Id), new(0, 2, "Dos", null)]));
        Assert.Equal("Salto", saved.Name);
        await h.Session.SaveChecklistAsync(saved with { Steps = [new(0, 1, "Solo", null)] });

        var reloaded = h.Session.Checklists.Single(c => c.Id == saved.Id);
        Assert.Equal(["Solo"], reloaded.Steps.Select(s => s.Text));
    }

    [Fact]
    public async Task DuplicateOrEmptyNames_AreRefused()
    {
        await using var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.SaveChecklistAsync(new Checklist(0, 0, "prevuelo", [new(0, 1, "x", null)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.SaveChecklistAsync(new Checklist(0, 0, " ", [new(0, 1, "x", null)])));
    }

    [Fact]
    public async Task NewProfile_CopiesChecklists_WithItsOwnModules()
    {
        await using var h = await Harness.CreateAsync();
        var oldLights = h.Session.Buttons.First(b => b.Name == "Lights").Id;

        await h.Session.SaveProfileAsync("Carguero", "C2");

        var newLights = h.Session.Buttons.First(b => b.Name == "Lights").Id;
        Assert.NotEqual(oldLights, newLights);
        var steps = h.Session.Checklists.Single(c => c.Name == "Prevuelo").Steps;
        Assert.Contains(steps, s => s.ButtonId == newLights);
        Assert.DoesNotContain(steps, s => s.ButtonId == oldLights);
    }
}

public class ChecklistRunnerTests
{
    private static async Task<(Harness H, ChecklistRunner Runner)> WithChecklistAsync()
    {
        var h = await Harness.CreateAsync();
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");
        await h.Session.SaveChecklistAsync(new Checklist(0, 0, "Prueba", [new(0, 1, "Luces", lights.Id), new(0, 2, "Mirar", null)]));
        return (h, h.Shell.Checklists);
    }

    [Fact]
    public async Task Done_WithNothingRunning_DoesNothing()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(RecognitionOutcome.NothingToDo, await h.Shell.Checklists.DoneAsync("Voice"));
        Assert.False(h.Shell.Checklists.Skip());
        Assert.False(h.Shell.Checklists.Repeat());
        Assert.False(h.Shell.Checklists.Cancel());
        Assert.Empty(h.Sender.Sent);
    }

    [Fact]
    public async Task EachDone_SendsAtMostOnePress_AndTheChecklistFinishes()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;

        Assert.True(runner.Start("prueba"));
        Assert.Equal("Paso 1 de 2: Luces", runner.StatusText);

        await runner.DoneAsync("Voice");
        Assert.Single(h.Sender.Sent);
        Assert.Equal(1, runner.Index);

        await runner.DoneAsync("Voice");
        Assert.Single(h.Sender.Sent);
        Assert.Null(runner.Active);
        Assert.Equal("Checklist Prueba completa", runner.StatusText);
    }

    [Fact]
    public async Task BlockedStep_DoesNotAdvance()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;
        h.Shell.Checklists.Start("Prueba");
        h.Sender.ThrowOnSend = true;

        Assert.Equal(RecognitionOutcome.Failed, await runner.DoneAsync("Voice"));

        Assert.Equal(0, runner.Index);
        Assert.StartsWith("Paso 1 sin completar", runner.StatusText);

        runner.Skip();
        Assert.Equal(1, runner.Index);
    }

    [Fact]
    public async Task UnknownName_DoesNotStart()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;

        Assert.False(runner.Start("inexistente"));
        Assert.Null(runner.Active);
    }

    [Fact]
    public async Task DeletingTheRunningChecklist_StopsIt()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;
        runner.Start("Prueba");

        await h.Session.DeleteChecklistAsync(runner.Active!.Id);

        Assert.Null(runner.Active);
        Assert.Equal(RecognitionOutcome.NothingToDo, await runner.DoneAsync("Voice"));
    }

    [Fact]
    public async Task DoneHeardDuringALongPress_IsIgnored()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;
        runner.Start("Prueba");
        h.Sender.Hold = new TaskCompletionSource();

        var first = runner.DoneAsync("Voice");
        var second = await runner.DoneAsync("Voice");
        h.Sender.Hold.SetResult();
        await first;

        Assert.Equal(RecognitionOutcome.Repeated, second);
        Assert.Single(h.Sender.Sent);
        Assert.Equal(1, runner.Index);
    }

    [Fact]
    public async Task CancelDuringAPress_DoesNotAdvanceOrCrash()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;
        runner.Start("Prueba");
        h.Sender.Hold = new TaskCompletionSource();

        var done = runner.DoneAsync("Voice");
        runner.Cancel();
        h.Sender.Hold.SetResult();

        Assert.Equal(RecognitionOutcome.Executed, await done);
        Assert.Null(runner.Active);
        Assert.Equal("Checklist Prueba cancelada", runner.StatusText);
    }

    [Fact]
    public async Task DeletedModule_LeavesTheStepWithoutAKey()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;

        await h.Session.DeleteButtonAsync(h.Session.Buttons.First(b => b.Name == "Lights").Id);

        Assert.Null(h.Session.Checklists.Single(c => c.Name == "Prueba").Steps[0].ButtonId);
    }

    [Fact]
    public async Task Steps_AreAnnouncedByTheCopilot()
    {
        var (h, runner) = await WithChecklistAsync();
        await using var _ = h;
        await h.EnableCopilotAsync();

        runner.Start("Prueba");
        await runner.Pending;

        Assert.Contains("Paso 1 de 2: Luces.", h.Tts.Synthesized);
    }
}

public class TimerServiceTests
{
    [Fact]
    public async Task ManyTimers_UseASingleSystemTimer_AndNoneWhenEmpty()
    {
        await using var h = await Harness.CreateAsync();
        var timers = h.Shell.Timers;
        Assert.False(timers.IsScheduled);

        timers.Add(null, TimeSpan.FromMinutes(10));
        timers.Add("hangar", TimeSpan.FromMinutes(5));
        timers.Add("carga", TimeSpan.FromMinutes(20));

        Assert.True(timers.IsScheduled);
        Assert.Equal(0, h.Ui.Fire(TimeSpan.FromMinutes(10)));
        Assert.Equal(0, h.Ui.Fire(TimeSpan.FromMinutes(20)));

        timers.CancelAll();
        Assert.False(timers.IsScheduled);
        Assert.Equal(0, h.Ui.Fire(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task DueTimer_Fires_AndTheNextOneIsScheduled()
    {
        await using var h = await Harness.CreateAsync();
        var timers = h.Shell.Timers;
        timers.Add("hangar", TimeSpan.FromMinutes(5));
        timers.Add("carga", TimeSpan.FromMinutes(20));

        h.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(1, h.Ui.Fire(TimeSpan.FromMinutes(5)));

        Assert.True(timers.Timers.Single(t => t.Label == "hangar").Fired);
        Assert.False(timers.Timers.Single(t => t.Label == "carga").Fired);
        Assert.Equal(1, h.Audio.CommandPlays);
        h.Now += TimeSpan.FromMinutes(15);
        Assert.Equal(1, h.Ui.Fire(TimeSpan.FromMinutes(15)));
        Assert.Equal(2, h.Audio.CommandPlays);
        Assert.False(timers.IsScheduled);
    }

    [Fact]
    public async Task TimersDueTogether_AreOnePhrase()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Shell.Timers.Add("hangar", TimeSpan.FromMinutes(5));
        h.Shell.Timers.Add("carga", TimeSpan.FromMinutes(5));

        h.Now += TimeSpan.FromMinutes(5);
        h.Ui.Fire(TimeSpan.FromMinutes(5));
        await h.Copilot.Pending;
        await Task.Delay(200);

        Assert.Contains("Hangar: han pasado cinco minutos. Carga: han pasado cinco minutos.", h.Tts.Synthesized);
    }

    [Fact]
    public async Task FiredTimer_IsSpoken_WhenTheCopilotIsOn()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Shell.Timers.Add("refinería", TimeSpan.FromMinutes(30));

        h.Now += TimeSpan.FromMinutes(30);
        h.Ui.Fire(TimeSpan.FromMinutes(30));
        await h.Copilot.Pending;

        Assert.Contains("Refinería: han pasado treinta minutos.", h.Tts.Synthesized);
        Assert.Equal(0, h.Audio.CommandPlays);
    }

    [Theory]
    [InlineData(60, "un minuto")]
    [InlineData(600, "diez minutos")]
    [InlineData(30, "treinta segundos")]
    [InlineData(90, "noventa segundos")]
    [InlineData(1, "un segundo")]
    public void Describe_IsSpokenSpanish(int seconds, string words)
    {
        Assert.Equal(words, TimerService.Describe(TimeSpan.FromSeconds(seconds)));
    }
}

public class LogbookTests
{
    [Fact]
    public async Task Notes_AreTagged_Listed_AndDeleted()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Aboard.Pending;

        h.Shell.Aboard.NewNote = "  dejé la Cutlass en Lorville ";
        await h.Shell.Aboard.AddNoteCommand.ExecuteAsync(null);
        await h.Shell.Aboard.Pending;

        var note = Assert.Single(h.Shell.Aboard.Notes);
        Assert.Equal("dejé la Cutlass en Lorville", note.Text);
        Assert.Equal(h.Session.ActiveProfile!.Name, note.ProfileName);
        Assert.Equal(h.Now, note.CreatedAt);
        Assert.Empty(h.Shell.Aboard.NewNote);

        await h.Shell.Aboard.DeleteNoteCommand.ExecuteAsync(note);
        await h.Shell.Aboard.Pending;
        Assert.Empty(h.Shell.Aboard.Notes);
    }

    [Fact]
    public async Task EmptyNote_IsNotSaved()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.Aboard.NewNote = "   ";
        await h.Shell.Aboard.AddNoteCommand.ExecuteAsync(null);

        Assert.Empty(await h.Repository.GetNotesAsync(10));
    }

    [Fact]
    public async Task Export_WritesOldestFirst()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Aboard.HandleAsync(new CompanionCommand(CompanionKind.Note, Text: "primera"));
        h.Now += TimeSpan.FromMinutes(1);
        await h.Shell.Aboard.HandleAsync(new CompanionCommand(CompanionKind.Note, Text: "segunda"));

        await h.Shell.Aboard.ExportCommand.ExecuteAsync(null);

        var file = Assert.Single(Directory.GetFiles(Path.Combine(h.SpeechRoot, "export")));
        var lines = File.ReadAllLines(file);
        Assert.EndsWith("primera", lines[0]);
        Assert.EndsWith("segunda", lines[1]);
    }
}

public class CompanionRoutingTests
{
    private static async Task<Harness> ListeningAsync()
    {
        var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        return h;
    }

    [Fact]
    public async Task Engine_IsGivenTheChecklistNames_AndRestartsWhenTheyChange()
    {
        await using var h = await ListeningAsync();
        Assert.Contains("Prevuelo", h.Voice.ChecklistNames);
        var starts = h.Voice.StartCount;

        await h.Session.SaveChecklistAsync(new Checklist(0, 0, "Minado", [new(0, 1, "Escanear", null)]));
        await h.Shell.Voice.Pending;

        Assert.True(h.Voice.StartCount > starts);
        Assert.Contains("Minado", h.Voice.ChecklistNames);
    }

    [Fact]
    public async Task VoiceRunsAChecklist_AndRecordsIt()
    {
        await using var h = await ListeningAsync();

        h.Voice.RaiseCompanion("checklist prevuelo");
        await h.Shell.Voice.Pending;

        Assert.Equal("Prevuelo", h.Shell.Checklists.Active?.Name);
        Assert.Equal(RecognitionOutcome.Executed, h.Shell.Voice.History[0].Outcome);
        Assert.True(h.Shell.Aboard.IsRunning);
    }

    [Fact]
    public async Task DoneWithNothingRunning_IsRecordedAsNothingToDo()
    {
        await using var h = await ListeningAsync();

        h.Voice.RaiseCompanion("hecho");
        await h.Shell.Voice.Pending;

        Assert.Equal(RecognitionOutcome.NothingToDo, h.Shell.Voice.History[0].Outcome);
        Assert.Equal("Ignorado: nada en marcha", h.Shell.Voice.History[0].OutcomeText);
        Assert.Empty(h.Sender.Sent);
    }

    [Fact]
    public async Task EchoOfTheSamePhrase_IsIgnored()
    {
        await using var h = await ListeningAsync();
        h.Voice.RaiseCompanion("checklist prevuelo");
        await h.Shell.Voice.Pending;

        h.Voice.RaiseCompanion("hecho");
        await h.Shell.Voice.Pending;

        Assert.Equal(RecognitionOutcome.Repeated, h.Shell.Voice.History[0].Outcome);
        Assert.Equal(0, h.Shell.Checklists.Index);
    }

    [Fact]
    public async Task WithVoiceOff_NothingHappens()
    {
        await using var h = await Harness.CreateAsync();

        h.Voice.RaiseCompanion("avísame en diez minutos");
        await h.Shell.Voice.Pending;

        Assert.Empty(h.Shell.Timers.Timers);
        Assert.Equal(RecognitionOutcome.Offline, h.Shell.Voice.History[0].Outcome);
    }

    [Fact]
    public async Task CompanionCommand_WhileTheCopilotSpeaks_IsIgnored()
    {
        await using var h = await ListeningAsync();
        h.Guard.Speaking(h.Now, h.Now + TimeSpan.FromSeconds(3));

        h.Voice.RaiseCompanion("checklist prevuelo", h.Now);
        await h.Shell.Voice.Pending;

        Assert.Null(h.Shell.Checklists.Active);
        Assert.Equal(RecognitionOutcome.CopilotSpeaking, h.Shell.Voice.History[0].Outcome);
    }

    [Fact]
    public async Task FailedStepPress_IsRecordedAsFailed()
    {
        await using var h = await ListeningAsync();
        await h.Session.SaveChecklistAsync(new Checklist(0, 0, "Prueba", [new(0, 1, "Luces", h.Session.Buttons.First(b => b.Name == "Lights").Id)]));
        h.Shell.Checklists.Start("Prueba");
        h.Sender.ThrowOnSend = true;

        h.Voice.RaiseCompanion("hecho");
        await h.Shell.Voice.Pending;

        Assert.Equal(RecognitionOutcome.Failed, h.Shell.Voice.History[0].Outcome);
    }

    [Fact]
    public async Task VoiceTimerAndNote_Work()
    {
        await using var h = await ListeningAsync();

        h.Voice.RaiseCompanion("refinería treinta minutos");
        await h.Shell.Voice.Pending;
        h.Now += TimeSpan.FromSeconds(5);
        h.Voice.RaiseCompanion("anota la carga está en el hangar dos");
        await h.Shell.Voice.Pending;
        await h.Shell.Aboard.Pending;

        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(h.Shell.Timers.Timers).Duration);
        var note = Assert.Single(h.Shell.Aboard.Notes);
        Assert.Equal("la carga está en el hangar dos", note.Text);
        Assert.Equal("Voz", note.Source);
    }
}

public class AboardViewModelTests
{
    [Fact]
    public async Task Countdown_TicksOnlyWhileVisible_AndWithTimers()
    {
        await using var h = await Harness.CreateAsync();
        var aboard = h.Shell.Aboard;

        h.Shell.Timers.Add(null, TimeSpan.FromMinutes(2));
        Assert.False(aboard.IsCountingDown);
        Assert.Equal("02:00", aboard.Timers[0].RemainingText);

        h.Shell.Section = "Abordo";
        Assert.True(aboard.IsCountingDown);
        h.Now += TimeSpan.FromSeconds(1);
        h.Ui.Fire(TimeSpan.FromSeconds(1));
        Assert.Equal("01:59", aboard.Timers[0].RemainingText);

        h.Shell.Section = "Deck";
        Assert.False(aboard.IsCountingDown);
        Assert.Equal(0, h.Ui.Fire(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UnsavedDraft_SurvivesAnUnrelatedReload()
    {
        await using var h = await Harness.CreateAsync();
        var aboard = h.Shell.Aboard;
        aboard.NewChecklistCommand.Execute(null);
        aboard.EditName = "Borrador";
        aboard.EditSteps[0].Text = "Algo";
        aboard.EditSteps[0].Module = aboard.ModuleChoices.First(m => m.Name == "Lights");

        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVolume = 0.5 });

        Assert.Null(aboard.SelectedChecklist);
        Assert.Equal("Borrador", aboard.EditName);
        Assert.Equal("Lights", aboard.EditSteps[0].Module?.Name);

        aboard.SelectedChecklist = aboard.Checklists.First(c => c.Name == "Prevuelo");
        aboard.EditSteps[0].Text = "Cambiado";
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVolume = 0.6 });
        Assert.Equal("Cambiado", aboard.EditSteps[0].Text);
    }

    [Fact]
    public async Task Editor_LoadsSelected_AndSavesANewChecklist()
    {
        await using var h = await Harness.CreateAsync();
        var aboard = h.Shell.Aboard;
        Assert.Equal(2, aboard.Checklists.Count);
        Assert.NotEmpty(aboard.EditSteps);

        aboard.NewChecklistCommand.Execute(null);
        aboard.EditName = "Combate";
        aboard.EditSteps[0].Text = "Escudos";
        aboard.EditSteps[0].Module = aboard.ModuleChoices.First(m => m.Name == "Lights");
        aboard.AddStepCommand.Execute(null);
        await aboard.SaveChecklistCommand.ExecuteAsync(null);

        var saved = h.Session.Checklists.Single(c => c.Name == "Combate");
        Assert.Single(saved.Steps);
        Assert.Equal("Combate", aboard.SelectedChecklist?.Name);
    }
}

public class AboardSpeechTests
{
    [Fact]
    public async Task WarmUp_RendersChecklistAndTimerPhrases_Ahead()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Copilot.WarmUpAsync();

        Assert.Contains("Paso 1 de 3: Pedir permiso de aterrizaje.", h.Tts.Synthesized);
        Assert.Contains("Checklist Prevuelo completa.", h.Tts.Synthesized);
        Assert.Contains("Temporizador, diez minutos.", h.Tts.Synthesized);
        Assert.Contains("Temporizador: han pasado treinta minutos.", h.Tts.Synthesized);
        Assert.Contains("Anotado.", h.Tts.Synthesized);
    }

    [Fact]
    public async Task SavedChecklist_IsRenderedWhileEditing()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        var aboard = h.Shell.Aboard;

        aboard.NewChecklistCommand.Execute(null);
        aboard.EditName = "Minado";
        aboard.EditSteps[0].Text = "Abrir el escaner";
        await aboard.SaveChecklistCommand.ExecuteAsync(null);
        await aboard.Pending;

        Assert.Contains("Paso 1 de 1: Abrir el escaner.", h.Tts.Synthesized);
    }

    [Fact]
    public async Task SlowAnnouncement_IsStillSaid()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Shell.Timers.Add("hangar", TimeSpan.FromMinutes(7));
        var alert = "Hangar: han pasado siete minutos.";
        h.Tts.Hold(alert);

        h.Now += TimeSpan.FromMinutes(7);
        h.Ui.Fire(TimeSpan.FromMinutes(7));
        h.Now += TimeSpan.FromSeconds(5);
        h.Tts.Release(alert);
        await h.Copilot.Pending;
        await Task.Delay(200);

        Assert.Contains($"Copilot said '{alert}' (generated)", h.Log.Lines.Select(l => l.Trim()));
    }

    [Fact]
    public async Task ModelLoadedOnTheFly_IsReleasedWhenIdle()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Copilot.IdleUnload = TimeSpan.FromMilliseconds(20);
        var before = h.Tts.Unloads;

        await h.Copilot.SayAsync("Frase nunca dicha antes.");
        await h.Copilot.IdleRelease;

        Assert.Equal(before + 1, h.Tts.Unloads);
    }

    [Fact]
    public async Task CachedPhrase_DoesNotScheduleARelease()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Copilot.SayAsync("Frase repetida.");
        h.Copilot.IdleUnload = TimeSpan.FromMilliseconds(20);
        await h.Copilot.IdleRelease;
        var before = h.Tts.Unloads;
        var release = h.Copilot.IdleRelease;

        await h.Copilot.SayAsync("Frase repetida.");

        Assert.Same(release, h.Copilot.IdleRelease);
        Assert.Equal(before, h.Tts.Unloads);
    }
}

public class AboardReviewTests
{
    [Fact]
    public async Task FailedSynthesis_StillReleasesTheModel()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Copilot.IdleUnload = TimeSpan.FromMilliseconds(20);
        h.Tts.ThrowFor.Add("Esta falla.");
        var before = h.Tts.Unloads;

        await h.Copilot.SayAsync("Esta falla.");
        await h.Copilot.IdleRelease;

        Assert.Equal(before + 1, h.Tts.Unloads);
    }

    [Fact]
    public async Task InterruptedSeed_DoesNotDuplicateTheFirstExample()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var profile = (await repository.GetProfilesAsync()).Single(p => p.IsActive);
        var landing = (await repository.GetChecklistsAsync(profile.Id)).Single(c => c.Name == "Aterrizaje");
        await repository.DeleteChecklistAsync(landing.Id);
        await db.ExecuteAsync("DELETE FROM Settings WHERE Key = 'ChecklistsSeededV1';");

        var again = new SqliteVerseDeckRepository(db.Path);
        await again.InitializeAsync();

        Assert.Equal(["Aterrizaje", "Prevuelo"], (await again.GetChecklistsAsync(profile.Id)).Select(c => c.Name).Order());
    }
}

public class AboardWindowTests
{
    [Fact]
    public async Task MinimizedWindow_StopsTheCountdown()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Timers.Add(null, TimeSpan.FromMinutes(2));
        h.Shell.Section = "Abordo";
        Assert.True(h.Shell.Aboard.IsCountingDown);

        h.Shell.IsMinimized = true;
        Assert.False(h.Shell.Aboard.IsCountingDown);

        h.Shell.IsMinimized = false;
        Assert.True(h.Shell.Aboard.IsCountingDown);
    }
}
