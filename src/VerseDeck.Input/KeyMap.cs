namespace VerseDeck.Input;

public static class KeyMap
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12,
        ["LSHIFT"] = 0xA0, ["RSHIFT"] = 0xA1, ["LCTRL"] = 0xA2, ["RCTRL"] = 0xA3, ["LALT"] = 0xA4, ["RALT"] = 0xA5,
        ["SPACE"] = 0x20, ["ENTER"] = 0x0D, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["CAPSLOCK"] = 0x14,
        ["BACKSPACE"] = 0x08, ["DELETE"] = 0x2E, ["INSERT"] = 0x2D, ["HOME"] = 0x24, ["END"] = 0x23,
        ["PGUP"] = 0x21, ["PGDN"] = 0x22,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F,
        ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
        ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
        ["NP_0"] = 0x60, ["NP_1"] = 0x61, ["NP_2"] = 0x62, ["NP_3"] = 0x63, ["NP_4"] = 0x64,
        ["NP_5"] = 0x65, ["NP_6"] = 0x66, ["NP_7"] = 0x67, ["NP_8"] = 0x68, ["NP_9"] = 0x69,
        ["NP_MULTIPLY"] = 0x6A, ["NP_ADD"] = 0x6B, ["NP_SUBTRACT"] = 0x6D, ["NP_PERIOD"] = 0x6E, ["NP_DIVIDE"] = 0x6F,
        ["MOUSE_LEFT"] = 0x01, ["MOUSE_RIGHT"] = 0x02, ["MOUSE_MIDDLE"] = 0x04,
        ["MOUSE_X1"] = 0x05, ["MOUSE_X2"] = 0x06
    };

    // Punctuation is named after its position on a US keyboard, which is how the game names it.
    // Sending the physical scan code presses the same position on any layout.
    private static readonly Dictionary<string, ushort> FixedScanCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MINUS"] = 0x0C, ["EQUALS"] = 0x0D, ["LBRACKET"] = 0x1A, ["RBRACKET"] = 0x1B,
        ["SEMICOLON"] = 0x27, ["APOSTROPHE"] = 0x28, ["GRAVE"] = 0x29, ["BACKSLASH"] = 0x2B,
        ["COMMA"] = 0x33, ["PERIOD"] = 0x34, ["SLASH"] = 0x35
    };

    public static bool TryGetFixedScanCode(string key, out ushort scanCode)
    {
        return FixedScanCodes.TryGetValue((key ?? string.Empty).Trim(), out scanCode);
    }

    public static bool IsSupported(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            ToVirtualKey(key);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Virtual key for a key name, or 0 for keys that are sent by fixed scan code.</summary>
    public static ushort ToVirtualKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        }

        var normalized = key.Trim();
        if (normalized.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(normalized[3..], System.Globalization.NumberStyles.HexNumber, null, out var virtualKey))
        {
            return virtualKey;
        }

        if (NamedKeys.TryGetValue(normalized, out var named))
        {
            return named;
        }

        if (FixedScanCodes.ContainsKey(normalized))
        {
            return 0;
        }

        if (normalized.Length == 1)
        {
            var c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        throw new InvalidOperationException($"Unsupported key '{key}'. Use letters, digits, F1-F24, arrows, or common keys.");
    }

    public static ushort ToVirtualKeyCode(string key) => ToVirtualKey(key);
}
