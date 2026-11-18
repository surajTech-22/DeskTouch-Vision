package com.desktouch.vision;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.util.Log;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;

/**
 * Manages on-demand access to cellular mobile data via Android's
 * {@link ConnectivityManager#requestNetwork} so DeskTouch can keep an active
 * cellular path bound alongside Wi-Fi, measure real reachability and latency,
 * and fail back cleanly when local Wi-Fi has packet loss or goes offline.
 */
final class CellularLinkHelper {
    private static final String TAG = "DeskTouch";
    static final long SWITCH_COOLDOWN_MS = 25_000L;

    static final class ProbeResult {
        final boolean reachable;
        final String address;
        final double connectMs;
        final double rttMs;
        final double jitterMs;
        final int udpPort;
        final String errorMessage;

        private ProbeResult(boolean reachable, String address, double connectMs,
                double rttMs, double jitterMs, int udpPort, String errorMessage) {
            this.reachable = reachable;
            this.address = address;
            this.connectMs = connectMs;
            this.rttMs = rttMs;
            this.jitterMs = jitterMs;
            this.udpPort = udpPort;
            this.errorMessage = errorMessage;
        }

        static ProbeResult success(String address, double connectMs, double rttMs,
                double jitterMs, int udpPort) {
            return new ProbeResult(true, address, connectMs, rttMs, jitterMs, udpPort, "");
        }

        static ProbeResult failure(String errorMessage) {
            return new ProbeResult(false, "", 0.0, 0.0, 0.0, 0,
                errorMessage != null ? errorMessage : "unreachable");
        }
    }

    private final ConnectivityManager connectivityManager;
    private final Object lock = new Object();
    private Network cellularNetwork;
    private ConnectivityManager.NetworkCallback activeCallback;

    CellularLinkHelper(Context context) {
        this.connectivityManager = (ConnectivityManager)
            context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
    }

    Network getActiveNetwork() {
        synchronized (lock) {
            return cellularNetwork;
        }
    }

