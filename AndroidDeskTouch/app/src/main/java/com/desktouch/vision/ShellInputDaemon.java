package com.desktouch.vision;

import android.system.ErrnoException;
import android.system.Os;
import android.system.OsConstants;
import android.system.StructPollfd;
import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Base64;
import java.util.Locale;

/**
 * Shell-privileged virtual kernel mouse & keyboard bridge launched by the
 * user's Windows PC over an authorized Android bridge session (`app_process`).
 * Operates exclusively via `/dev/uhid` without Accessibility Settings and
 * accepts only loopback connections carrying the one-time stdin secret.
 */
public final class ShellInputDaemon {
    private static final int UHID_DESTROY = 1;
    private static final int UHID_START = 2;
    private static final int UHID_CREATE2 = 11;
    private static final int UHID_INPUT2 = 12;
    private static final int UHID_CREATE2_SIZE = 4 + 128 + 64 + 64 + 2 + 2 + 16 + 4096;

    // 5-button mouse (buttons, relX, relY, relVWheel, relHWheel AC Pan).
    private static final byte[] MOUSE_DESC = {
        (byte) 0x05, (byte) 0x01, // Usage Page (Generic Desktop)
        (byte) 0x09, (byte) 0x02, // Usage (Mouse)
        (byte) 0xA1, (byte) 0x01, // Collection (Application)
        (byte) 0x09, (byte) 0x01, //   Usage (Pointer)
        (byte) 0xA1, (byte) 0x00, //   Collection (Physical)
        (byte) 0x05, (byte) 0x09, //     Usage Page (Buttons)
        (byte) 0x19, (byte) 0x01, //     Usage Minimum (1)
        (byte) 0x29, (byte) 0x05, //     Usage Maximum (5)
        (byte) 0x15, (byte) 0x00, //     Logical Minimum (0)
        (byte) 0x25, (byte) 0x01, //     Logical Maximum (1)
        (byte) 0x95, (byte) 0x05, //     Report Count (5)
        (byte) 0x75, (byte) 0x01, //     Report Size (1)
        (byte) 0x81, (byte) 0x02, //     Input (Data,Var,Abs)
        (byte) 0x95, (byte) 0x01, //     Report Count (1)
        (byte) 0x75, (byte) 0x03, //     Report Size (3)
        (byte) 0x81, (byte) 0x01, //     Input (Const)
        (byte) 0x05, (byte) 0x01, //     Usage Page (Generic Desktop)
        (byte) 0x09, (byte) 0x30, //     Usage (X)
        (byte) 0x09, (byte) 0x31, //     Usage (Y)
        (byte) 0x09, (byte) 0x38, //     Usage (Wheel)
        (byte) 0x15, (byte) 0x81, //     Logical Minimum (-127)
        (byte) 0x25, (byte) 0x7F, //     Logical Maximum (127)
        (byte) 0x75, (byte) 0x08, //     Report Size (8)
        (byte) 0x95, (byte) 0x03, //     Report Count (3)
        (byte) 0x81, (byte) 0x06, //     Input (Data,Var,Rel)
        (byte) 0x05, (byte) 0x0C, //     Usage Page (Consumer)
        (byte) 0x0A, (byte) 0x38, (byte) 0x02, // Usage (AC Pan)
        (byte) 0x15, (byte) 0x81, //     Logical Minimum (-127)
        (byte) 0x25, (byte) 0x7F, //     Logical Maximum (127)
        (byte) 0x75, (byte) 0x08, //     Report Size (8)
        (byte) 0x95, (byte) 0x01, //     Report Count (1)
        (byte) 0x81, (byte) 0x06, //     Input (Data,Var,Rel)
        (byte) 0xC0,              //   End Collection
        (byte) 0xC0               // End Collection
    };

