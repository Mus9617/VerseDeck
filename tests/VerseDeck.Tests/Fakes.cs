using VerseDeck.Core.Models;

namespace VerseDeck.Tests;

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
