namespace VerseDeck.Input;

public static class KeyMap
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["SHIFT"] = 0x10, ["ALT"] = 0x12,
        ["SPACE"] = 0x20, ["ENTER"] = 0x0D, ["TAB"] = 0x09, ["ESC"] = 0x1B,
        ["BACKSPACE"] = 0x08, ["DELETE"] = 0x2E, ["INSERT"] = 0x2D, ["HOME"] = 0x24, ["END"] = 0x23,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F,
        ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
        ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
        ["MOUSE_LEFT"] = 0x01, ["MOUSE_RIGHT"] = 0x02, ["MOUSE_MIDDLE"] = 0x04,
        ["MOUSE_X1"] = 0x05, ["MOUSE_X2"] = 0x06
    };

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

        if (normalized.Length == 1)
        {
            var c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        throw new InvalidOperationException($"Unsupported key '{key}'. Use letters, digits, F1-F12, arrows, or common keys.");
    }

    public static ushort ToVirtualKeyCode(string key) => ToVirtualKey(key);
}
