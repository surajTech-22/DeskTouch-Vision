package com.desktouch.vision;

import android.app.Notification;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.RemoteInput;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Reads notification counts cleanly and (only when user-enabled) non-banking notification previews/quick-reply/OTP helper. */
public final class DeskTouchNotificationListener extends NotificationListenerService {
    private static volatile DeskTouchNotificationListener activeListener;
    private static volatile int cachedCount = -1;
    private static volatile List<EcosystemFeatures.NotificationItem> cachedItems = Collections.emptyList();
    private static volatile String lastOtpSentKey = "";

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

    static void openAppInfo(Context context) {
        if (context == null) return;
        try {
            Intent intent = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                android.net.Uri.fromParts("package", context.getPackageName(), null));
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
        publishUpdatedCount(null);
    }

    @Override
    public void onListenerDisconnected() {
        if (activeListener == this) activeListener = null;
        cachedCount = -1;
        DeskTouchRuntime.notifyLocalNotificationCountChanged(-1);
    }

    static List<EcosystemFeatures.NotificationItem> readRecentItems(Context context) {
        if (!isPermissionGranted(context)) return Collections.emptyList();
        return cachedItems;
    }

    static boolean cancelByKey(String key) {
        DeskTouchNotificationListener svc = activeListener;
        if (svc == null || key == null || key.isEmpty()) return false;
        try {
            svc.cancelNotification(key);
            return true;
        } catch (Exception ignored) {
            return false;
        }
    }

    static boolean sendInlineReply(Context context, String key, String replyText) {
        DeskTouchNotificationListener svc = activeListener;
        if (svc == null || context == null || key == null || key.isEmpty() ||
            replyText == null || replyText.trim().isEmpty()) {
            return false;
        }
        try {
            StatusBarNotification[] active = svc.getActiveNotifications();
            if (active == null) return false;
            for (StatusBarNotification sbn : active) {
                if (sbn == null || !key.equals(sbn.getKey())) continue;
                Notification n = sbn.getNotification();
                if (n == null || n.actions == null) return false;
                for (Notification.Action act : n.actions) {
                    if (act == null || act.actionIntent == null || act.getRemoteInputs() == null) continue;
                    RemoteInput[] inputs = act.getRemoteInputs();
                    if (inputs.length == 0) continue;
                    Intent fill = new Intent();
                    Bundle results = new Bundle();
                    for (RemoteInput ri : inputs) {
                        results.putCharSequence(ri.getResultKey(), replyText.trim());
                    }
                    RemoteInput.addResultsToIntent(inputs, fill, results);
                    act.actionIntent.send(context, 0, fill);
                    return true;
                }
            }
        } catch (Exception ignored) {
        }
        return false;
    }

    @Override
    public void onNotificationPosted(StatusBarNotification sbn) {
        publishUpdatedCount(sbn);
    }

    @Override
    public void onNotificationRemoved(StatusBarNotification sbn) {
        publishUpdatedCount(null);
    }

    private void publishUpdatedCount(StatusBarNotification newlyPosted) {
        int count = recountLocked();
        if (count >= 0) {
            DeskTouchRuntime.notifyLocalNotificationCountChanged(count);
        }
        Context ctx = getApplicationContext();
        boolean sharePreviews = EcosystemFeatures.isNotifPreviewsEnabled(ctx);
        boolean shareOtp = EcosystemFeatures.isOtpHelperEnabled(ctx);
        if (sharePreviews) {
            List<EcosystemFeatures.NotificationItem> items = buildPreviewList(ctx);
            cachedItems = items;
            DeskTouchRuntime.notifyLocalNotificationDetailsChanged(items);
        } else {
            cachedItems = Collections.emptyList();
        }
        if (shareOtp && newlyPosted != null) {
            tryPublishOtp(ctx, newlyPosted);
        }
    }

    private List<EcosystemFeatures.NotificationItem> buildPreviewList(Context ctx) {
        try {
            StatusBarNotification[] active = getActiveNotifications();
            if (active == null) return Collections.emptyList();
            String selfPkg = getPackageName();
            PackageManager pm = ctx != null ? ctx.getPackageManager() : null;
            ArrayList<EcosystemFeatures.NotificationItem> list = new ArrayList<>();
            for (StatusBarNotification sbn : active) {
                if (sbn == null) continue;
                String pkg = sbn.getPackageName();
                if (selfPkg != null && selfPkg.equals(pkg)) continue;
                if (PaymentAppDetector.isPaymentOrBankingPackage(pkg)) continue;
                Notification n = sbn.getNotification();
                if (n == null || (n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) continue;
                Bundle ex = n.extras;
                String title = ex != null && ex.getCharSequence(Notification.EXTRA_TITLE) != null
                    ? ex.getCharSequence(Notification.EXTRA_TITLE).toString() : "";
                String text = ex != null && ex.getCharSequence(Notification.EXTRA_TEXT) != null
                    ? ex.getCharSequence(Notification.EXTRA_TEXT).toString() : "";
                if (title.isEmpty() && text.isEmpty()) continue;
                String appLabel = pkg != null ? pkg : "App";
                if (pm != null && pkg != null) {
                    try {
                        appLabel = pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString();
                    } catch (Exception ignored) {}
                }
                boolean canReply = hasRemoteReplyAction(n);
                String otp = EcosystemFeatures.isOtpHelperEnabled(ctx)
                    ? EcosystemFeatures.extractVerificationCode(title, text) : "";
                list.add(new EcosystemFeatures.NotificationItem(
                    sbn.getKey(), appLabel, title, text, sbn.getPostTime(), canReply, otp));
                if (list.size() >= 6) break;
            }
            return Collections.unmodifiableList(list);
        } catch (Exception ignored) {
            return Collections.emptyList();
        }
    }

    private static boolean hasRemoteReplyAction(Notification n) {
        if (n.actions == null) return false;
        for (Notification.Action act : n.actions) {
            if (act != null && act.actionIntent != null && act.getRemoteInputs() != null &&
                act.getRemoteInputs().length > 0) {
                return true;
            }
        }
        return false;
    }

    private void tryPublishOtp(Context ctx, StatusBarNotification sbn) {
        try {
            String pkg = sbn.getPackageName();
            if (pkg == null || pkg.equals(getPackageName()) || PaymentAppDetector.isPaymentOrBankingPackage(pkg))
                return;
            Notification n = sbn.getNotification();
            if (n == null || n.extras == null || (n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) return;
            CharSequence tc = n.extras.getCharSequence(Notification.EXTRA_TITLE);
            CharSequence bc = n.extras.getCharSequence(Notification.EXTRA_TEXT);
            String title = tc != null ? tc.toString() : "";
            String body = bc != null ? bc.toString() : "";
            String code = EcosystemFeatures.extractVerificationCode(title, body);
            if (code.isEmpty()) return;
            String dedup = sbn.getKey() + "|" + code;
            if (dedup.equals(lastOtpSentKey)) return;
            lastOtpSentKey = dedup;
            String appLabel = pkg;
            try {
                PackageManager pm = ctx.getPackageManager();
                appLabel = pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString();
            } catch (Exception ignored) {}
            DeskTouchRuntime.notifyLocalOtpDetected(appLabel, code, title);
        } catch (Exception ignored) {}
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
