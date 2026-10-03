using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;

namespace VerseDeck.App.ViewModels;

public sealed record ModuleChoice(long? Id, string Name);

public sealed partial class StepRow : ObservableObject
{
    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private ModuleChoice? _module;

    public StepRow(string text, ModuleChoice? module)
    {
        _text = text;
        _module = module;
    }
}

public sealed partial class TimerRow : ObservableObject
{
    [ObservableProperty]
    private string _remainingText = string.Empty;

    public TimerRow(TimerEntry entry)
    {
        Entry = entry;
    }

    public TimerEntry Entry { get; }
    public long Id => Entry.Id;
    public string Title => Entry.Title;
    public bool Fired => Entry.Fired;
}

/// <summary>Checklists, timers and logbook: the copilot's helpers that do not need the game.</summary>
public sealed partial class AboardViewModel : ObservableObject
{
    public const string Noted = "Anotado.";

    private static readonly TimeSpan CountdownStep = TimeSpan.FromSeconds(1);

    private readonly DeckSession _session;
    private readonly ChecklistRunner _runner;
    private readonly TimerService _timers;
    private readonly LogbookService _logbook;
    private readonly CopilotService _copilot;
    private readonly IUiScheduler _ui;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IStatusSink _status;
    private readonly Func<string> _exportFolder;
    private IDisposable? _countdown;
    private bool _keepDraft;
    private bool _newDraft;

    [ObservableProperty]
    private Checklist? _selectedChecklist;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _runnerStatus = "Ninguna checklist en marcha";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _newTimerLabel = string.Empty;

    [ObservableProperty]
    private string _newTimerMinutes = "10";

    [ObservableProperty]
    private string _newNote = string.Empty;

    [ObservableProperty]
    private bool _isVisible;

    public AboardViewModel(
        DeckSession session,
        ChecklistRunner runner,
        TimerService timers,
        LogbookService logbook,
        CopilotService copilot,
        IUiScheduler ui,
        Func<DateTimeOffset> clock,
        IStatusSink status,
        Func<string> exportFolder)
    {
        _session = session;
        _runner = runner;
        _timers = timers;
        _logbook = logbook;
        _copilot = copilot;
        _ui = ui;
        _clock = clock;
        _status = status;
        _exportFolder = exportFolder;
        _session.Changed += (_, _) => SyncChecklists();
        _runner.Changed += (_, _) => SyncRunner();
        _timers.Changed += (_, _) => SyncTimers();
        _logbook.Changed += (_, _) => Pending = RefreshNotesAsync();
    }

    public ObservableCollection<Checklist> Checklists { get; } = [];
    public ObservableCollection<StepRow> EditSteps { get; } = [];
    public ObservableCollection<ModuleChoice> ModuleChoices { get; } = [];
    public ObservableCollection<TimerRow> Timers { get; } = [];
    public ObservableCollection<LogbookNote> Notes { get; } = [];
    public IReadOnlyList<string> TimerLabels { get; } = ["", .. Voice.CompanionGrammar.TimerLabels];

    /// <summary>The last background refresh, so tests can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public Task InitializeAsync() => RefreshNotesAsync();

    /// <summary>Carries out a checklist, timer or note request heard by voice, and says how it went.</summary>
    public async Task<RecognitionOutcome> HandleAsync(CompanionCommand command)
    {
        if (command.Kind == CompanionKind.Done)
        {
            return await _runner.DoneAsync("Voice");
        }

        return await HandleOtherAsync(command) ? RecognitionOutcome.Executed : RecognitionOutcome.NothingToDo;
    }

