package com.desktouch.vision;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Collections;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * Pure display-arrangement and hub-pairing helpers shared between the Home
 * Windows-format screen map, the Find Hubs panel, and unit tests.
 */
final class LinkedDisplayHelper {
    private static final Pattern DISPLAY_NUMBER_PATTERN =
        Pattern.compile("DISPLAY(\\d+)$", Pattern.CASE_INSENSITIVE);

    private LinkedDisplayHelper() {}

    static final class MonitorBox {
        final int x;
        final int y;
        final int width;
        final int height;
        final int displayNumber;
        final boolean primary;

        MonitorBox(int x, int y, int width, int height, int displayNumber, boolean primary) {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
            this.displayNumber = displayNumber;
            this.primary = primary;
        }
    }

    static final class DisplayTileModel {
        final int id;
        final String fullName;
        final String shortName;
        final String platform;
        final String displayLabel;
        final String resolutionSummary;
        final int primaryWidth;
        final int primaryHeight;
        final int monitorCount;
        final boolean portrait;
        final boolean hub;
        final boolean self;
        final boolean selected;
        final boolean selectable;
        final boolean supportsInput;
        final int notificationCount;
        final List<MonitorBox> monitors;
        final int batteryPercent;
        final boolean charging;

        DisplayTileModel(int id, String fullName, String shortName, String platform,
                String displayLabel, String resolutionSummary,
                int primaryWidth, int primaryHeight, int monitorCount,
                boolean portrait, boolean hub, boolean self, boolean selected,
                boolean selectable, boolean supportsInput, int notificationCount,
                List<MonitorBox> monitors) {
            this(id, fullName, shortName, platform, displayLabel, resolutionSummary,
                primaryWidth, primaryHeight, monitorCount, portrait, hub, self, selected,
                selectable, supportsInput, notificationCount, monitors, -1, false);
        }

        DisplayTileModel(int id, String fullName, String shortName, String platform,
                String displayLabel, String resolutionSummary,
                int primaryWidth, int primaryHeight, int monitorCount,
                boolean portrait, boolean hub, boolean self, boolean selected,
                boolean selectable, boolean supportsInput, int notificationCount,
                List<MonitorBox> monitors, int batteryPercent, boolean charging) {
            this.id = id;
            this.fullName = fullName;
            this.shortName = shortName;
            this.platform = platform;
            this.displayLabel = displayLabel;
            this.resolutionSummary = resolutionSummary;
            this.primaryWidth = primaryWidth;
            this.primaryHeight = primaryHeight;
            this.monitorCount = monitorCount;
            this.portrait = portrait;
            this.hub = hub;
            this.self = self;
            this.selected = selected;
            this.selectable = selectable;
            this.supportsInput = supportsInput;
            this.notificationCount = notificationCount;
            this.monitors = monitors;
            this.batteryPercent = batteryPercent;
            this.charging = charging;
        }
    }

    static String cleanShortName(String raw, String fallback) {
        String clean = raw == null ? "" : raw.replace('\r', ' ').replace('\n', ' ').trim();
        if (clean.isEmpty()) clean = fallback != null ? fallback : "Device";
        return clean.length() > 18 ? clean.substring(0, 17) + "…" : clean;
    }

    static String formatNotificationCount(int count) {
        return count >= 0 ? String.valueOf(count) : "—";
    }

    static int parseDisplayNumber(String monitorName, int fallback) {
        if (monitorName == null || monitorName.isEmpty()) return fallback;
        Matcher m = DISPLAY_NUMBER_PATTERN.matcher(monitorName.trim());
        if (m.find()) {
            try {
                int num = Integer.parseInt(m.group(1));
                if (num > 0 && num <= 99) return num;
            } catch (NumberFormatException ignored) {}
        }
        return fallback;
    }

