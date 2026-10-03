using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VerseDeck.Speech;

public sealed record VoiceInfo(string Id, string Name, string Engine, string Folder, string Model, int Speaker, string Url, long Bytes, string Sha256, string License);

/// <summary>The voices VerseDeck knows how to download and run. Several voices can share one model folder.</summary>
public sealed class VoiceCatalog
{
    public const string DefaultVoiceId = "piper-davefx";

    private VoiceCatalog(IReadOnlyList<VoiceInfo> voices)
    {
        Voices = voices;
    }

    public IReadOnlyList<VoiceInfo> Voices { get; }

    public static VoiceCatalog Load()
    {
        using var stream = Resources.Open("voices.json");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, Resources.Json)
            ?? throw new InvalidOperationException("voices.json is empty.");
        return new VoiceCatalog(file.Voices);
    }

    public VoiceInfo? Find(string? id)
    {
        return Voices.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CatalogFile(List<VoiceInfo> Voices);
}

/// <summary>Where downloaded voice models live on disk.</summary>
public sealed class VoiceStore
{
    public VoiceStore(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string FolderOf(VoiceInfo voice) => Path.Combine(Root, voice.Folder);

    public const string TokensFile = "tokens.txt";

    /// <summary>True when the files the engine cannot work without are on disk.</summary>
    public bool IsInstalled(VoiceInfo voice)
    {
        var folder = FolderOf(voice);
        return File.Exists(Path.Combine(folder, voice.Model)) && File.Exists(Path.Combine(folder, TokensFile));
    }
}

public sealed record SpeechAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

public sealed record CachedPhrase(string Path, TimeSpan Duration);

/// <summary>
/// Rendered phrases as WAV files. Responses are fixed sentences, so each is synthesized once per voice
/// and then played instantly, which is what makes the slower voices usable.
/// </summary>
public sealed class PhraseCache
{
    private const int HeaderBytes = 44;

    private readonly string _root;

    public PhraseCache(string root)
    {
        _root = root;
    }

    public int Count => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).Count() : 0;

    public CachedPhrase? TryGet(VoiceInfo voice, string text)
    {
        var path = PathFor(voice, text);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= HeaderBytes)
            {
                return null;
            }

            using var reader = new BinaryReader(File.OpenRead(path));
            reader.BaseStream.Position = 24;
            var sampleRate = reader.ReadInt32();
            return sampleRate <= 0 ? null : new CachedPhrase(path, TimeSpan.FromSeconds((info.Length - HeaderBytes) / 2.0 / sampleRate));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public CachedPhrase Store(VoiceInfo voice, string text, SpeechAudio audio)
    {
        var path = PathFor(voice, text);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written under a temporary name so a half-written file is never taken for a finished phrase.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            var dataBytes = audio.Samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(audio.SampleRate);
            writer.Write(audio.SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (var sample in audio.Samples)
            {
                writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
            }
        }

        File.Move(temporary, path, overwrite: true);
        return new CachedPhrase(path, audio.Duration);
    }

    public void Clear()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".wav") || f.EndsWith(".tmp")).ToList())
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A phrase that is being played stays until the next clear.
            }
        }
    }

    private string PathFor(VoiceInfo voice, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..32];
        return Path.Combine(_root, voice.Id, $"{hash}.wav");
    }
}

internal static class Resources
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Stream Open(string name)
    {
        return typeof(Resources).Assembly.GetManifestResourceStream($"VerseDeck.Speech.{name}")
            ?? throw new InvalidOperationException($"{name} is missing from the assembly.");
    }

    public static IEnumerable<string> Names(string prefix)
    {
        return typeof(Resources).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith($"VerseDeck.Speech.{prefix}", StringComparison.Ordinal))
            .Select(n => n["VerseDeck.Speech.".Length..]);
    }
}
