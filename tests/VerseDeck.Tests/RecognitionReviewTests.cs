using VerseDeck.App.Services;
using VerseDeck.Voice;

namespace VerseDeck.Tests;

/// <summary>Cases found by the branch review of the recognition work.</summary>
public class RecognitionReviewTests
{
    private static async Task EditAsync(Harness h, string module)
    {
        h.Shell.Deck.IsEditMode = true;
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile(module));
    }

    [Theory]
    [InlineData("PushToTalk", false)]
    [InlineData("ManualToggle", true)]
    public async Task DiscardModel_IsLoadedOnlyForAlwaysListening(string mode, bool expected)
    {
        await using var h = await Harness.CreateAsync();
        h.Shell.Voice.Mode = mode;

        await h.Shell.Voice.StartCommand.ExecuteAsync(null);

        Assert.Equal(expected, h.Voice.UseDiscardModel);
    }

    [Fact]
    public async Task TestPhrase_CancelledByAnotherCheck_DoesNotThrow()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Checker.Hold = new TaskCompletionSource();
        h.Shell.Editor.NewPhrase = "dame luz";

        var test = h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);
        var all = h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);
        h.Checker.Hold.SetResult();
        await test;
        await all;

        Assert.Contains("cancelada", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task TestPhrase_ResultForAModuleNoLongerSelected_IsDropped()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Checker.Hold = new TaskCompletionSource();
        h.Shell.Editor.NewPhrase = "dame luz";

        var test = h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);
        await h.Shell.Deck.PressCommand.ExecuteAsync(h.Tile("Cargo"));
        h.Checker.Hold.SetResult();
        await test;

        Assert.Equal("", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task Doctor_HeardRightButBelowTheMinimum_IsLowConfidence()
    {
        await using var h = await Harness.CreateAsync();
        await EditAsync(h, "Lights");
        h.Checker.HeardAs["dame luz"] = new PhraseHit("dame luz", 0.30, false);
        h.Shell.Editor.NewPhrase = "dame luz";

        await h.Shell.Editor.TestPhraseCommand.ExecuteAsync(null);

        Assert.Contains("confianza baja", h.Shell.Editor.PhraseTestResult);
    }

    [Fact]
    public async Task Doctor_OnePhraseFailing_OnlyMarksThatPhrase_AndLogsIt()
    {
        await using var h = await Harness.CreateAsync();
        h.Tts.ThrowFor.Add("eyectar");

        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);

        var results = h.Shell.Voice.CheckResults;
        Assert.Equal(VerdictKind.Unchecked, results.Single(r => r.Phrase == "eyectar").Kind);
        Assert.Contains(results, r => r.Kind == VerdictKind.Ok);
        Assert.Contains(h.Log.Lines, line => line.Contains("eyectar"));
    }

    [Fact]
    public async Task Doctor_WithoutARecogniser_StopsBeforeGeneratingAnyAudio()
    {
        await using var h = await Harness.CreateAsync();
        h.Checker.Available = false;

        await h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);

        Assert.Empty(h.Tts.Synthesized);
        Assert.Contains("reconocedor", h.Shell.Voice.CheckStatus);
    }

    [Fact]
    public async Task Doctor_ProfileChangeDuringACheck_CancelsIt()
    {
        await using var h = await Harness.CreateAsync();
        h.Checker.Hold = new TaskCompletionSource();

        var all = h.Shell.Voice.CheckAllCommand.ExecuteAsync(null);
        await h.Session.SaveProfileAsync("Combate", "Aegis Gladius");
        h.Checker.Hold.SetResult();
        await all;

        Assert.Contains("cancelada", h.Shell.Voice.CheckStatus);
        Assert.Empty(h.Shell.Voice.CheckResults);
    }

    [Fact]
    public async Task StoppingThePhonePanel_ShowsItsLastPresses()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);
        await h.Repository.AddCommandLogAsync("Mobile", "Lights", "Sent L");

        await h.Shell.Mobile.StopCommand.ExecuteAsync(null);

        Assert.Contains("Mobile", h.Shell.Activity.Entries.First());
    }

    [Fact]
    public async Task PeriodicTick_WithThePhonePanelOff_LeavesTheActivityAlone()
    {
        await using var h = await Harness.CreateAsync();
        var before = h.Shell.Activity.Entries.ToList();
        await h.Repository.AddCommandLogAsync("Mobile", "Lights", "Sent L");

        h.Ui.Fire(TimeSpan.FromSeconds(2));
        await h.Shell.RefreshTick;

        Assert.Equal(before, h.Shell.Activity.Entries);
    }

    [Fact]
    public async Task PeriodicTick_WithThePhonePanelOn_ShowsPhonePresses()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);
        await h.Repository.AddCommandLogAsync("Mobile", "Lights", "Sent L");

        h.Ui.Fire(TimeSpan.FromSeconds(2));
        await h.Shell.RefreshTick;

        Assert.Contains("Mobile", h.Shell.Activity.Entries.First());
    }

    [Theory]
    [InlineData(10, 1, 22050, 16)]
    [InlineData(200, 2, 22050, 16)]
    [InlineData(200, 1, 22050, 8)]
    [InlineData(200, 1, 0, 16)]
    public void RecognizerFormat_RejectsWavFilesItCannotConvert(int length, short channels, int rate, short bits)
    {
        var source = Path.Combine(Path.GetTempPath(), $"versedeck-bad-{Guid.NewGuid():N}.wav");
        var target = source + ".out.wav";
        try
        {
            var bytes = new byte[length];
            if (length >= 44)
            {
                "RIFF"u8.CopyTo(bytes);
                "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
                BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
                BitConverter.GetBytes(channels).CopyTo(bytes, 22);
                BitConverter.GetBytes(rate).CopyTo(bytes, 24);
                BitConverter.GetBytes(bits).CopyTo(bytes, 34);
            }

            File.WriteAllBytes(source, bytes);

            Assert.Throws<InvalidDataException>(() => WindowsPhraseChecker.ToRecognizerFormat(source, target));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }
}
