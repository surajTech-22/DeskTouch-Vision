package com.desktouch.vision;

import android.content.Context;
import android.hardware.input.InputManager;
import android.os.Build;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * Inspects currently connected Android keyboards (physical USB/Bluetooth or
 * DeskTouch Virtual USB Keyboard) and maps genuine Android-supported keyboard
 * shortcuts to their actual bound keys without adding synthetic OS actions.
 */
final class KeyboardBindingInspector {
    static final class ShortcutBinding {
        final String action;
        final String keys;
        final String status;
        final boolean activeNow;

        ShortcutBinding(String action, String keys, String status, boolean activeNow) {
            this.action = action;
            this.keys = keys;
            this.status = status;
            this.activeNow = activeNow;
        }
    }

    static final class KeyboardReport {
        final String connectedKeyboardName;
        final boolean externalKeyboardPresent;
        final boolean fullUsbKeyboardActive;
        final String pcLayoutName;
        final String activeModeLabel;
        final List<ShortcutBinding> bindings;

        KeyboardReport(String connectedKeyboardName, boolean externalKeyboardPresent,
                       boolean fullUsbKeyboardActive, String pcLayoutName,
                       String activeModeLabel, List<ShortcutBinding> bindings) {
            this.connectedKeyboardName = connectedKeyboardName;
            this.externalKeyboardPresent = externalKeyboardPresent;
            this.fullUsbKeyboardActive = fullUsbKeyboardActive;
            this.pcLayoutName = pcLayoutName;
            this.activeModeLabel = activeModeLabel;
            this.bindings = bindings;
        }

        JSONObject toWireJson() {
            JSONObject root = new JSONObject();
            try {
                root.put("keyboard", connectedKeyboardName);
                root.put("external", externalKeyboardPresent);
                root.put("fullUsb", fullUsbKeyboardActive);
                root.put("pcLayout", pcLayoutName);
                root.put("mode", activeModeLabel);
                JSONArray items = new JSONArray();
                for (ShortcutBinding b : bindings) {
                    JSONObject row = new JSONObject();
                    row.put("action", b.action);
                    row.put("keys", b.keys);
                    row.put("status", b.status);
                    row.put("ready", b.activeNow);
                    items.put(row);
                }
                root.put("bindings", items);
            } catch (JSONException ignored) {}
            return root;
        }

        String formatSummaryLines() {
            StringBuilder sb = new StringBuilder();
            sb.append("Keyboard: ").append(connectedKeyboardName);
            if (pcLayoutName != null && !pcLayoutName.isEmpty()) {
                sb.append(" (").append(pcLayoutName).append(")");
            }
            sb.append('\n').append("Mode: ").append(activeModeLabel).append('\n');
            for (ShortcutBinding b : bindings) {
                sb.append("• ").append(b.action).append(" — ").append(b.keys)
                  .append(" [").append(b.status).append("]\n");
            }
            return sb.toString().trim();
        }
    }

    private static final int[] INSPECT_KEYCODES = new int[] {
        KeyEvent.KEYCODE_META_LEFT,
        KeyEvent.KEYCODE_ESCAPE,
        KeyEvent.KEYCODE_ENTER,
        KeyEvent.KEYCODE_DEL,
        KeyEvent.KEYCODE_TAB,
        KeyEvent.KEYCODE_SPACE,
        KeyEvent.KEYCODE_SLASH,
        KeyEvent.KEYCODE_CTRL_LEFT,
        KeyEvent.KEYCODE_ALT_LEFT,
        KeyEvent.KEYCODE_A,
        KeyEvent.KEYCODE_C,
        KeyEvent.KEYCODE_V,
        KeyEvent.KEYCODE_X,
        KeyEvent.KEYCODE_Z,
        KeyEvent.KEYCODE_N,
        KeyEvent.KEYCODE_F,
        KeyEvent.KEYCODE_B,
        KeyEvent.KEYCODE_E,
        KeyEvent.KEYCODE_P,
        KeyEvent.KEYCODE_MENU
    };

    private KeyboardBindingInspector() {}

