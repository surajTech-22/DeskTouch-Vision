package com.desktouch.vision;

import android.view.KeyEvent;
import org.junit.Test;
import static org.junit.Assert.*;

/** Tests the exact key semantics expected by the editor input endpoint. */
public final class RemoteImeKeysTest {
    @Test public void editingAndNavigation() {
        assertEquals(KeyEvent.KEYCODE_DEL,RemoteImeKeys.androidKeyCode(8));
        assertEquals(KeyEvent.KEYCODE_FORWARD_DEL,RemoteImeKeys.androidKeyCode(46));
        assertEquals(KeyEvent.KEYCODE_ENTER,RemoteImeKeys.androidKeyCode(13));
        assertEquals(KeyEvent.KEYCODE_TAB,RemoteImeKeys.androidKeyCode(9));
        assertEquals(KeyEvent.KEYCODE_DPAD_LEFT,RemoteImeKeys.androidKeyCode(37));
        assertEquals(KeyEvent.KEYCODE_DPAD_RIGHT,RemoteImeKeys.androidKeyCode(39));
        assertEquals(KeyEvent.KEYCODE_DPAD_UP,RemoteImeKeys.androidKeyCode(38));
        assertEquals(KeyEvent.KEYCODE_DPAD_DOWN,RemoteImeKeys.androidKeyCode(40));
        assertEquals(KeyEvent.KEYCODE_MOVE_HOME,RemoteImeKeys.androidKeyCode(36));
        assertEquals(KeyEvent.KEYCODE_MOVE_END,RemoteImeKeys.androidKeyCode(35));
    }
    @Test public void functionAndUnsupportedKeys() {
        assertEquals(KeyEvent.KEYCODE_F1,RemoteImeKeys.androidKeyCode(112));
        assertEquals(KeyEvent.KEYCODE_F12,RemoteImeKeys.androidKeyCode(123));
        assertEquals(KeyEvent.KEYCODE_UNKNOWN,RemoteImeKeys.androidKeyCode(65));
        assertEquals(KeyEvent.KEYCODE_UNKNOWN,RemoteImeKeys.androidKeyCode(0));
        assertEquals(KeyEvent.KEYCODE_UNKNOWN,RemoteImeKeys.androidKeyCode(255));
    }
}
