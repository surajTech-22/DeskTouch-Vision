using System.Collections.Concurrent;
using System.Threading.Channels;
using BleHid.Core;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace DeskTouch.DeviceShare;

/// <summary>
/// One native Bluetooth LE HID keyboard and mouse, presented to Android through
/// Windows' GATT peripheral API. No Accessibility, Android APK, or network
/// injection is involved in this transport. Physical input remains subject
/// to the existing SourceCapture authority and emergency-return controls.
/// </summary>
internal sealed class NativeHidInputSession : IInputSession, IAsyncDisposable
{
    private readonly Channel<Packet> _events = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly HashSet<int> _keys = [];
    private readonly object _gate = new();
    private BleHidPeripheral? _peripheral;
    private Task? _pump;
    private Packet? _latestMove;
    private int _moveQueued;
    private int _lease;
    private int _mouseButtons;
    private int _previousX, _previousY;
    private bool _positionKnown;
    private bool _started;
    private bool _closed;

    internal event Action<Packet>? Authority;
    internal event Action<string>? Status;
    internal bool IsStarted => _started;
    internal bool Ready {
        get {
            var peripheral = _peripheral;
            return _started && peripheral != null &&
                peripheral.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started &&
                peripheral.SubscribedKeyboardClients == 1 &&
                peripheral.SubscribedMouseClients == 1 &&
                peripheral.Hosts().Count == 1;
        }
    }
    internal string CurrentState => !_started ? "Bluetooth HID is off" :
        Ready ? "Native HID paired; keyboard and mouse ready" :
        "Advertising. Pair DeskTouch with Android in Bluetooth settings.";

    public MonitorTopology RemoteMonitors { get; private set; } =
        new([new MonitorRect("Android Bluetooth HID", 0, 0, 1080, 2400, true)]);

    internal void SetPhoneGeometry(MonitorTopology monitors) {
        if (_lease == 0) RemoteMonitors = monitors;
    }

