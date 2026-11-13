using System.Runtime.InteropServices;

namespace DeskTouch.DeviceShare;

internal static class Win
{
    internal const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, WM_INPUT = 0x00FF, RID_INPUT = 0x10000003;
    internal const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    internal const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    internal const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    internal const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    internal const int RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    internal const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 16;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 32, MOUSEEVENTF_MIDDLEUP = 64;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4, KEYEVENTF_SCANCODE = 8;
    internal const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MSLLHOOKSTRUCT
    { public POINT pt; public uint mouseData, flags, time; public IntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public IntPtr extraInfo; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hModule, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    internal static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vkCode);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(
        uint action, uint param, out uint value, uint flags);
    internal static int CurrentMouseSpeed()
    {
        const uint SPI_GETMOUSESPEED = 0x0070;
        return SystemParametersInfo(SPI_GETMOUSESPEED, 0, out var speed, 0)
            ? (int)Math.Clamp(speed, 1u, 20u) : 10;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWMOUSE
    {
        public ushort Flags, Pad;
        public uint Buttons, RawButtons;
        public int LastX, LastY;
        public uint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUT { public RAWINPUTHEADER Header; public RAWMOUSE Mouse; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint Type; public INPUTUNION Data; }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] INPUT[] input, int size);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMs);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMs);

    private static readonly object HighResTimerGate = new();
    private static int _highResTimerRefs;

    /// <summary>
    /// Lowers the Windows timer tick from 15.6 ms to 1 ms while an active sharing
    /// session is connected so move pacing and keepalives avoid multi-millisecond stalls.
    /// </summary>
    internal static IDisposable EnterHighResolutionTimer()
    {
        lock (HighResTimerGate)
        {
            if (++_highResTimerRefs == 1)
            {
                try { _ = timeBeginPeriod(1); } catch { }
            }
        }
        return new HighResolutionTimerScope();
    }

    private sealed class HighResolutionTimerScope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (HighResTimerGate)
            {
                if (_highResTimerRefs > 0 && --_highResTimerRefs == 0)
                {
                    try { _ = timeEndPeriod(1); } catch { }
                }
            }
        }
    }
}

internal sealed class InputInjector
{
    private readonly HashSet<(ushort Scan, ushort Vk, bool Extended)> _keys = [];
    private readonly HashSet<int> _buttons = [];
    private static bool Inject(Win.INPUT input)
    {
        return Win.SendInput(1, [input], Marshal.SizeOf<Win.INPUT>()) == 1;
    }

