package com.desktouch.vision;

import android.content.ContentResolver;
import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Build;
import android.provider.Settings;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import org.json.JSONObject;

/**
 * Detects Android USB/Wireless Debugging readiness, resolves this device's own
 * `_adb-tls-connect._tcp` / `_adb-tls-pairing._tcp` ports via {@link NsdManager},
 * exchanges bridge hints over the verified TLS connection, and surfaces clear
 * actionable diagnostics when Virtual Bridge is waiting for setup or pairing.
 */
final class VirtualBridgeHelper {
    private static final String SERVICE_TLS_CONNECT = "_adb-tls-connect._tcp.";
    private static final String SERVICE_TLS_PAIRING = "_adb-tls-pairing._tcp.";
    private static final long MIN_PROBE_INTERVAL_MS = 4500L;

    static final class DebugState {
        final boolean usbDebugEnabled;
        final boolean wifiDebugEnabled;
        final boolean devOptionsEnabled;

        DebugState(boolean usbDebugEnabled, boolean wifiDebugEnabled, boolean devOptionsEnabled) {
            this.usbDebugEnabled = usbDebugEnabled;
            this.wifiDebugEnabled = wifiDebugEnabled;
            this.devOptionsEnabled = devOptionsEnabled;
        }
    }

    static final class RemoteStatus {
        final String state;
        final String detail;
        final int pairPortHint;
        final int connectPortHint;
        final long updatedAtMs;

        RemoteStatus(String state, String detail, int pairPortHint, int connectPortHint, long updatedAtMs) {
            this.state = state != null ? state : "";
            this.detail = detail != null ? detail : "";
            this.pairPortHint = pairPortHint;
            this.connectPortHint = connectPortHint;
            this.updatedAtMs = updatedAtMs;
        }
    }

    private static final ExecutorService EXECUTOR = Executors.newSingleThreadExecutor(r -> {
        Thread t = new Thread(r, "DeskTouch bridge-probe");
        t.setDaemon(true);
        return t;
    });
    private static final AtomicBoolean PROBING = new AtomicBoolean(false);
    private static volatile long lastProbeStartedMs;
    private static volatile int discoveredConnectPort;
    private static volatile int discoveredPairPort;
    private static volatile RemoteStatus latestRemoteStatus =
        new RemoteStatus("debug_off", "", 0, 0, 0L);

    private VirtualBridgeHelper() {}

    static DebugState inspectSystemDebugState(Context context) {
        if (context == null) return new DebugState(false, false, false);
        ContentResolver cr = context.getContentResolver();
        if (cr == null) return new DebugState(false, false, false);
        boolean usb = readGlobalBool(cr, Settings.Global.ADB_ENABLED);
        boolean dev = readGlobalBool(cr, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED);
        boolean wifi = Build.VERSION.SDK_INT >= 30 && readGlobalBool(cr, "adb_wifi_enabled");
        return new DebugState(usb, wifi, dev);
    }

    private static boolean readGlobalBool(ContentResolver cr, String key) {
        try {
            return Settings.Global.getInt(cr, key, 0) != 0;
        } catch (Throwable ignored) {
            return false;
        }
    }

    static RemoteStatus latestStatus() {
        return latestRemoteStatus;
    }

    static int bestConnectPortHint() {
        int remote = latestRemoteStatus.connectPortHint;
        return remote > 0 ? remote : discoveredConnectPort;
    }

    static int bestPairPortHint() {
        int remote = latestRemoteStatus.pairPortHint;
        return remote > 0 ? remote : discoveredPairPort;
    }

    static void updateRemoteStatus(int pairPortHint, int connectPortHint, String wireText) {
        String state = "debug_off";
        String detail = "";
        if (wireText != null && !wireText.isEmpty()) {
            int bar = wireText.indexOf('|');
            if (bar >= 0) {
                state = wireText.substring(0, bar).trim();
                detail = wireText.substring(bar + 1).trim();
            } else {
                state = wireText.trim();
            }
        }
        latestRemoteStatus = new RemoteStatus(
            state,
            detail,
            pairPortHint > 0 && pairPortHint <= 65535 ? pairPortHint : 0,
            connectPortHint > 0 && connectPortHint <= 65535 ? connectPortHint : 0,
            System.currentTimeMillis());
    }

    static void clearRemoteStatus() {
        latestRemoteStatus = new RemoteStatus("debug_off", "", 0, 0, 0L);
    }

    static String encodeHintJson(
        boolean usbDebug, boolean wifiDebug, int connectPort, int pairPort, boolean allowed, boolean ready
    ) {
        try {
            JSONObject obj = new JSONObject();
            obj.put("usb", usbDebug);
            obj.put("wifi", wifiDebug);
            obj.put("connectPort", Math.max(0, connectPort));
            obj.put("pairPort", Math.max(0, pairPort));
            obj.put("allowed", allowed);
            obj.put("ready", ready);
            return obj.toString();
        } catch (Exception ex) {
            return "{}";
        }
    }

