using VerseDeck.Speech;

namespace VerseDeck.Tests;

/// <summary>
/// Runs the real sherpa-onnx engine. Skipped unless VERSEDECK_VOICES_ROOT points at a folder that
/// holds a downloaded voice, because the models are tens of megabytes and are not part of the repository.
/// </summary>
public class RealSpeechEngineTests
{
    [Fact]
    public async Task DefaultVoice_SynthesizesSpanishSpeech_AndTheCachePlaysItBack()
    {
        var root = Environment.GetEnvironmentVariable("VERSEDECK_VOICES_ROOT");
        var voice = VoiceCatalog.Load().Find(VoiceCatalog.DefaultVoiceId)!;
        if (string.IsNullOrEmpty(root) || !new VoiceStore(root).IsInstalled(voice))
        {
            return;
        }

        using var engine = new SherpaTtsEngine(new VoiceStore(root));
        var audio = await engine.SynthesizeAsync(voice, "Tren de aterrizaje.", CancellationToken.None);
        var again = await engine.SynthesizeAsync(voice, "Motores, hecho.", CancellationToken.None);

        Assert.InRange(audio.Duration.TotalSeconds, 0.5, 5);
        Assert.InRange(again.Duration.TotalSeconds, 0.5, 5);
        Assert.Equal(22050, audio.SampleRate);
        Assert.Contains(audio.Samples, s => Math.Abs(s) > 0.05f);

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"versedeck-realcache-{Guid.NewGuid():N}");
        try
        {
            var cached = new PhraseCache(cacheRoot).Store(voice, "Tren de aterrizaje.", audio);
            Assert.Equal(audio.Duration.TotalSeconds, cached.Duration.TotalSeconds, 2);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }
}
