using System.Speech.Recognition;
using VerseDeck.Core.Models;

namespace VerseDeck.Voice;

public sealed class WindowsSpeechCommandService : IVoiceCommandService
{
    private readonly List<SpeechRecognitionEngine> _engines = [];
    private Dictionary<string, (VoiceCommand Command, DeckButton Button)> _commands = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _gateOpenUntil = DateTimeOffset.MaxValue;
    private bool _isListening;

    public event EventHandler<VoiceRecognizedEventArgs>? CommandRecognized;
    public event EventHandler<string>? Diagnostic;
    public event EventHandler<RecognitionHeard>? Heard;
    public bool UseDiscardModel { get; set; }
    public bool IsRunning => _engines.Count > 0;
    public bool IsListening => _isListening;
    public bool IsInputGateOpen => DateTimeOffset.Now <= _gateOpenUntil;

    public void SetInputGate(bool isOpen, TimeSpan? releaseGrace = null)
    {
        _gateOpenUntil = isOpen
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.Now.Add(releaseGrace ?? TimeSpan.Zero);
    }

    public Task StartAsync(IReadOnlyList<VoiceCommand> commands, IReadOnlyList<DeckButton> buttons, CancellationToken cancellationToken = default)
    {
        StopAsync(cancellationToken).GetAwaiter().GetResult();

        var enabled = commands.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return Task.CompletedTask;
        }

        var joinedCommands = enabled
            .Join(buttons, c => c.ButtonId, b => b.Id, (c, b) => (c, b))
            .Where(x => !string.IsNullOrWhiteSpace(x.c.Phrase))
            .ToList();

        _commands = joinedCommands
            .GroupBy(x => x.c.Phrase.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().c, g.First().b), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in joinedCommands.GroupBy(x => x.c.Phrase.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            Diagnostic?.Invoke(this, $"Voice duplicate phrase ignored after first match: '{duplicate.Key}'");
        }

        if (_commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        var recognizers = CommandGrammar.Recognizers().ToList();
        if (recognizers.Count == 0)
        {
            // A Windows without Spanish or English speech still gets whatever recogniser it has.
            recognizers = SpeechRecognitionEngine.InstalledRecognizers().Take(1).ToList();
        }
        Diagnostic?.Invoke(this, $"Installed recognizers: {string.Join(", ", SpeechRecognitionEngine.InstalledRecognizers().Select(r => $"{r.Culture.Name}/{r.Description}"))}");
        Diagnostic?.Invoke(this, $"Selected recognizers: {string.Join(", ", recognizers.Select(r => $"{r.Culture.Name}/{r.Description}"))}");

        foreach (var recognizer in recognizers)
        {
            try
            {
                var engine = new SpeechRecognitionEngine(recognizer);
                // Only the first engine gets the discard model: a second one would double its cost for little gain.
                CommandGrammar.Load(engine, _commands.Keys, UseDiscardModel && _engines.Count == 0);
                engine.SpeechDetected += (_, _) => Diagnostic?.Invoke(this, $"Voice speech detected by {engine.RecognizerInfo.Culture.Name}");
                engine.SpeechRecognized += OnSpeechRecognized;
                engine.SpeechRecognitionRejected += OnSpeechRejected;
                engine.SetInputToDefaultAudioDevice();
                engine.RecognizeAsync(RecognizeMode.Multiple);
                _engines.Add(engine);
                _isListening = true;
                Diagnostic?.Invoke(this, $"Voice engine started: {engine.RecognizerInfo.Culture.Name}");
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke(this, $"Voice engine failed for {recognizer.Culture.Name}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var engine in _engines.ToList())
        {
            // One engine failing to stop must not leave the others listening.
            try
            {
                engine.SpeechRecognized -= OnSpeechRecognized;
                engine.SpeechRecognitionRejected -= OnSpeechRejected;
                engine.RecognizeAsyncCancel();
                engine.RecognizeAsyncStop();
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke(this, $"Voice engine failed to stop cleanly: {ex.Message}");
            }
            finally
            {
                engine.Dispose();
            }
        }

        _engines.Clear();
        _isListening = false;
        return Task.CompletedTask;
    }

    public void PauseRecognition()
    {
        if (!_isListening)
        {
            return;
        }

        foreach (var engine in _engines)
        {
            engine.RecognizeAsyncCancel();
        }

        _isListening = false;
        Diagnostic?.Invoke(this, "Voice recognition paused");
    }

    public void ResumeRecognition()
    {
        if (_isListening || _engines.Count == 0)
        {
            return;
        }

        foreach (var engine in _engines)
        {
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }

        _isListening = true;
        Diagnostic?.Invoke(this, "Voice recognition resumed");
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text;
        var confidence = e.Result.Confidence;
        var known = _commands.TryGetValue(text, out var match);
        var rejected = RecognitionRules.Classify(e.Result.Grammar?.Name, confidence, known ? match.Command.MinimumConfidence : 1, known, IsInputGateOpen);
        if (rejected is { } outcome)
        {
            Diagnostic?.Invoke(this, $"Voice {outcome} '{text}' confidence={Invariant.Format(confidence)}");
            // Secondary engines hear the same audio; only the primary one explains itself in the history.
            if (ReferenceEquals(sender, _engines.FirstOrDefault()))
            {
                Heard?.Invoke(this, new RecognitionHeard(text, confidence, outcome, DateTimeOffset.Now));
            }
            return;
        }

        Diagnostic?.Invoke(this, $"Voice accepted '{text}' confidence={Invariant.Format(confidence)}");
        DateTimeOffset? heardAt = e.Result.Audio is { } audio ? new DateTimeOffset(audio.StartTime) : null;
        CommandRecognized?.Invoke(this, new VoiceRecognizedEventArgs(match.Command, match.Button, e.Result.Confidence, heardAt));
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var alternates = string.Join(", ", e.Result.Alternates.Take(3).Select(a => $"'{a.Text}' {a.Confidence:0.00}"));
        Diagnostic?.Invoke(this, $"Voice rejected. Alternates: {alternates}");
    }

}