    private async Task<bool> HandleOtherAsync(CompanionCommand command)
    {
        switch (command.Kind)
        {
            case CompanionKind.StartChecklist:
                if (_runner.Start(command.Name ?? string.Empty))
                {
                    return true;
                }

                _status.Error($"No hay ninguna checklist llamada '{command.Name}'.");
                return false;
            case CompanionKind.Skip:
                return _runner.Skip();
            case CompanionKind.Repeat:
                return _runner.Repeat();
            case CompanionKind.CancelChecklist:
                return _runner.Cancel();
            case CompanionKind.Timer when command.Duration is { } duration:
                _timers.Add(command.Label, duration);
                return true;
            case CompanionKind.CancelTimers:
                var any = _timers.Timers.Count > 0;
                _timers.CancelAll();
                return any;
            case CompanionKind.Note when command.Text is { } text:
                var note = await _logbook.AddAsync(text, "Voz");
                if (note is not null && _copilot.CanSpeak)
                {
                    _ = _copilot.SayAsync(Noted, whenReady: true);
                }

                return note is not null;
            default:
                return false;
        }
    }

    // ---- Checklists ----

    [RelayCommand]
    private void Start(Checklist? checklist)
    {
        if (checklist is null || !_runner.Start(checklist.Id))
        {
            _status.Error("Esa checklist no tiene pasos.");
        }
    }

    [RelayCommand]
    private async Task DoneAsync()
    {
        await _runner.DoneAsync("Windows");
    }

    [RelayCommand]
    private void Skip() => _runner.Skip();

    [RelayCommand]
    private void Repeat() => _runner.Repeat();

    [RelayCommand]
    private void CancelRun() => _runner.Cancel();

    [RelayCommand]
    private void NewChecklist()
    {
        SelectedChecklist = null;
        _newDraft = true;
        EditName = string.Empty;
        EditSteps.Clear();
        EditSteps.Add(new StepRow(string.Empty, ModuleChoices.FirstOrDefault()));
    }

    [RelayCommand]
    private void AddStep() => EditSteps.Add(new StepRow(string.Empty, ModuleChoices.FirstOrDefault()));

    [RelayCommand]
    private void RemoveStep(StepRow? row)
    {
        if (row is not null)
        {
            EditSteps.Remove(row);
        }
    }

