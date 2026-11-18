package com.desktouch.vision;

import android.app.Notification;
import android.app.NotificationManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.provider.Settings;
import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;

/** Counts active Android notifications (numeric count only; never reads notification text or titles). */
public final class DeskTouchNotificationListener extends NotificationListenerService {
    private static volatile DeskTouchNotificationListener activeListener;
    private static volatile int cachedCount = -1;

    static boolean isPermissionGranted(Context context) {
        if (context == null) return false;
        try {
            NotificationManager mgr = context.getSystemService(NotificationManager.class);
            if (mgr != null && Build.VERSION.SDK_INT >= 27) {
                return mgr.isNotificationListenerAccessGranted(
                    new ComponentName(context, DeskTouchNotificationListener.class));
            }
        } catch (Exception ignored) {
        }
        try {
            String flat = Settings.Secure.getString(
                context.getContentResolver(), "enabled_notification_listeners");
            if (flat == null || flat.isEmpty()) return false;
            String self = new ComponentName(context, DeskTouchNotificationListener.class).flattenToString();
            String shortSelf = context.getPackageName() + "/" + DeskTouchNotificationListener.class.getName();
            return flat.contains(self) || flat.contains(shortSelf);
        } catch (Exception ignored) {
            return false;
        }
    }

    static int readActiveCount(Context context) {
        if (!isPermissionGranted(context)) {
            cachedCount = -1;
            return -1;
        }
        DeskTouchNotificationListener svc = activeListener;
        if (svc != null) {
            int fresh = svc.recountLocked();
            if (fresh >= 0) return fresh;
        }
        return cachedCount >= 0 ? cachedCount : 0;
    }

    static void openSettings(Context context) {
        if (context == null) return;
        try {
            Intent intent = new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS);
            if (!(context instanceof android.app.Activity)) {
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            }
            context.startActivity(intent);
        } catch (Exception ignored) {
        }
    }

    @Override
    public void onListenerConnected() {
        activeListener = this;
        publishUpdatedCount();
    }

    @Override
    public void onListenerDisconnected() {
        if (activeListener == this) activeListener = null;
        cachedCount = -1;
        DeskTouchRuntime.notifyLocalNotificationCountChanged(-1);
    }

    @Override
    public void onNotificationPosted(StatusBarNotification sbn) {
        publishUpdatedCount();
    }

    @Override
    public void onNotificationRemoved(StatusBarNotification sbn) {
        publishUpdatedCount();
    }

    private void publishUpdatedCount() {
        int count = recountLocked();
        if (count >= 0) {
            DeskTouchRuntime.notifyLocalNotificationCountChanged(count);
        }
    }

    private int recountLocked() {
        try {
            StatusBarNotification[] active = getActiveNotifications();
            if (active == null) return 0;
            String selfPkg = getPackageName();
            int count = 0;
            for (StatusBarNotification sbn : active) {
                if (sbn == null) continue;
                if (selfPkg != null && selfPkg.equals(sbn.getPackageName())) continue;
                Notification n = sbn.getNotification();
                if (n != null && (n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) continue;
                count++;
            }
            cachedCount = count;
            return count;
        } catch (Exception ignored) {
            return cachedCount;
        }
    }
}
