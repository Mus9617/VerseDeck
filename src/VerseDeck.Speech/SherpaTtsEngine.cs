using SherpaOnnx;

namespace VerseDeck.Speech;

public interface ITtsEngine : IDisposable
{
    Task<SpeechAudio> SynthesizeAsync(VoiceInfo voice, string text, CancellationToken cancellationToken);
}

/// <summary>Local neural text-to-speech through sherpa-onnx. Keeps one model loaded at a time.</summary>
public sealed class SherpaTtsEngine : ITtsEngine
{
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
                var tts = Load(voice);
                var audio = tts.Generate(text, 1.0f, voice.Speaker);
                return new SpeechAudio(audio.Samples, audio.SampleRate);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _tts = null;
        _loadedFolder = null;
    }

    private OfflineTts Load(VoiceInfo voice)
    {
        if (_tts is not null && _loadedFolder == voice.Folder)
        {
            return _tts;
        }

        _tts?.Dispose();
        _tts = null;

        var folder = _store.FolderOf(voice);
        var config = new OfflineTtsConfig();
        if (voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            config.Model.Kokoro.Model = Path.Combine(folder, voice.Model);
            config.Model.Kokoro.Voices = Path.Combine(folder, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(folder, "tokens.txt");
            config.Model.Kokoro.DataDir = Path.Combine(folder, "espeak-ng-data");
            config.Model.Kokoro.Lang = "es";
        }
        else
        {
            config.Model.Vits.Model = Path.Combine(folder, voice.Model);
            config.Model.Vits.Tokens = Path.Combine(folder, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(folder, "espeak-ng-data");
        }

        config.Model.NumThreads = 2;
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
        _loadedFolder = voice.Folder;
        return _tts;
    }
}
