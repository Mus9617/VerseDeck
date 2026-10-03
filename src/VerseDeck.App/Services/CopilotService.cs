using System.IO;
using System.Runtime.InteropServices;
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
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;
    private const uint Memory = 0x0004;

    private readonly object _gate = new();

    // Windows reads the sound from this memory until playback ends, so it must stay pinned and referenced.
    private byte[]? _playing;

    public void Play(string wavPath, double volume)
    {
        try
        {
            var scaled = Scale(File.ReadAllBytes(wavPath), volume);
            var pinned = GC.AllocateUninitializedArray<byte>(scaled.Length, pinned: true);
            scaled.CopyTo(pinned, 0);
            lock (_gate)
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                _playing = pinned;
                PlaySound(Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0), IntPtr.Zero, Async | Memory | NoDefault);
            }
        }
        catch
        {
            // Speech is optional and must never interrupt command execution.
        }
    }

    public void Stop()
    {
        try
        {
            lock (_gate)
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                _playing = null;
            }
        }
        catch
        {
            // Nothing useful can be done about a player that fails to stop.
        }
    }

    /// <summary>Returns a copy of a 16-bit PCM WAV with its samples multiplied by the volume (0 to 1).</summary>
    public static byte[] Scale(byte[] wav, double volume)
    {
        var result = (byte[])wav.Clone();
        var gain = Math.Clamp(volume, 0, 1);
        if (gain >= 1)
        {
            return result;
        }

        for (var i = HeaderBytes; i + 1 < result.Length; i += 2)
        {
            var sample = (short)Math.Round(BitConverter.ToInt16(result, i) * gain);
            result[i] = (byte)(sample & 0xFF);
            result[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return result;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}

/// <summary>
/// The stretch of time during which the copilot is talking. The microphone hears the speakers, so anything
/// the recogniser heard in that stretch is the copilot's own voice and must never trigger a press.
/// </summary>
public sealed class SpeechGuard
{
    private readonly object _gate = new();
    private DateTimeOffset _from = DateTimeOffset.MinValue;
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    public void Speaking(DateTimeOffset from, DateTimeOffset until)
    {
        lock (_gate)
        {
            // A phrase that interrupts another continues the same stretch rather than starting a new one.
            if (from >= _until)
            {
                _from = from;
            }

            _until = until;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _from = _until = DateTimeOffset.MinValue;
        }
    }

    public bool Blocks(DateTimeOffset at)
    {
        lock (_gate)
        {
            return at >= _from && at < _until;
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

    // The room keeps echoing, and the recogniser keeps listening, for a moment after playback ends.
    private static readonly TimeSpan EchoMargin = TimeSpan.FromMilliseconds(500);

    // An answer that arrives this long after the action is confusing; it is cached for next time instead.
    private static readonly TimeSpan TooLate = TimeSpan.FromSeconds(2);


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
    private CancellationTokenSource? _saying;
    private CancellationTokenSource? _warmUp;
    private CancellationTokenSource? _idle;

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
        executor.Sent += (_, e) => Answer(e, () => _selector.ForModule(e.Button));
        executor.Failed += (_, e) => Answer(e, () => _selector.Failed());
        executor.Blocked += (_, e) => Answer(e, () => _selector.NoKey());
        _session.Changed += (_, _) => OnSessionChanged();
    }

    public IReadOnlyList<ResponsePack> Packs => _packs;
    public VoiceCatalog Voices => _voices;
    public VoiceStore Store => _store;
    public int CachedPhrases => _cache.Count;
    public string WarmUpStatus { get; private set; } = string.Empty;

    /// <summary>Phrases other features will say (checklist steps, timer alerts), rendered by the warm-up too.</summary>
    public Func<IEnumerable<string>>? ExtraPhrases { get; set; }

    /// <summary>A model loaded for a phrase that was not cached is freed after this long without another one.</summary>
    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The pending release of a model loaded on the fly, for tests.</summary>
    public Task IdleRelease { get; private set; } = Task.CompletedTask;

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

    /// <summary>Whether the switches allow the copilot to say anything about this module triggered from this source.</summary>
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

    /// <param name="whenReady">Say it however long it takes: for announcements that are not an answer to a key press.</param>
    public async Task SayAsync(string text, bool force = false, bool whenReady = false)
    {
        var voice = ActiveVoice;
        if (voice is null || (Muted && !force))
        {
            return;
        }

        // Only the most recent phrase may sound. An older one still being synthesized is abandoned.
        var mine = Interlocked.Increment(ref _sequence);
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _saying, cancellation)?.Cancel();
        var asked = _clock();
        try
        {
            var phrase = _cache.TryGet(voice, text);
            var source = phrase is null ? "generated" : "cache";
            if (phrase is null)
            {
                var audio = await _engine.SynthesizeAsync(voice, text, cancellation.Token);
                phrase = _cache.Store(voice, text, audio);
                UnloadWhenIdle();
            }

            if (mine != Volatile.Read(ref _sequence) || (Muted && !force))
            {
                return;
            }

            var now = _clock();
            if (!whenReady && now - asked > TooLate)
            {
                _log.Write($"Copilot kept '{text}' for next time: it was ready {(now - asked).TotalSeconds:0.0} s after the action");
                return;
            }

            _player.Play(phrase.Path, _session.Settings.CopilotVolume);
            _guard.Speaking(now, now + phrase.Duration + EchoMargin);
            _log.Write($"Copilot said '{text}' ({source})");
        }
        catch (OperationCanceledException)
        {
            // A newer phrase or the mute switch took over.
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
        var wanted = _selector.AllFor(_session.Buttons).Concat(ExtraPhrases?.Invoke() ?? []).Distinct().ToList();
        var missing = wanted.Count(text => _cache.TryGet(voice, text) is null);
        var done = 0;
        foreach (var text in wanted)
        {
            if (cancellation.IsCancellationRequested)
            {
                break;
            }

            // Checked again here: a press may have cached the phrase while the warm-up was running.
            if (_cache.TryGet(voice, text) is not null)
            {
                continue;
            }

            WarmUpStatus = $"Generando frases: {++done} de {missing}";
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                var audio = await _engine.SynthesizeAsync(voice, text, cancellation.Token);
                _cache.Store(voice, text, audio);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Write($"Copilot warm-up stopped at '{text}': {ex.Message}");
                break;
            }
        }

        if (ReferenceEquals(_warmUp, cancellation))
        {
            // Everything the deck can say is on disk now; the model would only hold memory next to the game.
            _engine.Unload();
            WarmUpStatus = string.Empty;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Voices whose models are on disk, the chosen one first.</summary>
    public IReadOnlyList<VoiceInfo> InstalledVoices
    {
        get
        {
            var active = ActiveVoice;
            return _voices.Voices.Where(_store.IsInstalled).OrderBy(v => v.Id == active?.Id ? 0 : 1).ToList();
        }
    }

    /// <summary>The phrase as audio on disk, generated if needed. Never plays it.</summary>
    public async Task<CachedPhrase> RenderAsync(VoiceInfo voice, string text, CancellationToken cancellationToken)
    {
        return _cache.TryGet(voice, text)
            ?? _cache.Store(voice, text, await _engine.SynthesizeAsync(voice, text, cancellationToken));
    }

    /// <summary>Frees the voice model after on-demand work; it is reloaded when next needed.</summary>
    public void ReleaseEngine() => _engine.Unload();

    public void ClearCache()
    {
        _warmUp?.Cancel();
        Silence();
        _cache.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Answer(ExecutedEventArgs e, Func<string?> phrase)
    {
        if (!WillAnswer(e.Button, e.Source))
        {
            return;
        }

        UseCurrentPack();
        if (phrase() is { } text)
        {
            Pending = SayAsync(text);
        }
    }

    private void OnSessionChanged()
    {
        UseCurrentPack();
        var profile = _session.ActiveProfile;

        // Profiles are switched by clicking, so "voice commands only" keeps this quiet too.
        if (_lastProfileId is not null && profile is not null && profile.Id != _lastProfileId && CanSpeak && !_session.Settings.CopilotVoiceOnly)
        {
            Pending = SayAsync(_selector.Profile(profile.Name));
        }

        if (_lastProfileId is not null)
        {
            _lastProfileId = profile?.Id;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The game needs the memory more than an idle voice model does; the next phrase reloads it.
    private void UnloadWhenIdle()
    {
        var idle = new CancellationTokenSource();
        Interlocked.Exchange(ref _idle, idle)?.Cancel();
        IdleRelease = Task.Delay(IdleUnload, idle.Token).ContinueWith(
            _ =>
            {
                // A warm-up in progress unloads the model itself when it finishes.
                if (ReferenceEquals(Volatile.Read(ref _idle), idle) && WarmUpStatus.Length == 0)
                {
                    _engine.Unload();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
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
        Interlocked.Exchange(ref _saying, null)?.Cancel();
        _player.Stop();
        _guard.Clear();
    }
}
