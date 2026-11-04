package com.desktouch.vision;

import android.view.KeyEvent;

/** Windows virtual-key to Android input-connection key mapping (editor scoped). */
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
            default: return vk>=112&&vk<=123 ?
                KeyEvent.KEYCODE_F1+(vk-112) : KeyEvent.KEYCODE_UNKNOWN;
        }
    }
}
