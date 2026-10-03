using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;
using VerseDeck.Input;

namespace VerseDeck.App.ViewModels;

public sealed partial class VoiceViewModel : ObservableObject
{
    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan DetectInterval = TimeSpan.FromMilliseconds(35);

    // Several installed recognizers hear the same utterance; only the first report may send a press.
    public static readonly TimeSpan RecognitionDebounce = TimeSpan.FromMilliseconds(750);

    private const string PushToTalk = "PushToTalk";

    private readonly DeckSession _session;
    private readonly IVoiceCommandService _voice;
    private readonly IPttMonitor _ptt;
    private readonly ButtonExecutor _executor;
    private readonly IStatusSink _status;
    private readonly IUiScheduler _ui;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SpeechGuard _guard;
    private string _engineSignature = string.Empty;
    private DateTimeOffset _lastRecognition = DateTimeOffset.MinValue;
    private IDisposable? _graceTimer;
    private bool _busy;
    private bool _waitingForRelease;

    [ObservableProperty]
    private LinkState _state;

    [ObservableProperty]
    private string _statusText = "Voz detenida";

    [ObservableProperty]
    private string _mode = PushToTalk;

    [ObservableProperty]
    private string _pttDevice = "Keyboard";

    [ObservableProperty]
    private string _pttBinding = "F13";

    [ObservableProperty]
    private string _confidence = "0.40";

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _checkStatus = string.Empty;

    private CancellationTokenSource? _check;

    public VoiceViewModel(DeckSession session, IVoiceCommandService voice, IPttMonitor ptt, ButtonExecutor executor, IStatusSink status, IUiScheduler ui, IDebugLog log, Func<DateTimeOffset> clock, SpeechGuard guard)
    {
        _clock = clock;
        _guard = guard;
        _session = session;
        _voice = voice;
        _ptt = ptt;
        _executor = executor;
        _status = status;
        _ui = ui;
        _session.Changed += (_, _) => OnSessionChanged();
        _voice.Diagnostic += (_, message) => log.Write($"Voice: {message}");
        _voice.CommandRecognized += (_, e) => _ui.Post(() => Pending = HandleRecognizedAsync(e));
        _voice.Heard += (_, heard) => _ui.Post(() => Record(heard.Text, heard.Confidence, heard.Outcome));
        _ptt.PressedChanged += (_, pressed) => _ui.Post(() => OnPttChanged(pressed));
    }

    public IReadOnlyList<string> Modes { get; } = [PushToTalk, "ManualToggle"];
    public IReadOnlyList<string> Devices { get; } = ["Keyboard", "Mouse", "Gamepad", "Joystick"];

    /// <summary>The last background reaction (engine restart or recognised command), so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public Action<long>? RequestSelect { get; set; }

    /// <summary>Checks phrases offline with the copilot's voice; set by the shell.</summary>
    public VoiceDoctor? Doctor { get; set; }

    public const int HistorySize = 20;

    /// <summary>The last things the microphone heard and what happened to each, newest first.</summary>
    public ObservableCollection<HistoryRow> History { get; } = [];

    public ObservableCollection<PhraseVerdict> CheckResults { get; } = [];

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        if (Doctor is null)
        {
            return;
        }