    Network acquireCellularNetwork(long timeoutMs) {
        if (connectivityManager == null) return null;
        Network existing;
        synchronized (lock) {
            existing = cellularNetwork;
        }
        if (existing != null) {
            NetworkCapabilities caps = connectivityManager.getNetworkCapabilities(existing);
            if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
                && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                return existing;
            }
            releaseCellularNetwork();
        }
        final CountDownLatch latch = new CountDownLatch(1);
        final AtomicReference<Network> found = new AtomicReference<>();
        final AtomicReference<ConnectivityManager.NetworkCallback> cbRef = new AtomicReference<>();
        ConnectivityManager.NetworkCallback cb = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network network) {
                synchronized (lock) {
                    if (activeCallback != cbRef.get()) return;
                    cellularNetwork = network;
                }
                found.compareAndSet(null, network);
                latch.countDown();
            }

            @Override public void onLost(Network network) {
                synchronized (lock) {
                    if (activeCallback == cbRef.get()
                        && network != null && network.equals(cellularNetwork)) {
                        cellularNetwork = null;
                    }
                }
            }

            @Override public void onUnavailable() {
                synchronized (lock) {
                    if (activeCallback == cbRef.get()) {
                        cellularNetwork = null;
                    }
                }
                latch.countDown();
            }
        };
        cbRef.set(cb);
        try {
            NetworkRequest req = new NetworkRequest.Builder()
                .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
                .build();
            synchronized (lock) {
                if (activeCallback != null) {
                    try {
                        connectivityManager.unregisterNetworkCallback(activeCallback);
                    } catch (Throwable ignored) {}
                }
                activeCallback = cb;
                cellularNetwork = null;
            }
            connectivityManager.requestNetwork(req, cb);
        } catch (Throwable ex) {
            synchronized (lock) {
                if (activeCallback == cb) {
                    activeCallback = null;
                    cellularNetwork = null;
                }
            }
            Log.w(TAG, "Cellular requestNetwork unavailable: " + ex.getMessage());
            return null;
        }

        boolean acquired = false;
        try {
            // Never hold lock while awaiting ConnectivityManager callback (avoids self-deadlock).
            acquired = latch.await(Math.max(400L, timeoutMs), TimeUnit.MILLISECONDS);
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
        }
        Network result = found.get();
        if (!acquired || result == null) {
            Log.w(TAG, "Cellular request timed out or unavailable after "
                + Math.max(400L, timeoutMs) + " ms");
            releaseCallbackIfCurrent(cb);
            return null;
        }
        return result;
    }

    private void releaseCallbackIfCurrent(ConnectivityManager.NetworkCallback expected) {
        ConnectivityManager.NetworkCallback toUnregister = null;
        synchronized (lock) {
            if (activeCallback == expected) {
                toUnregister = activeCallback;
                activeCallback = null;
                cellularNetwork = null;
            }
        }
        if (connectivityManager != null && toUnregister != null) {
            try {
                connectivityManager.unregisterNetworkCallback(toUnregister);
            } catch (Throwable ignored) {}
        }
    }

    ConnectivityManager connectivityManager() {
        return connectivityManager;
    }

    void releaseCellularNetwork() {
        ConnectivityManager.NetworkCallback toUnregister;
        synchronized (lock) {
            toUnregister = activeCallback;
            activeCallback = null;
            cellularNetwork = null;
        }
        if (connectivityManager != null && toUnregister != null) {
            try {
                connectivityManager.unregisterNetworkCallback(toUnregister);
            } catch (Throwable ignored) {}
        }
    }

    static List<String> getGlobalCellularIpv6Addresses(ConnectivityManager cm, Network cellNet) {
        if (cm == null || cellNet == null) return Collections.emptyList();
        try {
            LinkProperties lp = cm.getLinkProperties(cellNet);
            if (lp == null) return Collections.emptyList();
            ArrayList<String> out = new ArrayList<>();
            for (LinkAddress la : lp.getLinkAddresses()) {
                InetAddress addr = la.getAddress();
                if (!(addr instanceof Inet6Address)) continue;
                if (addr.isLinkLocalAddress() || addr.isLoopbackAddress()
                    || addr.isSiteLocalAddress() || addr.isMulticastAddress()) {
                    continue;
                }
                byte[] b = addr.getAddress();
                if (b.length == 16 && (b[0] & 0xFE) == 0xFC) continue; // skip ULA fc00::/7
                String host = addr.getHostAddress();
                if (host != null) {
                    int pct = host.indexOf('%');
                    if (pct > 0) host = host.substring(0, pct);
                    if (!host.isEmpty() && !out.contains(host)) out.add(host);
                }
            }
            return out;
        } catch (Throwable ex) {
            return Collections.emptyList();
        }
    }

    static ProbeResult probeDirectEndpoints(Network boundNetwork, String fingerprint,
            TrustedWindows trust, List<String> endpoints, int timeoutMs) {
        if (fingerprint == null || trust == null
            || endpoints == null || endpoints.isEmpty()) {
            return ProbeResult.failure("Missing peer endpoint");
        }
        String token = trust.get(fingerprint);
        if (token == null || token.isEmpty()) {
            return ProbeResult.failure("PC must be saved as a Trusted device first");
        }
        SSLSocketFactory factory;
        try {
            factory = DeskTouchSession.createPinnedSslFactory(fingerprint);
        } catch (Exception ex) {
            return ProbeResult.failure("TLS setup error: " + ex.getMessage());
        }
        String lastErr = "Timed out reaching PC over network";
        for (String host : endpoints) {
            if (host == null || host.trim().isEmpty()) continue;
            Socket raw = null;
            SSLSocket ssl = null;
            try {
                InetAddress addr = InetAddress.getByName(host.trim());
                raw = new Socket();
                if (boundNetwork != null) {
                    boundNetwork.bindSocket(raw);
                }
                raw.setTcpNoDelay(true);
                long t0 = System.nanoTime();
                raw.connect(new InetSocketAddress(addr, 37377), timeoutMs);
                double connectMs = (System.nanoTime() - t0) / 1_000_000.0;
                ssl = (SSLSocket) factory.createSocket(raw, host.trim(), 37377, true);
                ssl.setSoTimeout(timeoutMs);
                ssl.setTcpNoDelay(true);
                ssl.startHandshake();
                DataOutputStream out = new DataOutputStream(ssl.getOutputStream());
                DataInputStream in = new DataInputStream(ssl.getInputStream());
                long tReq = System.nanoTime();
                // Request a = 2 tells v0.3.65+ Windows peers to echo up to 2 follow-up probe-echo packets.
                new Packet("auth-trusted-probe", 2, 0, 0, 0, token).write(out);
                Packet resp = Packet.read(in);
                double rtt0 = (System.nanoTime() - tReq) / 1_000_000.0;
                if ("probe-ok".equals(resp.type)) {
                    int udpPort = 0;
                    if (resp.text != null && !resp.text.isEmpty()) {
                        try { udpPort = Integer.parseInt(resp.text.trim()); }
                        catch (NumberFormatException ignored) {}
                    }
                    double[] samples = new double[] { rtt0, rtt0, rtt0 };
                    int sampleCount = 1;
                    try {
                        ssl.setSoTimeout(Math.min(1200, timeoutMs));
                        for (int s = 1; s < 3; s++) {
                            long ts = System.nanoTime();
                            new Packet("probe-echo", s, 0, 0, 0, "").write(out);
                            Packet echo = Packet.read(in);
                            if (echo == null || !"probe-ok".equals(echo.type)) break;
                            samples[sampleCount++] = (System.nanoTime() - ts) / 1_000_000.0;
                        }
                    } catch (Exception ignored) {
                        // Older v0.3.64 peers close cleanly after the first probe-ok.
                    }
                    double medianRtt;
                    double jitterMs;
                    if (sampleCount >= 3) {
                        double a = samples[0], b = samples[1], c = samples[2];
                        medianRtt = Math.max(Math.min(a, b), Math.min(Math.max(a, b), c));
                        double mean = (a + b + c) / 3.0;
                        jitterMs = (Math.abs(a - mean) + Math.abs(b - mean) + Math.abs(c - mean)) / 3.0;
                    } else if (sampleCount == 2) {
                        medianRtt = (samples[0] + samples[1]) * 0.5;
                        jitterMs = Math.abs(samples[0] - samples[1]) * 0.5;
                    } else {
                        medianRtt = rtt0;
                        jitterMs = Math.min(4.0, Math.abs(rtt0 - connectMs) * 0.5);
                    }
                    return ProbeResult.success(host.trim(), connectMs, medianRtt, jitterMs, udpPort);
                }
                lastErr = "Trusted probe rejected (" + resp.type + ")";
            } catch (Exception ex) {
                lastErr = ex.getClass().getSimpleName() + ": " + ex.getMessage();
            } finally {
                if (ssl != null) {
                    try { ssl.close(); } catch (Exception ignored) {}
                } else if (raw != null) {
                    try { raw.close(); } catch (Exception ignored) {}
                }
            }
        }
        return ProbeResult.failure(lastErr);
    }

    static double computePathScore(double rttMs, double jitterMs, double lossPermille) {
        double safeRtt = Math.max(1.0, rttMs);
        double safeJitter = Math.max(0.0, jitterMs);
        double safeLoss = Math.max(0.0, lossPermille);
        return safeRtt + safeJitter * 2.5 + safeLoss * 1.4;
    }

    static boolean shouldPreferCellular(double wifiRttMs, double wifiJitterMs, double wifiLossPermille,
            double cellRttMs, double cellJitterMs, double cellLossPermille,
            long nowMs, long lastSwitchMs) {
        if (lastSwitchMs > 0L && nowMs - lastSwitchMs < SWITCH_COOLDOWN_MS) {
            return false;
        }
        boolean wifiDegraded = wifiLossPermille >= 15.0 || wifiJitterMs >= 6.0 || wifiRttMs >= 22.0;
        if (!wifiDegraded) {
            return false;
        }
        double wifiScore = computePathScore(wifiRttMs, wifiJitterMs, wifiLossPermille);
        double cellScore = computePathScore(cellRttMs, cellJitterMs, cellLossPermille);
        return cellScore + 12.0 < wifiScore;
    }

    static boolean shouldReturnToWifi(double cellRttMs, double cellJitterMs, double cellLossPermille,
            double wifiRttMs, double wifiJitterMs, double wifiLossPermille,
            long nowMs, long lastSwitchMs) {
        if (lastSwitchMs > 0L && nowMs - lastSwitchMs < SWITCH_COOLDOWN_MS) {
            return false;
        }
        if (wifiLossPermille >= 10.0) {
            return false;
        }
        double cellScore = computePathScore(cellRttMs, cellJitterMs, cellLossPermille);
        double wifiScore = computePathScore(wifiRttMs, wifiJitterMs, wifiLossPermille);
        return wifiScore + 18.0 < cellScore;
    }
}
