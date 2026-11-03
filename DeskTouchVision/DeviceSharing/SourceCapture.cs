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
    private readonly IInputSession _session;
    private readonly bool _preserveRawInput;
    private readonly AuthorityRequestTracker _claims = new();
    private int _activeClaim;
    private long _claimStarted;
    private readonly Win.HookProc _keyboardDelegate;
    private readonly Win.HookProc _mouseDelegate;
    private readonly Action<string> _status;
    private MonitorTopology _localMonitors, _remoteMonitors;
    private System.Drawing.Rectangle _local;
    private MonitorEdge _edge;
    private IntPtr _keyHook, _mouseHook;
    private int _armed, _active, _switchPending, _pending;
    private System.Drawing.Point _pendingPoint;
    private int _remoteX, _remoteY;
    private int _anchorX, _anchorY;
    private long _cooldownUntil;
    private long _lastRawMs;
    private System.Drawing.Point _lastPhysicalPointer;
    private bool _hasLastPhysicalPointer;
    private readonly SlowEdgeApproach _slowEdge = new();
    private System.Windows.Forms.Timer? _slowEdgeTimer;
    private bool _controlPressed, _altPressed, _shiftPressed;

    internal bool RemoteActive => Volatile.Read(ref _active) != 0;
    internal bool IsClaimPending => _claims.Pending != 0;
    internal bool IsArmed => Volatile.Read(ref _armed) != 0;

    internal SourceCapture(Control owner, IInputSession session, Action<string> status,
        MonitorEdge exitEdge = MonitorEdge.Right, bool preserveRawInput = false)
    {
        _owner = owner;
        _preserveRawInput = preserveRawInput;
        _edge = exitEdge;
        _session = session;
        _status = status;
        _localMonitors = MonitorTopology.Capture();
        _remoteMonitors = session.RemoteMonitors;
        _local = _localMonitors.Bounds;
        var home = _localMonitors.Monitors.FirstOrDefault(m => m.Primary)
            ?? _localMonitors.Monitors[0];
        _anchorX = home.X + home.Width / 2;
        _anchorY = home.Y + home.Height / 2;
        _keyboardDelegate = KeyboardHook;
        _mouseDelegate = MouseHook;
    }

    internal void SetEdge(MonitorEdge side)
    {
        ReturnLocal();
        Interlocked.Exchange(ref _pending, 0);
        _hasLastPhysicalPointer = false;
        _slowEdge.Reset();
        _edge = side;
    }

    internal void UpdateLayouts(MonitorTopology local, MonitorTopology remote)
    {
        // A real Windows monitor change never needs to destroy an authenticated
        // TCP pairing. Release remote input first, then re-map the active
        // screen boundaries against the actual native Windows geometry.
        ReturnLocal();
        Interlocked.Exchange(ref _pending, 0);
        _localMonitors = local;
        _remoteMonitors = remote;
        _local = local.Bounds;
        _hasLastPhysicalPointer = false;
        _slowEdge.Reset();
        var home = local.Monitors.FirstOrDefault(m => m.Primary) ?? local.Monitors[0];
        _anchorX = home.X + home.Width / 2;
        _anchorY = home.Y + home.Height / 2;
        _status("Native Windows monitor layout refreshed. Connection remains paired.");
    }

    private static MonitorEdge Opposite(MonitorEdge edge) => edge switch
    {
        MonitorEdge.Left => MonitorEdge.Right,
        MonitorEdge.Right => MonitorEdge.Left,
        MonitorEdge.Top => MonitorEdge.Bottom,
        MonitorEdge.Bottom => MonitorEdge.Top,
        _ => MonitorEdge.Left
    };

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
        _hasLastPhysicalPointer = true;
        _lastPhysicalPointer = Cursor.Position;
        _slowEdge.Reset();
        _slowEdgeTimer ??= new System.Windows.Forms.Timer { Interval = 55 };
        _slowEdgeTimer.Tick -= SlowEdgeTick;
        _slowEdgeTimer.Tick += SlowEdgeTick;
        _slowEdgeTimer.Start();
        Interlocked.Exchange(ref _armed, 1);
        _status($"Ready. Move to the {_edge.ToString().ToLowerInvariant()} outside edge to control the connected PC.");
    }

    internal void Disarm()
    {
        _slowEdgeTimer?.Stop();
        _slowEdge.Reset();
        ReturnLocal();
        Interlocked.Exchange(ref _pending, 0);
        Interlocked.Exchange(ref _armed, 0);
        if (_keyHook != IntPtr.Zero) { Win.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Win.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        // A PhysicalInputMonitor on the same HWND must continue observing
        // physical input when this capture session ends. Removing the mouse
        // registration would silently disable its native priority protection.
        if (!_preserveRawInput && _owner.IsHandleCreated)
        {
            var remove = new[] { new Win.RAWINPUTDEVICE { UsagePage = 1,
                Usage = 2, Flags = Win.RIDEV_REMOVE, Target = IntPtr.Zero } };
            Win.RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<Win.RAWINPUTDEVICE>());
        }
    }

    internal void ReturnLocal()
    {
        int pendingClaim = _claims.Pending;
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        if (pendingClaim > 0)
            _session.TrySend(new Packet("release", pendingClaim));
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        int priorLease = Interlocked.Exchange(ref _activeClaim, 0);
        if (priorLease > 0)
            _session.TrySend(new Packet("release", priorLease));
        _cooldownUntil = Environment.TickCount64 + 1000;
        // Return to the real Windows screen the cursor left, not an empty
        // rectangle or another physical monitor.
        var nativeEdge = _localMonitors.EntryPoint(
            _edge, _localMonitors.EdgeFraction(_pendingPoint, _edge));
        var interior = _edge switch
        {
            MonitorEdge.Left => new System.Drawing.Point(nativeEdge.X + 72, nativeEdge.Y),
            MonitorEdge.Right => new System.Drawing.Point(nativeEdge.X - 72, nativeEdge.Y),
            MonitorEdge.Top => new System.Drawing.Point(nativeEdge.X, nativeEdge.Y + 72),
            MonitorEdge.Bottom => new System.Drawing.Point(nativeEdge.X, nativeEdge.Y - 72),
            _ => nativeEdge
        };
        var localReturn = _localMonitors.ClampToRealMonitor(interior);
        Win.SetCursorPos(localReturn.X, localReturn.Y);
        _hasLastPhysicalPointer = false;
        _slowEdge.Reset();
        _status("Control returned to this Windows computer.");
    }

    private void EnterRemote(System.Drawing.Point sourcePoint)
    {
        if (!IsArmed || RemoteActive ||
            Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        _slowEdge.Reset();
        _pendingPoint = sourcePoint;
        int request = _claims.Begin();
        _claimStarted = Environment.TickCount64;
        // Never hide or park local pointer until the authenticated target
        // acknowledges THIS exact request. A stale grant cannot switch owners.
        if (!_session.TrySend(new Packet("claim", request)))
        {
            _claims.Cancel();
            Interlocked.Exchange(ref _pending, 0);
            _status("Receiver unavailable; local authority remains active.");
        }
        else _status("Requesting remote control; local keyboard/mouse still work.");
    }

    internal void GrantRemote(int request)
    {
        // Network responses can arrive after cancellation, a native-input
        // override, or a newer claim. Never promote one of those stale grants.
        if (!_claims.Accept(request))
        {
            _session.TrySend(new Packet("release", request));
            return;
        }
        if (Interlocked.Exchange(ref _pending, 0) != 1 ||
            !IsArmed || RemoteActive)
        {
            _session.TrySend(new Packet("release", request));
            return;
        }
        var entry = _remoteMonitors.EntryPoint(Opposite(_edge),
            _localMonitors.EdgeFraction(_pendingPoint, _edge));
        _remoteX = entry.X;
        _remoteY = entry.Y;
        Win.SetCursorPos(_anchorX, _anchorY);
        // Let touchpads without Raw Input fall back to the low-level mouse
        // hook immediately; do not leave their first 650 ms unresponsive.
        _lastRawMs = 0;
        Volatile.Write(ref _activeClaim, request);
        Interlocked.Exchange(ref _active, 1);
        if (!_session.TrySend(new Packet("move", _remoteX, _remoteY)))
            ReturnLocal();
        else _status($"Authority granted for handoff #{request}; remote keyboard/mouse now active until native override or return.");
    }

    internal void ReceiverNativePriority(int lease)
    {
        // Native input on the destination always wins. Ignore notifications
        // for earlier requests to avoid destroying a newer authority lease.
        if (lease > 0 && lease != _activeClaim && lease != _claims.Pending)
            return;
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        ReturnLocal();
        _cooldownUntil = Environment.TickCount64 + 1500;
        _status("Receiver physical keyboard/mouse detected. Automatically restored LOCAL control.");
    }

    internal void ReceiverBusy(int request)
    {
        if (request != _claims.Pending) return;
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        ReturnLocal();
        _cooldownUntil = Environment.TickCount64 + 1500;
        _status("Remote PC is currently being used locally. Native input has priority.");
    }

    private bool Post(Action action)
    {
        try
        {
            if (_owner.IsHandleCreated && !_owner.IsDisposed)
            {
                _owner.BeginInvoke(action);
                return true;
            }
        }
        catch (InvalidOperationException) { }
        return false;
    }

    private void SlowEdgeTick(object? sender, EventArgs args)
    {
        // A disconnected or lagging target must not leave "pending" stuck
        // forever while no machine actually owns the handoff.
        if (_claims.Pending != 0 &&
            Environment.TickCount64 - _claimStarted > 3500)
        {
            ReturnLocal();
            _cooldownUntil = Environment.TickCount64 + 1200;
            _status("Handoff acknowledgement timed out; local authority retained.");
        }
        if (!IsArmed || RemoteActive || Volatile.Read(ref _pending) != 0 ||
            Environment.TickCount64 < _cooldownUntil) return;
        var cursor = Cursor.Position;
        if (_slowEdge.Ready(_localMonitors, cursor, _edge,
            Environment.TickCount64))
            RequestAtOuterEdge(cursor, "slow edge approach");
    }

    private void ObserveEdgeMotion(System.Drawing.Point cursor, int dx, int dy)
    {
        if (IsArmed && !RemoteActive)
            _slowEdge.Observe(_localMonitors, cursor, _edge,
                dx, dy, Environment.TickCount64);
    }

    private void RequestAtOuterEdge(System.Drawing.Point cursor, string source)
    {
        if (!IsArmed || RemoteActive || Volatile.Read(ref _pending) != 0 ||
            Environment.TickCount64 < _cooldownUntil ||
            Interlocked.CompareExchange(ref _switchPending, 1, 0) != 0)
            return;
        if (!Post(() =>
        {
            try
            {
                // The pointer may have moved away before the UI thread gets
                // the queued action. Do not switch based on a stale event.
                if (IsArmed && !RemoteActive &&
                    _localMonitors.IsExternalEdge(Cursor.Position, _edge,
                        EdgeHandoffPolicy.EdgeZonePixels + 2))
                {
                    _status($"Outward {source} detected at {_edge.ToString().ToLowerInvariant()} external Windows screen edge.");
                    EnterRemote(cursor);
                }
            }
            finally { Interlocked.Exchange(ref _switchPending, 0); }
        }))
            Interlocked.Exchange(ref _switchPending, 0);
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
        else if (IsArmed && msg == Win.WM_MOUSEMOVE)
        {
            var cursor = new System.Drawing.Point(mouse.pt.X, mouse.pt.Y);
            int dx = _hasLastPhysicalPointer ? cursor.X - _lastPhysicalPointer.X : 0;
            int dy = _hasLastPhysicalPointer ? cursor.Y - _lastPhysicalPointer.Y : 0;
            _lastPhysicalPointer = cursor;
            _hasLastPhysicalPointer = true;
            // Touchpad fallback: even a single slow outward pixel movement
            // must count toward a deliberate outer-edge approach.
            ObserveEdgeMotion(cursor, dx, dy);
            if (EdgeHandoffPolicy.ShouldRequest(_localMonitors, cursor, _edge, dx, dy))
                RequestAtOuterEdge(cursor, "mouse-hook movement");
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
        // Track physical modifiers locally because remote mode suppresses the
        // usual Windows key-state transitions; GetAsyncKeyState is not reliable.
        if (key.vkCode is 0x11 or 0xA2 or 0xA3) _controlPressed = down;
        if (key.vkCode is 0x12 or 0xA4 or 0xA5) _altPressed = down;
        if (key.vkCode is 0x10 or 0xA0 or 0xA1) _shiftPressed = down;
        if (RemoteActive && down && key.vkCode == 0x7B &&
            _controlPressed && _altPressed && _shiftPressed)
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
        if (!IsArmed) return;
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
            if (RemoteActive)
            {
                _lastRawMs = Environment.TickCount64;
                MoveRemote(input.Mouse.LastX, input.Mouse.LastY);
            }
            else
            {
                // Windows may clamp the pointer at the last physical screen
                // pixel. WM_INPUT still reports the mouse pushing outward.
                // This is the missing-edge case that the old LL hook missed.
                var cursor = Cursor.Position;
                ObserveEdgeMotion(cursor, input.Mouse.LastX, input.Mouse.LastY);
                if (EdgeHandoffPolicy.ShouldRequest(_localMonitors, cursor,
                    _edge, input.Mouse.LastX, input.Mouse.LastY))
                    RequestAtOuterEdge(cursor, "raw-mouse movement");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void MoveRemote(int dx, int dy)
    {
        if (!RemoteActive || (dx == 0 && dy == 0)) return;
        int nextX = _remoteX + dx;
        int nextY = _remoteY + dy;
        var monitor = _remoteMonitors.MonitorAt(new System.Drawing.Point(_remoteX, _remoteY));
        var returnEdge = Opposite(_edge);
        if (monitor != null)
        {
            bool leaving = returnEdge switch
            {
                MonitorEdge.Left => dx < 0 && nextX < monitor.X,
                MonitorEdge.Right => dx > 0 && nextX >= monitor.X + monitor.Width,
                MonitorEdge.Top => dy < 0 && nextY < monitor.Y,
                MonitorEdge.Bottom => dy > 0 && nextY >= monitor.Y + monitor.Height,
                _ => false
            };
            var borderPoint = returnEdge switch
            {
                MonitorEdge.Left => new System.Drawing.Point(monitor.X, _remoteY),
                MonitorEdge.Right => new System.Drawing.Point(monitor.X + monitor.Width - 1, _remoteY),
                MonitorEdge.Top => new System.Drawing.Point(_remoteX, monitor.Y),
                MonitorEdge.Bottom => new System.Drawing.Point(_remoteX, monitor.Y + monitor.Height - 1),
                _ => new System.Drawing.Point(_remoteX, _remoteY)
            };
            if (leaving && _remoteMonitors.IsExternalEdge(borderPoint, returnEdge, 1))
            {
                if (Interlocked.CompareExchange(ref _switchPending, 1, 0) == 0 &&
                    !Post(() =>
                    {
                        try { ReturnLocal(); }
                        finally { Interlocked.Exchange(ref _switchPending, 0); }
                    }))
                    Interlocked.Exchange(ref _switchPending, 0);
                return;
            }
        }
        // The OS can have real monitors with gaps / negative screen coordinates.
        // Never inject the pointer into an unoccupied part of the virtual bounding box.
        var mapped = _remoteMonitors.ClampToRealMonitor(
            new System.Drawing.Point(nextX, nextY));
        _remoteX = mapped.X;
        _remoteY = mapped.Y;
        _session.TrySend(new Packet("move", _remoteX, _remoteY));
    }

    public void Dispose()
    {
        Disarm();
        if (_slowEdgeTimer != null)
        {
            _slowEdgeTimer.Tick -= SlowEdgeTick;
            _slowEdgeTimer.Dispose();
            _slowEdgeTimer = null;
        }
    }
}
