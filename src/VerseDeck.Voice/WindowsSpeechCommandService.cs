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

        var recognizers = SelectRecognizers().ToList();
        Diagnostic?.Invoke(this, $"Installed recognizers: {string.Join(", ", SpeechRecognitionEngine.InstalledRecognizers().Select(r => $"{r.Culture.Name}/{r.Description}"))}");
        Diagnostic?.Invoke(this, $"Selected recognizers: {string.Join(", ", recognizers.Select(r => $"{r.Culture.Name}/{r.Description}"))}");

        foreach (var recognizer in recognizers)
        {
            try
            {
                var engine = new SpeechRecognitionEngine(recognizer);
                var choices = new Choices(_commands.Keys.ToArray());
                var grammar = new Grammar(new GrammarBuilder(choices) { Culture = engine.RecognizerInfo.Culture })
                {
                    Name = $"VerseDeck {engine.RecognizerInfo.Culture.Name}"
                };
                engine.LoadGrammar(grammar);
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
            engine.SpeechRecognized -= OnSpeechRecognized;
            engine.SpeechRecognitionRejected -= OnSpeechRejected;
            engine.RecognizeAsyncCancel();
            engine.RecognizeAsyncStop();
            engine.Dispose();
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
        if (!IsInputGateOpen)
        {
            Diagnostic?.Invoke(this, $"Voice heard while PTT closed: '{e.Result.Text}' confidence={e.Result.Confidence:0.00}");
            return;
        }

        if (!_commands.TryGetValue(e.Result.Text, out var match))
        {
            Diagnostic?.Invoke(this, $"Voice recognized unknown '{e.Result.Text}' confidence={e.Result.Confidence:0.00}");
            return;
        }

        if (e.Result.Confidence < match.Command.MinimumConfidence)
        {
            Diagnostic?.Invoke(this, $"Voice ignored '{e.Result.Text}' confidence={e.Result.Confidence:0.00} minimum={match.Command.MinimumConfidence:0.00}");
            return;
        }

        Diagnostic?.Invoke(this, $"Voice accepted '{e.Result.Text}' confidence={e.Result.Confidence:0.00}");
        CommandRecognized?.Invoke(this, new VoiceRecognizedEventArgs(match.Command, match.Button, e.Result.Confidence));
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var alternates = string.Join(", ", e.Result.Alternates.Take(3).Select(a => $"'{a.Text}' {a.Confidence:0.00}"));
        Diagnostic?.Invoke(this, $"Voice rejected. Alternates: {alternates}");
    }

    private static IEnumerable<RecognizerInfo> SelectRecognizers()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var selected = recognizers
            .Where(r => r.Culture.Name.Equals("es-ES", StringComparison.OrdinalIgnoreCase))
            .Concat(recognizers.Where(r => r.Culture.TwoLetterISOLanguageName == "es" && !r.Culture.Name.Equals("es-ES", StringComparison.OrdinalIgnoreCase)))
            .Concat(recognizers.Where(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase) || r.Culture.Name.Equals("en-GB", StringComparison.OrdinalIgnoreCase)))
            .Concat(recognizers.Where(r => r.Culture.TwoLetterISOLanguageName == "en"))
            .DistinctBy(r => r.Id)
            .ToList();

        if (selected.Count > 0)
        {
            return selected;
        }

        return recognizers.Take(1);
    }
}
