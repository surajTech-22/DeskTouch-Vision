package com.desktouch.vision;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.RemoteViews;
import android.widget.Toast;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Fixed 4x4 Home Screen Touchpad Widget with toggle-select / unselect PC pills,
 * direct left/right/scroll actions, and 1-tap launch into translucent floating overlays
 * for live touchpad drag, overlapping Full Screen preview, Keyboard, Ctrl+ shortcuts,
 * and Horizontal mode (zero Pay Safe buttons inside the widget).
 */
public final class TouchpadWidgetProvider extends AppWidgetProvider {
    static final String ACTION_TOGGLE_PC = "com.desktouch.vision.ACTION_WIDGET_TOGGLE_PC";
    static final String ACTION_MOUSE_CLICK = "com.desktouch.vision.ACTION_WIDGET_MOUSE_CLICK";
    static final String ACTION_SCROLL = "com.desktouch.vision.ACTION_WIDGET_SCROLL";
    static final String ACTION_REFRESH = "com.desktouch.vision.ACTION_WIDGET_REFRESH";

    static final String EXTRA_TARGET_ID = "target_id";
    static final String EXTRA_TARGET_NAME = "target_name";
    static final String EXTRA_BUTTON = "button";
    static final String EXTRA_WHEEL = "wheel";

    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static final Object WIDGET_UPDATE_LOCK = new Object();
    private static final ScheduledExecutorService WIDGET_EXECUTOR =
        Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "DeskTouch WidgetUpdate");
            t.setDaemon(true);
            t.setPriority(Thread.MIN_PRIORITY);
            return t;
        });
    private static final AtomicBoolean pendingScheduledUpdate = new AtomicBoolean(false);
    private static final AtomicBoolean widgetsDisabled = new AtomicBoolean(false);
    private static volatile String lastRenderSignature = "";
    private static Toast activeToast;

    @Override
    public void onEnabled(Context context) {
        super.onEnabled(context);
        widgetsDisabled.set(false);
        lastRenderSignature = "";
        scheduleUpdateIfChanged(context);
    }

    @Override
    public void onDisabled(Context context) {
        super.onDisabled(context);
        widgetsDisabled.set(true);
        lastRenderSignature = "";
    }

    @Override
    public void onUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds) {
        if (context == null || appWidgetManager == null || appWidgetIds == null || appWidgetIds.length == 0) {
            return;
        }
        widgetsDisabled.set(false);
        Context app = context.getApplicationContext();
        try {
            RemoteViews views = buildViews(app);
            for (int appWidgetId : appWidgetIds) {
                appWidgetManager.updateAppWidget(appWidgetId, views);
            }
        } catch (Throwable ignored) {
            try {
                RemoteViews fallback = new RemoteViews(app.getPackageName(), R.layout.touchpad_home_widget);
                for (int appWidgetId : appWidgetIds) {
                    appWidgetManager.updateAppWidget(appWidgetId, fallback);
                }
            } catch (Throwable ignoredFallback) {}
        }
    }

    @Override
    public void onAppWidgetOptionsChanged(
            Context context, AppWidgetManager appWidgetManager, int appWidgetId, Bundle newOptions) {
        try {
            super.onAppWidgetOptionsChanged(context, appWidgetManager, appWidgetId, newOptions);
            scheduleUpdateIfChanged(context);
        } catch (Throwable ignored) {}
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        try {
            super.onReceive(context, intent);
            if (context == null || intent == null || intent.getAction() == null) return;
            Context app = context.getApplicationContext();
            String action = intent.getAction();

            if (ACTION_REFRESH.equals(action)) {
                AutoConnectService.nudgeReconnect(app);
                updateAll(app);
                return;
            }

            if (ACTION_TOGGLE_PC.equals(action)) {
                int clickedId = intent.getIntExtra(EXTRA_TARGET_ID, TouchpadWidgetHelper.UNSELECTED);
                String clickedName = intent.getStringExtra(EXTRA_TARGET_NAME);
                if (clickedName == null || clickedName.isEmpty()) clickedName = "PC";

                DeskTouchSession active = DeskTouchRuntime.session(app);
                DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(app);
                boolean connected = snap != null && snap.connected;
                if (!connected || clickedId < 0) {
                    showToast(app, "Connect to a computer in DeskTouch Find first");
                    updateAll(app);
                    return;
                }

                List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
                    active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
                int curSelected = TouchpadWidgetHelper.resolveEffectiveSelection(
                    TouchpadWidgetHelper.loadSelectedTargetId(app),
                    TouchpadWidgetHelper.isExplicitlyUnselected(app),
                    pcs);
                int nextSelected = TouchpadWidgetHelper.togglePcSelection(curSelected, clickedId);
                if (nextSelected == TouchpadWidgetHelper.UNSELECTED) {
                    TouchpadWidgetHelper.saveSelectedTarget(app, TouchpadWidgetHelper.UNSELECTED, true);
                    active.releaseControl();
                    showToast(app, "Unselected " + clickedName);
                } else {
                    TouchpadWidgetHelper.saveSelectedTarget(app, nextSelected, false);
                    active.switchTargetAndClaim(nextSelected, 0);
                    showToast(app, "Selected → " + clickedName);
                }
                updateAll(app);
                DeskTouchRuntime.notifyLiveStatusChanged();
                return;
            }

            if (ACTION_MOUSE_CLICK.equals(action) || ACTION_SCROLL.equals(action)) {
                DeskTouchSession active = DeskTouchRuntime.session(app);
                DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(app);
                if (snap == null || !snap.connected) {
                    showToast(app, "No computer connected — open DeskTouch Find");
                    updateAll(app);
                    return;
                }
                List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
                    active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
                int selectedId = TouchpadWidgetHelper.resolveEffectiveSelection(
                    TouchpadWidgetHelper.loadSelectedTargetId(app),
                    TouchpadWidgetHelper.isExplicitlyUnselected(app),
                    pcs);
                if (selectedId == TouchpadWidgetHelper.UNSELECTED) {
                    showToast(app, "Select a computer above first");
                    updateAll(app);
                    return;
                }
                if (active.selectedEnvironmentTarget() != selectedId || (!active.isActive() && !active.hasClaim())) {
                    active.switchTargetAndClaim(selectedId, 0);
                }
                if (ACTION_MOUSE_CLICK.equals(action)) {
                    int btn = intent.getIntExtra(EXTRA_BUTTON, 1);
                    active.click(btn);
                } else {
                    int wheel = intent.getIntExtra(EXTRA_WHEEL, 120);
                    active.wheel(wheel, false);
                }
            }
        } catch (Throwable ignored) {}
    }

    static void updateAll(Context context) {
        if (context == null) return;
        final Context app = context.getApplicationContext();
        widgetsDisabled.set(false);
        lastRenderSignature = "";
        try {
            WIDGET_EXECUTOR.execute(() -> updateAllInternal(app, true));
        } catch (Throwable ignored) {}
    }

    static void updateAllIfChanged(Context context) {
        scheduleUpdateIfChanged(context);
    }

    static void scheduleUpdateIfChanged(Context context) {
        if (context == null || widgetsDisabled.get()) return;
        if (!pendingScheduledUpdate.compareAndSet(false, true)) return;
        final Context app = context.getApplicationContext();
        try {
            WIDGET_EXECUTOR.schedule(() -> {
                pendingScheduledUpdate.set(false);
                if (!widgetsDisabled.get()) {
                    updateAllInternal(app, false);
                }
            }, 300L, TimeUnit.MILLISECONDS);
        } catch (Throwable ignored) {
            pendingScheduledUpdate.set(false);
        }
    }

    private static void updateAllInternal(Context app, boolean force) {
        synchronized (WIDGET_UPDATE_LOCK) {
            try {
                AppWidgetManager manager = AppWidgetManager.getInstance(app);
                if (manager == null) return;
                int[] ids = manager.getAppWidgetIds(new ComponentName(app, TouchpadWidgetProvider.class));
                if (ids == null || ids.length == 0) {
                    if (!force) widgetsDisabled.set(true);
                    return;
                }
                widgetsDisabled.set(false);

                WidgetModel model = buildModel(app);
                if (!force && model.signature.equals(lastRenderSignature)) return;
                lastRenderSignature = model.signature;

                RemoteViews views = applyModelToViews(app, model);
                for (int id : ids) {
                    manager.updateAppWidget(id, views);
                }
            } catch (Throwable ignored) {}
        }
    }

    static boolean requestPin(Context context) {
        if (context == null) return false;
        widgetsDisabled.set(false);
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        if (manager == null || !manager.isRequestPinAppWidgetSupported()) {
            return false;
        }
        return manager.requestPinAppWidget(
            new ComponentName(context, TouchpadWidgetProvider.class), null, null);
    }

    private static RemoteViews buildViews(Context context) {
        WidgetModel model = buildModel(context);
        lastRenderSignature = model.signature;
        return applyModelToViews(context, model);
    }

    private static final class WidgetModel {
        final boolean connected;
        final String linkLabel;
        final boolean badgeOk;
        final List<LinkedDisplayHelper.DisplayTileModel> pcs;
        final int effectiveSelectedId;
        final String selectedName;
        final boolean explicitUnselected;
        final boolean horizontalPref;
        final String signature;

        WidgetModel(
                boolean connected,
                String linkLabel,
                boolean badgeOk,
                List<LinkedDisplayHelper.DisplayTileModel> pcs,
                int effectiveSelectedId,
                String selectedName,
                boolean explicitUnselected,
                boolean horizontalPref,
                String signature) {
            this.connected = connected;
            this.linkLabel = linkLabel;
            this.badgeOk = badgeOk;
            this.pcs = pcs;
            this.effectiveSelectedId = effectiveSelectedId;
            this.selectedName = selectedName;
            this.explicitUnselected = explicitUnselected;
            this.horizontalPref = horizontalPref;
            this.signature = signature;
        }
    }

    private static WidgetModel buildModel(Context context) {
        DeskTouchSession active = DeskTouchRuntime.session(context);
        DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(context);
        boolean connected = snap != null && snap.connected;
        List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
            active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
        boolean explicitUnselected = TouchpadWidgetHelper.isExplicitlyUnselected(context);
        int storedId = TouchpadWidgetHelper.loadSelectedTargetId(context);
        int effectiveId = connected
            ? TouchpadWidgetHelper.resolveEffectiveSelection(storedId, explicitUnselected, pcs)
            : TouchpadWidgetHelper.UNSELECTED;

        String selectedName = "";
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            if (pc.id == effectiveId) {
                selectedName = pc.shortName;
                break;
            }
        }

        String linkLabel;
        boolean badgeOk;
        if (!connected) {
            linkLabel = "Offline";
            badgeOk = false;
        } else if (effectiveId == TouchpadWidgetHelper.UNSELECTED) {
            linkLabel = explicitUnselected ? "No PC selected" : "Ready";
            badgeOk = !explicitUnselected;
        } else {
            boolean fastUdp = DeskTouchRuntime.hasActiveMobileUdpStream()
                || snap.mobConfirmed
                || (snap.rttMs > 0 && snap.rttMs < 500);
            linkLabel = fastUdp ? "Fast UDP" : "Connected";
            badgeOk = true;
        }

        boolean horizPref = TouchpadWidgetHelper.isWidgetHorizontal(context);
        StringBuilder sig = new StringBuilder();
        sig.append(connected ? '1' : '0').append('|')
            .append(linkLabel).append('|')
            .append(effectiveId).append('|')
            .append(explicitUnselected ? '1' : '0').append('|')
            .append(horizPref ? '1' : '0');
        for (int i = 0; i < Math.min(2, pcs.size()); i++) {
            sig.append(';').append(pcs.get(i).id).append(':').append(pcs.get(i).shortName);
        }

        return new WidgetModel(
            connected, linkLabel, badgeOk, pcs, effectiveId, selectedName,
            explicitUnselected, horizPref, sig.toString());
    }

    private static RemoteViews applyModelToViews(Context context, WidgetModel model) {
        RemoteViews views = new RemoteViews(
            context.getPackageName(), R.layout.touchpad_home_widget);

        views.setTextViewText(R.id.widget_link_badge, model.linkLabel);
        views.setInt(R.id.widget_link_badge, "setBackgroundResource",
            model.badgeOk ? R.drawable.touchpad_widget_badge_ok : R.drawable.touchpad_widget_badge_warn);
        views.setTextColor(R.id.widget_link_badge, model.badgeOk ? 0xFF34D399 : 0xFFFBBF24);

        if (!model.connected) {
            views.setTextViewText(R.id.widget_pc_1, "No PC connected • Tap Find");
            views.setInt(R.id.widget_pc_1, "setBackgroundResource", R.drawable.touchpad_widget_pc_idle);
            views.setTextColor(R.id.widget_pc_1, 0xFFCBD5E1);
            views.setViewVisibility(R.id.widget_pc_1, View.VISIBLE);
            views.setViewVisibility(R.id.widget_pc_2, View.GONE);
            Intent findIntent = new Intent(context, MainActivity.class)
                .setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP)
                .putExtra(MainActivity.EXTRA_OPEN_TAB, "find");
            PendingIntent findPending = PendingIntent.getActivity(
                context, 37410, findIntent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            views.setOnClickPendingIntent(R.id.widget_pc_1, findPending);
            views.setTextViewText(R.id.widget_pad_subtitle, "Open DeskTouch Find to connect your PC");
        } else if (model.pcs.isEmpty()) {
            views.setTextViewText(R.id.widget_pc_1, "Syncing PCs...");
            views.setInt(R.id.widget_pc_1, "setBackgroundResource", R.drawable.touchpad_widget_pc_idle);
            views.setTextColor(R.id.widget_pc_1, 0xFFCBD5E1);
            views.setViewVisibility(R.id.widget_pc_1, View.VISIBLE);
            views.setViewVisibility(R.id.widget_pc_2, View.GONE);
            Intent refreshIntent = new Intent(context, TouchpadWidgetProvider.class)
                .setAction(ACTION_REFRESH);
            PendingIntent refreshPending = PendingIntent.getBroadcast(
                context, 37410, refreshIntent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            views.setOnClickPendingIntent(R.id.widget_pc_1, refreshPending);
            views.setTextViewText(R.id.widget_pad_subtitle, "Waiting for Windows display roster");
        } else {
            bindPcPill(context, views, R.id.widget_pc_1, 37410, model.pcs.get(0), model.effectiveSelectedId);
            if (model.pcs.size() >= 2) {
                views.setViewVisibility(R.id.widget_pc_2, View.VISIBLE);
                bindPcPill(context, views, R.id.widget_pc_2, 37411, model.pcs.get(1), model.effectiveSelectedId);
            } else {
                views.setViewVisibility(R.id.widget_pc_2, View.GONE);
            }
            views.setTextViewText(
                R.id.widget_pad_subtitle,
                model.effectiveSelectedId != TouchpadWidgetHelper.UNSELECTED && !model.selectedName.isEmpty()
                    ? ("Controlling " + model.selectedName + " • Tap to drag")
                    : "Tap a computer above to select & control");
        }

        views.setOnClickPendingIntent(
            R.id.widget_touch_surface,
            overlayPendingIntent(context, 37412, TouchpadWidgetOverlayActivity.MODE_TOUCHPAD, model.horizontalPref));
        views.setOnClickPendingIntent(
            R.id.widget_btn_fullscreen,
            overlayPendingIntent(context, 37413, TouchpadWidgetOverlayActivity.MODE_FULLSCREEN, model.horizontalPref));
        views.setOnClickPendingIntent(
            R.id.widget_btn_horizontal,
            overlayPendingIntent(context, 37414, TouchpadWidgetOverlayActivity.MODE_TOUCHPAD, !model.horizontalPref));
        views.setOnClickPendingIntent(
            R.id.widget_btn_keys,
            overlayPendingIntent(context, 37415, TouchpadWidgetOverlayActivity.MODE_KEYS, model.horizontalPref));
        views.setOnClickPendingIntent(
            R.id.widget_btn_ctrl,
            overlayPendingIntent(context, 37416, TouchpadWidgetOverlayActivity.MODE_CTRL, model.horizontalPref));

        views.setOnClickPendingIntent(
            R.id.widget_btn_left, clickBroadcastIntent(context, 37417, 1));
        views.setOnClickPendingIntent(
            R.id.widget_btn_right, clickBroadcastIntent(context, 37418, 2));
        views.setOnClickPendingIntent(
            R.id.widget_btn_up, scrollBroadcastIntent(context, 37419, 120));
        views.setOnClickPendingIntent(
            R.id.widget_btn_down, scrollBroadcastIntent(context, 37420, -120));

        return views;
    }

    private static void bindPcPill(
            Context context,
            RemoteViews views,
            int viewId,
            int reqCode,
            LinkedDisplayHelper.DisplayTileModel pc,
            int effectiveSelectedId) {
        boolean active = pc.id == effectiveSelectedId;
        views.setTextViewText(viewId, (active ? "● " : "○ ") + pc.shortName);
        views.setInt(viewId, "setBackgroundResource",
            active ? R.drawable.touchpad_widget_pc_active : R.drawable.touchpad_widget_pc_idle);
        views.setTextColor(viewId, active ? 0xFFFFFFFF : 0xFFCBD5E1);
        Intent intent = new Intent(context, TouchpadWidgetProvider.class)
            .setAction(ACTION_TOGGLE_PC)
            .putExtra(EXTRA_TARGET_ID, pc.id)
            .putExtra(EXTRA_TARGET_NAME, pc.shortName);
        PendingIntent pending = PendingIntent.getBroadcast(
            context, reqCode, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(viewId, pending);
    }

    private static PendingIntent overlayPendingIntent(
            Context context, int reqCode, String mode, boolean horizontal) {
        Intent intent = new Intent(context, TouchpadWidgetOverlayActivity.class)
            .setFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(TouchpadWidgetOverlayActivity.EXTRA_MODE, mode)
            .putExtra(TouchpadWidgetOverlayActivity.EXTRA_HORIZONTAL, horizontal);
        return PendingIntent.getActivity(
            context, reqCode, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static PendingIntent clickBroadcastIntent(Context context, int reqCode, int button) {
        Intent intent = new Intent(context, TouchpadWidgetProvider.class)
            .setAction(ACTION_MOUSE_CLICK)
            .putExtra(EXTRA_BUTTON, button);
        return PendingIntent.getBroadcast(
            context, reqCode, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static PendingIntent scrollBroadcastIntent(Context context, int reqCode, int wheelAmount) {
        Intent intent = new Intent(context, TouchpadWidgetProvider.class)
            .setAction(ACTION_SCROLL)
            .putExtra(EXTRA_WHEEL, wheelAmount);
        return PendingIntent.getBroadcast(
            context, reqCode, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static void showToast(Context app, String msg) {
        MAIN.post(() -> {
            if (activeToast != null) activeToast.cancel();
            activeToast = Toast.makeText(app, msg, Toast.LENGTH_SHORT);
            activeToast.show();
        });
    }
}
