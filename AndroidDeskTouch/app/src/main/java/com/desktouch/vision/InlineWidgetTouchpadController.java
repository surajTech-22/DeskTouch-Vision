package com.desktouch.vision;

import android.app.KeyguardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Handler;
import android.os.PowerManager;
import android.os.SystemClock;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import java.util.ArrayDeque;
import java.util.List;

/**
 * Attaches an in-place transparent TYPE_ACCESSIBILITY_OVERLAY directly over the
 * 4x4 Home Screen widget's touchpad area when—and only when—a connected PC is
 * selected to control and the user is viewing the Home launcher. Supports direct
 * multi-touch trackpad gestures, overlapping live Full Screen preview, and the
 * single-row Ctrl+ shortcuts bar without opening a second widget or Activity.
 */
final class InlineWidgetTouchpadController {
    private static final String VIEW_ID_SURFACE = "com.desktouch.vision:id/widget_touch_surface";
    private static final String VIEW_ID_FULLSCREEN = "com.desktouch.vision:id/widget_btn_fullscreen";
    private static final int MAX_DFS_NODES = 180;
    private static final int MAX_DFS_DEPTH = 16;

    private static final class WidgetPadGeometry {
        final Rect padRect;
        final Rect fsLocalRect;

        WidgetPadGeometry(Rect padRect, Rect fsLocalRect) {
            this.padRect = padRect;
            this.fsLocalRect = fsLocalRect;
        }
    }

    private static final class ResolvedTarget {
        final DeskTouchSession session;
        final int targetId;
        final String targetName;

        ResolvedTarget(DeskTouchSession session, int targetId, String targetName) {
            this.session = session;
            this.targetId = targetId;
            this.targetName = targetName;
        }
    }

    private final DeskTouchAccessibilityService service;
    private final Handler ui;
    private final WindowManager windows;

    private FrameLayout rootView;
    private View touchCanvas;
    private LinearLayout fsOverlayView;
    private TextView fsPcLabel;
    private ImageView fsPreviewImage;
    private LinearLayout ctrlFloatCard;
    private LinearLayout ctrlTabsRow;
    private LinearLayout ctrlItemsRow;
    private WindowManager.LayoutParams layoutParams;

    private final Rect attachedPadRect = new Rect();
    private final Rect fsLocalRect = new Rect();
    private final Rect lastHintRect = new Rect();

    private boolean attached;
    private boolean fullscreenMode;
    private boolean ctrlVisible;
    private String activeModCat = TouchpadWidgetHelper.CAT_CTRL;
    private volatile boolean reverseRemoteActive;

    private String cachedLauncherPkg = "";
    private long cachedLauncherAtMs;

    private DeskTouchSession previewWatchSession;
    private Bitmap lastRenderedBmp;
    private Toast activeToast;

    private DeskTouchSession activeDragSession;
    private float pointerSpeed = 2.8f;
    private float prevX, prevY, downX, downY, twoFingerPrevY, twoFingerScrollCarry, pinchPrevSpan;
    private float threeFingerStartX, threeFingerStartY;
    private boolean threeFingerHandled, dragged, twoFingerMoved;
    private int maxPointerCount;
    private float touchGlowX, touchGlowY;
    private boolean touchGlowActive;

    private boolean syncScheduled;
    private final Runnable syncRunnable;
    private final Runnable delayedResyncRunnable;

    InlineWidgetTouchpadController(
            DeskTouchAccessibilityService service, Handler ui, WindowManager windows) {
        this.service = service;
        this.ui = ui;
        this.windows = windows;
        this.syncRunnable = () -> {
            syncScheduled = false;
            sync(this.service);
        };
        this.delayedResyncRunnable = () -> sync(this.service);
    }

    boolean isAttached() {
        return attached;
    }

    void onWindowStateChanged(CharSequence packageName) {
        if (!isHomeLauncherPackage(packageName)) {
            lastHintRect.setEmpty();
            detach();
            return;
        }
        scheduleSync(120L, true);
    }

    void onViewScrolled(CharSequence packageName) {
        if (attached || isHomeLauncherPackage(packageName)) {
            lastHintRect.setEmpty();
            scheduleSync(90L, true);
        }
    }

    void onReverseRemoteStateChanged(boolean remoteActive) {
        reverseRemoteActive = remoteActive;
        if (remoteActive) {
            detach();
        } else {
            scheduleSync(140L, false);
        }
    }

    private void scheduleSync(long delayMs, boolean followUp) {
        if (!syncScheduled) {
            syncScheduled = true;
            ui.postDelayed(syncRunnable, delayMs);
        }
        if (followUp) {
            ui.removeCallbacks(delayedResyncRunnable);
            ui.postDelayed(delayedResyncRunnable, Math.max(260L, delayMs + 220L));
        }
    }

    void sync(Context context) {
        if (context == null || windows == null) {
            detach();
            return;
        }
        ResolvedTarget resolved = resolveSelectedTarget(context, true);
        if (resolved == null) {
            detach();
            return;
        }
        WidgetPadGeometry geo = findWidgetPadGeometry();
        if (geo == null && !lastHintRect.isEmpty() && isOnHomeLauncherScreen()) {
            Rect copy = new Rect(lastHintRect);
            if (isValidRectForCurrentScreen(copy)) {
                geo = new WidgetPadGeometry(copy, defaultFsLocalRect(copy));
            }
        }
        if (geo == null) {
            detach();
            return;
        }
        attachOrUpdateOverlay(geo.padRect, geo.fsLocalRect, resolved);
    }

