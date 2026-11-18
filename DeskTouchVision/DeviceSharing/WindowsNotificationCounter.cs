using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Reads the local Windows notification count (integer count only; never reads titles,
/// bodies, or app identities) using Windows Action Center when permission is granted.
/// </summary>
internal sealed class WindowsNotificationCounter : IDisposable
{
    private volatile int _lastCount = -1;
    private volatile NotificationPreviewItem[] _lastItems = Array.Empty<NotificationPreviewItem>();
    private string _lastItemsJson = "";
    private int _polling;
    private volatile bool _disposed;
    private readonly System.Threading.Timer _timer;

    internal int CurrentCount => _lastCount;
    internal NotificationPreviewItem[] CurrentItems => _lastItems;
    internal bool EnablePreviewCapture { get; set; }
    internal event Action<int>? CountChanged;
    internal event Action<NotificationPreviewItem[]>? ItemsChanged;

    internal WindowsNotificationCounter()
    {
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, 500, 5000);
    }

    internal static async Task<int> TryReadCountOnceAsync()
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener == null) return -1;
            var access = listener.GetAccessStatus();
            if (access != UserNotificationListenerAccessStatus.Allowed)
                return -1;
            var active = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            return active?.Count ?? 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static async Task<(int Count, NotificationPreviewItem[] Items)> TryReadNotificationsOnceAsync(bool includeText)
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener == null) return (-1, Array.Empty<NotificationPreviewItem>());
            var access = listener.GetAccessStatus();
            if (access != UserNotificationListenerAccessStatus.Allowed)
                return (-1, Array.Empty<NotificationPreviewItem>());
            var active = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (active == null) return (0, Array.Empty<NotificationPreviewItem>());
            if (!includeText)
                return (active.Count, Array.Empty<NotificationPreviewItem>());

            var items = new List<NotificationPreviewItem>(Math.Min(6, active.Count));
            foreach (var n in active.Reverse().Take(6))
            {
                try
                {
                    string appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
                    var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()
                        ?? Array.Empty<string>();
                    string title = texts.Length > 0 ? texts[0] : appName;
                    string body = texts.Length > 1 ? string.Join(" ", texts.Skip(1)) : "";
                    items.Add(new NotificationPreviewItem(
                        n.Id.ToString(),
                        appName,
                        title,
                        body,
                        n.CreationTime.ToUnixTimeMilliseconds(),
                        CanReply: false,
                        OtpCode: null));
                }
                catch { }
            }
            return (active.Count, items.ToArray());
        }
        catch (Exception)
        {
            return (-1, Array.Empty<NotificationPreviewItem>());
        }
    }

    internal static void DismissLocalNotification(string key)
    {
        try
        {
            if (!uint.TryParse(key, out uint id)) return;
            var listener = UserNotificationListener.Current;
            if (listener?.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed)
                listener.RemoveNotification(id);
        }
        catch { }
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;
        try
        {
            var (next, items) = await TryReadNotificationsOnceAsync(EnablePreviewCapture);
            int prev = Interlocked.Exchange(ref _lastCount, next);
            if (prev != next)
                CountChanged?.Invoke(next);
            if (EnablePreviewCapture)
            {
                string json = DeviceTelemetryAndNotifModel.SerializeNotificationList(items);
                if (!string.Equals(json, _lastItemsJson, StringComparison.Ordinal))
                {
                    _lastItemsJson = json;
                    _lastItems = items;
                    ItemsChanged?.Invoke(items);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
