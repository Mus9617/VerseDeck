using VerseDeck.App.Services;
using VerseDeck.Speech;

namespace VerseDeck.Tests;

public sealed class FakeTtsEngine : ITtsEngine
{
    private readonly Dictionary<string, TaskCompletionSource> _holds = [];

    public List<string> Synthesized { get; } = [];
    public bool Throw { get; set; }
    public HashSet<string> ThrowFor { get; } = [];
    public double Seconds { get; set; } = 1.0;

    /// <summary>Makes synthesis of this text wait until <see cref="Release"/> is called, like a slow model.</summary>
    public void Hold(string text) => _holds[text] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(string text) => _holds[text].TrySetResult();

    public async Task<SpeechAudio> SynthesizeAsync(VoiceInfo voice, string text, CancellationToken cancellationToken)
    {
        if (Throw || ThrowFor.Contains(text))
        {
            throw new InvalidOperationException("model failed to load");
        }

        lock (Synthesized)
        {
            Synthesized.Add(text);
        }

        if (_holds.TryGetValue(text, out var hold))
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return SpeechFixture.Tone(Seconds);
    }

    public int Unloads { get; private set; }

    public void Unload() => Unloads++;

    public void Dispose()
    {
    }
}

public sealed class FakeAudioPlayer : IAudioPlayer
{
    public List<(string Path, double Volume)> Played { get; } = [];
    public int Stops { get; private set; }

    public void Play(string wavPath, double volume)
    {
        lock (Played)
        {
            Played.Add((wavPath, volume));
        }
    }

    public void Stop() => Stops++;
}

public sealed class FakeVoiceInstaller : IVoiceInstaller
{
    private readonly VoiceStore _store;

    public FakeVoiceInstaller(VoiceStore store)
    {
        _store = store;
    }

    public Exception? Fail { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public List<string> Installed { get; } = [];

    public async Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.5);
        if (Hold is not null)
        {
            await Hold.Task.WaitAsync(cancellationToken);
        }

        if (Fail is not null)
        {
            throw Fail;
        }

        Put(_store, voice);
        Installed.Add(voice.Id);
        progress?.Report(1);
    }

    /// <summary>Makes a voice look installed by creating its model file.</summary>
    public static void Put(VoiceStore store, VoiceInfo voice)
    {
        Directory.CreateDirectory(store.FolderOf(voice));
        File.WriteAllText(Path.Combine(store.FolderOf(voice), voice.Model), "model");
        File.WriteAllText(Path.Combine(store.FolderOf(voice), VoiceStore.TokensFile), "tokens");
    }
}