    static void probeAndSync(Context context, DeskTouchSession session, boolean force) {
        if (context == null || session == null || !session.isConnected()) return;
        final Context app = context.getApplicationContext();
        long now = android.os.SystemClock.uptimeMillis();
        if (!force && now - lastProbeStartedMs < MIN_PROBE_INTERVAL_MS) return;
        if (!PROBING.compareAndSet(false, true)) return;
        lastProbeStartedMs = now;

        EXECUTOR.execute(() -> {
            try {
                if (!session.isConnected()) return;
                DebugState debug = inspectSystemDebugState(app);
                boolean allowed = ShellInputClient.allowed(app);
                boolean ready = ShellInputClient.ready(app);

                if (!ready && allowed) {
                    ShellInputClient.warmConnectIfConfigured(app);
                }

                int connectPort = 0;
                int pairPort = 0;
                if (Build.VERSION.SDK_INT >= 30 && debug.wifiDebugEnabled && allowed && !ready) {
                    int[] resolved = discoverLocalWirelessAdbPorts(app, force ? 2500L : 1700L);
                    connectPort = resolved[0];
                    pairPort = resolved[1];
                    if (connectPort > 0) discoveredConnectPort = connectPort;
                    if (pairPort > 0) discoveredPairPort = pairPort;
                } else if (!debug.wifiDebugEnabled) {
                    discoveredConnectPort = 0;
                    discoveredPairPort = 0;
                }

                if (connectPort == 0 && debug.wifiDebugEnabled) connectPort = discoveredConnectPort;
                if (pairPort == 0 && debug.wifiDebugEnabled) pairPort = discoveredPairPort;

                String hintJson = encodeHintJson(
                    debug.usbDebugEnabled, debug.wifiDebugEnabled, connectPort, pairPort, allowed, ready);
                session.sendAdbBridgeHint(hintJson);
                if (allowed && !ready && (force || debug.usbDebugEnabled || debug.wifiDebugEnabled)) {
                    session.requestShellBridgeSync();
                }
            } finally {
                PROBING.set(false);
            }
        });
    }

    private static int[] discoverLocalWirelessAdbPorts(Context app, long timeoutMs) {
        NsdManager nsd = (NsdManager) app.getSystemService(Context.NSD_SERVICE);
        if (nsd == null) return new int[]{0, 0};
        Set<String> localIps = collectLocalIpv4Strings();
        if (localIps.isEmpty()) return new int[]{0, 0};

        AtomicInteger connectPortRef = new AtomicInteger(0);
        AtomicInteger pairPortRef = new AtomicInteger(0);
        CountDownLatch connectDone = new CountDownLatch(1);

        NsdManager.DiscoveryListener connectListener = createNsdPortDiscoveryListener(
            nsd, localIps, connectPortRef, connectDone);
        NsdManager.DiscoveryListener pairListener = createNsdPortDiscoveryListener(
            nsd, localIps, pairPortRef, null);

        boolean connectStarted = false;
        boolean pairStarted = false;
        try {
            nsd.discoverServices(SERVICE_TLS_CONNECT, NsdManager.PROTOCOL_DNS_SD, connectListener);
            connectStarted = true;
        } catch (Throwable ignored) {}
        try {
            nsd.discoverServices(SERVICE_TLS_PAIRING, NsdManager.PROTOCOL_DNS_SD, pairListener);
            pairStarted = true;
        } catch (Throwable ignored) {}

        try {
            connectDone.await(Math.max(500L, timeoutMs), TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } finally {
            if (connectStarted) {
                try { nsd.stopServiceDiscovery(connectListener); } catch (Throwable ignored) {}
            }
            if (pairStarted) {
                try { nsd.stopServiceDiscovery(pairListener); } catch (Throwable ignored) {}
            }
        }
        return new int[]{connectPortRef.get(), pairPortRef.get()};
    }

    private static NsdManager.DiscoveryListener createNsdPortDiscoveryListener(
        NsdManager nsd,
        Set<String> localIps,
        AtomicInteger portTarget,
        CountDownLatch firstMatchLatch
    ) {
        return new NsdManager.DiscoveryListener() {
            @Override public void onStartDiscoveryFailed(String serviceType, int errorCode) {}
            @Override public void onStopDiscoveryFailed(String serviceType, int errorCode) {}
            @Override public void onDiscoveryStarted(String serviceType) {}
            @Override public void onDiscoveryStopped(String serviceType) {}
            @Override public void onServiceLost(NsdServiceInfo serviceInfo) {}
            @Override public void onServiceFound(NsdServiceInfo serviceInfo) {
                if (serviceInfo == null) return;
                try {
                    nsd.resolveService(serviceInfo, new NsdManager.ResolveListener() {
                        @Override public void onResolveFailed(NsdServiceInfo info, int errorCode) {}
                        @Override public void onServiceResolved(NsdServiceInfo resolved) {
                            if (resolved == null) return;
                            InetAddress host = resolved.getHost();
                            int port = resolved.getPort();
                            if (host == null || port <= 0 || port > 65535) return;
                            String addr = host.getHostAddress();
                            if (addr != null && (localIps.contains(addr) || host.isLoopbackAddress())) {
                                portTarget.set(port);
                                if (firstMatchLatch != null) firstMatchLatch.countDown();
                            }
                        }
                    });
                } catch (Throwable ignored) {}
            }
        };
    }

    private static Set<String> collectLocalIpv4Strings() {
        Set<String> ips = new HashSet<>();
        try {
            Enumeration<NetworkInterface> ifaces = NetworkInterface.getNetworkInterfaces();
            while (ifaces != null && ifaces.hasMoreElements()) {
                NetworkInterface nif = ifaces.nextElement();
                if (!nif.isUp() || nif.isLoopback()) continue;
                Enumeration<InetAddress> addrs = nif.getInetAddresses();
                while (addrs.hasMoreElements()) {
                    InetAddress addr = addrs.nextElement();
                    if (addr instanceof Inet4Address && !addr.isLoopbackAddress()) {
                        String hostAddr = addr.getHostAddress();
                        if (hostAddr != null && !hostAddr.isEmpty()) ips.add(hostAddr);
                    }
                }
            }
        } catch (Throwable ignored) {}
        return ips;
    }
}
