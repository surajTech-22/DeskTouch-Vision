package com.desktouch.vision;

import static org.junit.Assert.*;

import java.util.List;
import org.junit.Test;

public final class FullscreenPreviewLayoutTest {
    @Test
    public void monitorRectAndBlankPillarsEnforceScreenExclusion() {
        // 900x414 landscape bezel -> ~876x390 root, 16:9 screen centered (~91.3..784.7)
        FullscreenPreviewLayoutHelper.MonitorRect m =
            FullscreenPreviewLayoutHelper.computeFittedMonitorRect(876f, 390f, 1920, 1080);

        float railW = FullscreenPreviewLayoutHelper.effectiveRailWidthDp(m);
        assertEquals(62f, railW, 0.1f);
        assertTrue(FullscreenPreviewLayoutHelper.hasSideBlankPillars(m, railW));

        // Left & right blank edges do not overlap center monitor
        assertFalse(FullscreenPreviewLayoutHelper.overlapsMonitorRect(10f, 12f, railW, 340f, m));
        assertFalse(FullscreenPreviewLayoutHelper.overlapsMonitorRect(876f - 10f - railW, 56f, railW, 310f, m));

        // Center monitor placement IS blocked
        assertTrue(FullscreenPreviewLayoutHelper.overlapsMonitorRect(240f, 40f, railW, 180f, m));
    }

    @Test
    public void stackedExpandStaysInPlacePushesLowerHeaderDownAndRestoresOnCollapse() {
        FullscreenPreviewLayoutHelper.MonitorRect m =
            FullscreenPreviewLayoutHelper.computeFittedMonitorRect(876f, 390f, 1920, 1080);
        FullscreenPreviewLayoutHelper.LayoutState state = FullscreenPreviewLayoutHelper.LayoutState.defaults();

        // Place both rails on left, stacked close together at the top while collapsed
        state.fn.side = "left";
        state.fn.baseTopDp = 12f;
        state.fn.baseHeightDp = 180f;
        state.fn.collapsed = true;
        state.fn.stackOrder = 0;

        state.mod.side = "left";
        state.mod.baseTopDp = 54f;
        state.mod.baseHeightDp = 150f;
        state.mod.collapsed = true;
        state.mod.stackOrder = 1;

        FullscreenPreviewLayoutHelper.resolveLayout(state, m, "mod");
        assertEquals(12f, state.fn.topDp, 0.5f);
        assertEquals(54f, state.mod.topDp, 0.5f);

        // Expand upper rail (#6): upper stays anchored at 12dp and pushes lower header (#2) down
        state.fn.collapsed = false;
        FullscreenPreviewLayoutHelper.resolveLayout(state, m, "fn");
        assertEquals(12f, state.fn.topDp, 0.5f);
        assertEquals(180f, state.fn.heightDp, 0.5f);
        assertEquals(12f + 180f + FullscreenPreviewLayoutHelper.RAIL_GAP_DP, state.mod.topDp, 0.5f);
        // baseTopDp of lower header must remain untouched so collapsing restores it
        assertEquals(54f, state.mod.baseTopDp, 0.5f);

        // Collapse upper rail again: lower header moves right back up to 54dp
        state.fn.collapsed = true;
        FullscreenPreviewLayoutHelper.resolveLayout(state, m, "fn");
        assertEquals(12f, state.fn.topDp, 0.5f);
        assertEquals(54f, state.mod.topDp, 0.5f);
    }

    @Test
    public void twoExpandedStackedRailsKeepAtLeastTwoOptionsMinimumHeight() {
        FullscreenPreviewLayoutHelper.MonitorRect m =
            FullscreenPreviewLayoutHelper.computeFittedMonitorRect(876f, 390f, 1920, 1080);
        FullscreenPreviewLayoutHelper.LayoutState state = FullscreenPreviewLayoutHelper.LayoutState.defaults();

        state.fn.side = "right";
        state.fn.baseTopDp = 56f;
        state.fn.baseHeightDp = 280f;
        state.fn.stackOrder = 0;
        state.mod.side = "right";
        state.mod.baseTopDp = 180f;
        state.mod.baseHeightDp = 260f;
        state.mod.stackOrder = 1;

        FullscreenPreviewLayoutHelper.resolveLayout(state, m, "mod");
        assertTrue(state.fn.heightDp >= FullscreenPreviewLayoutHelper.MIN_RAIL_HEIGHT_DP);
        assertTrue(state.mod.heightDp >= FullscreenPreviewLayoutHelper.MIN_RAIL_HEIGHT_DP);
        assertTrue(state.mod.topDp >= state.fn.topDp + state.fn.heightDp + FullscreenPreviewLayoutHelper.RAIL_GAP_DP - 0.1f);
        assertTrue(state.mod.topDp + state.mod.heightDp <= FullscreenPreviewLayoutHelper.getMaxAllowedBottomDp(m) + 0.1f);
    }

