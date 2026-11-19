package com.desktouch.vision;

import android.view.KeyEvent;

/** Windows virtual-key to Android keycode mapping (editor & fallback injection). */
final class RemoteImeKeys {
    private RemoteImeKeys() {}

    static int androidKeyCode(int vk) {
        switch (vk) {
            case 8: return KeyEvent.KEYCODE_DEL;
            case 9: return KeyEvent.KEYCODE_TAB;
            case 13: return KeyEvent.KEYCODE_ENTER;
            case 33: return KeyEvent.KEYCODE_PAGE_UP;
            case 34: return KeyEvent.KEYCODE_PAGE_DOWN;
            case 35: return KeyEvent.KEYCODE_MOVE_END;
            case 36: return KeyEvent.KEYCODE_MOVE_HOME;
            case 37: return KeyEvent.KEYCODE_DPAD_LEFT;
            case 38: return KeyEvent.KEYCODE_DPAD_UP;
            case 39: return KeyEvent.KEYCODE_DPAD_RIGHT;
            case 40: return KeyEvent.KEYCODE_DPAD_DOWN;
            case 46: return KeyEvent.KEYCODE_FORWARD_DEL;
            default: return vk >= 112 && vk <= 123
                ? KeyEvent.KEYCODE_F1 + (vk - 112) : KeyEvent.KEYCODE_UNKNOWN;
        }
    }

    static int fullAndroidKeyCode(int vk) {
        if (vk >= 0x41 && vk <= 0x5A) return KeyEvent.KEYCODE_A + (vk - 0x41);
        if (vk >= 0x30 && vk <= 0x39) return KeyEvent.KEYCODE_0 + (vk - 0x30);
        if (vk >= 0x60 && vk <= 0x69) return KeyEvent.KEYCODE_NUMPAD_0 + (vk - 0x60);
        int base = androidKeyCode(vk);
        if (base != KeyEvent.KEYCODE_UNKNOWN) return base;
        switch (vk) {
            case 0x10:
            case 0xA0: return KeyEvent.KEYCODE_SHIFT_LEFT;
            case 0xA1: return KeyEvent.KEYCODE_SHIFT_RIGHT;
            case 0x11:
            case 0xA2: return KeyEvent.KEYCODE_CTRL_LEFT;
            case 0xA3: return KeyEvent.KEYCODE_CTRL_RIGHT;
            case 0x12:
            case 0xA4: return KeyEvent.KEYCODE_ALT_LEFT;
            case 0xA5: return KeyEvent.KEYCODE_ALT_RIGHT;
            case 0x5B: return KeyEvent.KEYCODE_META_LEFT;
            case 0x5C: return KeyEvent.KEYCODE_META_RIGHT;
            case 0x14: return KeyEvent.KEYCODE_CAPS_LOCK;
            case 0x1B: return KeyEvent.KEYCODE_ESCAPE;
            case 0x20: return KeyEvent.KEYCODE_SPACE;
            case 0x2D: return KeyEvent.KEYCODE_INSERT;
            case 0x5D: return KeyEvent.KEYCODE_MENU;
            case 0xBA: return KeyEvent.KEYCODE_SEMICOLON;
            case 0xBB: return KeyEvent.KEYCODE_EQUALS;
            case 0xBC: return KeyEvent.KEYCODE_COMMA;
            case 0xBD: return KeyEvent.KEYCODE_MINUS;
            case 0xBE: return KeyEvent.KEYCODE_PERIOD;
            case 0xBF: return KeyEvent.KEYCODE_SLASH;
            case 0xC0: return KeyEvent.KEYCODE_GRAVE;
            case 0xDB: return KeyEvent.KEYCODE_LEFT_BRACKET;
            case 0xDC: return KeyEvent.KEYCODE_BACKSLASH;
            case 0xDD: return KeyEvent.KEYCODE_RIGHT_BRACKET;
            case 0xDE: return KeyEvent.KEYCODE_APOSTROPHE;
            case 0x6A: return KeyEvent.KEYCODE_NUMPAD_MULTIPLY;
            case 0x6B: return KeyEvent.KEYCODE_NUMPAD_ADD;
            case 0x6D: return KeyEvent.KEYCODE_NUMPAD_SUBTRACT;
            case 0x6E: return KeyEvent.KEYCODE_NUMPAD_DOT;
            case 0x6F: return KeyEvent.KEYCODE_NUMPAD_DIVIDE;
            default: return KeyEvent.KEYCODE_UNKNOWN;
        }
    }

    static int metaStateFromHidModifiers(int modMask) {
        int meta = 0;
        if ((modMask & (HidKeyTable.MOD_LEFT_CTRL | HidKeyTable.MOD_RIGHT_CTRL)) != 0) {
            meta |= KeyEvent.META_CTRL_ON;
            if ((modMask & HidKeyTable.MOD_LEFT_CTRL) != 0) meta |= KeyEvent.META_CTRL_LEFT_ON;
            if ((modMask & HidKeyTable.MOD_RIGHT_CTRL) != 0) meta |= KeyEvent.META_CTRL_RIGHT_ON;
        }
        if ((modMask & (HidKeyTable.MOD_LEFT_SHIFT | HidKeyTable.MOD_RIGHT_SHIFT)) != 0) {
            meta |= KeyEvent.META_SHIFT_ON;
            if ((modMask & HidKeyTable.MOD_LEFT_SHIFT) != 0) meta |= KeyEvent.META_SHIFT_LEFT_ON;
            if ((modMask & HidKeyTable.MOD_RIGHT_SHIFT) != 0) meta |= KeyEvent.META_SHIFT_RIGHT_ON;
        }
        if ((modMask & (HidKeyTable.MOD_LEFT_ALT | HidKeyTable.MOD_RIGHT_ALT)) != 0) {
            meta |= KeyEvent.META_ALT_ON;
            if ((modMask & HidKeyTable.MOD_LEFT_ALT) != 0) meta |= KeyEvent.META_ALT_LEFT_ON;
            if ((modMask & HidKeyTable.MOD_RIGHT_ALT) != 0) meta |= KeyEvent.META_ALT_RIGHT_ON;
        }
        if ((modMask & (HidKeyTable.MOD_LEFT_GUI | HidKeyTable.MOD_RIGHT_GUI)) != 0) {
            meta |= KeyEvent.META_META_ON;
            if ((modMask & HidKeyTable.MOD_LEFT_GUI) != 0) meta |= KeyEvent.META_META_LEFT_ON;
            if ((modMask & HidKeyTable.MOD_RIGHT_GUI) != 0) meta |= KeyEvent.META_META_RIGHT_ON;
        }
        return meta;
    }
}
