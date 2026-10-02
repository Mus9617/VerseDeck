using System.IO;
using System.Media;
using VerseDeck.Core.Models;
using VerseDeck.Speech;

namespace VerseDeck.App.Services;

public interface IAudioPlayer
{
    /// <summary>Plays a WAV file, cutting off whatever was playing. Never throws.</summary>
    void Play(string wavPath, double volume);

    void Stop();
}

/// <summary>Plays 16-bit PCM WAV files through the default output, scaling the samples for volume.</summary>
public sealed class WavAudioPlayer : IAudioPlayer
{
    private const int HeaderBytes = 44;

    private readonly object _gate = new();
    private SoundPlayer? _player;
    private MemoryStream? _stream;

    public void Play(string wavPath, double volume)
    {
        try
        {
            var bytes = File.ReadAllBytes(wavPath);
            var gain = Math.Clamp(volume, 0, 1);
            for (var i = HeaderBytes; i + 1 < bytes.Length; i += 2)
            {
                var sample = (short)(BitConverter.ToInt16(bytes, i) * gain);
                bytes[i] = (byte)(sample & 0xFF);
                bytes[i + 1] = (byte)((sample >> 8) & 0xFF);
            }

            lock (_gate)
            {
                StopLocked();
                _stream = new MemoryStream(bytes);
                _player = new SoundPlayer(_stream);
                _player.Play();
            }
        }
        catch
        {
            // Speech is optional and must never interrupt command execution.
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _stream?.Dispose();
        }
        catch
        {
            // Nothing useful can be done about a player that fails to stop.
        }

        _player = null;
        _stream = null;
    }
}

/// <summary>
/// While the copilot is talking the microphone hears it, so recognised commands are ignored until it finishes.
/// </summary>
public sealed class SpeechGuard
{
    private readonly object _gate = new();
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    public void BlockUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            _until = until;
        }
    }

    public void Clear() => BlockUntil(DateTimeOffset.MinValue);

    public bool Blocks(DateTimeOffset now)
    {
        lock (_gate)
        {
            return now < _until;
        }
    }
}

/// <summary>
/// The copilot's voice. It reacts to what the deck did; it is never awaited by the code that sends a key,
/// so a slow or broken voice cannot delay or stop a press.
/// </summary>
public sealed class CopilotService
{
    public const string TestPhrase = "Hola, comandante. Esta es mi voz.";

    // The microphone keeps hearing the room for a moment after playback ends.
    private static readonly TimeSpan EchoMargin = TimeSpan.FromMilliseconds(300);

    private readonly DeckSession _session;
    private readonly ITtsEngine _engine;
    private readonly PhraseCache _cache;
    private readonly IAudioPlayer _player;
    private readonly VoiceCatalog _voices;
    private readonly VoiceStore _store;
    private readonly IReadOnlyList<ResponsePack> _packs;
    private readonly SpeechGuard _guard;
    private readonly IDebugLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private ResponseSelector _selector;
    private string _selectorPack;
    private long? _lastProfileId;
    private int _sequence;
    private bool _muted;
    private CancellationTokenSource? _warmUp;

    public CopilotService(
        DeckSession session,
        ButtonExecutor executor,
        ITtsEngine engine,
        PhraseCache cache,
        IAudioPlayer player,
        VoiceCatalog voices,
        VoiceStore store,
        IReadOnlyList<ResponsePack> packs,
        SpeechGuard guard,
        IDebugLog log,
        Func<DateTimeOffset> clock)
    {
        _session = session;
        _engine = engine;
        _cache = cache;
        _player = player;
        _voices = voices;
        _store = store;
        _packs = packs;
        _guard = guard;
        _log = log;
        _clock = clock;
        _selectorPack = packs[0].Id;
        _selector = new ResponseSelector(packs[0], new Random());

        executor.WillBeAnswered = WillAnswer;
        executor.Sent += (_, e) => OnSent(e);
        executor.Failed += (_, _) => SayIfSpeaking(() => _selector.Failed());
        executor.Blocked += (_, _) => SayIfSpeaking(() => _selector.NoKey());
        _session.Changed += (_, _) => OnSessionChanged();
    }

    public IReadOnlyList<ResponsePack> Packs => _packs;
    public VoiceCatalog Voices => _voices;
    public VoiceStore Store => _store;
    public int CachedPhrases => _cache.Count;
    public string WarmUpStatus { get; private set; } = string.Empty;

    /// <summary>The last speech or warm-up started in the background, so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public event EventHandler? Changed;

    /// <summary>Quick mute: silences everything at once without touching the saved switches.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
            {
                return;
            }

