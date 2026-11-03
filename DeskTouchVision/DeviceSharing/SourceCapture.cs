using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Captures physical Windows input only while the user explicitly arms sharing.
/// Hooks never block for network I/O, and an emergency keyboard chord returns
/// control locally. Source cursor is parked while remote control is active.
/// </summary>
internal sealed class SourceCapture : IDisposable
{
    private readonly Control _owner;
    private readonly HostSession _session;
    private readonly Win.HookProc _keyboardDelegate;
    private readonly Win.HookProc _mouseDelegate;
    private readonly Action<string> _status;
    private readonly System.Drawing.Rectangle _local, _remote;
    private IntPtr _keyHook, _mouseHook;
    private int _armed, _active, _switchPending;
    private int _remoteX, _remoteY;
    private readonly int _anchorX, _anchorY;
    private long _cooldownUntil;
    private long _lastRawMs;

    internal bool RemoteActive => Volatile.Read(ref _active) != 0;
    internal bool IsArmed => Volatile.Read(ref _armed) != 0;

    internal SourceCapture(Control owner, HostSession session, Action<string> status)
    {
        _owner = owner;
        _session = session;
        _status = status;
        _local = SystemInformation.VirtualScreen;
        _remote = session.Screen;
        _anchorX = _local.Left + _local.Width / 2;
        _anchorY = _local.Top + _local.Height / 2;
        _keyboardDelegate = KeyboardHook;
        _mouseDelegate = MouseHook;
    }

    internal void Arm()
    {
        if (_keyHook != IntPtr.Zero) return;
        if (!_owner.IsHandleCreated) throw new InvalidOperationException("Window handle not created.");
        var devices = new[] { new Win.RAWINPUTDEVICE {
            UsagePage = 1, Usage = 2, Flags = Win.RIDEV_INPUTSINK, Target = _owner.Handle } };
        if (!Win.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Win.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Raw Input mouse registration failed: " + Marshal.GetLastWin32Error());

        IntPtr module = Win.GetModuleHandle(null);
        _keyHook = Win.SetWindowsHookEx(Win.WH_KEYBOARD_LL, _keyboardDelegate, module, 0);
        _mouseHook = Win.SetWindowsHookEx(Win.WH_MOUSE_LL, _mouseDelegate, module, 0);
        if (_keyHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Disarm();
            throw new InvalidOperationException("Windows global low-level hooks unavailable.");
        }
        Interlocked.Exchange(ref _armed, 1);
        _status("Armed. Move to the RIGHT edge of this PC to enter the remote Windows screen.");
    }

    internal void Disarm()
    {
        ReturnLocal();
        Interlocked.Exchange(ref _armed, 0);
        if (_keyHook != IntPtr.Zero) { Win.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Win.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_owner.IsHandleCreated)
        {
            var remove = new[] { new Win.RAWINPUTDEVICE { UsagePage = 1,
                Usage = 2, Flags = Win.RIDEV_REMOVE, Target = IntPtr.Zero } };
            Win.RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<Win.RAWINPUTDEVICE>());
        }
    }

