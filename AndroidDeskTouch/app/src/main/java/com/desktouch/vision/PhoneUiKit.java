package com.desktouch.vision;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.Switch;
import android.widget.TextView;
import java.util.function.Consumer;
import java.util.function.IntConsumer;

/**
 * Reusable stock-SDK UI components and theme palettes for DeskTouch Vision.
 * Requires zero AppCompat or Material dependencies.
 */
final class PhoneUiKit {
    static final int THEME_SLATE = 0;
    static final int THEME_AMOLED = 1;
    static final int THEME_LIGHT = 2;

    static final String PREFS = "desktouch_prefs";
    static final String PREF_THEME = "ui_theme";

    static final int BTN_PRIMARY = 0;
    static final int BTN_ACTIVE_GREEN = 1;
    static final int BTN_SUBTLE = 2;
    static final int BTN_TINT_ACCENT = 3;
    static final int BTN_TINT_INDIGO = 4;
    static final int BTN_TINT_EMERALD = 5;
    static final int BTN_TINT_AMBER = 6;
    static final int BTN_DANGER = 7;

    static final int TAB_HOME = 0;
    static final int TAB_FIND = 1;
    static final int TAB_TRACKPAD = 2;
    static final int TAB_SPEED = 3;
    static final int TAB_SETUP = 4;

    private PhoneUiKit() {}

    static final class Palette {
        final int themeId;
        final boolean lightBars;
        final int canvasBg;
        final int chromeBg;
        final int chromeBorder;
        final int cardBg;
        final int subcardBg;
        final int subtleBtnBg;
        final int subtleBtnPressed;
        final int border;
        final int divider;
        final int textPrimary;
        final int textSecondary;
        final int textMuted;
        final int accent;
        final int accentPressed;
        final int accentSoftBg;
        final int accentText;
        final int accentBorder;
        final int indigoSoftBg;
        final int indigoText;
        final int indigoBorder;
        final int emerald;
        final int emeraldPressed;
        final int emeraldSoftBg;
        final int emeraldText;
        final int emeraldBorder;
        final int amber;
        final int amberSoftBg;
        final int amberText;
        final int amberBorder;
        final int dangerSoftBg;
        final int dangerText;
        final int dangerBorder;
        final int neutralBadgeBg;
        final int neutralBadgeText;
        final int touchpadBg;
        final int touchpadGrid;
        final int touchpadBorder;
        final int touchpadText;
        final int touchpadSubText;
        final int switchTrackOff;
        final int rippleColor;

        Palette(int themeId, boolean lightBars, int canvasBg, int chromeBg, int chromeBorder,
                int cardBg, int subcardBg, int subtleBtnBg, int subtleBtnPressed,
                int border, int divider, int textPrimary, int textSecondary, int textMuted,
                int accent, int accentPressed, int accentSoftBg, int accentText, int accentBorder,
                int indigoSoftBg, int indigoText, int indigoBorder,
                int emerald, int emeraldPressed, int emeraldSoftBg, int emeraldText, int emeraldBorder,
                int amber, int amberSoftBg, int amberText, int amberBorder,
                int dangerSoftBg, int dangerText, int dangerBorder,
                int neutralBadgeBg, int neutralBadgeText,
                int touchpadBg, int touchpadGrid, int touchpadBorder, int touchpadText, int touchpadSubText,
                int switchTrackOff, int rippleColor) {
            this.themeId = themeId;
            this.lightBars = lightBars;
            this.canvasBg = canvasBg;
            this.chromeBg = chromeBg;
            this.chromeBorder = chromeBorder;
            this.cardBg = cardBg;
            this.subcardBg = subcardBg;
            this.subtleBtnBg = subtleBtnBg;
            this.subtleBtnPressed = subtleBtnPressed;
            this.border = border;
            this.divider = divider;
            this.textPrimary = textPrimary;
            this.textSecondary = textSecondary;
            this.textMuted = textMuted;
            this.accent = accent;
            this.accentPressed = accentPressed;
            this.accentSoftBg = accentSoftBg;
            this.accentText = accentText;
            this.accentBorder = accentBorder;
            this.indigoSoftBg = indigoSoftBg;
            this.indigoText = indigoText;
            this.indigoBorder = indigoBorder;
            this.emerald = emerald;
            this.emeraldPressed = emeraldPressed;
            this.emeraldSoftBg = emeraldSoftBg;
            this.emeraldText = emeraldText;
            this.emeraldBorder = emeraldBorder;
            this.amber = amber;
            this.amberSoftBg = amberSoftBg;
            this.amberText = amberText;
            this.amberBorder = amberBorder;
            this.dangerSoftBg = dangerSoftBg;
            this.dangerText = dangerText;
            this.dangerBorder = dangerBorder;
            this.neutralBadgeBg = neutralBadgeBg;
            this.neutralBadgeText = neutralBadgeText;
            this.touchpadBg = touchpadBg;
            this.touchpadGrid = touchpadGrid;
            this.touchpadBorder = touchpadBorder;
            this.touchpadText = touchpadText;
            this.touchpadSubText = touchpadSubText;
            this.switchTrackOff = switchTrackOff;
            this.rippleColor = rippleColor;
        }
    }

