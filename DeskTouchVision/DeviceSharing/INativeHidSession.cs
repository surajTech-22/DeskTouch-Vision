namespace DeskTouch.DeviceShare;

/// <summary>Keep existing one-hook SourceCapture and UI; only the actual
/// Android native HID transport varies (Wi-Fi UHID or optional BLE HOGP).</summary>
internal interface INativeHidSession : IInputSession, IAsyncDisposable
{
    event Action<Packet>? Authority;
    event Action<string>? Status;
    bool IsStarted { get; }
    bool Ready { get; }
    string CurrentState { get; }
    Task StartAsync();
    void SetPhoneGeometry(MonitorTopology monitors);
}