    boolean activateAt(Context context, Rect sourceBoundsHint) {
        if (context == null || windows == null) return false;
        ResolvedTarget resolved = resolveSelectedTarget(context, false);
        if (resolved == null) {
            detach();
            return false;
        }
        if (sourceBoundsHint != null && isValidRectForCurrentScreen(sourceBoundsHint)) {
            lastHintRect.set(sourceBoundsHint);
        }
        WidgetPadGeometry geo = findWidgetPadGeometry();
        if (geo == null && sourceBoundsHint != null && isValidRectForCurrentScreen(sourceBoundsHint)) {
            geo = new WidgetPadGeometry(new Rect(sourceBoundsHint), defaultFsLocalRect(sourceBoundsHint));
        }
        if (geo == null) {
            return false;
        }
        attachOrUpdateOverlay(geo.padRect, geo.fsLocalRect, resolved);
        return true;
    }

    boolean toggleFullscreen(Context context, Rect sourceBoundsHint) {
        if (context == null || windows == null) return false;
        ResolvedTarget resolved = resolveSelectedTarget(context, false);
        if (resolved == null) {
            detach();
            return false;
        }
        if (!attached) {
            if (!activateAt(context, sourceBoundsHint)) return false;
        }
        fullscreenMode = !fullscreenMode;
        if (fullscreenMode) {
            ctrlVisible = false;
            startPreviewWatch(resolved.session, resolved.targetId, resolved.targetName);
        } else {
            stopPreviewWatch();
        }
        syncLayerVisibility(resolved.targetName);
        return true;
    }

    boolean toggleCtrl(Context context, Rect sourceBoundsHint) {
        if (context == null || windows == null) return false;
        ResolvedTarget resolved = resolveSelectedTarget(context, false);
        if (resolved == null) {
            detach();
            return false;
        }
        if (!attached) {
            if (!activateAt(context, sourceBoundsHint)) return false;
        }
        ctrlVisible = !ctrlVisible;
        if (ctrlVisible) {
            rebuildCtrlCategoryBarAndRow();
        }
        syncLayerVisibility(resolved.targetName);
        return true;
    }

    void detach() {
        syncScheduled = false;
        ui.removeCallbacks(syncRunnable);
        ui.removeCallbacks(delayedResyncRunnable);
        stopPreviewWatch();
        fullscreenMode = false;
        ctrlVisible = false;
        touchGlowActive = false;
        activeDragSession = null;
        attachedPadRect.setEmpty();
        if (attached && rootView != null && windows != null) {
            try {
                windows.removeViewImmediate(rootView);
            } catch (RuntimeException ignored) {
                try {
                    windows.removeView(rootView);
                } catch (RuntimeException ignoredSecondary) {}
            }
        }
        attached = false;
        rootView = null;
        touchCanvas = null;
        fsOverlayView = null;
        fsPcLabel = null;
        fsPreviewImage = null;
        ctrlFloatCard = null;
        ctrlTabsRow = null;
        ctrlItemsRow = null;
        layoutParams = null;
    }