    static List<MonitorBox> parseMonitors(String layoutJson) {
        if (layoutJson == null || layoutJson.trim().isEmpty()) {
            return Collections.emptyList();
        }
        try {
            JSONArray arr = new JSONArray(layoutJson);
            ArrayList<MonitorBox> boxes = new ArrayList<>(arr.length());
            for (int i = 0; i < arr.length(); i++) {
                JSONObject obj = arr.optJSONObject(i);
                if (obj == null) continue;
                int w = obj.optInt("Width", 0);
                int h = obj.optInt("Height", 0);
                if (w <= 0 || h <= 0) continue;
                int x = obj.optInt("X", 0);
                int y = obj.optInt("Y", 0);
                boolean primary = obj.optBoolean("Primary", i == 0);
                int num = parseDisplayNumber(obj.optString("Name", ""), i + 1);
                boxes.add(new MonitorBox(x, y, w, h, num, primary));
            }
            return Collections.unmodifiableList(boxes);
        } catch (Exception ignored) {
            return Collections.emptyList();
        }
    }

    static DisplayTileModel parseTileModel(DeskTouchSession.EnvironmentTarget target,
            int selectedId, int ownId) {
        if (target == null) {
            return new DisplayTileModel(
                -1, "Windows PC", "Windows PC", "Windows",
                "Display 1", "Offline", 1920, 1080, 1,
                false, false, false, false, false, false, -1,
                Collections.emptyList());
        }
        boolean isAndroid = "Android".equalsIgnoreCase(target.platform);
        boolean isSelf = ownId >= 0 ? target.id == ownId : isAndroid;
        boolean isHub = target.id == 0 && !isAndroid;
        boolean selected = target.id == selectedId;
        boolean selectable = !isSelf && target.supportsInput;

        List<MonitorBox> monitors = parseMonitors(target.layout);
        MonitorBox primary = null;
        for (MonitorBox m : monitors) {
            if (m.primary) { primary = m; break; }
        }
        if (primary == null && !monitors.isEmpty()) primary = monitors.get(0);

        int pw = primary != null ? primary.width : (isAndroid ? 1080 : 1920);
        int ph = primary != null ? primary.height : (isAndroid ? 2400 : 1080);
        boolean portrait = ph > pw && (isAndroid || monitors.size() <= 1);
        int count = Math.max(1, monitors.size());
        int primaryNum = primary != null ? primary.displayNumber : 1;

        String displayLabel = isAndroid
            ? "Android · 1"
            : (count > 1 ? count + " Displays" : "Display " + primaryNum);
        String resSummary = (pw > 0 && ph > 0)
            ? (count > 1 ? count + " screens • " + pw + " × " + ph : pw + " × " + ph)
            : (isSelf ? "This phone" : target.platform);

        String fullName = target.name != null && !target.name.trim().isEmpty()
            ? target.name.trim()
            : (isAndroid ? "Android" : "Windows PC");
        return new DisplayTileModel(
            target.id, fullName, cleanShortName(fullName, fullName),
            target.platform != null ? target.platform : "Windows",
            displayLabel, resSummary, pw, ph, count,
            portrait, isHub, isSelf, selected, selectable, target.supportsInput,
            target.notificationCount, monitors, target.batteryPercent, target.charging);
    }

    static List<DeskTouchSession.EnvironmentTarget> sortTargetsSpatially(
            List<DeskTouchSession.EnvironmentTarget> targets) {
        if (targets == null || targets.isEmpty()) return Collections.emptyList();
        ArrayList<DeskTouchSession.EnvironmentTarget> copy = new ArrayList<>(targets);
        copy.sort((a, b) -> {
            boolean hasOrdA = a.arrangementOrder >= 0;
            boolean hasOrdB = b.arrangementOrder >= 0;
            if (hasOrdA && hasOrdB && a.arrangementOrder != b.arrangementOrder) {
                return Integer.compare(a.arrangementOrder, b.arrangementOrder);
            }
            if (hasOrdA != hasOrdB) return hasOrdA ? -1 : 1;

            boolean hasGeoA = a.layoutW > 0.001 && a.layoutH > 0.001;
            boolean hasGeoB = b.layoutW > 0.001 && b.layoutH > 0.001;
            if (hasGeoA && hasGeoB) {
                double ay = a.layoutY + (a.layoutH * 0.5);
                double by = b.layoutY + (b.layoutH * 0.5);
                if (Math.abs(ay - by) > 0.22) return Double.compare(ay, by);
                int xc = Double.compare(a.layoutX, b.layoutX);
                if (xc != 0) return xc;
            }
            return Integer.compare(a.id, b.id);
        });
        return copy;
    }

