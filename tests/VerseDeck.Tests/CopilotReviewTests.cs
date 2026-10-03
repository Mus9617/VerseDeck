using System.Net;
using System.Net.Http.Json;
using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;
using VerseDeck.Core.Models;
using VerseDeck.Speech;

namespace VerseDeck.Tests;

/// <summary>Cases found by the branch review of the copilot voice.</summary>
public class CopilotReviewTests
{
    private static readonly ResponsePack Sobria = ResponsePack.LoadAll().First(p => p.Id == "sobria");

    private static DeckButton Button(Harness h, string name) => h.Session.Buttons.First(b => b.Name == name);

    private static async Task<Harness> EnabledAsync(string mode = "ManualToggle")
    {
        var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Shell.Voice.Mode = mode;
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        return h;
    }

    private static async Task PressAsync(Harness h, string module)
    {
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(module));
        await h.Copilot.Pending;
    }

    private static async Task HearAsync(Harness h, string module, DateTimeOffset? heardAt = null)
    {
        var button = Button(h, module);
        h.Voice.Raise(h.Session.VoiceCommands.First(v => v.ButtonId == button.Id), button, heardAt);
        await h.Shell.Voice.Pending;
        await h.Copilot.Pending;
    }

    // ---- The copilot must never trigger a press with its own voice ----

    [Theory]
    [InlineData("PushToTalk")]
    [InlineData("ManualToggle")]
    public async Task WhileTheCopilotSpeaks_WhatTheMicrophoneHears_IsIgnored_InEveryMode(string mode)
    {
        await using var h = await EnabledAsync(mode);
        h.Tts.Seconds = 2;

        await PressAsync(h, "Star Map");
        h.Now += TimeSpan.FromSeconds(1);
        await HearAsync(h, "Lights");

        Assert.Single(h.Sender.Sent);
    }

    [Fact]
    public async Task EchoRecognisedLate_IsStillIgnored_BecauseItWasHeardWhileSpeaking()
    {
        await using var h = await EnabledAsync();
        h.Tts.Seconds = 2;
        await PressAsync(h, "Star Map");
        var heardAt = h.Now + TimeSpan.FromSeconds(1);

        // The recogniser reports the utterance only after its end-of-speech silence, once the guard has expired.
        h.Now += TimeSpan.FromSeconds(4);
        await HearAsync(h, "Lights", heardAt);

        Assert.Single(h.Sender.Sent);
    }

    [Fact]
    public async Task CommandSpokenAfterTheCopilotFinished_IsAccepted()
    {
        await using var h = await EnabledAsync();
        h.Tts.Seconds = 2;
        await PressAsync(h, "Star Map");

        h.Now += TimeSpan.FromSeconds(4);
        await HearAsync(h, "Lights", h.Now - TimeSpan.FromSeconds(0.8));

        Assert.Equal(2, h.Sender.Sent.Count);
    }

    [Fact]
    public async Task VoiceCommand_AnsweredByTheCopilot_DoesNotChainIntoASecondPress()
    {
        await using var h = await EnabledAsync("PushToTalk");
        h.Tts.Seconds = 1.5;

        await HearAsync(h, "Landing Gear");
        Assert.Single(h.Sender.Sent);
        Assert.Single(h.Player.Played);

        // The microphone hears "Tren de aterrizaje." inside the push-to-talk grace window.
        h.Now += TimeSpan.FromSeconds(1.2);
        await HearAsync(h, "Landing Gear");

        Assert.Single(h.Sender.Sent);
    }

    [Fact]
    public async Task Mute_EndsTheGuard_SoCommandsAreHeardAgainAtOnce()
    {
        await using var h = await EnabledAsync();
        h.Tts.Seconds = 5;
        await PressAsync(h, "Star Map");

        h.Shell.IsMuted = true;
        h.Now += TimeSpan.FromSeconds(1);
        await HearAsync(h, "Lights");

        Assert.Equal(2, h.Sender.Sent.Count);
    }

    // ---- A subscriber can never change the outcome of a press ----

    [Theory]
    [InlineData("Luces.|Luces.")]
    [InlineData("x||x")]
    public async Task CustomResponseWithRepeatedVariants_NeverThrows(string response)
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Session.SaveButtonAsync(Button(h, "Lights") with { Response = response });

        await PressAsync(h, "Lights");
        await PressAsync(h, "Lights");
        await PressAsync(h, "Lights");

        Assert.Equal(3, h.Sender.Sent.Count);
        Assert.Equal(3, h.Player.Played.Count);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotTurnASuccessfulPressIntoAFailure()
    {
        await using var db = new TempDatabase();
        var repository = await db.CreateAsync();
        var session = new DeckSession(repository);
        await session.LoadAsync();
        var sender = new FakeInputSender();
        var log = new FakeLog();
        var executor = new ButtonExecutor(sender, repository, new FakeDialogService(), new FakeAudio(), log, () => session.Settings);
        var later = 0;
        executor.Sent += (_, _) => throw new InvalidOperationException("subscriber bug");
        executor.Sent += (_, _) => later++;

        var result = await executor.ExecuteAsync(session.Buttons.First(b => b.Name == "Lights"), "Windows");

        Assert.Equal(ExecuteResult.Sent, result);
        Assert.Single(sender.Sent);
        Assert.Equal(1, later);
        Assert.Contains(log.Lines, line => line.Contains("subscriber bug"));
    }

    // ---- The block for links without a usable key ----

    [Fact]
    public async Task LinkedModule_IsNotBlocked_WhenThePlayersBindsCouldNotBeRead()
    {
        await using var h = await Harness.CreateAsync(withGame: false);
        await h.Session.SaveButtonAsync(Button(h, "Doors") with { GameAction = "doors_toggle" });
        await h.ControlSync.Pending;

        await PressAsync(h, "Doors");

        Assert.Equal("K", Assert.Single(h.Sender.Sent).Key);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Shell_GivesThePhoneTheSameBlock()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        Assert.NotNull(h.Mobile.BlockReason);
        Assert.NotNull(h.Mobile.BlockReason!(Button(h, "Doors")));
        Assert.Null(h.Mobile.BlockReason!(Button(h, "Flight Ready")));
    }

    // ---- Failure, no-key and profile phrases respect the switches ----

    [Fact]
    public async Task VoiceOnly_KeepsQuietAboutABlockedClick()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVoiceOnly = true });
        await h.Shell.Controls.LinkPresetsCommand.ExecuteAsync(null);

        await PressAsync(h, "Doors");

        Assert.Empty(h.Player.Played);
    }

    [Fact]
    public async Task SilentModule_KeepsQuietAboutItsOwnFailure()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Session.SaveButtonAsync(Button(h, "Lights") with { Response = "-" });
        h.Sender.ThrowOnSend = true;

        await PressAsync(h, "Lights");

        Assert.Empty(h.Player.Played);
    }

    [Fact]
    public async Task VoiceOnly_DoesNotAnnounceAProfileLoadedByClick()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotVoiceOnly = true });

        await h.Session.SaveProfileAsync("Combate", "Aegis Gladius");
        await h.Copilot.Pending;

        Assert.Empty(h.Player.Played);
    }

    // ---- Superseded and stale answers ----

    [Fact]
    public async Task SupersededAnswer_IsCancelled_InsteadOfBeingSynthesizedToTheEnd()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        foreach (var phrase in Sobria.Actions["headlights"])
        {
            h.Tts.Hold(phrase);
        }

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        var slow = h.Copilot.Pending;
        await PressAsync(h, "Star Map");
        await slow;

        Assert.All(Sobria.Actions["headlights"], phrase => Assert.Null(h.PhraseCache.TryGet(SpeechFixture.Voice, phrase)));
        Assert.Single(h.Player.Played);
    }

    [Fact]
    public async Task AnswerThatArrivesTooLate_IsKeptForNextTime_ButNotPlayed()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();
        h.Tts.Hold("Mapa estelar.");

        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Star Map"));
        h.Now += TimeSpan.FromSeconds(5);
        h.Tts.Release("Mapa estelar.");
        await h.Copilot.Pending;

        Assert.Empty(h.Player.Played);
        Assert.NotNull(h.PhraseCache.TryGet(SpeechFixture.Voice, "Mapa estelar."));
    }

    [Fact]
    public async Task WarmUp_ReleasesTheModel_WhenItFinishes()
    {
        await using var h = await Harness.CreateAsync();
        await h.EnableCopilotAsync();

        await h.Copilot.WarmUpAsync();

        Assert.True(h.Tts.Unloads >= 1);
    }

    // ---- Voices that share a download ----

    [Fact]
    public async Task InstallingAVoice_WhileItsSharedModelIsDownloading_IsRefused()
    {
        await using var h = await Harness.CreateAsync();
        h.VoiceInstaller.Hold = new TaskCompletionSource();
        var first = h.Shell.Copilot.Voices.Single(v => v.Voice.Id == "piper-sharvard-0");
        var second = h.Shell.Copilot.Voices.Single(v => v.Voice.Id == "piper-sharvard-1");

        var running = first.InstallCommand.ExecuteAsync(null);
        await second.InstallCommand.ExecuteAsync(null);
        Assert.False(second.IsBusy);

        h.VoiceInstaller.Hold.SetResult();
        await running;

        Assert.Equal(["piper-sharvard-0"], h.VoiceInstaller.Installed);
        Assert.True(second.IsInstalled);
    }

    [Fact]
    public async Task Shutdown_CancelsDownloadsInProgress()
    {
        await using var h = await Harness.CreateAsync(withVoice: false);
        h.VoiceInstaller.Hold = new TaskCompletionSource();
        var row = h.Shell.Copilot.Voices[0];
        var running = row.InstallCommand.ExecuteAsync(null);

        await h.Shell.ShutdownAsync();
        await running;

        Assert.False(row.IsBusy);
        Assert.False(row.IsInstalled);
    }

    // ---- Module response editor ----

    [Theory]
    [InlineData("|")]
    [InlineData(" | | ")]
    public async Task Editor_CustomResponseMadeOnlyOfSeparators_IsRejected(string text)
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        h.Shell.Editor.SelectedResponseMode = ModuleEditorViewModel.ResponseCustom;
        h.Shell.Editor.ResponseText = text;

        await h.Shell.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("", Button(h, "Lights").Response);
    }

    [Fact]
    public async Task Editor_CreateNew_KeepsTheChosenResponse()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Lights"));
        h.Shell.Editor.Name = "Minar";
        h.Shell.Editor.SelectedResponseMode = ModuleEditorViewModel.ResponseNone;

        await h.Shell.Editor.CreateCommand.ExecuteAsync(null);

        Assert.Equal("-", Button(h, "Minar").Response);
    }

    [Fact]
    public void CustomResponse_CanUseTheModuleName()
    {
        var selector = new ResponseSelector(Sobria, new Random(1));

        Assert.Equal("Minar, a la orden.", selector.ForModule(SpeechFixture.Button("Minar", response: "{nombre}, a la orden.")));
    }

    [Fact]
    public async Task MutedCategory_OfAnotherProfile_IsNotForgotten()
    {
        await using var h = await Harness.CreateAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { CopilotMutedCategories = "Mineria,Combat" });

        h.Shell.Copilot.Categories.Single(c => c.Name == "Scan").Speaks = false;
        await h.Shell.Copilot.Pending;

        var muted = CopilotService.MutedCategories(h.Session.Settings);
        Assert.Contains("Mineria", muted);
        Assert.Contains("Combat", muted);
        Assert.Contains("Scan", muted);
    }

    // ---- Player and installer details ----

    [Fact]
    public void Volume_ScalesTheSamples_AndLeavesTheHeaderAlone()
    {
        var root = SpeechFixture.TempFolder("scale");
        try
        {
            var path = new PhraseCache(root).Store(SpeechFixture.Voice, "x", new SpeechAudio([0.5f, -0.5f, 1f], 22050)).Path;
            var original = File.ReadAllBytes(path);

            var scaled = WavAudioPlayer.Scale(original, 0.5);

            Assert.Equal(original.Take(44), scaled.Take(44));
            Assert.Equal(BitConverter.ToInt16(original, 44) / 2, BitConverter.ToInt16(scaled, 44), 1.0);
            Assert.Equal(BitConverter.ToInt16(original, 46) / 2, BitConverter.ToInt16(scaled, 46), 1.0);
            Assert.Equal(short.MaxValue / 2, BitConverter.ToInt16(scaled, 48), 1.0);
            Assert.Equal(original, WavAudioPlayer.Scale(original, 1.0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_VoiceNeedsItsTokensFileToo()
    {
        var root = SpeechFixture.TempFolder("voices");
        try
        {
            var store = new VoiceStore(root);
            Directory.CreateDirectory(store.FolderOf(SpeechFixture.Voice));
            File.WriteAllText(Path.Combine(store.FolderOf(SpeechFixture.Voice), SpeechFixture.Voice.Model), "x");

            Assert.False(store.IsInstalled(SpeechFixture.Voice));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Installer_CleanLeftovers_RemovesAbandonedDownloads_ButNotVoices()
    {
        var root = SpeechFixture.TempFolder("leftovers");
        try
        {
            var store = new VoiceStore(root);
            FakeVoiceInstaller.Put(store, SpeechFixture.Voice);
            Directory.CreateDirectory(Path.Combine(root, ".descargas"));
            File.WriteAllText(Path.Combine(root, ".descargas", "half.tar.bz2"), "partial");
            Directory.CreateDirectory(Path.Combine(root, ".tmp-abc", "inner"));
            Directory.CreateDirectory(Path.Combine(root, ".old-abc"));

            new VoiceInstaller(new HttpClient(), store).CleanLeftovers();

            Assert.True(store.IsInstalled(SpeechFixture.Voice));
            Assert.False(Directory.Exists(Path.Combine(root, ".tmp-abc")));
            Assert.False(Directory.Exists(Path.Combine(root, ".old-abc")));
            Assert.False(File.Exists(Path.Combine(root, ".descargas", "half.tar.bz2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoDefaultPhrase_ClaimsAStateForAToggle()
    {
        string[] claims = ["desacoplado", "no nos ven", "desplegado", "encendid", "apagad", "abiert", "cerrad"];

        var offenders = ResponsePack.LoadAll()
            .SelectMany(p => p.Actions.Values.SelectMany(v => v))
            .Where(text => claims.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(offenders);
    }
}

public sealed class MobileBlockTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private readonly FakeInputSender _sender = new();
    private readonly HttpClient _http = new();
    private VerseDeck.MobileServer.MobilePanelServer _server = null!;
    private VerseDeck.Data.SqliteVerseDeckRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = await _db.CreateAsync();
        _server = new VerseDeck.MobileServer.MobilePanelServer(_repository, _sender);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await _server.StartAsync(port, "7391");
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        var pair = await _http.PostAsJsonAsync("/api/pair", new { pin = "7391" });
        var token = (await pair.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task PhonePress_OfABlockedModule_SendsNothing_AndSaysWhy()
    {
        var profile = (await _repository.GetProfilesAsync()).First(p => p.IsActive);
        var doors = (await _repository.GetButtonsAsync(profile.Id)).First(b => b.Name == "Doors");
        var lights = (await _repository.GetButtonsAsync(profile.Id)).First(b => b.Name == "Lights");
        _server.BlockReason = button => button.Name == "Doors" ? "sin tecla en el juego" : null;

        var blocked = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = doors.Id, confirmed = false });
        var allowed = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = lights.Id, confirmed = false });

        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("sin tecla", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("L", Assert.Single(_sender.Sent).Key);
    }
}
