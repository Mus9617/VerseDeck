using System.Text.RegularExpressions;
using VerseDeck.Core.Models;
using VerseDeck.Input;

namespace VerseDeck.Game;

public enum ScDevice { Keyboard, Mouse, Joystick, Gamepad, Unknown }

/// <summary>One input string from actionmaps.xml, such as "kb1_lctrl+n" or "js1_button3".</summary>
public sealed partial record ScInput(ScDevice Device, int Instance, IReadOnlyList<string> Modifiers, string Key, bool IsUnbound, string Raw)
{
    // Game key names that differ from the names KeyMap uses; everything else matches once upper-cased.
    private static readonly Dictionary<string, string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lalt"] = "LAlt", ["ralt"] = "RAlt", ["lctrl"] = "LCtrl", ["rctrl"] = "RCtrl",
        ["lshift"] = "LShift", ["rshift"] = "RShift", ["escape"] = "ESC"
    };

    public static ScInput Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var match = InputPattern().Match(text);
        if (!match.Success)
        {
            return new ScInput(ScDevice.Unknown, 0, [], string.Empty, text.Length == 0, raw ?? string.Empty);
        }

        var device = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "kb" => ScDevice.Keyboard,
            "mo" => ScDevice.Mouse,
            "js" => ScDevice.Joystick,
            "gp" => ScDevice.Gamepad,
            _ => ScDevice.Unknown
        };
        var instance = int.Parse(match.Groups[2].Value);
        var rest = match.Groups[3].Value.Trim();
        if (rest.Length == 0)
        {
            return new ScInput(device, instance, [], string.Empty, true, raw!);
        }

        // In "lctrl+n" the last element is the key and the ones before it are held with it.
        var parts = rest.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = parts[^1];
        if (device == ScDevice.Keyboard && MousePattern().IsMatch(key))
        {
            device = ScDevice.Mouse;
        }

        return new ScInput(device, instance, parts[..^1].Select(Translate).ToList(), Translate(key), false, raw!);
    }

    /// <summary>Converts to a press VerseDeck can send: keyboard only, known keys, real modifiers.</summary>
    public bool TryToKeyPress(int pressDurationMs, out KeyPressAction action)
    {
        action = KeyPressAction.DefaultLandingGear;
        if (Device != ScDevice.Keyboard || IsUnbound || !KeyMap.IsSupported(Key))
        {
            return false;
        }

        var candidate = new KeyPressAction(Key, Modifiers, pressDurationMs);
        try
        {
            candidate.Validate();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        action = candidate;
        return true;
    }

    private static string Translate(string name)
    {
        return KeyNames.TryGetValue(name, out var mapped) ? mapped : name.ToUpperInvariant();
    }

    [GeneratedRegex(@"^(kb|mo|js|gp)(\d+)_(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"^(mouse\d+|mwheel_\w+|maxis_\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MousePattern();
}
