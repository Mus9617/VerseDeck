using VerseDeck.Core.Models;
using VerseDeck.Speech;
using VerseDeck.Voice;

namespace VerseDeck.Tests;

public class SpanishNumbersTests
{
    [Theory]
    [InlineData(1, "uno")]
    [InlineData(16, "dieciséis")]
    [InlineData(21, "veintiuno")]
    [InlineData(22, "veintidós")]
    [InlineData(30, "treinta")]
    [InlineData(45, "cuarenta y cinco")]
    [InlineData(100, "cien")]
    [InlineData(101, "ciento uno")]
    [InlineData(120, "ciento veinte")]
    public void ToWords_IsSpanish(int value, string words)
    {
        Assert.Equal(words, SpanishNumbers.ToWords(value));
    }

    [Fact]
    public void EveryNumber_RoundTrips()
    {
        for (var value = 1; value <= SpanishNumbers.Max; value++)
        {
            Assert.True(SpanishNumbers.TryParse(SpanishNumbers.ToWords(value), out var parsed));
            Assert.Equal(value, parsed);
        }
    }

    [Theory]
    [InlineData("un", 1)]
    [InlineData("una", 1)]
    [InlineData("veintiún", 21)]
    [InlineData("treinta y un", 31)]
    [InlineData("dieciseis", 16)]
    [InlineData("Cuarenta Y Cinco", 45)]
    public void ShortAndUnaccentedForms_AreUnderstood(string words, int value)
    {
        Assert.True(SpanishNumbers.TryParse(words, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("cero")]
    [InlineData("ciento veintiuno")]
    [InlineData("mil")]
    [InlineData("")]
    public void OutOfRange_IsRejected(string words)
    {
        Assert.False(SpanishNumbers.TryParse(words, out _));
    }
}

public class CompanionParserTests
{
    [Theory]
    [InlineData("hecho")]
    [InlineData("Siguiente")]
    [InlineData("listo")]
    public void DoneWords(string text)
    {
        Assert.Equal(CompanionKind.Done, CompanionParser.Parse(text)!.Kind);
    }

    [Theory]
    [InlineData("saltar", CompanionKind.Skip)]
    [InlineData("repetir", CompanionKind.Repeat)]
    [InlineData("cancelar checklist", CompanionKind.CancelChecklist)]
    [InlineData("cancelar temporizadores", CompanionKind.CancelTimers)]
    public void ControlWords(string text, CompanionKind kind)
    {
        Assert.Equal(kind, CompanionParser.Parse(text)!.Kind);
    }

    [Theory]
    [InlineData("checklist prevuelo")]
    [InlineData("empezar prevuelo")]
    public void StartChecklist_CarriesTheName(string text)
    {
        var command = CompanionParser.Parse(text)!;

        Assert.Equal(CompanionKind.StartChecklist, command.Kind);
        Assert.Equal("prevuelo", command.Name);
    }

    [Theory]
    [InlineData("avísame en diez minutos", 600)]
    [InlineData("avisame en un minuto", 60)]
    [InlineData("avísame en treinta segundos", 30)]
    [InlineData("avísame en ciento veinte minutos", 7200)]
    public void UnnamedTimer(string text, int seconds)
    {
        var command = CompanionParser.Parse(text)!;

        Assert.Equal(CompanionKind.Timer, command.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), command.Duration);
        Assert.Null(command.Label);
    }

    [Fact]
    public void NamedTimer_KeepsTheAccentedLabel()
    {
        var command = CompanionParser.Parse("temporizador refineria treinta y cinco minutos")!;

        Assert.Equal(CompanionKind.Timer, command.Kind);
        Assert.Equal("refinería", command.Label);
        Assert.Equal(TimeSpan.FromMinutes(35), command.Duration);
    }

    [Fact]
    public void ShortNamedTimer_LabelFirst()
    {
        var command = CompanionParser.Parse("hangar quince minutos")!;

        Assert.Equal("hangar", command.Label);
        Assert.Equal(TimeSpan.FromMinutes(15), command.Duration);
    }

    [Fact]
    public void Note_KeepsTheRecognisedText()
    {
        var command = CompanionParser.Parse("anota dejé la Cutlass en Lorville")!;

        Assert.Equal(CompanionKind.Note, command.Kind);
        Assert.Equal("dejé la Cutlass en Lorville", command.Text);
    }

    [Theory]
    [InlineData("anota")]
    [InlineData("nota   ")]
    [InlineData("avísame en mil minutos")]
    [InlineData("avísame en diez horas")]
    [InlineData("temporizador pizza diez minutos")]
    [InlineData("vamos a por ellos")]
    [InlineData("")]
    public void Nonsense_IsNull(string text)
    {
        Assert.Null(CompanionParser.Parse(text));
    }

    [Fact]
    public void Grammar_Builds_WithAndWithoutChecklists()
    {
        var culture = new System.Globalization.CultureInfo("es-ES");

        Assert.Equal(2, CompanionGrammar.Build(culture, ["prevuelo", "aterrizaje"]).Count);
        Assert.Equal(2, CompanionGrammar.Build(culture, []).Count);
    }
}

/// <summary>Real engine, real voice: optional, needs VERSEDECK_VOICES_ROOT and an installed Spanish recogniser.</summary>
public class RealCompanionRecognitionTests
{
    [Fact]
    public async Task TimerAndNotePhrases_AreRecognisedAndParsed()
    {
        var root = Environment.GetEnvironmentVariable("VERSEDECK_VOICES_ROOT");
        var voice = VoiceCatalog.Load().Find(VoiceCatalog.DefaultVoiceId)!;
        var recognizer = CommandGrammar.Recognizers().FirstOrDefault();
        if (string.IsNullOrEmpty(root) || !new VoiceStore(root).IsInstalled(voice) || recognizer is null)
        {
            return;
        }

        var folder = SpeechFixture.TempFolder("companion");
        try
        {
            using var tts = new SherpaTtsEngine(new VoiceStore(root));
            var cache = new PhraseCache(folder);
            string[] spoken = ["Avísame en diez minutos.", "Refinería, diez minutos.", "Checklist prevuelo.", "Hecho.", "Anota, dejé la nave en Lorville."];
            var results = new List<string?>();
            using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(recognizer);
            foreach (var grammar in CompanionGrammar.Build(recognizer.Culture, ["prevuelo", "aterrizaje"]))
            {
                engine.LoadGrammar(grammar);
            }

            foreach (var text in spoken)
            {
                var wav = cache.Store(voice, text, await tts.SynthesizeAsync(voice, text, CancellationToken.None)).Path;
                var converted = wav + ".16k.wav";
                WindowsPhraseChecker.ToRecognizerFormat(wav, converted);
                engine.SetInputToWaveFile(converted);
                results.Add(engine.Recognize()?.Text);
                engine.SetInputToNull();
            }

            var heard = string.Join(" | ", results);
            Assert.True(results.All(r => r is not null), heard);
            Assert.Equal(TimeSpan.FromMinutes(10), CompanionParser.Parse(results[0]!)!.Duration);
            Assert.Equal("refinería", CompanionParser.Parse(results[1]!)?.Label);
            Assert.Equal("prevuelo", CompanionParser.Parse(results[2]!)!.Name);
            Assert.Equal(CompanionKind.Done, CompanionParser.Parse(results[3]!)!.Kind);

            // Dictation transcribes synthetic speech loosely; what matters is that a note is taken.
            Assert.Equal(CompanionKind.Note, CompanionParser.Parse(results[4]!)!.Kind);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