    [RelayCommand]
    private void MoveUp(StepRow? row)
    {
        var index = row is null ? -1 : EditSteps.IndexOf(row);
        if (index > 0)
        {
            EditSteps.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private async Task SaveChecklistAsync()
    {
        var steps = EditSteps
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select((s, i) => new ChecklistStep(0, i + 1, s.Text.Trim(), s.Module?.Id))
            .ToList();
        if (steps.Count == 0)
        {
            _status.Error("La checklist necesita al menos un paso.");
            return;
        }

        try
        {
            var saved = await _session.SaveChecklistAsync(new Checklist(SelectedChecklist?.Id ?? 0, _session.ActiveProfile?.Id ?? 0, EditName, steps));
            _newDraft = false;
            SelectedChecklist = Checklists.FirstOrDefault(c => c.Id == saved.Id);
            _status.Info($"Checklist guardada: {saved.Name}");

            // New steps are rendered now, while editing, so nothing is generated mid-flight.
            if (_copilot.CanSpeak)
            {
                Pending = _copilot.WarmUpAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            _status.Error(ex.Message);
        }
        catch (Exception ex)
        {
            _status.Error($"No se pudo guardar la checklist: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteChecklistAsync()
    {
        if (SelectedChecklist is null)
        {
            return;
        }

        var name = SelectedChecklist.Name;
        await _session.DeleteChecklistAsync(SelectedChecklist.Id);
        NewChecklist();
        _status.Info($"Checklist eliminada: {name}");
    }

    partial void OnSelectedChecklistChanged(Checklist? value)
    {
        if (value is null || _keepDraft)
        {
            return;
        }

        _newDraft = false;
        EditName = value.Name;
        EditSteps.Clear();
        foreach (var step in value.Steps)
        {
            EditSteps.Add(new StepRow(step.Text, ModuleChoices.FirstOrDefault(m => m.Id == step.ButtonId) ?? ModuleChoices.FirstOrDefault()));
        }
    }

    // Runs on every session change (a setting, a module edit): what the player is typing must survive it.
    private void SyncChecklists()
    {
        var selected = SelectedChecklist?.Id;
        var choices = new List<ModuleChoice> { new(null, "(sin tecla)") };
        choices.AddRange(_session.Buttons.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(b => new ModuleChoice(b.Id, b.Name)));
        if (!choices.SequenceEqual(ModuleChoices))
        {
            var picked = EditSteps.Select(s => s.Module?.Id).ToList();
            ModuleChoices.Clear();
            foreach (var choice in choices)
            {
                ModuleChoices.Add(choice);
            }

            for (var i = 0; i < EditSteps.Count; i++)
            {
                EditSteps[i].Module = choices.FirstOrDefault(c => c.Id == picked[i]) ?? choices[0];
            }
        }

        Checklists.Clear();
        foreach (var checklist in _session.Checklists)
        {
            Checklists.Add(checklist);
        }

        var same = Checklists.FirstOrDefault(c => c.Id == selected);
        _keepDraft = same is not null || (selected is null && _newDraft);
        try
        {
            SelectedChecklist = same ?? (_keepDraft ? null : Checklists.FirstOrDefault());
        }
        finally
        {
            _keepDraft = false;
        }
    }

    private void SyncRunner()
    {
        IsRunning = _runner.Active is not null;
        RunnerStatus = string.IsNullOrEmpty(_runner.StatusText) ? "Ninguna checklist en marcha" : _runner.StatusText;
    }

    // ---- Timers ----

    [RelayCommand]
    private void AddTimer()
    {
        if (!int.TryParse(NewTimerMinutes.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var minutes) || minutes is < 1 or > 600)
        {
            _status.Error("Escribe los minutos, de 1 a 600.");
            return;
        }

        _timers.Add(string.IsNullOrWhiteSpace(NewTimerLabel) ? null : NewTimerLabel.Trim(), TimeSpan.FromMinutes(minutes));
    }

    [RelayCommand]
    private void CancelTimer(TimerRow? row)
    {
        if (row is not null)
        {
            _timers.Cancel(row.Id);
        }
    }

    [RelayCommand]
    private void CancelAllTimers() => _timers.CancelAll();

    partial void OnIsVisibleChanged(bool value) => UpdateCountdown();

    private void SyncTimers()
    {
        Timers.Clear();
        foreach (var entry in _timers.Timers.OrderBy(t => t.Fired ? 0 : 1).ThenBy(t => t.Due))
        {
            Timers.Add(new TimerRow(entry));
        }

        UpdateCountdown();
    }

    // The countdown text is only refreshed while someone can see it, and only while there are timers.
    private void UpdateCountdown()
    {
        var now = _clock();
        foreach (var row in Timers)
        {
            var left = row.Entry.Due - now;
            row.RemainingText = row.Fired || left <= TimeSpan.Zero
                ? "Vencido"
                : left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : left.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }

        _countdown?.Dispose();
        _countdown = IsVisible && Timers.Any(t => !t.Fired)
            ? _ui.After(CountdownStep, UpdateCountdown)
            : null;
    }

    /// <summary>True while the visible countdown is ticking, for tests.</summary>
    public bool IsCountingDown => _countdown is not null;

    // ---- Logbook ----

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (await _logbook.AddAsync(NewNote, "Teclado") is null)
        {
            _status.Error("Escribe algo para anotar.");
            return;
        }

        NewNote = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(LogbookNote? note)
    {
        if (note is not null)
        {
            await _logbook.DeleteAsync(note.Id);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            var path = await _logbook.ExportAsync(_exportFolder());
            _status.Info($"Bitacora exportada a {path}");
        }
        catch (Exception ex)
        {
            _status.Error($"No se pudo exportar: {ex.Message}");
        }
    }

    private async Task RefreshNotesAsync()
    {
        var notes = await _logbook.RecentAsync();
        Notes.Clear();
        foreach (var note in notes)
        {
            Notes.Add(note);
        }
    }
}
