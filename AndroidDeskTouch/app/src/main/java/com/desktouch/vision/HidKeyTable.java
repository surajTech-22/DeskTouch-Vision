package com.desktouch.vision;

/**
 * Pure USB HID Keyboard/Keypad (Usage Page 0x07) mapping for Windows virtual-key
 * codes and printable ASCII characters. Contains zero Android framework
 * dependencies so every key mapping is unit-tested on the JVM.
 */
final class HidKeyTable {
    static final int MOD_LEFT_CTRL = 0x01;
    static final int MOD_LEFT_SHIFT = 0x02;
    static final int MOD_LEFT_ALT = 0x04;
    static final int MOD_LEFT_GUI = 0x08;
    static final int MOD_RIGHT_CTRL = 0x10;
    static final int MOD_RIGHT_SHIFT = 0x20;
    static final int MOD_RIGHT_ALT = 0x40;
    static final int MOD_RIGHT_GUI = 0x80;

    static final class Stroke {
        final int modifier;
        final int usage;

        Stroke(int modifier, int usage) {
            this.modifier = modifier;
            this.usage = usage;
        }
    }

    private HidKeyTable() {}

    static Stroke forAsciiChar(char ch) {
        if (ch == '\n' || ch == '\r') return new Stroke(0, 0x28);
        if (ch == '\t') return new Stroke(0, 0x2B);
        if (ch == ' ') return new Stroke(0, 0x2C);
        if (ch >= 'a' && ch <= 'z') return new Stroke(0, 0x04 + (ch - 'a'));
        if (ch >= 'A' && ch <= 'Z') return new Stroke(MOD_LEFT_SHIFT, 0x04 + (ch - 'A'));
        if (ch >= '1' && ch <= '9') return new Stroke(0, 0x1E + (ch - '1'));
        if (ch == '0') return new Stroke(0, 0x27);
        switch (ch) {
            case '!': return new Stroke(MOD_LEFT_SHIFT, 0x1E);
            case '@': return new Stroke(MOD_LEFT_SHIFT, 0x1F);
            case '#': return new Stroke(MOD_LEFT_SHIFT, 0x20);
            case '$': return new Stroke(MOD_LEFT_SHIFT, 0x21);
            case '%': return new Stroke(MOD_LEFT_SHIFT, 0x22);
            case '^': return new Stroke(MOD_LEFT_SHIFT, 0x23);
            case '&': return new Stroke(MOD_LEFT_SHIFT, 0x24);
            case '*': return new Stroke(MOD_LEFT_SHIFT, 0x25);
            case '(': return new Stroke(MOD_LEFT_SHIFT, 0x26);
            case ')': return new Stroke(MOD_LEFT_SHIFT, 0x27);
            case '-': return new Stroke(0, 0x2D);
            case '_': return new Stroke(MOD_LEFT_SHIFT, 0x2D);
            case '=': return new Stroke(0, 0x2E);
            case '+': return new Stroke(MOD_LEFT_SHIFT, 0x2E);
            case '[': return new Stroke(0, 0x2F);
            case '{': return new Stroke(MOD_LEFT_SHIFT, 0x2F);
            case ']': return new Stroke(0, 0x30);
            case '}': return new Stroke(MOD_LEFT_SHIFT, 0x30);
            case '\\': return new Stroke(0, 0x31);
            case '|': return new Stroke(MOD_LEFT_SHIFT, 0x31);
            case ';': return new Stroke(0, 0x33);
            case ':': return new Stroke(MOD_LEFT_SHIFT, 0x33);
            case '\'': return new Stroke(0, 0x34);
            case '"': return new Stroke(MOD_LEFT_SHIFT, 0x34);
            case '`': return new Stroke(0, 0x35);
            case '~': return new Stroke(MOD_LEFT_SHIFT, 0x35);
            case ',': return new Stroke(0, 0x36);
            case '<': return new Stroke(MOD_LEFT_SHIFT, 0x36);
            case '.': return new Stroke(0, 0x37);
            case '>': return new Stroke(MOD_LEFT_SHIFT, 0x37);
            case '/': return new Stroke(0, 0x38);
            case '?': return new Stroke(MOD_LEFT_SHIFT, 0x38);
            default: return null;
        }
    }

