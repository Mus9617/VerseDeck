using System.Text.Json;
using VerseDeck.Core.Models;

namespace VerseDeck.Speech;

/// <summary>A personality: the same events, worded differently. Phrases confirm the order, never a ship state.</summary>
public sealed class ResponsePack
{
    private ResponsePack(PackFile file)
    {
        Id = file.Id;
        Name = file.Name;
        Greeting = file.Greeting;
        Generic = file.Generic;
        Hold = file.Hold;
        Failed = file.Failed;
        NoKey = file.NoKey;
        Profile = file.Profile;
        Actions = new Dictionary<string, List<string>>(file.Actions, StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Greeting { get; }
    public IReadOnlyList<string> Generic { get; }
    public IReadOnlyList<string> Hold { get; }
    public IReadOnlyList<string> Failed { get; }
    public IReadOnlyList<string> NoKey { get; }
    public IReadOnlyList<string> Profile { get; }
    public IReadOnlyDictionary<string, List<string>> Actions { get; }

    public static IReadOnlyList<ResponsePack> LoadAll()
    {
        return Resources.Names("Packs.")
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = Resources.Open(name);
                return new ResponsePack(JsonSerializer.Deserialize<PackFile>(stream, Resources.Json)
                    ?? throw new InvalidOperationException($"{name} is empty."));
            })
            .OrderBy(pack => pack.Id == "sobria" ? 0 : 1)
            .ToList();
    }

    private sealed record PackFile(
        string Id,
        string Name,
        List<string> Greeting,
        List<string> Generic,
        List<string> Hold,
        List<string> Failed,
        List<string> NoKey,
        List<string> Profile,
        Dictionary<string, List<string>> Actions);
}

/// <summary>Chooses what the copilot says for a module or an event.</summary>
public sealed class ResponseSelector
{
    public const string Silent = "-";

    // A press held this long is announced with the "hold" wording when no specific phrase exists.
    private const int HoldThresholdMs = 500;

    private static readonly Lazy<Dictionary<string, string>> Aliases = new(() =>
    {
        using var stream = Resources.Open("aliases.json");
        return new Dictionary<string, string>(
            JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [],
            StringComparer.OrdinalIgnoreCase);
    });

    private readonly ResponsePack _pack;
    private readonly Random _random;
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);

    public ResponseSelector(ResponsePack pack, Random random)
    {
        _pack = pack;
        _random = random;
    }

    /// <summary>The phrase for a module that was just triggered, or null when the module is set to stay silent.</summary>
    public string? ForModule(DeckButton button)
    {
        var variants = VariantsFor(button);
        return variants.Count == 0 ? null : Pick($"module:{button.Id}", variants);
    }

    public string Failed() => Pick("failed", _pack.Failed);

    public string NoKey() => Pick("noKey", _pack.NoKey);

    public string Profile(string name) => Fill(Pick("profile", _pack.Profile), name);

    public string Greeting() => Pick("greeting", _pack.Greeting);

    /// <summary>Everything that could be said for these modules, used to render the cache ahead of time.</summary>
    public IReadOnlyList<string> AllFor(IEnumerable<DeckButton> buttons)
    {
        return buttons.SelectMany(VariantsFor)
            .Concat(_pack.Failed)
            .Concat(_pack.NoKey)
            .Concat(_pack.Greeting)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> VariantsFor(DeckButton button)
    {
        var custom = (button.Response ?? string.Empty).Trim();
        if (custom == Silent)
        {
            return [];
        }

        if (custom.Length > 0)
        {
            return custom.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(variant => Fill(variant, button.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (_pack.Actions.TryGetValue(button.GameAction ?? string.Empty, out var byAction))
        {
            return byAction;
        }

        if (Aliases.Value.TryGetValue(button.Name, out var alias) && _pack.Actions.TryGetValue(alias, out var byName))
        {
            return byName;
        }

        var templates = button.Action.PressDurationMs >= HoldThresholdMs ? _pack.Hold : _pack.Generic;
        return templates.Select(t => Fill(t, button.Name)).ToList();
    }

    // The same wording twice in a row sounds like a recording, so the previous choice is skipped.
    private string Pick(string key, IReadOnlyList<string> variants)
    {
        IReadOnlyList<string> candidates = variants;
        if (variants.Count > 1 && _last.TryGetValue(key, out var previous))
        {
            var others = variants.Where(v => v != previous).ToList();
            if (others.Count > 0)
            {
                candidates = others;
            }
        }

        var chosen = candidates[_random.Next(candidates.Count)];
        _last[key] = chosen;
        return chosen;
    }

    private static string Fill(string template, string name) => template.Replace("{nombre}", name, StringComparison.Ordinal);
}
