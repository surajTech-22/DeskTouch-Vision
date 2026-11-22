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
            1920, 1080, 1, false, id == 1, false, selected, true, true, 0,
            Collections.emptyList());
    }

    @Test
    public void togglePcSelectionSelectsSwitchesAndUnselects() {
        assertEquals(1, TouchpadWidgetHelper.togglePcSelection(TouchpadWidgetHelper.UNSELECTED, 1));
        assertEquals(2, TouchpadWidgetHelper.togglePcSelection(1, 2));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(2, 2));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(1, 0));
        assertEquals(TouchpadWidgetHelper.UNSELECTED, TouchpadWidgetHelper.togglePcSelection(1, -5));
    }

    @Test
    public void resolveEffectiveSelectionHonorsExplicitUnselectAndFallback() {
        List<LinkedDisplayHelper.DisplayTileModel> pcs = Arrays.asList(
            makePc(1, "DESKTOP-A", false),
            makePc(2, "LAPTOP-B", true)
        );
        // Explicit unselect stays unselected even when PCs exist.
        assertEquals(
            TouchpadWidgetHelper.UNSELECTED,
            TouchpadWidgetHelper.resolveEffectiveSelection(2, true, pcs));

        // Stored valid ID wins over selected flag.
        assertEquals(1, TouchpadWidgetHelper.resolveEffectiveSelection(1, false, pcs));

        // Stale stored ID falls back to active selected PC.
        assertEquals(2, TouchpadWidgetHelper.resolveEffectiveSelection(99, false, pcs));

        // Empty list always resolves to UNSELECTED.
        assertEquals(
            TouchpadWidgetHelper.UNSELECTED,
            TouchpadWidgetHelper.resolveEffectiveSelection(1, false, Collections.emptyList()));
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