    static int modifierBitForVk(int vk) {
        switch (vk) {
            case 0x11: // VK_CONTROL
            case 0xA2: // VK_LCONTROL
                return MOD_LEFT_CTRL;
            case 0xA3: // VK_RCONTROL
                return MOD_RIGHT_CTRL;
            case 0x10: // VK_SHIFT
            case 0xA0: // VK_LSHIFT
                return MOD_LEFT_SHIFT;
            case 0xA1: // VK_RSHIFT
                return MOD_RIGHT_SHIFT;
            case 0x12: // VK_MENU
            case 0xA4: // VK_LMENU
                return MOD_LEFT_ALT;
            case 0xA5: // VK_RMENU
                return MOD_RIGHT_ALT;
            case 0x5B: // VK_LWIN
                return MOD_LEFT_GUI;
            case 0x5C: // VK_RWIN
                return MOD_RIGHT_GUI;
            default:
                return 0;
        }
    }

    static int usageForVk(int vk) {
        if (vk >= 0x41 && vk <= 0x5A) return 0x04 + (vk - 0x41);
        if (vk >= 0x31 && vk <= 0x39) return 0x1E + (vk - 0x31);
        if (vk == 0x30) return 0x27;
        if (vk >= 0x70 && vk <= 0x7B) return 0x3A + (vk - 0x70); // F1..F12
        if (vk >= 0x7C && vk <= 0x87) return 0x68 + (vk - 0x7C); // F13..F24
        if (vk >= 0x61 && vk <= 0x69) return 0x59 + (vk - 0x61); // Numpad 1..9
        switch (vk) {
            case 0x0D: return 0x28; // VK_RETURN
            case 0x1B: return 0x29; // VK_ESCAPE
            case 0x08: return 0x2A; // VK_BACK
            case 0x09: return 0x2B; // VK_TAB
            case 0x20: return 0x2C; // VK_SPACE
            case 0xBD: return 0x2D; // VK_OEM_MINUS
            case 0xBB: return 0x2E; // VK_OEM_PLUS
            case 0xDB: return 0x2F; // VK_OEM_4 '['
            case 0xDD: return 0x30; // VK_OEM_6 ']'
            case 0xDC: return 0x31; // VK_OEM_5 '\'
            case 0xE2: return 0x64; // VK_OEM_102 (ISO <>|)
            case 0xBA: return 0x33; // VK_OEM_1 ';'
            case 0xDE: return 0x34; // VK_OEM_7 '\''
            case 0xC0: return 0x35; // VK_OEM_3 '`'
            case 0xBC: return 0x36; // VK_OEM_COMMA
            case 0xBE: return 0x37; // VK_OEM_PERIOD
            case 0xBF: return 0x38; // VK_OEM_2 '/'
            case 0x14: return 0x39; // VK_CAPITAL
            case 0x2C: return 0x46; // VK_SNAPSHOT
            case 0x91: return 0x47; // VK_SCROLL
            case 0x90: return 0x53; // VK_NUMLOCK
            case 0x13: return 0x48; // VK_PAUSE
            case 0x2D: return 0x49; // VK_INSERT
            case 0x24: return 0x4A; // VK_HOME
            case 0x21: return 0x4B; // VK_PRIOR (PageUp)
            case 0x2E: return 0x4C; // VK_DELETE
            case 0x23: return 0x4D; // VK_END
            case 0x22: return 0x4E; // VK_NEXT (PageDown)
            case 0x27: return 0x4F; // VK_RIGHT
            case 0x25: return 0x50; // VK_LEFT
            case 0x28: return 0x51; // VK_DOWN
            case 0x26: return 0x52; // VK_UP
            case 0x60: return 0x62; // VK_NUMPAD0
            case 0x6E: return 0x63; // VK_DECIMAL
            case 0x6F: return 0x54; // VK_DIVIDE
            case 0x6A: return 0x55; // VK_MULTIPLY
            case 0x6D: return 0x56; // VK_SUBTRACT
            case 0x6B: return 0x57; // VK_ADD
            case 0x5D: return 0x65; // VK_APPS (Context menu)
            default: return 0;
        }
    }
}
