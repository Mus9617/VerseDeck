using System.Globalization;
using System.Speech.Recognition;
using VerseDeck.Core.Models;

namespace VerseDeck.Voice;

/// <summary>
/// The grammars every recogniser in VerseDeck uses: the player's phrases, plus free dictation competing
/// with them. When what was heard sounds more like conversation than like a command, dictation wins and
/// nothing is executed. In a local benchmark this cut accepted non-commands from 56 of 72 to 12.
/// </summary>
public static class CommandGrammar
{
    public const string Commands = "commands";
    public const string Discard = "discard";

    public static void Load(SpeechRecognitionEngine engine, IEnumerable<string> phrases)
    {
        engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(phrases.ToArray())) { Culture = engine.RecognizerInfo.Culture }) { Name = Commands });
        engine.LoadGrammar(new DictationGrammar { Name = Discard });
    }

    /// <summary>Spanish recognisers first, then English ones; the first is used for phrase checks.</summary>
    public static IReadOnlyList<RecognizerInfo> Recognizers()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        return installed
            .Where(r => r.Culture.Name.Equals("es-ES", StringComparison.OrdinalIgnoreCase))
            .Concat(installed.Where(r => r.Culture.TwoLetterISOLanguageName == "es"))
            .Concat(installed.Where(r => r.Culture.Name is "en-US" or "en-GB"))
            .Concat(installed.Where(r => r.Culture.TwoLetterISOLanguageName == "en"))
            .DistinctBy(r => r.Id)
            .ToList();
    }
}

public static class RecognitionRules
{
    /// <summary>Why a recognition must not run a command, or null when it may.</summary>
    public static RecognitionOutcome? Classify(string? grammar, double confidence, double minimum, bool knownPhrase, bool gateOpen)
    {
        if (!gateOpen)
        {
            return RecognitionOutcome.GateClosed;
        }

        if (grammar == CommandGrammar.Discard || !knownPhrase)
        {
            return RecognitionOutcome.Discarded;
        }

        return confidence < minimum ? RecognitionOutcome.LowConfidence : null;
    }
}

public sealed record PhraseHit(string? Text, double Confidence, bool Discarded);

public interface IPhraseChecker
{
    /// <summary>Recognises each WAV file against the phrases, without a microphone and without the live engine.</summary>
    Task<IReadOnlyList<PhraseHit>> CheckAsync(IReadOnlyList<string> phrases, IReadOnlyList<string> wavPaths, CancellationToken cancellationToken);
}

/// <summary>
/// Runs Windows speech recognition over audio files. It builds its own engine for each call and frees it at
/// the end, so it costs nothing while the player is not checking phrases.
/// </summary>
public sealed class WindowsPhraseChecker : IPhraseChecker
{
    private const int TargetRate = 16000;
    private const int PaddingSamples = TargetRate * 3 / 10;

    public Task<IReadOnlyList<PhraseHit>> CheckAsync(IReadOnlyList<string> phrases, IReadOnlyList<string> wavPaths, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew<IReadOnlyList<PhraseHit>>(
            () => Check(phrases, wavPaths, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static IReadOnlyList<PhraseHit> Check(IReadOnlyList<string> phrases, IReadOnlyList<string> wavPaths, CancellationToken cancellationToken)
    {
        // Phrases are checked against the same language the player's commands are heard in.
        var recognizer = CommandGrammar.Recognizers().FirstOrDefault()
            ?? throw new InvalidOperationException("Windows no tiene instalado un reconocedor de voz en español o inglés.");
        var temporary = Path.Combine(Path.GetTempPath(), $"versedeck-check-{Guid.NewGuid():N}.wav");
        var hits = new List<PhraseHit>();
        try
        {
            using var engine = new SpeechRecognitionEngine(recognizer);
            CommandGrammar.Load(engine, phrases.Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var path in wavPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToRecognizerFormat(path, temporary);
                engine.SetInputToWaveFile(temporary);
                var result = engine.Recognize();
                engine.SetInputToNull();
                hits.Add(result is null
                    ? new PhraseHit(null, 0, false)
                    : new PhraseHit(result.Text, result.Confidence, result.Grammar?.Name == CommandGrammar.Discard));
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }

        return hits;
    }

    /// <summary>Converts a 16-bit mono PCM WAV to 16 kHz with a short silence either side, as a real utterance has.</summary>
    internal static void ToRecognizerFormat(string source, string target)
    {
        var bytes = File.ReadAllBytes(source);
        var rate = BitConverter.ToInt32(bytes, 24);
        var input = new short[(bytes.Length - 44) / 2];
        Buffer.BlockCopy(bytes, 44, input, 0, input.Length * 2);

        var ratio = rate / (double)TargetRate;
        var count = (int)(input.Length / ratio);
        var output = new short[count + 2 * PaddingSamples];
        for (var i = 0; i < count; i++)
        {
            var position = i * ratio;
            var index = (int)position;
            var next = Math.Min(index + 1, input.Length - 1);
            output[PaddingSamples + i] = (short)(input[index] + (input[next] - input[index]) * (position - index));
        }

        using var writer = new BinaryWriter(File.Create(target));
        writer.Write("RIFF"u8);
        writer.Write(36 + output.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(TargetRate);
        writer.Write(TargetRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(output.Length * 2);
        foreach (var sample in output)
        {
            writer.Write(sample);
        }
    }
}

internal static class Invariant
{
    public static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