    private static final Palette PALETTE_SLATE = new Palette(
        THEME_SLATE, false,
        0xFF12161F, 0xFF171D29, 0xFF1E293B,
        0xFF1B2230, 0xFF131822, 0xFF263042, 0xFF2F3B52,
        0xB3334155, 0x99334155, 0xFFF1F5F9, 0xFF94A3B8, 0xFF64748B,
        0xFF0EA5E9, 0xFF38BDF8, 0x260EA5E9, 0xFF38BDF8, 0x5538BDF8,
        0x2E6366F1, 0xFFA5B4FC, 0x4D818CF8,
        0xFF10B981, 0xFF34D399, 0x2610B981, 0xFF34D399, 0x4D10B981,
        0xFFF59E0B, 0x33F59E0B, 0xFFFBBF24, 0x66F59E0B,
        0x26F43F5E, 0xFFFB7185, 0x4DF43F5E,
        0x3364748B, 0xFF94A3B8,
        0xFF0F172A, 0x1694A3B8, 0x550EA5E9, 0xFFE0F2FE, 0xBF7DD3FC,
        0xFF334155, 0x33FFFFFF
    );

    private static final Palette PALETTE_AMOLED = new Palette(
        THEME_AMOLED, false,
        0xFF000000, 0xFF0A0C10, 0xFF27272A,
        0xFF11141B, 0xFF080A0E, 0xFF1B202B, 0xFF252C3B,
        0xFF27272A, 0xFF27272A, 0xFFF4F4F5, 0xFFA1A1AA, 0xFF71717A,
        0xFF0EA5E9, 0xFF38BDF8, 0x260EA5E9, 0xFF38BDF8, 0x5538BDF8,
        0x2E6366F1, 0xFFA5B4FC, 0x4D818CF8,
        0xFF10B981, 0xFF34D399, 0x2610B981, 0xFF34D399, 0x4D10B981,
        0xFFF59E0B, 0x33F59E0B, 0xFFFBBF24, 0x66F59E0B,
        0x26F43F5E, 0xFFFB7185, 0x4DF43F5E,
        0x3371717A, 0xFFA1A1AA,
        0xFF070A10, 0x1694A3B8, 0x550EA5E9, 0xFFE0F2FE, 0xBF7DD3FC,
        0xFF27272A, 0x33FFFFFF
    );

    private static final Palette PALETTE_LIGHT = new Palette(
        THEME_LIGHT, true,
        0xFFF1F5F9, 0xFFFFFFFF, 0xFFE2E8F0,
        0xFFFFFFFF, 0xFFF8FAFC, 0xFFF1F5F9, 0xFFE2E8F0,
        0xFFE2E8F0, 0xFFE2E8F0, 0xFF0F172A, 0xFF475569, 0xFF64748B,
        0xFF0EA5E9, 0xFF0284C7, 0x1A0EA5E9, 0xFF0284C7, 0x550284C7,
        0x1A6366F1, 0xFF4338CA, 0x4D6366F1,
        0xFF10B981, 0xFF059669, 0x1F10B981, 0xFF059669, 0x4D10B981,
        0xFFF59E0B, 0x26F59E0B, 0xFFD97706, 0x59D97706,
        0x1AF43F5E, 0xFFE11D48, 0x4DE11D48,
        0x1F64748B, 0xFF475569,
        0xFFEBF3FC, 0x180284C7, 0x4D0284C7, 0xFF0C4A6E, 0xFF0369A1,
        0xFFCBD5E1, 0x1A0F172A
    );

