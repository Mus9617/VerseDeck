using VerseDeck.Core.Models;
using VerseDeck.Input;

namespace VerseDeck.Tests;

public class InputSenderTests
{
    private static string Describe(IReadOnlyList<KeyStroke> strokes)
    {
        return string.Join(",", strokes.Select(s => $"{s.VirtualKey:X2}{(s.KeyUp ? "u" : "d")}"));
    }

    [Fact]
    public async Task ConcurrentPresses_DoNotInterleave()
    {
        var batches = new List<string>();
        var sender = new WindowsInputSender(strokes =>
        {
            lock (batches)
            {
                batches.Add(Describe(strokes));
            }
        }, virtualKey => virtualKey);

        await Task.WhenAll(
            sender.SendAsync(new KeyPressAction("A", [], 60)),
            sender.SendAsync(new KeyPressAction("B", [], 60)));

        Assert.Equal(["41d", "41u", "42d", "42u"], batches);
    }

    [Fact]
    public async Task DownFailure_StillReleasesEveryKey()
    {
        var batches = new List<string>();
        var calls = 0;
        var sender = new WindowsInputSender(strokes =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("partial send");
            }

            batches.Add(Describe(strokes));
        }, virtualKey => virtualKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new KeyPressAction("R", ["Alt"], 60)));

        Assert.Equal(["52u,12u"], batches);
    }

    [Fact]
    public async Task Cancellation_DuringHold_StillReleases()
    {
        var batches = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var sender = new WindowsInputSender(strokes =>
        {
            batches.Add(Describe(strokes));
            cancellation.Cancel();
        }, virtualKey => virtualKey);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(new KeyPressAction("N", [], 250), cancellation.Token));

        Assert.Equal(["4Ed", "4Eu"], batches);
    }
}