            _muted = value;
            if (value)
            {
                Silence();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The chosen voice, or null when none is chosen or its model is not on disk.</summary>
    public VoiceInfo? ActiveVoice
    {
        get
        {
            var voice = _voices.Find(_session.Settings.CopilotVoice);
            return voice is not null && _store.IsInstalled(voice) ? voice : null;
        }
    }

    public bool CanSpeak => _session.Settings.CopilotEnabled && !Muted && ActiveVoice is not null;

    public bool WillAnswer(DeckButton button, string source)
    {
        var settings = _session.Settings;
        return CanSpeak
            && (!settings.CopilotVoiceOnly || source == "Voice")
            && !MutedCategories(settings).Contains(button.Category, StringComparer.OrdinalIgnoreCase)
            && (button.Response ?? string.Empty).Trim() != ResponseSelector.Silent;
    }

    public static IReadOnlyList<string> MutedCategories(AppSettings settings)
    {
        return settings.CopilotMutedCategories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Greets if asked to and renders the deck's phrases ahead of time.</summary>
    public Task StartAsync()
    {
        _lastProfileId = _session.ActiveProfile?.Id;
        UseCurrentPack();
        if (CanSpeak && _session.Settings.CopilotGreeting)
        {
            Pending = SayAsync(_selector.Greeting());
        }

        return CanSpeak ? WarmUpAsync() : Task.CompletedTask;
    }

    /// <summary>Speaks a sample with the chosen voice even when the copilot is switched off or muted.</summary>
    public Task TestAsync() => SayAsync(TestPhrase, force: true);

    public async Task SayAsync(string text, bool force = false)
    {
        var voice = ActiveVoice;
        if (voice is null || (Muted && !force))
        {
            return;
        }

        // Only the most recent phrase may sound: a slow synthesis for an older command is dropped.
        var mine = Interlocked.Increment(ref _sequence);
        try
        {
            var phrase = _cache.TryGet(voice, text);
            if (phrase is null)
            {
                var audio = await _engine.SynthesizeAsync(voice, text, CancellationToken.None);
                phrase = _cache.Store(voice, text, audio);
            }

            if (mine != Volatile.Read(ref _sequence) || (Muted && !force))
            {
                return;
            }

            _player.Play(phrase.Path, _session.Settings.CopilotVolume);
            _guard.BlockUntil(_clock() + phrase.Duration + EchoMargin);
        }
        catch (Exception ex)
        {
            _log.Write($"Copilot could not say '{text}': {ex.Message}");
        }
    }

    /// <summary>Synthesizes whatever the active deck could say that is not cached yet.</summary>
    public async Task WarmUpAsync()
    {
        var voice = ActiveVoice;
        if (voice is null)
        {
            return;
        }

        _warmUp?.Cancel();
        var cancellation = _warmUp = new CancellationTokenSource();
        UseCurrentPack();
        var missing = _selector.AllFor(_session.Buttons).Where(text => _cache.TryGet(voice, text) is null).ToList();
        try
        {
            for (var i = 0; i < missing.Count && !cancellation.IsCancellationRequested; i++)
            {
                WarmUpStatus = $"Generando frases: {i + 1} de {missing.Count}";
                Changed?.Invoke(this, EventArgs.Empty);
                var audio = await _engine.SynthesizeAsync(voice, missing[i], cancellation.Token);
                _cache.Store(voice, missing[i], audio);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer warm-up after the voice or the personality changed.
        }
        catch (Exception ex)
        {
            _log.Write($"Copilot warm-up stopped: {ex.Message}");
        }

        if (ReferenceEquals(_warmUp, cancellation))
        {
            WarmUpStatus = string.Empty;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearCache()
    {
        _warmUp?.Cancel();
        Silence();
        _cache.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSent(ExecutedEventArgs e)
    {
        if (!WillAnswer(e.Button, e.Source))
        {
            return;
        }

        UseCurrentPack();
        if (_selector.ForModule(e.Button) is { } phrase)
        {
            Pending = SayAsync(phrase);
        }
    }

    private void SayIfSpeaking(Func<string> phrase)
    {
        if (CanSpeak)
        {
            UseCurrentPack();
            Pending = SayAsync(phrase());
        }
    }

    private void OnSessionChanged()
    {
        UseCurrentPack();
        var profile = _session.ActiveProfile;
        if (_lastProfileId is not null && profile is not null && profile.Id != _lastProfileId && CanSpeak)
        {
            Pending = SayAsync(_selector.Profile(profile.Name));
        }

        if (_lastProfileId is not null)
        {
            _lastProfileId = profile?.Id;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UseCurrentPack()
    {
        var pack = _packs.FirstOrDefault(p => p.Id.Equals(_session.Settings.CopilotPack, StringComparison.OrdinalIgnoreCase)) ?? _packs[0];
        if (pack.Id != _selectorPack)
        {
            _selectorPack = pack.Id;
            _selector = new ResponseSelector(pack, new Random());
        }
    }

    private void Silence()
    {
        Interlocked.Increment(ref _sequence);
        _player.Stop();
        _guard.Clear();
    }
}