    static int clampTheme(int theme) {
        if (theme == THEME_AMOLED || theme == THEME_LIGHT) return theme;
        return THEME_SLATE;
    }

    static String themeName(int theme) {
        switch (clampTheme(theme)) {
            case THEME_AMOLED: return "AMOLED Black";
            case THEME_LIGHT: return "Crisp Light";
            default: return "Midnight Slate";
        }
    }

    static Palette palette(int theme) {
        switch (clampTheme(theme)) {
            case THEME_AMOLED: return PALETTE_AMOLED;
            case THEME_LIGHT: return PALETTE_LIGHT;
            default: return PALETTE_SLATE;
        }
    }

    static int readTheme(Context context) {
        if (context == null) return THEME_SLATE;
        int raw = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getInt(PREF_THEME, THEME_SLATE);
        return clampTheme(raw);
    }

    static void writeTheme(Context context, int theme) {
        if (context == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putInt(PREF_THEME, clampTheme(theme)).apply();
    }

    static int mouseModeToSegment(int mode) {
        if (mode == DeskTouchSession.STREAM_MODE_FORCE_MOBILE) return 1;
        if (mode == DeskTouchSession.STREAM_MODE_FORCE_WIFI) return 2;
        return 0;
    }

    static int segmentToMouseMode(int index) {
        if (index == 1) return DeskTouchSession.STREAM_MODE_FORCE_MOBILE;
        if (index == 2) return DeskTouchSession.STREAM_MODE_FORCE_WIFI;
        return DeskTouchSession.STREAM_MODE_AUTO;
    }

    static int keyboardModeToSegment(int mode) {
        if (mode == DeskTouchSession.STREAM_MODE_FORCE_WIFI) return 1;
        if (mode == DeskTouchSession.STREAM_MODE_FORCE_MOBILE) return 2;
        return 0;
    }

    static int segmentToKeyboardMode(int index) {
        if (index == 1) return DeskTouchSession.STREAM_MODE_FORCE_WIFI;
        if (index == 2) return DeskTouchSession.STREAM_MODE_FORCE_MOBILE;
        return DeskTouchSession.STREAM_MODE_AUTO;
    }

    static int pointerFeelToSegment(int feel) {
        if (feel == PointerSmoother.FEEL_RAW_DIRECT) return 1;
        if (feel == PointerSmoother.FEEL_SMOOTH) return 2;
        return 0;
    }

    static int segmentToPointerFeel(int index) {
        if (index == 1) return PointerSmoother.FEEL_RAW_DIRECT;
        if (index == 2) return PointerSmoother.FEEL_SMOOTH;
        return PointerSmoother.FEEL_AUTO;
    }

    static int dp(Context ctx, int size) {
        return Math.round(ctx.getResources().getDisplayMetrics().density * size);
    }

    static GradientDrawable roundedBox(int fill, int stroke, float radiusPx, int strokeWidthPx) {
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.RECTANGLE);
        d.setColor(fill);
        d.setCornerRadius(radiusPx);
        if (strokeWidthPx > 0 && stroke != Color.TRANSPARENT) {
            d.setStroke(strokeWidthPx, stroke);
        }
        return d;
    }

    static Drawable interactiveBg(int normalColor, int pressedColor, int strokeColor,
            float radiusPx, int strokeWidthPx, int rippleColor) {
        GradientDrawable normal = roundedBox(normalColor, strokeColor, radiusPx, strokeWidthPx);
        GradientDrawable mask = roundedBox(Color.WHITE, Color.TRANSPARENT, radiusPx, 0);
        int ripple = rippleColor != 0 ? rippleColor
            : Color.argb(48, Color.red(pressedColor), Color.green(pressedColor), Color.blue(pressedColor));
        return new RippleDrawable(ColorStateList.valueOf(ripple), normal, mask);
    }

