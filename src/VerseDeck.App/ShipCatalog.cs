using VerseDeck.App.Services;

namespace VerseDeck.App;

/// <summary>
/// Ship names for the profile list, written "Maker Model" as the game does. The field stays editable, so a
/// ship missing here can still be typed; this list only saves typing and picks the maker's theme.
/// </summary>
public static class ShipCatalog
{
    private static readonly (string Maker, string[] Models)[] Fleet =
    [
        ("Aegis", ["Avenger Stalker", "Avenger Titan", "Avenger Titan Renegade", "Avenger Warlock", "Eclipse", "Gladius", "Gladius Valiant",
            "Hammerhead", "Idris-P", "Reclaimer", "Redeemer", "Retaliator", "Sabre", "Sabre Comet", "Sabre Firebird", "Sabre Peregrine",
            "Sabre Raven", "Vanguard Harbinger", "Vanguard Hoplite", "Vanguard Sentinel", "Vanguard Warden"]),
        ("Anvil", ["Arrow", "Asgard", "Ballista", "C8 Pisces", "C8R Pisces Rescue", "C8X Pisces Expedition", "Carrack", "Carrack Expedition",
            "Centurion", "F7A Hornet Mk II", "F7C Hornet Mk II", "F7C-M Super Hornet Mk II", "F7C-M Heartseeker Mk II", "F7C-R Hornet Tracker Mk II",
            "F7C-S Hornet Ghost Mk II", "F8C Lightning", "Gladiator", "Hawk", "Hurricane", "Legionnaire", "Paladin", "Spartan", "Terrapin",
            "Terrapin Medic", "Valkyrie"]),
        ("Aopoa", ["Khartu-Al", "Nox", "San'tok.yai"]),
        ("Argo", ["CSV-SM", "MOLE", "MPUV Cargo", "MPUV Personnel", "MPUV Tractor", "RAFT", "SRV"]),
        ("Banu", ["Defender"]),
        ("Consolidated Outland", ["HoverQuad", "Mustang Alpha", "Mustang Beta", "Mustang Delta", "Mustang Gamma", "Mustang Omega", "Nomad"]),
        ("Crusader", ["A1 Spirit", "A2 Hercules Starlifter", "Ares Inferno", "Ares Ion", "C1 Spirit", "C2 Hercules Starlifter", "Intrepid",
            "M2 Hercules Starlifter", "Mercury Star Runner"]),
        ("Drake", ["Buccaneer", "Caterpillar", "Clipper", "Corsair", "Cutlass Black", "Cutlass Blue", "Cutlass Red", "Cutlass Steel", "Cutter",
            "Cutter Rambler", "Cutter Scout", "Dragonfly", "Golem", "Herald", "Mule", "Vulture"]),
        ("Esperia", ["Blade", "Glaive", "Prowler", "Prowler Utility", "Talon", "Talon Shrike"]),
        ("Gatac", ["Railen", "Syulen"]),
        ("Greycat", ["MDC", "MTC", "PTV", "ROC", "ROC-DS", "STV"]),
        ("Kruger", ["L-21 Wolf", "L-22 Alpha Wolf", "P-52 Merlin", "P-72 Archimedes"]),
        ("MISC", ["Fortune", "Freelancer", "Freelancer DUR", "Freelancer MAX", "Freelancer MIS", "Hull A", "Hull C", "Prospector", "Razor",
            "Razor EX", "Razor LX", "Reliant Kore", "Reliant Mako", "Reliant Sen", "Reliant Tana", "Starfarer", "Starfarer Gemini",
            "Starlancer MAX", "Starlancer TAC"]),
        ("Mirai", ["Fury", "Fury LX", "Fury MX", "Guardian", "Guardian MX", "Guardian QI", "Pulse", "Pulse LX"]),
        ("Origin", ["100i", "125a", "135c", "300i", "315p", "325a", "350r", "400i", "600i Explorer", "600i Touring", "85X", "890 Jump",
            "G12", "G12a", "G12r", "M50", "X1 Base", "X1 Force", "X1 Velocity"]),
        ("RSI", ["Apollo Medivac", "Apollo Triage", "Aurora CL", "Aurora ES", "Aurora LN", "Aurora LX", "Aurora MR", "Constellation Andromeda",
            "Constellation Aquila", "Constellation Phoenix", "Constellation Taurus", "Galaxy", "Lynx", "Mantis", "Meteor", "Perseus", "Polaris",
            "Salvation", "Scorpius", "Scorpius Antares", "Ursa", "Ursa Medivac", "Zeus Mk II CL", "Zeus Mk II ES", "Zeus Mk II MR"]),
        ("Tumbril", ["Cyclone", "Cyclone AA", "Cyclone MT", "Cyclone RC", "Cyclone RN", "Cyclone TR", "Nova", "Storm", "Storm AA"])
    ];

    /// <summary>The ship of the profile a fresh install creates.</summary>
    public const string DefaultShip = "Starter Ship";

    public static readonly IReadOnlyList<string> Names = Fleet
        .SelectMany(f => f.Models.Select(model => $"{f.Maker} {model}"))
        .ToList();

    // Longest first, so a two-word maker is matched whole.
    private static readonly string[] Makers = Fleet.Select(f => f.Maker).OrderByDescending(m => m.Length).ToArray();

    /// <summary>The maker that starts the ship name, or null for a name the list does not know.</summary>
    public static string? MakerOf(string? shipName)
    {
        var name = (shipName ?? string.Empty).Trim();
        return Makers.FirstOrDefault(m => name.Length > m.Length
            && name.StartsWith(m, StringComparison.OrdinalIgnoreCase)
            && name[m.Length] == ' ');
    }

    /// <summary>Theme for the manufacturer that prefixes the ship name; anything else is Neutral.</summary>
    public static ThemeId ThemeFor(string? shipName)
    {
        var maker = MakerOf(shipName) ?? (shipName ?? string.Empty).Trim().Split(' ', 2)[0];
        return ThemeService.TryParseTheme(maker, out var theme) ? theme : ThemeId.Neutral;
    }

    /// <summary>What the copilot calls the ship: the model without its maker ("Gatac Syulen" is "Syulen").</summary>
    public static string Callsign(string? shipName)
    {
        var name = (shipName ?? string.Empty).Trim();

        // The placeholder of a fresh install is not a ship anyone would call by name.
        if (name.Equals(DefaultShip, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return MakerOf(name) is { } maker ? name[(maker.Length + 1)..].Trim() : name;
    }
}
