using System.Globalization;
using System.Speech.Recognition;
using System.Text;
using VerseDeck.Core.Models;

namespace VerseDeck.Voice;

/// <summary>Spanish number words from 1 to 120, which is what the recogniser returns for spoken numbers.</summary>
public static class SpanishNumbers
{
    public const int Max = 120;

    private static readonly string[] Units = ["", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve"];
    private static readonly string[] Teens = ["diez", "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve"];
    private static readonly string[] Twenties = ["veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve"];
    private static readonly string[] Tens = ["", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa"];

    private static readonly Lazy<Dictionary<string, int>> Lookup = new(() =>
    {
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (words, value) in AllWords())
        {
            lookup[SpeechText.Normalize(words)] = value;
        }

        return lookup;
    });

    public static string ToWords(int value)
    {
        if (value is < 1 or > Max)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value == 100)
        {
            return "cien";
        }

        if (value > 100)
        {
            return $"ciento {ToWords(value - 100)}";
        }

        return value switch
        {
            < 10 => Units[value],
            < 20 => Teens[value - 10],
            < 30 => Twenties[value - 20],
            _ => value % 10 == 0 ? Tens[value / 10] : $"{Tens[value / 10]} y {Units[value % 10]}"
        };
    }

    /// <summary>Every way a number can be said, including the short forms before a noun ("un minuto", "veintiún").</summary>
    public static IEnumerable<(string Words, int Value)> AllWords()
    {
        for (var value = 1; value <= Max; value++)
        {
            var words = ToWords(value);
            yield return (words, value);
            if (words.EndsWith("uno", StringComparison.Ordinal))
            {
                yield return (words == "veintiuno" ? "veintiún" : words[..^3] + "un", value);
            }
        }

        yield return ("una", 1);
    }

    public static bool TryParse(string words, out int value) => Lookup.Value.TryGetValue(SpeechText.Normalize(words), out value);
}

/// <summary>Accent- and punctuation-insensitive text for matching what the recogniser returns.</summary>
public static class SpeechText
{
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>The copilot's own vocabulary: checklists, timers and notes, next to the player's module phrases.</summary>
public static class CompanionGrammar
{
    public const string Commands = "companion";
    public const string Notes = "companion-note";

    public static readonly IReadOnlyList<string> TimerLabels = ["refinería", "reclamación", "hangar", "carga", "combustible", "misión", "descanso"];
    public static readonly IReadOnlyList<string> Done = ["hecho", "siguiente", "listo"];
    private static readonly string[] Units = ["minutos", "minuto", "segundos", "segundo"];

    /// <summary>The amounts a timer can be set to by voice; the screen accepts any value.</summary>
    public static readonly IReadOnlySet<int> TimerAmounts = Enumerable.Range(1, 15).Concat([20, 25, 30, 40, 45, 50, 60, 90, 120]).ToHashSet();

    public static IReadOnlyList<Grammar> Build(CultureInfo culture, IReadOnlyList<string> checklistNames)
    {
        // Fewer numbers, fewer confusions: in a test "treinta" was heard as "quince" with the full range.
        var numbers = new Choices(SpanishNumbers.AllWords().Where(n => TimerAmounts.Contains(n.Value)).Select(n => n.Words).Distinct().ToArray());
        var units = new Choices(Units);
        var alternatives = new List<GrammarBuilder>();
        alternatives.AddRange(Done.Concat(["saltar", "repetir", "cancelar checklist", "cancelar temporizadores"]).Select(word => new GrammarBuilder(word)));

        var names = checklistNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (names.Length > 0)
        {
            foreach (var verb in new[] { "checklist", "empezar" })
            {
                var start = new GrammarBuilder(verb);
                start.Append(new Choices(names));
                alternatives.Add(start);
            }
        }

        var unnamed = new GrammarBuilder("avísame en");
        unnamed.Append(numbers);
        unnamed.Append(units);
        alternatives.Add(unnamed);

        // Named timers are said label first ("refinería treinta minutos"): in a test the longer
        // "temporizador refinería ..." was misheard as control words.
        var labelFirst = new GrammarBuilder(new Choices(TimerLabels.ToArray()));
        labelFirst.Append(numbers);
        labelFirst.Append(units);
        alternatives.Add(labelFirst);

        var commands = new GrammarBuilder(new Choices(alternatives.ToArray())) { Culture = culture };

        // Free text only after the note keyword, so dictation never competes with commands otherwise.
        var noteBuilders = new[] { "anota", "nota" }.Select(word =>
        {
            var builder = new GrammarBuilder(word);
            builder.AppendDictation();
            return builder;
        }).ToArray();
        var notes = new GrammarBuilder(new Choices(noteBuilders)) { Culture = culture };

        return [new Grammar(commands) { Name = Commands }, new Grammar(notes) { Name = Notes }];
    }
}

public static class CompanionParser
{
    private static readonly Dictionary<string, string> Labels = CompanionGrammar.TimerLabels.ToDictionary(SpeechText.Normalize, label => label);

    /// <summary>What a recognised companion phrase asks for, or null when it does not make sense.</summary>
    public static CompanionCommand? Parse(string recognized)
    {
        var text = SpeechText.Normalize(recognized);
        switch (text)
        {
            case "hecho" or "siguiente" or "listo":
                return new CompanionCommand(CompanionKind.Done);
            case "saltar":
                return new CompanionCommand(CompanionKind.Skip);
            case "repetir":
                return new CompanionCommand(CompanionKind.Repeat);
            case "cancelar checklist":
                return new CompanionCommand(CompanionKind.CancelChecklist);
            case "cancelar temporizadores":
                return new CompanionCommand(CompanionKind.CancelTimers);
        }

        var words = text.Split(' ');
        if (words.Length >= 2 && words[0] is "checklist" or "empezar")
        {
            return new CompanionCommand(CompanionKind.StartChecklist, Name: string.Join(' ', words[1..]));
        }

        if (words.Length >= 2 && words[0] is "anota" or "nota")
        {
            // The note keeps the recogniser's own spelling, accents and capitals included.
            var original = recognized.Trim();
            var note = original[(original.IndexOf(' ') + 1)..].Trim();
            return note.Length == 0 ? null : new CompanionCommand(CompanionKind.Note, Text: note);
        }

        if (words.Length >= 4 && words[0] == "avisame" && words[1] == "en")
        {
            return Timer(null, words[2..]);
        }

        if (words.Length >= 4 && words[0] == "temporizador" && Labels.TryGetValue(words[1], out var label))
        {
            return Timer(label, words[2..]);
        }

        if (words.Length >= 3 && Labels.TryGetValue(words[0], out var shortLabel))
        {
            return Timer(shortLabel, words[1..]);
        }

        return null;
    }

    private static CompanionCommand? Timer(string? label, string[] rest)
    {
        var unit = rest[^1];
        var seconds = unit.StartsWith("segundo", StringComparison.Ordinal);
        if (!seconds && !unit.StartsWith("minuto", StringComparison.Ordinal))
        {
            return null;
        }

        if (!SpanishNumbers.TryParse(string.Join(' ', rest[..^1]), out var amount))
        {
            return null;
        }

        return new CompanionCommand(CompanionKind.Timer, Duration: seconds ? TimeSpan.FromSeconds(amount) : TimeSpan.FromMinutes(amount), Label: label);
    }
}