        _check?.Cancel();
        var cancellation = _check = new CancellationTokenSource();
        IsChecking = true;
        CheckResults.Clear();
        CheckStatus = "Comprobando frases...";
        try
        {
            var results = await Doctor.CheckAllAsync(new Progress<string>(text => CheckStatus = text), cancellation.Token);
            foreach (var verdict in results)
            {
                CheckResults.Add(verdict);
            }

            var problems = results.Count(r => r.Kind != VerdictKind.Ok);
            CheckStatus = problems == 0
                ? $"Las {results.Count} frases se entienden bien."
                : $"{problems} de {results.Count} frases necesitan atencion. Arriba las que se confunden con otro modulo.";
        }
        catch (OperationCanceledException)
        {
            CheckStatus = "Comprobacion cancelada.";
        }
        catch (Exception ex)
        {
            CheckStatus = ex.Message;
            _status.Error(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_check, cancellation))
            {
                IsChecking = false;
                _check = null;
            }
        }
    }

    [RelayCommand]
    private void CancelCheck() => _check?.Cancel();

    private void Record(string text, double confidence, RecognitionOutcome outcome)
    {
        History.Insert(0, new HistoryRow(_clock().ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), text, confidence, outcome));
        while (History.Count > HistorySize)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    public void SyncFromSettings()
    {
        var settings = _session.Settings;
        Mode = settings.VoiceActivationMode;
        PttDevice = settings.PushToTalkDevice;
        PttBinding = settings.PushToTalkBinding;
        Confidence = settings.VoiceMinimumConfidence.ToString("0.00", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _busy = true;
        try
        {
            if (await SaveSettingsCoreAsync())
            {
                await StartEngineAsync();
            }
        }
        catch (Exception ex)
        {
            await GoOfflineAsync();
            _status.Error($"No se pudo activar la voz: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await GoOfflineAsync();
        _status.Info("Voz detenida");
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            if (await SaveSettingsCoreAsync())
            {
                _status.Info("Voz / PTT guardado");
            }
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void DetectPtt()
    {
        _ptt.Stop();
        _waitingForRelease = true;
        IsDetecting = true;
        StatusText = "Deteccion PTT: suelta todo y pulsa la tecla o boton deseado";
        _ui.After(DetectInterval, DetectTick);
    }

    private void DetectTick()
    {
        if (!IsDetecting)
        {
            return;
        }

        // The click that started detection must be released before a new input counts.
        if (_waitingForRelease)
        {
            _waitingForRelease = _ptt.AnyInputPressed();
            _ui.After(DetectInterval, DetectTick);
            return;
        }

        if (!_ptt.TryDetectPressed(out var detected))
        {
            _ui.After(DetectInterval, DetectTick);
            return;
        }

        IsDetecting = false;
        PttDevice = detected.DeviceType;
        PttBinding = detected.Binding;
        StatusText = $"PTT detectado: {detected.DeviceType}:{detected.Binding}. Guarda para aplicarlo.";
    }

    private async Task<bool> SaveSettingsCoreAsync()
    {
        var binding = PttBinding.Trim();
        if (binding.Length == 0 || binding == "-")
        {
            _status.Error("Pulsa 'Detectar boton PTT' y asigna una tecla o boton valido.");
            return false;
        }

        if (PttDevice is "Keyboard" or "Mouse" && !IsSupportedKey(binding))
        {
            _status.Error($"Tecla PTT no soportada: '{binding}'.");
            return false;
        }

        var confidence = Numbers.ParseConfidence(Confidence, _session.Settings.VoiceMinimumConfidence);
        await _session.SaveSettingsAsync(_session.Settings with
        {
            VoiceActivationMode = Mode,
            PushToTalkDevice = PttDevice,
            PushToTalkBinding = binding,
            VoiceMinimumConfidence = confidence
        });
        Confidence = confidence.ToString("0.00", CultureInfo.CurrentCulture);
        return true;
    }

    private async Task StartEngineAsync()
    {
        // A pause scheduled by the previous engine must not land on the new one.
        _graceTimer?.Dispose();
        _engineSignature = EngineSignature();
        var settings = _session.Settings;
        var commands = _session.VoiceCommands
            .Select(c => c with { MinimumConfidence = Math.Min(c.MinimumConfidence, settings.VoiceMinimumConfidence) })
            .ToList();
        _voice.UseDiscardModel = settings.VoiceActivationMode != PushToTalk;
        await _voice.StartAsync(commands, _session.Buttons);
        if (!_voice.IsRunning)
        {
            await GoOfflineAsync();
            _status.Error("No hay frases de voz o Windows no tiene un reconocedor de voz instalado.");
            return;
        }

        if (settings.VoiceActivationMode == PushToTalk)
        {
            _voice.SetInputGate(false);
            _voice.PauseRecognition();
            _ptt.Start(settings.PushToTalkDevice, settings.PushToTalkBinding);
            State = LinkState.Armed;
            StatusText = ArmedText();
        }
        else
        {
            _ptt.Stop();
            _voice.SetInputGate(true);
            State = LinkState.Online;
            StatusText = "Escuchando comandos locales";
        }
    }

    private async Task GoOfflineAsync()
    {
        _ptt.Stop();
        _graceTimer?.Dispose();
        try
        {
            await _voice.StopAsync();
        }
        catch
        {
            // Stopping is best effort: the engine may already be gone.
        }

        State = LinkState.Offline;
        StatusText = "Voz detenida";
    }

    private void OnSessionChanged()
    {
        // The engine holds a snapshot of phrases and buttons, so any deck change needs a reload.
        // Unrelated changes (a volume slider, a theme) must not reload the recogniser.
        if (_busy || State == LinkState.Offline || EngineSignature() == _engineSignature)
        {
            return;
        }

        Pending = RestartAsync();
    }

    private async Task RestartAsync()
    {
        _busy = true;
        try
        {
            await StartEngineAsync();
        }
        catch (Exception ex)
        {
            await GoOfflineAsync();
            _status.Error($"No se pudo recargar la voz: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnPttChanged(bool pressed)
    {
        if (State == LinkState.Offline || _session.Settings.VoiceActivationMode != PushToTalk)
        {
            return;
        }

        _graceTimer?.Dispose();
        if (pressed)
        {
            _voice.SetInputGate(true);
            _voice.ResumeRecognition();
            State = LinkState.Online;
            StatusText = "PTT pulsado: escuchando";
            return;
        }

        // Keep listening briefly so the end of the phrase is not cut off.
        _voice.SetInputGate(false, ReleaseGrace);
        _graceTimer = _ui.After(ReleaseGrace, () =>
        {
            _voice.PauseRecognition();
            _voice.SetInputGate(false);
        });
        State = LinkState.Armed;
        StatusText = ArmedText();
    }

    private async Task HandleRecognizedAsync(VoiceRecognizedEventArgs e)
    {
        // Never act on a recognition the user cannot see coming: voice off, module gone, or an echo of the last one.
        var now = _clock();
        var button = _session.Buttons.FirstOrDefault(b => b.Id == e.Button.Id);
        var ignored = State == LinkState.Offline ? RecognitionOutcome.Offline
            : button is null ? RecognitionOutcome.ModuleGone
            : now - _lastRecognition < RecognitionDebounce ? RecognitionOutcome.Repeated
            : (RecognitionOutcome?)null;
        if (ignored is { } reason || button is null)
        {
            Record(e.Command.Phrase, e.Confidence, ignored ?? RecognitionOutcome.ModuleGone);
            return;
        }

        // The microphone hears the copilot. Anything that began while it was talking is its own voice,
        // even if the recogniser only reports it afterwards, and must never send a press.
        if (_guard.Blocks(e.HeardAt ?? now) || _guard.Blocks(now))
        {
            StatusText = "Ignorado: el copiloto estaba hablando";
            Record(e.Command.Phrase, e.Confidence, RecognitionOutcome.CopilotSpeaking);
            return;
        }

        _lastRecognition = now;
        RequestSelect?.Invoke(button.Id);
        var result = await _executor.ExecuteAsync(button, "Voice");
        Record(e.Command.Phrase, e.Confidence, result == ExecuteResult.Sent ? RecognitionOutcome.Executed : RecognitionOutcome.Failed);
        StatusText = $"Reconocido: {e.Command.Phrase} ({e.Confidence.ToString("0.00", CultureInfo.CurrentCulture)})";
        if (result == ExecuteResult.Sent)
        {
            _status.Info($"{button.Name} enviado por voz");
        }
        else if (result == ExecuteResult.Failed)
        {
            _status.Error($"No se pudo enviar {button.Name}: {_executor.LastError}");
        }
    }

    // Everything the running engine depends on: what it listens for and how it is activated.
    private string EngineSignature()
    {
        var settings = _session.Settings;
        var phrases = string.Join("|", _session.VoiceCommands.Select(c => $"{c.ButtonId}:{c.Phrase}:{c.MinimumConfidence}:{c.Enabled}"));
        var buttons = string.Join("|", _session.Buttons.Select(b => $"{b.Id}:{b.Name}"));
        return $"{settings.VoiceActivationMode};{settings.PushToTalkDevice};{settings.PushToTalkBinding};{settings.VoiceMinimumConfidence};{phrases};{buttons}";
    }

    private string ArmedText() => $"PTT armado: manten {_session.Settings.PushToTalkDevice}:{_session.Settings.PushToTalkBinding}";

    private static bool IsSupportedKey(string key)
    {
        // Push-to-talk is polled by virtual key; keys that are only sent by scan code have none.
        return KeyMap.IsSupported(key) && KeyMap.ToVirtualKey(key) != 0;
    }
}
