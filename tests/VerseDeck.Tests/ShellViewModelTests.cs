using VerseDeck.App;
using VerseDeck.App.Services;
using VerseDeck.App.ViewModels;

namespace VerseDeck.Tests;

public class VoiceViewModelTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2.5);

    private static async Task<Harness> WithModeAsync(string mode)
    {
        var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = mode;
        return h;
    }

    [Fact]
    public async Task Start_PushToTalk_StartsPaused_AndArmsMonitor()
    {
        await using var h = await WithModeAsync("PushToTalk");

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Armed, h.Shell.Voice.State);
        Assert.True(h.Voice.IsRunning);
        Assert.True(h.Voice.Paused);
        Assert.False(h.Voice.GateOpen);
        Assert.True(h.Ptt.Running);
        Assert.Equal("F13", h.Ptt.Binding);
    }

    [Fact]
    public async Task Start_ManualToggle_ListensImmediately()
    {
        await using var h = await WithModeAsync("ManualToggle");

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Online, h.Shell.Voice.State);
        Assert.True(h.Voice.GateOpen);
        Assert.False(h.Voice.Paused);
        Assert.False(h.Ptt.Running);
        Assert.Equal("ManualToggle", h.Session.Settings.VoiceActivationMode);
    }

    [Fact]
    public async Task Start_NoRecognizerAvailable_StaysOffline_WithMessage()
    {
        await using var h = await WithModeAsync("ManualToggle");
        h.Voice.CanStart = false;

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Offline, h.Shell.Voice.State);
        Assert.True(h.Shell.StatusIsError);
        Assert.False(h.Ptt.Running);
    }

    [Fact]
    public async Task Start_ServiceThrows_StaysOffline_WithMessage()
    {
        await using var h = await WithModeAsync("PushToTalk");
        h.Voice.ThrowOnStart = true;

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Offline, h.Shell.Voice.State);
        Assert.True(h.Shell.StatusIsError);
        Assert.Contains("no microphone", h.Shell.StatusText);
        Assert.False(h.Ptt.Running);
    }

    [Fact]
    public async Task Start_OnlySendsActiveProfilePhrases_CappedBySettingConfidence()
    {
        await using var h = await WithModeAsync("ManualToggle");
        h.Shell.Voice.Confidence = "0.30";

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(h.Session.VoiceCommands.Count, h.Voice.LastCommands.Count);
        Assert.All(h.Voice.LastCommands, c => Assert.True(c.MinimumConfidence <= 0.30 + 1e-9));
    }

    [Fact]
    public async Task SessionChanged_WhileRunning_RestartsEngineWithNewPhrases()
    {
        await using var h = await WithModeAsync("ManualToggle");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var starts = h.Voice.StartCount;
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");

        await h.Session.SaveVoicePhraseAsync(lights.Id, "dame luz", 0.4);
        await h.Shell.Voice.Pending;

        Assert.Equal(starts + 1, h.Voice.StartCount);
        Assert.Contains(h.Voice.LastCommands, c => c.Phrase == "dame luz");
        Assert.Equal(LinkState.Online, h.Shell.Voice.State);
        Assert.True(h.Voice.GateOpen);
    }

    [Fact]
    public async Task SessionChanged_WhileRunningInPtt_RestartsAndPausesAgain()
    {
        await using var h = await WithModeAsync("PushToTalk");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");

        await h.Session.SaveVoicePhraseAsync(lights.Id, "dame luz", 0.4);
        await h.Shell.Voice.Pending;

        Assert.Contains(h.Voice.LastCommands, c => c.Phrase == "dame luz");
        Assert.True(h.Voice.Paused);
        Assert.False(h.Voice.GateOpen);
        Assert.Equal(LinkState.Armed, h.Shell.Voice.State);
        Assert.True(h.Ptt.Running);
    }

    [Fact]
    public async Task SessionChanged_WhileStopped_DoesNotStartEngine()
    {
        await using var h = await Harness.CreateAsync();
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");

        await h.Session.SaveVoicePhraseAsync(lights.Id, "dame luz", 0.4);
        await h.Shell.Voice.Pending;

        Assert.Equal(0, h.Voice.StartCount);
    }

    [Fact]
    public async Task PttPress_OpensGate_Release_ClosesAfterGrace()
    {
        await using var h = await WithModeAsync("PushToTalk");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        h.Ptt.Raise(true);
        Assert.True(h.Voice.GateOpen);
        Assert.False(h.Voice.Paused);
        Assert.Equal(LinkState.Online, h.Shell.Voice.State);

        h.Ptt.Raise(false);
        Assert.Equal(Grace, h.Voice.LastGrace);
        Assert.False(h.Voice.Paused);
        Assert.Equal(LinkState.Armed, h.Shell.Voice.State);

        Assert.Equal(1, h.Ui.Fire(Grace));
        Assert.True(h.Voice.Paused);
    }

    [Fact]
    public async Task PttPressAgain_BeforeGraceEnds_CancelsThePause()
    {
        await using var h = await WithModeAsync("PushToTalk");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        h.Ptt.Raise(true);
        h.Ptt.Raise(false);
        h.Ptt.Raise(true);

        Assert.Equal(0, h.Ui.Fire(Grace));
        Assert.False(h.Voice.Paused);
    }

    [Fact]
    public async Task Recognized_ExecutesButtonWithSourceVoice()
    {
        await using var h = await WithModeAsync("ManualToggle");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        var lights = h.Session.Buttons.First(b => b.Name == "Lights");
        var command = h.Session.VoiceCommands.First(v => v.ButtonId == lights.Id);

        h.Voice.Raise(command, lights);
        await h.Shell.Voice.Pending;

        Assert.Equal("L", Assert.Single(h.Sender.Sent).Key);
        Assert.Equal("Voice", (await h.Repository.GetRecentCommandLogAsync(1)).Single().Source);
        Assert.True(h.Tile("Lights").IsSelected);
    }

    [Fact]
    public async Task Stop_GoesOffline_AndStopsMonitor()
    {
        await using var h = await WithModeAsync("PushToTalk");
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        await h.Shell.Voice.StopCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Offline, h.Shell.Voice.State);
        Assert.False(h.Voice.IsRunning);
        Assert.False(h.Ptt.Running);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("ñ")]
    public async Task SaveSettings_InvalidPttBinding_ReportsError(string binding)
    {
        await using var h = await WithModeAsync("PushToTalk");
        h.Shell.Voice.PttBinding = binding;

        await h.Shell.Voice.SaveSettingsCommand.ExecuteAsync(null);

        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("F13", h.Session.Settings.PushToTalkBinding);
    }

    [Fact]
    public async Task DetectPtt_WaitsForRelease_ThenTakesNextInput()
    {
        await using var h = await Harness.CreateAsync();
        var tick = TimeSpan.FromMilliseconds(35);
        h.Ptt.AnyPressed = true;

        h.Shell.Voice.DetectPttCommand.Execute(null);
        h.Ptt.Detected = new PttBinding("Joystick", "JOY0:BUTTON3");
        h.Ui.Fire(tick);
        Assert.True(h.Shell.Voice.IsDetecting);
        Assert.Equal("F13", h.Shell.Voice.PttBinding);

        h.Ptt.AnyPressed = false;
        h.Ui.Fire(tick);
        h.Ui.Fire(tick);

        Assert.False(h.Shell.Voice.IsDetecting);
        Assert.Equal("Joystick", h.Shell.Voice.PttDevice);
        Assert.Equal("JOY0:BUTTON3", h.Shell.Voice.PttBinding);
        Assert.Equal(0, h.Ui.Fire(tick));
    }
}

