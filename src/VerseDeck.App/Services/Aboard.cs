using System.Globalization;
using System.IO;
using System.Text;
using VerseDeck.Core.Models;
using VerseDeck.Voice;

namespace VerseDeck.App.Services;

/// <summary>
/// Runs a checklist one step at a time. "Done" on a step linked to a module sends that module's key through
/// the same executor as a click: one phrase, one press. Steps are never chained.
/// </summary>
public sealed class ChecklistRunner
{
    private readonly DeckSession _session;
    private readonly ButtonExecutor _executor;
    private readonly CopilotService _copilot;

    public ChecklistRunner(DeckSession session, ButtonExecutor executor, CopilotService copilot)
    {
        _session = session;
        _executor = executor;
        _copilot = copilot;
        _session.Changed += (_, _) => FollowEdits();
    }

    public Checklist? Active { get; private set; }
    public int Index { get; private set; }
    public ChecklistStep? Current => Active is null || Index >= Active.Steps.Count ? null : Active.Steps[Index];
    public string StatusText { get; private set; } = string.Empty;
    private bool _pressing;

    /// <summary>The last announcement started in the background, so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public event EventHandler? Changed;

    /// <summary>Everything the runner can say for the profile's checklists, so the warm-up can render it ahead.</summary>
    public IEnumerable<string> Phrases()
    {
        foreach (var checklist in _session.Checklists)
        {
            for (var i = 0; i < checklist.Steps.Count; i++)
            {
                yield return StepPhrase(checklist, i);
            }

            yield return Finished(checklist.Name);
            yield return Cancelled(checklist.Name);
        }
    }

    private static string StepPhrase(Checklist checklist, int index) => $"Paso {index + 1} de {checklist.Steps.Count}: {checklist.Steps[index].Text}.";
    private static string Finished(string name) => $"Checklist {name} completa.";
    private static string Cancelled(string name) => $"Checklist {name} cancelada.";

    public bool Start(string name)
    {
        var wanted = SpeechText.Normalize(name);
        var checklist = _session.Checklists.FirstOrDefault(c => SpeechText.Normalize(c.Name) == wanted);
        return checklist is not null && Start(checklist);
    }

    public bool Start(long checklistId)
    {
        var checklist = _session.Checklists.FirstOrDefault(c => c.Id == checklistId);
        return checklist is not null && Start(checklist);
    }

    /// <summary>
    /// Completes the current step, sending its module's key if it has one. A 'done' heard while that key is
    /// still being pressed is ignored, so a step can never be pressed twice or skipped.
    /// </summary>
    public async Task<RecognitionOutcome> DoneAsync(string source)
    {
        var step = Current;
        if (step is null)
        {
            return RecognitionOutcome.NothingToDo;
        }

        if (_pressing)
        {
            return RecognitionOutcome.Repeated;
        }

        var checklist = Active;
        var index = Index;
        var button = step.ButtonId is { } id ? _session.Buttons.FirstOrDefault(b => b.Id == id) : null;
        if (button is not null)
        {
            ExecuteResult result;
            _pressing = true;
            try
            {
                result = await _executor.ExecuteAsync(button, source);
            }
            finally
            {
                _pressing = false;
            }

            // Cancelled, edited or switched while the key was held: whatever is running now is not this step.
            if (!ReferenceEquals(Active, checklist) || Index != index)
            {
                return result == ExecuteResult.Sent ? RecognitionOutcome.Executed : RecognitionOutcome.Failed;
            }

            if (result != ExecuteResult.Sent)
            {
                // The step stays where it is: the player decides whether to retry or skip it.
                var why = result == ExecuteResult.Cancelled ? "cancelado" : _executor.LastError ?? "fallo al enviar";
                StatusText = $"Paso {Index + 1} sin completar: {why}";
                Changed?.Invoke(this, EventArgs.Empty);
                return RecognitionOutcome.Failed;
            }
        }

        Advance();
        return RecognitionOutcome.Executed;
    }

