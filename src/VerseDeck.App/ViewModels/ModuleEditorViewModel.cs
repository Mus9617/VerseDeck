using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Input;

namespace VerseDeck.App.ViewModels;

public sealed partial class ModuleEditorViewModel : ObservableObject
{
    private static readonly string[] AllowedModifiers = ["Ctrl", "Control", "Shift", "Alt"];

    private readonly DeckSession _session;
    private readonly IDialogService _dialogs;
    private readonly IStatusSink _status;
    private DeckButton? _button;

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

    public ModuleEditorViewModel(DeckSession session, IDialogService dialogs, IStatusSink status)
    {
        _session = session;
        _dialogs = dialogs;
        _status = status;
    }

    public IReadOnlyList<string> Categories => ModuleStyle.Categories;
    public IReadOnlyList<string> Icons => ModuleStyle.Icons;
    public IReadOnlyList<string> Frames => ModuleStyle.Frames;
    public ObservableCollection<string> Phrases { get; } = [];

    /// <summary>Asks the deck to select a module, used after creating one.</summary>
    public Action<long>? RequestSelect { get; set; }

    public void Load(DeckButton? button)
    {
        _button = button;
        Phrases.Clear();
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

        if (!TryReadName(out var name) || !TryBuildAction(Key, _button.Action.PressDurationMs, out var action))
        {
            return;
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
                RequiresConfirmation = RequiresConfirmation
            });
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

        if (!TryReadName(out var name) || !TryBuildAction(string.IsNullOrWhiteSpace(Key) ? "F13" : Key, 60, out var action))
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
                true));
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
        var unknown = modifiers.FirstOrDefault(m => !AllowedModifiers.Contains(m, StringComparer.OrdinalIgnoreCase));
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
        try
        {
            KeyMap.ToVirtualKey(key);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
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