    // Standard 8-byte boot keyboard (modifiers, reserved, 6 active keycodes).
    private static final byte[] KEYBOARD_DESC = {
        (byte) 0x05, (byte) 0x01, // Usage Page (Generic Desktop)
        (byte) 0x09, (byte) 0x06, // Usage (Keyboard)
        (byte) 0xA1, (byte) 0x01, // Collection (Application)
        (byte) 0x05, (byte) 0x07, //   Usage Page (Key Codes)
        (byte) 0x19, (byte) 0xE0, //   Usage Minimum (224)
        (byte) 0x29, (byte) 0xE7, //   Usage Maximum (231)
        (byte) 0x15, (byte) 0x00, //   Logical Minimum (0)
        (byte) 0x25, (byte) 0x01, //   Logical Maximum (1)
        (byte) 0x75, (byte) 0x01, //   Report Size (1)
        (byte) 0x95, (byte) 0x08, //   Report Count (8)
        (byte) 0x81, (byte) 0x02, //   Input (Data,Var,Abs)
        (byte) 0x95, (byte) 0x01, //   Report Count (1)
        (byte) 0x75, (byte) 0x08, //   Report Size (8)
        (byte) 0x81, (byte) 0x01, //   Input (Const)
        (byte) 0x95, (byte) 0x06, //   Report Count (6)
        (byte) 0x75, (byte) 0x08, //   Report Size (8)
        (byte) 0x15, (byte) 0x00, //   Logical Minimum (0)
        (byte) 0x26, (byte) 0xFF, (byte) 0x00, // Logical Maximum (255)
        (byte) 0x05, (byte) 0x07, //   Usage Page (Key Codes)
        (byte) 0x19, (byte) 0x00, //   Usage Minimum (0)
        (byte) 0x29, (byte) 0xFF, //   Usage Maximum (255)
        (byte) 0x81, (byte) 0x00, //   Input (Data,Array)
        (byte) 0xC0               // End Collection
    };

    private final byte[] expectedTokenBytes;
    private FileDescriptor mouseFd;
    private FileDescriptor keyboardFd;
    private int screenWidth = 1080;
    private int screenHeight = 2400;
    private boolean anchoredCursor;
    private int cursorX;
    private int cursorY;
    private int buttonMask;
    private int vWheelCarry;
    private int hWheelCarry;
    private int keyModifierMask;
    private final int[] activeUsages = new int[6];

    private ShellInputDaemon(byte[] expectedTokenBytes) {
        this.expectedTokenBytes = expectedTokenBytes;
    }

    public static void main(String[] args) throws Exception {
        String portFile = null;
        String pidFile = null;
        for (int i = 0; i + 1 < args.length; i++) {
            if ("--port-file".equals(args[i])) {
                portFile = args[i + 1];
            } else if ("--pid-file".equals(args[i])) {
                pidFile = args[i + 1];
            }
        }
        BufferedReader stdin = new BufferedReader(
            new InputStreamReader(System.in, StandardCharsets.US_ASCII));
        String token = stdin.readLine();
        if (token == null) System.exit(10);
        token = token.trim();
        if (!token.matches("[A-Fa-f0-9]{32,128}")) System.exit(11);
        byte[] expectedBytes = token.toUpperCase(Locale.ROOT)
            .getBytes(StandardCharsets.US_ASCII);

        ShellInputDaemon daemon = new ShellInputDaemon(expectedBytes);
        daemon.runServer(portFile, pidFile);
    }

    private void runServer(String portFile, String pidFile) throws Exception {
        if (pidFile != null && !pidFile.isEmpty()) {
            try (FileOutputStream out = new FileOutputStream(pidFile)) {
                out.write((Os.getpid() + "\n").getBytes(StandardCharsets.US_ASCII));
                out.getFD().sync();
            }
        }
        InetAddress loopback = InetAddress.getByName("127.0.0.1");
        try (ServerSocket server = new ServerSocket(0, 4, loopback)) {
            int port = server.getLocalPort();
            if (portFile != null && !portFile.isEmpty()) {
                try (FileOutputStream out = new FileOutputStream(portFile)) {
                    out.write((port + "\n").getBytes(StandardCharsets.US_ASCII));
                    out.getFD().sync();
                }
            }
            System.out.println("DESKTOUCH_SHELL_PORT=" + port);
            System.out.flush();

            while (true) {
                Socket client = server.accept();
                if (!handleClient(client)) {
                    destroyDevices();
                    break;
                }
                destroyDevices();
            }
        }
    }

