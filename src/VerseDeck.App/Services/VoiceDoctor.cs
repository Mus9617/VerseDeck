using System.Globalization;
using VerseDeck.Voice;

namespace VerseDeck.App.Services;

/// <summary>Ordered from worst to best, so sorting puts the phrases that need attention first.</summary>
public enum VerdictKind { Confused, NotUnderstood, Unchecked, Ok }

public sealed record PhraseVerdict(string Phrase, string Module, VerdictKind Kind, string? ConfusedWith, double Confidence, string Text);

/// <summary>
/// Checks voice phrases before the player relies on them: each phrase is spoken by the copilot's voice and
/// run through the real Windows recogniser, offline. It only works on request and frees the voice model after.
/// </summary>
public sealed class VoiceDoctor
{
    // Every extra voice multiplies the work; a few different voices already show which phrases are fragile.
    private const int MaxVoices = 3;

    private readonly DeckSession _session;
    private readonly CopilotService _copilot;
    private readonly IPhraseChecker _checker;
    private readonly IDebugLog _log;
    private CancellationTokenSource? _running;

    public VoiceDoctor(DeckSession session, CopilotService copilot, IPhraseChecker checker, IDebugLog log)
    {
        _session = session;
        _copilot = copilot;
        _checker = checker;
        _log = log;
    }

    /// <summary>Checks one phrase for a module, as if it were already saved.</summary>
    public async Task<PhraseVerdict> CheckPhraseAsync(long buttonId, string phrase, CancellationToken cancellationToken = default)
    {
        var module = _session.Buttons.FirstOrDefault(b => b.Id == buttonId)?.Name ?? "?";
        var clean = phrase.Trim().ToLowerInvariant();
        try
        {
            var verdicts = await CheckAsync([(clean, buttonId, module)], null, cancellationToken);
            return verdicts[0];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write($"Phrase check failed for '{clean}': {ex.Message}");
            return new PhraseVerdict(clean, module, VerdictKind.Unchecked, null, 0, $"No se pudo comprobar: {ex.Message}");
        }
    }

    /// <summary>Checks every enabled phrase of the active profile, the problems first.</summary>
    public async Task<IReadOnlyList<PhraseVerdict>> CheckAllAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var names = _session.Buttons.ToDictionary(b => b.Id, b => b.Name);
        var items = _session.VoiceCommands
            .Where(c => c.Enabled && names.ContainsKey(c.ButtonId))
            .Select(c => (Phrase: c.Phrase.Trim().ToLowerInvariant(), c.ButtonId, Module: names[c.ButtonId]))
            .DistinctBy(i => i.Phrase)
            .ToList();
        var verdicts = await CheckAsync(items, progress, cancellationToken);
        return verdicts.OrderBy(v => v.Kind).ThenBy(v => v.Module, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Phrase, StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyList<PhraseVerdict>> CheckAsync(
        IReadOnlyList<(string Phrase, long ButtonId, string Module)> items,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var voices = _copilot.InstalledVoices.Take(MaxVoices).ToList();
        if (voices.Count == 0)
        {
            throw new InvalidOperationException("Descarga una voz en la seccion Copiloto para poder comprobar frases.");
        }

        // A new check replaces one that is still running instead of doubling the work.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _running, cancellation)?.Cancel();
        try
        {
            // The grammar is what the live engine would listen for: the profile's phrases plus the ones being checked.
            var owners = _session.VoiceCommands
                .Where(c => c.Enabled)
                .GroupBy(c => c.Phrase.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().ButtonId);
            foreach (var item in items)
            {
                owners.TryAdd(item.Phrase, item.ButtonId);
            }

            var grammar = owners.Keys.ToList();
            var worst = items.Select(_ => (Kind: VerdictKind.Ok, ConfusedWith: (string?)null, Heard: (string?)null, Confidence: 1.0)).ToArray();
            for (var v = 0; v < voices.Count; v++)
            {
                var paths = new List<string>();
                for (var i = 0; i < items.Count; i++)
                {
                    progress?.Report($"Voz {v + 1} de {voices.Count}: preparando {i + 1} de {items.Count}");
                    paths.Add((await _copilot.RenderAsync(voices[v], items[i].Phrase, cancellation.Token)).Path);
                }

                progress?.Report($"Voz {v + 1} de {voices.Count}: escuchando {items.Count} frases");
                var hits = await _checker.CheckAsync(grammar, paths, cancellation.Token);
                for (var i = 0; i < items.Count; i++)
                {
                    var verdict = Classify(items[i].ButtonId, hits[i], owners);
                    if (verdict.Kind < worst[i].Kind || (verdict.Kind == worst[i].Kind && verdict.Confidence < worst[i].Confidence))
                    {
                        worst[i] = verdict;
                    }
                }
            }

            return items.Select((item, i) => new PhraseVerdict(item.Phrase, item.Module, worst[i].Kind, worst[i].ConfusedWith, worst[i].Confidence, Describe(worst[i]))).ToList();
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _running, null, cancellation) == cancellation)
            {
                // Checks are occasional; the model should not stay in memory next to the game afterwards.
                _copilot.ReleaseEngine();
            }

            cancellation.Dispose();
        }
    }

    private (VerdictKind Kind, string? ConfusedWith, string? Heard, double Confidence) Classify(long buttonId, PhraseHit hit, IReadOnlyDictionary<string, long> owners)
    {
        if (hit.Text is null || hit.Discarded || !owners.TryGetValue(hit.Text.Trim().ToLowerInvariant(), out var heardButton))
        {
            return (VerdictKind.NotUnderstood, null, hit.Text, 0);
        }

        if (heardButton == buttonId)
        {
            return (VerdictKind.Ok, null, hit.Text, hit.Confidence);
        }

        var other = _session.Buttons.FirstOrDefault(b => b.Id == heardButton)?.Name ?? "otro modulo";
        return (VerdictKind.Confused, other, hit.Text, hit.Confidence);
    }

    private static string Describe((VerdictKind Kind, string? ConfusedWith, string? Heard, double Confidence) verdict) => verdict.Kind switch
    {
        VerdictKind.Ok => $"Bien (confianza {verdict.Confidence.ToString("0.00", CultureInfo.CurrentCulture)})",
        VerdictKind.Confused => $"Se confunde con {verdict.ConfusedWith} (oye '{verdict.Heard}')",
        _ => "No la entiende: prueba otra frase mas larga o en espanol"
    };
}
