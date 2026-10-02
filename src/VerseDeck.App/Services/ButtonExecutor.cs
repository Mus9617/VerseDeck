using VerseDeck.Core.Models;

namespace VerseDeck.App.Services;

public enum ExecuteResult { Sent, Cancelled, Failed }

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

    public event EventHandler<DeckButton>? Sent;
    public string? LastError { get; private set; }

    public async Task<ExecuteResult> ExecuteAsync(DeckButton button, string source)
    {
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
            return ExecuteResult.Failed;
        }

        LastError = null;
        if (_settings().CommandSoundEnabled)
        {
            _audio.PlayCommand();
        }

        Sent?.Invoke(this, button);
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