    static KeyboardReport inspect(Context context, boolean shellBridgeActive,
                                  boolean accessibilityActive, String pcLayoutName) {
        InputDevice external = findBestExternalKeyboard(context);
        boolean hasExternalDevice = external != null;
        boolean fullUsb = shellBridgeActive || hasExternalDevice;

        boolean hasMeta = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_META_LEFT);
        boolean hasEsc = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_ESCAPE);
        boolean hasEnter = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_ENTER);
        boolean hasBackspace = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_DEL);
        boolean hasTab = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_TAB);
        boolean hasSpace = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_SPACE);
        boolean hasSlash = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_SLASH);
        boolean hasN = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_N);
        boolean hasCtrl = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_CTRL_LEFT);
        boolean hasAlt = hasExternalDevice && safeHasKey(external, KeyEvent.KEYCODE_ALT_LEFT);

        KeyCharacterMap kcm = external != null ? external.getKeyCharacterMap() : null;
        boolean metaSpaceSearch = hasMeta && hasSpace;
        boolean metaEnterHome = hasMeta && hasEnter;
        boolean metaEscBack = hasMeta && (hasEsc || hasBackspace);
        boolean hasKeyboardFallbackMap = kcm != null
            && kcm.getKeyboardType() == KeyCharacterMap.FULL;

        String detectedName;
        if (external != null && external.getName() != null && !external.getName().isEmpty()) {
            detectedName = external.getName();
        } else if (shellBridgeActive) {
            detectedName = "DeskTouch cable keyboard (initializing)";
        } else if (accessibilityActive) {
            detectedName = "Compatibility typing only (no external hardware keyboard active)";
        } else {
            detectedName = "No keyboard bridge connected";
        }

        String modeLabel;
        if (shellBridgeActive) {
            modeLabel = hasExternalDevice
                ? "Full cable keyboard active (native external shortcuts enabled)"
                : "Cable keyboard starting — connect on data cable";
        } else if (hasExternalDevice) {
            modeLabel = "External hardware keyboard attached";
        } else if (accessibilityActive) {
            modeLabel = "Wireless compatibility mode — Android blocks system Meta/Alt-Tab shortcuts without a connected keyboard";
        } else {
            modeLabel = "Disconnected";
        }

        List<ShortcutBinding> bindings = buildShortcutTable(
            fullUsb, accessibilityActive,
            hasMeta, hasEsc, metaEnterHome, metaEscBack, hasTab, metaSpaceSearch,
            hasSlash, hasN, hasCtrl, hasAlt, hasKeyboardFallbackMap);

        String layoutLabel = pcLayoutName == null ? "" : pcLayoutName.trim();
        return new KeyboardReport(detectedName, hasExternalDevice, fullUsb,
            layoutLabel, modeLabel, bindings);
    }

    static List<ShortcutBinding> buildShortcutTable(
            boolean fullUsb, boolean accessibilityActive,
            boolean hasMeta, boolean hasEsc, boolean metaEnterHome,
            boolean metaEscBack, boolean hasTab, boolean metaSpaceSearch,
            boolean hasSlash, boolean hasN, boolean hasCtrl, boolean hasAlt,
            boolean fullLayout) {
        List<ShortcutBinding> list = new ArrayList<>();
        String fullReady = fullLayout ? "Ready on this keyboard" : "Ready in full mode";
        String needCable = "Needs data cable (full keyboard mode)";

        // Home
        if (fullUsb && (metaEnterHome || hasMeta)) {
            list.add(new ShortcutBinding("Go Home", "Windows + Enter", fullReady, true));
        } else if (accessibilityActive) {
            list.add(new ShortcutBinding("Go Home", "Home key", "Ready (basic mode)", true));
        } else {
            list.add(new ShortcutBinding("Go Home", "Windows + Enter", needCable, false));
        }

        // Back
        if (fullUsb && (metaEscBack || hasEsc)) {
            list.add(new ShortcutBinding("Go Back", "Windows + Backspace / Escape", fullReady, true));
        } else if (accessibilityActive) {
            list.add(new ShortcutBinding("Go Back", "Escape key", "Ready (basic mode)", true));
        } else {
            list.add(new ShortcutBinding("Go Back", "Escape / Windows + Backspace", needCable, false));
        }

        // Switch apps / Overview
        if (fullUsb && hasAlt && hasTab) {
            list.add(new ShortcutBinding("Switch recent apps", "Alt + Tab  or  Windows + Tab", fullReady, true));
        } else if (fullUsb) {
            list.add(new ShortcutBinding("Switch recent apps", "Alt + Tab  or  Windows + Tab", "Supported on full keyboard", true));
        } else {
            list.add(new ShortcutBinding("Switch recent apps", "Alt + Tab  or  Windows + Tab", needCable, false));
        }

        // Notifications
        if (fullUsb && hasMeta && hasN) {
            list.add(new ShortcutBinding("Notifications", "Windows + N", fullReady, true));
        } else {
            list.add(new ShortcutBinding("Notifications", "Windows + N", needCable, false));
        }

        // Search
        if (fullUsb && metaSpaceSearch) {
            list.add(new ShortcutBinding("Quick search", "Windows + Space", fullReady, true));
        } else {
            list.add(new ShortcutBinding("Quick search", "Windows + Space", needCable, false));
        }

        // Android shortcut helper
        if (fullUsb && hasMeta && hasSlash) {
            list.add(new ShortcutBinding("Open Android shortcut list", "Windows + /", fullReady, true));
        } else {
            list.add(new ShortcutBinding("Open Android shortcut list", "Windows + /", needCable, false));
        }

        // Input language switcher
        if (fullUsb && hasCtrl) {
            list.add(new ShortcutBinding("Switch input language", "Control + Space", fullReady, true));
        } else {
            list.add(new ShortcutBinding("Switch input language", "Control + Space", needCable, false));
        }

        // Standard editing in text fields
        String editState = fullUsb
            ? "Ready in apps & text fields"
            : (accessibilityActive ? "Ready in editable text fields" : "Connect to enable");
        boolean editReady = fullUsb || accessibilityActive;
        list.add(new ShortcutBinding("Select all", "Control + A", editState, editReady));
        list.add(new ShortcutBinding("Copy / Cut / Paste", "Control + C / X / V", editState, editReady));

        if (fullUsb) {
            list.add(new ShortcutBinding("Undo / Find inside app", "Control + Z  /  Control + F", "Ready in supported apps", true));
        } else {
            list.add(new ShortcutBinding("Undo / Find inside app", "Control + Z  /  Control + F", needCable, false));
        }

        return list;
    }

    private static InputDevice findBestExternalKeyboard(Context context) {
        try {
            InputManager im = (InputManager) context.getSystemService(Context.INPUT_SERVICE);
            if (im == null) return null;
            int[] ids = im.getInputDeviceIds();
            InputDevice fallback = null;
            for (int id : ids) {
                InputDevice dev = im.getInputDevice(id);
                if (dev == null) continue;
                int sources = dev.getSources();
                if ((sources & InputDevice.SOURCE_KEYBOARD) != InputDevice.SOURCE_KEYBOARD) continue;
                if (dev.getKeyboardType() != InputDevice.KEYBOARD_TYPE_ALPHABETIC) continue;
                if (dev.isVirtual()) continue;
                boolean external = isExternalDevice(dev);
                String name = dev.getName() == null ? "" : dev.getName();
                if (name.contains("DeskTouch")) {
                    return dev;
                }
                if (external) {
                    boolean[] probed = dev.hasKeys(INSPECT_KEYCODES);
                    if (probed != null && probed.length > 0) {
                        fallback = dev;
                    }
                }
            }
            return fallback;
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    private static boolean isExternalDevice(InputDevice dev) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            return dev.isExternal();
        }
        return !dev.isVirtual() && (dev.getVendorId() != 0 || dev.getProductId() != 0);
    }

    private static boolean safeHasKey(InputDevice dev, int keyCode) {
        if (dev == null) return false;
        try {
            boolean[] res = dev.hasKeys(keyCode);
            return res != null && res.length == 1 && res[0];
        } catch (RuntimeException ignored) {
            return true;
        }
    }
}