    private boolean handleClient(Socket client) {
        try (Socket sock = client) {
            sock.setTcpNoDelay(true);
            sock.setSoTimeout(3000);
            BufferedReader reader = new BufferedReader(
                new InputStreamReader(sock.getInputStream(), StandardCharsets.US_ASCII));
            BufferedWriter writer = new BufferedWriter(
                new OutputStreamWriter(sock.getOutputStream(), StandardCharsets.US_ASCII));

            String authLine = reader.readLine();
            if (authLine == null || !authLine.startsWith("AUTH ")) {
                return true;
            }
            byte[] candidate = authLine.substring(5).trim()
                .toUpperCase(Locale.ROOT).getBytes(StandardCharsets.US_ASCII);
            if (!MessageDigest.isEqual(candidate, expectedTokenBytes)) {
                writer.write("ERR\n");
                writer.flush();
                return true;
            }
            sock.setSoTimeout(0);
            writer.write("OK\n");
            writer.flush();

            String line;
            while ((line = reader.readLine()) != null) {
                if (line.isEmpty()) continue;
                if ("PING".equals(line)) {
                    writer.write("PONG\n");
                    writer.flush();
                    continue;
                }
                if ("QUIT".equals(line)) {
                    writer.write("BYE\n");
                    writer.flush();
                    return false;
                }
                if ("END".equals(line)) {
                    destroyDevices();
                    writer.write("OK\n");
                    writer.flush();
                    continue;
                }
                if (line.startsWith("BEGIN ")) {
                    String[] parts = line.split(" ");
                    int w = parts.length >= 3 ? parseBoundedInt(parts[1], 200, 16384, 1080) : 1080;
                    int h = parts.length >= 3 ? parseBoundedInt(parts[2], 200, 16384, 2400) : 2400;
                    boolean started = ensureDevices(w, h);
                    writer.write(started ? "OK\n" : "ERR\n");
                    writer.flush();
                    continue;
                }
                if (line.startsWith("MOVETO ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int x = parseBoundedInt(parts[1], 0, screenWidth - 1, 0);
                        int y = parseBoundedInt(parts[2], 0, screenHeight - 1, 0);
                        moveTo(x, y);
                    }
                    continue;
                }
                if (line.startsWith("BTN ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int btn = parseBoundedInt(parts[1], 1, 5, 1);
                        boolean down = "1".equals(parts[2]);
                        setMouseButton(btn, down);
                    }
                    continue;
                }
                if (line.startsWith("WHEEL ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int amount = parseBoundedInt(parts[1], -12000, 12000, 0);
                        boolean horizontal = "1".equals(parts[2]);
                        scrollWheel(amount, horizontal);
                    }
                    continue;
                }
                if (line.startsWith("KEY ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int vk = parseBoundedInt(parts[1], 0, 255, 0);
                        boolean down = "1".equals(parts[2]);
                        handleVk(vk, down);
                    }
                    continue;
                }
                if (line.startsWith("TEXT ")) {
                    String b64 = line.substring(5).trim();
                    typeBase64Text(b64);
                }
            }
            return true;
        } catch (IOException ignored) {
            return true;
        }
    }

    private synchronized boolean ensureDevices(int width, int height) {
        screenWidth = Math.max(100, width);
        screenHeight = Math.max(100, height);
        try {
            if (mouseFd == null) {
                mouseFd = createUhidDevice("DeskTouch Virtual Mouse", 0x3741, MOUSE_DESC);
                anchoredCursor = false;
                cursorX = 0;
                cursorY = 0;
                buttonMask = 0;
                vWheelCarry = 0;
                hWheelCarry = 0;
            }
            if (keyboardFd == null) {
                keyboardFd = createUhidDevice("DeskTouch Virtual Keyboard", 0x3742, KEYBOARD_DESC);
                keyModifierMask = 0;
                for (int i = 0; i < activeUsages.length; i++) activeUsages[i] = 0;
            }
            return mouseFd != null && keyboardFd != null;
        } catch (Exception ex) {
            destroyDevices();
            return false;
        }
    }

    private static FileDescriptor createUhidDevice(String name, int productId, byte[] desc)
            throws ErrnoException, IOException {
        FileDescriptor fd = Os.open("/dev/uhid", OsConstants.O_RDWR, 0);
        try {
            ByteBuffer buf = ByteBuffer.allocate(UHID_CREATE2_SIZE).order(ByteOrder.LITTLE_ENDIAN);
            buf.putInt(UHID_CREATE2);
            byte[] nameBytes = name.getBytes(StandardCharsets.UTF_8);
            buf.put(nameBytes, 0, Math.min(nameBytes.length, 127));
            buf.position(4 + 128 + 64 + 64);
            buf.putShort((short) desc.length);
            buf.putShort((short) 0x06); // BUS_VIRTUAL
            buf.putInt(0x18D1);
            buf.putInt(productId);
            buf.putInt(1);
            buf.putInt(0);
            buf.put(desc);
            Os.write(fd, buf.array(), 0, UHID_CREATE2_SIZE);
            waitForUhidStart(fd);
            return fd;
        } catch (Throwable err) {
            try { Os.close(fd); } catch (ErrnoException ignored) {}
            if (err instanceof ErrnoException) throw (ErrnoException) err;
            throw new IOException(err);
        }
    }

