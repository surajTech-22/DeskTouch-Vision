package com.desktouch.vision;

import android.content.Context;
import android.graphics.Point;
import android.util.Log;
import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Authenticates over loopback TCP to `ShellInputDaemon` using credentials
 * delivered across the verified mutual-TLS Windows connection. Enables full
 * remote mouse and keyboard control on Android without Accessibility Settings.
 */
final class ShellInputClient {
    private static final String PREFS = "desktouch_android_control";
    private static final String ALLOW_SHELL_BRIDGE = "user_allowed_shell_bridge";
    private static final ExecutorService WORKER = Executors.newSingleThreadExecutor(r -> {
        Thread t = new Thread(() -> {
            try {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY);
            } catch (Throwable ignored) {}
            r.run();
        }, "DeskTouch shell-bridge");
        t.setDaemon(true);
        return t;
    });
    private static final Object MOVE_LOCK = new Object();
    private static final Object CLIENT_LOCK = new Object();

    private static volatile Context appContext;
    private static volatile boolean connectedReady;
    private static volatile boolean remoteSessionActive;
    private static volatile int configuredPort;
    private static volatile String configuredSecret;
    private static volatile int lastPointerSpeed;
    private static volatile int lastRefreshHz = 60;
    private static volatile Socket socket;
    private static volatile BufferedWriter writer;
    private static volatile BufferedReader reader;
    private static final AtomicBoolean moveDrainScheduled = new AtomicBoolean(false);
    private static boolean hasPendingMove;
    private static int pendingMoveX;
    private static int pendingMoveY;
    private static int pendingPushX;
    private static int pendingPushY;
    private static Runnable pendingMoveAck;

    private ShellInputClient() {}

    static boolean allowed(Context context) {
        if (context == null) return true;
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(ALLOW_SHELL_BRIDGE, true);
    }

    static void setAllowed(Context context, boolean allow) {
        if (context == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putBoolean(ALLOW_SHELL_BRIDGE, allow).apply();
        if (!allow) {
            endRemote();
        }
        DeskTouchRuntime.accessibilityPermissionChanged(context);
    }

    static boolean ready(Context context) {
        if (!connectedReady || !allowed(context)) return false;
        Socket s = socket;
        return s != null && !s.isClosed();
    }

    static boolean hasBridgeConfig(Context context) {
        return allowed(context) && configuredPort > 0 && configuredSecret != null;
    }

    static void configure(Context context, int port, String secret) {
        if (context == null || port <= 0 || port > 65535 || secret == null || secret.length() < 32) {
            return;
        }
        final Context app = context.getApplicationContext();
        appContext = app;
        boolean sameEndpoint = port == configuredPort && secret.equals(configuredSecret);
        configuredPort = port;
        configuredSecret = secret;
        if (sameEndpoint && ready(app)) {
            return;
        }
        WORKER.execute(() -> {
            synchronized (CLIENT_LOCK) {
                connectAndAuthLocked(app, port, secret);
                if (connectedReady && remoteSessionActive) {
                    ensureBeginLocked(app, lastPointerSpeed, lastRefreshHz);
                }
            }
            DeskTouchRuntime.accessibilityPermissionChanged(app);
        });
    }

    private static boolean connectAndAuthLocked(Context app, int port, String secret) {
        closeSocketLocked();
        if (port <= 0 || secret == null || secret.length() < 32) {
            connectedReady = false;
            return false;
        }
        try {
            Socket s = new Socket();
            s.connect(new InetSocketAddress("127.0.0.1", port), 1200);
            s.setTcpNoDelay(true);
            s.setSoTimeout(1200);
            BufferedWriter w = new BufferedWriter(
                new OutputStreamWriter(s.getOutputStream(), StandardCharsets.US_ASCII));
            BufferedReader r = new BufferedReader(
                new InputStreamReader(s.getInputStream(), StandardCharsets.US_ASCII));
            w.write("AUTH " + secret + "\n");
            w.flush();
            String ack = r.readLine();
            if (!"OK".equals(ack)) {
                try { s.close(); } catch (IOException ignored) {}
                connectedReady = false;
                return false;
            }
            s.setSoTimeout(800);
            socket = s;
            writer = w;
            reader = r;
            connectedReady = true;
            Log.i("DeskTouch", "Accessibility-free Shell Bridge virtual input ready on loopback:" + port);
            return true;
        } catch (IOException ex) {
            connectedReady = false;
            Log.w("DeskTouch", "Shell Bridge loopback connect deferred: " + ex.getMessage());
            return false;
        }
    }

    private static boolean pingLocked() {
        BufferedWriter w = writer;
        BufferedReader r = reader;
        if (w == null || r == null) return false;
        try {
            w.write("PING\n");
            w.flush();
            return "PONG".equals(r.readLine());
        } catch (IOException ex) {
            closeSocketLocked();
            connectedReady = false;
            return false;
        }
    }

    private static boolean ensureBeginLocked(Context app, int pointerSpeed, int refreshHz) {
        if (app == null) app = appContext;
        if (app == null) return false;
        Point size = PhoneScreenGeometry.read(app);
        int w = Math.max(200, size.x);
        int h = Math.max(200, size.y);
        int hz = refreshHz >= 24 && refreshHz <= 360 ? refreshHz : 60;
        String cmd = "BEGIN " + w + " " + h + " " + pointerSpeed + " " + hz;
        for (int attempt = 0; attempt < 2; attempt++) {
            if (writer == null || reader == null || !connectedReady) {
                if (!connectAndAuthLocked(app, configuredPort, configuredSecret)) {
                    return false;
                }
            }
            try {
                writer.write(cmd);
                writer.write('\n');
                writer.flush();
                String resp = reader.readLine();
                if ("OK".equals(resp)) {
                    connectedReady = true;
                    return true;
                }
                closeSocketLocked();
                connectedReady = false;
            } catch (IOException ex) {
                closeSocketLocked();
                connectedReady = false;
            }
        }
        return false;
    }

    /**
     * Synchronously verifies (and self-heals on loopback if needed) both the
     * Shell Bridge socket and kernel virtual HID devices before granting control,
     * preventing transient loopback timeouts from falling back to Accessibility.
     */
    static boolean prepareReverseClaim(Context context, int pointerSpeed, int refreshHz) {
        lastPointerSpeed = pointerSpeed;
        if (refreshHz >= 24 && refreshHz <= 360) lastRefreshHz = refreshHz;
        if (context == null || !allowed(context)) return false;
        final Context app = context.getApplicationContext();
        appContext = app;
        if (configuredPort <= 0 || configuredSecret == null) return false;

        final CompletableFuture<Boolean> done = new CompletableFuture<>();
        WORKER.execute(() -> {
            boolean ok;
            synchronized (CLIENT_LOCK) {
                boolean wasReady = connectedReady;
                if (!ready(app) || !pingLocked()) {
                    connectAndAuthLocked(app, configuredPort, configuredSecret);
                }
                ok = connectedReady && ensureBeginLocked(app, lastPointerSpeed, lastRefreshHz);
                remoteSessionActive = ok;
                if (wasReady != connectedReady) {
                    DeskTouchRuntime.accessibilityPermissionChanged(app);
                }
            }
            done.complete(ok);
        });
        try {
            return done.get(550, TimeUnit.MILLISECONDS);
        } catch (Exception ex) {
            return ready(app);
        }
    }

    static void beginRemote(Context context) {
        beginRemote(context, lastPointerSpeed, lastRefreshHz);
    }

    static void beginRemote(Context context, int pointerSpeed, int refreshHz) {
        if (context == null) return;
        lastPointerSpeed = pointerSpeed;
        if (refreshHz >= 24 && refreshHz <= 360) lastRefreshHz = refreshHz;
        final Context app = context.getApplicationContext();
        appContext = app;
        if (!hasBridgeConfig(app) && !ready(app)) return;
        remoteSessionActive = true;
        WORKER.execute(() -> {
            boolean wasReady = connectedReady;
            boolean ok;
            synchronized (CLIENT_LOCK) {
                ok = ensureBeginLocked(app, lastPointerSpeed, lastRefreshHz);
            }
            if (!ok || wasReady != connectedReady) {
                DeskTouchRuntime.accessibilityPermissionChanged(app);
            }
        });
    }

    static boolean movePointer(int x, int y, Runnable ack) {
        return movePointer(x, y, 0, 0, ack);
    }

    static boolean movePointer(int x, int y, int pushX, int pushY, Runnable ack) {
        if (!connectedReady && !hasBridgeConfig(appContext)) return false;
        synchronized (MOVE_LOCK) {
            boolean sameX = hasPendingMove && pendingMoveX == x;
            boolean sameY = hasPendingMove && pendingMoveY == y;
            pendingMoveX = x;
            pendingMoveY = y;
            if (pushX != 0) {
                int base = sameX ? pendingPushX : 0;
                pendingPushX = Math.max(-240, Math.min(240, base + pushX));
            } else if (!sameX) {
                pendingPushX = 0;
            }
            if (pushY != 0) {
                int base = sameY ? pendingPushY : 0;
                pendingPushY = Math.max(-240, Math.min(240, base + pushY));
            } else if (!sameY) {
                pendingPushY = 0;
            }
            if (ack != null) pendingMoveAck = ack;
            hasPendingMove = true;
        }
        if (moveDrainScheduled.compareAndSet(false, true)) {
            WORKER.execute(ShellInputClient::drainPendingMove);
        }
        return true;
    }

    private static void drainPendingMove() {
        while (true) {
            int x, y, pushX, pushY;
            Runnable ack;
            synchronized (MOVE_LOCK) {
                if (!hasPendingMove) {
                    moveDrainScheduled.set(false);
                    if (!hasPendingMove || !moveDrainScheduled.compareAndSet(false, true)) {
                        return;
                    }
                    continue;
                }
                x = pendingMoveX;
                y = pendingMoveY;
                pushX = pendingPushX;
                pushY = pendingPushY;
                ack = pendingMoveAck;
                hasPendingMove = false;
                pendingPushX = 0;
                pendingPushY = 0;
                pendingMoveAck = null;
            }
            if (pushX != 0 || pushY != 0) {
                sendLine("MOVETO " + x + " " + y + " " + pushX + " " + pushY);
            } else {
                sendLine("MOVETO " + x + " " + y);
            }
            if (ack != null) ack.run();
        }
    }

    static void mouseButton(int button, boolean pressed) {
        if ((!connectedReady && !hasBridgeConfig(appContext)) || button < 1 || button > 5) return;
        WORKER.execute(() -> sendLine("BTN " + button + " " + (pressed ? "1" : "0")));
    }

    static void wheel(int amount, boolean horizontal) {
        if ((!connectedReady && !hasBridgeConfig(appContext)) || amount == 0) return;
        WORKER.execute(() -> sendLine("WHEEL " + amount + " " + (horizontal ? "1" : "0")));
    }

    static void key(int vk, boolean pressed, Runnable accepted, Runnable rejected) {
        if (!connectedReady && !hasBridgeConfig(appContext)) {
            if (rejected != null) rejected.run();
            return;
        }
        final int usage = HidKeyTable.usageForVk(vk);
        final int mod = HidKeyTable.modifierBitForVk(vk);
        if (usage == 0 && mod == 0) {
            if (rejected != null) rejected.run();
            return;
        }
        WORKER.execute(() -> {
            if (sendLine("KEY " + vk + " " + (pressed ? "1" : "0"))) {
                if (accepted != null) accepted.run();
            } else if (rejected != null) {
                rejected.run();
            }
        });
    }

    static void text(String value, Runnable accepted, Runnable rejected) {
        if ((!connectedReady && !hasBridgeConfig(appContext)) || value == null || value.isEmpty()) {
            if (rejected != null) rejected.run();
            return;
        }
        boolean allAsciiTypeable = true;
        for (int i = 0; i < value.length(); i++) {
            if (HidKeyTable.forAsciiChar(value.charAt(i)) == null) {
                allAsciiTypeable = false;
                break;
            }
        }
        if (!allAsciiTypeable) {
            if (rejected != null) rejected.run();
            return;
        }
        final String encoded = Base64.getEncoder().encodeToString(
            value.getBytes(StandardCharsets.UTF_8));
        WORKER.execute(() -> {
            if (sendLine("TEXT " + encoded)) {
                if (accepted != null) accepted.run();
            } else if (rejected != null) {
                rejected.run();
            }
        });
    }

    static void endRemote() {
        remoteSessionActive = false;
        synchronized (MOVE_LOCK) {
            hasPendingMove = false;
            pendingPushX = 0;
            pendingPushY = 0;
            pendingMoveAck = null;
        }
        if (!connectedReady) return;
        WORKER.execute(() -> {
            String resp = sendAndReadLine("END");
            if (!"OK".equals(resp) && hasBridgeConfig(appContext)) {
                // Heal dropped loopback socket right away so next screen entry stays native.
                synchronized (CLIENT_LOCK) {
                    if (connectAndAuthLocked(appContext, configuredPort, configuredSecret)) {
                        DeskTouchRuntime.accessibilityPermissionChanged(appContext);
                    }
                }
            }
        });
    }

    static void disconnect() {
        remoteSessionActive = false;
        synchronized (MOVE_LOCK) {
            hasPendingMove = false;
            pendingPushX = 0;
            pendingPushY = 0;
            pendingMoveAck = null;
        }
        connectedReady = false;
        configuredPort = 0;
        configuredSecret = null;
        WORKER.execute(() -> {
            synchronized (CLIENT_LOCK) {
                closeSocketLocked();
            }
        });
    }

    private static boolean reconnectLocked() {
        Context app = appContext;
        if (!allowed(app) || configuredPort <= 0 || configuredSecret == null) {
            return false;
        }
        if (!connectAndAuthLocked(app, configuredPort, configuredSecret)) {
            return false;
        }
        if (remoteSessionActive) {
            return ensureBeginLocked(app, lastPointerSpeed, lastRefreshHz);
        }
        return true;
    }

    private static boolean sendLine(String line) {
        synchronized (CLIENT_LOCK) {
            boolean wasReady = connectedReady;
            for (int attempt = 0; attempt < 2; attempt++) {
                BufferedWriter w = writer;
                if (w == null) {
                    if (attempt == 0 && reconnectLocked()) {
                        continue;
                    }
                    break;
                }
                try {
                    w.write(line);
                    w.write('\n');
                    w.flush();
                    connectedReady = true;
                    if (!wasReady) {
                        DeskTouchRuntime.accessibilityPermissionChanged(appContext);
                    }
                    return true;
                } catch (IOException ex) {
                    closeSocketLocked();
                    connectedReady = false;
                    if (attempt == 0 && reconnectLocked()) {
                        continue;
                    }
                }
            }
            if (wasReady != connectedReady) {
                DeskTouchRuntime.accessibilityPermissionChanged(appContext);
            }
            return false;
        }
    }

    private static String sendAndReadLine(String line) {
        synchronized (CLIENT_LOCK) {
            BufferedWriter w = writer;
            BufferedReader r = reader;
            if (w == null || r == null) return null;
            try {
                w.write(line);
                w.write('\n');
                w.flush();
                return r.readLine();
            } catch (IOException ex) {
                closeSocketLocked();
                connectedReady = false;
                DeskTouchRuntime.accessibilityPermissionChanged(appContext);
                return null;
            }
        }
    }

    private static void markDisconnected(Context app) {
        synchronized (CLIENT_LOCK) {
            closeSocketLocked();
            connectedReady = false;
        }
        DeskTouchRuntime.accessibilityPermissionChanged(app);
    }

    private static void closeSocketLocked() {
        BufferedWriter w = writer;
        writer = null;
        BufferedReader r = reader;
        reader = null;
        Socket s = socket;
        socket = null;
        if (w != null) try { w.close(); } catch (IOException ignored) {}
        if (r != null) try { r.close(); } catch (IOException ignored) {}
        if (s != null) try { s.close(); } catch (IOException ignored) {}
    }
}
