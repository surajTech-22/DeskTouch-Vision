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
    private int _polling;
    private volatile bool _disposed;
    private readonly System.Threading.Timer _timer;

    internal int CurrentCount => _lastCount;
    internal event Action<int>? CountChanged;

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

    internal async Task RefreshAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;
        try
        {
            int next = await TryReadCountOnceAsync();
            int prev = Interlocked.Exchange(ref _lastCount, next);
            if (prev != next)
                CountChanged?.Invoke(next);
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
