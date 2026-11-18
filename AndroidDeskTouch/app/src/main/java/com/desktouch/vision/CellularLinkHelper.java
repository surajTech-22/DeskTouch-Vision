package com.desktouch.vision;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.util.Log;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

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
        final int udpPort;
        final String errorMessage;

        private ProbeResult(boolean reachable, String address, double connectMs,
                double rttMs, int udpPort, String errorMessage) {
            this.reachable = reachable;
            this.address = address;
            this.connectMs = connectMs;
            this.rttMs = rttMs;
            this.udpPort = udpPort;
            this.errorMessage = errorMessage;
        }

        static ProbeResult success(String address, double connectMs, double rttMs, int udpPort) {
            return new ProbeResult(true, address, connectMs, rttMs, udpPort, "");
        }

        static ProbeResult failure(String errorMessage) {
            return new ProbeResult(false, "", 0.0, 0.0, 0,
                errorMessage != null ? errorMessage : "unreachable");
        }
    }

    private final ConnectivityManager connectivityManager;
    private Network cellularNetwork;
    private ConnectivityManager.NetworkCallback activeCallback;

    CellularLinkHelper(Context context) {
        this.connectivityManager = (ConnectivityManager)
            context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
    }

    synchronized Network getActiveNetwork() {
        return cellularNetwork;
    }

    synchronized Network acquireCellularNetwork(long timeoutMs) {
        if (connectivityManager == null) return null;
        if (cellularNetwork != null) {
            NetworkCapabilities caps = connectivityManager.getNetworkCapabilities(cellularNetwork);
            if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
                && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                return cellularNetwork;
            }
            releaseCellularNetwork();
        }
        final CountDownLatch latch = new CountDownLatch(1);
        final Network[] found = new Network[1];
        ConnectivityManager.NetworkCallback cb = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network network) {
                synchronized (CellularLinkHelper.this) {
                    cellularNetwork = network;
                    found[0] = network;
                }
                latch.countDown();
            }

            @Override public void onLost(Network network) {
                synchronized (CellularLinkHelper.this) {
                    if (network != null && network.equals(cellularNetwork)) {
                        cellularNetwork = null;
                    }
                }
            }
        };
        try {
            NetworkRequest req = new NetworkRequest.Builder()
                .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
                .build();
            activeCallback = cb;
            connectivityManager.requestNetwork(req, cb);
        } catch (Throwable ex) {
            activeCallback = null;
            Log.w(TAG, "Cellular requestNetwork unavailable: " + ex.getMessage());
            return null;
        }

        boolean acquired = false;
        try {
            acquired = latch.await(Math.max(400L, timeoutMs), TimeUnit.MILLISECONDS);
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
        }
        if (!acquired || found[0] == null) {
            releaseCellularNetwork();
            return null;
        }
        return found[0];
    }

    synchronized void releaseCellularNetwork() {
        if (connectivityManager != null && activeCallback != null) {
            try {
                connectivityManager.unregisterNetworkCallback(activeCallback);
            } catch (Throwable ignored) {}
        }
        activeCallback = null;
        cellularNetwork = null;
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
                new Packet("auth-trusted-probe", 0, 0, 0, 0, token).write(out);
                Packet resp = Packet.read(in);
                double rttMs = (System.nanoTime() - tReq) / 1_000_000.0;
                if ("probe-ok".equals(resp.type)) {
                    int udpPort = 0;
                    if (resp.text != null && !resp.text.isEmpty()) {
                        try { udpPort = Integer.parseInt(resp.text.trim()); }
                        catch (NumberFormatException ignored) {}
                    }
                    return ProbeResult.success(host.trim(), connectMs, rttMs, udpPort);
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
        boolean wifiDegraded = wifiLossPermille >= 20.0 || wifiJitterMs >= 6.5 || wifiRttMs >= 25.0;
        if (!wifiDegraded) {
            return false;
        }
        double wifiScore = computePathScore(wifiRttMs, wifiJitterMs, wifiLossPermille);
        double cellScore = computePathScore(cellRttMs, cellJitterMs, cellLossPermille);
        return cellScore + 15.0 < wifiScore;
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
