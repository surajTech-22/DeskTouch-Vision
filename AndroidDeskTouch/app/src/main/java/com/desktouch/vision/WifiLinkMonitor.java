package com.desktouch.vision;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;

import java.net.InetAddress;
import java.util.Objects;

/**
 * Tracks active transport and wireless frequency band transitions (2.4 GHz,
 * 5 GHz, 6 GHz, or wired) plus local interface address changes so ping
 * baselines and transport selection adapt immediately when a network shifts.
 */
final class WifiLinkMonitor {
    static final String BAND_CABLE = "cable";
    static final String BAND_24_GHZ = "2.4GHz";
    static final String BAND_5_GHZ = "5GHz";
    static final String BAND_6_GHZ = "6GHz";
    static final String BAND_WIRELESS = "wireless";
    static final String BAND_NONE = "none";

    static final class LinkSnapshot {
        final String transport;
        final String band;
        final int frequencyMhz;
        final int linkSpeedMbps;
        final int rssiDbm;
        final int networkHash;
        final int epoch;

        LinkSnapshot(String transport, String band, int frequencyMhz,
                     int linkSpeedMbps, int rssiDbm, int networkHash, int epoch) {
            this.transport = transport;
            this.band = band;
            this.frequencyMhz = frequencyMhz;
            this.linkSpeedMbps = linkSpeedMbps;
            this.rssiDbm = rssiDbm;
            this.networkHash = networkHash;
            this.epoch = epoch;
        }

        boolean sameRadioPath(LinkSnapshot other) {
            if (other == null) return false;
            return Objects.equals(transport, other.transport)
                && Objects.equals(band, other.band)
                && networkHash == other.networkHash;
        }

        boolean significantlyDifferentForReporting(LinkSnapshot other) {
            if (!sameRadioPath(other)) return true;
            return Math.abs(linkSpeedMbps - other.linkSpeedMbps) >= 20
                || (frequencyMhz > 0 && other.frequencyMhz > 0
                    && Math.abs(frequencyMhz - other.frequencyMhz) >= 5);
        }

        String describePlainLabel() {
            if (BAND_CABLE.equals(band)) return "Data cable";
            if (BAND_24_GHZ.equals(band)) {
                return linkSpeedMbps > 0
                    ? "2.4 GHz wireless (" + linkSpeedMbps + " megabits/sec)"
                    : "2.4 GHz wireless";
            }
            if (BAND_5_GHZ.equals(band)) {
                return linkSpeedMbps > 0
                    ? "5 GHz wireless (" + linkSpeedMbps + " megabits/sec)"
                    : "5 GHz wireless";
            }
            if (BAND_6_GHZ.equals(band)) {
                return linkSpeedMbps > 0
                    ? "6 GHz wireless (" + linkSpeedMbps + " megabits/sec)"
                    : "6 GHz wireless";
            }
            if ("wifi".equals(transport) || BAND_WIRELESS.equals(band)) {
                return linkSpeedMbps > 0
                    ? "Wireless (" + linkSpeedMbps + " megabits/sec)"
                    : "Wireless";
            }
            if ("ethernet".equals(transport)) return "Wired network";
            if ("cellular".equals(transport)) return "Mobile data";
            return "Network offline";
        }
    }

    private WifiLinkMonitor() {}

    static String classifyBand(int frequencyMhz) {
        if (frequencyMhz >= 5925 && frequencyMhz <= 7125) return BAND_6_GHZ;
        if (frequencyMhz >= 4900 && frequencyMhz < 5925) return BAND_5_GHZ;
        if (frequencyMhz >= 2400 && frequencyMhz <= 2500) return BAND_24_GHZ;
        return BAND_WIRELESS;
    }

    static LinkSnapshot inspect(Context context, boolean cableActive, int epoch) {
        if (cableActive) {
            return new LinkSnapshot("cable", BAND_CABLE, 0, 480, 0, 1, epoch);
        }
        try {
            ConnectivityManager cm =
                (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm == null) {
                return new LinkSnapshot("none", BAND_NONE, 0, 0, -127, 0, epoch);
            }
            Network active = cm.getActiveNetwork();
            if (active == null) {
                return new LinkSnapshot("none", BAND_NONE, 0, 0, -127, 0, epoch);
            }
            NetworkCapabilities nc = cm.getNetworkCapabilities(active);
            if (nc == null) {
                return new LinkSnapshot("none", BAND_NONE, 0, 0, -127, 0, epoch);
            }
            int hash = computeNetworkHash(active, cm.getLinkProperties(active));
            if (nc.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) {
                return new LinkSnapshot("ethernet", BAND_CABLE, 0, 1000, 0, hash, epoch);
            }
            if (nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                WifiInfo wifi = extractWifiInfo(context, nc);
                int freq = wifi != null ? Math.max(0, wifi.getFrequency()) : 0;
                int speed = selectBestWifiSpeed(wifi);
                int rssi = wifi != null ? wifi.getRssi() : -127;
                String band = freq > 0 ? classifyBand(freq) : BAND_WIRELESS;
                return new LinkSnapshot("wifi", band, freq, speed, rssi, hash, epoch);
            }
            if (nc.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) {
                return new LinkSnapshot("cellular", BAND_NONE, 0, 0, -127, hash, epoch);
            }
            return new LinkSnapshot("other", BAND_WIRELESS, 0, 0, -127, hash, epoch);
        } catch (RuntimeException ignored) {
            return new LinkSnapshot("wireless", BAND_WIRELESS, 0, 0, -127, 0, epoch);
        }
    }

    @SuppressWarnings("deprecation")
    private static WifiInfo extractWifiInfo(Context context, NetworkCapabilities nc) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            Object transportInfo = nc.getTransportInfo();
            if (transportInfo instanceof WifiInfo) {
                return (WifiInfo) transportInfo;
            }
        }
        try {
            WifiManager wm = (WifiManager) context.getApplicationContext()
                .getSystemService(Context.WIFI_SERVICE);
            return wm != null ? wm.getConnectionInfo() : null;
        } catch (RuntimeException ignored) {
            return null;
        }
    }

    private static int selectBestWifiSpeed(WifiInfo wifi) {
        if (wifi == null) return 0;
        int link = wifi.getLinkSpeed();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            int tx = wifi.getTxLinkSpeedMbps();
            int rx = wifi.getRxLinkSpeedMbps();
            int best = Math.max(link, Math.max(tx, rx));
            return Math.max(0, best);
        }
        return Math.max(0, link);
    }

    private static int computeNetworkHash(Network net, LinkProperties lp) {
        int h = (int) (net.getNetworkHandle() ^ (net.getNetworkHandle() >>> 32));
        if (lp != null) {
            String iface = lp.getInterfaceName();
            if (iface != null) h = h * 31 + iface.hashCode();
            for (LinkAddress addr : lp.getLinkAddresses()) {
                InetAddress ip = addr.getAddress();
                if (ip != null && !ip.isLoopbackAddress() && !ip.isLinkLocalAddress()) {
                    h = h * 31 + ip.hashCode();
                }
            }
        }
        return h;
    }
}
