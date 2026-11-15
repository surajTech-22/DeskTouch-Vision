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
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
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
        Thread t = new Thread(r, "DeskTouch shell-bridge");
        t.setDaemon(true);
        return t;
    });
    private static final Object MOVE_LOCK = new Object();
    private static final Object CLIENT_LOCK = new Object();

    private static volatile Context appContext;
    private static volatile boolean connectedReady;
    private static volatile int configuredPort;
    private static volatile String configuredSecret;
    private static volatile Socket socket;
    private static volatile BufferedWriter writer;
    private static volatile BufferedReader reader;
    private static final AtomicBoolean moveDrainScheduled = new AtomicBoolean(false);
    private static boolean hasPendingMove;
    private static int pendingMoveX;
    private static int pendingMoveY;
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

    static void configure(Context context, int port, String secret) {
        if (context == null || port <= 0 || port > 65535 || secret == null || secret.length() < 32) {
            return;
        }
        final Context app = context.getApplicationContext();
        appContext = app;
        configuredPort = port;
        configuredSecret = secret;
        WORKER.execute(() -> connectAndAuth(app, port, secret));
    }

    private static void connectAndAuth(Context app, int port, String secret) {
        synchronized (CLIENT_LOCK) {
            closeSocketLocked();
            try {
                Socket s = new Socket();
                s.connect(new InetSocketAddress("127.0.0.1", port), 1500);
                s.setTcpNoDelay(true);
                s.setSoTimeout(1500);
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
                    DeskTouchRuntime.accessibilityPermissionChanged(app);
                    return;
                }
                s.setSoTimeout(3000);
                socket = s;
                writer = w;
                reader = r;
                connectedReady = true;
                Log.i("DeskTouch", "Accessibility-free Shell Bridge virtual input ready on loopback:" + port);
            } catch (IOException ex) {
                connectedReady = false;
                Log.w("DeskTouch", "Shell Bridge loopback connect deferred: " + ex.getMessage());
            }
        }
        DeskTouchRuntime.accessibilityPermissionChanged(app);
    }

    static void beginRemote(Context context) {
        if (context == null || !ready(context)) return;
        Point size = PhoneScreenGeometry.read(context);
        final int w = Math.max(200, size.x);
        final int h = Math.max(200, size.y);
        final Context app = context.getApplicationContext();
        WORKER.execute(() -> {
            String resp = sendAndReadLine("BEGIN " + w + " " + h);
            if (!"OK".equals(resp)) {
                markDisconnected(app);
            }
        });
    }

    static boolean movePointer(int x, int y, Runnable ack) {
        if (!connectedReady) return false;
        synchronized (MOVE_LOCK) {
            pendingMoveX = x;
            pendingMoveY = y;
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
            int x, y;
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
                ack = pendingMoveAck;
                hasPendingMove = false;
                pendingMoveAck = null;
            }
            sendLine("MOVETO " + x + " " + y);
            if (ack != null) ack.run();
        }
    }

    static void mouseButton(int button, boolean pressed) {
        if (!connectedReady || button < 1 || button > 5) return;
        WORKER.execute(() -> sendLine("BTN " + button + " " + (pressed ? "1" : "0")));
    }

    static void wheel(int amount, boolean horizontal) {
        if (!connectedReady || amount == 0) return;
        WORKER.execute(() -> sendLine("WHEEL " + amount + " " + (horizontal ? "1" : "0")));
    }

    static void key(int vk, boolean pressed, Runnable accepted, Runnable rejected) {
        if (!connectedReady) {
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
        if (!connectedReady || value == null || value.isEmpty()) {
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
        synchronized (MOVE_LOCK) {
            hasPendingMove = false;
            pendingMoveAck = null;
        }
        if (!connectedReady) return;
        WORKER.execute(() -> sendAndReadLine("END"));
    }

    static void disconnect() {
        synchronized (MOVE_LOCK) {
            hasPendingMove = false;
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

    private static boolean sendLine(String line) {
        synchronized (CLIENT_LOCK) {
            BufferedWriter w = writer;
            if (w == null) return false;
            try {
                w.write(line);
                w.write('\n');
                w.flush();
                return true;
            } catch (IOException ex) {
                closeSocketLocked();
                connectedReady = false;
                DeskTouchRuntime.accessibilityPermissionChanged(appContext);
                return false;
            }
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
