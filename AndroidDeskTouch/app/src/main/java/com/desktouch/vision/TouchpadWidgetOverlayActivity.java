package com.desktouch.vision;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import androidx.core.graphics.Insets;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Translucent in-place floating 4x4 Touchpad card launched from the Home Screen Widget.
 * Fixed card dimensions ensure Keys, Ctrl+ single-row shortcuts, and overlapping Full Screen
 * float over the touchpad area without expanding, shifting, or clipping. Supports Vertical
 * (4x4 portrait) and Horizontal (landscape) toggle.
 */
public final class TouchpadWidgetOverlayActivity extends Activity {
    static final String EXTRA_MODE = "overlay_mode";
    static final String EXTRA_HORIZONTAL = "overlay_horizontal";

    static final String MODE_TOUCHPAD = "touchpad";
    static final String MODE_FULLSCREEN = "fullscreen";
    static final String MODE_KEYS = "keys";
    static final String MODE_CTRL = "ctrl";

    private FrameLayout scrimRoot;
    private LinearLayout cardContainer;
    private LinearLayout pcPillsRow;
    private TextView linkBadgeView;
    private TextView orientToggleBtn;
    private View touchPadCanvas;
    private TextView toggleFsBtn;
    private LinearLayout fsOverlayView;
    private TextView fsPcLabel;
    private ImageView fsPreviewImage;
    private LinearLayout kbFloatCard;
    private EditText kbInput;
    private LinearLayout ctrlFloatCard;
    private LinearLayout ctrlTabsRow;
    private LinearLayout ctrlItemsRow;
    private TextView keysBtnView;
    private TextView ctrlBtnView;

    private boolean horizontalMode;
    private boolean fullscreenMode;
    private String activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
    private String activeModCat = TouchpadWidgetHelper.CAT_CTRL;

    private float pointerSpeed = 2.8f;
    private float prevX, prevY, downX, downY, twoFingerPrevY, twoFingerScrollCarry;
    private float pinchPrevSpan, threeFingerStartX, threeFingerStartY;
    private boolean threeFingerHandled, dragged, twoFingerMoved;
    private int maxPointerCount;
    private float touchGlowX, touchGlowY;
    private boolean touchGlowActive;

