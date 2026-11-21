using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskTouch.DeviceShare;

internal readonly record struct NormalizedSpatialBox(
    int Order,
    double X,
    double Y,
    double W,
    double H);

internal sealed record BridgeDeviceItemDto(
    int Id,
    string Name,
    string Platform,
    bool IsLocal,
    bool IsSelected,
    bool IsControlling,
    bool SupportsInput,
    int NotificationCount,
    string StatusText,
    double LayoutX = 0,
    double LayoutY = 0,
    double LayoutW = 0,
    double LayoutH = 0,
    int ArrangementOrder = 0,
    int BatteryPercent = -1,
    bool IsCharging = false,
    string SignalLabel = "",
    bool HasCamera = false,
    bool HasMicrophone = false,
    bool IsCameraActive = false,
    bool IsMicrophoneActive = false);

internal sealed record BridgeDeviceSnapshotDto(
    int Version,
    long UpdatedUtcMs,
    string LocalName,
    int LocalNotificationCount,
    int ActiveTargetId,
    bool IsRemoteActive,
    string ActiveDeviceName,
    int ConnectedCount,
    int TotalNotificationCount,
    bool HasAnyKnownNotificationCount,
    IReadOnlyList<BridgeDeviceItemDto> Devices,
    string ActiveProfile = "Work",
    bool ClipboardSyncEnabled = true,
    string ActiveForegroundApp = "",
    string ActiveShortcutPreset = "Default");

/// <summary>
/// Exposes live connected devices, notification badges, battery, profile/clipboard state,
/// and 1-click commands over a local user-only Named Pipe + atomic JSON file for DeskSpace.
/// </summary>
internal sealed class DeskSpaceDeckBridge : IDisposable
{
    internal const string PipeName = "DeskTouch.Vision.DeckBridge.V1";

    internal static string SnapshotFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "live-devices.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly Action<int> _onSwitchTarget;
    private readonly Action _onReturnLocal;
    private readonly Action<int> _onTogglePay;
    private readonly Action _onShowWindow;
    private readonly Action<int>? _onOpenNotifications;
    private readonly Action? _onToggleClipboard;
    private readonly Action<int, string?>? _onDropFile;
    private readonly Action<int, string>? _onPowerAction;
    private readonly Action<int, string>? _onMediaAction;
    private readonly Action<string>? _onSwitchProfile;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _stateLock = new();
    private readonly object _fileLock = new();

    private BridgeDeviceSnapshotDto _snapshot;
    private string _snapshotJson = "{}";
    private string _lastStateSignature = "";
    private long _lastFileWriteTicks;
    private int _started;
    private volatile bool _disposed;

    internal DeskSpaceDeckBridge(
        Action<int> onSwitchTarget,
        Action onReturnLocal,
        Action<int> onTogglePay,
        Action onShowWindow,
        Action<int>? onOpenNotifications = null,
        Action? onToggleClipboard = null,
        Action<int, string?>? onDropFile = null,
        Action<int, string>? onPowerAction = null,
        Action<int, string>? onMediaAction = null,
        Action<string>? onSwitchProfile = null)
    {
        _onSwitchTarget = onSwitchTarget;
        _onReturnLocal = onReturnLocal;
        _onTogglePay = onTogglePay;
        _onShowWindow = onShowWindow;
        _onOpenNotifications = onOpenNotifications;
        _onToggleClipboard = onToggleClipboard;
        _onDropFile = onDropFile;
        _onPowerAction = onPowerAction;
        _onMediaAction = onMediaAction;
        _onSwitchProfile = onSwitchProfile;
        _snapshot = CreateEmptySnapshot(Environment.MachineName, -1);
        _snapshotJson = SerializeSnapshot(_snapshot);
    }

    internal static BridgeDeviceSnapshotDto CreateEmptySnapshot(string localName, int localNotificationCount)
    {
        int total = localNotificationCount >= 0 ? localNotificationCount : 0;
        bool hasKnown = localNotificationCount >= 0;
        var local = new BridgeDeviceItemDto(
            EnvironmentRouteOrder.Local,
            string.IsNullOrWhiteSpace(localName) ? Environment.MachineName : localName,
            "Windows",
            IsLocal: true,
            IsSelected: true,
            IsControlling: true,
            SupportsInput: true,
            NotificationCount: localNotificationCount,
            StatusText: "This PC");
        return new BridgeDeviceSnapshotDto(
            Version: 1,
            UpdatedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LocalName: local.Name,
            LocalNotificationCount: localNotificationCount,
            ActiveTargetId: EnvironmentRouteOrder.Local,
            IsRemoteActive: false,
            ActiveDeviceName: local.Name,
            ConnectedCount: 0,
            TotalNotificationCount: total,
            HasAnyKnownNotificationCount: hasKnown,
            Devices: new[] { local });
    }

    internal static string SerializeSnapshot(BridgeDeviceSnapshotDto snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);

