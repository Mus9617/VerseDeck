using VerseDeck.Game;

namespace VerseDeck.Tests;

/// <summary>VerseDeck runs next to Star Citizen, so it must not do periodic work it does not need.</summary>
public class IdleCostTests
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WithThePhonePanelOff_ThePeriodicTickDoesNotQueryTheDatabase()
    {
        await using var h = await Harness.CreateAsync();
        var before = h.Shell.Activity.Entries.ToList();
        await h.Repository.AddCommandLogAsync("Mobile", "Lights", "Sent L");

        h.Ui.Fire(Refresh);
        await Task.Yield();

        Assert.Equal(before, h.Shell.Activity.Entries);
    }

    [Fact]
    public async Task WithThePhonePanelOn_PhonePressesShowUpOnTheNextTick()
    {
        await using var h = await Harness.CreateAsync();
        await h.Shell.Mobile.StartCommand.ExecuteAsync(null);
        await h.Repository.AddCommandLogAsync("Mobile", "Lights", "Sent L");

        h.Ui.Fire(Refresh);
        await Task.Delay(50);

        Assert.Contains("Mobile", h.Shell.Activity.Entries.First());
    }

    [Fact]
    public void LauncherLog_IsReadAgainOnlyWhenItChanges()
    {
        var root = SpeechFixture.TempFolder("launcher");
        try
        {
            var channel = Path.Combine(root, "LIVE");
            Directory.CreateDirectory(Path.Combine(channel, "user", "client", "0"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            var log = Path.Combine(root, "logs", "log.log");
            File.WriteAllText(log, $"[Launcher::launch] Launching Star Citizen LIVE from ({channel.Replace(@"\", @"\\")})");
            var locator = new GameInstallLocator(Path.Combine(root, "logs"), []);

            locator.Locate(null);
            locator.Locate(null);
            locator.Locate(null);
            Assert.Equal(1, locator.LauncherLogReads);

            File.AppendAllText(log, Environment.NewLine + "otra linea");
            Assert.Equal(channel, locator.Locate(null)!.ChannelFolder);
            Assert.Equal(2, locator.LauncherLogReads);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
