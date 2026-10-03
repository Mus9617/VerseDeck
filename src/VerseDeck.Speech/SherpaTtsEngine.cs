using SherpaOnnx;

namespace VerseDeck.Speech;

public interface ITtsEngine : IDisposable
{
    Task<SpeechAudio> SynthesizeAsync(VoiceInfo voice, string text, CancellationToken cancellationToken);

    /// <summary>Frees the loaded model. The next synthesis loads it again.</summary>
    void Unload();
}

/// <summary>Local neural text-to-speech through sherpa-onnx. Keeps one model loaded at a time.</summary>
public sealed class SherpaTtsEngine : ITtsEngine
{
    // A model whose files are damaged can answer with a few milliseconds of noise instead of failing.
    private const double ShortestPlausibleSeconds = 0.1;

    private readonly VoiceStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private string? _loadedFolder;

    public SherpaTtsEngine(VoiceStore store)
    {
        _store = store;
    }

    public async Task<SpeechAudio> SynthesizeAsync(VoiceInfo voice, string text, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Loading a model takes seconds and synthesis is CPU-bound, so neither runs on the caller's thread.
            return await Task.Run(() =>
            {
                // A request that was superseded while it waited for the engine is not worth synthesizing.
                cancellationToken.ThrowIfCancellationRequested();
                return Generate(voice, text);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Unload()
    {
        // Never waits: if a phrase is being synthesized right now the model is still needed.
        if (_gate.Wait(0))
        {
            Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        Release();
    }

    private void Release()
    {
        try
        {
            _tts?.Dispose();
            _tts = null;
            _loadedFolder = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SpeechAudio Generate(VoiceInfo voice, string text)
    {
        var loaded = _tts is not null && _loadedFolder == voice.Folder;
        var tts = loaded ? _tts! : Create(voice);
        try
        {
            var audio = tts.Generate(text, 1.0f, voice.Speaker);
            var result = new SpeechAudio(audio.Samples, audio.SampleRate);
            if (result.Duration.TotalSeconds < ShortestPlausibleSeconds)
            {
                throw new InvalidOperationException("El modelo de voz no ha generado audio. Vuelve a descargar la voz.");
            }

            if (!loaded)
            {
                // Kept only once it has proved it works, so a broken model is retried from scratch next time.
                _tts?.Dispose();
                _tts = tts;
                _loadedFolder = voice.Folder;
            }

            return result;
        }
        catch
        {
            if (!loaded)
            {
                tts.Dispose();
            }

            throw;
        }
    }

    private OfflineTts Create(VoiceInfo voice)
    {
        var folder = _store.FolderOf(voice);
        var config = new OfflineTtsConfig();
        if (voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            config.Model.Kokoro.Model = Path.Combine(folder, voice.Model);
            config.Model.Kokoro.Voices = Path.Combine(folder, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(folder, VoiceStore.TokensFile);
            config.Model.Kokoro.DataDir = Path.Combine(folder, "espeak-ng-data");
            config.Model.Kokoro.Lang = "es";
        }
        else
        {
            config.Model.Vits.Model = Path.Combine(folder, voice.Model);
            config.Model.Vits.Tokens = Path.Combine(folder, VoiceStore.TokensFile);
            config.Model.Vits.DataDir = Path.Combine(folder, "espeak-ng-data");
        }

        config.Model.NumThreads = 2;
        config.Model.Provider = "cpu";
        return new OfflineTts(config);
    }
}
