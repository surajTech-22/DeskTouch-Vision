package com.desktouch.vision;

import android.content.Context;
import android.content.SharedPreferences;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.json.JSONObject;

/**
 * Pure layout geometry, stacking/swap math, persistence, and shortcut catalog for the
 * Android Full-Screen Preview customizable blank-area HUD and side rails.
 */
final class FullscreenPreviewLayoutHelper {
    static final String PREFS_NAME = "desktouch_fullscreen_preview_ui";
    static final String KEY_STATE_JSON = "layout_state_v1";

    static final float MIN_RAIL_HEIGHT_DP = 114f;
    static final float COLLAPSED_HEIGHT_DP = 34f;
    static final float RAIL_GAP_DP = 8f;
    static final float RAIL_WIDTH_DP = 62f;
    static final float RAIL_EDGE_DP = 8f;
    static final float HUD_CLEARANCE_DP = 56f;
    static final float EDGE_PAD_DP = 10f;
    static final long STATUS_AUTO_HIDE_MS = 5000L;

    static final int VK_CONTROL = 0x11;
    static final int VK_SHIFT = 0x10;
    static final int VK_MENU = 0x12; // Alt
    static final int VK_LWIN = 0x5B;
    static final int VK_TAB = 0x09;
    static final int VK_RETURN = 0x0D;
    static final int VK_ESCAPE = 0x1B;
    static final int VK_SPACE = 0x20;
    static final int VK_LEFT = 0x25;
    static final int VK_UP = 0x26;
    static final int VK_RIGHT = 0x27;
    static final int VK_DOWN = 0x28;
    static final int VK_SNAPSHOT = 0x2C;
    static final int VK_DELETE = 0x2E;
    static final int VK_F4 = 0x73;
    static final int VK_OEM_PERIOD = 0xBE;

    static final class RailState {
        String side;
        float baseTopDp;
        float baseHeightDp;
        float topDp;
        float heightDp;
        boolean collapsed;
        int stackOrder;

        RailState(
                String side,
                float baseTopDp,
                float baseHeightDp,
                boolean collapsed,
                int stackOrder) {
            this.side = "right".equals(side) ? "right" : "left";
            this.baseTopDp = baseTopDp;
            this.baseHeightDp = Math.max(MIN_RAIL_HEIGHT_DP, baseHeightDp);
            this.topDp = this.baseTopDp;
            this.heightDp = this.baseHeightDp;
            this.collapsed = collapsed;
            this.stackOrder = stackOrder;
        }

        RailState copy() {
            RailState r = new RailState(side, baseTopDp, baseHeightDp, collapsed, stackOrder);
            r.topDp = topDp;
            r.heightDp = heightDp;
            return r;
        }
    }

    static final class LayoutState {
        boolean hudCollapsed;
        boolean landscape;
        final RailState fn;
        final RailState mod;

        LayoutState(boolean hudCollapsed, boolean landscape, RailState fn, RailState mod) {
            this.hudCollapsed = hudCollapsed;
            this.landscape = landscape;
            this.fn = fn;
            this.mod = mod;
        }

        static LayoutState defaults() {
            return new LayoutState(
                false,
                true,
                new RailState("left", 12f, 340f, false, 0),
                new RailState("right", 56f, 310f, false, 1));
        }
    }

    static final class MonitorRect {
        final float rootW;
        final float rootH;
        final float monLeft;
        final float monTop;
        final float monRight;
        final float monBot;

        MonitorRect(
                float rootW,
                float rootH,
                float monLeft,
                float monTop,
                float monRight,
                float monBot) {
            this.rootW = rootW;
            this.rootH = rootH;
            this.monLeft = monLeft;
            this.monTop = monTop;
            this.monRight = monRight;
            this.monBot = monBot;
        }
    }

    static final class ShortcutItem {
        final String label;
        final String desc;
        final int[] modifierVks;
        final int mainVk;
        final String powerAction;

        ShortcutItem(String label, String desc, int[] modifierVks, int mainVk, String powerAction) {
            this.label = label;
            this.desc = desc;
            this.modifierVks = modifierVks != null ? modifierVks : new int[0];
            this.mainVk = mainVk;
            this.powerAction = powerAction != null ? powerAction : "";
        }
    }

