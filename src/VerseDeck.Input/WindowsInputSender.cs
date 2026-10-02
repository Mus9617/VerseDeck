using System.Runtime.InteropServices;
using VerseDeck.Core.Models;

namespace VerseDeck.Input;

public sealed class WindowsInputSender : IInputSender
{
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    private readonly Action<IReadOnlyList<KeyStroke>> _send;
    private readonly Func<ushort, ushort> _scanCodeOf;

    // Presses from the deck, voice and phone must not overlap: a held Alt would change the next key.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsInputSender()
        : this(Send, ScanCodeOf)
    {
    }

    /// <summary>For tests: replaces the Win32 calls so the press sequence can be observed.</summary>
    public WindowsInputSender(Action<IReadOnlyList<KeyStroke>> send, Func<ushort, ushort> scanCodeOf)
    {
        _send = send;
        _scanCodeOf = scanCodeOf;
    }

    public async Task SendAsync(KeyPressAction action, CancellationToken cancellationToken = default)
    {
        action.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        // One press: keys go down, are held for the configured duration, then released.
        var down = KeyInputBuilder.Down(action, _scanCodeOf);
        var up = KeyInputBuilder.Up(action, _scanCodeOf);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                _send(down);
                await Task.Delay(Math.Min(action.PressDurationMs, KeyPressAction.MaxPressDurationMs), cancellationToken);
            }
            finally
            {
                // Runs even if only part of the down batch was accepted, so no key stays held.
                _send(up);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ushort ScanCodeOf(ushort virtualKey) => (ushort)MapVirtualKey(virtualKey, 0);

    private static void Send(IReadOnlyList<KeyStroke> strokes)
    {
        var inputs = strokes.Select(KeyInput).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"Windows SendInput did not accept the full key press. Win32 error: {Marshal.GetLastWin32Error()}.");
        }
    }

    private static INPUT KeyInput(KeyStroke stroke)
    {
        // Games read scan codes; keys without one (mouse buttons, some F13-F24) fall back to the virtual key.
        var useScanCode = stroke.ScanCode != 0;
        var flags = stroke.KeyUp ? KeyEventKeyUp : 0u;
        if (useScanCode)
        {
            flags |= KeyEventScanCode;
        }

        if (stroke.Extended)
        {
            flags |= KeyEventExtendedKey;
        }

        return new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = useScanCode ? (ushort)0 : stroke.VirtualKey,
                    wScan = stroke.ScanCode,
                    dwFlags = flags
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

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
