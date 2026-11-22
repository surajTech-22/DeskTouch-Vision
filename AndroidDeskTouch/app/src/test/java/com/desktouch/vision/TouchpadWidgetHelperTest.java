package com.desktouch.vision;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class TouchpadWidgetHelperTest {
    private static LinkedDisplayHelper.DisplayTileModel makePc(int id, String name, boolean selected) {
        return new LinkedDisplayHelper.DisplayTileModel(
            id, name, name, "Windows", "Display 1", "1920x1080",
            1920, 1080, 1, false, id == 0, false, selected, true, true, 0,
            Collections.emptyList());
    }

    @Test
    public void togglePcSelectionSelectsSwitchesAndUnselects() {
        // Windows Hub PC has target id == 0 and must be selectable and unselectable cleanly.
        assertEquals(0, TouchpadWidgetHelper.togglePcSelection(TouchpadWidgetHelper.UNSELECTED, 0));
        assertEquals(2, TouchpadWidgetHelper.togglePcSelection(0, 2));
        assertEquals(0, TouchpadWidgetHelper.togglePcSelection(2, 0));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(0, 0));
        assertEquals(1, TouchpadWidgetHelper.togglePcSelection(TouchpadWidgetHelper.UNSELECTED, 1));
        assertEquals(2, TouchpadWidgetHelper.togglePcSelection(1, 2));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(2, 2));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(0, -1));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(1, -5));
    }

    @Test
    public void resolveEffectiveSelectionHonorsExplicitUnselectAndFallback() {
        List<LinkedDisplayHelper.DisplayTileModel> pcs = Arrays.asList(
            makePc(0, "DESKTOP-HUB", false),
            makePc(2, "LAPTOP-B", true)
        );
        // Explicit unselect stays unselected even when Hub/member PCs exist.
        assertEquals(
            TouchpadWidgetHelper.UNSELECTED,
            TouchpadWidgetHelper.resolveEffectiveSelection(0, true, pcs));

        // Stored Hub PC ID (0) and member PC ID (2) are both valid.
        assertTrue(TouchpadWidgetHelper.isTargetStillAvailable(0, pcs));
        assertTrue(TouchpadWidgetHelper.isTargetStillAvailable(2, pcs));
        assertFalse(TouchpadWidgetHelper.isTargetStillAvailable(-1, pcs));
        assertEquals(0, TouchpadWidgetHelper.resolveEffectiveSelection(0, false, pcs));

        // Stale stored ID falls back to active selected PC (or Hub 0 when none active).
        assertEquals(2, TouchpadWidgetHelper.resolveEffectiveSelection(99, false, pcs));
        List<LinkedDisplayHelper.DisplayTileModel> singleHub =
            Collections.singletonList(makePc(0, "DESKTOP-HUB", false));
        assertEquals(0, TouchpadWidgetHelper.resolveEffectiveSelection(99, false, singleHub));

        // Empty list always resolves to UNSELECTED.
        assertEquals(
            TouchpadWidgetHelper.UNSELECTED,
            TouchpadWidgetHelper.resolveEffectiveSelection(0, false, Collections.emptyList()));
    }

    @Test
    public void toggleFloatingOverlayIsMutuallyExclusive() {
        assertEquals(
            TouchpadWidgetHelper.OVERLAY_KEYS,
            TouchpadWidgetHelper.toggleFloatingOverlay(
                TouchpadWidgetHelper.OVERLAY_NONE, TouchpadWidgetHelper.OVERLAY_KEYS));
        assertEquals(
            TouchpadWidgetHelper.OVERLAY_NONE,
            TouchpadWidgetHelper.toggleFloatingOverlay(
                TouchpadWidgetHelper.OVERLAY_KEYS, TouchpadWidgetHelper.OVERLAY_KEYS));
        assertEquals(
            TouchpadWidgetHelper.OVERLAY_CTRL,
            TouchpadWidgetHelper.toggleFloatingOverlay(
                TouchpadWidgetHelper.OVERLAY_KEYS, TouchpadWidgetHelper.OVERLAY_CTRL));
        assertEquals(
            TouchpadWidgetHelper.OVERLAY_NONE,
            TouchpadWidgetHelper.toggleFloatingOverlay(
                TouchpadWidgetHelper.OVERLAY_CTRL, "unknown"));
    }

    @Test
    public void shortcutsForCategoryCoversAllFiveCategories() {
        assertFalse(TouchpadWidgetHelper.shortcutsForCategory(TouchpadWidgetHelper.CAT_CTRL).isEmpty());
        assertFalse(TouchpadWidgetHelper.shortcutsForCategory(TouchpadWidgetHelper.CAT_ALT).isEmpty());
        assertFalse(TouchpadWidgetHelper.shortcutsForCategory(TouchpadWidgetHelper.CAT_WIN).isEmpty());

        List<FullscreenPreviewLayoutHelper.ShortcutItem> fns =
            TouchpadWidgetHelper.shortcutsForCategory(TouchpadWidgetHelper.CAT_FN);
        assertEquals(12, fns.size());
        assertEquals("F1", fns.get(0).label);
        assertEquals("F12", fns.get(11).label);

        List<FullscreenPreviewLayoutHelper.ShortcutItem> nav =
            TouchpadWidgetHelper.shortcutsForCategory(TouchpadWidgetHelper.CAT_NAV);
        assertEquals(10, nav.size());
        assertEquals("Esc", nav.get(0).label);
        assertEquals("PgDn", nav.get(9).label);
    }
}
