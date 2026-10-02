using VerseDeck.App.Services;
using VerseDeck.Core.Models;

namespace VerseDeck.Tests;

public sealed class FakeDialogService : IDialogService
{
    public bool Answer { get; set; } = true;
    public List<string> Questions { get; } = [];

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return Answer;
    }
}

public sealed class FakeAudio : IAudioFeedback
{
    public int WelcomePlays { get; private set; }
    public int CommandPlays { get; private set; }

    public void PlayWelcome() => WelcomePlays++;
    public void PlayCommand() => CommandPlays++;
}

public sealed class FakeLog : IDebugLog
{
    public List<string> Lines { get; } = [];
    public string Path => "debug.log";

    public void Write(string message) => Lines.Add(message);
}

public sealed class FakeInputSender : IInputSender
{
    public List<KeyPressAction> Sent { get; } = [];
    public bool ThrowOnSend { get; set; }

    public Task SendAsync(KeyPressAction action, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("send failed");
        }

        Sent.Add(action);
        return Task.CompletedTask;
    }
}
