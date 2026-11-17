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
import java.util.concurrent.locks.LockSupport;

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
    private final ShellPointerMath.PointerCompensator pointer = new ShellPointerMath.PointerCompensator();
    private final ShellPointerMath.SubFrameGlide subFrameGlide = new ShellPointerMath.SubFrameGlide();
    private final int[] glideOutXY = new int[2];
    private final Object mouseQueueLock = new Object();
    private FileDescriptor mouseFd;
    private FileDescriptor keyboardFd;
    private int screenWidth = 1080;
    private int screenHeight = 2400;
    private int configuredPointerSpeed;
    private volatile int configuredRefreshHz = 60;
    private boolean hasDumpsysVelocityParams;
    private static final int MOVE_DONE_IDLE = 0;
    private static final int MOVE_SENT_SETTLED = 1;
    private static final int MOVE_SENT_MORE = 2;

    private boolean hasPendingMove;
    private boolean pendingMoveCoasting;
    private int pendingMoveX;
    private int pendingMoveY;
    private int pendingPushX;
    private int pendingPushY;
    private long nextMoveSeq;
    private long pendingMoveSeq;
    private long appliedMoveSeq;
    private long lastMouseWriteNs;
    private long nextSubFrameTickNs;
    private volatile boolean daemonRunning = true;
    private Thread mousePacerThread;
    private int buttonMask;
    private boolean mousePrimedForReader;
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

        try {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY);
        } catch (Throwable ignored) {}
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
            applyLowLatencyWifiConfigAsync();
            startMousePacer();

            try {
                while (true) {
                    Socket client = server.accept();
                    if (!handleClient(client)) {
                        destroyDevices();
                        break;
                    }
                    // Keep kernel /dev/uhid devices registered across brief reconnects
                    // so Android does not churn input device IDs or fall back to software pointer.
                    releaseInputs();
                }
            } finally {
                stopMousePacer();
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

            String[] pendingSlot = new String[1];
            String line;
            while ((line = ShellPointerMath.readNextCommand(reader, pendingSlot)) != null) {
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
                if ("DESTROY".equals(line)) {
                    destroyDevices();
                    writer.write("OK\n");
                    writer.flush();
                    continue;
                }
                if ("END".equals(line)) {
                    releaseInputs();
                    writer.write("OK\n");
                    writer.flush();
                    continue;
                }
                if (line.startsWith("BEGIN ")) {
                    applyLowLatencyWifiConfigAsync();
                    String[] parts = line.split(" ");
                    int w = parts.length >= 3 ? parseBoundedInt(parts[1], 200, 16384, 1080) : 1080;
                    int h = parts.length >= 3 ? parseBoundedInt(parts[2], 200, 16384, 2400) : 2400;
                    int speed = parts.length >= 4 ? parseBoundedInt(parts[3], -7, 7, configuredPointerSpeed) : configuredPointerSpeed;
                    int hz = parts.length >= 5 ? parseBoundedInt(parts[4], 24, 360, configuredRefreshHz) : configuredRefreshHz;
                    boolean started = ensureDevices(w, h, speed, hz);
                    writer.write(started ? "OK\n" : "ERR\n");
                    writer.flush();
                    continue;
                }
                if (line.startsWith("MOVETO ")) {
                    applyMoveLine(line);
                    continue;
                }
                if (line.startsWith("BTN ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int btn = parseBoundedInt(parts[1], 1, 5, 1);
                        boolean down = "1".equals(parts[2]);
                        flushPendingMouseNow();
                        setMouseButton(btn, down);
                    }
                    continue;
                }
                if (line.startsWith("WHEEL ")) {
                    String[] parts = line.split(" ");
                    if (parts.length >= 3) {
                        int amount = parseBoundedInt(parts[1], -12000, 12000, 0);
                        boolean horizontal = "1".equals(parts[2]);
                        flushPendingMouseNow();
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
        } finally {
            releaseInputs();
        }
    }

    private void startMousePacer() {
        daemonRunning = true;
        Thread t = new Thread(() -> {
            try {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY);
            } catch (Throwable ignored) {}
            while (daemonRunning) {
                int tx;
                int ty;
                int px;
                int py;
                long seq;
                boolean allowSameSeq;
                long subFrameNs;
                synchronized (mouseQueueLock) {
                    while (daemonRunning && !hasPendingMove && !subFrameGlide.isActive()) {
                        nextSubFrameTickNs = 0L;
                        try {
                            mouseQueueLock.wait();
                        } catch (InterruptedException ignored) {}
                    }
                    if (!daemonRunning) break;
                    subFrameNs = subFrameGlide.subFrameIntervalNs();
                    long nowNs = System.nanoTime();
                    if (nextSubFrameTickNs == 0L) {
                        nextSubFrameTickNs = nowNs;
                    }
                    long minGapNs = pendingMoveCoasting
                        ? subFrameNs
                        : (subFrameGlide.isActive() ? subFrameNs : Math.min(900_000L, subFrameNs / 3L));
                    long targetTickNs = Math.max(
                        nextSubFrameTickNs,
                        lastMouseWriteNs == 0L ? 0L : (lastMouseWriteNs + minGapNs));
                    long waitNs = targetTickNs - nowNs;
                    if (waitNs > 1_200_000L) {
                        long sleepNs = waitNs - 350_000L;
                        long waitMs = sleepNs / 1_000_000L;
                        int nanos = (int) (sleepNs % 1_000_000L);
                        try {
                            mouseQueueLock.wait(waitMs, nanos);
                        } catch (InterruptedException ignored) {}
                        continue;
                    }
                    if (waitNs > 0L) {
                        long parkNs = waitNs - 160_000L;
                        if (parkNs > 80_000L) {
                            LockSupport.parkNanos(parkNs);
                        }
                        while (System.nanoTime() < targetTickNs && daemonRunning) {
                            Thread.yield();
                        }
                        if (!daemonRunning) break;
                        nowNs = System.nanoTime();
                    }

                    if (hasPendingMove || subFrameGlide.isActive()) {
                        px = subFrameGlide.currentPushX();
                        py = subFrameGlide.currentPushY();
                        if (subFrameGlide.sampleNext(nowNs, glideOutXY)) {
                            tx = glideOutXY[0];
                            ty = glideOutXY[1];
                        } else {
                            tx = pendingMoveX;
                            ty = pendingMoveY;
                            px = pendingPushX;
                            py = pendingPushY;
                        }
                    } else {
                        break;
                    }
                    seq = pendingMoveSeq;
                    allowSameSeq = pendingMoveCoasting || subFrameGlide.isActive() || hasPendingMove;
                    pendingMoveCoasting = false;
                    hasPendingMove = false;
                    pendingPushX = 0;
                    pendingPushY = 0;
                    nextSubFrameTickNs = targetTickNs + subFrameNs;
                }
                int moveOutcome = moveTo(tx, ty, px, py, seq, allowSameSeq);
                synchronized (mouseQueueLock) {
                    if (moveOutcome != MOVE_DONE_IDLE) {
                        lastMouseWriteNs = System.nanoTime();
                        if (moveOutcome == MOVE_SENT_MORE && daemonRunning &&
                                !hasPendingMove && !subFrameGlide.isActive() && seq == pendingMoveSeq) {
                            pendingMoveX = Math.max(0, Math.min(screenWidth - 1, tx));
                            pendingMoveY = Math.max(0, Math.min(screenHeight - 1, ty));
                            pendingPushX = 0;
                            pendingPushY = 0;
                            pendingMoveCoasting = true;
                            hasPendingMove = true;
                        }
                    } else if (!hasPendingMove && !subFrameGlide.isActive()) {
                        nextSubFrameTickNs = 0L;
                    }
                }
            }
        }, "DeskTouch-UhidPacer");
        t.setDaemon(true);
        mousePacerThread = t;
        t.start();
    }

    private void stopMousePacer() {
        daemonRunning = false;
        synchronized (mouseQueueLock) {
            hasPendingMove = false;
            pendingPushX = 0;
            pendingPushY = 0;
            nextSubFrameTickNs = 0L;
            subFrameGlide.reset();
            mouseQueueLock.notifyAll();
        }
    }

    private void applyMoveLine(String line) {
        int len = line.length();
        int idx = 7; // Skip "MOVETO "
        if (idx >= len) return;
        int xEnd = findSpaceOrEnd(line, idx, len);
        int x = parseAsciiBoundedInt(line, idx, xEnd, 0, screenWidth - 1, -1);
        if (x < 0 || xEnd >= len) return;
        int yStart = xEnd + 1;
        int yEnd = findSpaceOrEnd(line, yStart, len);
        int y = parseAsciiBoundedInt(line, yStart, yEnd, 0, screenHeight - 1, -1);
        if (y < 0) return;
        int pushX = 0;
        int pushY = 0;
        int prevX = x;
        int prevY = y;
        if (yEnd + 1 < len) {
            int pxStart = yEnd + 1;
            int pxEnd = findSpaceOrEnd(line, pxStart, len);
            pushX = parseAsciiBoundedInt(line, pxStart, pxEnd, -240, 240, 0);
            if (pxEnd + 1 < len) {
                int pyStart = pxEnd + 1;
                int pyEnd = findSpaceOrEnd(line, pyStart, len);
                pushY = parseAsciiBoundedInt(line, pyStart, pyEnd, -240, 240, 0);
                if (pyEnd + 1 < len) {
                    int pvxStart = pyEnd + 1;
                    int pvxEnd = findSpaceOrEnd(line, pvxStart, len);
                    prevX = parseAsciiBoundedInt(line, pvxStart, pvxEnd, 0, screenWidth - 1, x);
                    if (pvxEnd + 1 < len) {
                        int pvyStart = pvxEnd + 1;
                        int pvyEnd = findSpaceOrEnd(line, pvyStart, len);
                        prevY = parseAsciiBoundedInt(line, pvyStart, pvyEnd, 0, screenHeight - 1, y);
                    }
                }
            }
        }
        long nowNs = System.nanoTime();
        synchronized (mouseQueueLock) {
            boolean sameX = hasPendingMove && pendingMoveX == x;
            boolean sameY = hasPendingMove && pendingMoveY == y;
            pendingMoveX = x;
            pendingMoveY = y;
            if (pushX != 0) {
                pendingPushX = Math.max(-240, Math.min(240, (sameX ? pendingPushX : 0) + pushX));
            } else if (!sameX) {
                pendingPushX = 0;
            }
            if (pushY != 0) {
                pendingPushY = Math.max(-240, Math.min(240, (sameY ? pendingPushY : 0) + pushY));
            } else if (!sameY) {
                pendingPushY = 0;
            }
            subFrameGlide.onTarget(prevX, prevY, x, y, pendingPushX, pendingPushY, nowNs);
            pendingMoveSeq = ++nextMoveSeq;
            pendingMoveCoasting = false;
            hasPendingMove = true;
            mouseQueueLock.notifyAll();
        }
    }

    private void flushPendingMouseNow() {
        boolean have = false;
        int tx = 0;
        int ty = 0;
        int px = 0;
        int py = 0;
        long seq = 0L;
        synchronized (mouseQueueLock) {
            if (hasPendingMove || subFrameGlide.isActive()) {
                px = Math.abs(pendingPushX) >= Math.abs(subFrameGlide.currentPushX())
                    ? pendingPushX : subFrameGlide.currentPushX();
                py = Math.abs(pendingPushY) >= Math.abs(subFrameGlide.currentPushY())
                    ? pendingPushY : subFrameGlide.currentPushY();
                subFrameGlide.snapToTarget();
                if (subFrameGlide.sampleNext(System.nanoTime(), glideOutXY)) {
                    tx = glideOutXY[0];
                    ty = glideOutXY[1];
                } else {
                    tx = pendingMoveX;
                    ty = pendingMoveY;
                }
                seq = pendingMoveSeq;
                pendingMoveCoasting = false;
                hasPendingMove = false;
                pendingPushX = 0;
                pendingPushY = 0;
                nextSubFrameTickNs = 0L;
                have = true;
            }
        }
        if (have) {
            for (int step = 0; step < 12; step++) {
                int outcome = moveTo(tx, ty, step == 0 ? px : 0, step == 0 ? py : 0, seq, true);
                if (outcome == MOVE_DONE_IDLE) break;
                synchronized (mouseQueueLock) {
                    lastMouseWriteNs = System.nanoTime();
                }
                if (outcome != MOVE_SENT_MORE) break;
            }
        }
    }

    private synchronized boolean ensureDevices(int width, int height) {
        return ensureDevices(width, height, configuredPointerSpeed, configuredRefreshHz);
    }

    private synchronized boolean ensureDevices(int width, int height, int pointerSpeed, int refreshHz) {
        int nextW = Math.max(100, width);
        int nextH = Math.max(100, height);
        boolean speedChanged = pointerSpeed != configuredPointerSpeed;
        configuredPointerSpeed = pointerSpeed;
        if (refreshHz >= 24 && refreshHz <= 360) {
            configuredRefreshHz = refreshHz;
        }
        screenWidth = nextW;
        screenHeight = nextH;
        ShellPointerMath.VelocityParams initParams = (!hasDumpsysVelocityParams || speedChanged)
            ? ShellPointerMath.defaultVelocityParams(configuredPointerSpeed)
            : null;
        pointer.configure(nextW, nextH, initParams, configuredRefreshHz);
        subFrameGlide.configure(configuredRefreshHz);
        try {
            boolean createdMouse = false;
            boolean createdKeyboard = false;
            if (mouseFd == null) {
                mouseFd = createUhidDevice("DeskTouch USB Mouse", 0x3741, MOUSE_DESC);
                pointer.releaseAnchor();
                buttonMask = 0;
                mousePrimedForReader = false;
                vWheelCarry = 0;
                hWheelCarry = 0;
                createdMouse = true;
            }
            if (keyboardFd == null) {
                keyboardFd = createUhidDevice("DeskTouch USB Keyboard", 0x3742, KEYBOARD_DESC);
                keyModifierMask = 0;
                for (int i = 0; i < activeUsages.length; i++) activeUsages[i] = 0;
                createdKeyboard = true;
            }
            if (createdMouse || createdKeyboard) {
                if (createdMouse && mouseFd != null) {
                    writeMouseReport(1, 0, 0, 0);
                    LockSupport.parkNanos(12_000_000L);
                    writeMouseReport(-1, 0, 0, 0);
                    mousePrimedForReader = true;
                }
                // Let EventHub/InputReader register CursorInputMapper and VelocityControl before probing.
                LockSupport.parkNanos(140_000_000L);
            }
            if (createdMouse || speedChanged || !hasDumpsysVelocityParams) {
                ShellPointerMath.VelocityParams probed = probeVelocityParamsBlocking(280);
                if (probed != null) {
                    hasDumpsysVelocityParams = true;
                    pointer.updateVelocityParams(probed);
                } else {
                    scheduleDelayedVelocityProbe();
                }
            }
            return mouseFd != null && keyboardFd != null;
        } catch (Exception ex) {
            destroyDevices();
            return false;
        }
    }

    private void scheduleDelayedVelocityProbe() {
        Thread retry = new Thread(() -> {
            LockSupport.parkNanos(450_000_000L);
            if (!daemonRunning || mouseFd == null) return;
            ShellPointerMath.VelocityParams probed = probeVelocityParamsBlocking(600);
            if (probed != null) {
                synchronized (this) {
                    hasDumpsysVelocityParams = true;
                    pointer.updateVelocityParams(probed);
                }
            }
        }, "DeskTouch-VelRetry");
        retry.setDaemon(true);
        retry.start();
    }

    private ShellPointerMath.VelocityParams probeVelocityParamsBlocking(long timeoutMs) {
        final ShellPointerMath.VelocityParams[] foundRef = new ShellPointerMath.VelocityParams[1];
        final Process[] procRef = new Process[1];
        Thread t = new Thread(() -> {
            try {
                Process proc = new ProcessBuilder("dumpsys", "input").redirectErrorStream(true).start();
                procRef[0] = proc;
                try (BufferedReader br = new BufferedReader(
                    new InputStreamReader(proc.getInputStream(), StandardCharsets.UTF_8))) {
                    StringBuilder sb = new StringBuilder(8192);
                    String row;
                    while ((row = br.readLine()) != null) {
                        sb.append(row).append('\n');
                    }
                    foundRef[0] = ShellPointerMath.parseDumpsysVelocityParamsForDevice(
                        sb.toString(), "DeskTouch USB Mouse");
                }
            } catch (Throwable ignored) {
            } finally {
                Process p = procRef[0];
                if (p != null) {
                    try { p.destroy(); } catch (Throwable ignored) {}
                }
            }
        }, "DeskTouch-VelProbe");
        t.setDaemon(true);
        t.start();
        try {
            t.join(Math.max(50L, timeoutMs));
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
        Process p = procRef[0];
        if (t.isAlive() && p != null) {
            try { p.destroyForcibly(); } catch (Throwable ignored) {}
        }
        return foundRef[0];
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
            buf.putShort((short) 0x03); // BUS_USB so Android treats keyboard shortcuts as standard USB hardware keyboard
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

    private synchronized void releaseInputs() {
        synchronized (mouseQueueLock) {
            hasPendingMove = false;
            pendingPushX = 0;
            pendingPushY = 0;
            nextSubFrameTickNs = 0L;
            subFrameGlide.reset();
        }
        if (buttonMask != 0 && mouseFd != null) {
            buttonMask = 0;
            writeMouseReport(0, 0, 0, 0);
        }
        if ((keyModifierMask != 0 || hasActiveKeys()) && keyboardFd != null) {
            keyModifierMask = 0;
            for (int i = 0; i < activeUsages.length; i++) activeUsages[i] = 0;
            writeKeyboardReport(0, activeUsages);
        }
        vWheelCarry = 0;
        hWheelCarry = 0;
        pointer.releaseAnchor();
    }

    private synchronized void destroyDevices() {
        releaseInputs();
        closeUhid(mouseFd);
        mouseFd = null;
        closeUhid(keyboardFd);
        keyboardFd = null;
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

    private synchronized int moveTo(int targetX, int targetY, int pushX, int pushY, long seq, boolean coasting) {
        if (!coasting && seq <= appliedMoveSeq) return MOVE_DONE_IDLE;
        if (seq > appliedMoveSeq) appliedMoveSeq = seq;
        if (mouseFd == null && !ensureDevices(screenWidth, screenHeight, configuredPointerSpeed, configuredRefreshHz)) {
            return MOVE_DONE_IDLE;
        }
        ShellPointerMath.MoveStep step = pointer.stepTo(targetX, targetY, pushX, pushY, System.nanoTime());
        if (!step.hasMotion()) return MOVE_DONE_IDLE;
        if (step.needsAnchorReset) {
            if (!mousePrimedForReader) {
                int primeX = Integer.compare(step.anchorPullX, 0);
                int primeY = Integer.compare(step.anchorPullY, 0);
                if (primeX != 0 || primeY != 0) {
                    writeMouseReport(primeX, primeY, 0, 0);
                    // Wait for system InputReader/PointerController to allocate the cursor surface on event 1.
                    LockSupport.parkNanos(12_000_000L);
                }
                mousePrimedForReader = true;
            }
            sendRelativeBurst(step.anchorPullX, step.anchorPullY, true);
            LockSupport.parkNanos(400_000L);
            if (step.resetSignX != 0 || step.resetSignY != 0) {
                writeMouseReport(step.resetSignX, step.resetSignY, 0, 0);
                LockSupport.parkNanos(300_000L);
            }
        }
        if (step.rawX != 0 || step.rawY != 0) {
            if (step.needsAnchorReset) {
                sendRelativeBurst(step.rawX, step.rawY, true);
            } else {
                writeMouseReport(step.rawX, step.rawY, 0, 0);
                mousePrimedForReader = true;
            }
        }
        boolean needMore = !step.needsAnchorReset && pointer.hasRemainingMotion(targetX, targetY);
        return needMore ? MOVE_SENT_MORE : MOVE_SENT_SETTLED;
    }

    private void sendRelativeBurst(int totalDx, int totalDy, boolean paceChunks) {
        int steps = Math.max(1, (Math.max(Math.abs(totalDx), Math.abs(totalDy)) + 126) / 127);
        int rx = totalDx;
        int ry = totalDy;
        int sent = 0;
        for (int rem = steps; rem > 0; rem--) {
            int stepX = Math.max(-127, Math.min(127, rx / rem));
            int stepY = Math.max(-127, Math.min(127, ry / rem));
            if (stepX != 0 || stepY != 0) {
                writeMouseReport(stepX, stepY, 0, 0);
                rx -= stepX;
                ry -= stepY;
                sent++;
                if (paceChunks && (rem > 1 || rx != 0 || ry != 0)) {
                    LockSupport.parkNanos((sent % 8 == 0) ? 700_000L : 350_000L);
                }
            }
        }
        while (rx != 0 || ry != 0) {
            int stepX = Math.max(-127, Math.min(127, rx));
            int stepY = Math.max(-127, Math.min(127, ry));
            writeMouseReport(stepX, stepY, 0, 0);
            rx -= stepX;
            ry -= stepY;
            sent++;
            if (paceChunks && (rx != 0 || ry != 0)) {
                LockSupport.parkNanos((sent % 8 == 0) ? 700_000L : 350_000L);
            }
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

    private static int findSpaceOrEnd(String s, int start, int len) {
        int i = start;
        while (i < len && s.charAt(i) != ' ') i++;
        return i;
    }

    private static int parseAsciiBoundedInt(String s, int start, int end, int min, int max, int fallback) {
        if (start >= end) return fallback;
        int i = start;
        boolean neg = false;
        char first = s.charAt(i);
        if (first == '-') {
            neg = true;
            i++;
        } else if (first == '+') {
            i++;
        }
        if (i >= end) return fallback;
        long val = 0;
        for (; i < end; i++) {
            char ch = s.charAt(i);
            if (ch < '0' || ch > '9') return fallback;
            val = val * 10 + (ch - '0');
            if (val > 1_000_000L) break;
        }
        long signed = neg ? -val : val;
        if (signed < min) return min;
        if (signed > max) return max;
        return (int) signed;
    }

    private volatile long lastWifiConfigNs;

    private void applyLowLatencyWifiConfigAsync() {
        long now = System.nanoTime();
        if (lastWifiConfigNs != 0L && now - lastWifiConfigNs < 60_000_000_000L) return;
        lastWifiConfigNs = now;
        Thread t = new Thread(() -> {
            try {
                new ProcessBuilder("cmd", "wifi", "set-scan-always-available", "disabled")
                    .redirectErrorStream(true).start().waitFor();
            } catch (Throwable ignored) {}
        }, "DeskTouch-WifiLowLatency");
        t.setDaemon(true);
        t.start();
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