    private FullscreenPreviewLayoutHelper() {}

    static float clamp(float value, float min, float max) {
        if (max < min) return min;
        return Math.max(min, Math.min(max, value));
    }

    static MonitorRect computeFittedMonitorRect(float rootWDp, float rootHDp, int bmpW, int bmpH) {
        float w = Math.max(1f, rootWDp);
        float h = Math.max(1f, rootHDp);
        float aspect = (bmpW > 0 && bmpH > 0) ? ((float) bmpW / (float) bmpH) : (16f / 9f);
        float rootAspect = w / h;
        float drawnW;
        float drawnH;
        if (rootAspect > aspect) {
            drawnH = h;
            drawnW = h * aspect;
        } else {
            drawnW = w;
            drawnH = w / aspect;
        }
        float left = (w - drawnW) * 0.5f;
        float top = (h - drawnH) * 0.5f;
        return new MonitorRect(w, h, left, top, left + drawnW, top + drawnH);
    }

    static float effectiveRailWidthDp(MonitorRect m) {
        if (m == null) return RAIL_WIDTH_DP;
        float leftAvail = m.monLeft - RAIL_EDGE_DP - 4f;
        float rightAvail = (m.rootW - m.monRight) - RAIL_EDGE_DP - 4f;
        float minPillar = Math.min(leftAvail, rightAvail);
        if (minPillar > 48f) {
            return clamp(Math.min(RAIL_WIDTH_DP, minPillar), 48f, RAIL_WIDTH_DP);
        }
        return RAIL_WIDTH_DP;
    }

    static boolean hasSideBlankPillars(MonitorRect m, float railWidthDp) {
        if (m == null) return true;
        return m.monLeft > railWidthDp + RAIL_EDGE_DP + 4f &&
            (m.rootW - m.monRight) > railWidthDp + RAIL_EDGE_DP + 4f;
    }

    static float getSideMinTopDp(String side) {
        return "right".equals(side) ? HUD_CLEARANCE_DP : 12f;
    }

    static float getMaxAllowedBottomDp(MonitorRect m) {
        float rootH = m != null ? m.rootH : 390f;
        return Math.max(120f, rootH - 12f);
    }

    static float getEffectiveHeightDp(RailState s) {
        return s.collapsed ? COLLAPSED_HEIGHT_DP : s.heightDp;
    }

    static boolean overlapsMonitorRect(
            float candLeft, float candTop, float candW, float candH, MonitorRect m) {
        if (m == null) return false;
        boolean horiz = candLeft < (m.monRight - 2f) && (candLeft + candW) > (m.monLeft + 2f);
        boolean vert = candTop < (m.monBot - 2f) && (candTop + candH) > (m.monTop + 2f);
        return horiz && vert;
    }