    static List<List<DeskTouchSession.EnvironmentTarget>> groupTargetsIntoRows(
            List<DeskTouchSession.EnvironmentTarget> sorted) {
        if (sorted == null || sorted.isEmpty()) return Collections.emptyList();
        boolean allHaveBounds = true;
        for (DeskTouchSession.EnvironmentTarget t : sorted) {
            if (t.layoutW <= 0.001 || t.layoutH <= 0.001) {
                allHaveBounds = false;
                break;
            }
        }
        if (!allHaveBounds || sorted.size() <= 1) {
            return Collections.singletonList(sorted);
        }

        ArrayList<DeskTouchSession.EnvironmentTarget> byRow = new ArrayList<>(sorted);
        byRow.sort(Comparator.comparingDouble((DeskTouchSession.EnvironmentTarget t) -> t.layoutY + t.layoutH * 0.5)
            .thenComparingDouble(t -> t.layoutX));

        ArrayList<List<DeskTouchSession.EnvironmentTarget>> rows = new ArrayList<>();
        ArrayList<DeskTouchSession.EnvironmentTarget> currentRow = new ArrayList<>();
        double rowMinY = 0;
        double rowMaxY = 0;
        for (DeskTouchSession.EnvironmentTarget t : byRow) {
            double top = t.layoutY;
            double bottom = t.layoutY + t.layoutH;
            if (currentRow.isEmpty()) {
                currentRow.add(t);
                rowMinY = top;
                rowMaxY = bottom;
            } else {
                double overlap = Math.min(rowMaxY, bottom) - Math.max(rowMinY, top);
                double minH = Math.max(0.01, Math.min(rowMaxY - rowMinY, t.layoutH));
                if (overlap > minH * 0.30) {
                    currentRow.add(t);
                    rowMinY = Math.min(rowMinY, top);
                    rowMaxY = Math.max(rowMaxY, bottom);
                } else {
                    currentRow.sort(Comparator.comparingDouble(x -> x.layoutX));
                    rows.add(currentRow);
                    currentRow = new ArrayList<>();
                    currentRow.add(t);
                    rowMinY = top;
                    rowMaxY = bottom;
                }
            }
        }
        if (!currentRow.isEmpty()) {
            currentRow.sort(Comparator.comparingDouble(x -> x.layoutX));
            rows.add(currentRow);
        }
        return rows;
    }

    static List<DisplayTileModel> buildTrackpadPcTiles(
            List<DeskTouchSession.EnvironmentTarget> targets, int selectedId, int ownId) {
        List<DeskTouchSession.EnvironmentTarget> ordered = sortTargetsSpatially(targets);
        ArrayList<DisplayTileModel> pcs = new ArrayList<>();
        for (DeskTouchSession.EnvironmentTarget t : ordered) {
            DisplayTileModel model = parseTileModel(t, selectedId, ownId);
            if (!model.self && model.selectable && !"Android".equalsIgnoreCase(model.platform)) {
                pcs.add(model);
            }
        }
        if (pcs.isEmpty()) {
            for (DeskTouchSession.EnvironmentTarget t : ordered) {
                DisplayTileModel model = parseTileModel(t, selectedId, ownId);
                if (!model.self && model.selectable) pcs.add(model);
            }
        }
        return pcs;
    }

