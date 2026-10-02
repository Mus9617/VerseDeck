using VerseDeck.Core.Models;

namespace VerseDeck.App.Services;

public enum ExecuteResult { Sent, Cancelled, Failed }

public sealed record ExecutedEventArgs(DeckButton Button, string Source);

/// <summary>Turns one human trigger into at most one key press.</summary>
public sealed class ButtonExecutor
{
    private readonly IInputSender _sender;
    private readonly IVerseDeckRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly IAudioFeedback _audio;
    private readonly IDebugLog _log;
    private readonly Func<AppSettings> _settings;

    public ButtonExecutor(IInputSender sender, IVerseDeckRepository repository, IDialogService dialogs, IAudioFeedback audio, IDebugLog log, Func<AppSettings> settings)
    {
        _sender = sender;
        _repository = repository;
        _dialogs = dialogs;
        _audio = audio;
        _log = log;
        _settings = settings;
    }

    /// <summary>The press went out.</summary>
    public event EventHandler<ExecutedEventArgs>? Sent;

    /// <summary>The press was attempted and Windows or validation rejected it.</summary>
    public event EventHandler<ExecutedEventArgs>? Failed;

    /// <summary>Nothing was sent because <see cref="BlockReason"/> gave a reason.</summary>
    public event EventHandler<ExecutedEventArgs>? Blocked;

    public string? LastError { get; private set; }

    /// <summary>Returns why a module must not be sent right now, or null to let it through.</summary>
    public Func<DeckButton, string?>? BlockReason { get; set; }

    /// <summary>True when something else will acknowledge this press out loud, so the command beep stays quiet.</summary>
    public Func<DeckButton, string, bool>? WillBeAnswered { get; set; }

    public async Task<ExecuteResult> ExecuteAsync(DeckButton button, string source)
    {
        var args = new ExecutedEventArgs(button, source);
        if (BlockReason?.Invoke(button) is { } reason)
        {
            LastError = reason;
            _log.Write($"{source} blocked {button.Name}: {reason}");
            Blocked?.Invoke(this, args);
            return ExecuteResult.Failed;
        }

        if (button.RequiresConfirmation && !_dialogs.Confirm("Confirmar accion", $"Ejecutar {button.Name}?"))
        {
            return ExecuteResult.Cancelled;
        }

        try
        {
            button.Action.Validate();
            await _sender.SendAsync(button.Action);
            await _repository.AddCommandLogAsync(source, button.Name, $"Sent {ActionText.Display(button.Action)}");
            _log.Write($"{source} sent {button.Name} => {ActionText.Display(button.Action)}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Write($"{source} failed {button.Name}: {ex}");
            Failed?.Invoke(this, args);
            return ExecuteResult.Failed;
        }

        LastError = null;
        if (_settings().CommandSoundEnabled && WillBeAnswered?.Invoke(button, source) != true)
        {
            _audio.PlayCommand();
        }

        Sent?.Invoke(this, args);
        return ExecuteResult.Sent;
    }
}

public static class ActionText
{
    public static string Display(KeyPressAction action)
    {
        return action.Modifiers.Count == 0 ? action.Key : $"{string.Join("+", action.Modifiers)}+{action.Key}";
    }
}