    static void resolveLayout(LayoutState state, MonitorRect m, String priorityId) {
        if (state == null || m == null) return;
        String prio = "fn".equals(priorityId) ? "fn" : "mod";
        float railW = effectiveRailWidthDp(m);
        float maxBot = getMaxAllowedBottomDp(m);
        boolean pillarMode = hasSideBlankPillars(m, railW);

        String[] sides = new String[] { "left", "right" };
        for (String side : sides) {
            boolean fnMatch = side.equals(state.fn.side);
            boolean modMatch = side.equals(state.mod.side);
            if (!fnMatch && !modMatch) continue;
            float minTop = getSideMinTopDp(side);

            if (pillarMode) {
                if (fnMatch && modMatch) {
                    fitTwoBandRails(state, "fn", "mod", minTop, maxBot, prio);
                } else {
                    String singleId = fnMatch ? "fn" : "mod";
                    fitSingleBandRail(singleId.equals("fn") ? state.fn : state.mod, minTop, maxBot);
                }
                continue;
            }

            // Portrait letterbox mode: split across top blank band and bottom blank band by baseTopDp
            float topBandMax = Math.max(minTop + COLLAPSED_HEIGHT_DP, m.monTop - 8f);
            float botBandMin = Math.min(maxBot - COLLAPSED_HEIGHT_DP, m.monBot + 8f);
            float midScreen = (m.monTop + m.monBot) * 0.5f;

            if (fnMatch && !modMatch) {
                boolean top = (state.fn.baseTopDp + COLLAPSED_HEIGHT_DP * 0.5f) < midScreen;
                fitSingleBandRail(state.fn, top ? minTop : botBandMin, top ? topBandMax : maxBot);
            } else if (!fnMatch && modMatch) {
                boolean top = (state.mod.baseTopDp + COLLAPSED_HEIGHT_DP * 0.5f) < midScreen;
                fitSingleBandRail(state.mod, top ? minTop : botBandMin, top ? topBandMax : maxBot);
            } else {
                boolean fnTop = (state.fn.baseTopDp + COLLAPSED_HEIGHT_DP * 0.5f) < midScreen;
                boolean modTop = (state.mod.baseTopDp + COLLAPSED_HEIGHT_DP * 0.5f) < midScreen;
                if (fnTop != modTop) {
                    fitSingleBandRail(state.fn, fnTop ? minTop : botBandMin, fnTop ? topBandMax : maxBot);
                    fitSingleBandRail(state.mod, modTop ? minTop : botBandMin, modTop ? topBandMax : maxBot);
                } else if (fnTop) {
                    float minNeed = minNeededHeight(state.fn) + minNeededHeight(state.mod) + RAIL_GAP_DP;
                    if (minNeed <= (topBandMax - minTop)) {
                        fitTwoBandRails(state, "fn", "mod", minTop, topBandMax, prio);
                    } else {
                        String topId = firstInStackOrder(state.fn, state.mod, prio);
                        String botId = "fn".equals(topId) ? "mod" : "fn";
                        fitSingleBandRail("fn".equals(topId) ? state.fn : state.mod, minTop, topBandMax);
                        fitSingleBandRail("fn".equals(botId) ? state.fn : state.mod, botBandMin, maxBot);
                    }
                } else {
                    float minNeed = minNeededHeight(state.fn) + minNeededHeight(state.mod) + RAIL_GAP_DP;
                    if (minNeed <= (maxBot - botBandMin)) {
                        fitTwoBandRails(state, "fn", "mod", botBandMin, maxBot, prio);
                    } else {
                        String topId = firstInStackOrder(state.fn, state.mod, prio);
                        String botId = "fn".equals(topId) ? "mod" : "fn";
                        fitSingleBandRail("fn".equals(topId) ? state.fn : state.mod, minTop, topBandMax);
                        fitSingleBandRail("fn".equals(botId) ? state.fn : state.mod, botBandMin, maxBot);
                    }
                }
            }
        }
    }

    private static float minNeededHeight(RailState s) {
        return s.collapsed ? COLLAPSED_HEIGHT_DP : MIN_RAIL_HEIGHT_DP;
    }

    private static String firstInStackOrder(RailState fn, RailState mod, String prio) {
        if (fn.stackOrder != mod.stackOrder) {
            return fn.stackOrder < mod.stackOrder ? "fn" : "mod";
        }
        if (Math.abs(fn.baseTopDp - mod.baseTopDp) > 1f) {
            return fn.baseTopDp < mod.baseTopDp ? "fn" : "mod";
        }
        return "fn".equals(prio) ? "fn" : "mod";
    }

    private static void fitSingleBandRail(RailState s, float bandMinTop, float bandMaxBot) {
        float avail = Math.max(COLLAPSED_HEIGHT_DP, bandMaxBot - bandMinTop);
        s.heightDp = s.baseHeightDp;
        s.topDp = clamp(s.baseTopDp, bandMinTop, Math.max(bandMinTop, bandMaxBot - COLLAPSED_HEIGHT_DP));
        if (!s.collapsed) {
            float minH = Math.min(MIN_RAIL_HEIGHT_DP, avail);
            float roomBelow = bandMaxBot - s.topDp;
            if (roomBelow < minH) {
                s.topDp = Math.max(bandMinTop, bandMaxBot - minH);
            }
            float maxHere = Math.max(minH, bandMaxBot - s.topDp);
            s.heightDp = clamp(s.baseHeightDp, minH, maxHere);
        }
    }