    static LinearLayout createCard(Context ctx, Palette p, int paddingDp) {
        LinearLayout card = new LinearLayout(ctx);
        card.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(ctx, paddingDp);
        card.setPadding(pad, pad, pad, pad);
        styleCard(card, p, ctx);
        return card;
    }

    static LinearLayout createSubcard(Context ctx, Palette p, int paddingDp) {
        LinearLayout sub = new LinearLayout(ctx);
        sub.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(ctx, paddingDp);
        sub.setPadding(pad, pad, pad, pad);
        styleSubcard(sub, p, ctx);
        return sub;
    }

    static void styleCard(View v, Palette p, Context ctx) {
        v.setBackground(roundedBox(p.cardBg, p.border, dp(ctx, 16), Math.max(1, dp(ctx, 1))));
    }

    static void styleSubcard(View v, Palette p, Context ctx) {
        v.setBackground(roundedBox(p.subcardBg, p.border, dp(ctx, 12), Math.max(1, dp(ctx, 1))));
    }

    static TextView createSectionLabel(Context ctx, String text, Palette p) {
        TextView tv = new TextView(ctx);
        tv.setText(text);
        tv.setTextSize(11.5f);
        tv.setTypeface(null, Typeface.BOLD);
        tv.setLetterSpacing(0.05f);
        tv.setTextColor(p.textSecondary);
        return tv;
    }

