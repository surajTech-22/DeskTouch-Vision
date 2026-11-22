package com.desktouch.vision;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * Pure selection, overlay state, persistence, and shortcut helpers shared by the
 * fixed 4x4 Home Screen Touchpad Widget, its floating overlay, and MainActivity.
 */
final class TouchpadWidgetHelper {
    static final String PREFS_NAME = "desktouch_touchpad_widget";
    static final String KEY_SELECTED_TARGET_ID = "selected_target_id";
    static final String KEY_EXPLICIT_UNSELECTED = "explicitly_unselected";
    static final String KEY_HORIZONTAL_MODE = "horizontal_mode";
    static final String KEY_TRACKPAD_HORIZONTAL = "trackpad_horizontal_mode";
    static final int UNSELECTED = -1;

    static final String OVERLAY_NONE = "";
    static final String OVERLAY_KEYS = "keys";
    static final String OVERLAY_CTRL = "ctrl";

    static final String CAT_CTRL = "ctrl";
    static final String CAT_ALT = "alt";
    static final String CAT_WIN = "win";
    static final String CAT_FN = "fn";
    static final String CAT_NAV = "nav";

    static final int VK_BACK = 0x08;
    static final int VK_HOME = 0x24;
    static final int VK_END = 0x23;
    static final int VK_PRIOR = 0x21;
    static final int VK_NEXT = 0x22;

    private static final List<FullscreenPreviewLayoutHelper.ShortcutItem> FN_ITEMS;
    private static final List<FullscreenPreviewLayoutHelper.ShortcutItem> NAV_ITEMS;