    private static void fitTwoBandRails(
            LayoutState state, String aId, String bId,
            float bandMinTop, float bandMaxBot, String prio) {
        RailState aS = "fn".equals(aId) ? state.fn : state.mod;
        RailState bS = "fn".equals(bId) ? state.fn : state.mod;
        String topId = firstInStackOrder(aS, bS, prio);
        String botId = topId.equals(aId) ? bId : aId;
        RailState topS = "fn".equals(topId) ? state.fn : state.mod;
        RailState botS = "fn".equals(botId) ? state.fn : state.mod;

        float avail = Math.max(COLLAPSED_HEIGHT_DP, bandMaxBot - bandMinTop);
        float minNeedTop = topS.collapsed ? COLLAPSED_HEIGHT_DP : Math.min(MIN_RAIL_HEIGHT_DP, avail);
        float minNeedBot = botS.collapsed ? COLLAPSED_HEIGHT_DP : Math.min(MIN_RAIL_HEIGHT_DP, avail);

        // Anchor upper header in place at baseTopDp; only shift up if lower rail leaves insufficient min room
        float maxAnchorTopY = Math.max(bandMinTop, bandMaxBot - minNeedTop - RAIL_GAP_DP - minNeedBot);
        topS.topDp = clamp(topS.baseTopDp, bandMinTop, maxAnchorTopY);

        float topH = topS.collapsed ? COLLAPSED_HEIGHT_DP : Math.max(minNeedTop, topS.baseHeightDp);
        float botH = botS.collapsed ? COLLAPSED_HEIGHT_DP : Math.max(minNeedBot, botS.baseHeightDp);

        float remAvail = Math.max(COLLAPSED_HEIGHT_DP, bandMaxBot - topS.topDp);
        float maxTopH = Math.max(minNeedTop, remAvail - RAIL_GAP_DP - minNeedBot);
        float maxBotH = Math.max(minNeedBot, remAvail - RAIL_GAP_DP - minNeedTop);
        if (!topS.collapsed) topH = clamp(topH, minNeedTop, maxTopH);
        if (!botS.collapsed) botH = clamp(botH, minNeedBot, maxBotH);

        float excess = topH + RAIL_GAP_DP + botH - remAvail;
        if (excess > 0f) {
            String firstShrink = prio.equals(topId) ? botId : topId;
            String[] shrinkOrder = new String[] { firstShrink, prio };
            for (String sid : shrinkOrder) {
                if (excess <= 0f) break;
                RailState rs = "fn".equals(sid) ? state.fn : state.mod;
                if (rs.collapsed) continue;
                boolean isTop = sid.equals(topId);
                float curH = isTop ? topH : botH;
                float minH = isTop ? minNeedTop : minNeedBot;
                float drop = Math.min(excess, Math.max(0f, curH - minH));
                if (isTop) topH -= drop;
                else botH -= drop;
                excess -= drop;
            }
        }

        topS.heightDp = topS.collapsed ? topS.baseHeightDp : topH;
        botS.heightDp = botS.collapsed ? botS.baseHeightDp : botH;

        float minBotY = topS.topDp + topH + RAIL_GAP_DP;
        float maxBotY = Math.max(minBotY, bandMaxBot - botH);
        botS.topDp = clamp(botS.baseTopDp, minBotY, maxBotY);
    }

    static void swapRails(LayoutState state) {
        if (state == null) return;
        RailState prevFn = state.fn.copy();
        RailState prevMod = state.mod.copy();

        state.fn.side = prevMod.side;
        state.fn.baseTopDp = prevMod.baseTopDp;
        state.fn.baseHeightDp = prevMod.baseHeightDp;
        state.fn.topDp = prevMod.topDp;
        state.fn.heightDp = prevMod.heightDp;
        state.fn.stackOrder = prevMod.stackOrder;

        state.mod.side = prevFn.side;
        state.mod.baseTopDp = prevFn.baseTopDp;
        state.mod.baseHeightDp = prevFn.baseHeightDp;
        state.mod.topDp = prevFn.topDp;
        state.mod.heightDp = prevFn.heightDp;
        state.mod.stackOrder = prevFn.stackOrder;
    }