    @Test
    public void swapRailsInterchangesOppositeAndSameSidePositions() {
        FullscreenPreviewLayoutHelper.LayoutState state = FullscreenPreviewLayoutHelper.LayoutState.defaults();
        state.fn.side = "left";
        state.fn.baseTopDp = 20f;
        state.fn.baseHeightDp = 140f;
        state.fn.stackOrder = 0;

        state.mod.side = "right";
        state.mod.baseTopDp = 90f;
        state.mod.baseHeightDp = 200f;
        state.mod.stackOrder = 1;

        FullscreenPreviewLayoutHelper.swapRails(state);
        assertEquals("right", state.fn.side);
        assertEquals(90f, state.fn.baseTopDp, 0.01f);
        assertEquals(200f, state.fn.baseHeightDp, 0.01f);
        assertEquals("left", state.mod.side);
        assertEquals(20f, state.mod.baseTopDp, 0.01f);
        assertEquals(140f, state.mod.baseHeightDp, 0.01f);

        // Stack same-side then swap order
        state.fn.side = "left";
        state.fn.baseTopDp = 12f;
        state.fn.stackOrder = 0;
        state.mod.side = "left";
        state.mod.baseTopDp = 170f;
        state.mod.stackOrder = 1;
        FullscreenPreviewLayoutHelper.swapRails(state);
        assertEquals("left", state.fn.side);
        assertEquals("left", state.mod.side);
        assertEquals(1, state.fn.stackOrder);
        assertEquals(0, state.mod.stackOrder);
        assertEquals(170f, state.fn.baseTopDp, 0.01f);
        assertEquals(12f, state.mod.baseTopDp, 0.01f);
    }

    @Test
    public void portraitLetterboxKeepsRailsOutsideMonitor() {
        FullscreenPreviewLayoutHelper.MonitorRect portraitMon =
            FullscreenPreviewLayoutHelper.computeFittedMonitorRect(372f, 736f, 1920, 1080);
        assertFalse(FullscreenPreviewLayoutHelper.hasSideBlankPillars(portraitMon, 62f));

        FullscreenPreviewLayoutHelper.LayoutState state = FullscreenPreviewLayoutHelper.LayoutState.defaults();
        state.fn.side = "left";
        state.fn.baseTopDp = 12f;
        state.fn.baseHeightDp = 180f;
        state.mod.side = "right";
        state.mod.baseTopDp = 500f;
        state.mod.baseHeightDp = 180f;

        FullscreenPreviewLayoutHelper.resolveLayout(state, portraitMon, "mod");
        assertFalse(FullscreenPreviewLayoutHelper.overlapsMonitorRect(
            10f, state.fn.topDp, 62f, state.fn.heightDp, portraitMon));
        assertFalse(FullscreenPreviewLayoutHelper.overlapsMonitorRect(
            300f, state.mod.topDp, 62f, state.mod.heightDp, portraitMon));
    }

    @Test
    public void jsonRoundTripAndMalformedFallbackAndShortcutCatalog() {
        FullscreenPreviewLayoutHelper.LayoutState state = FullscreenPreviewLayoutHelper.LayoutState.defaults();
        state.hudCollapsed = true;
        state.landscape = false;
        state.fn.side = "right";
        state.fn.baseTopDp = 72f;
        state.fn.baseHeightDp = 132f;
        state.fn.collapsed = true;
        state.fn.stackOrder = 1;
        state.mod.side = "right";
        state.mod.baseTopDp = 212f;
        state.mod.baseHeightDp = 148f;
        state.mod.stackOrder = 0;

        FullscreenPreviewLayoutHelper.LayoutState restored =
            FullscreenPreviewLayoutHelper.fromJson(FullscreenPreviewLayoutHelper.toJson(state));
        assertTrue(restored.hudCollapsed);
        assertFalse(restored.landscape);
        assertEquals("right", restored.fn.side);
        assertEquals(72f, restored.fn.baseTopDp, 0.01f);
        assertEquals(132f, restored.fn.baseHeightDp, 0.01f);
        assertTrue(restored.fn.collapsed);
        assertEquals(1, restored.fn.stackOrder);
        assertEquals(212f, restored.mod.baseTopDp, 0.01f);
        assertEquals(148f, restored.mod.baseHeightDp, 0.01f);
        assertEquals(0, restored.mod.stackOrder);

        FullscreenPreviewLayoutHelper.LayoutState bad =
            FullscreenPreviewLayoutHelper.fromJson("{not-valid-json");
        assertEquals("left", bad.fn.side);
        assertEquals("right", bad.mod.side);
        assertFalse(bad.hudCollapsed);

        List<FullscreenPreviewLayoutHelper.ShortcutItem> ctrl = FullscreenPreviewLayoutHelper.shortcutsFor("ctrl");
        List<FullscreenPreviewLayoutHelper.ShortcutItem> alt = FullscreenPreviewLayoutHelper.shortcutsFor("alt");
        List<FullscreenPreviewLayoutHelper.ShortcutItem> win = FullscreenPreviewLayoutHelper.shortcutsFor("win");
        assertEquals(15, ctrl.size());
        assertEquals(8, alt.size());
        assertEquals(15, win.size());
        boolean sawWinLock = false;
        for (FullscreenPreviewLayoutHelper.ShortcutItem item : win) {
            if ("Win+L".equals(item.label) && "lock".equals(item.powerAction)) {
                sawWinLock = true;
                break;
            }
        }
        assertTrue(sawWinLock);
    }
}
