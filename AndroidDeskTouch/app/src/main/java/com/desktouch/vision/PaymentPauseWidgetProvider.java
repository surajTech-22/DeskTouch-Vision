package com.desktouch.vision;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Handler;
import android.os.Looper;
import android.widget.RemoteViews;
import android.widget.Toast;

/**
 * 1-tap home-screen widget to turn off Android Accessibility for payment/banking apps,
 * or jump straight to Accessibility settings when ready to turn remote control back on.
 */
public final class PaymentPauseWidgetProvider extends AppWidgetProvider {
    static final String ACTION_TOGGLE_PAYMENT_MODE =
        "com.desktouch.vision.ACTION_TOGGLE_PAYMENT_MODE";
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    @Override
    public void onUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds) {
        if (appWidgetIds == null || appWidgetIds.length == 0) return;
        RemoteViews views = buildViews(context);
        for (int appWidgetId : appWidgetIds) {
            appWidgetManager.updateAppWidget(appWidgetId, views);
        }
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        super.onReceive(context, intent);
        if (intent == null || !ACTION_TOGGLE_PAYMENT_MODE.equals(intent.getAction())) {
            return;
        }
        Context app = context.getApplicationContext();
        if (PermissionContinuity.systemAccessibilityEnabled(app)) {
            boolean selfDisabled = DeskTouchAccessibilityService.pauseForBanking(
                app, "Turned off via Pay Mode widget");
            updateAll(app);
            AutoConnectService.refreshNotification();
            MAIN.post(() -> Toast.makeText(
                app,
                selfDisabled
                    ? "DeskTouch Accessibility turned off for payment apps"
                    : "Turn off DeskTouch Windows control in Accessibility settings",
                Toast.LENGTH_SHORT
            ).show());
            if (!selfDisabled) {
                DeskTouchAccessibilityService.openAccessibilitySettings(app, false);
            }
            return;
        }
        DeskTouchAccessibilityService.setAllowed(app, true);
        DeskTouchRuntime.accessibilityPermissionChanged();
        updateAll(app);
        DeskTouchAccessibilityService.openAccessibilitySettings(app, true);
    }

    static void updateAll(Context context) {
        if (context == null) return;
        Context app = context.getApplicationContext();
        AppWidgetManager manager = AppWidgetManager.getInstance(app);
        if (manager == null) return;
        int[] ids = manager.getAppWidgetIds(
            new ComponentName(app, PaymentPauseWidgetProvider.class));
        if (ids == null || ids.length == 0) return;
        RemoteViews views = buildViews(app);
        for (int id : ids) {
            manager.updateAppWidget(id, views);
        }
    }

    static boolean requestPin(Context context) {
        if (context == null) return false;
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        if (manager == null || !manager.isRequestPinAppWidgetSupported()) {
            return false;
        }
        return manager.requestPinAppWidget(
            new ComponentName(context, PaymentPauseWidgetProvider.class), null, null);
    }

    private static RemoteViews buildViews(Context context) {
        boolean accessibilityOn = PermissionContinuity.systemAccessibilityEnabled(context);
        RemoteViews views = new RemoteViews(
            context.getPackageName(), R.layout.payment_pause_widget);
        views.setInt(
            R.id.payment_widget_root,
            "setBackgroundResource",
            accessibilityOn ? R.drawable.payment_widget_bg_active : R.drawable.payment_widget_bg_safe);
        views.setTextViewText(
            R.id.payment_widget_title,
            accessibilityOn ? "Tap for Payment Apps" : "Payment Apps Safe");
        views.setTextViewText(
            R.id.payment_widget_status,
            accessibilityOn
                ? "Accessibility ON • Tap to turn off"
                : "Accessibility OFF • Tap to turn on");
        Intent click = new Intent(context, PaymentPauseWidgetProvider.class)
            .setAction(ACTION_TOGGLE_PAYMENT_MODE);
        PendingIntent pending = PendingIntent.getBroadcast(
            context,
            37384,
            click,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.payment_widget_root, pending);
        return views;
    }
}