    private boolean suppressTypedText;
    private String previousTyped = "";
    private DeskTouchSession activeTouchSession;
    private Bitmap lastRenderedBmp;
    private Toast activeToast;
    private int lastPcsSignatureHash;
    private final Handler overlayUiHandler = new Handler(Looper.getMainLooper());
    private final AtomicBoolean overlayRefreshQueued = new AtomicBoolean(false);
    private long lastOverlayUiRefreshMs;
    private final Runnable overlayRefreshTask = new Runnable() {
        @Override public void run() {
            overlayRefreshQueued.set(false);
            if (!isFinishing() && !isDestroyed()) {
                lastOverlayUiRefreshMs = SystemClock.uptimeMillis();
                refreshAllUi(false);
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        int savedProgress = getSharedPreferences("desktouch_prefs", MODE_PRIVATE)
            .getInt("pointer_sensitivity_progress", 180);
        savedProgress = Math.max(0, Math.min(400, savedProgress));
        pointerSpeed = 1f + savedProgress / 100f;

        WindowCompat.setDecorFitsSystemWindows(getWindow(), false);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        buildUi();
        setContentView(scrimRoot);
        applyInsets();
        applyLaunchIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        applyLaunchIntent(intent);
    }

    @Override
    protected void onResume() {
        super.onResume();
        DeskTouchAccessibilityService.hideInlineWidgetOverlay();
        DeskTouchRuntime.setOverlayLiveStatusListener(this::scheduleCoalescedRefresh);
        DeskTouchRuntime.setOverlayScreenPreviewListener(() -> runOnUiThread(this::renderLatestPreview));
        lastOverlayUiRefreshMs = SystemClock.uptimeMillis();
        refreshAllUi(true);
        if (fullscreenMode) startPreviewWatch();
        if (TouchpadWidgetHelper.OVERLAY_KEYS.equals(activeOverlay)) focusKeyboardInput();
    }

    @Override
    protected void onPause() {
        super.onPause();
        DeskTouchRuntime.setOverlayLiveStatusListener(null);
        DeskTouchRuntime.setOverlayScreenPreviewListener(null);
        overlayUiHandler.removeCallbacks(overlayRefreshTask);
        overlayRefreshQueued.set(false);
        stopPreviewWatchIfNeeded();
    }

    private void scheduleCoalescedRefresh() {
        if (!overlayRefreshQueued.compareAndSet(false, true)) return;
        long elapsed = SystemClock.uptimeMillis() - lastOverlayUiRefreshMs;
        long delayMs = elapsed >= 200L ? 0L : (200L - elapsed);
        if (delayMs == 0L) {
            overlayUiHandler.post(overlayRefreshTask);
        } else {
            overlayUiHandler.postDelayed(overlayRefreshTask, delayMs);
        }
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        updateCardSize();
    }

    private void applyLaunchIntent(Intent intent) {
        String mode = intent != null ? intent.getStringExtra(EXTRA_MODE) : MODE_TOUCHPAD;
        if (intent != null && intent.hasExtra(EXTRA_HORIZONTAL)) {
            horizontalMode = intent.getBooleanExtra(EXTRA_HORIZONTAL, false);
            TouchpadWidgetHelper.setWidgetHorizontal(this, horizontalMode);
            TouchpadWidgetProvider.updateAll(this);
        } else {
            horizontalMode = TouchpadWidgetHelper.isWidgetHorizontal(this);
        }

        if (MODE_FULLSCREEN.equals(mode)) {
            fullscreenMode = true;
            activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
        } else if (MODE_KEYS.equals(mode)) {
            fullscreenMode = false;
            activeOverlay = TouchpadWidgetHelper.OVERLAY_KEYS;
        } else if (MODE_CTRL.equals(mode)) {
            fullscreenMode = false;
            activeOverlay = TouchpadWidgetHelper.OVERLAY_CTRL;
        } else {
            fullscreenMode = false;
            activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
        }

        applyRequestedOrientation();
        refreshAllUi(true);
        if (MODE_FULLSCREEN.equals(mode) && !fullscreenMode) {
            showToast("Select a computer above first");
        }
        if (fullscreenMode) {
            startPreviewWatch();
        } else {
            stopPreviewWatchIfNeeded();
        }
        if (TouchpadWidgetHelper.OVERLAY_KEYS.equals(activeOverlay)) {
            focusKeyboardInput();
        }
    }

    private void applyRequestedOrientation() {
        try {
            setRequestedOrientation(horizontalMode
                ? ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                : ActivityInfo.SCREEN_ORIENTATION_PORTRAIT);
        } catch (RuntimeException ignored) {}
    }

    private void buildUi() {
        scrimRoot = new FrameLayout(this);
        scrimRoot.setBackgroundColor(0x8A000000);
        scrimRoot.setOnClickListener(v -> finish());

        cardContainer = new LinearLayout(this);
        cardContainer.setOrientation(LinearLayout.VERTICAL);
        cardContainer.setPadding(dp(10), dp(10), dp(10), dp(10));
        cardContainer.setBackground(PhoneUiKit.roundedBox(0xF50F172A, 0xCC38BDF8, dp(18), Math.max(2, dp(2))));
        cardContainer.setClickable(true);
        cardContainer.setOnClickListener(v -> {}); // Consume taps inside floating card

        // Header bar: Connected Systems label, link badge, Horizontal/Vertical toggle, Close
        LinearLayout topRow = new LinearLayout(this);
        topRow.setOrientation(LinearLayout.HORIZONTAL);
        topRow.setGravity(Gravity.CENTER_VERTICAL);

        TextView titleLbl = new TextView(this);
        titleLbl.setText("CONNECTED SYSTEMS");
        titleLbl.setTextSize(10f);
        titleLbl.setTypeface(null, Typeface.BOLD);
        titleLbl.setTextColor(0xFF94A3B8);
        titleLbl.setSingleLine(true);
        titleLbl.setEllipsize(TextUtils.TruncateAt.END);
        topRow.addView(titleLbl, new LinearLayout.LayoutParams(0, -2, 1f));

        linkBadgeView = new TextView(this);
        linkBadgeView.setTextSize(10f);
        linkBadgeView.setTypeface(null, Typeface.BOLD);
        linkBadgeView.setPadding(dp(8), dp(2), dp(8), dp(2));
        LinearLayout.LayoutParams lbLp = new LinearLayout.LayoutParams(-2, -2);
        lbLp.leftMargin = dp(6);
        topRow.addView(linkBadgeView, lbLp);

        orientToggleBtn = createChip("", 0xFF1E293B, 0xFF475569, 0xFF7DD3FC, () -> {
            horizontalMode = !horizontalMode;
            TouchpadWidgetHelper.setWidgetHorizontal(this, horizontalMode);
            TouchpadWidgetProvider.updateAll(this);
            applyRequestedOrientation();
            syncOrientationChip();
            updateCardSize();
        });
        LinearLayout.LayoutParams otLp = new LinearLayout.LayoutParams(-2, -2);
        otLp.leftMargin = dp(6);
        topRow.addView(orientToggleBtn, otLp);

        TextView closeBtn = createChip("✕", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, this::finish);
        LinearLayout.LayoutParams clLp = new LinearLayout.LayoutParams(-2, -2);
        clLp.leftMargin = dp(6);
        topRow.addView(closeBtn, clLp);
        cardContainer.addView(topRow, new LinearLayout.LayoutParams(-1, -2));

        // Connected PC selector pills row (tap once to select, tap again to unselect)
        HorizontalScrollView pcScroll = new HorizontalScrollView(this);
        pcScroll.setHorizontalScrollBarEnabled(false);
        pcPillsRow = new LinearLayout(this);
        pcPillsRow.setOrientation(LinearLayout.HORIZONTAL);
        pcPillsRow.setGravity(Gravity.CENTER_VERTICAL);
        pcScroll.addView(pcPillsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        LinearLayout.LayoutParams pcScLp = new LinearLayout.LayoutParams(-1, -2);
        pcScLp.topMargin = dp(6);
        cardContainer.addView(pcScroll, pcScLp);

        // Center Fixed Viewport (hosts Touchpad Layer 1, Full Screen Layer 2, Floating Keys/Ctrl+ Layer 3)
        FrameLayout viewport = new FrameLayout(this);
        viewport.setBackground(PhoneUiKit.roundedBox(0xFF0B1220, 0x660EA5E9, dp(14), Math.max(1, dp(1))));
        LinearLayout.LayoutParams vpLp = new LinearLayout.LayoutParams(-1, 0, 1f);
        vpLp.topMargin = dp(7);
        vpLp.bottomMargin = dp(7);
        cardContainer.addView(viewport, vpLp);

        // Layer 1: Live Touchpad Canvas
        touchPadCanvas = new View(this) {
            final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
            final RectF bounds = new RectF();

            @Override
            protected void onDraw(Canvas c) {
                bounds.set(0, 0, getWidth(), getHeight());
                paint.setStyle(Paint.Style.FILL);
                paint.setColor(0xFF0F172A);
                c.drawRoundRect(bounds, dp(14), dp(14), paint);

                paint.setColor(0x1694A3B8);
                paint.setStrokeWidth(Math.max(1f, dp(1)));
                int step = dp(22);
                for (int gx = step; gx < getWidth(); gx += step) c.drawLine(gx, 0, gx, getHeight(), paint);
                for (int gy = step; gy < getHeight(); gy += step) c.drawLine(0, gy, getWidth(), gy, paint);

                if (touchGlowActive) {
                    paint.setColor(0x260EA5E9);
                    c.drawCircle(touchGlowX, touchGlowY, dp(24), paint);
                    paint.setStyle(Paint.Style.STROKE);
                    paint.setStrokeWidth(Math.max(2f, dp(1) * 1.5f));
                    paint.setColor(0xFF38BDF8);
                    c.drawCircle(touchGlowX, touchGlowY, dp(13), paint);
                }

                String activePcName = resolveActivePcName();
                paint.setStyle(Paint.Style.FILL);
                paint.setTextAlign(Paint.Align.CENTER);
                paint.setColor(0xFFBAE6FD);
                paint.setTextSize(dp(13));
                c.drawText("Live Touchpad", getWidth() / 2f, getHeight() / 2f - dp(4), paint);
                paint.setColor(activePcName.isEmpty() ? 0xFFFBBF24 : 0xBF7DD3FC);
                paint.setTextSize(dp(11));
                c.drawText(
                    activePcName.isEmpty()
                        ? "Tap a computer above to select & control"
                        : ("Controlling " + activePcName + " • Drag or tap"),
                    getWidth() / 2f, getHeight() / 2f + dp(14), paint);
            }
        };
        touchPadCanvas.setOnTouchListener(this::handleTouchpadMotion);
        viewport.addView(touchPadCanvas, new FrameLayout.LayoutParams(-1, -1));

        toggleFsBtn = createChip("⛶ Full Screen", 0xE61E293B, 0x9938BDF8, 0xFFBAE6FD, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act == null) return;
            fullscreenMode = true;
            syncOverlayState();
            startPreviewWatch();
        });
        FrameLayout.LayoutParams fsBtnLp = new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.END);
        fsBtnLp.topMargin = dp(8);
        fsBtnLp.rightMargin = dp(8);
        viewport.addView(toggleFsBtn, fsBtnLp);

        // Layer 2: Overlapping Full Screen Monitor Preview (same bounds as Touchpad)
        fsOverlayView = new LinearLayout(this);
        fsOverlayView.setOrientation(LinearLayout.VERTICAL);
        fsOverlayView.setPadding(dp(8), dp(8), dp(8), dp(6));
        fsOverlayView.setBackgroundColor(0xFF0B1220);
        fsOverlayView.setVisibility(View.GONE);

        LinearLayout fsTop = new LinearLayout(this);
        fsTop.setOrientation(LinearLayout.HORIZONTAL);
        fsTop.setGravity(Gravity.CENTER_VERTICAL);
        fsPcLabel = new TextView(this);
        fsPcLabel.setTextColor(0xFF34D399);
        fsPcLabel.setTextSize(10.5f);
        fsPcLabel.setTypeface(null, Typeface.BOLD);
        fsPcLabel.setSingleLine(true);
        fsPcLabel.setEllipsize(TextUtils.TruncateAt.END);
        fsTop.addView(fsPcLabel, new LinearLayout.LayoutParams(0, -2, 1f));

        TextView backPadBtn = createChip("🖱 Touchpad", 0xFF0EA5E9, 0xFF7DD3FC, Color.WHITE, () -> {
            fullscreenMode = false;
            stopPreviewWatchIfNeeded();
            syncOverlayState();
        });
        fsTop.addView(backPadBtn, new LinearLayout.LayoutParams(-2, -2));
        fsOverlayView.addView(fsTop, new LinearLayout.LayoutParams(-1, -2));

        fsPreviewImage = new ImageView(this);
        fsPreviewImage.setScaleType(ImageView.ScaleType.FIT_CENTER);
        fsPreviewImage.setBackgroundColor(0xFF060B14);
        bindFullscreenImageTouch();
        LinearLayout.LayoutParams imgLp = new LinearLayout.LayoutParams(-1, 0, 1f);
        imgLp.topMargin = dp(6);
        imgLp.bottomMargin = dp(4);
        fsOverlayView.addView(fsPreviewImage, imgLp);

        TextView fsHint = new TextView(this);
        fsHint.setText("Tap screen to click • Long-press / 2-finger tap = Right");
        fsHint.setTextSize(9.5f);
        fsHint.setTextColor(0xFF94A3B8);
        fsHint.setGravity(Gravity.CENTER);
        fsOverlayView.addView(fsHint, new LinearLayout.LayoutParams(-1, -2));
        viewport.addView(fsOverlayView, new FrameLayout.LayoutParams(-1, -1));

        // Layer 3A: Floating Keyboard Overlay (anchored to bottom of fixed viewport)
        kbFloatCard = new LinearLayout(this);
        kbFloatCard.setOrientation(LinearLayout.VERTICAL);
        kbFloatCard.setPadding(dp(8), dp(7), dp(8), dp(7));
        kbFloatCard.setBackground(PhoneUiKit.roundedBox(0xF5090D16, 0xB338BDF8, dp(12), Math.max(1, dp(1))));
        kbFloatCard.setVisibility(View.GONE);
        kbFloatCard.setClickable(true);

        LinearLayout kbHdr = new LinearLayout(this);
        kbHdr.setOrientation(LinearLayout.HORIZONTAL);
        kbHdr.setGravity(Gravity.CENTER_VERTICAL);
        TextView kbHdrTitle = new TextView(this);
        kbHdrTitle.setText("⌨ Floating Keyboard");
        kbHdrTitle.setTextSize(10f);
        kbHdrTitle.setTypeface(null, Typeface.BOLD);
        kbHdrTitle.setTextColor(0xFF7DD3FC);
        kbHdr.addView(kbHdrTitle, new LinearLayout.LayoutParams(0, -2, 1f));
        kbHdr.addView(createChip("✕", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, () -> {
            activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
            hideSoftKeyboard();
            syncOverlayState();
        }));
        kbFloatCard.addView(kbHdr, new LinearLayout.LayoutParams(-1, -2));

        LinearLayout kbRow = new LinearLayout(this);
        kbRow.setOrientation(LinearLayout.HORIZONTAL);
        kbRow.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams kbRowLp = new LinearLayout.LayoutParams(-1, -2);
        kbRowLp.topMargin = dp(5);
        kbFloatCard.addView(kbRow, kbRowLp);

        kbInput = createRemoteEditText();
        LinearLayout.LayoutParams inLp = new LinearLayout.LayoutParams(0, -2, 1f);
        inLp.rightMargin = dp(6);
        kbRow.addView(kbInput, inLp);

        kbRow.addView(createChip("⌫", 0xFF1E293B, 0xFF475569, 0xFFE2E8F0, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act == null) return;
            act.key(TouchpadWidgetHelper.VK_BACK, true);
            act.key(TouchpadWidgetHelper.VK_BACK, false);
        }));
        TextView enterBtn = createChip("↵", 0xFF0EA5E9, 0xFF38BDF8, Color.WHITE, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act == null) return;
            act.key(FullscreenPreviewLayoutHelper.VK_RETURN, true);
            act.key(FullscreenPreviewLayoutHelper.VK_RETURN, false);
            suppressTypedText = true;
            kbInput.setText("");
            previousTyped = "";
            suppressTypedText = false;
        });
        LinearLayout.LayoutParams enLp = new LinearLayout.LayoutParams(-2, -2);
        enLp.leftMargin = dp(4);
        kbRow.addView(enterBtn, enLp);

        FrameLayout.LayoutParams kbFloatLp = new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM);
        kbFloatLp.leftMargin = dp(8);
        kbFloatLp.rightMargin = dp(8);
        kbFloatLp.bottomMargin = dp(8);
        viewport.addView(kbFloatCard, kbFloatLp);

        // Layer 3B: Floating All-in-One Single-Row Ctrl+ Shortcuts Overlay
        ctrlFloatCard = new LinearLayout(this);
        ctrlFloatCard.setOrientation(LinearLayout.VERTICAL);
        ctrlFloatCard.setPadding(dp(8), dp(7), dp(8), dp(7));
        ctrlFloatCard.setBackground(PhoneUiKit.roundedBox(0xF5090D16, 0xB3818CF8, dp(12), Math.max(1, dp(1))));
        ctrlFloatCard.setVisibility(View.GONE);
        ctrlFloatCard.setClickable(true);

        LinearLayout catBar = new LinearLayout(this);
        catBar.setOrientation(LinearLayout.HORIZONTAL);
        catBar.setGravity(Gravity.CENTER_VERTICAL);
        HorizontalScrollView catScroll = new HorizontalScrollView(this);
        catScroll.setHorizontalScrollBarEnabled(false);
        ctrlTabsRow = new LinearLayout(this);
        ctrlTabsRow.setOrientation(LinearLayout.HORIZONTAL);
        catScroll.addView(ctrlTabsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        catBar.addView(catScroll, new LinearLayout.LayoutParams(0, -2, 1f));
        catBar.addView(createChip("✕", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, () -> {
            activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
            syncOverlayState();
        }));
        ctrlFloatCard.addView(catBar, new LinearLayout.LayoutParams(-1, -2));

        HorizontalScrollView itemsScroll = new HorizontalScrollView(this);
        itemsScroll.setHorizontalScrollBarEnabled(false);
        ctrlItemsRow = new LinearLayout(this);
        ctrlItemsRow.setOrientation(LinearLayout.HORIZONTAL);
        itemsScroll.addView(ctrlItemsRow, new HorizontalScrollView.LayoutParams(-2, -2));
        LinearLayout.LayoutParams itScLp = new LinearLayout.LayoutParams(-1, -2);
        itScLp.topMargin = dp(6);
        ctrlFloatCard.addView(itemsScroll, itScLp);

        FrameLayout.LayoutParams ctrlFloatLp = new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM);
        ctrlFloatLp.leftMargin = dp(8);
        ctrlFloatLp.rightMargin = dp(8);
        ctrlFloatLp.bottomMargin = dp(8);
        viewport.addView(ctrlFloatCard, ctrlFloatLp);

        // Bottom Fixed 6-Button Row (Left, Right, Scroll Up, Scroll Down, Keys, Ctrl+ — Zero Pay Safe)
        LinearLayout bottomRow = new LinearLayout(this);
        bottomRow.setOrientation(LinearLayout.HORIZONTAL);
        addBottomAction(bottomRow, "Left", 1f, false, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act != null) act.click(1);
        });
        addBottomAction(bottomRow, "Right", 1f, true, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act != null) act.click(2);
        });
        addBottomAction(bottomRow, "↑", 0.8f, true, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act != null) act.wheel(120, false);
        });
        addBottomAction(bottomRow, "↓", 0.8f, true, () -> {
            DeskTouchSession act = requireSelectedActiveSession();
            if (act != null) act.wheel(-120, false);
        });

        keysBtnView = createBottomButton("⌨ Keys", 1.15f, true, () -> {
            activeOverlay = TouchpadWidgetHelper.toggleFloatingOverlay(
                activeOverlay, TouchpadWidgetHelper.OVERLAY_KEYS);
            syncOverlayState();
            if (TouchpadWidgetHelper.OVERLAY_KEYS.equals(activeOverlay)) {
                focusKeyboardInput();
            } else {
                hideSoftKeyboard();
            }
        });
        bottomRow.addView(keysBtnView);

        ctrlBtnView = createBottomButton("Ctrl+", 1f, true, () -> {
            activeOverlay = TouchpadWidgetHelper.toggleFloatingOverlay(
                activeOverlay, TouchpadWidgetHelper.OVERLAY_CTRL);
            hideSoftKeyboard();
            syncOverlayState();
        });
        bottomRow.addView(ctrlBtnView);
        cardContainer.addView(bottomRow, new LinearLayout.LayoutParams(-1, -2));

        FrameLayout.LayoutParams cardLp = new FrameLayout.LayoutParams(dp(360), dp(420), Gravity.CENTER);
        scrimRoot.addView(cardContainer, cardLp);
        scrimRoot.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> updateCardSize());
        rebuildCtrlCategoryBarAndRow();
    }

    private void updateCardSize() {
        if (scrimRoot == null || cardContainer == null) return;
        int availW = scrimRoot.getWidth() > 0 ? scrimRoot.getWidth() : getResources().getDisplayMetrics().widthPixels;
        int availH = scrimRoot.getHeight() > 0 ? scrimRoot.getHeight() : getResources().getDisplayMetrics().heightPixels;
        int targetW = horizontalMode
            ? Math.min(dp(640), Math.max(dp(300), availW - dp(24)))
            : Math.min(dp(390), Math.max(dp(260), availW - dp(24)));
        int targetH = horizontalMode
            ? Math.min(dp(340), Math.max(dp(220), availH - dp(20)))
            : Math.min(dp(440), Math.max(dp(260), availH - dp(40)));
        ViewGroup.LayoutParams lp = cardContainer.getLayoutParams();
        if (lp != null && (lp.width != targetW || lp.height != targetH)) {
            lp.width = targetW;
            lp.height = targetH;
            cardContainer.setLayoutParams(lp);
        }
    }

    private void applyInsets() {
        scrimRoot.setOnApplyWindowInsetsListener((v, wi) -> {
            WindowInsetsCompat compat = WindowInsetsCompat.toWindowInsetsCompat(wi, v);
            Insets bars = compat.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            Insets ime = compat.getInsets(WindowInsetsCompat.Type.ime());
            int bot = Math.max(bars.bottom, ime.bottom);
            scrimRoot.setPadding(bars.left, bars.top, bars.right, bot);
            return wi;
        });
        scrimRoot.requestApplyInsets();
    }

    private void refreshAllUi(boolean forcePills) {
        DeskTouchSession active = DeskTouchRuntime.session(this);
        DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(this);
        boolean connected = snap != null && snap.connected;
        List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
            active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
        boolean explicitUnselected = TouchpadWidgetHelper.isExplicitlyUnselected(this);
        int effId = connected
            ? TouchpadWidgetHelper.resolveEffectiveSelection(
                TouchpadWidgetHelper.loadSelectedTargetId(this), explicitUnselected, pcs)
            : TouchpadWidgetHelper.UNSELECTED;

        if (!connected) {
            linkBadgeView.setText("Offline");
            linkBadgeView.setTextColor(0xFFFBBF24);
            linkBadgeView.setBackground(PhoneUiKit.roundedBox(0x26F59E0B, 0x4DF59E0B, dp(99), Math.max(1, dp(1))));
        } else if (effId == TouchpadWidgetHelper.UNSELECTED) {
            fullscreenMode = false;
            linkBadgeView.setText("No PC selected");
            linkBadgeView.setTextColor(0xFFFBBF24);
            linkBadgeView.setBackground(PhoneUiKit.roundedBox(0x26F59E0B, 0x4DF59E0B, dp(99), Math.max(1, dp(1))));
        } else {
            boolean fastUdp = DeskTouchRuntime.hasActiveMobileUdpStream()
                || snap.mobConfirmed
                || (snap.rttMs > 0 && snap.rttMs < 500);
            linkBadgeView.setText(fastUdp ? "Fast UDP" : "Connected");
            linkBadgeView.setTextColor(0xFF34D399);
            linkBadgeView.setBackground(PhoneUiKit.roundedBox(0x2610B981, 0x4D10B981, dp(99), Math.max(1, dp(1))));
        }

        int sigHash = (connected ? 1 : 0) * 31 + effId * 17 + pcs.size();
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            sigHash = sigHash * 31 + pc.id + (pc.shortName != null ? pc.shortName.hashCode() : 0);
        }
        if (forcePills || sigHash != lastPcsSignatureHash) {
            lastPcsSignatureHash = sigHash;
            rebuildPcPills(connected, pcs, effId);
        }

        syncOrientationChip();
        syncOverlayState();
        touchPadCanvas.invalidate();
        if (fullscreenMode) renderLatestPreview();
    }

    private void rebuildPcPills(
            boolean connected, List<LinkedDisplayHelper.DisplayTileModel> pcs, int effId) {
        pcPillsRow.removeAllViews();
        if (!connected) {
            pcPillsRow.addView(createChip("No PC connected • Open Find", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, () -> {
                startActivity(new Intent(this, MainActivity.class)
                    .setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP)
                    .putExtra(MainActivity.EXTRA_OPEN_TAB, "find"));
                finish();
            }));
            return;
        }
        if (pcs.isEmpty()) {
            pcPillsRow.addView(createChip("Syncing PCs...", 0xFF1E293B, 0xFF475569, 0xFFCBD5E1, null));
            return;
        }
        for (int i = 0; i < pcs.size(); i++) {
            final LinkedDisplayHelper.DisplayTileModel pc = pcs.get(i);
            final boolean sel = pc.id == effId;
            TextView pill = createChip(
                (sel ? "● " : "○ ") + pc.shortName,
                sel ? 0x330EA5E9 : 0xFF1E293B,
                sel ? 0xFF38BDF8 : 0xFF475569,
                sel ? Color.WHITE : 0xFFCBD5E1,
                () -> {
                    DeskTouchSession active = DeskTouchRuntime.session(this);
                    List<LinkedDisplayHelper.DisplayTileModel> latest = LinkedDisplayHelper.buildTrackpadPcTiles(
                        active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
                    int cur = TouchpadWidgetHelper.resolveEffectiveSelection(
                        TouchpadWidgetHelper.loadSelectedTargetId(this),
                        TouchpadWidgetHelper.isExplicitlyUnselected(this),
                        latest);
                    int next = TouchpadWidgetHelper.togglePcSelection(cur, pc.id);
                    if (next == TouchpadWidgetHelper.UNSELECTED) {
                        TouchpadWidgetHelper.saveSelectedTarget(this, TouchpadWidgetHelper.UNSELECTED, true);
                        active.releaseControl();
                        fullscreenMode = false;
                        stopPreviewWatchIfNeeded();
                        showToast("Unselected " + pc.shortName);
                    } else {
                        TouchpadWidgetHelper.saveSelectedTarget(this, next, false);
                        active.switchTargetAndClaim(next, 0);
                        if (fullscreenMode) startPreviewWatch();
                        showToast("Selected → " + pc.shortName);
                    }
                    TouchpadWidgetProvider.updateAll(this);
                    refreshAllUi(true);
                });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
            if (i > 0) lp.leftMargin = dp(6);
            pcPillsRow.addView(pill, lp);
        }
    }

    private void syncOrientationChip() {
        orientToggleBtn.setText(horizontalMode ? "⟳ Vertical" : "⟳ Horizontal");
        orientToggleBtn.setBackground(PhoneUiKit.roundedBox(
            horizontalMode ? 0x330EA5E9 : 0xFF1E293B,
            horizontalMode ? 0xFF38BDF8 : 0xFF475569,
            dp(8), Math.max(1, dp(1))));
    }

    private void syncOverlayState() {
        String name = resolveActivePcName();
        boolean hasPc = !name.isEmpty();
        if (!hasPc) fullscreenMode = false;

        toggleFsBtn.setVisibility(fullscreenMode ? View.GONE : View.VISIBLE);
        toggleFsBtn.setAlpha(hasPc ? 1f : 0.55f);
        fsOverlayView.setVisibility(fullscreenMode ? View.VISIBLE : View.GONE);
        fsPcLabel.setText(hasPc ? ("● Live Screen • " + name) : "● Live Screen");

        boolean showKeys = TouchpadWidgetHelper.OVERLAY_KEYS.equals(activeOverlay);
        boolean showCtrl = TouchpadWidgetHelper.OVERLAY_CTRL.equals(activeOverlay);
        kbFloatCard.setVisibility(showKeys ? View.VISIBLE : View.GONE);
        ctrlFloatCard.setVisibility(showCtrl ? View.VISIBLE : View.GONE);

        kbInput.setHint(hasPc ? ("Type to " + name + "...") : "Select a computer above first...");
        keysBtnView.setBackground(PhoneUiKit.roundedBox(
            showKeys ? 0xFF0EA5E9 : 0x330EA5E9,
            showKeys ? 0xFF7DD3FC : 0x8038BDF8,
            dp(10), Math.max(1, dp(1))));
        keysBtnView.setTextColor(showKeys ? Color.WHITE : 0xFF7DD3FC);

        ctrlBtnView.setBackground(PhoneUiKit.roundedBox(
            showCtrl ? 0xFF6366F1 : 0x336366F1,
            showCtrl ? 0xFFA5B4FC : 0x80818CF8,
            dp(10), Math.max(1, dp(1))));
        ctrlBtnView.setTextColor(showCtrl ? Color.WHITE : 0xFFA5B4FC);
    }

    private void rebuildCtrlCategoryBarAndRow() {
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
        List<FullscreenPreviewLayoutHelper.ShortcutItem> items = TouchpadWidgetHelper.shortcutsForCategory(activeModCat);
        for (int i = 0; i < items.size(); i++) {
            final FullscreenPreviewLayoutHelper.ShortcutItem item = items.get(i);
            TextView btn = createChip(
                item.label,
                0xFF1E293B,
                0xFF475569,
                0xFFA5B4FC,
                () -> {
                    DeskTouchSession act = requireSelectedActiveSession();
                    if (act == null) return;
                    TouchpadWidgetHelper.sendWindowsKeyCombo(
                        act, item.modifierVks, item.mainVk, item.powerAction);
                    showToast("Sent " + item.label);
                });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, -2);
            if (i > 0) lp.leftMargin = dp(5);
            ctrlItemsRow.addView(btn, lp);
        }
    }

    private DeskTouchSession requireSelectedActiveSession() {
        DeskTouchSession active = DeskTouchRuntime.session(this);
        DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(this);
        if (snap == null || !snap.connected) {
            showToast("Connect to a computer first");
            return null;
        }
        List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
            active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
        int effId = TouchpadWidgetHelper.resolveEffectiveSelection(
            TouchpadWidgetHelper.loadSelectedTargetId(this),
            TouchpadWidgetHelper.isExplicitlyUnselected(this),
            pcs);
        if (effId == TouchpadWidgetHelper.UNSELECTED) {
            showToast("Select a computer above first");
            return null;
        }
        if (active.selectedEnvironmentTarget() != effId || (!active.isActive() && !active.hasClaim())) {
            active.switchTargetAndClaim(effId, 0);
        }
        return active;
    }

    private String resolveActivePcName() {
        DeskTouchSession active = DeskTouchRuntime.session(this);
        DeskTouchSession.LiveStatusSnapshot snap = DeskTouchRuntime.liveSnapshot(this);
        if (snap == null || !snap.connected) return "";
        List<LinkedDisplayHelper.DisplayTileModel> pcs = LinkedDisplayHelper.buildTrackpadPcTiles(
            active.environmentTargets(), active.selectedEnvironmentTarget(), active.ownEnvironmentId());
        int effId = TouchpadWidgetHelper.resolveEffectiveSelection(
            TouchpadWidgetHelper.loadSelectedTargetId(this),
            TouchpadWidgetHelper.isExplicitlyUnselected(this),
            pcs);
        if (effId == TouchpadWidgetHelper.UNSELECTED) return "";
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            if (pc.id == effId) return pc.shortName;
        }
        return "";
    }

    private boolean handleTouchpadMotion(View v, MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                if (!TouchpadWidgetHelper.OVERLAY_NONE.equals(activeOverlay)) {
                    activeOverlay = TouchpadWidgetHelper.OVERLAY_NONE;
                    hideSoftKeyboard();
                    syncOverlayState();
                }
                if (v.getParent() != null) v.getParent().requestDisallowInterceptTouchEvent(true);
                activeTouchSession = !resolveActivePcName().isEmpty() ? requireSelectedActiveSession() : null;
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
            case MotionEvent.ACTION_MOVE:
                touchGlowX = event.getX();
                touchGlowY = event.getY();
                touchGlowActive = true;
                v.invalidate();
                DeskTouchSession act = activeTouchSession;
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
                if (Math.abs(event.getX() - downX) + Math.abs(event.getY() - downY) > dp(9)) dragged = true;
                act.moveBy(dx * pointerSpeed, dy * pointerSpeed);
                prevX = event.getX();
                prevY = event.getY();
                return true;
            case MotionEvent.ACTION_UP:
                if (v.getParent() != null) v.getParent().requestDisallowInterceptTouchEvent(false);
                touchGlowActive = false;
                v.invalidate();
                DeskTouchSession upSession = activeTouchSession;
                activeTouchSession = null;
                if (upSession != null && upSession.isConnected()) {
                    if (maxPointerCount >= 2) {
                        if (!twoFingerMoved) upSession.click(2);
                    } else if (!dragged) {
                        upSession.click(1);
                    }
                }
                return true;
            case MotionEvent.ACTION_CANCEL:
                if (v.getParent() != null) v.getParent().requestDisallowInterceptTouchEvent(false);
                touchGlowActive = false;
                v.invalidate();
                activeTouchSession = null;
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

        fsPreviewImage.setOnTouchListener((iv, ev) -> {
            if (iv.getWidth() <= 0 || iv.getHeight() <= 0) return true;
            DeskTouchSession act = requireSelectedActiveSession();
            if (act == null) return true;
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
                    fsDownMs[0] = android.os.SystemClock.uptimeMillis();
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
                        long held = android.os.SystemClock.uptimeMillis() - fsDownMs[0];
                        act.click(held >= 450L ? 2 : 1);
                    }
                    return true;
                default:
                    return true;
            }
        });
    }

    private void startPreviewWatch() {
        DeskTouchSession act = DeskTouchRuntime.session(this);
        if (!act.isConnected()) return;
        int tid = Math.max(0, act.selectedEnvironmentTarget());
        act.setScreenPreviewWatch(tid, Math.max(0, act.activeMonitorIndex()), true);
        renderLatestPreview();
    }

    private void stopPreviewWatchIfNeeded() {
        DeskTouchSession act = DeskTouchRuntime.session(this);
        if (act.isConnected()) {
            act.setScreenPreviewWatch(-1, -1, false);
        }
        lastRenderedBmp = null;
        if (fsPreviewImage != null) fsPreviewImage.setImageDrawable(null);
    }

    private void renderLatestPreview() {
        if (!fullscreenMode || fsPreviewImage == null || isFinishing() || isDestroyed()) return;
        DeskTouchSession act = DeskTouchRuntime.session(this);
        Bitmap bmp = act.currentScreenPreview();
        if (bmp != null && !bmp.isRecycled() && bmp != lastRenderedBmp) {
            fsPreviewImage.setImageBitmap(bmp);
            lastRenderedBmp = bmp;
        }
    }

    private EditText createRemoteEditText() {
        EditText edit = new EditText(this) {
            @Override
            public InputConnection onCreateInputConnection(EditorInfo outAttrs) {
                InputConnection base = super.onCreateInputConnection(outAttrs);
                if (outAttrs != null) {
                    outAttrs.imeOptions = (outAttrs.imeOptions & ~EditorInfo.IME_MASK_ACTION)
                        | EditorInfo.IME_ACTION_NONE | EditorInfo.IME_FLAG_NO_EXTRACT_UI;
                }
                if (base == null) return null;
                return new InputConnectionWrapper(base, true) {
                    @Override
                    public boolean deleteSurroundingText(int beforeLength, int afterLength) {
                        if (getText() != null && getText().length() == 0 && beforeLength > 0) {
                            DeskTouchSession s = requireSelectedActiveSession();
                            if (s != null) {
                                s.key(TouchpadWidgetHelper.VK_BACK, true);
                                s.key(TouchpadWidgetHelper.VK_BACK, false);
                                return true;
                            }
                        }
                        return super.deleteSurroundingText(beforeLength, afterLength);
                    }

                    @Override
                    public boolean sendKeyEvent(KeyEvent event) {
                        if (event != null && event.getAction() == KeyEvent.ACTION_DOWN
                            && event.getKeyCode() == KeyEvent.KEYCODE_DEL
                            && getText() != null && getText().length() == 0) {
                            DeskTouchSession s = requireSelectedActiveSession();
                            if (s != null) {
                                s.key(TouchpadWidgetHelper.VK_BACK, true);
                                s.key(TouchpadWidgetHelper.VK_BACK, false);
                                return true;
                            }
                        }
                        return super.sendKeyEvent(event);
                    }
                };
            }
        };
        edit.setSingleLine(true);
        edit.setTextSize(12f);
        edit.setTextColor(Color.WHITE);
        edit.setHintTextColor(0xFF64748B);
        edit.setPadding(dp(8), dp(6), dp(8), dp(6));
        edit.setBackground(PhoneUiKit.roundedBox(0xFF0F172A, 0xFF334155, dp(8), Math.max(1, dp(1))));
        edit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        edit.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            public void onTextChanged(CharSequence s, int start, int before, int count) {}
            public void afterTextChanged(Editable editable) {
                if (suppressTypedText) return;
                String next = editable.toString();
                if (!resolveActivePcName().isEmpty()) {
                    DeskTouchSession act = requireSelectedActiveSession();
                    if (act != null) {
                        int common = 0;
                        while (common < previousTyped.length() && common < next.length()
                            && previousTyped.charAt(common) == next.charAt(common)) {
                            common++;
                        }
                        for (int n = common; n < previousTyped.length(); n++) {
                            act.key(TouchpadWidgetHelper.VK_BACK, true);
                            act.key(TouchpadWidgetHelper.VK_BACK, false);
                        }
                        if (common < next.length()) {
                            MainActivity.sendRemoteTextWithNewlines(act, next.substring(common));
                        }
                    }
                }
                previousTyped = next;
                if (next.length() > 300) {
                    suppressTypedText = true;
                    editable.clear();
                    previousTyped = "";
                    suppressTypedText = false;
                }
            }
        });
        return edit;
    }

    private void focusKeyboardInput() {
        if (kbInput == null) return;
        kbInput.requestFocus();
        kbInput.post(() -> {
            InputMethodManager imm = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
            if (imm != null && !imm.showSoftInput(kbInput, 0)) {
                imm.showSoftInput(kbInput, InputMethodManager.SHOW_FORCED);
            }
        });
    }

    private void hideSoftKeyboard() {
        if (kbInput == null) return;
        InputMethodManager imm = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
        if (imm != null) imm.hideSoftInputFromWindow(kbInput.getWindowToken(), 0);
    }

    private void addBottomAction(
            LinearLayout row, String label, float weight, boolean leftMargin, Runnable onClick) {
        row.addView(createBottomButton(label, weight, leftMargin, onClick));
    }

    private TextView createBottomButton(
            String label, float weight, boolean leftMargin, Runnable onClick) {
        TextView tv = createChip(label, 0xFF1E293B, 0xFF475569, 0xFFF1F5F9, onClick);
        tv.setGravity(Gravity.CENTER);
        tv.setPadding(dp(4), dp(8), dp(4), dp(8));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, weight);
        if (leftMargin) lp.leftMargin = dp(4);
        tv.setLayoutParams(lp);
        return tv;
    }

    private TextView createChip(
            String label, int bgColor, int strokeColor, int textColor, Runnable onClick) {
        TextView tv = new TextView(this);
        tv.setText(label);
        tv.setTextSize(10.5f);
        tv.setTypeface(null, Typeface.BOLD);
        tv.setTextColor(textColor);
        tv.setSingleLine(true);
        tv.setEllipsize(TextUtils.TruncateAt.END);
        tv.setPadding(dp(9), dp(5), dp(9), dp(5));
        tv.setBackground(PhoneUiKit.roundedBox(bgColor, strokeColor, dp(9), Math.max(1, dp(1))));
        tv.setClickable(onClick != null);
        tv.setFocusable(onClick != null);
        if (onClick != null) tv.setOnClickListener(v -> onClick.run());
        return tv;
    }

    private void showToast(String msg) {
        if (activeToast != null) activeToast.cancel();
        activeToast = Toast.makeText(this, msg, Toast.LENGTH_SHORT);
        activeToast.show();
    }

    private int dp(int size) {
        return Math.round(getResources().getDisplayMetrics().density * size);
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
