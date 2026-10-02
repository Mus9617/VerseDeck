namespace VerseDeck.App.Services;

public enum ThemeId { Neutral, Drake, Origin, Aegis, Anvil }

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

    public IReadOnlyList<string> Choices { get; } = [Auto, .. Enum.GetNames<ThemeId>()];

    /// <summary>Applies the fixed theme named by the setting, or the ship manufacturer's theme when it is not a theme name.</summary>
    public void Update(string? themeSetting, string? shipName)
    {
        var theme = Enum.TryParse<ThemeId>(themeSetting, ignoreCase: true, out var fixedTheme) && Enum.IsDefined(fixedTheme)
            ? fixedTheme
            : ShipCatalog.ThemeFor(shipName);
        if (_current == theme)
        {
            return;
        }

        _current = theme;
        _apply(theme);
    }
}
