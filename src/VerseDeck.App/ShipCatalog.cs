using VerseDeck.App.Services;

namespace VerseDeck.App;

public static class ShipCatalog
{
    /// <summary>Theme for the manufacturer that prefixes the ship name; anything else is Neutral.</summary>
    public static ThemeId ThemeFor(string? shipName)
    {
        var manufacturer = (shipName ?? string.Empty).Trim().Split(' ', 2)[0];
        return ThemeService.TryParseTheme(manufacturer, out var theme) ? theme : ThemeId.Neutral;
    }

    /// <summary>What the copilot calls the ship: the model without its maker ("Gatac Syulen" is "Syulen").</summary>
    public static string Callsign(string? shipName)
    {
        var name = (shipName ?? string.Empty).Trim();
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && Manufacturers.Value.Contains(parts[0]) ? parts[1] : name;
    }

    private static readonly Lazy<HashSet<string>> Manufacturers = new(() =>
        Names.Select(n => n.Split(' ', 2)[0]).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static readonly IReadOnlyList<string> Names =
    [
        "Aegis Avenger Titan",
        "Aegis Gladius",
        "Aegis Hammerhead",
        "Aegis Reclaimer",
        "Aegis Retaliator",
        "Aegis Sabre",
        "Anvil Arrow",
        "Anvil Carrack",
        "Anvil C8R Pisces Rescue",
        "Anvil F7C Hornet Mk II",
        "Anvil F8C Lightning",
        "Anvil Terrapin",
        "Argo MOLE",
        "Argo MPUV",
        "Banu Defender",
        "Consolidated Outland Mustang Alpha",
        "Crusader Ares Ion",
        "Crusader C1 Spirit",
        "Crusader C2 Hercules",
        "Crusader Mercury Star Runner",
        "Crusader M2 Hercules",
        "Drake Buccaneer",
        "Drake Caterpillar",
        "Drake Corsair",
        "Drake Cutlass Black",
        "Drake Cutlass Red",
        "Drake Cutter",
        "Drake Dragonfly",
        "Esperia Blade",
        "Esperia Prowler",
        "Gatac Syulen",
        "Greycat ROC",
        "Kruger P-52 Merlin",
        "MISC Freelancer",
        "MISC Hull A",
        "MISC Prospector",
        "MISC Razor",
        "Mirai Fury",
        "Mirai Guardian",
        "Origin 100i",
        "Origin 300i",
        "Origin 400i",
        "Origin 600i",
        "Origin 890 Jump",
        "Origin M50",
        "RSI Aurora MR",
        "RSI Constellation Andromeda",
        "RSI Constellation Taurus",
        "RSI Galaxy",
        "RSI Perseus",
        "RSI Polaris",
        "Tumbril Cyclone"
    ];
}