    private static void waitForUhidStart(FileDescriptor fd) throws ErrnoException, IOException {
        StructPollfd pfd = new StructPollfd();
        pfd.fd = fd;
        pfd.events = (short) OsConstants.POLLIN;
        StructPollfd[] pollfds = new StructPollfd[] { pfd };
        byte[] event = new byte[UHID_CREATE2_SIZE];
        long deadline = System.nanoTime() + 180_000_000L;
        while (System.nanoTime() < deadline) {
            int remainingMs = (int) Math.max(1L, (deadline - System.nanoTime()) / 1_000_000L);
            int ready = Os.poll(pollfds, remainingMs);
            if (ready <= 0) break;
            if ((pfd.revents & OsConstants.POLLIN) != 0) {
                int n = Os.read(fd, event, 0, event.length);
                if (n >= 4) {
                    int type = ByteBuffer.wrap(event).order(ByteOrder.LITTLE_ENDIAN).getInt(0);
                    if (type == UHID_START) return;
                }
            }
        }
    }

    private synchronized void destroyDevices() {
        if (buttonMask != 0 && mouseFd != null) {
            buttonMask = 0;
            writeMouseReport(0, 0, 0, 0);
        }
        if ((keyModifierMask != 0 || hasActiveKeys()) && keyboardFd != null) {
            keyModifierMask = 0;
            for (int i = 0; i < activeUsages.length; i++) activeUsages[i] = 0;
            writeKeyboardReport(0, activeUsages);
        }
        closeUhid(mouseFd);
        mouseFd = null;
        closeUhid(keyboardFd);
        keyboardFd = null;
        anchoredCursor = false;
    }

    private static void closeUhid(FileDescriptor fd) {
        if (fd == null) return;
        try {
            byte[] destroy = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(UHID_DESTROY).array();
            Os.write(fd, destroy, 0, destroy.length);
        } catch (ErrnoException | IOException ignored) {}
        try { Os.close(fd); } catch (ErrnoException ignored) {}
    }

    private synchronized void moveTo(int targetX, int targetY) {
        if (mouseFd == null && !ensureDevices(screenWidth, screenHeight)) return;
        if (!anchoredCursor) {
            // Pull Android's hardware pointer deterministically to top-left (0, 0)
            // before applying the entry coordinate.
            sendRelativeBurst(-Math.max(screenWidth, 2400), -Math.max(screenHeight, 2400));
            anchoredCursor = true;
            cursorX = 0;
            cursorY = 0;
        }
        int dx = targetX - cursorX;
        int dy = targetY - cursorY;
        if (dx == 0 && targetX == 0) dx = -16;
        else if (dx == 0 && targetX >= screenWidth - 1) dx = 16;
        if (dy == 0 && targetY == 0) dy = -16;
        else if (dy == 0 && targetY >= screenHeight - 1) dy = 16;

        cursorX = targetX;
        cursorY = targetY;
        if (dx != 0 || dy != 0) {
            sendRelativeBurst(dx, dy);
        }
    }

    private void sendRelativeBurst(int totalDx, int totalDy) {
        int rx = totalDx;
        int ry = totalDy;
        while (rx != 0 || ry != 0) {
            int stepX = Math.max(-127, Math.min(127, rx));
            int stepY = Math.max(-127, Math.min(127, ry));
            writeMouseReport(stepX, stepY, 0, 0);
            rx -= stepX;
            ry -= stepY;
        }
    }

    private synchronized void setMouseButton(int button, boolean down) {
        if (mouseFd == null && !ensureDevices(screenWidth, screenHeight)) return;
        int bit = 1 << (button - 1);
        int next = down ? (buttonMask | bit) : (buttonMask & ~bit);
        if (next == buttonMask) return;
        buttonMask = next;
        writeMouseReport(0, 0, 0, 0);
    }

    private synchronized void scrollWheel(int amount, boolean horizontal) {
        if (mouseFd == null && !ensureDevices(screenWidth, screenHeight)) return;
        if (amount == 0) return;
        if (horizontal) {
            hWheelCarry += amount;
            int steps = hWheelCarry / 120;
            if (steps == 0 && Math.abs(hWheelCarry) >= 60) {
                steps = hWheelCarry > 0 ? 1 : -1;
            }
            if (steps != 0) {
                hWheelCarry -= steps * 120;
                int clamped = Math.max(-127, Math.min(127, steps));
                writeMouseReport(0, 0, 0, clamped);
            }
        } else {
            vWheelCarry += amount;
            int steps = vWheelCarry / 120;
            if (steps == 0 && Math.abs(vWheelCarry) >= 60) {
                steps = vWheelCarry > 0 ? 1 : -1;
            }
            if (steps != 0) {
                vWheelCarry -= steps * 120;
                int clamped = Math.max(-127, Math.min(127, steps));
                writeMouseReport(0, 0, clamped, 0);
            }
        }
    }