    public bool Skip()
    {
        if (Current is null)
        {
            return false;
        }

        Advance();
        return true;
    }

    public bool Repeat()
    {
        if (Current is null)
        {
            return false;
        }

        Announce();
        return true;
    }

    public bool Cancel()
    {
        if (Active is null)
        {
            return false;
        }

        var name = Active.Name;
        Active = null;
        Index = 0;
        StatusText = $"Checklist {name} cancelada";
        Say(Cancelled(name));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Start(Checklist checklist)
    {
        if (checklist.Steps.Count == 0)
        {
            return false;
        }

        Active = checklist;
        Index = 0;
        Announce();
        return true;
    }

    private void Advance()
    {
        Index++;
        if (Active is not null && Index >= Active.Steps.Count)
        {
            var name = Active.Name;
            Active = null;
            Index = 0;
            StatusText = $"Checklist {name} completa";
            Say(Finished(name));
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Announce();
    }

    private void Announce()
    {
        var step = Current!;
        StatusText = $"Paso {Index + 1} de {Active!.Steps.Count}: {step.Text}";
        Say(StepPhrase(Active, Index));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Say(string text)
    {
        if (_copilot.CanSpeak)
        {
            Pending = _copilot.SayAsync(text, whenReady: true);
        }
    }

    // A checklist edited while running continues with its new steps; one deleted, or of another profile, stops.
    private void FollowEdits()
    {
        if (Active is null)
        {
            return;
        }

        var updated = _session.Checklists.FirstOrDefault(c => c.Id == Active.Id);
        if (updated is null || updated.Steps.Count == 0)
        {
            Active = null;
            Index = 0;
            StatusText = string.Empty;
        }
        else
        {
            Active = updated;
            Index = Math.Min(Index, updated.Steps.Count - 1);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class TimerEntry
{
    public TimerEntry(long id, string? label, TimeSpan duration, DateTimeOffset due)
    {
        Id = id;
        Label = label;
        Duration = duration;
        Due = due;
    }

    public long Id { get; }
    public string? Label { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset Due { get; }
    public bool Fired { get; internal set; }
    public string Title => Label is null ? "Temporizador" : char.ToUpper(Label[0], CultureInfo.CurrentCulture) + Label[1..];
}

/// <summary>
/// Timers with a spoken alert. Only one system timer exists at a time, set for the next one due, so having
/// no timers, or many, costs nothing while they wait.
/// </summary>
public sealed class TimerService
{
    private readonly IUiScheduler _ui;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CopilotService _copilot;
    private readonly IAudioFeedback _audio;
    private readonly List<TimerEntry> _timers = [];
    private IDisposable? _next;
    private long _nextId = 1;

    public TimerService(IUiScheduler ui, Func<DateTimeOffset> clock, CopilotService copilot, IAudioFeedback audio)
    {
        _ui = ui;
        _clock = clock;
        _copilot = copilot;
        _audio = audio;
    }

    public const string AllCancelled = "Temporizadores cancelados.";

    public IReadOnlyList<TimerEntry> Timers => _timers;

    /// <summary>What unnamed voice timers say, so the warm-up can render it ahead. Named ones are rendered when used.</summary>
    public static IEnumerable<string> CommonPhrases()
    {
        yield return AllCancelled;
        foreach (var minutes in CompanionGrammar.TimerAmounts)
        {
            var entry = new TimerEntry(0, null, TimeSpan.FromMinutes(minutes), default);
            yield return Confirmation(entry);
            yield return Alert(entry);
        }
    }

    private static string Confirmation(TimerEntry entry) => $"{entry.Title}, {Describe(entry.Duration)}.";
    private static string Alert(TimerEntry entry) => $"{entry.Title}: han pasado {Describe(entry.Duration)}.";

    /// <summary>True when a system timer is waiting; false means the service is doing nothing at all.</summary>
    public bool IsScheduled => _next is not null;

    public event EventHandler? Changed;

    public TimerEntry Add(string? label, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var entry = new TimerEntry(_nextId++, label, duration, _clock() + duration);
        _timers.Add(entry);
        Say(Confirmation(entry));
        Schedule();
        return entry;
    }

    public void Cancel(long id)
    {
        _timers.RemoveAll(t => t.Id == id);
        Schedule();
    }

    public void CancelAll()
    {
        if (_timers.Count == 0)
        {
            return;
        }

        _timers.Clear();
        Say(AllCancelled);
        Schedule();
    }

    public void Dismiss(long id) => Cancel(id);

    public static string Describe(TimeSpan duration)
    {
        var (amount, unit) = duration.TotalSeconds < 60 || duration.TotalSeconds % 60 != 0
            ? ((int)duration.TotalSeconds, "segundo")
            : ((int)duration.TotalMinutes, "minuto");
        var words = amount is >= 1 and <= SpanishNumbers.Max ? SpanishNumbers.ToWords(amount) : amount.ToString(CultureInfo.InvariantCulture);
        if (amount == 1)
        {
            return $"un {unit}";
        }

        return $"{words} {unit}s";
    }

    private void Schedule()
    {
        _next?.Dispose();
        _next = null;
        var due = _timers.Where(t => !t.Fired).OrderBy(t => t.Due).FirstOrDefault();
        if (due is not null)
        {
            var wait = due.Due - _clock();
            _next = _ui.After(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Fire);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Fire()
    {
        _next = null;
        var now = _clock();
        var due = _timers.Where(t => !t.Fired && t.Due <= now).ToList();
        foreach (var timer in due)
        {
            timer.Fired = true;
        }

        // Timers due together are said as one phrase: a second phrase would cut the first one off.
        if (due.Count > 0 && !Say(string.Join(' ', due.Select(Alert))))
        {
            _audio.PlayCommand();
        }

        Schedule();
    }

    private bool Say(string text)
    {
        if (!_copilot.CanSpeak)
        {
            return false;
        }

        _ = _copilot.SayAsync(text, whenReady: true);
        return true;
    }
}

/// <summary>Notes the player writes or dictates, tagged with the profile and ship in use.</summary>
public sealed class LogbookService
{
    public const int Shown = 200;

    private readonly IVerseDeckRepository _repository;
    private readonly DeckSession _session;
    private readonly Func<DateTimeOffset> _clock;

    public LogbookService(IVerseDeckRepository repository, DeckSession session, Func<DateTimeOffset> clock)
    {
        _repository = repository;
        _session = session;
        _clock = clock;
    }

    public event EventHandler? Changed;

    public async Task<LogbookNote?> AddAsync(string text, string source)
    {
        var clean = text.Trim();
        if (clean.Length == 0)
        {
            return null;
        }

        var profile = _session.ActiveProfile;
        var note = await _repository.AddNoteAsync(new LogbookNote(0, _clock(), profile?.Name ?? string.Empty, profile?.ShipName ?? string.Empty, clean, source));
        Changed?.Invoke(this, EventArgs.Empty);
        return note;
    }

    public Task<IReadOnlyList<LogbookNote>> RecentAsync() => _repository.GetNotesAsync(Shown);

    public async Task DeleteAsync(long id)
    {
        await _repository.DeleteNoteAsync(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the notes, oldest first, to a text file in the folder and returns its path.</summary>
    public async Task<string> ExportAsync(string folder)
    {
        var notes = await _repository.GetNotesAsync(int.MaxValue);
        var builder = new StringBuilder();
        foreach (var note in notes.Reverse())
        {
            builder.AppendLine($"{note.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  [{note.ProfileName} / {note.ShipName}]  {note.Text}");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"VerseDeck-bitacora-{_clock().ToLocalTime():yyyyMMdd-HHmm}.txt");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
        return path;
    }
}
