using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;

namespace VerseDeck.App.ViewModels;

public interface IStatusSink
{
    void Info(string message);
    void Error(string message);
}

public enum LinkState { Offline, Armed, Online }

public sealed partial class ModuleTileViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _justSent;

    public ModuleTileViewModel(DeckButton button, string voiceSummary)
    {
        Id = button.Id;
        Name = button.Name;
        Category = button.Category;
        KeyText = ActionText.Display(button.Action);
        IconKey = ModuleStyle.IconKeyFor(button);
        AccentKey = ModuleStyle.AccentKeyFor(ModuleStyle.FrameFor(button));
        VoiceSummary = voiceSummary;
        RequiresConfirmation = button.RequiresConfirmation;
    }

    public long Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string KeyText { get; }
    public string IconKey { get; }
    public string AccentKey { get; }
    public string VoiceSummary { get; }
    public bool RequiresConfirmation { get; }
}

public sealed record ModuleGroup(string Category, IReadOnlyList<ModuleTileViewModel> Tiles);

public sealed partial class DeckViewModel : ObservableObject
{
    private static readonly TimeSpan SentFlash = TimeSpan.FromMilliseconds(400);

    private readonly DeckSession _session;
    private readonly ButtonExecutor _executor;
    private readonly IStatusSink _status;
    private readonly IUiScheduler _ui;
    private long? _selectedId;

    [ObservableProperty]
    private bool _isEditMode;

    public DeckViewModel(DeckSession session, ButtonExecutor executor, IStatusSink status, IUiScheduler ui)
    {
        _session = session;
        _executor = executor;
        _status = status;
        _ui = ui;
        _session.Changed += (_, _) => Rebuild();
        _executor.Sent += (_, button) => Flash(button.Id);
    }

    public ObservableCollection<ModuleGroup> Groups { get; } = [];

    public event EventHandler<DeckButton?>? SelectedButtonChanged;

    public void Select(long buttonId)
    {
        _selectedId = buttonId;
        ApplySelection();
    }

    [RelayCommand]
    private async Task PressAsync(ModuleTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        Select(tile.Id);
        var button = _session.Buttons.FirstOrDefault(b => b.Id == tile.Id);
        if (button is null)
        {
            return;
        }

        if (IsEditMode)
        {
            _status.Info($"Editando {button.Name}");
            return;
        }

        switch (await _executor.ExecuteAsync(button, "Windows"))
        {
            case ExecuteResult.Sent:
                _status.Info($"{button.Name} enviado");
                break;
            case ExecuteResult.Failed:
                _status.Error($"No se pudo enviar {button.Name}: {_executor.LastError}");
                break;
        }
    }

    private void Rebuild()
    {
        var buttons = _session.Buttons;
        Groups.Clear();
        var groups = buttons
            .GroupBy(b => b.Category)
            .OrderBy(g => ModuleStyle.CategoryOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var tiles = group
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .Select(b => new ModuleTileViewModel(b, VoiceSummaryFor(b.Id)))
                .ToList();
            Groups.Add(new ModuleGroup(group.Key, tiles));
        }

        if (_selectedId is null || buttons.All(b => b.Id != _selectedId))
        {
            _selectedId = (buttons.FirstOrDefault(b => b.Name.Equals("Landing Gear", StringComparison.OrdinalIgnoreCase)) ?? buttons.FirstOrDefault())?.Id;
        }

        ApplySelection();
    }

    private string VoiceSummaryFor(long buttonId)
    {
        return string.Join(" / ", _session.VoiceCommands.Where(v => v.ButtonId == buttonId && v.Enabled).Select(v => v.Phrase).Take(2));
    }

    private void ApplySelection()
    {
        foreach (var tile in Tiles())
        {
            tile.IsSelected = tile.Id == _selectedId;
        }

        SelectedButtonChanged?.Invoke(this, _session.Buttons.FirstOrDefault(b => b.Id == _selectedId));
    }

    private void Flash(long buttonId)
    {
        var tile = Tiles().FirstOrDefault(t => t.Id == buttonId);
        if (tile is null)
        {
            return;
        }

        tile.JustSent = true;
        _ui.After(SentFlash, () =>
        {
            foreach (var current in Tiles().Where(t => t.Id == buttonId))
            {
                current.JustSent = false;
            }
        });
    }

    private IEnumerable<ModuleTileViewModel> Tiles() => Groups.SelectMany(g => g.Tiles);
}