    static String toJson(LayoutState state) {
        try {
            JSONObject root = new JSONObject();
            root.put("version", 1);
            root.put("hudCollapsed", state.hudCollapsed);
            root.put("landscape", state.landscape);
            root.put("fn", railToJson(state.fn));
            root.put("mod", railToJson(state.mod));
            return root.toString();
        } catch (Throwable ignored) {
            return "{}";
        }
    }

    private static JSONObject railToJson(RailState r) throws Exception {
        JSONObject o = new JSONObject();
        o.put("side", r.side);
        o.put("baseTopDp", Math.round(r.baseTopDp));
        o.put("baseHeightDp", Math.round(r.baseHeightDp));
        o.put("collapsed", r.collapsed);
        o.put("stackOrder", r.stackOrder);
        return o;
    }

    static LayoutState fromJson(String raw) {
        LayoutState def = LayoutState.defaults();
        if (raw == null || raw.trim().isEmpty()) return def;
        try {
            JSONObject root = new JSONObject(raw);
            boolean hudCollapsed = root.optBoolean("hudCollapsed", def.hudCollapsed);
            boolean landscape = root.optBoolean("landscape", def.landscape);
            RailState fn = railFromJson(root.optJSONObject("fn"), def.fn);
            RailState mod = railFromJson(root.optJSONObject("mod"), def.mod);
            return new LayoutState(hudCollapsed, landscape, fn, mod);
        } catch (Throwable ignored) {
            return def;
        }
    }

    private static RailState railFromJson(JSONObject obj, RailState def) {
        if (obj == null) return def.copy();
        String side = obj.optString("side", def.side);
        if (!"left".equals(side) && !"right".equals(side)) side = def.side;
        double topVal = obj.optDouble("baseTopDp", def.baseTopDp);
        double heightVal = obj.optDouble("baseHeightDp", def.baseHeightDp);
        float baseTop = Double.isNaN(topVal) || Double.isInfinite(topVal)
            ? def.baseTopDp : Math.max(8f, (float) topVal);
        float baseH = Double.isNaN(heightVal) || Double.isInfinite(heightVal)
            ? def.baseHeightDp : Math.max(MIN_RAIL_HEIGHT_DP, (float) heightVal);
        boolean collapsed = obj.optBoolean("collapsed", def.collapsed);
        int stackOrder = obj.optInt("stackOrder", def.stackOrder);
        return new RailState(side, baseTop, baseH, collapsed, stackOrder);
    }