    internal async Task StartAsync()
    {
        if (_started || _closed) return;
        var check = await BluetoothHidDiagnostics.CheckAsync();
        if (!check.Candidate)
            throw new NotSupportedException(check.Detail);
        var peripheral = new BleHidPeripheral(requireEncryption: true);
        peripheral.Log += message => {
            if (message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("subs", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("adv ", StringComparison.OrdinalIgnoreCase))
                Status?.Invoke(message);
        };
        try {
            // WinRT GATT server async operations must not block the WinForms UI.
            await Task.Run(peripheral.StartAsync);
            if (peripheral.AdvertisementStatus != GattServiceProviderAdvertisementStatus.Started)
                throw new InvalidOperationException(
                    "Bluetooth HID advertising did not start. Check Windows radio/driver.");
            _peripheral = peripheral;
            _started = true;
            _pump = Task.Run(PumpAsync);
            Status?.Invoke("Native Bluetooth keyboard/mouse advertising started.");
        }
        catch {
            await peripheral.DisposeAsync();
            throw;
        }
    }

    public bool TrySend(Packet p)
    {
        if (_closed || !Ready) return false;
        if (p.Type == "claim") {
            if (p.A <= 0 || _lease != 0) return false;
            var hosts = _peripheral!.Hosts();
            if (hosts.Count != 1 || !_peripheral.SelectHost(0)) return false;
            _lease = p.A;
            _positionKnown = false;
            Authority?.Invoke(new Packet("granted", p.A));
            return true;
        }
        if (p.Type is "release" or "reset") {
            if (p.Type == "reset" || p.A == _lease) _lease = 0;
            Interlocked.Exchange(ref _latestMove, null);
            return _events.Writer.TryWrite(new Packet("release"));
        }
        if (_lease <= 0) return false;
        if (p.Type == "move") {
            Interlocked.Exchange(ref _latestMove, p);
            if (Interlocked.CompareExchange(ref _moveQueued, 1, 0) == 0)
                return _events.Writer.TryWrite(new Packet("move-flush"));
            return true;
        }
        return _events.Writer.TryWrite(p);
    }

    private async Task PumpAsync()
    {
        try {
            await foreach (var p in _events.Reader.ReadAllAsync()) {
                var peripheral = _peripheral;
                if (peripheral == null) break;
                if (p.Type == "release") {
                    _keys.Clear(); _mouseButtons = 0; _positionKnown = false;
                    await peripheral.ReleaseKeysAsync();
                    await peripheral.SendMouseAsync(BleHid.Core.MouseButtons.None, 0, 0, 0);
                    continue;
                }
                if (!Ready || _lease <= 0) continue;
                if (p.Type == "move-flush") {
                    Interlocked.Exchange(ref _moveQueued, 0);
                    var latest = Interlocked.Exchange(ref _latestMove, null);
                    if (latest != null) await ApplyMoveAsync(peripheral, latest);
                    continue;
                }
                // Preserve keyboard/button ordering with the latest cursor position.
                var before = Interlocked.Exchange(ref _latestMove, null);
                if (before != null) await ApplyMoveAsync(peripheral, before);
                if (p.Type == "button") {
                    int flag = p.A switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
                    if (p.B != 0) _mouseButtons |= flag;
                    else _mouseButtons &= ~flag;
                    await peripheral.SendMouseAsync((BleHid.Core.MouseButtons)_mouseButtons, 0, 0, 0);
                }
                else if (p.Type == "wheel") {
                    if (p.B == 0)
                        await peripheral.SendMouseAsync((BleHid.Core.MouseButtons)_mouseButtons, 0, 0,
                            Math.Clamp(p.A / 120, -127, 127));
                }
                else if (p.Type == "key") {
                    var vk = p.D;
                    if (p.C != 0) _keys.Add(vk);
                    else _keys.Remove(vk);
                    var modifiers = NativeHidKeyMap.Modifiers(_keys);
                    var usages = _keys.Where(k => !NativeHidKeyMap.IsModifier(k))
                        .Select(NativeHidKeyMap.Usage).Where(x => x != 0)
                        .Take(6).ToArray();
                    await peripheral.SendKeyboardAsync(modifiers, usages);
                }
            }
        }
        catch (Exception ex) {
            _lease = 0;
            Status?.Invoke("Native HID report delivery stopped: " + ex.GetType().Name);
            Authority?.Invoke(new Packet("local-active"));
        }
    }

    private async Task ApplyMoveAsync(BleHidPeripheral peripheral, Packet p)
    {
        if (_lease <= 0 || !Ready) return;
        if (!_positionKnown) {
            _previousX = p.A; _previousY = p.B; _positionKnown = true;
            Authority?.Invoke(new Packet("active", _lease));
            return;
        }
        int dx = p.A - _previousX, dy = p.B - _previousY;
        _previousX = p.A; _previousY = p.B;
        if (dx == 0 && dy == 0) return;
        // Native relative X/Y HID reports: Android owns cursor acceleration.
        await peripheral.SendMouseAsync((BleHid.Core.MouseButtons)_mouseButtons,
            Math.Clamp(dx, -32767, 32767), Math.Clamp(dy, -32767, 32767), 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true; _started = false; _lease = 0;
        _events.Writer.TryWrite(new Packet("release"));
        _events.Writer.TryComplete();
        if (_pump != null) try { await _pump; } catch { }
        if (_peripheral != null) {
            await _peripheral.DisposeAsync();
            _peripheral = null;
        }
    }
}

/// <summary>Windows virtual-key to USB HID usage codes (US physical layout).
/// Input remains genuine HID and never commits text through Android IME.</summary>
internal static class NativeHidKeyMap
{
    internal static bool IsModifier(int vk) => vk is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    internal static KeyModifiers Modifiers(IReadOnlySet<int> keys)
    {
        KeyModifiers m = KeyModifiers.None;
        if (keys.Contains(0x10) || keys.Contains(0xA0)) m |= KeyModifiers.LeftShift;
        if (keys.Contains(0xA1)) m |= KeyModifiers.RightShift;
        if (keys.Contains(0x11) || keys.Contains(0xA2)) m |= KeyModifiers.LeftControl;
        if (keys.Contains(0xA3)) m |= KeyModifiers.RightControl;
        if (keys.Contains(0x12) || keys.Contains(0xA4)) m |= KeyModifiers.LeftAlt;
        if (keys.Contains(0xA5)) m |= KeyModifiers.RightAlt;
        if (keys.Contains(0x5B)) m |= KeyModifiers.LeftGui;
        if (keys.Contains(0x5C)) m |= KeyModifiers.RightGui;
        return m;
    }

    internal static byte Usage(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return (byte)(vk - 0x41 + 4);
        if (vk is >= 0x31 and <= 0x39) return (byte)(vk - 0x31 + 0x1E);
        if (vk == 0x30) return 0x27;
        if (vk is >= 0x70 and <= 0x7B) return (byte)(vk - 0x70 + 0x3A);
        if (vk is >= 0x60 and <= 0x69) return (byte)(vk - 0x60 + 0x62);
        return vk switch {
            0x08 => 0x2A, 0x09 => 0x2B, 0x0D => 0x28, 0x1B => 0x29,
            0x20 => 0x2C, 0x14 => 0x39, 0x90 => 0x53, 0x91 => 0x47,
            0x2D => 0x49, 0x24 => 0x4A, 0x21 => 0x4B, 0x2E => 0x4C,
            0x23 => 0x4D, 0x22 => 0x4E, 0x27 => 0x4F, 0x25 => 0x50,
            0x28 => 0x51, 0x26 => 0x52,
            0xBA => 0x33, 0xBB => 0x2E, 0xBC => 0x36, 0xBD => 0x2D,
            0xBE => 0x37, 0xBF => 0x38, 0xC0 => 0x35, 0xDB => 0x2F,
            0xDC => 0x31, 0xDD => 0x30, 0xDE => 0x34, 0x6A => 0x55,
            0x6B => 0x57, 0x6D => 0x56, 0x6E => 0x63, 0x6F => 0x54,
            _ => 0
        };
    }
}