    private static bool InjectAbsolutePosition(int x, int y)
    {
        // SendInput marks synthetic pointer movement as injected, so local
        // physical-activity monitoring can distinguish actual touchpad motion.
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        var point = new System.Drawing.Point(x, y);
        if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(point)))
            return false;
        long dx = v.Width > 1 ? (long)(x - v.Left) * 65535L / (v.Width - 1) : 0;
        long dy = v.Height > 1 ? (long)(y - v.Top) * 65535L / (v.Height - 1) : 0;
        return Inject(new Win.INPUT
        {
            Type = Win.INPUT_MOUSE,
            Data = new Win.INPUTUNION { Mouse = new Win.MOUSEINPUT {
                X = (int)Math.Clamp(dx, 0, 65535),
                Y = (int)Math.Clamp(dy, 0, 65535),
                Flags = Win.MOUSEEVENTF_MOVE | Win.MOUSEEVENTF_ABSOLUTE | Win.MOUSEEVENTF_VIRTUALDESK } }
        });
    }

    internal bool Handle(Packet p)
    {
        switch (p.Type)
        {
            case "move":
                return InjectAbsolutePosition(p.A, p.B);
            case "key":
            {
                if (p.A is < 0 or > 255 || p.D is < 0 or > 255) return false;
                bool down = p.C != 0;
                ushort scan = (ushort)p.A, vk = (ushort)p.D;
                bool extended = p.B != 0;
                uint flags = (scan != 0 ? Win.KEYEVENTF_SCANCODE : 0) |
                             (extended ? Win.KEYEVENTF_EXTENDEDKEY : 0) |
                             (!down ? Win.KEYEVENTF_KEYUP : 0);
                var e = new Win.INPUT { Type = Win.INPUT_KEYBOARD,
                    Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT {
                        Scan = scan, Vk = scan != 0 ? (ushort)0 : vk, Flags = flags } } };
                bool ok = Inject(e);
                // A small number of remapped keyboards/remote keyboard
                // layouts reject scan-code injection even when VK is valid.
                // Retry as virtual-key input only when SendInput explicitly
                // fails. Never mark a key as held if both attempts fail.
                if (!ok && vk != 0)
                {
                    uint fallbackFlags = (extended ? Win.KEYEVENTF_EXTENDEDKEY : 0) |
                        (!down ? Win.KEYEVENTF_KEYUP : 0);
                    ok = Inject(new Win.INPUT { Type = Win.INPUT_KEYBOARD,
                        Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT {
                            Vk = vk, Scan = 0, Flags = fallbackFlags } } });
                }
                if (ok)
                {
                    var id = (scan, vk, extended);
                    if (down) _keys.Add(id); else _keys.Remove(id);
                }
                return ok;
            }
            case "text":
            {
                // Android input methods commit Unicode text, not physical key events.
                // Input is delivered only while an authenticated remote lease is active.
                if (p.Text is not { Length: > 0 and <= 512 } value ||
                    value.Any(c => c == '\0' ||
                        (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
                    return false;
                foreach (char codeUnit in value)
                {
                    var key = new Win.INPUT { Type = Win.INPUT_KEYBOARD,
                        Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT {
                            Vk = 0, Scan = codeUnit, Flags = Win.KEYEVENTF_UNICODE } } };
                    if (!Inject(key)) return false;
                    key.Data.Key.Flags = Win.KEYEVENTF_UNICODE | Win.KEYEVENTF_KEYUP;
                    if (!Inject(key)) return false;
                }
                return true;
            }
            case "button":
            {
                uint flag = (p.A, p.B) switch {
                    (1, 1) => Win.MOUSEEVENTF_LEFTDOWN,
                    (1, 0) => Win.MOUSEEVENTF_LEFTUP,
                    (2, 1) => Win.MOUSEEVENTF_RIGHTDOWN,
                    (2, 0) => Win.MOUSEEVENTF_RIGHTUP,
                    (3, 1) => Win.MOUSEEVENTF_MIDDLEDOWN,
                    (3, 0) => Win.MOUSEEVENTF_MIDDLEUP,
                    _ => 0u };
                if (flag == 0) return false;
                bool ok = Inject(new Win.INPUT { Type = Win.INPUT_MOUSE,
                    Data = new Win.INPUTUNION { Mouse = new Win.MOUSEINPUT { Flags = flag } } });
                if (ok) { if (p.B != 0) _buttons.Add(p.A); else _buttons.Remove(p.A); }
                return ok;
            }
            case "wheel":
                return Inject(new Win.INPUT { Type = Win.INPUT_MOUSE,
                    Data = new Win.INPUTUNION { Mouse = new Win.MOUSEINPUT {
                        Flags = p.B != 0 ? Win.MOUSEEVENTF_HWHEEL : Win.MOUSEEVENTF_WHEEL,
                        Data = unchecked((uint)p.A) } } });
            case "ping": return true;
            default: return false;
        }
    }

    internal void ReleaseAll()
    {
        foreach (var (scan, vk, ext) in _keys.ToArray())
            Handle(new Packet("key", scan, ext ? 1 : 0, 0, vk));
        foreach (int button in _buttons.ToArray()) Handle(new Packet("button", button, 0));
        _keys.Clear();
        _buttons.Clear();
    }
}
