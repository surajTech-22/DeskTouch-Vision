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
    private IInputSession _session;
    private readonly bool _preserveRawInput;
    private readonly int _mouseSpeed;
    private readonly AuthorityRequestTracker _claims = new();
    private int _activeClaim;
    private int _awaitingAppliedClaim;
    private long _claimStarted;
    private long _claimGrantedAtMs;
    private readonly Win.HookProc _keyboardDelegate;
    private readonly Win.HookProc _mouseDelegate;
    private readonly Action<string> _status;
    private MonitorTopology _localMonitors, _remoteMonitors;
    private System.Drawing.Rectangle _local;
    private MonitorEdge _edge;
    private const uint RawBufferSize = 4096;
    private IntPtr _keyHook, _mouseHook;
    private IntPtr _rawBuffer = Marshal.AllocHGlobal((int)RawBufferSize);
    private int _armed, _active, _switchPending, _pending;
    private System.Drawing.Point _pendingPoint;
    private int _remoteX, _remoteY;
    private int _anchorX, _anchorY;
    private long _cooldownUntil;
    private long _enteredRemoteAt;
    private long _lastRawMs;
    private long _lastFallbackMs;
    private long _lastWarmHintMs;
    private int _hookMovesWithoutRaw;
    private bool _continuingAcrossDevices;
    internal bool OutboundInputReserved => RemoteActive ||
        (_continuingAcrossDevices && IsClaimPending);
    private System.Drawing.Point _lastPhysicalPointer;
    private bool _hasLastPhysicalPointer;
    private readonly SlowEdgeApproach _slowEdge = new();
    private readonly SlowEdgeApproach _alternateSlowEdge = new();
    private readonly SlowEdgeApproach _orthogonalFirst = new();
    private readonly SlowEdgeApproach _orthogonalSecond = new();
    private (MonitorEdge First,MonitorEdge Second) OrthogonalEdges() =>
        _edge is MonitorEdge.Left or MonitorEdge.Right
            ? (MonitorEdge.Top,MonitorEdge.Bottom)
            : (MonitorEdge.Left,MonitorEdge.Right);
    private void ResetEdgeTracking()
    {
        _slowEdge.Reset();
        _alternateSlowEdge.Reset();
        _orthogonalFirst.Reset();
        _orthogonalSecond.Reset();
    }
    // Request a different already authenticated environment member when
    // crossing its actual screen boundary. Never connects another socket.
    internal Func<MonitorEdge,double,bool>? NavigateEnvironment { get; set; }
    internal Func<bool>? DestinationIsAndroid { get; set; }
    internal event Action? ToggleMobileRequested;

    internal static string? AndroidPrintableKey(int vk,bool shift)
    {
        if(vk is >=0x41 and <=0x5A)
            return ((char)(vk+(shift?0:32))).ToString();
        if(vk is >=0x30 and <=0x39)
            return shift ? ")!@#$%^&*("[vk-0x30].ToString()
                : ((char)vk).ToString();
        if(vk is >=0x60 and <=0x69)
            return ((char)('0'+vk-0x60)).ToString();
        if(vk==0x20)return " ";
        return vk switch{
            0xBA=>shift?":":";", 0xBB=>shift?"+":"=",
            0xBC=>shift?"<":",", 0xBD=>shift?"_":"-",
            0xBE=>shift?">":".", 0xBF=>shift?"?":"/",
            0xC0=>shift?"~":"`", 0xDB=>shift?"{":"[",
            0xDC=>shift?"|":"\\", 0xDD=>shift?"}":"]",
            0xDE=>shift?"\"":"'", _=>null
        };
    }

    internal static bool ShouldToggleMobileShortcut(bool down,int vk,bool ctrl,
        bool alt,bool shift) =>
        down && vk == 0x4D && ctrl && alt && !shift;

    private System.Windows.Forms.Timer? _slowEdgeTimer;
    private bool _controlPressed, _altPressed, _shiftPressed;
    private int _remoteModifierDownMask;
    private bool _suppressMobileShortcutUp;
    private bool _directShortcutEntry;
    private double _remoteFracX, _remoteFracY;
    private double _pointerSensitivity = 1.0;
    private int _androidReturnPushPx;

    internal void SetPointerSensitivity(double multiplier) =>
        Volatile.Write(ref _pointerSensitivity, Math.Clamp(multiplier, 0.25, 2.50));

    internal bool RemoteActive => Volatile.Read(ref _active) != 0;
    internal bool IsClaimPending =>
        _claims.Pending != 0 || Volatile.Read(ref _awaitingAppliedClaim) > 0;
    internal bool IsArmed => Volatile.Read(ref _armed) != 0;

    internal SourceCapture(Control owner, IInputSession session, Action<string> status,
        MonitorEdge exitEdge = MonitorEdge.Right, bool preserveRawInput = false)
    {
        _owner = owner;
        _preserveRawInput = preserveRawInput;
        _mouseSpeed = Win.CurrentMouseSpeed();
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
        ResetEdgeTracking();
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
        ResetEdgeTracking();
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

    // One physical keyboard/mouse hook set for the WHOLE environment. A
    // destination change must release the previous session while still
    // bound to it, and then attach the next already-authenticated stream
    // WITHOUT disposing/reattaching global Windows hooks or Raw Input.
    internal void Retarget(IInputSession next, MonitorEdge entryEdge,
        bool continuingRemoteHandoff=false)
    {
        // A native mouse that was already controlling another screen
        // should not revoke an independent inbound controller of this PC
        // during the two-phase transition to a neighboring screen.
        bool continuation=continuingRemoteHandoff || RemoteActive;
        ReturnLocal();
        _continuingAcrossDevices=continuation;
        _session=next;
        _remoteMonitors=next.RemoteMonitors;
        _edge=entryEdge;
        ResetEdgeTracking();
        Interlocked.Exchange(ref _pending,0);
        Interlocked.Exchange(ref _switchPending,0);
        _cooldownUntil=0;
    }

    internal bool SameInputSession(IInputSession session) =>
        ReferenceEquals(_session,session);

    internal MonitorTopology ActiveRemoteMonitors => _remoteMonitors;

    internal void RefreshRemoteGeometry(MonitorTopology newGeometry)
    {
        // A roster/selection metadata event must never silently revoke a
        // fresh input lease after the mouse has already crossed screens.
        // Real monitor geometry changes still clamp the current pointer.
        _remoteMonitors=newGeometry;
        if(RemoteActive)
        {
            var point=_remoteMonitors.ClampToRealMonitor(
                new System.Drawing.Point(_remoteX,_remoteY));
            _remoteX=point.X;_remoteY=point.Y;
        }
    }

    internal void BeginLinkedHandoff(MonitorEdge approach,double fraction)
    {
        if(!IsArmed || RemoteActive || IsClaimPending)return;
        if(_edge!=approach)SetEdge(approach);
        var source=_localMonitors.EntryPoint(approach,
            Math.Clamp(fraction,0d,1d));
        EnterRemote(source, directShortcut: false);
    }

    internal void BeginDirectHandoff()
    {
        if(!IsArmed || RemoteActive || IsClaimPending)return;
        EnterRemote(new System.Drawing.Point(_anchorX, _anchorY), directShortcut: true);
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
        _hasLastPhysicalPointer = true;
        _lastPhysicalPointer = Cursor.Position;
        ResetEdgeTracking();
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
        ResetEdgeTracking();
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

    internal static IReadOnlyList<int> ModifiersForMask(int mask)
    {
        var vks = new List<int>(3);
        if ((mask & 1) != 0) vks.Add(0x11); // Ctrl
        if ((mask & 2) != 0) vks.Add(0x12); // Alt
        if ((mask & 4) != 0) vks.Add(0x10); // Shift
        return vks;
    }

    private void ReleaseHeldRemoteModifiers()
    {
        int mask = Interlocked.Exchange(ref _remoteModifierDownMask, 0);
        foreach (int vk in ModifiersForMask(mask))
            _session.TrySend(new Packet("key", 0, 0, 0, vk));
    }

    internal void ReturnLocal()
    {
        _continuingAcrossDevices=false;
        int pendingClaim = _claims.Pending;
        int awaitingApplied = Interlocked.Exchange(ref _awaitingAppliedClaim, 0);
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        if (pendingClaim > 0)
            _session.TrySend(new Packet("release", pendingClaim));
        if (awaitingApplied > 0)
            _session.TrySend(new Packet("release", awaitingApplied));
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        ReleaseHeldRemoteModifiers();
        int priorLease = Interlocked.Exchange(ref _activeClaim, 0);
        if (priorLease > 0)
            _session.TrySend(new Packet("release", priorLease));
        _cooldownUntil = Environment.TickCount64 + 180;
        _remoteFracX = 0;
        _remoteFracY = 0;
        _androidReturnPushPx = 0;
        // Return to the real Windows screen the cursor left, not an empty
        // rectangle or another physical monitor. When entered via Ctrl+Alt+M,
        // place the cursor safely inside the primary monitor center.
        System.Drawing.Point localReturn;
        if (_directShortcutEntry)
        {
            _directShortcutEntry = false;
            localReturn = _localMonitors.ClampToRealMonitor(
                new System.Drawing.Point(_anchorX, _anchorY));
        }
        else
        {
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
            localReturn = _localMonitors.ClampToRealMonitor(interior);
        }
        Win.SetCursorPos(localReturn.X, localReturn.Y);
        _hasLastPhysicalPointer = false;
        ResetEdgeTracking();
        _status("Control returned to this Windows computer.");
    }

    private void EnterRemote(System.Drawing.Point sourcePoint, bool directShortcut = false)
    {
        if (!IsArmed || RemoteActive ||
            Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        _slowEdge.Reset();
        _directShortcutEntry = directShortcut;
        _pendingPoint = sourcePoint;
        int request = _claims.Begin();
        _claimStarted = Environment.TickCount64;
        _session.TrySend(new Packet("warm"));
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
        if (!_claims.Accept(request) ||
            Interlocked.CompareExchange(ref _pending, 2, 1) != 1 ||
            !IsArmed || RemoteActive)
        {
            // A late grant cannot move the pointer or capture keys. Even if
            // destination has temporarily reserved a lease, release it.
            _session.TrySend(new Packet("release", request));
            return;
        }
        var entryEdge = Opposite(_edge);
        var entry = _remoteMonitors.EntryPoint(entryEdge,
            _localMonitors.EdgeFraction(_pendingPoint, _edge));
        var targetMon = _remoteMonitors.MonitorAt(entry);
        if (_directShortcutEntry && targetMon != null)
        {
            _remoteX = targetMon.X + targetMon.Width / 2;
            _remoteY = targetMon.Y + targetMon.Height / 2;
        }
        else if (targetMon != null)
        {
            int insetX = Math.Min(36, Math.Max(4, targetMon.Width / 8));
            int insetY = Math.Min(36, Math.Max(4, targetMon.Height / 8));
            _remoteX = entryEdge switch
            {
                MonitorEdge.Left => Math.Min(targetMon.X + targetMon.Width - 1, entry.X + insetX),
                MonitorEdge.Right => Math.Max(targetMon.X, entry.X - insetX),
                _ => entry.X
            };
            _remoteY = entryEdge switch
            {
                MonitorEdge.Top => Math.Min(targetMon.Y + targetMon.Height - 1, entry.Y + insetY),
                MonitorEdge.Bottom => Math.Max(targetMon.Y, entry.Y - insetY),
                _ => entry.Y
            };
        }
        else
        {
            _remoteX = entry.X;
            _remoteY = entry.Y;
        }
        Volatile.Write(ref _awaitingAppliedClaim, request);
        long grantedAt = Environment.TickCount64;
        Volatile.Write(ref _claimGrantedAtMs, grantedAt);
        long grantMs = Math.Max(0, grantedAt - Volatile.Read(ref _claimStarted));
        // Two-phase handoff: the receiving machine must actually APPLY the
        // first cursor packet and acknowledge before the source parks its
        // physical cursor and suppresses keyboard/mouse input.
        if (!_session.TrySend(new Packet("move", _remoteX, _remoteY)))
            ReturnLocal();
        else _status($"Destination granted request #{request} ({grantMs}ms); waiting for cursor/authority application acknowledgement.");
    }

    internal void ConfirmRemoteAuthority(int request)
    {
        if (request <= 0 ||
            Interlocked.CompareExchange(ref _awaitingAppliedClaim, 0, request) != request ||
            Interlocked.CompareExchange(ref _pending, 0, 2) != 2 ||
            !IsArmed || RemoteActive)
        {
            _session.TrySend(new Packet("release", request));
            return;
        }
        Volatile.Write(ref _activeClaim, request);
        // Native Windows cursor only moves AFTER remote input was accepted.
        Win.SetCursorPos(_anchorX, _anchorY);
        long now = Environment.TickCount64;
        _enteredRemoteAt = now;
        _lastRawMs = now;
        _lastFallbackMs = 0;
        _hookMovesWithoutRaw = 0;
        _remoteFracX = 0;
        _remoteFracY = 0;
        _androidReturnPushPx = 0;
        Interlocked.Exchange(ref _active, 1);
        _continuingAcrossDevices=false;
        long totalMs = Math.Max(0, now - Volatile.Read(ref _claimStarted));
        long applyMs = Math.Max(0, now - Volatile.Read(ref _claimGrantedAtMs));
        _status($"Authority handoff confirmed #{request}: remote pointer, keyboard and mouse are active ({totalMs}ms total, {applyMs}ms apply).");
    }

    internal void ReceiverNativePriority(int lease)
    {
        // Native input on the destination always wins. Ignore notifications
        // for earlier requests to avoid destroying a newer authority lease.
        if (lease > 0 && lease != _activeClaim &&
            lease != _claims.Pending && lease != _awaitingAppliedClaim)
            return;
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        ReturnLocal();
        _cooldownUntil = Environment.TickCount64 + 450;
        _status("Receiver physical keyboard/mouse detected. Automatically restored LOCAL control.");
    }

    internal void ReceiverBusy(int request)
    {
        if (request != _claims.Pending) return;
        _claims.Cancel();
        Interlocked.Exchange(ref _pending, 0);
        ReturnLocal();
        _cooldownUntil = Environment.TickCount64 + 450;
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
        if (Volatile.Read(ref _pending) != 0 &&
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
        else if (NavigateEnvironment!=null &&
            _alternateSlowEdge.Ready(_localMonitors, cursor,
                Opposite(_edge), Environment.TickCount64))
            RequestAtOuterEdge(cursor, "alternate shared environment edge",
                Opposite(_edge));
        if(NavigateEnvironment!=null)
        {
            var (first,second)=OrthogonalEdges();
            if(_orthogonalFirst.Ready(_localMonitors,cursor,first,
                Environment.TickCount64))
                RequestAtOuterEdge(cursor,"dragged upper/lower device edge",first);
            else if(_orthogonalSecond.Ready(_localMonitors,cursor,second,
                Environment.TickCount64))
                RequestAtOuterEdge(cursor,"dragged upper/lower device edge",second);
        }
    }

    private void ObserveEdgeMotion(System.Drawing.Point cursor, int dx, int dy)
    {
        if (IsArmed && !RemoteActive)
        {
            long now = Environment.TickCount64;
            const int warmEdgeRadiusPx = 260;
            if (now - _lastWarmHintMs >= 35 &&
                (_localMonitors.IsExternalEdge(cursor, _edge, warmEdgeRadiusPx) ||
                 (NavigateEnvironment != null &&
                  (_localMonitors.IsExternalEdge(cursor, Opposite(_edge), warmEdgeRadiusPx) ||
                   _localMonitors.IsExternalEdge(cursor, OrthogonalEdges().First, warmEdgeRadiusPx) ||
                   _localMonitors.IsExternalEdge(cursor, OrthogonalEdges().Second, warmEdgeRadiusPx)))))
            {
                _lastWarmHintMs = now;
                _session.TrySend(new Packet("warm"));
            }
            _slowEdge.Observe(_localMonitors, cursor, _edge,
                dx, dy, now);
            _alternateSlowEdge.Observe(_localMonitors, cursor,
                Opposite(_edge), dx, dy, now);
            var (first,second)=OrthogonalEdges();
            _orthogonalFirst.Observe(_localMonitors,cursor,first,
                dx,dy,now);
            _orthogonalSecond.Observe(_localMonitors,cursor,second,
                dx,dy,now);
        }
    }

    private void RequestAtOuterEdge(System.Drawing.Point cursor, string source,
        MonitorEdge? requestedEdge = null)
    {
        var edge=requestedEdge??_edge;
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
                var liveCursor = Cursor.Position;
                if (IsArmed && !RemoteActive &&
                    _localMonitors.IsExternalEdge(liveCursor, edge,
                        EdgeHandoffPolicy.EdgeZonePixels + 2))
                {
                    double fraction=_localMonitors.EdgeFraction(
                        _localMonitors.IsExternalEdge(liveCursor, edge,
                            EdgeHandoffPolicy.EdgeZonePixels + 2) ? liveCursor : cursor,
                        edge);
                    if(NavigateEnvironment!=null)
                    {
                        if(NavigateEnvironment(edge,fraction))
                            _status($"Outward {source} detected at {edge.ToString().ToLowerInvariant()} external Windows screen edge.");
                    }
                    else if(edge==_edge)
                    {
                        _status($"Outward {source} detected at {edge.ToString().ToLowerInvariant()} external Windows screen edge.");
                        EnterRemote(liveCursor);
                    }
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
                int dx = mouse.pt.X - _anchorX;
                int dy = mouse.pt.Y - _anchorY;
                if (dx != 0 || dy != 0)
                {
                    int missingRaw = ++_hookMovesWithoutRaw;
                    long now = Environment.TickCount64;
                    if (now - _lastRawMs > 60 && missingRaw >= 3)
                    {
                        _lastFallbackMs = now;
                        double sens = Volatile.Read(ref _pointerSensitivity);
                        _remoteFracX += dx * sens;
                        _remoteFracY += dy * sens;
                        int sdx = (int)Math.Truncate(_remoteFracX);
                        int sdy = (int)Math.Truncate(_remoteFracY);
                        _remoteFracX -= sdx;
                        _remoteFracY -= sdy;
                        if (sdx != 0 || sdy != 0)
                            MoveRemote(sdx, sdy);
                    }
                }
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
            else if(NavigateEnvironment!=null &&
                EdgeHandoffPolicy.ShouldRequest(_localMonitors, cursor,
                    Opposite(_edge), dx, dy))
                RequestAtOuterEdge(cursor, "alternate environment movement",
                    Opposite(_edge));
            else if(NavigateEnvironment!=null)
            {
                var (first,second)=OrthogonalEdges();
                if(EdgeHandoffPolicy.ShouldRequest(_localMonitors,cursor,
                    first,dx,dy))
                    RequestAtOuterEdge(cursor,"draggable screen movement",first);
                else if(EdgeHandoffPolicy.ShouldRequest(_localMonitors,cursor,
                    second,dx,dy))
                    RequestAtOuterEdge(cursor,"draggable screen movement",second);
            }
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
        if (IsArmed && ShouldToggleMobileShortcut(down, (int)key.vkCode,
            _controlPressed, _altPressed, _shiftPressed))
        {
            _suppressMobileShortcutUp = true;
            Post(() => ToggleMobileRequested?.Invoke());
            return (IntPtr)1;
        }
        if (!down && _suppressMobileShortcutUp && key.vkCode == 0x4D)
        {
            _suppressMobileShortcutUp = false;
            return (IntPtr)1;
        }
        if (RemoteActive && down && key.vkCode == 0x7B &&
            _controlPressed && _altPressed && _shiftPressed)
        {
            Post(ReturnLocal);
            return (IntPtr)1;
        }
        if (RemoteActive)
        {
            int modMask = key.vkCode switch
            {
                0x11 or 0xA2 or 0xA3 => 1,
                0x12 or 0xA4 or 0xA5 => 2,
                0x10 or 0xA0 or 0xA1 => 4,
                _ => 0
            };
            if (modMask != 0)
            {
                if (down)
                    _remoteModifierDownMask |= modMask;
                else if ((_remoteModifierDownMask & modMask) == 0)
                    // This modifier was pressed locally before entering remote
                    // (e.g. during Ctrl+Alt+M); let Windows receive its UP event.
                    return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
                else
                    _remoteModifierDownMask &= ~modMask;
            }
            // Android's Accessibility API is an editable-field/IME API, not
            // raw hardware keyboard injection. Send printable characters
            // through the authenticated reverse-text route so Chrome/WebView
            // can commit them using the active Android InputConnection.
            // Modifier, Ctrl/Alt shortcuts, and navigation keys use reverse-key.
            if (DestinationIsAndroid?.Invoke() == true && !_controlPressed && !_altPressed)
            {
                var printable = AndroidPrintableKey((int)key.vkCode, _shiftPressed);
                if (printable != null)
                {
                    if (down && !_session.TrySend(new Packet("text", Text: printable)))
                        Post(ReturnLocal);
                    return (IntPtr)1;
                }
            }
            bool extended = (key.flags & 1) != 0;
            if(!_session.TrySend(new Packet("key",(int)key.scanCode,
                extended?1:0,down?1:0,(int)key.vkCode)))
                Post(ReturnLocal);
            return (IntPtr)1;
        }
        return Win.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    internal void OnRawInput(IntPtr lParam)
    {
        if (!IsArmed) return;
        IntPtr memory = Volatile.Read(ref _rawBuffer);
        if (memory == IntPtr.Zero) return;
        uint headerSize = (uint)Marshal.SizeOf<Win.RAWINPUTHEADER>();
        uint size = RawBufferSize;
        uint read = Win.GetRawInputData(lParam, Win.RID_INPUT, memory, ref size, headerSize);
        if (read == uint.MaxValue || read < (uint)Marshal.SizeOf<Win.RAWINPUT>()) return;
        var input = Marshal.PtrToStructure<Win.RAWINPUT>(memory);
        if (input.Header.Type != 0 || (input.Mouse.Flags & 1) != 0) return;
        if (RemoteActive)
        {
            long now = Environment.TickCount64;
            _lastRawMs = now;
            _hookMovesWithoutRaw = 0;
            // Mouse low-level hook and Raw Input can describe the SAME
            // physical movement when a slow touchpad switches paths.
            // Suppress only the immediately duplicated raw event;
            // alternating input sources must not double movement speed.
            if (!ShouldDropDuplicateRaw(_lastFallbackMs, now))
            {
                bool targetAndroid = DestinationIsAndroid?.Invoke() == true;
                int? dpi = _remoteMonitors.Monitors.FirstOrDefault()?.DensityDpi;
                double sens = Volatile.Read(ref _pointerSensitivity);
                double sx = ScaleRawDeltaPrecise(input.Mouse.LastX, _mouseSpeed, targetAndroid, dpi, sens);
                double sy = ScaleRawDeltaPrecise(input.Mouse.LastY, _mouseSpeed, targetAndroid, dpi, sens);
                _remoteFracX += sx;
                _remoteFracY += sy;
                int dx = (int)Math.Truncate(_remoteFracX);
                int dy = (int)Math.Truncate(_remoteFracY);
                _remoteFracX -= dx;
                _remoteFracY -= dy;
                if (dx != 0 || dy != 0)
                    MoveRemote(dx, dy);
            }
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
            else if(NavigateEnvironment!=null &&
                EdgeHandoffPolicy.ShouldRequest(_localMonitors, cursor,
                    Opposite(_edge),input.Mouse.LastX,input.Mouse.LastY))
                RequestAtOuterEdge(cursor, "raw alternate environment edge",
                    Opposite(_edge));
            else if(NavigateEnvironment!=null)
            {
                var (first,second)=OrthogonalEdges();
                if(EdgeHandoffPolicy.ShouldRequest(_localMonitors,cursor,
                    first,input.Mouse.LastX,input.Mouse.LastY))
                    RequestAtOuterEdge(cursor,"raw dragged device edge",first);
                else if(EdgeHandoffPolicy.ShouldRequest(_localMonitors,cursor,
                    second,input.Mouse.LastX,input.Mouse.LastY))
                    RequestAtOuterEdge(cursor,"raw dragged device edge",second);
            }
        }
    }

    internal static bool ShouldDropDuplicateRaw(long lastHookFallback,long now) =>
        lastHookFallback>0 && now>=lastHookFallback &&
        now-lastHookFallback<=24;

    internal static int ScaleRawDelta(int raw, int systemMouseSpeed)
    {
        // Raw Input omits the OS's pointer sensitivity. Cap extreme device
        // reports while preserving even one slow pixel of physical movement.
        if (raw == 0) return 0;
        int bounded = Math.Clamp(raw, -800, 800);
        double gain = Math.Clamp(systemMouseSpeed, 1, 20) / 10.0 * 1.45;
        int scaled = (int)Math.Round(bounded * gain, MidpointRounding.AwayFromZero);
        return scaled == 0 ? Math.Sign(raw) : scaled;
    }

    internal static double ScaleRawDeltaPrecise(
        int raw, int systemMouseSpeed, bool isAndroid, int? remoteDensityDpi = null,
        double sensitivity = 1.0)
    {
        if (raw == 0) return 0;
        int bounded = Math.Clamp(raw, isAndroid ? -450 : -800, isAndroid ? 450 : 800);
        double speedFactor = Math.Clamp(systemMouseSpeed, 1, 20) / 10.0;
        double sensFactor = Math.Clamp(sensitivity, 0.25, 2.50);
        if (!isAndroid)
            return bounded * speedFactor * 1.45 * sensFactor;
        double dpiFactor = remoteDensityDpi is > 0
            ? Math.Clamp(remoteDensityDpi.Value / 440.0, 0.70, 1.35)
            : 1.0;
        return bounded * speedFactor * 0.95 * dpiFactor * sensFactor;
    }

    private static int EdgeOvershoot(MonitorRect monitor, int nextX, int nextY, MonitorEdge edge) =>
        edge switch
        {
            MonitorEdge.Left => monitor.X - nextX,
            MonitorEdge.Right => nextX - (monitor.X + monitor.Width - 1),
            MonitorEdge.Top => monitor.Y - nextY,
            MonitorEdge.Bottom => nextY - (monitor.Y + monitor.Height - 1),
            _ => 0
        };

    private bool ShouldCrossAndroidReturnEdge(bool isAndroidTarget, bool isReturnEdge,
        MonitorRect monitor, int nextX, int nextY, MonitorEdge edge)
    {
        if (!isAndroidTarget || !isReturnEdge) return true;
        int overshoot = EdgeOvershoot(monitor, nextX, nextY, edge);
        return (_androidReturnPushPx += Math.Max(1, overshoot)) >= 12;
    }

    private void MoveRemote(int dx, int dy)
    {
        if (!RemoteActive || (dx == 0 && dy == 0)) return;
        int nextX = _remoteX + dx;
        int nextY = _remoteY + dy;
        var monitor = _remoteMonitors.MonitorAt(new System.Drawing.Point(_remoteX, _remoteY));
        var returnEdge = Opposite(_edge);
        bool withinEntryGrace = Environment.TickCount64 - _enteredRemoteAt < 260;
        bool isAndroidTarget = DestinationIsAndroid?.Invoke() == true;
        if(monitor!=null && NavigateEnvironment!=null)
        {
            // The inline diagram may have another authenticated screen on
            // either side. Crossing its exterior edge should select that
            // device, not trap the cursor or terminate other TLS sessions.
            bool crossedAny = false;
            foreach(var side in new[]{
                MonitorEdge.Left,MonitorEdge.Right,MonitorEdge.Top,MonitorEdge.Bottom})
            {
                if (withinEntryGrace && side == returnEdge) continue;
                bool outside=side switch{
                    MonitorEdge.Left=>dx<0 && nextX<monitor.X,
                    MonitorEdge.Right=>dx>0 && nextX>=monitor.X+monitor.Width,
                    MonitorEdge.Top=>dy<0 && nextY<monitor.Y,
                    MonitorEdge.Bottom=>dy>0 && nextY>=monitor.Y+monitor.Height,
                    _=>false
                };
                if(!outside)continue;
                var border=side switch{
                    MonitorEdge.Left=>new System.Drawing.Point(monitor.X,_remoteY),
                    MonitorEdge.Right=>new System.Drawing.Point(
                        monitor.X+monitor.Width-1,_remoteY),
                    MonitorEdge.Top=>new System.Drawing.Point(_remoteX,monitor.Y),
                    MonitorEdge.Bottom=>new System.Drawing.Point(
                        _remoteX,monitor.Y+monitor.Height-1),
                    _=>new System.Drawing.Point(_remoteX,_remoteY)
                };
                if(!_remoteMonitors.IsExternalEdge(border,side,1))continue;
                crossedAny = true;
                if (!ShouldCrossAndroidReturnEdge(isAndroidTarget, side == returnEdge,
                    monitor, nextX, nextY, side))
                    break;
                if (side == returnEdge) _androidReturnPushPx = 0;
                double fraction=_remoteMonitors.EdgeFraction(border,side);
                if(Interlocked.CompareExchange(ref _switchPending,1,0)==0)
                {
                    if(!Post(() =>
                    {
                        try
                        {
                            // Invalid/unpaired screen edges are SOLID.
                            // Never teleport back to the hub on an unrelated
                            // right/top/bottom edge of the second computer.
                            NavigateEnvironment?.Invoke(side,fraction);
                        }
                        finally{Interlocked.Exchange(ref _switchPending,0);}
                    }))
                        Interlocked.Exchange(ref _switchPending,0);
                }
                var slid = _remoteMonitors.ClampToRealMonitor(new System.Drawing.Point(nextX, nextY));
                if (slid.X != _remoteX || slid.Y != _remoteY)
                {
                    _remoteX = slid.X;
                    _remoteY = slid.Y;
                    _session.TrySend(new Packet("move", _remoteX, _remoteY));
                }
                return;
            }
            if (isAndroidTarget && !crossedAny) _androidReturnPushPx = 0;
        }
        if (monitor != null && !withinEntryGrace)
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
            if (NavigateEnvironment==null && leaving &&
                _remoteMonitors.IsExternalEdge(borderPoint, returnEdge, 1))
            {
                if (ShouldCrossAndroidReturnEdge(isAndroidTarget, true,
                    monitor, nextX, nextY, returnEdge))
                {
                    _androidReturnPushPx = 0;
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
            else if (isAndroidTarget && !leaving && NavigateEnvironment == null)
            {
                _androidReturnPushPx = 0;
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
        IntPtr rawBuffer = Interlocked.Exchange(ref _rawBuffer, IntPtr.Zero);
        if (rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rawBuffer);
    }
}
