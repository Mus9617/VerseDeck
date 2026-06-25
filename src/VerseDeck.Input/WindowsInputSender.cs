using System.Runtime.InteropServices;
using VerseDeck.Core.Models;

namespace VerseDeck.Input;

public sealed class WindowsInputSender : IInputSender
{
    public Task SendAsync(KeyPressAction action, CancellationToken cancellationToken = default)
    {
        action.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var inputs = new List<INPUT>();
        foreach (var modifier in action.Modifiers)
        {
            inputs.Add(KeyInput(modifier, false));
        }

        inputs.Add(KeyInput(action.Key, false));
        inputs.Add(KeyInput(action.Key, true));

        foreach (var modifier in action.Modifiers.Reverse())
        {
            inputs.Add(KeyInput(modifier, true));
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), INPUT.Size);
        if (sent != inputs.Count)
        {
            throw new InvalidOperationException($"Windows SendInput did not accept the full key press. Win32 error: {Marshal.GetLastWin32Error()}.");
        }

        return Task.Delay(Math.Min(action.PressDurationMs, KeyPressAction.MaxPressDurationMs), cancellationToken);
    }

    private static INPUT KeyInput(string key, bool keyUp)
    {
        var virtualKey = KeyMap.ToVirtualKey(key);
        return new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? 0x0002u : 0u
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct INPUT
    {
        public const int Size = 40;

        [FieldOffset(0)]
        public uint type;

        // On x64 the native INPUT union starts at offset 8. Using explicit layout keeps
        // SendInput compatible with both x86 and x64 instead of relying on managed padding.
        [FieldOffset(8)]
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}
