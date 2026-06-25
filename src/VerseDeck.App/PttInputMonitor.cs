using System.Runtime.InteropServices;
using System.Windows.Threading;
using VerseDeck.Input;

namespace VerseDeck.App;

public sealed class PttInputMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new();
    private bool _isPressed;
    private bool _observedPressed;
    private DateTimeOffset _observedChangedAt = DateTimeOffset.Now;
    private static readonly TimeSpan StableInputDuration = TimeSpan.FromMilliseconds(130);

    public PttInputMonitor()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(45);
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler<bool>? PressedChanged;
    public string DeviceType { get; private set; } = "Keyboard";
    public string Binding { get; private set; } = "F13";
    public bool IsRunning => _timer.IsEnabled;

    public void Start(string deviceType, string binding)
    {
        DeviceType = string.IsNullOrWhiteSpace(deviceType) ? "Keyboard" : deviceType.Trim();
        Binding = string.IsNullOrWhiteSpace(binding) ? "F13" : binding.Trim();
        _isPressed = false;
        _observedPressed = false;
        _observedChangedAt = DateTimeOffset.Now;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        SetPressed(false);
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        try
        {
            var pressed = DeviceType.ToUpperInvariant() switch
            {
                "MOUSE" => IsKeyboardOrMousePressed(Binding),
                "GAMEPAD" => IsGamepadPressed(Binding),
                "JOYSTICK" => IsJoystickPressed(Binding),
                _ => IsKeyboardOrMousePressed(Binding)
            };

            SetObservedPressed(pressed);
        }
        catch
        {
            SetObservedPressed(false);
        }
    }

    private void SetObservedPressed(bool pressed)
    {
        if (_observedPressed != pressed)
        {
            _observedPressed = pressed;
            _observedChangedAt = DateTimeOffset.Now;
            return;
        }

        if (DateTimeOffset.Now - _observedChangedAt >= StableInputDuration)
        {
            SetPressed(pressed);
        }
    }

    private void SetPressed(bool pressed)
    {
        if (_isPressed == pressed)
        {
            return;
        }

        _isPressed = pressed;
        PressedChanged?.Invoke(this, pressed);
    }

    private static bool IsKeyboardOrMousePressed(string binding)
    {
        var vk = KeyMap.ToVirtualKeyCode(binding);
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public static bool AnyInputPressed()
    {
        if (TryDetectPressed(out _))
        {
            return true;
        }

        return false;
    }

    public static bool TryDetectPressed(out PttBinding binding)
    {
        foreach (var mouse in MouseBindings)
        {
            if ((GetAsyncKeyState(mouse.VirtualKey) & 0x8000) != 0)
            {
                binding = new PttBinding("Mouse", mouse.Name);
                return true;
            }
        }

        if (XInputGetState(0, out var gamepadState) == 0)
        {
            foreach (var gamepad in GamepadBindings)
            {
                if ((gamepadState.Gamepad.wButtons & gamepad.Mask) != 0)
                {
                    binding = new PttBinding("Gamepad", gamepad.Name);
                    return true;
                }
            }
        }

        for (var joyIndex = 0; joyIndex < 16; joyIndex++)
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = 0x80 };
            if (joyGetPosEx((uint)joyIndex, ref info) != 0 || info.dwButtons == 0)
            {
                continue;
            }

            for (var buttonIndex = 0; buttonIndex < 32; buttonIndex++)
            {
                if ((info.dwButtons & (1u << buttonIndex)) != 0)
                {
                    binding = new PttBinding("Joystick", $"JOY{joyIndex}:BUTTON{buttonIndex + 1}");
                    return true;
                }
            }
        }

        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (MouseBindings.Any(m => m.VirtualKey == vk))
            {
                continue;
            }

            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                binding = new PttBinding("Keyboard", VirtualKeyName(vk));
                return true;
            }
        }

        binding = default;
        return false;
    }

    private static bool IsGamepadPressed(string binding)
    {
        if (XInputGetState(0, out var state) != 0)
        {
            return false;
        }

        var mask = GamepadBindings.FirstOrDefault(b => b.Name.Equals(binding, StringComparison.OrdinalIgnoreCase)).Mask;

        return mask != 0 && (state.Gamepad.wButtons & mask) != 0;
    }

    private static string VirtualKeyName(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x30 and <= 0x39)
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x6F}";
        }

        return VkNames.GetValueOrDefault(vk, $"VK_{vk:X2}");
    }

    private static readonly IReadOnlyList<(int VirtualKey, string Name)> MouseBindings =
    [
        (0x01, "MOUSE_LEFT"),
        (0x02, "MOUSE_RIGHT"),
        (0x04, "MOUSE_MIDDLE"),
        (0x05, "MOUSE_X1"),
        (0x06, "MOUSE_X2")
    ];

    private static readonly IReadOnlyList<(ushort Mask, string Name)> GamepadBindings =
    [
        (0x1000, "A"),
        (0x2000, "B"),
        (0x4000, "X"),
        (0x8000, "Y"),
        (0x0100, "LB"),
        (0x0200, "RB"),
        (0x0020, "BACK"),
        (0x0010, "START"),
        (0x0040, "LS"),
        (0x0080, "RS"),
        (0x0001, "DPAD_UP"),
        (0x0002, "DPAD_DOWN"),
        (0x0004, "DPAD_LEFT"),
        (0x0008, "DPAD_RIGHT")
    ];

    private static readonly Dictionary<int, string> VkNames = new()
    {
        [0x08] = "BACKSPACE",
        [0x09] = "TAB",
        [0x0D] = "ENTER",
        [0x10] = "SHIFT",
        [0x11] = "CTRL",
        [0x12] = "ALT",
        [0x1B] = "ESC",
        [0x20] = "SPACE",
        [0x23] = "END",
        [0x24] = "HOME",
        [0x25] = "LEFT",
        [0x26] = "UP",
        [0x27] = "RIGHT",
        [0x28] = "DOWN",
        [0x2D] = "INSERT",
        [0x2E] = "DELETE"
    };

    private static bool IsJoystickPressed(string binding)
    {
        var index = 0;
        var buttonText = binding;
        if (binding.Contains(':', StringComparison.Ordinal))
        {
            var parts = binding.Split(':', 2, StringSplitOptions.TrimEntries);
            _ = int.TryParse(parts[0].Replace("JOY", "", StringComparison.OrdinalIgnoreCase), out index);
            buttonText = parts[1];
        }

        var buttonNumber = 1;
        _ = int.TryParse(buttonText.Replace("BUTTON", "", StringComparison.OrdinalIgnoreCase), out buttonNumber);
        var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = 0x80 };
        if (joyGetPosEx((uint)Math.Clamp(index, 0, 15), ref info) != 0)
        {
            return false;
        }

        var mask = 1u << Math.Clamp(buttonNumber - 1, 0, 31);
        return (info.dwButtons & mask) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public uint dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }
}

public readonly record struct PttBinding(string DeviceType, string Binding);
