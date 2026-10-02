using VerseDeck.Core.Models;

namespace VerseDeck.Input;

public readonly record struct KeyStroke(ushort VirtualKey, ushort ScanCode, bool Extended, bool KeyUp);

public static class KeyInputBuilder
{
    // Keys that share a scan code with the numeric keypad and need the extended flag.
    private static readonly HashSet<ushort> ExtendedKeys = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F, 0xA3, 0xA5];

    public static IReadOnlyList<KeyStroke> Down(KeyPressAction action, Func<ushort, ushort> scanCodeOf)
    {
        return action.Modifiers
            .Append(action.Key)
            .Select(key => Stroke(key, false, scanCodeOf))
            .ToList();
    }

    public static IReadOnlyList<KeyStroke> Up(KeyPressAction action, Func<ushort, ushort> scanCodeOf)
    {
        return action.Modifiers
            .Reverse()
            .Prepend(action.Key)
            .Select(key => Stroke(key, true, scanCodeOf))
            .ToList();
    }

    private static KeyStroke Stroke(string key, bool keyUp, Func<ushort, ushort> scanCodeOf)
    {
        if (KeyMap.TryGetFixedScanCode(key, out var fixedScanCode))
        {
            return new KeyStroke(0, fixedScanCode, false, keyUp);
        }

        var virtualKey = KeyMap.ToVirtualKey(key);
        return new KeyStroke(virtualKey, scanCodeOf(virtualKey), ExtendedKeys.Contains(virtualKey), keyUp);
    }
}