    static String normalizePairingCode(String raw) {
        if (raw == null) return "";
        String cleaned = raw.replaceAll("[\\s:\\-.]+", "").trim().toUpperCase(Locale.ROOT);
        if (cleaned.length() < 6 || cleaned.length() > 64) return "";
        for (int i = 0; i < cleaned.length(); i++) {
            char c = cleaned.charAt(i);
            boolean hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!hex) return "";
        }
        return cleaned;
    }

    static String normalizeCustomPairingCode(String raw) {
        if (raw == null) return "";
        String clean = raw.trim();
        if (clean.length() < 4 || clean.length() > 32) return "";
        for (int i = 0; i < clean.length(); i++) {
            char c = clean.charAt(i);
            boolean ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z') || c == '-' || c == '_';
            if (!ok) return "";
        }
        return clean.toUpperCase(Locale.ROOT);
    }

    static String computePairingTag(String fingerprint, String rawCode) {
        if (fingerprint == null) return "";
        String fp = fingerprint.trim().toUpperCase(Locale.ROOT);
        String code = normalizeCustomPairingCode(rawCode);
        if (fp.length() != 64 || code.isEmpty()) return "";
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] digest = md.digest(("DT1-PAIRTAG-V1|" + fp + "|" + code).getBytes(StandardCharsets.UTF_8));
            StringBuilder hex = new StringBuilder(24);
            for (int i = 0; i < 12; i++) {
                hex.append(String.format(Locale.ROOT, "%02X", digest[i] & 0xFF));
            }
            return hex.toString();
        } catch (Exception e) {
            return "";
        }
    }

    static DesktopDiscovery.Device findDeviceByPairingCode(
            List<DesktopDiscovery.Device> devices, String rawCode) {
        if (devices == null || devices.isEmpty()) return null;
        String customCode = normalizeCustomPairingCode(rawCode);
        if (!customCode.isEmpty()) {
            for (DesktopDiscovery.Device d : devices) {
                if (d == null || d.android || d.fingerprint == null || d.pairingTag == null) continue;
                String expectedTag = computePairingTag(d.fingerprint, customCode);
                if (!expectedTag.isEmpty() && expectedTag.equalsIgnoreCase(d.pairingTag)) {
                    return d;
                }
            }
        }
        String code = normalizePairingCode(rawCode);
        if (code.isEmpty()) return null;
        DesktopDiscovery.Device uniquePrefixMatch = null;
        int prefixCount = 0;
        for (DesktopDiscovery.Device d : devices) {
            if (d == null || d.android || d.fingerprint == null) continue;
            String fp = d.fingerprint.toUpperCase(Locale.ROOT);
            if (fp.equals(code)) return d;
            if (fp.startsWith(code)) {
                uniquePrefixMatch = d;
                prefixCount++;
            }
        }
        return prefixCount == 1 ? uniquePrefixMatch : null;
    }

    static DesktopDiscovery.Device resolveGatewayForDevice(
            List<DesktopDiscovery.Device> devices, DesktopDiscovery.Device target,
            Set<String> connectedFingerprints, Set<String> approvedFingerprints) {
        if (target == null || devices == null || devices.isEmpty()) return target;
        LinkedHashMap<String, List<DesktopDiscovery.Device>> groups =
            DesktopDiscovery.windowsGroups(devices);
        for (List<DesktopDiscovery.Device> members : groups.values()) {
            for (DesktopDiscovery.Device m : members) {
                if (m.fingerprint.equals(target.fingerprint)) {
                    DesktopDiscovery.Device gw = DesktopDiscovery.chooseGateway(
                        members,
                        connectedFingerprints != null ? connectedFingerprints : Collections.emptySet(),
                        approvedFingerprints != null ? approvedFingerprints : Collections.emptySet());
                    if (gw != null) return gw;
                }
            }
        }
        return target;
    }

    private static final String WAN_RECORD_PREFIX = "DTR1|";
    private static final String WAN_RELAY_BASE_URL = "https://ntfy.sh";
    private static final byte[] WAN_AAD = "DTR1".getBytes(StandardCharsets.UTF_8);

    static String computeWanTopic(String lookupKey) {
        if (lookupKey == null) return "";
        String clean = lookupKey.trim().toUpperCase(Locale.ROOT);
        if (clean.length() < 4) return "";
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] digest = md.digest(("DT1-WAN-TOPIC-V1|" + clean).getBytes(StandardCharsets.UTF_8));
            StringBuilder hex = new StringBuilder(24);
            for (int i = 0; i < 12; i++) {
                hex.append(String.format(Locale.ROOT, "%02x", digest[i] & 0xFF));
            }
            return "dtv-wan-" + hex.toString();
        } catch (Exception e) {
            return "";
        }
    }

    static byte[] deriveWanAesKey(String lookupKey) {
        if (lookupKey == null || lookupKey.trim().length() < 4) {
            throw new IllegalArgumentException("Lookup key must be at least 4 characters");
        }
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            return md.digest(("DT1-WAN-KEY-V1|" + lookupKey.trim().toUpperCase(Locale.ROOT))
                .getBytes(StandardCharsets.UTF_8));
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    static List<String> parseCandidateAddresses(String rawAddressList) {
        if (rawAddressList == null || rawAddressList.trim().isEmpty()) {
            return Collections.emptyList();
        }
        LinkedHashSet<String> valid = new LinkedHashSet<>();
        String[] tokens = rawAddressList.split("[,;\\s]+");
        for (String tok : tokens) {
            if (tok == null) continue;
            String t = tok.trim();
            if (t.startsWith("[") && t.endsWith("]") && t.length() > 2) {
                t = t.substring(1, t.length() - 1);
            }
            int zone = t.indexOf('%');
            if (zone > 0) t = t.substring(0, zone);
            if (t.isEmpty()) continue;
            boolean ipv4 = t.matches("^((25[0-5]|2[0-4]\\d|[01]?\\d\\d?)\\.){3}(25[0-5]|2[0-4]\\d|[01]?\\d\\d?)$");
            boolean ipv6 = !ipv4 && t.contains(":") && t.matches("^[0-9a-fA-F:]+$");
            if (!ipv4 && !ipv6) continue;
            if ("127.0.0.1".equals(t) || "0.0.0.0".equals(t) || "::1".equals(t) || "::".equals(t)) continue;
            String lower = t.toLowerCase(Locale.ROOT);
            if (lower.startsWith("fe80:") || lower.startsWith("ff0")) continue;
            valid.add(t);
            if (valid.size() >= 6) break;
        }
        return new ArrayList<>(valid);
    }

    static String sealWanRecord(
            String name, String fingerprint, String addresses, String pairingTag,
            boolean isHub, int memberCount, long timestampUnixSeconds, String lookupKey) {
        try {
            byte[] key = deriveWanAesKey(lookupKey);
            JSONObject obj = new JSONObject()
                .put("name", name != null ? name : "DeskTouch PC")
                .put("fp", fingerprint.trim().toUpperCase(Locale.ROOT))
                .put("addrs", addresses)
                .put("tag", pairingTag != null ? pairingTag : "")
                .put("hub", isHub)
                .put("members", Math.max(1, memberCount))
                .put("ts", timestampUnixSeconds);
            byte[] plain = obj.toString().getBytes(StandardCharsets.UTF_8);
            byte[] nonce = new byte[12];
            new SecureRandom().nextBytes(nonce);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(key, "AES"), new GCMParameterSpec(128, nonce));
            cipher.updateAAD(WAN_AAD);
            byte[] sealed = cipher.doFinal(plain);
            byte[] combined = new byte[12 + sealed.length];
            System.arraycopy(nonce, 0, combined, 0, 12);
            System.arraycopy(sealed, 0, combined, 12, sealed.length);
            return WAN_RECORD_PREFIX + Base64.getEncoder().encodeToString(combined);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    static DesktopDiscovery.Device tryOpenWanRecord(String envelope, String lookupKey) {
        if (envelope == null || lookupKey == null) return null;
        String trimmed = envelope.trim();
        if (!trimmed.startsWith(WAN_RECORD_PREFIX)) return null;
        try {
            byte[] combined = Base64.getDecoder().decode(trimmed.substring(WAN_RECORD_PREFIX.length()));
            if (combined.length < 30 || combined.length > 4096) return null;
            byte[] key = deriveWanAesKey(lookupKey);
            byte[] nonce = new byte[12];
            System.arraycopy(combined, 0, nonce, 0, 12);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(key, "AES"), new GCMParameterSpec(128, nonce));
            cipher.updateAAD(WAN_AAD);
            byte[] plain = cipher.doFinal(combined, 12, combined.length - 12);
            JSONObject root = new JSONObject(new String(plain, StandardCharsets.UTF_8));
            String name = root.optString("name", "DeskTouch PC").trim();
            String fp = root.optString("fp", "").trim().toUpperCase(Locale.ROOT);
            String addrs = root.optString("addrs", "").trim();
            String tag = root.optString("tag", "").trim().toUpperCase(Locale.ROOT);
            boolean hub = root.optBoolean("hub", true);
            int members = Math.max(1, root.optInt("members", 1));
            long ts = root.optLong("ts", 0L);

            if (!fp.matches("[A-F0-9]{64}")) return null;
            List<String> validAddrs = parseCandidateAddresses(addrs);
            if (validAddrs.isEmpty()) return null;
            long nowUnix = System.currentTimeMillis() / 1000L;
            if (ts > 0L && Math.abs(nowUnix - ts) > 86400L) return null;

            String joinedAddrs = String.join(",", validAddrs);
            DesktopDiscovery.Device dev = new DesktopDiscovery.Device(
                name.isEmpty() ? "DeskTouch PC" : name,
                joinedAddrs,
                fp,
                true,
                false,
                false);
            dev.isHub = hub;
            dev.hubMemberCount = members;
            if (!tag.isEmpty()) dev.pairingTag = tag;
            return dev;
        } catch (Exception e) {
            return null;
        }
    }

    static DesktopDiscovery.Device resolveByCode(String rawCode, int timeoutMs) {
        String code = normalizeCustomPairingCode(rawCode);
        if (code.length() < 4) return null;
        return resolveWanCore(code, timeoutMs);
    }

    static DesktopDiscovery.Device resolveByFingerprint(String fingerprint, int timeoutMs) {
        if (fingerprint == null) return null;
        String fp = fingerprint.trim().toUpperCase(Locale.ROOT);
        if (!fp.matches("[A-F0-9]{64}")) return null;
        return resolveWanCore(fp, timeoutMs);
    }

    private static DesktopDiscovery.Device resolveWanCore(String lookupKey, int timeoutMs) {
        String topic = computeWanTopic(lookupKey);
        if (topic.isEmpty()) return null;
        HttpURLConnection conn = null;
        try {
            URL url = new URL(WAN_RELAY_BASE_URL + "/" + topic + "/json?poll=1&since=45m");
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("GET");
            int t = Math.max(1500, Math.min(timeoutMs, 8000));
            conn.setConnectTimeout(t);
            conn.setReadTimeout(t);
            if (conn.getResponseCode() != 200) return null;

            DesktopDiscovery.Device newest = null;
            long newestTime = Long.MIN_VALUE;
            try (BufferedReader br = new BufferedReader(
                    new InputStreamReader(conn.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = br.readLine()) != null) {
                    line = line.trim();
                    if (line.isEmpty()) continue;
                    try {
                        JSONObject event = new JSONObject(line);
                        String msg = event.optString("message", "");
                        long evTime = event.optLong("time", 0L);
                        DesktopDiscovery.Device opened = tryOpenWanRecord(msg, lookupKey);
                        if (opened != null && evTime >= newestTime) {
                            newest = opened;
                            newestTime = evTime;
                        }
                    } catch (Exception ignored) {}
                }
            }
            return newest;
        } catch (Exception e) {
            return null;
        } finally {
            if (conn != null) try { conn.disconnect(); } catch (Exception ignored) {}
        }
    }
}