    private ResolvedTarget resolveSelectedTarget(Context context, boolean requireHomeScreen) {
        if (context == null) return null;
        if (requireHomeScreen && !isOnHomeLauncherScreen()) return null;
        DeskTouchSession active = DeskTouchRuntime.session(context);
        DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(context);
        boolean connected = snap != null && snap.connected;
        List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
            active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
        boolean explicitUnselected = TouchpadWidgetHelper.isExplicitlyUnselected(context);
        int effectiveId = connected
            ? TouchpadWidgetHelper.resolveEffectiveSelection(
                TouchpadWidgetHelper.loadSelectedTargetId(context), explicitUnselected, pcs)
            : TouchpadWidgetHelper.UNSELECTED;
        boolean onHome = !requireHomeScreen || isOnHomeLauncherScreen();
        if (!TouchpadWidgetHelper.shouldEnableInlineTouchpad(
                connected, effectiveId, explicitUnselected, onHome, reverseRemoteActive)) {
            return null;
        }
        String name = "PC";
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            if (pc.id == effectiveId && pc.shortName != null && !pc.shortName.isEmpty()) {
                name = pc.shortName;
                break;
            }
        }
        return new ResolvedTarget(active, effectiveId, name);
    }

    private boolean isHomeLauncherPackage(CharSequence packageName) {
        if (packageName == null) return false;
        String pkg = packageName.toString();
        if (pkg.isEmpty() || "com.android.systemui".equals(pkg) || "com.desktouch.vision".equals(pkg)) {
            return false;
        }
        String homePkg = resolveHomeLauncherPackage();
        return !homePkg.isEmpty() && homePkg.equals(pkg);
    }

    private String resolveHomeLauncherPackage() {
        long now = SystemClock.uptimeMillis();
        if (!cachedLauncherPkg.isEmpty() && now - cachedLauncherAtMs < 30_000L) {
            return cachedLauncherPkg;
        }
        try {
            PackageManager pm = service.getPackageManager();
            Intent intent = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME);
            ResolveInfo info = pm.resolveActivity(intent, PackageManager.MATCH_DEFAULT_ONLY);
            if (info != null && info.activityInfo != null && info.activityInfo.packageName != null) {
                cachedLauncherPkg = info.activityInfo.packageName;
                cachedLauncherAtMs = now;
            }
        } catch (RuntimeException ignored) {}
        return cachedLauncherPkg;
    }

    private boolean isOnHomeLauncherScreen() {
        try {
            PowerManager pm = (PowerManager) service.getSystemService(Context.POWER_SERVICE);
            if (pm != null && !pm.isInteractive()) return false;
        } catch (RuntimeException ignored) {}
        try {
            KeyguardManager km = (KeyguardManager) service.getSystemService(Context.KEYGUARD_SERVICE);
            if (km != null && km.isKeyguardLocked()) return false;
        } catch (RuntimeException ignored) {}
        try {
            AccessibilityNodeInfo activeRoot = service.getRootInActiveWindow();
            if (activeRoot != null) {
                return isHomeLauncherPackage(activeRoot.getPackageName());
            }
        } catch (RuntimeException ignored) {}
        try {
            List<AccessibilityWindowInfo> winList = service.getWindows();
            if (winList != null) {
                for (AccessibilityWindowInfo win : winList) {
                    if (win == null) continue;
                    AccessibilityNodeInfo root = win.getRoot();
                    if (root != null && isHomeLauncherPackage(root.getPackageName())
                        && win.getType() == AccessibilityWindowInfo.TYPE_APPLICATION) {
                        return true;
                    }
                }
            }
        } catch (RuntimeException ignored) {}
        return false;
    }

    private WidgetPadGeometry findWidgetPadGeometry() {
        String launcherPkg = resolveHomeLauncherPackage();
        if (launcherPkg.isEmpty()) return null;

        Rect boundsOut = new Rect();
        Rect fsOut = new Rect();

        try {
            AccessibilityNodeInfo activeRoot = service.getRootInActiveWindow();
            if (activeRoot != null) {
                if (!launcherPkg.equals(String.valueOf(activeRoot.getPackageName()))) {
                    return null;
                }
                if (findSurfaceInTree(activeRoot, boundsOut, fsOut)) {
                    return new WidgetPadGeometry(boundsOut, fsOut);
                }
            }
        } catch (RuntimeException ignored) {}

        try {
            List<AccessibilityWindowInfo> winList = service.getWindows();
            if (winList != null) {
                for (AccessibilityWindowInfo win : winList) {
                    if (win == null || win.getType() != AccessibilityWindowInfo.TYPE_APPLICATION) continue;
                    AccessibilityNodeInfo root = win.getRoot();
                    if (root == null || !launcherPkg.equals(String.valueOf(root.getPackageName()))) {
                        continue;
                    }
                    if (findSurfaceInTree(root, boundsOut, fsOut)) {
                        return new WidgetPadGeometry(boundsOut, fsOut);
                    }
                }
            }
        } catch (RuntimeException ignored) {}
        return null;
    }

    private boolean findSurfaceInTree(AccessibilityNodeInfo root, Rect padOut, Rect fsLocalOut) {
        if (root == null) return false;
        AccessibilityNodeInfo surfaceNode = null;
        AccessibilityNodeInfo fsNode = null;

        try {
            List<AccessibilityNodeInfo> byId = root.findAccessibilityNodeInfosByViewId(VIEW_ID_SURFACE);
            if (byId != null) {
                Rect candidate = new Rect();
                for (AccessibilityNodeInfo node : byId) {
                    if (node == null || !node.isVisibleToUser()) continue;
                    node.getBoundsInScreen(candidate);
                    if (isValidRectForCurrentScreen(candidate)) {
                        surfaceNode = node;
                        padOut.set(candidate);
                        break;
                    }
                }
            }
        } catch (RuntimeException ignored) {}

        if (surfaceNode == null) {
            surfaceNode = dfsFindSurface(root, padOut);
        }
        if (surfaceNode == null) return false;

        try {
            List<AccessibilityNodeInfo> fsList = root.findAccessibilityNodeInfosByViewId(VIEW_ID_FULLSCREEN);
            if (fsList != null) {
                Rect r = new Rect();
                for (AccessibilityNodeInfo n : fsList) {
                    if (n == null || !n.isVisibleToUser()) continue;
                    n.getBoundsInScreen(r);
                    if (padOut.contains(r.centerX(), r.centerY())) {
                        fsNode = n;
                        break;
                    }
                }
            }
        } catch (RuntimeException ignored) {}

        if (fsNode == null) {
            fsNode = dfsFindFullscreenNode(root, padOut);
        }

        if (fsNode != null) {
            Rect absFs = new Rect();
            fsNode.getBoundsInScreen(absFs);
            int padSlop = dp(6);
            fsLocalOut.set(
                Math.max(0, absFs.left - padOut.left - padSlop),
                Math.max(0, absFs.top - padOut.top - padSlop),
                Math.min(padOut.width(), absFs.right - padOut.left + padSlop),
                Math.min(padOut.height(), absFs.bottom - padOut.top + padSlop));
        } else {
            fsLocalOut.set(defaultFsLocalRect(padOut));
        }
        return true;
    }

    private AccessibilityNodeInfo dfsFindSurface(AccessibilityNodeInfo root, Rect padOut) {
        ArrayDeque<AccessibilityNodeInfo> stack = new ArrayDeque<>();
        ArrayDeque<Integer> depths = new ArrayDeque<>();
        stack.push(root);
        depths.push(0);
        int visited = 0;
        Rect r = new Rect();
        while (!stack.isEmpty() && ++visited <= MAX_DFS_NODES) {
            AccessibilityNodeInfo node = stack.pop();
            int depth = depths.pop();
            if (node == null || depth > MAX_DFS_DEPTH) continue;
            if (!node.isVisibleToUser()) continue;

            String viewId = node.getViewIdResourceName();
            if (viewId != null && viewId.endsWith(":id/widget_touch_surface")) {
                node.getBoundsInScreen(r);
                if (isValidRectForCurrentScreen(r)) {
                    padOut.set(r);
                    return node;
                }
            }
            CharSequence txt = node.getText();
            if (txt != null && "Live Touchpad".contentEquals(txt)) {
                AccessibilityNodeInfo parent = node.getParent();
                if (parent != null && parent.isVisibleToUser()) {
                    parent.getBoundsInScreen(r);
                    if (isValidRectForCurrentScreen(r)) {
                        padOut.set(r);
                        return parent;
                    }
                }
            }

            for (int i = node.getChildCount() - 1; i >= 0; i--) {
                AccessibilityNodeInfo child = node.getChild(i);
                if (child != null) {
                    stack.push(child);
                    depths.push(depth + 1);
                }
            }
        }
        return null;
    }

    private AccessibilityNodeInfo dfsFindFullscreenNode(AccessibilityNodeInfo root, Rect padOut) {
        ArrayDeque<AccessibilityNodeInfo> stack = new ArrayDeque<>();
        ArrayDeque<Integer> depths = new ArrayDeque<>();
        stack.push(root);
        depths.push(0);
        int visited = 0;
        Rect r = new Rect();
        while (!stack.isEmpty() && ++visited <= MAX_DFS_NODES) {
            AccessibilityNodeInfo node = stack.pop();
            int depth = depths.pop();
            if (node == null || depth > MAX_DFS_DEPTH || !node.isVisibleToUser()) continue;

            String viewId = node.getViewIdResourceName();
            boolean idMatch = viewId != null && viewId.endsWith(":id/widget_btn_fullscreen");
            CharSequence txt = node.getText();
            boolean textMatch = txt != null && txt.toString().contains("Full Screen");
            if (idMatch || textMatch) {
                node.getBoundsInScreen(r);
                if (padOut.contains(r.centerX(), r.centerY())) {
                    return node;
                }
            }
            for (int i = node.getChildCount() - 1; i >= 0; i--) {
                AccessibilityNodeInfo child = node.getChild(i);
                if (child != null) {
                    stack.push(child);
                    depths.push(depth + 1);
                }
            }
        }
        return null;
    }

    private boolean isValidRectForCurrentScreen(Rect rect) {
        if (rect == null) return false;
        int sw = service.getResources().getDisplayMetrics().widthPixels;
        int sh = service.getResources().getDisplayMetrics().heightPixels;
        return TouchpadWidgetHelper.isValidInlinePadBounds(
            rect.left, rect.top, rect.right, rect.bottom, sw, sh, dp(120), dp(80));
    }

    private Rect defaultFsLocalRect(Rect padRect) {
        int w = padRect.width();
        return new Rect(Math.max(0, w - dp(112)), 0, w, dp(46));
    }

    private void attachOrUpdateOverlay(Rect padRect, Rect nextFsLocal, ResolvedTarget resolved) {
        fsLocalRect.set(nextFsLocal);
        if (!attached || rootView == null) {
            buildOverlayViews(resolved.targetName);
            WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
                padRect.width(),
                padRect.height(),
                WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL
                    | WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH
                    | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                    | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                    | WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
                PixelFormat.TRANSLUCENT);
            lp.gravity = Gravity.TOP | Gravity.LEFT;
            lp.x = padRect.left;
            lp.y = padRect.top;
            if (Build.VERSION.SDK_INT >= 28) {
                lp.layoutInDisplayCutoutMode =
                    WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            }
            if (Build.VERSION.SDK_INT >= 30) lp.setFitInsetsTypes(0);
            try {
                windows.addView(rootView, lp);
                layoutParams = lp;
                attachedPadRect.set(padRect);
                attached = true;
            } catch (RuntimeException rejected) {
                detach();
                return;
            }
        } else if (!attachedPadRect.equals(padRect) && layoutParams != null) {
            layoutParams.x = padRect.left;
            layoutParams.y = padRect.top;
            layoutParams.width = padRect.width();
            layoutParams.height = padRect.height();
            try {
                windows.updateViewLayout(rootView, layoutParams);
                attachedPadRect.set(padRect);
            } catch (RuntimeException ignored) {
                detach();
                return;
            }
        }
        syncLayerVisibility(resolved.targetName);
    }

    private void buildOverlayViews(String targetName) {
        rootView = new FrameLayout(service);
        rootView.setBackgroundColor(Color.TRANSPARENT);

        // Layer 1: Transparent inline touchpad surface capturing gestures in-place over widget
        touchCanvas = new View(service) {
            final Paint glowFill = new Paint(Paint.ANTI_ALIAS_FLAG);
            final Paint glowStroke = new Paint(Paint.ANTI_ALIAS_FLAG);

            {
                glowFill.setStyle(Paint.Style.FILL);
                glowFill.setColor(0x2E0EA5E9);
                glowStroke.setStyle(Paint.Style.STROKE);
                glowStroke.setStrokeWidth(Math.max(2f, dp(1.5f)));
                glowStroke.setColor(0xFF38BDF8);
            }

            @Override
            protected void onDraw(Canvas c) {
                if (touchGlowActive) {
                    c.drawCircle(touchGlowX, touchGlowY, dp(22), glowFill);
                    c.drawCircle(touchGlowX, touchGlowY, dp(12), glowStroke);
                }
            }
        };
        touchCanvas.setOnTouchListener(this::handleInlineTouchpadMotion);
        rootView.addView(touchCanvas, new FrameLayout.LayoutParams(-1, -1));

        // Layer 2: In-place Full Screen Preview overlapping the widget touchpad area
        fsOverlayView = new LinearLayout(service);
        fsOverlayView.setOrientation(LinearLayout.VERTICAL);
        fsOverlayView.setPadding(dp(7), dp(6), dp(7), dp(5));
        fsOverlayView.setBackground(
            PhoneUiKit.roundedBox(0xFF0B1220, 0xCC0EA5E9, dp(14), Math.max(1, dp(1))));
        fsOverlayView.setVisibility(View.GONE);

        LinearLayout fsTop = new LinearLayout(service);
        fsTop.setOrientation(LinearLayout.HORIZONTAL);
        fsTop.setGravity(Gravity.CENTER_VERTICAL);
        fsPcLabel = new TextView(service);
        fsPcLabel.setTextColor(0xFF34D399);
        fsPcLabel.setTextSize(10f);
        fsPcLabel.setTypeface(null, Typeface.BOLD);
        fsPcLabel.setSingleLine(true);
        fsPcLabel.setEllipsize(TextUtils.TruncateAt.END);
        fsTop.addView(fsPcLabel, new LinearLayout.LayoutParams(0, -2, 1f));
        fsTop.addView(createChip("🖱 Touchpad", 0xFF0EA5E9, 0xFF7DD3FC, Color.WHITE, () -> {
            fullscreenMode = false;
            stopPreviewWatch();
            syncLayerVisibility(targetName);
        }), new LinearLayout.LayoutParams(-2, -2));
        fsOverlayView.addView(fsTop, new LinearLayout.LayoutParams(-1, -2));

        fsPreviewImage = new ImageView(service);
        fsPreviewImage.setScaleType(ImageView.ScaleType.FIT_CENTER);
        fsPreviewImage.setBackgroundColor(0xFF060B14);
        bindFullscreenImageTouch();
        LinearLayout.LayoutParams imgLp = new LinearLayout.LayoutParams(-1, 0, 1f);
        imgLp.topMargin = dp(4);
        imgLp.bottomMargin = dp(3);
        fsOverlayView.addView(fsPreviewImage, imgLp);

        TextView fsHint = new TextView(service);
        fsHint.setText("Tap screen to click • 2-finger / hold = Right");
        fsHint.setTextSize(9f);
        fsHint.setTextColor(0xFF94A3B8);
        fsHint.setGravity(Gravity.CENTER);
        fsOverlayView.addView(fsHint, new LinearLayout.LayoutParams(-1, -2));
        rootView.addView(fsOverlayView, new FrameLayout.LayoutParams(-1, -1));

        // Layer 3: Single-row floating Ctrl+ shortcuts card inside the widget touchpad bounds
        ctrlFloatCard = new LinearLayout(service);
        ctrlFloatCard.setOrientation(LinearLayout.VERTICAL);
        ctrlFloatCard.setPadding(dp(7), dp(6), dp(7), dp(6));
        ctrlFloatCard.setBackground(
            PhoneUiKit.roundedBox(0xF5090D16, 0xB3818CF8, dp(12), Math.max(1, dp(1))));
        ctrlFloatCard.setVisibility(View.GONE);
        ctrlFloatCard.setClickable(true);

        LinearLayout catBar = new LinearLayout(service);
        catBar.setOrientation(LinearLayout.HORIZONTAL);
        catBar.setGravity(Gravity.CENTER_VERTICAL);
        HorizontalScrollView catScroll = new HorizontalScrollView(service);
        catScroll.setHorizontalScrollBarEnabled(false);
        ctrlTabsRow = new LinearLayout(service);
        ctrlTabsRow.setOrientation(LinearLayout.HORIZONTAL);
        catScroll.addView(ctrlTabsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        catBar.addView(catScroll, new LinearLayout.LayoutParams(0, -2, 1f));
        catBar.addView(createChip("✕", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, () -> {
            ctrlVisible = false;
            syncLayerVisibility(targetName);
        }));
        ctrlFloatCard.addView(catBar, new LinearLayout.LayoutParams(-1, -2));

        HorizontalScrollView itemsScroll = new HorizontalScrollView(service);
        itemsScroll.setHorizontalScrollBarEnabled(false);
        ctrlItemsRow = new LinearLayout(service);
        ctrlItemsRow.setOrientation(LinearLayout.HORIZONTAL);
        itemsScroll.addView(ctrlItemsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        LinearLayout.LayoutParams itLp = new LinearLayout.LayoutParams(-1, -2);
        itLp.topMargin = dp(5);
        ctrlFloatCard.addView(itemsScroll, itLp);

        FrameLayout.LayoutParams ctrlLp = new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM);
        ctrlLp.leftMargin = dp(6);
        ctrlLp.rightMargin = dp(6);
        ctrlLp.bottomMargin = dp(6);
        rootView.addView(ctrlFloatCard, ctrlLp);

        rebuildCtrlCategoryBarAndRow();
    }

    private void syncLayerVisibility(String targetName) {
        if (fsOverlayView != null) {
            fsOverlayView.setVisibility(fullscreenMode ? View.VISIBLE : View.GONE);
        }
        if (fsPcLabel != null) {
            fsPcLabel.setText("● Live Screen • " + (targetName != null ? targetName : "PC"));
        }
        if (ctrlFloatCard != null) {
            ctrlFloatCard.setVisibility(ctrlVisible ? View.VISIBLE : View.GONE);
        }
    }

    private void rebuildCtrlCategoryBarAndRow() {
        if (ctrlTabsRow == null || ctrlItemsRow == null) return;
        ctrlTabsRow.removeAllViews();
        String[][] cats = {
            { TouchpadWidgetHelper.CAT_CTRL, "Ctrl" },
            { TouchpadWidgetHelper.CAT_ALT, "Alt" },
            { TouchpadWidgetHelper.CAT_WIN, "⊞ Win" },
            { TouchpadWidgetHelper.CAT_FN, "Fn" },
            { TouchpadWidgetHelper.CAT_NAV, "Nav" }
        };
        for (int i = 0; i < cats.length; i++) {
            final String key = cats[i][0];
            boolean on = key.equals(activeModCat);
            TextView tab = createChip(
                cats[i][1],
                on ? 0xFF6366F1 : 0xFF1E293B,
                on ? 0xFFA5B4FC : 0xFF334155,
                on ? Color.WHITE : 0xFFCBD5E1,
                () -> {
                    activeModCat = key;
                    rebuildCtrlCategoryBarAndRow();
                });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
            if (i > 0) lp.leftMargin = dp(5);
            ctrlTabsRow.addView(tab, lp);
        }

        ctrlItemsRow.removeAllViews();
        List<FullscreenPreviewLayoutHelper.ShortcutItem> items =
            TouchpadWidgetHelper.shortcutsForCategory(activeModCat);
        for (int i = 0; i < items.size(); i++) {
            final FullscreenPreviewLayoutHelper.ShortcutItem item = items.get(i);
            TextView btn = createChip(
                item.label,
                0xFF1E293B,
                0xFF475569,
                0xFFA5B4FC,
                () -> {
                    ResolvedTarget target = resolveSelectedTarget(service, false);
                    if (target == null) {
                        detach();
                        return;
                    }
                    ensureClaimed(target);
                    TouchpadWidgetHelper.sendWindowsKeyCombo(
                        target.session, item.modifierVks, item.mainVk, item.powerAction);
                    showToast("Sent " + item.label);
                });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
            if (i > 0) lp.leftMargin = dp(5);
            ctrlItemsRow.addView(btn, lp);
        }
    }

    private void startPreviewWatch(DeskTouchSession act, int targetId, String targetName) {
        if (act == null || !act.isConnected()) return;
        DeskTouchRuntime.setInlineWidgetPreviewListener(() -> ui.post(this::renderLatestPreview));
        previewWatchSession = act;
        act.setScreenPreviewWatch(Math.max(0, targetId), Math.max(0, act.activeMonitorIndex()), true);
        if (fsPcLabel != null) {
            fsPcLabel.setText("● Live Screen • " + (targetName != null ? targetName : "PC"));
        }
        renderLatestPreview();
    }

    private void stopPreviewWatch() {
        DeskTouchRuntime.setInlineWidgetPreviewListener(null);
        DeskTouchSession s = previewWatchSession;
        previewWatchSession = null;
        if (s != null && s.isConnected()) {
            s.setScreenPreviewWatch(-1, -1, false);
        }
        lastRenderedBmp = null;
        if (fsPreviewImage != null) {
            fsPreviewImage.setImageDrawable(null);
        }
    }

    private void renderLatestPreview() {
        if (!attached || !fullscreenMode || fsPreviewImage == null) return;
        DeskTouchSession act = previewWatchSession != null ? previewWatchSession : DeskTouchRuntime.session(service);
        Bitmap bmp = act != null ? act.currentScreenPreview() : null;
        if (bmp != null && !bmp.isRecycled() && bmp != lastRenderedBmp) {
            fsPreviewImage.setImageBitmap(bmp);
            lastRenderedBmp = bmp;
        }
    }

    private void loadPointerSpeed() {
        try {
            int savedProgress = service.getSharedPreferences("desktouch_prefs", Context.MODE_PRIVATE)
                .getInt("pointer_sensitivity_progress", 180);
            savedProgress = Math.max(0, Math.min(400, savedProgress));
            pointerSpeed = 1f + savedProgress / 100f;
        } catch (RuntimeException ignored) {}
    }

    private void ensureClaimed(ResolvedTarget target) {
        if (target == null || target.session == null) return;
        if (target.session.selectedEnvironmentTarget() != target.targetId
            || (!target.session.isActive() && !target.session.hasClaim())) {
            target.session.switchTargetAndClaim(target.targetId, 0);
        }
    }

    private boolean handleInlineTouchpadMotion(View v, MotionEvent event) {
        if (event.getActionMasked() == MotionEvent.ACTION_OUTSIDE) {
            lastHintRect.setEmpty();
            scheduleSync(80L, true);
            return false;
        }
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN: {
                if (ctrlVisible) {
                    ctrlVisible = false;
                    syncLayerVisibility(null);
                }
                ResolvedTarget resolved = resolveSelectedTarget(service, false);
                if (resolved == null) {
                    activeDragSession = null;
                    detach();
                    return false;
                }
                ensureClaimed(resolved);
                activeDragSession = resolved.session;
                loadPointerSpeed();
                prevX = downX = event.getX();
                prevY = downY = event.getY();
                touchGlowX = event.getX();
                touchGlowY = event.getY();
                touchGlowActive = true;
                v.invalidate();
                dragged = false;
                twoFingerMoved = false;
                maxPointerCount = 1;
                twoFingerScrollCarry = 0f;
                threeFingerHandled = false;
                return true;
            }
            case MotionEvent.ACTION_POINTER_DOWN:
                maxPointerCount = Math.max(maxPointerCount, event.getPointerCount());
                if (event.getPointerCount() >= 3) {
                    threeFingerStartX = avgPointerX(event, 3);
                    threeFingerStartY = avgPointerY(event, 3);
                    threeFingerHandled = false;
                } else if (event.getPointerCount() == 2) {
                    twoFingerPrevY = (event.getY(0) + event.getY(1)) * 0.5f;
                    pinchPrevSpan = pointerSpan(event);
                    twoFingerScrollCarry = 0f;
                }
                return true;
            case MotionEvent.ACTION_MOVE: {
                touchGlowX = event.getX();
                touchGlowY = event.getY();
                touchGlowActive = true;
                v.invalidate();
                DeskTouchSession act = activeDragSession;
                if (act == null || !act.isConnected()) return true;
                if (event.getPointerCount() >= 3) {
                    maxPointerCount = Math.max(maxPointerCount, event.getPointerCount());
                    if (!threeFingerHandled) {
                        float dx3 = avgPointerX(event, 3) - threeFingerStartX;
                        float dy3 = avgPointerY(event, 3) - threeFingerStartY;
                        int tid3 = Math.max(0, act.selectedEnvironmentTarget());
                        if (Math.abs(dx3) > dp(42) && Math.abs(dx3) > Math.abs(dy3)) {
                            act.sendMediaAction(tid3, "switch-window", dx3 > 0 ? 1 : -1);
                            threeFingerHandled = true;
                        } else if (dy3 < -dp(42) && Math.abs(dy3) > Math.abs(dx3)) {
                            act.sendMediaAction(tid3, "task-view", 0);
                            threeFingerHandled = true;
                        } else if (dy3 > dp(42) && Math.abs(dy3) > Math.abs(dx3)) {
                            act.sendMediaAction(tid3, "desktop", 0);
                            threeFingerHandled = true;
                        }
                    }
                    return true;
                }
                if (event.getPointerCount() == 2) {
                    maxPointerCount = Math.max(maxPointerCount, event.getPointerCount());
                    float span = pointerSpan(event);
                    float dSpan = span - pinchPrevSpan;
                    if (Math.abs(dSpan) > dp(20)) {
                        act.key(0x11, true);
                        act.wheel(dSpan > 0 ? 120 : -120, false);
                        act.key(0x11, false);
                        pinchPrevSpan = span;
                        twoFingerMoved = true;
                        twoFingerPrevY = (event.getY(0) + event.getY(1)) * 0.5f;
                        return true;
                    }
                    float avgY = (event.getY(0) + event.getY(1)) * 0.5f;
                    float sdy = avgY - twoFingerPrevY;
                    twoFingerPrevY = avgY;
                    if (Math.abs(sdy) > 0.5f) {
                        twoFingerScrollCarry += sdy;
                        if (Math.abs(twoFingerScrollCarry) > dp(6)) twoFingerMoved = true;
                        int step = dp(14);
                        while (Math.abs(twoFingerScrollCarry) >= step) {
                            act.wheel(twoFingerScrollCarry > 0 ? 120 : -120, false);
                            twoFingerScrollCarry -= twoFingerScrollCarry > 0 ? step : -step;
                        }
                    }
                    return true;
                }
                if (maxPointerCount > 1) return true;
                float dx = event.getX() - prevX;
                float dy = event.getY() - prevY;
                if (Math.abs(event.getX() - downX) + Math.abs(event.getY() - downY) > dp(9)) {
                    dragged = true;
                }
                act.moveBy(dx * pointerSpeed, dy * pointerSpeed);
                prevX = event.getX();
                prevY = event.getY();
                return true;
            }
            case MotionEvent.ACTION_UP: {
                touchGlowActive = false;
                v.invalidate();
                DeskTouchSession act = activeDragSession;
                activeDragSession = null;
                if (act != null && act.isConnected()) {
                    if (maxPointerCount >= 2) {
                        if (!twoFingerMoved) act.click(2);
                    } else if (!dragged) {
                        if (TouchpadWidgetHelper.isPointInsideRect(
                                downX, downY, fsLocalRect.left, fsLocalRect.top, fsLocalRect.right, fsLocalRect.bottom)
                            && TouchpadWidgetHelper.isPointInsideRect(
                                event.getX(), event.getY(), fsLocalRect.left, fsLocalRect.top, fsLocalRect.right, fsLocalRect.bottom)) {
                            toggleFullscreen(service, null);
                        } else {
                            act.click(1);
                        }
                    }
                }
                return true;
            }
            case MotionEvent.ACTION_CANCEL:
                touchGlowActive = false;
                v.invalidate();
                activeDragSession = null;
                return true;
            default:
                return true;
        }
    }

    private void bindFullscreenImageTouch() {
        final float[] fsDownXY = { 0f, 0f };
        final float[] fsTwoPrevY = { 0f };
        final float[] fsTwoCarry = { 0f };
        final boolean[] fsMoved = { false };
        final boolean[] fsMulti = { false };
        final int[] fsMaxPtr = { 1 };
        final long[] fsDownMs = { 0L };
        final DeskTouchSession[] fsSession = { null };

        fsPreviewImage.setOnTouchListener((iv, ev) -> {
            if (ev.getActionMasked() == MotionEvent.ACTION_OUTSIDE) {
                lastHintRect.setEmpty();
                scheduleSync(80L, true);
                return false;
            }
            if (iv.getWidth() <= 0 || iv.getHeight() <= 0) return true;
            if (ev.getActionMasked() == MotionEvent.ACTION_DOWN) {
                if (ctrlVisible) {
                    ctrlVisible = false;
                    syncLayerVisibility(null);
                }
                ResolvedTarget resolved = resolveSelectedTarget(service, false);
                if (resolved == null) {
                    fsSession[0] = null;
                    detach();
                    return true;
                }
                ensureClaimed(resolved);
                fsSession[0] = resolved.session;
            }
            DeskTouchSession act = fsSession[0];
            if (act == null || !act.isConnected()) return true;

            Bitmap curBmp = act.currentScreenPreview();
            int bw = curBmp != null && !curBmp.isRecycled() ? curBmp.getWidth() : 0;
            int bh = curBmp != null && !curBmp.isRecycled() ? curBmp.getHeight() : 0;
            float[] norm = EcosystemFeatures.mapTouchToBitmapNormalized(
                ev.getX(), ev.getY(), iv.getWidth(), iv.getHeight(), bw, bh);
            switch (ev.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    fsDownXY[0] = ev.getX();
                    fsDownXY[1] = ev.getY();
                    fsMoved[0] = false;
                    fsMulti[0] = false;
                    fsMaxPtr[0] = 1;
                    fsTwoCarry[0] = 0f;
                    fsDownMs[0] = SystemClock.uptimeMillis();
                    act.moveToNormalizedInActiveMonitor(norm[0], norm[1]);
                    return true;
                case MotionEvent.ACTION_POINTER_DOWN:
                    fsMaxPtr[0] = Math.max(fsMaxPtr[0], ev.getPointerCount());
                    fsTwoPrevY[0] = avgPointerY(ev, 2);
                    fsTwoCarry[0] = 0f;
                    return true;
                case MotionEvent.ACTION_MOVE:
                    if (ev.getPointerCount() >= 2) {
                        float avgY = avgPointerY(ev, 2);
                        float sdy = avgY - fsTwoPrevY[0];
                        fsTwoPrevY[0] = avgY;
                        fsTwoCarry[0] += sdy;
                        int step = dp(14);
                        if (Math.abs(fsTwoCarry[0]) > dp(6)) fsMulti[0] = true;
                        while (Math.abs(fsTwoCarry[0]) >= step) {
                            act.wheel(fsTwoCarry[0] > 0 ? 120 : -120, false);
                            fsTwoCarry[0] -= fsTwoCarry[0] > 0 ? step : -step;
                        }
                        return true;
                    }
                    if (Math.abs(ev.getX() - fsDownXY[0]) + Math.abs(ev.getY() - fsDownXY[1]) > dp(7)) {
                        fsMoved[0] = true;
                    }
                    act.moveToNormalizedInActiveMonitor(norm[0], norm[1]);
                    return true;
                case MotionEvent.ACTION_UP:
                    if (fsMaxPtr[0] >= 2) {
                        if (!fsMulti[0]) act.click(2);
                    } else if (!fsMoved[0]) {
                        act.moveToNormalizedInActiveMonitor(norm[0], norm[1]);
                        long held = SystemClock.uptimeMillis() - fsDownMs[0];
                        act.click(held >= 450L ? 2 : 1);
                    }
                    fsSession[0] = null;
                    return true;
                case MotionEvent.ACTION_CANCEL:
                    fsSession[0] = null;
                    return true;
                default:
                    return true;
            }
        });
    }

    private TextView createChip(
            String label, int bgColor, int strokeColor, int textColor, Runnable onClick) {
        TextView tv = new TextView(service);
        tv.setText(label);
        tv.setTextSize(10f);
        tv.setTypeface(null, Typeface.BOLD);
        tv.setTextColor(textColor);
        tv.setSingleLine(true);
        tv.setEllipsize(TextUtils.TruncateAt.END);
        tv.setPadding(dp(8), dp(4), dp(8), dp(4));
        tv.setBackground(PhoneUiKit.roundedBox(bgColor, strokeColor, dp(8), Math.max(1, dp(1))));
        tv.setClickable(onClick != null);
        tv.setFocusable(onClick != null);
        if (onClick != null) tv.setOnClickListener(v -> onClick.run());
        return tv;
    }

    private void showToast(String msg) {
        if (activeToast != null) activeToast.cancel();
        activeToast = Toast.makeText(service, msg, Toast.LENGTH_SHORT);
        activeToast.show();
    }

    private int dp(float px) {
        return Math.round(service.getResources().getDisplayMetrics().density * px);
    }

    private static float avgPointerX(MotionEvent e, int count) {
        int n = Math.min(count, e.getPointerCount());
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += e.getX(i);
        return n > 0 ? sum / n : 0f;
    }

    private static float avgPointerY(MotionEvent e, int count) {
        int n = Math.min(count, e.getPointerCount());
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += e.getY(i);
        return n > 0 ? sum / n : 0f;
    }

    private static float pointerSpan(MotionEvent e) {
        if (e.getPointerCount() < 2) return 0f;
        return (float) Math.hypot(e.getX(0) - e.getX(1), e.getY(0) - e.getY(1));
    }
}