    internal static string ComputeStateSignature(BridgeDeviceSnapshotDto snapshot)
    {
        var sb = new StringBuilder(128);
        sb.Append(snapshot.LocalName).Append('|')
            .Append(snapshot.LocalNotificationCount).Append('|')
            .Append(snapshot.ActiveTargetId).Append('|')
            .Append(snapshot.IsRemoteActive ? '1' : '0').Append('|')
            .Append(snapshot.ConnectedCount).Append('|')
            .Append(snapshot.TotalNotificationCount).Append('|')
            .Append(snapshot.ActiveProfile).Append('|')
            .Append(snapshot.ClipboardSyncEnabled ? '1' : '0').Append('|')
            .Append(snapshot.ActiveForegroundApp).Append('|')
            .Append(snapshot.ActiveShortcutPreset);
        foreach (var d in snapshot.Devices)
        {
            sb.Append(';').Append(d.Id).Append(':')
                .Append(d.Name).Append(':')
                .Append(d.Platform).Append(':')
                .Append(d.IsSelected ? '1' : '0')
                .Append(d.IsControlling ? '1' : '0')
                .Append(d.SupportsInput ? '1' : '0').Append(':')
                .Append(d.NotificationCount).Append(':')
                .Append(d.BatteryPercent).Append(d.IsCharging ? '1' : '0').Append(':')
                .Append(d.HasCamera ? '1' : '0')
                .Append(d.HasMicrophone ? '1' : '0')
                .Append(d.IsCameraActive ? '1' : '0')
                .Append(d.IsMicrophoneActive ? '1' : '0').Append(':')
                .Append(d.ArrangementOrder).Append(':')
                .Append(Math.Round(d.LayoutX, 3)).Append(',')
                .Append(Math.Round(d.LayoutY, 3)).Append(',')
                .Append(Math.Round(d.LayoutW, 3)).Append(',')
                .Append(Math.Round(d.LayoutH, 3)).Append(':')
                .Append(d.StatusText);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static Dictionary<TKey, NormalizedSpatialBox> ComputeNormalizedLayout<TItem, TKey>(
        IReadOnlyList<TItem> items,
        Func<TItem, TKey> keySelector,
        Func<TItem, System.Drawing.RectangleF?> boundsSelector)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, NormalizedSpatialBox>();
        if (items.Count == 0) return result;

        var ordered = DisplayArrangementControl.SortBySpatialArrangement(items, boundsSelector);
        var matchedRects = ordered
            .Select(boundsSelector)
            .Where(r => r.HasValue && r.Value.Width > 0 && r.Value.Height > 0)
            .Select(r => r!.Value)
            .ToArray();

        if (matchedRects.Length == 0)
        {
            for (int i = 0; i < ordered.Count; i++)
                result[keySelector(ordered[i])] = new NormalizedSpatialBox(i, 0, 0, 0, 0);
            return result;
        }

        float minX = matchedRects.Min(r => r.Left);
        float minY = matchedRects.Min(r => r.Top);
        float maxX = matchedRects.Max(r => r.Right);
        float maxY = matchedRects.Max(r => r.Bottom);
        double spanW = Math.Max(1.0, maxX - minX);
        double spanH = Math.Max(1.0, maxY - minY);

        for (int i = 0; i < ordered.Count; i++)
        {
            var r = boundsSelector(ordered[i]);
            if (r is { Width: > 0, Height: > 0 } rect)
            {
                result[keySelector(ordered[i])] = new NormalizedSpatialBox(
                    i,
                    Math.Round((rect.Left - minX) / spanW, 4),
                    Math.Round((rect.Top - minY) / spanH, 4),
                    Math.Round(rect.Width / spanW, 4),
                    Math.Round(rect.Height / spanH, 4));
            }
            else
            {
                result[keySelector(ordered[i])] = new NormalizedSpatialBox(i, 0, 0, 0, 0);
            }
        }
        return result;
    }

    internal static IReadOnlyList<BridgeDeviceItemDto> NormalizeAndOrderDevices(
        IReadOnlyList<BridgeDeviceItemDto> raw,
        IReadOnlyDictionary<int, System.Drawing.RectangleF> arrangedBounds)
    {
        if (raw.Count == 0) return raw;
        if (arrangedBounds.Count == 0)
        {
            return raw.Select((d, idx) => d with { ArrangementOrder = idx }).ToArray();
        }
        var layout = ComputeNormalizedLayout(
            raw,
            d => d.Id,
            d => arrangedBounds.TryGetValue(d.Id, out var r) && r.Width > 0 && r.Height > 0 ? r : null);
        return raw
            .Select(d =>
            {
                if (!layout.TryGetValue(d.Id, out var b))
                    return d;
                return d with
                {
                    ArrangementOrder = b.Order,
                    LayoutX = b.X,
                    LayoutY = b.Y,
                    LayoutW = b.W,
                    LayoutH = b.H
                };
            })
            .OrderBy(d => d.ArrangementOrder)
            .ToArray();
    }

    internal BridgeDeviceSnapshotDto CurrentSnapshot
    {
        get { lock (_stateLock) return _snapshot; }
    }

    internal void Start()
    {
        if (_disposed || Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;
        WriteSnapshotFile(_snapshotJson, force: true);
        for (int i = 0; i < 2; i++)
            _ = Task.Run(AcceptLoopAsync);
    }

    internal void PublishSnapshot(BridgeDeviceSnapshotDto snapshot)
    {
        if (_disposed) return;
        string json = SerializeSnapshot(snapshot);
        string signature = ComputeStateSignature(snapshot);
        bool shouldWriteFile = false;
        lock (_stateLock)
        {
            _snapshot = snapshot;
            _snapshotJson = json;
            long now = Environment.TickCount64;
            if (!string.Equals(_lastStateSignature, signature, StringComparison.Ordinal) ||
                now - _lastFileWriteTicks >= 15000)
            {
                _lastStateSignature = signature;
                _lastFileWriteTicks = now;
                shouldWriteFile = true;
            }
        }
        if (shouldWriteFile)
            WriteSnapshotFile(json, force: false);
    }

    private void WriteSnapshotFile(string json, bool force)
    {
        if (_disposed && !force) return;
        try
        {
            lock (_fileLock)
            {
                string path = SnapshotFilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string tmp = path + ".tmp." + Environment.ProcessId;
                File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception) { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancel.IsCancellationRequested && !_disposed)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    4,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);
                await HandleClientAsync(server, _cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                try { await Task.Delay(120, _cancel.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (server != null)
                {
                    try { server.Dispose(); } catch (Exception) { }
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var buffer = new byte[512];
        int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
        string request = read > 0
            ? Encoding.UTF8.GetString(buffer, 0, read).Trim()
            : "GET";
        DispatchCommand(request);
        string response;
        lock (_stateLock)
            response = _snapshotJson;
        byte[] payload = Encoding.UTF8.GetBytes(response);
        await server.WriteAsync(payload, ct).ConfigureAwait(false);
        await server.FlushAsync(ct).ConfigureAwait(false);
    }

    internal void DispatchCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        string trimmed = command.Trim();
        if (string.Equals(trimmed, "GET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "PING", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(trimmed, "RETURN", StringComparison.OrdinalIgnoreCase))
        {
            _onReturnLocal();
            return;
        }
        if (string.Equals(trimmed, "SHOW", StringComparison.OrdinalIgnoreCase))
        {
            _onShowWindow();
            return;
        }
        if (trimmed.StartsWith("SWITCH:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed["SWITCH:".Length..].Trim(), out int targetId))
                _onSwitchTarget(targetId);
            return;
        }
        if (trimmed.StartsWith("TOGGLE_PAY:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed["TOGGLE_PAY:".Length..].Trim(), out int peerId))
                _onTogglePay(peerId);
            else
                _onTogglePay(0);
            return;
        }
        if (trimmed.StartsWith("OPEN_NOTIFS:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed["OPEN_NOTIFS:".Length..].Trim(), out int nid))
                _onOpenNotifications?.Invoke(nid);
            return;
        }
        if (string.Equals(trimmed, "TOGGLE_CLIPBOARD", StringComparison.OrdinalIgnoreCase))
        {
            _onToggleClipboard?.Invoke();
            return;
        }
        if (trimmed.StartsWith("DROP_FILE:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed["DROP_FILE:".Length..].Trim(), out int fid))
                _onDropFile?.Invoke(fid, null);
            return;
        }
        if (trimmed.StartsWith("DROP_FILE_PATH:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed["DROP_FILE_PATH:".Length..];
            int sep = rest.IndexOf('|');
            if (sep > 0 && int.TryParse(rest[..sep], out int tid))
                _onDropFile?.Invoke(tid, rest[(sep + 1)..]);
            return;
        }
        if (trimmed.StartsWith("POWER:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed["POWER:".Length..];
            int sep = rest.IndexOf(':');
            if (sep > 0 && int.TryParse(rest[..sep], out int pid))
                _onPowerAction?.Invoke(pid, rest[(sep + 1)..].Trim());
            return;
        }
        if (trimmed.StartsWith("MEDIA:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed["MEDIA:".Length..];
            int sep = rest.IndexOf(':');
            if (sep > 0 && int.TryParse(rest[..sep], out int mid))
                _onMediaAction?.Invoke(mid, rest[(sep + 1)..].Trim());
            return;
        }
        if (trimmed.StartsWith("PROFILE:", StringComparison.OrdinalIgnoreCase))
        {
            _onSwitchProfile?.Invoke(trimmed["PROFILE:".Length..].Trim());
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            var offline = CreateEmptySnapshot(Environment.MachineName, -1);
            WriteSnapshotFile(SerializeSnapshot(offline), force: true);
        }
        catch (Exception) { }
        try { _cancel.Dispose(); } catch (ObjectDisposedException) { }
    }
}
