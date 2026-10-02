using VerseDeck.App.Services;
using VerseDeck.Core.Models;

namespace VerseDeck.Tests;

public sealed class ButtonExecutorTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private readonly FakeInputSender _sender = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeAudio _audio = new();
    private DeckSession _session = null!;
    private ButtonExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        var repository = await _db.CreateAsync();
        _session = new DeckSession(repository);
        await _session.LoadAsync();
        _executor = new ButtonExecutor(_sender, repository, _dialogs, _audio, new FakeLog(), () => _session.Settings);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private DeckButton Button(string name) => _session.Buttons.First(b => b.Name == name);

    [Fact]
    public async Task Execute_SendsOnePress_LogsAndPlaysSound()
    {
        DeckButton? raised = null;
        _executor.Sent += (_, button) => raised = button;

        var result = await _executor.ExecuteAsync(Button("Lights"), "Windows");

        Assert.Equal(ExecuteResult.Sent, result);
        Assert.Equal("L", Assert.Single(_sender.Sent).Key);
        Assert.Equal(1, _audio.CommandPlays);
        Assert.Equal("Lights", raised?.Name);
        var entry = (await _db.CreateAsync()).GetRecentCommandLogAsync(1).Result.Single();
        Assert.Equal("Windows", entry.Source);
        Assert.Equal("Lights", entry.Command);
    }

    [Fact]
    public async Task Execute_ConfirmationDeclined_SendsNothing()
    {
        _dialogs.Answer = false;

        var result = await _executor.ExecuteAsync(Button("Eject") with { RequiresConfirmation = true }, "Windows");

        Assert.Equal(ExecuteResult.Cancelled, result);
        Assert.Empty(_sender.Sent);
        Assert.Single(_dialogs.Questions);
    }

    [Fact]
    public async Task Execute_NoConfirmationRequired_DoesNotAsk()
    {
        await _executor.ExecuteAsync(Button("Lights"), "Windows");

        Assert.Empty(_dialogs.Questions);
    }

    [Fact]
    public async Task Execute_SenderThrows_ReturnsFailed_WithLastError()
    {
        _sender.ThrowOnSend = true;

        var result = await _executor.ExecuteAsync(Button("Lights"), "Windows");

        Assert.Equal(ExecuteResult.Failed, result);
        Assert.Equal("send failed", _executor.LastError);
        Assert.Equal(0, _audio.CommandPlays);
    }

    [Fact]
    public async Task Execute_CommandSoundDisabled_DoesNotPlay()
    {
        await _session.SaveSettingsAsync(_session.Settings with { CommandSoundEnabled = false });

        await _executor.ExecuteAsync(Button("Lights"), "Windows");

        Assert.Equal(0, _audio.CommandPlays);
    }
}
