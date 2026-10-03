using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Game;
using VerseDeck.Input;

namespace VerseDeck.App.ViewModels;

public sealed partial class ModuleEditorViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly IDialogService _dialogs;
    private readonly IStatusSink _status;
    private readonly ControlSync _controls;
    private readonly GameActionCatalog _catalog;
    private DeckButton? _button;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked))]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    private GameActionChoice? _selectedGameAction;

    [ObservableProperty]
    private string _title = "Selecciona un modulo";

    [ObservableProperty]
    private string _meta = "-";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _category = "Custom";

    [ObservableProperty]
    private string _icon = "power";

    [ObservableProperty]
    private string _frame = "Cyan";

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _modifiers = string.Empty;

    [ObservableProperty]
    private bool _requiresConfirmation;

    [ObservableProperty]
    private string _newPhrase = string.Empty;

    [ObservableProperty]
    private string _confidence = "0.40";

    [ObservableProperty]
    private string _selectedResponseMode = ResponseFromPack;

    [ObservableProperty]
    private string _responseText = string.Empty;

    [ObservableProperty]
    private string _phraseTestResult = string.Empty;

    /// <summary>Checks phrases offline with the copilot's voice; set by the shell.</summary>
    public VoiceDoctor? Doctor { get; set; }

    public ModuleEditorViewModel(DeckSession session, IDialogService dialogs, IStatusSink status, ControlSync controls, GameActionCatalog catalog)
    {
        _controls = controls;
        _catalog = catalog;
        _session = session;
        _dialogs = dialogs;
        _status = status;
    }

    public IReadOnlyList<string> Categories => ModuleStyle.Categories;
    public IReadOnlyList<string> Icons => ModuleStyle.Icons;
    public IReadOnlyList<string> Frames => ModuleStyle.Frames;
    public const string ResponseFromPack = "De la personalidad";
    public const string ResponseCustom = "Texto propio";
    public const string ResponseNone = "Ninguna";

    public IReadOnlyList<string> ResponseModes { get; } = [ResponseFromPack, ResponseCustom, ResponseNone];
    public ObservableCollection<string> Phrases { get; } = [];
    public ObservableCollection<GameActionChoice> GameActionChoices { get; } = [];

    /// <summary>Linked modules take their key from the game, so the key fields are read-only.</summary>
    public bool IsLinked => !string.IsNullOrEmpty(SelectedGameAction?.Id);
    public bool IsManual => !IsLinked;

    /// <summary>Asks the deck to select a module, used after creating one.</summary>
    public Action<long>? RequestSelect { get; set; }

    public void Load(DeckButton? button)
    {
        _button = button;
        Phrases.Clear();
        PhraseTestResult = string.Empty;
        LoadGameActionChoices(button?.GameAction ?? string.Empty);
        if (button is null)
        {
            Title = "Selecciona un modulo";
            Meta = "-";
            return;
        }

        Title = button.Name;
        Meta = $"{button.Category}  /  {ActionText.Display(button.Action)}";
        Name = button.Name;
        Category = button.Category;
        Icon = ModuleStyle.IconKeyFor(button);
        Frame = ModuleStyle.FrameFor(button);
        Key = button.Action.Key;
        Modifiers = string.Join(", ", button.Action.Modifiers);
        RequiresConfirmation = button.RequiresConfirmation;
        var response = (button.Response ?? string.Empty).Trim();
        SelectedResponseMode = response.Length == 0 ? ResponseFromPack : response == Speech.ResponseSelector.Silent ? ResponseNone : ResponseCustom;
        ResponseText = SelectedResponseMode == ResponseCustom ? response : string.Empty;
        NewPhrase = button.Name.ToLowerInvariant();
        Confidence = _session.Settings.VoiceMinimumConfidence.ToString("0.00", CultureInfo.CurrentCulture);
        foreach (var command in _session.VoiceCommands.Where(v => v.ButtonId == button.Id).OrderBy(v => v.Phrase))
        {
            Phrases.Add($"{command.Phrase}  [{command.MinimumConfidence.ToString("0.00", CultureInfo.CurrentCulture)}]");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_button is null)
        {
            _status.Error("Selecciona un modulo.");
            return;
        }

        // A linked module keeps its current key until the sync writes the game's key.
        var action = _button.Action;
        if (!TryReadName(out var name) || (IsManual && !TryBuildAction(Key, ManualPressMs, out action)))
        {
            return;
        }

        if (!TryReadResponse(out var response))
        {
            return;
        }

        // A long hold belongs to the game action that needs it, not to the module that was linked to it.
        var selectedId = SelectedGameAction?.Id ?? string.Empty;
        if (IsLinked && !selectedId.Equals(_button.GameAction, StringComparison.OrdinalIgnoreCase))
        {
            action = action with { PressDurationMs = ManualPressMs };
        }

        await RunAsync(async () =>
        {
            await _session.SaveButtonAsync(_button with
            {
                Name = name,
                Category = OrDefault(Category, _button.Category),
                Icon = OrDefault(Icon, _button.Icon),
                AccentColor = ModuleStyle.AccentForFrame(OrDefault(Frame, ModuleStyle.FrameFor(_button))),
                Action = action,
                RequiresConfirmation = RequiresConfirmation,
                GameAction = SelectedGameAction?.Id ?? string.Empty,
                Response = response
            });
            await _controls.Pending;
            _status.Info("Modulo guardado");
        });
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var profile = _session.ActiveProfile;
        if (profile is null)
        {
            _status.Error("No hay un perfil activo.");
            return;
        }

        if (!TryReadName(out var name) || !TryBuildAction(string.IsNullOrWhiteSpace(Key) || IsLinked ? "F13" : Key, ManualPressMs, out var action)
            || !TryReadResponse(out var response))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var created = await _session.SaveButtonAsync(new DeckButton(
                0,
                profile.Id,
                name,
                OrDefault(Icon, "power"),
                ModuleStyle.AccentForFrame(OrDefault(Frame, "Cyan")),
                OrDefault(Category, "Custom"),
                action,
                RequiresConfirmation,
                true,
                SelectedGameAction?.Id ?? string.Empty,
                response));
            await _controls.Pending;
            RequestSelect?.Invoke(created.Id);
            _status.Info($"Modulo creado: {created.Name}");
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_button is null)
        {
            _status.Error("Selecciona un modulo.");
            return;
        }

        var deletedName = _button.Name;
        if (!_dialogs.Confirm("Eliminar modulo", $"Eliminar el modulo '{deletedName}' y sus frases de voz?"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _session.DeleteButtonAsync(_button.Id);
            _status.Info($"Modulo eliminado: {deletedName}");
        });
    }

    /// <summary>Speaks the new phrase with the copilot's voice and runs it through the recogniser, without a microphone.</summary>
    [RelayCommand]
    private async Task TestPhraseAsync()
    {
        if (_button is null || Doctor is null)
        {
            _status.Error("Selecciona un modulo.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewPhrase))
        {
            _status.Error("Escribe la frase que quieres probar.");
            return;
        }

        var buttonId = _button.Id;
        PhraseTestResult = "Comprobando...";
        string result;
        try
        {
            result = (await Doctor.CheckPhraseAsync(buttonId, NewPhrase)).Text;
        }
        catch (OperationCanceledException)
        {
            result = "Comprobacion cancelada.";
        }

        // The player may have moved to another module while the check ran.
        if (_button?.Id == buttonId)
        {
            PhraseTestResult = result;
        }
    }

    [RelayCommand]
    private async Task AddPhraseAsync()
    {
        if (_button is null)
        {
            _status.Error("Selecciona un modulo.");
            return;
        }

        var confidence = Numbers.ParseConfidence(Confidence, _session.Settings.VoiceMinimumConfidence);
        await RunAsync(async () =>
        {
            await _session.SaveVoicePhraseAsync(_button.Id, NewPhrase, confidence);
            _status.Info("Frase de voz guardada");
        });
    }

    private const int ManualPressMs = 60;

    private void LoadGameActionChoices(string current)
    {
        GameActionChoices.Clear();
        GameActionChoices.Add(new GameActionChoice(string.Empty, "(ninguna: tecla manual)"));
        // Grouped as in the game's options, so a long list stays easy to scan.
        foreach (var action in _catalog.Actions.OrderBy(a => a.Group, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase))
        {
            GameActionChoices.Add(new GameActionChoice(action.Id, $"{action.Group} · {action.Label}"));
        }

        // Actions the player rebound that the catalog does not know can still be linked by their file name.
        var fileOnly = _controls.Rebinds
            .Where(r => r.Input.Device == ScDevice.Keyboard && _catalog.FindByActionName(r.Action) is null)
            .Select(r => $"{r.ActionMap}/{r.Action}")
            .Append(current)
            .Where(id => id.Contains('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in fileOnly)
        {
            GameActionChoices.Add(new GameActionChoice(id, id));
        }

        SelectedGameAction = GameActionChoices.FirstOrDefault(c => c.Id.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? GameActionChoices[0];
    }

    // What the copilot says for this module: empty for the personality's phrase, "-" for silence, or the player's own text.
    private bool TryReadResponse(out string response)
    {
        response = string.Empty;
        if (SelectedResponseMode == ResponseNone)
        {
            response = Speech.ResponseSelector.Silent;
        }
        else if (SelectedResponseMode == ResponseCustom)
        {
            response = ResponseText.Trim();
            var hasVariant = response.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0;
            if (!hasVariant || response == Speech.ResponseSelector.Silent)
            {
                _status.Error("Escribe lo que debe decir el copiloto, o elige otra opcion de respuesta.");
                return false;
            }
        }

        return true;
    }

    private bool TryReadName(out string name)
    {
        name = Name.Trim();
        if (name.Length == 0)
        {
            _status.Error("El nombre del modulo no puede estar vacio.");
            return false;
        }

        return true;
    }

    private bool TryBuildAction(string keyText, int pressDurationMs, out KeyPressAction action)
    {
        action = KeyPressAction.DefaultLandingGear;
        var key = keyText.Trim();
        if (key.Length == 0)
        {
            _status.Error("Escribe una tecla para el modulo.");
            return false;
        }

        if (!IsSupportedKey(key))
        {
            _status.Error($"Tecla no soportada: '{key}'. Usa letras, numeros, F1-F24, flechas o teclas comunes.");
            return false;
        }

        var modifiers = Modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = modifiers.FirstOrDefault(m => !KeyPressAction.AllowedModifiers.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            _status.Error($"Modificador no soportado: '{unknown}'. Usa Ctrl, Shift o Alt.");
            return false;
        }

        if (modifiers.Length > 3)
        {
            _status.Error("Como maximo tres modificadores.");
            return false;
        }

        action = new KeyPressAction(key, modifiers, pressDurationMs);
        return true;
    }

    private static bool IsSupportedKey(string key)
    {
        // Mouse buttons are valid names for push-to-talk but cannot be sent as a module key.
        return KeyMap.IsSupported(key) && !key.StartsWith("MOUSE_", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    private static string OrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

public static class Numbers
{
    /// <summary>Reads a 0.10-0.98 confidence typed with either a dot or a comma as decimal separator.</summary>
    public static double ParseConfidence(string? text, double fallback)
    {
        var parsed = double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
        return Math.Clamp(parsed, 0.1, 0.98);
    }
}