    static View createDivider(Context ctx, Palette p) {
        View line = new View(ctx);
        line.setBackgroundColor(p.divider);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, Math.max(1, dp(ctx, 1)));
        lp.topMargin = dp(ctx, 10);
        lp.bottomMargin = dp(ctx, 10);
        line.setLayoutParams(lp);
        return line;
    }

    static Button createButton(Context ctx, String label, Palette p, int style) {
        Button b = new Button(ctx);
        b.setText(label);
        b.setAllCaps(false);
        b.setStateListAnimator(null);
        b.setElevation(0f);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setMinWidth(0);
        b.setMinimumWidth(0);
        b.setGravity(Gravity.CENTER);
        b.setTextSize(12.5f);
        b.setPadding(dp(ctx, 12), dp(ctx, 10), dp(ctx, 12), dp(ctx, 10));
        b.setMaxLines(2);
        b.setEllipsize(TextUtils.TruncateAt.END);
        styleButton(b, p, style, ctx);
        return b;
    }

    static void styleButton(TextView btn, Palette p, int style, Context ctx) {
        int bg;
        int pressed;
        int stroke;
        int text;
        boolean bold = false;
        switch (style) {
            case BTN_PRIMARY:
                bg = p.accent; pressed = p.accentPressed; stroke = Color.TRANSPARENT;
                text = Color.WHITE; bold = true;
                break;
            case BTN_ACTIVE_GREEN:
                bg = p.emerald; pressed = p.emeraldPressed; stroke = Color.TRANSPARENT;
                text = Color.WHITE; bold = true;
                break;
            case BTN_TINT_ACCENT:
                bg = p.accentSoftBg; pressed = p.accentSoftBg; stroke = p.accentBorder;
                text = p.accentText; bold = true;
                break;
            case BTN_TINT_INDIGO:
                bg = p.indigoSoftBg; pressed = p.indigoSoftBg; stroke = p.indigoBorder;
                text = p.indigoText; bold = true;
                break;
            case BTN_TINT_EMERALD:
                bg = p.emeraldSoftBg; pressed = p.emeraldSoftBg; stroke = p.emeraldBorder;
                text = p.emeraldText; bold = true;
                break;
            case BTN_TINT_AMBER:
                bg = p.amberSoftBg; pressed = p.amberSoftBg; stroke = p.amberBorder;
                text = p.amberText; bold = true;
                break;
            case BTN_DANGER:
                bg = p.dangerSoftBg; pressed = p.dangerSoftBg; stroke = p.dangerBorder;
                text = p.dangerText; bold = true;
                break;
            default:
                bg = p.subtleBtnBg; pressed = p.subtleBtnPressed; stroke = p.border;
                text = p.textPrimary;
                break;
        }
        int strokePx = stroke == Color.TRANSPARENT ? 0 : Math.max(1, dp(ctx, 1));
        btn.setBackground(interactiveBg(bg, pressed, stroke, dp(ctx, 12), strokePx, p.rippleColor));
        btn.setTextColor(text);
        btn.setTypeface(null, bold ? Typeface.BOLD : Typeface.NORMAL);
    }

    static void stylePill(TextView tv, int bgColor, int strokeColor, int textColor, Context ctx) {
        int strokePx = strokeColor == Color.TRANSPARENT ? 0 : Math.max(1, dp(ctx, 1));
        tv.setBackground(roundedBox(bgColor, strokeColor, dp(ctx, 999), strokePx));
        tv.setTextColor(textColor);
    }

    static void styleSwitch(Switch sw, Palette p, Context ctx) {
        sw.setText("");
        sw.setTextOn("");
        sw.setTextOff("");
        sw.setShowText(false);
        sw.setSplitTrack(false);
        sw.setSwitchMinWidth(dp(ctx, 42));
        sw.setMinWidth(0);
        sw.setMinimumWidth(0);
        sw.setMinHeight(0);
        sw.setMinimumHeight(0);

        StateListDrawable track = new StateListDrawable();
        GradientDrawable trackOn = new GradientDrawable();
        trackOn.setShape(GradientDrawable.RECTANGLE);
        trackOn.setCornerRadius(dp(ctx, 12));
        trackOn.setColor(p.accent);
        trackOn.setSize(dp(ctx, 42), dp(ctx, 24));

        GradientDrawable trackOff = new GradientDrawable();
        trackOff.setShape(GradientDrawable.RECTANGLE);
        trackOff.setCornerRadius(dp(ctx, 12));
        trackOff.setColor(p.switchTrackOff);
        trackOff.setSize(dp(ctx, 42), dp(ctx, 24));

        track.addState(new int[]{android.R.attr.state_checked}, trackOn);
        track.addState(new int[]{}, trackOff);
        sw.setTrackDrawable(track);

        GradientDrawable knob = new GradientDrawable();
        knob.setShape(GradientDrawable.OVAL);
        knob.setColor(Color.WHITE);
        knob.setSize(dp(ctx, 18), dp(ctx, 18));
        LayerDrawable thumb = new LayerDrawable(new Drawable[]{knob});
        int inset = dp(ctx, 3);
        thumb.setLayerInset(0, inset, inset, inset, inset);
        sw.setThumbDrawable(thumb);
    }

    /** Segmented 3-option bar that highlights selection without recreating child views. */
    static final class SegmentedControl {
        final LinearLayout container;
        final TextView[] segments;
        int selectedIndex;
        Palette palette;

        SegmentedControl(Context ctx, Palette initial, String[] labels, int initialIndex, IntConsumer onSelect) {
            this.palette = initial;
            this.selectedIndex = Math.max(0, Math.min(labels.length - 1, initialIndex));
            this.container = new LinearLayout(ctx);
            container.setOrientation(LinearLayout.HORIZONTAL);
            int pad = dp(ctx, 4);
            container.setPadding(pad, pad, pad, pad);
            this.segments = new TextView[labels.length];
            for (int i = 0; i < labels.length; i++) {
                final int idx = i;
                TextView tab = new TextView(ctx);
                tab.setText(labels[i]);
                tab.setGravity(Gravity.CENTER);
                tab.setTextSize(12f);
                tab.setPadding(dp(ctx, 6), dp(ctx, 8), dp(ctx, 6), dp(ctx, 8));
                tab.setSingleLine(true);
                tab.setEllipsize(TextUtils.TruncateAt.END);
                tab.setClickable(true);
                tab.setFocusable(true);
                tab.setOnClickListener(v -> {
                    setSelectedIndex(idx);
                    if (onSelect != null) onSelect.accept(idx);
                });
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
                if (i > 0) lp.leftMargin = dp(ctx, 3);
                container.addView(tab, lp);
                segments[i] = tab;
            }
            applyPalette(initial);
        }

        View getView() { return container; }

        void setSelectedIndex(int index) {
            if (index < 0 || index >= segments.length) return;
            this.selectedIndex = index;
            refreshStyles();
        }

        void applyPalette(Palette p) {
            this.palette = p;
            container.setBackground(roundedBox(p.subcardBg, p.border, dp(container.getContext(), 12),
                Math.max(1, dp(container.getContext(), 1))));
            refreshStyles();
        }

        private void refreshStyles() {
            Context ctx = container.getContext();
            for (int i = 0; i < segments.length; i++) {
                TextView tv = segments[i];
                boolean on = i == selectedIndex;
                if (on) {
                    tv.setBackground(roundedBox(palette.accent, Color.TRANSPARENT, dp(ctx, 9), 0));
                    tv.setTextColor(Color.WHITE);
                    tv.setTypeface(null, Typeface.BOLD);
                } else {
                    tv.setBackground(interactiveBg(Color.TRANSPARENT, palette.subtleBtnBg,
                        Color.TRANSPARENT, dp(ctx, 9), 0, palette.rippleColor));
                    tv.setTextColor(palette.textSecondary);
                    tv.setTypeface(null, Typeface.NORMAL);
                }
            }
        }
    }

    /** Clean switch card row with title, subtitle, and styled android.widget.Switch. */
    static final class SwitchRow {
        final LinearLayout root;
        final TextView titleView;
        final TextView subtitleView;
        final Switch toggle;
        private boolean suppressListener;

        SwitchRow(Context ctx, Palette initial, String title, String subtitle,
                  boolean initialChecked, Consumer<Boolean> onUserToggle) {
            root = new LinearLayout(ctx);
            root.setOrientation(LinearLayout.HORIZONTAL);
            root.setGravity(Gravity.CENTER_VERTICAL);
            root.setPadding(dp(ctx, 2), dp(ctx, 4), dp(ctx, 2), dp(ctx, 4));
            root.setClickable(true);
            root.setFocusable(true);

            LinearLayout labels = new LinearLayout(ctx);
            labels.setOrientation(LinearLayout.VERTICAL);
            titleView = new TextView(ctx);
            titleView.setText(title);
            titleView.setTextSize(13f);
            titleView.setTypeface(null, Typeface.BOLD);
            labels.addView(titleView);

            subtitleView = new TextView(ctx);
            subtitleView.setText(subtitle);
            subtitleView.setTextSize(11.5f);
            subtitleView.setPadding(0, dp(ctx, 2), 0, 0);
            labels.addView(subtitleView);

            LinearLayout.LayoutParams labelParams = new LinearLayout.LayoutParams(0, -2, 1f);
            labelParams.rightMargin = dp(ctx, 12);
            root.addView(labels, labelParams);

            toggle = new Switch(ctx);
            toggle.setChecked(initialChecked);
            root.addView(toggle, new LinearLayout.LayoutParams(-2, -2));

            toggle.setOnCheckedChangeListener((btn, isChecked) -> {
                if (!suppressListener && onUserToggle != null) {
                    onUserToggle.accept(isChecked);
                }
            });
            root.setOnClickListener(v -> toggle.toggle());

            applyPalette(initial);
        }

        View getView() { return root; }

        boolean isChecked() { return toggle.isChecked(); }

        void setChecked(boolean checked) {
            if (toggle.isChecked() == checked) return;
            suppressListener = true;
            toggle.setChecked(checked);
            suppressListener = false;
        }

        void applyPalette(Palette p) {
            Context ctx = root.getContext();
            titleView.setTextColor(p.textPrimary);
            subtitleView.setTextColor(p.textSecondary);
            styleSwitch(toggle, p, ctx);
        }
    }

    /** Crisp vector-style Canvas-drawn icon for bottom navigation tabs. */
    static final class TabIconView extends View {
        private final int iconType;
        private final Paint strokePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Path path = new Path();
        private int iconColor = Color.WHITE;

        TabIconView(Context ctx, int iconType) {
            super(ctx);
            this.iconType = iconType;
            strokePaint.setStyle(Paint.Style.STROKE);
            strokePaint.setStrokeCap(Paint.Cap.ROUND);
            strokePaint.setStrokeJoin(Paint.Join.ROUND);
        }

        void setColor(int color) {
            if (this.iconColor != color) {
                this.iconColor = color;
                invalidate();
            }
        }

        @Override
        protected void onDraw(Canvas c) {
            super.onDraw(c);
            int w = getWidth();
            int h = getHeight();
            if (w <= 0 || h <= 0) return;
            float s = Math.min(w, h) / 24f;
            float ox = (w - 24f * s) * 0.5f;
            float oy = (h - 24f * s) * 0.5f;
            strokePaint.setColor(iconColor);
            strokePaint.setStrokeWidth(Math.max(1.5f, 1.9f * s));
            path.reset();

            switch (iconType) {
                case TAB_HOME:
                    path.moveTo(ox + 3f * s, oy + 10.5f * s);
                    path.lineTo(ox + 12f * s, oy + 3f * s);
                    path.lineTo(ox + 21f * s, oy + 10.5f * s);
                    path.lineTo(ox + 21f * s, oy + 20f * s);
                    path.lineTo(ox + 14.5f * s, oy + 20f * s);
                    path.lineTo(ox + 14.5f * s, oy + 14.5f * s);
                    path.lineTo(ox + 9.5f * s, oy + 14.5f * s);
                    path.lineTo(ox + 9.5f * s, oy + 20f * s);
                    path.lineTo(ox + 3f * s, oy + 20f * s);
                    path.close();
                    c.drawPath(path, strokePaint);
                    break;
                case TAB_FIND:
                    c.drawCircle(ox + 10.5f * s, oy + 10.5f * s, 6f * s, strokePaint);
                    c.drawLine(ox + 15f * s, oy + 15f * s, ox + 20f * s, oy + 20f * s, strokePaint);
                    break;
                case TAB_TRACKPAD:
                    c.drawRoundRect(ox + 3.5f * s, oy + 4f * s, ox + 20.5f * s, oy + 20f * s,
                        3f * s, 3f * s, strokePaint);
                    c.drawLine(ox + 3.5f * s, oy + 15f * s, ox + 20.5f * s, oy + 15f * s, strokePaint);
                    c.drawLine(ox + 12f * s, oy + 15f * s, ox + 12f * s, oy + 20f * s, strokePaint);
                    break;
                case TAB_SPEED:
                    path.moveTo(ox + 13.5f * s, oy + 2.5f * s);
                    path.lineTo(ox + 4.5f * s, oy + 13.5f * s);
                    path.lineTo(ox + 11f * s, oy + 13.5f * s);
                    path.lineTo(ox + 10f * s, oy + 21.5f * s);
                    path.lineTo(ox + 19.5f * s, oy + 10.5f * s);
                    path.lineTo(ox + 13f * s, oy + 10.5f * s);
                    path.close();
                    c.drawPath(path, strokePaint);
                    break;
                case TAB_SETUP:
                default:
                    c.drawLine(ox + 3.5f * s, oy + 6f * s, ox + 6.7f * s, oy + 6f * s, strokePaint);
                    c.drawCircle(ox + 8.5f * s, oy + 6f * s, 1.8f * s, strokePaint);
                    c.drawLine(ox + 10.3f * s, oy + 6f * s, ox + 20.5f * s, oy + 6f * s, strokePaint);

                    c.drawLine(ox + 3.5f * s, oy + 12f * s, ox + 13.7f * s, oy + 12f * s, strokePaint);
                    c.drawCircle(ox + 15.5f * s, oy + 12f * s, 1.8f * s, strokePaint);
                    c.drawLine(ox + 17.3f * s, oy + 12f * s, ox + 20.5f * s, oy + 12f * s, strokePaint);

                    c.drawLine(ox + 3.5f * s, oy + 18f * s, ox + 8.2f * s, oy + 18f * s, strokePaint);
                    c.drawCircle(ox + 10f * s, oy + 18f * s, 1.8f * s, strokePaint);
                    c.drawLine(ox + 11.8f * s, oy + 18f * s, ox + 20.5f * s, oy + 18f * s, strokePaint);
                    break;
            }
        }
    }
}
