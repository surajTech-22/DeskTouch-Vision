using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Local devices always win. A claimed remote session is revoked on the first
/// native physical input; when idle again it can be claimed intentionally.
/// No keyboard contents, device identifiers, or input history are retained.
/// </summary>
internal sealed class LocalInputPriority
{
    internal const int IdleBeforeRemoteMilliseconds = 2000;
    private readonly Func<long> _clock;
    private long _lastPhysical;
    private int _remoteGranted;
    internal event Action? PhysicalOverride;

    internal LocalInputPriority(Func<long>? monotonicClock = null)
    {
        _clock = monotonicClock ?? (() => Environment.TickCount64);
        _lastPhysical = _clock();
    }

    internal bool RemoteGranted => Volatile.Read(ref _remoteGranted) == 1;
    internal long MillisecondsSinceNative =>
        Math.Max(0, _clock() - Interlocked.Read(ref _lastPhysical));

    internal bool TryClaim()
    {
        if (MillisecondsSinceNative < IdleBeforeRemoteMilliseconds) return false;
        Interlocked.Exchange(ref _remoteGranted, 1);
        // A concurrent native input event must win even when it happens
        // between the initial idle check and the claim.
        if (MillisecondsSinceNative < IdleBeforeRemoteMilliseconds)
        {
            Interlocked.Exchange(ref _remoteGranted, 0);
            return false;
        }
        return true;
    }

    internal void RecordPhysicalActivity()
    {
        Interlocked.Exchange(ref _lastPhysical, _clock());
        if (Interlocked.Exchange(ref _remoteGranted, 0) == 1)
            PhysicalOverride?.Invoke();
    }

    internal bool CanApplyRemote => RemoteGranted &&
        MillisecondsSinceNative >= IdleBeforeRemoteMilliseconds;

    internal void ReleaseRemote() => Interlocked.Exchange(ref _remoteGranted, 0);
}

/// <summary>
/// Refuse remote authority unless physical-input safety monitoring is active.
/// Independent, testable gate: inactive app discovery never injects input.
/// </summary>
internal static class InputClaimLifecyclePolicy
{
    internal static bool CanGrant(bool physicalMonitoringActive,
        bool reverseRemoteActive, bool reverseClaimPending) =>
        // A captured LOCAL keyboard/mouse is deliberately forwarded OUT
        // to another screen. An independent authenticated peer may control
        // THIS PC simultaneously. A pending handoff is still local input.
        physicalMonitoringActive && !reverseClaimPending;
}

/// <summary>
/// Local hardware keyboard, touchpad and mouse activity, including when this
/// window is in the background. Never blocks/disables native input.
/// LL hooks are an additional detector where touchpad activity does not appear
/// as conventional Raw Input. Synthetic SendInput signals are ignored.
/// </summary>
internal sealed class PhysicalInputMonitor : IDisposable
{
    private readonly Control _window;
    private readonly Win.HookProc _keyboardProc, _mouseProc;
    private IntPtr _keyboardHook, _mouseHook;
    private volatile bool _running;
    internal bool IsRunning => _running;
    internal LocalInputPriority Priority { get; } = new();
    internal Func<bool>? IsOutboundInputCaptured { get; set; }
    internal static bool IsLocallyDirected(bool outboundCaptureActive) =>
        !outboundCaptureActive;
    private void RecordPhysicalIfLocal()
    {
        // When this keyboard is controlling another connected screen,
        // it must NOT revoke an independent remote controller of this
        // Windows PC. Physical ownership still wins whenever the capture
        // is local or has returned via emergency shortcut.
        if(IsLocallyDirected(IsOutboundInputCaptured?.Invoke()==true))
            Priority.RecordPhysicalActivity();
    }

    internal PhysicalInputMonitor(Control window)
    {
        _window = window;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
    }

    internal void Start()
    {
        if (_running) return;
        if (!_window.IsHandleCreated) throw new InvalidOperationException("UI handle unavailable.");
        var devices = new[]
        {
            new Win.RAWINPUTDEVICE { UsagePage = 1, Usage = 2,
                Flags = Win.RIDEV_INPUTSINK, Target = _window.Handle },
            new Win.RAWINPUTDEVICE { UsagePage = 1, Usage = 6,
                Flags = Win.RIDEV_INPUTSINK, Target = _window.Handle }
        };
        if (!Win.RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<Win.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Unable to observe native input: " + Marshal.GetLastWin32Error());

        var module = Win.GetModuleHandle(null);
        _keyboardHook = Win.SetWindowsHookEx(Win.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Win.SetWindowsHookEx(Win.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Unable to set up physical-input hooks.");
        }
        _running = true;
        // Physical input might have occurred while this monitoring session was
        // intentionally idle. Require a fresh 2-second local idle interval
        // before the first remotely granted input lease.
        Priority.RecordPhysicalActivity();
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Win.KBDLLHOOKSTRUCT>(lParam);
            int msg = unchecked((int)wParam.ToInt64());
            if ((key.flags & 0x10) == 0 &&
                msg is Win.WM_KEYDOWN or Win.WM_SYSKEYDOWN)
                RecordPhysicalIfLocal();
        }
        return Win.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var mouse = Marshal.PtrToStructure<Win.MSLLHOOKSTRUCT>(lParam);
            int msg = unchecked((int)wParam.ToInt64());
            // Real touchpad motion may arrive as non-injected mouse moves
            // without Raw Input. Remote pointer movement now uses SendInput,
            // which sets LLMHF_INJECTED and is safely ignored here.
            if ((mouse.flags & 1) == 0 &&
                (msg is Win.WM_MOUSEMOVE or Win.WM_LBUTTONDOWN or Win.WM_RBUTTONDOWN or
                       Win.WM_MBUTTONDOWN or Win.WM_MOUSEWHEEL or Win.WM_MOUSEHWHEEL))
                RecordPhysicalIfLocal();
        }
        return Win.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    internal void HandleRawInput(IntPtr handle)
    {
        if (!_running) return;
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<Win.RAWINPUTHEADER>();
        if (Win.GetRawInputData(handle, Win.RID_INPUT, IntPtr.Zero,
            ref size, headerSize) == uint.MaxValue || size < headerSize || size > 4096)
            return;
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Win.GetRawInputData(handle, Win.RID_INPUT, ptr,
                ref size, headerSize) == uint.MaxValue) return;
            var header = Marshal.PtrToStructure<Win.RAWINPUTHEADER>(ptr);
            if (header.Device == IntPtr.Zero) return; // reject virtual/unidentified source
            if (header.Type == 1) // RIM_TYPEKEYBOARD; no scan codes stored
            {
                RecordPhysicalIfLocal();
            }
            else if (header.Type == 0 && size >= Marshal.SizeOf<Win.RAWINPUT>())
            {
                var mouse = Marshal.PtrToStructure<Win.RAWINPUT>(ptr).Mouse;
                if (mouse.LastX != 0 || mouse.LastY != 0 || mouse.Buttons != 0)
                    RecordPhysicalIfLocal();
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Dispose()
    {
        _running = false;
        Priority.ReleaseRemote();
        if (_keyboardHook != IntPtr.Zero)
        {
            Win.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            Win.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_window.IsHandleCreated)
        {
            var remove = new[]
            {
                new Win.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Win.RIDEV_REMOVE },
                new Win.RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = Win.RIDEV_REMOVE }
            };
            Win.RegisterRawInputDevices(remove, (uint)remove.Length,
                (uint)Marshal.SizeOf<Win.RAWINPUTDEVICE>());
        }
    }
}