    static LayoutState load(Context context) {
        if (context == null) return LayoutState.defaults();
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            return fromJson(prefs.getString(KEY_STATE_JSON, ""));
        } catch (Throwable ignored) {
            return LayoutState.defaults();
        }
    }

    static void save(Context context, LayoutState state) {
        if (context == null || state == null) return;
        try {
            context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putString(KEY_STATE_JSON, toJson(state))
                .apply();
        } catch (Throwable ignored) {}
    }

    static List<ShortcutItem> shortcutsFor(String modKey) {
        if ("ctrl".equals(modKey)) {
            return Arrays.asList(
                new ShortcutItem("Ctrl+C", "Copy", new int[] { VK_CONTROL }, 'C', ""),
                new ShortcutItem("Ctrl+V", "Paste", new int[] { VK_CONTROL }, 'V', ""),
                new ShortcutItem("Ctrl+X", "Cut", new int[] { VK_CONTROL }, 'X', ""),
                new ShortcutItem("Ctrl+Z", "Undo", new int[] { VK_CONTROL }, 'Z', ""),
                new ShortcutItem("Ctrl+Y", "Redo", new int[] { VK_CONTROL }, 'Y', ""),
                new ShortcutItem("Ctrl+A", "All", new int[] { VK_CONTROL }, 'A', ""),
                new ShortcutItem("Ctrl+S", "Save", new int[] { VK_CONTROL }, 'S', ""),
                new ShortcutItem("Ctrl+F", "Find", new int[] { VK_CONTROL }, 'F', ""),
                new ShortcutItem("Ctrl+P", "Print", new int[] { VK_CONTROL }, 'P', ""),
                new ShortcutItem("Ctrl+N", "New", new int[] { VK_CONTROL }, 'N', ""),
                new ShortcutItem("Ctrl+T", "Tab", new int[] { VK_CONTROL }, 'T', ""),
                new ShortcutItem("Ctrl+W", "Close", new int[] { VK_CONTROL }, 'W', ""),
                new ShortcutItem("Ctrl+⇧+T", "Reopen", new int[] { VK_CONTROL, VK_SHIFT }, 'T', ""),
                new ShortcutItem("Ctrl+⇧+Esc", "TaskMgr", new int[] { VK_CONTROL, VK_SHIFT }, VK_ESCAPE, ""),
                new ShortcutItem("Ctrl+Alt+Del", "Security", new int[] { VK_CONTROL, VK_MENU }, VK_DELETE, "")
            );
        }
        if ("alt".equals(modKey)) {
            return Arrays.asList(
                new ShortcutItem("Alt+Tab", "Switch", new int[] { VK_MENU }, VK_TAB, ""),
                new ShortcutItem("Alt+F4", "Close", new int[] { VK_MENU }, VK_F4, ""),
                new ShortcutItem("Alt+←", "Back", new int[] { VK_MENU }, VK_LEFT, ""),
                new ShortcutItem("Alt+→", "Fwd", new int[] { VK_MENU }, VK_RIGHT, ""),
                new ShortcutItem("Alt+Enter", "Props", new int[] { VK_MENU }, VK_RETURN, ""),
                new ShortcutItem("Alt+Space", "Menu", new int[] { VK_MENU }, VK_SPACE, ""),
                new ShortcutItem("Alt+Esc", "Cycle", new int[] { VK_MENU }, VK_ESCAPE, ""),
                new ShortcutItem("Alt+PrtSc", "Snap", new int[] { VK_MENU }, VK_SNAPSHOT, "")
            );
        }
        if ("win".equals(modKey)) {
            return Arrays.asList(
                new ShortcutItem("⊞ Start", "Menu", new int[] { VK_LWIN }, 0, ""),
                new ShortcutItem("Win+D", "Desktop", new int[] { VK_LWIN }, 'D', ""),
                new ShortcutItem("Win+E", "Files", new int[] { VK_LWIN }, 'E', ""),
                new ShortcutItem("Win+L", "Lock", new int[] { VK_LWIN }, 'L', "lock"),
                new ShortcutItem("Win+Tab", "Tasks", new int[] { VK_LWIN }, VK_TAB, ""),
                new ShortcutItem("Win+R", "Run", new int[] { VK_LWIN }, 'R', ""),
                new ShortcutItem("Win+S", "Search", new int[] { VK_LWIN }, 'S', ""),
                new ShortcutItem("Win+I", "Settings", new int[] { VK_LWIN }, 'I', ""),
                new ShortcutItem("Win+V", "Clip", new int[] { VK_LWIN }, 'V', ""),
                new ShortcutItem("Win+⇧+S", "Snip", new int[] { VK_LWIN, VK_SHIFT }, 'S', ""),
                new ShortcutItem("Win+←", "Snap L", new int[] { VK_LWIN }, VK_LEFT, ""),
                new ShortcutItem("Win+→", "Snap R", new int[] { VK_LWIN }, VK_RIGHT, ""),
                new ShortcutItem("Win+↑", "Max", new int[] { VK_LWIN }, VK_UP, ""),
                new ShortcutItem("Win+↓", "Min", new int[] { VK_LWIN }, VK_DOWN, ""),
                new ShortcutItem("Win+.", "Emoji", new int[] { VK_LWIN }, VK_OEM_PERIOD, "")
            );
        }
        return Collections.emptyList();
    }
}
