package com.desktouch.vision;

import android.system.Os;
import android.system.OsConstants;
import java.io.DataInputStream;
import java.io.FileDescriptor;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;

/**
 * Shell-only Android UHID transport. Launched by a previously authorized
 * adb shell connection under the 'shell' UID, not by the Android app UID.
 *
 * Native /dev/uhid is usually inaccessible to ordinary app UIDs. We NEVER
 * attempt root, hidden API privileges, or fake Bluetooth hardware.
 *
 * The Windows companion writes 1-byte type + 8/5-byte HID report over ADB
 * stdin. This command has no network listener or extra app UI.
 * License: original DeskTouch implementation informed by Apache 2.0
 * scrcpy UhidManager and standard Linux uhid.h.
 */
public final class UhidShellBridge {
    private static final int UHID_CREATE2 = 11, UHID_INPUT2 = 12;
    private static final int BUS_VIRTUAL = 6;
    private static final int KEYBOARD = 1, MOUSE = 2, CLOSE = 3;
    private static final byte[] KEYBOARD_DESC = hex(
        "05010906a101050719e029e715002501750195088102"+
        "95017508810105081901290515002501750195059102"+
        "95017503910105071900296515002565750895068100c0");
    private static final byte[] MOUSE_DESC = hex(
        "05010902a1010901a100050919012905150025019505"+
        "7501810295017503810105010930093109381581257f"+
        "750895038106050c0a38021581257f750895018106c0c0");
    private static byte[] hex(String source) {
        if ((source.length() & 1) != 0) throw new IllegalArgumentException();
        byte[] result = new byte[source.length() / 2];
        for (int i = 0; i < result.length; i++)
            result[i] = (byte) Integer.parseInt(source.substring(i*2, i*2+2), 16);
        return result;
    }
    private UhidShellBridge() {}

    static byte[] createRequest(String name, byte[] descriptor) {
        if (descriptor.length == 0 || descriptor.length > 4096)
            throw new IllegalArgumentException("Invalid HID descriptor");
        ByteBuffer b = ByteBuffer.allocate(280 + descriptor.length)
            .order(ByteOrder.nativeOrder());
        b.putInt(UHID_CREATE2);
        byte[] title = name.getBytes(StandardCharsets.UTF_8);
        b.put(title, 0, Math.min(127, title.length));
        b.position(260); // uhid_create2_req: type 4 + name 128 + phys/uniq 128
        b.putShort((short) descriptor.length);
        b.putShort((short) BUS_VIRTUAL);
        b.putInt(0); // no false device-vendor identity
        b.putInt(0);
        b.putInt(0);
        b.putInt(0);
        b.put(descriptor);
        return b.array();
    }

    static byte[] inputRequest(byte[] data) {
        if (data.length <= 0 || data.length > 4096)
            throw new IllegalArgumentException("Invalid HID input report");
        ByteBuffer b = ByteBuffer.allocate(6 + data.length)
            .order(ByteOrder.nativeOrder());
        b.putInt(UHID_INPUT2);
        b.putShort((short) data.length);
        b.put(data);
        return b.array();
    }

    public static void main(String[] args) throws Exception {
        FileDescriptor keyboard = null, mouse = null;
        try {
            keyboard = Os.open("/dev/uhid", OsConstants.O_RDWR, 0);
            mouse = Os.open("/dev/uhid", OsConstants.O_RDWR, 0);
            byte[] createKey = createRequest("DeskTouch Vision Keyboard", KEYBOARD_DESC);
            byte[] createMouse = createRequest("DeskTouch Vision Mouse", MOUSE_DESC);
            Os.write(keyboard, createKey, 0, createKey.length);
            Os.write(mouse, createMouse, 0, createMouse.length);
            // Require both kernel writes before acknowledging. Android's
            // InputReader may still need a moment to register the two devices.
            System.out.write("READY\n".getBytes(StandardCharsets.US_ASCII));
            System.out.flush();

            DataInputStream in = new DataInputStream(System.in);
            byte[] keyReport = new byte[8], mouseReport = new byte[5];
            boolean firstMouseApplied = false;
            for (;;) {
                int operation = in.read();
                if (operation < 0 || operation == CLOSE) break;
                if (operation != KEYBOARD && operation != MOUSE)
                    throw new IOException("Unrecognized UHID report");
                byte[] report = operation == KEYBOARD ? keyReport : mouseReport;
                in.readFully(report);
                byte[] request = inputRequest(report);
                Os.write(operation == KEYBOARD ? keyboard : mouse, request, 0, request.length);
                if (operation == MOUSE && !firstMouseApplied) {
                    firstMouseApplied = true;
                    System.out.write("ACTIVE\n".getBytes(StandardCharsets.US_ASCII));
                    System.out.flush();
                }
            }
        } catch (Exception ex) {
            System.err.println("DeskTouch UHID: " + ex.getClass().getSimpleName() +
                ": " + ex.getMessage());
            throw ex;
        } finally {
            // Closing a UHID file descriptor removes the virtual input device.
            if (keyboard != null) try { Os.close(keyboard); } catch (Exception ignored) {}
            if (mouse != null) try { Os.close(mouse); } catch (Exception ignored) {}
        }
    }
}