    internal void ReturnLocal()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        _session.TrySend(new Packet("reset"));
        _cooldownUntil = Environment.TickCount64 + 1000;
        // Put local cursor inside display, rather than immediately on switching boundary.
        Win.SetCursorPos(_local.Right - Math.Min(100, _local.Width / 4),
            Math.Clamp(_anchorY, _local.Top, _local.Bottom - 1));
        _status("Control returned to this Windows computer.");
    }

    private void EnterRemote(int localY)
    {
        if (!IsArmed || RemoteActive) return;
        _remoteX = _remote.Left + Math.Min(45, _remote.Width / 5);
        double fractionalY = (double)(localY - _local.Top) / Math.Max(1, _local.Height);
        _remoteY = _remote.Top + Math.Clamp((int)(fractionalY * _remote.Height), 0, _remote.Height - 1);
        Win.SetCursorPos(_anchorX, _anchorY);
        _lastRawMs = Environment.TickCount64;
        Interlocked.Exchange(ref _active, 1);
        if (!_session.TrySend(new Packet("move", _remoteX, _remoteY)))
            ReturnLocal();
        else _status("Controlling remote Windows. Return via LEFT screen edge, or Ctrl+Alt+Shift+F12.");
    }

    private void Post(Action action)
    {
        try
        {
            if (_owner.IsHandleCreated && !_owner.IsDisposed)
                _owner.BeginInvoke(action);
        }
        catch (InvalidOperationException) { }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Win.CallNextHookEx(_mouseHook, code, wParam, lParam);
        int msg = unchecked((int)wParam.ToInt64());
        var mouse = Marshal.PtrToStructure<Win.MSLLHOOKSTRUCT>(lParam);
        if ((mouse.flags & 1) != 0) return Win.CallNextHookEx(_mouseHook, code, wParam, lParam);
        if (RemoteActive)
        {
            if (msg == Win.WM_MOUSEMOVE)
            {
                // Some precision touchpads do not provide raw mouse packets.
                // Fallback uses attempted pointer displacement from parked anchor.
                if (Environment.TickCount64 - _lastRawMs > 650)
                    MoveRemote(mouse.pt.X - _anchorX, mouse.pt.Y - _anchorY);
                return (IntPtr)1;
            }
            int button = msg switch {
                Win.WM_LBUTTONDOWN or Win.WM_LBUTTONUP => 1,
                Win.WM_RBUTTONDOWN or Win.WM_RBUTTONUP => 2,
                Win.WM_MBUTTONDOWN or Win.WM_MBUTTONUP => 3,
                _ => 0
            };
            if (button != 0)
            {
                bool down = msg is Win.WM_LBUTTONDOWN or Win.WM_RBUTTONDOWN or Win.WM_MBUTTONDOWN;
                _session.TrySend(new Packet("button", button, down ? 1 : 0));
                return (IntPtr)1;
            }
            if (msg == Win.WM_MOUSEWHEEL || msg == Win.WM_MOUSEHWHEEL)
            {
                int wheel = unchecked((short)(mouse.mouseData >> 16));
                _session.TrySend(new Packet("wheel", wheel, msg == Win.WM_MOUSEHWHEEL ? 1 : 0));
                return (IntPtr)1;
            }
        }
        else if (IsArmed && msg == Win.WM_MOUSEMOVE &&
            Environment.TickCount64 >= _cooldownUntil &&
            mouse.pt.X >= _local.Right - 2 &&
            Interlocked.CompareExchange(ref _switchPending, 1, 0) == 0)
        {
            int y = mouse.pt.Y;
            Post(() => { Interlocked.Exchange(ref _switchPending, 0); EnterRemote(y); });
        }
        return Win.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
        int msg = unchecked((int)wParam.ToInt64());
        if (msg != Win.WM_KEYDOWN && msg != Win.WM_KEYUP &&
            msg != Win.WM_SYSKEYDOWN && msg != Win.WM_SYSKEYUP)
            return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
        var key = Marshal.PtrToStructure<Win.KBDLLHOOKSTRUCT>(lParam);
        if ((key.flags & 0x10) != 0) return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
        bool down = msg is Win.WM_KEYDOWN or Win.WM_SYSKEYDOWN;
        if (RemoteActive && down && key.vkCode == 0x7B &&
            (Win.GetAsyncKeyState(0x11) & 0x8000) != 0 &&
            (Win.GetAsyncKeyState(0x12) & 0x8000) != 0 &&
            (Win.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            Post(ReturnLocal);
            return (IntPtr)1;
        }
        if (RemoteActive)
        {
            bool extended = (key.flags & 1) != 0;
            _session.TrySend(new Packet("key", (int)key.scanCode, extended ? 1 : 0,
                down ? 1 : 0, (int)key.vkCode));
            return (IntPtr)1;
        }
        return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    internal void OnRawInput(IntPtr lParam)
    {
        if (!RemoteActive) return;
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<Win.RAWINPUTHEADER>();
        uint result = Win.GetRawInputData(lParam, Win.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (result == uint.MaxValue || size < Marshal.SizeOf<Win.RAWINPUT>() || size > 4096) return;
        IntPtr memory = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = Win.GetRawInputData(lParam, Win.RID_INPUT, memory, ref size, headerSize);
            if (read == uint.MaxValue) return;
            var input = Marshal.PtrToStructure<Win.RAWINPUT>(memory);
            if (input.Header.Type != 0 || (input.Mouse.Flags & 1) != 0) return;
            _lastRawMs = Environment.TickCount64;
            MoveRemote(input.Mouse.LastX, input.Mouse.LastY);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void MoveRemote(int dx, int dy)
    {
        if (!RemoteActive || (dx == 0 && dy == 0)) return;
        int nextX = _remoteX + dx;
        if (nextX <= _remote.Left)
        {
            if (Interlocked.CompareExchange(ref _switchPending, 1, 0) == 0)
                Post(() => { Interlocked.Exchange(ref _switchPending, 0); ReturnLocal(); });
            return;
        }
        _remoteX = Math.Clamp(nextX, _remote.Left, _remote.Right - 1);
        _remoteY = Math.Clamp(_remoteY + dy, _remote.Top, _remote.Bottom - 1);
        _session.TrySend(new Packet("move", _remoteX, _remoteY));
    }

    public void Dispose() => Disarm();
}
