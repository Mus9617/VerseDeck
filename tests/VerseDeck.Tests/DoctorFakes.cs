using VerseDeck.Speech;
using VerseDeck.Voice;

namespace VerseDeck.Tests;

/// <summary>
/// Stands in for the Windows recogniser. It works out which phrase each audio file holds from the phrase cache,
/// then answers what a test told it to, or the phrase itself.
/// </summary>
public sealed class FakePhraseChecker : IPhraseChecker
{
    private readonly PhraseCache _cache;
    private readonly IReadOnlyList<VoiceInfo> _voices;

    public FakePhraseChecker(PhraseCache cache, IReadOnlyList<VoiceInfo> voices)
    {
        _cache = cache;
        _voices = voices;
    }

    /// <summary>What a phrase is heard as, optionally per voice id. Missing entries are heard correctly.</summary>
    public Dictionary<string, PhraseHit> HeardAs { get; } = [];
    public Dictionary<(string Voice, string Phrase), PhraseHit> HeardAsByVoice { get; } = [];
    public Exception? Fail { get; set; }
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public TaskCompletionSource? Hold { get; set; }
    public int Calls { get; private set; }
    public List<IReadOnlyList<string>> Grammars { get; } = [];

    public async Task<IReadOnlyList<PhraseHit>> CheckAsync(IReadOnlyList<string> phrases, IReadOnlyList<string> wavPaths, CancellationToken cancellationToken)
    {
        Calls++;
        Grammars.Add(phrases);
        if (Hold is not null)
        {
            await Hold.Task.WaitAsync(cancellationToken);
        }

        if (Fail is not null)
        {
            throw Fail;
        }

        return wavPaths.Select(path =>
        {
            foreach (var voice in _voices)
            {
                foreach (var phrase in phrases)
                {
                    if (_cache.TryGet(voice, phrase)?.Path == path)
                    {
                        if (HeardAsByVoice.TryGetValue((voice.Id, phrase), out var byVoice))
                        {
                            return byVoice;
                        }

                        return HeardAs.TryGetValue(phrase, out var hit) ? hit : new PhraseHit(phrase, 0.8, false);
                    }
                }
            }

            return new PhraseHit(null, 0, false);
        }).ToList();
    }
}
