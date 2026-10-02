using VerseDeck.Core.Models;

namespace VerseDeck.App.Services;

/// <summary>Fixed choices for a module and how its stored accent colour maps to a frame.</summary>
public static class ModuleStyle
{
    public static readonly IReadOnlyList<string> Categories = ["Flight", "Navigation", "Scan", "Combat", "Utility", "Emergency", "Systems", "Custom"];
    public static readonly IReadOnlyList<string> Icons = ["power", "engine", "gear", "flight", "quantum", "map", "radar", "scan", "shield", "weapons", "cargo", "comms", "doors", "eject", "warning", "lights"];
    public static readonly IReadOnlyList<string> Frames = ["Cyan", "Green", "Red"];

    private const string CyanAccent = "#49E7FF";
    private const string GreenAccent = "#31F6A5";
    private const string RedAccent = "#FF4C58";
    private static readonly string[] Reds = [RedAccent, "#FF2E2E"];
    private static readonly string[] Greens = [GreenAccent, "#2EF6D1", "#54D6A7", "#A7C957"];

    public static int CategoryOrder(string category)
    {
        for (var i = 0; i < Categories.Count; i++)
        {
            if (Categories[i].Equals(category, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    public static string FrameFor(DeckButton button)
    {
        if (Is(button.AccentColor, CyanAccent))
        {
            return "Cyan";
        }

        if (Reds.Any(c => Is(button.AccentColor, c)) || button.Name is "Eject" or "Self Destruct")
        {
            return "Red";
        }

        if (Greens.Any(c => Is(button.AccentColor, c)))
        {
            return "Green";
        }

        // Preset accents that are not one of the three frames fall back to the category.
        return button.Category switch
        {
            "Scan" or "Utility" => "Green",
            "Combat" or "Emergency" => "Red",
            _ => "Cyan"
        };
    }

    public static string AccentForFrame(string frame) => frame.Trim().ToLowerInvariant() switch
    {
        "green" => GreenAccent,
        "red" => RedAccent,
        _ => CyanAccent
    };

    public static string AccentKeyFor(string frame) => frame switch
    {
        "Red" => "Danger",
        "Green" => "Positive",
        _ => "Accent"
    };

    public static string IconKeyFor(DeckButton button)
    {
        return Icons.Contains(button.Icon) ? button.Icon : "power";
    }

    private static bool Is(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