    static {
        ArrayList<FullscreenPreviewLayoutHelper.ShortcutItem> fns = new ArrayList<>(12);
        for (int n = 1; n <= 12; n++) {
            fns.add(new FullscreenPreviewLayoutHelper.ShortcutItem(
                "F" + n, "Fn Key", null, 0x70 + (n - 1), ""));
        }
        FN_ITEMS = Collections.unmodifiableList(fns);

        NAV_ITEMS = Collections.unmodifiableList(Arrays.asList(
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "Esc", "Escape", null, FullscreenPreviewLayoutHelper.VK_ESCAPE, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "Tab", "Tab", null, FullscreenPreviewLayoutHelper.VK_TAB, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "⇧ Shift", "Shift", null, FullscreenPreviewLayoutHelper.VK_SHIFT, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "⌫", "Back", null, VK_BACK, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "↵", "Enter", null, FullscreenPreviewLayoutHelper.VK_RETURN, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "Del", "Delete", null, FullscreenPreviewLayoutHelper.VK_DELETE, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "Home", "Start", null, VK_HOME, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "End", "End", null, VK_END, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "PgUp", "Page Up", null, VK_PRIOR, ""),
            new FullscreenPreviewLayoutHelper.ShortcutItem(
                "PgDn", "Page Down", null, VK_NEXT, "")
        ));
    }

    private TouchpadWidgetHelper() {}

    /** Toggles selection for a PC pill: tapping the active PC again unselects it. */
    static int togglePcSelection(int currentSelectedId, int clickedTargetId) {
        if (clickedTargetId < 0) return UNSELECTED;
        return currentSelectedId == clickedTargetId ? UNSELECTED : clickedTargetId;
    }

    /** Mutually exclusive toggle between floating widget overlays ("keys" vs "ctrl"). */
    static String toggleFloatingOverlay(String currentOverlay, String requestedOverlay) {
        String normReq = normalizeOverlay(requestedOverlay);
        if (OVERLAY_NONE.equals(normReq)) return OVERLAY_NONE;
        String normCur = normalizeOverlay(currentOverlay);
        return normReq.equals(normCur) ? OVERLAY_NONE : normReq;
    }

    private static String normalizeOverlay(String raw) {
        if (OVERLAY_KEYS.equals(raw)) return OVERLAY_KEYS;
        if (OVERLAY_CTRL.equals(raw)) return OVERLAY_CTRL;
        return OVERLAY_NONE;
    }

    static boolean isTargetStillAvailable(
            int selectedId, List<LinkedDisplayHelper.DisplayTileModel> pcs) {
        if (selectedId < 0 || pcs == null || pcs.isEmpty()) return false;
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            if (pc != null && pc.id == selectedId) return true;
        }
        return false;
    }

    static int resolveEffectiveSelection(
            int storedSelectedId,
            boolean explicitlyUnselected,
            List<LinkedDisplayHelper.DisplayTileModel> pcs) {
        if (explicitlyUnselected || pcs == null || pcs.isEmpty()) {
            return UNSELECTED;
        }
        if (isTargetStillAvailable(storedSelectedId, pcs)) {
            return storedSelectedId;
        }
        for (LinkedDisplayHelper.DisplayTileModel pc : pcs) {
            if (pc != null && pc.selected && pc.id >= 0) return pc.id;
        }
        LinkedDisplayHelper.DisplayTileModel first = pcs.get(0);
        return first != null && first.id >= 0 ? first.id : UNSELECTED;
    }

    static List<FullscreenPreviewLayoutHelper.ShortcutItem> shortcutsForCategory(String category) {
        if (CAT_CTRL.equals(category) || CAT_ALT.equals(category) || CAT_WIN.equals(category)) {
            return FullscreenPreviewLayoutHelper.shortcutsFor(category);
        }
        if (CAT_FN.equals(category)) return FN_ITEMS;
        if (CAT_NAV.equals(category)) return NAV_ITEMS;
        return Collections.emptyList();
    }

    static int loadSelectedTargetId(Context context) {
        SharedPreferences p = prefs(context);
        return p != null ? p.getInt(KEY_SELECTED_TARGET_ID, UNSELECTED) : UNSELECTED;
    }

    static boolean isExplicitlyUnselected(Context context) {
        SharedPreferences p = prefs(context);
        return p != null && p.getBoolean(KEY_EXPLICIT_UNSELECTED, false);
    }

    static void saveSelectedTarget(Context context, int targetId, boolean explicitlyUnselected) {
        SharedPreferences p = prefs(context);
        if (p == null) return;
        p.edit()
            .putInt(KEY_SELECTED_TARGET_ID, targetId >= 0 ? targetId : UNSELECTED)
            .putBoolean(KEY_EXPLICIT_UNSELECTED, explicitlyUnselected)
            .apply();
    }

    static boolean isWidgetHorizontal(Context context) {
        SharedPreferences p = prefs(context);
        return p != null && p.getBoolean(KEY_HORIZONTAL_MODE, false);
    }

    static void setWidgetHorizontal(Context context, boolean horizontal) {
        SharedPreferences p = prefs(context);
        if (p == null) return;
        p.edit().putBoolean(KEY_HORIZONTAL_MODE, horizontal).apply();
    }

    static boolean isTrackpadHorizontal(Context context) {
        SharedPreferences p = prefs(context);
        return p != null && p.getBoolean(KEY_TRACKPAD_HORIZONTAL, false);
    }

    static void setTrackpadHorizontal(Context context, boolean horizontal) {
        SharedPreferences p = prefs(context);
        if (p == null) return;
        p.edit().putBoolean(KEY_TRACKPAD_HORIZONTAL, horizontal).apply();
    }

    private static SharedPreferences prefs(Context context) {
        if (context == null) return null;
        try {
            return context.getApplicationContext()
                .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        } catch (Throwable ignored) {
            return null;
        }
    }

    /** Sends a shortcut chord or power lock action to the active Windows session. */
    static boolean sendWindowsKeyCombo(
            DeskTouchSession act, int[] modifierVks, int mainVk, String powerAction) {
        if (act == null || !act.isConnected()) return false;
        if (!act.isActive() && !act.hasClaim()) act.claimControl();
        if ("lock".equals(powerAction)) {
            int tid = Math.max(0, act.selectedEnvironmentTarget());
            act.sendPowerAction(tid, "lock");
            return true;
        }
        if (modifierVks != null) {
            for (int mv : modifierVks) {
                if (mv > 0) act.key(mv, true);
            }
        }
        if (mainVk > 0) {
            act.key(mainVk, true);
            act.key(mainVk, false);
        }
        if (modifierVks != null) {
            for (int i = modifierVks.length - 1; i >= 0; i--) {
                if (modifierVks[i] > 0) act.key(modifierVks[i], false);
            }
        }
        return false;
    }
}