public class MobileLinkViewModelTests
{
    [Fact]
    public async Task Start_SetsOnline_UrlAndQr()
    {
        await using var h = await Harness.CreateAsync();

        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Online, h.Shell.Mobile.State);
        Assert.Equal("http://192.168.1.10:4785", h.Shell.Mobile.Url);
        Assert.NotNull(h.Shell.Mobile.QrPng);
        Assert.Equal(h.Session.Settings.PairingPin, h.Mobile.LastPin);
        Assert.Contains(h.Session.Settings.PairingPin, h.Shell.Mobile.PinText);
    }

    [Fact]
    public async Task Start_PortBusy_StaysOffline_ReportsError_AndCanRetry()
    {
        await using var h = await Harness.CreateAsync();
        h.Mobile.ThrowOnStart = true;

        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Offline, h.Shell.Mobile.State);
        Assert.True(h.Shell.StatusIsError);
        Assert.Null(h.Shell.Mobile.QrPng);

        h.Mobile.ThrowOnStart = false;
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Online, h.Shell.Mobile.State);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Stop_ClearsUrlAndQr()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);

        await h.Shell.Mobile.StopCommand.ExecuteAsync(null);

        Assert.Equal(LinkState.Offline, h.Shell.Mobile.State);
        Assert.Null(h.Shell.Mobile.QrPng);
        Assert.False(h.Mobile.IsRunning);
    }

    [Fact]
    public async Task Refresh_ShowsConnectedDeviceCount()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);
        h.Mobile.ConnectedCount = 2;

        h.Shell.Mobile.Refresh();

        Assert.Contains("2", h.Shell.Mobile.DevicesText);
    }
}