    private void writeMouseReport(int relX, int relY, int vWheel, int hWheel) {
        FileDescriptor fd = mouseFd;
        if (fd == null) return;
        ByteBuffer pkt = ByteBuffer.allocate(6 + 5).order(ByteOrder.LITTLE_ENDIAN);
        pkt.putInt(UHID_INPUT2);
        pkt.putShort((short) 5);
        pkt.put((byte) (buttonMask & 0x1F));
        pkt.put((byte) Math.max(-127, Math.min(127, relX)));
        pkt.put((byte) Math.max(-127, Math.min(127, relY)));
        pkt.put((byte) Math.max(-127, Math.min(127, vWheel)));
        pkt.put((byte) Math.max(-127, Math.min(127, hWheel)));
        try {
            Os.write(fd, pkt.array(), 0, pkt.capacity());
        } catch (ErrnoException | IOException ignored) {}
    }

    private synchronized void handleVk(int vk, boolean down) {
        if (keyboardFd == null && !ensureDevices(screenWidth, screenHeight)) return;
        int modBit = HidKeyTable.modifierBitForVk(vk);
        if (modBit != 0) {
            int next = down ? (keyModifierMask | modBit) : (keyModifierMask & ~modBit);
            if (next != keyModifierMask) {
                keyModifierMask = next;
                writeKeyboardReport(keyModifierMask, activeUsages);
            }
            return;
        }
        int usage = HidKeyTable.usageForVk(vk);
        if (usage == 0) return;
        boolean changed = false;
        if (down) {
            boolean alreadyDown = false;
            int freeSlot = -1;
            for (int i = 0; i < activeUsages.length; i++) {
                if (activeUsages[i] == usage) {
                    alreadyDown = true;
                    break;
                }
                if (activeUsages[i] == 0 && freeSlot < 0) freeSlot = i;
            }
            if (!alreadyDown && freeSlot >= 0) {
                activeUsages[freeSlot] = usage;
                changed = true;
            }
        } else {
            for (int i = 0; i < activeUsages.length; i++) {
                if (activeUsages[i] == usage) {
                    activeUsages[i] = 0;
                    changed = true;
                }
            }
        }
        if (changed) writeKeyboardReport(keyModifierMask, activeUsages);
    }

    private synchronized void typeBase64Text(String b64) {
        if (keyboardFd == null && !ensureDevices(screenWidth, screenHeight)) return;
        byte[] raw;
        try {
            raw = Base64.getDecoder().decode(b64);
        } catch (IllegalArgumentException ex) {
            return;
        }
        String text = new String(raw, StandardCharsets.UTF_8);
        for (int i = 0; i < text.length(); i++) {
            HidKeyTable.Stroke stroke = HidKeyTable.forAsciiChar(text.charAt(i));
            if (stroke == null) continue;
            int mods = (keyModifierMask & ~(HidKeyTable.MOD_LEFT_SHIFT | HidKeyTable.MOD_RIGHT_SHIFT))
                | stroke.modifier;
            int[] tempUsages = new int[] { stroke.usage, 0, 0, 0, 0, 0 };
            writeKeyboardReport(mods, tempUsages);
            writeKeyboardReport(keyModifierMask, activeUsages);
        }
    }

    private boolean hasActiveKeys() {
        for (int u : activeUsages) {
            if (u != 0) return true;
        }
        return false;
    }

    private void writeKeyboardReport(int mods, int[] usages) {
        FileDescriptor fd = keyboardFd;
        if (fd == null) return;
        ByteBuffer pkt = ByteBuffer.allocate(6 + 8).order(ByteOrder.LITTLE_ENDIAN);
        pkt.putInt(UHID_INPUT2);
        pkt.putShort((short) 8);
        pkt.put((byte) (mods & 0xFF));
        pkt.put((byte) 0);
        for (int i = 0; i < 6; i++) {
            pkt.put((byte) (usages[i] & 0xFF));
        }
        try {
            Os.write(fd, pkt.array(), 0, pkt.capacity());
        } catch (ErrnoException | IOException ignored) {}
    }

    private static int parseBoundedInt(String raw, int min, int max, int fallback) {
        try {
            int val = Integer.parseInt(raw.trim());
            return Math.max(min, Math.min(max, val));
        } catch (NumberFormatException ex) {
            return fallback;
        }
    }
}
