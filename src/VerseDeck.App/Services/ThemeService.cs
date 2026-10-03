namespace VerseDeck.App.Services;

// Named as the ship makers write themselves, so "RSI Constellation" finds RSI.
public enum ThemeId { Neutral, Drake, Origin, Aegis, Anvil, Gatac, RSI, MISC, Crusader, Esperia, Banu }

public sealed class ThemeService
{
    public const string Auto = "Auto";

    private readonly Action<ThemeId> _apply;
    private ThemeId? _current;

    public ThemeService(Action<ThemeId> apply)
    {
        _apply = apply;
    }

    public ThemeId Current => _current ?? ThemeId.Neutral;

    /// <summary>Exact theme name only; Enum.TryParse would also accept numbers and comma lists.</summary>
    public static bool TryParseTheme(string? text, out ThemeId theme)
    {
        foreach (var candidate in Enum.GetValues<ThemeId>())
        {
            if (candidate.ToString().Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                theme = candidate;
                return true;
            }
        }

        theme = ThemeId.Neutral;
        return false;
    }

    public IReadOnlyList<string> Choices { get; } = [Auto, .. Enum.GetNames<ThemeId>()];

    /// <summary>Applies the fixed theme named by the setting, or the ship manufacturer's theme when it is not a theme name.</summary>
    public void Update(string? themeSetting, string? shipName)
    {
        var theme = TryParseTheme(themeSetting, out var fixedTheme) ? fixedTheme : ShipCatalog.ThemeFor(shipName);
        if (_current == theme)
        {
            return;
        }

        _current = theme;
        _apply(theme);
    }
}