public class ShellViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsSession_AppliesTheme_AndPlaysWelcomeWhenEnabled()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal("GLOBAL", h.Shell.ProfileTitle);
        Assert.Equal("STARTER SHIP", h.Shell.ShipTitle);
        Assert.Equal([ThemeId.Neutral], h.AppliedThemes);
        Assert.Equal(1, h.Audio.WelcomePlays);
        Assert.Equal("Deck", h.Shell.Section);
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Initialize_WelcomeSoundDisabled_DoesNotPlay()
    {
        await using var h = await Harness.CreateAsync(initialize: false);
        await h.Session.LoadAsync();
        await h.Session.SaveSettingsAsync(h.Session.Settings with { WelcomeSoundEnabled = false });

        await h.Shell.InitializeAsync();

        Assert.Equal(0, h.Audio.WelcomePlays);
    }

    [Fact]
    public async Task ProfileChange_UpdatesTitles_AndTheme()
    {
        await using var h = await Harness.CreateAsync();

        await h.Session.SaveProfileAsync("Pirata", "Drake Cutlass Black");

        Assert.Equal("PIRATA", h.Shell.ProfileTitle);
        Assert.Equal("DRAKE CUTLASS BLACK", h.Shell.ShipTitle);
        Assert.Equal(ThemeId.Drake, h.AppliedThemes.Last());
        Assert.Equal(1, h.Audio.WelcomePlays);
    }

    [Fact]
    public async Task Error_SetsStatusIsError_Info_ClearsIt()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.Error("algo fallo");
        Assert.True(h.Shell.StatusIsError);
        Assert.Equal("algo fallo", h.Shell.StatusText);

        h.Shell.Info("todo bien");
        Assert.False(h.Shell.StatusIsError);
    }

    [Fact]
    public async Task Settings_FixedTheme_OverridesShip_AndPersists()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.Settings.Theme = "Origin";
        await h.Shell.Settings.Pending;

        Assert.Equal(ThemeId.Origin, h.AppliedThemes.Last());
        Assert.Equal("Origin", h.Session.Settings.Theme);
    }

    [Fact]
    public async Task Settings_SoundToggles_Persist()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.Settings.CommandSound = false;
        await h.Shell.Settings.Pending;

        Assert.False(h.Session.Settings.CommandSoundEnabled);
        Assert.True(h.Session.Settings.WelcomeSoundEnabled);
    }

    [Fact]
    public async Task Settings_LegacyThemeValue_ShowsAsAuto()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(ThemeService.Auto, h.Shell.Settings.Theme);
    }

    [Fact]
    public async Task Shutdown_StopsVoiceAndMobile()
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = "ManualToggle";
        await h.Shell.Voice.StartCommand.ExecuteAsync(null);
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);

        await h.Shell.ShutdownAsync();

        Assert.False(h.Voice.IsRunning);
        Assert.False(h.Mobile.IsRunning);
    }

    [Fact]
    public async Task Navigate_ChangesSection()
    {
        await using var h = await Harness.CreateAsync();

        h.Shell.NavigateCommand.Execute("Voz");

        Assert.Equal("Voz", h.Shell.Section);
    }
}
