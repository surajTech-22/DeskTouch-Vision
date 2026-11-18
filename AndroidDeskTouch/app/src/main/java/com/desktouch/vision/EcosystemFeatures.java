package com.desktouch.vision;

import android.app.PendingIntent;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.os.SystemClock;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.util.Base64;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Modular ecosystem helpers for shared clipboard, file drop, verification code detection,
 * live screen preview reassembly, customizable trackpad shortcuts, notifications, and profiles.
 */
final class EcosystemFeatures {
    private static final String PREFS = "desktouch_ecosystem";
    static final String KEY_CLIPBOARD_SYNC = "clipboard_sync";
    static final String KEY_NOTIF_PREVIEWS = "notif_previews";
    static final String KEY_OTP_HELPER = "otp_helper";
    static final String KEY_CUSTOM_SHORTCUTS = "custom_shortcuts";

    static final int MAX_FILE_BYTES = 25 * 1024 * 1024;
    static final int FILE_CHUNK_RAW_BYTES = 3400;

    private static final Pattern OTP_KEYWORD = Pattern.compile(
        "(?i)\\b(?:otp|one[-\\s]?time|verification|verify|passcode|security\\s*code|login\\s*code|auth\\s*code|pin)\\b");
    private static final Pattern OTP_DIGITS = Pattern.compile("(?<!\\d)(\\d{4,8})(?!\\d)");

    static final class NotificationItem {
        final String key;
        final String app;
        final String title;
        final String text;
        final long postTimeMs;
        final boolean canReply;
        final String otpCode;

        NotificationItem(String key, String app, String title, String text,
                long postTimeMs, boolean canReply, String otpCode) {
            this.key = key == null ? "" : key;
            this.app = app == null ? "" : app;
            this.title = title == null ? "" : title;
            this.text = text == null ? "" : text;
            this.postTimeMs = postTimeMs;
            this.canReply = canReply;
            this.otpCode = otpCode == null ? "" : otpCode;
        }
    }

    static final class ProfileSummary {
        final String activeProfile;
        final boolean clipboardSync;
        final boolean notifPreviews;
        final boolean otpHelper;
        final String shortcutPreset;
        final List<String> profiles;

        ProfileSummary(String activeProfile, boolean clipboardSync, boolean notifPreviews,
                boolean otpHelper, String shortcutPreset, List<String> profiles) {
            this.activeProfile = activeProfile;
            this.clipboardSync = clipboardSync;
            this.notifPreviews = notifPreviews;
            this.otpHelper = otpHelper;
            this.shortcutPreset = shortcutPreset;
            this.profiles = profiles;
        }
    }

    static final class ShortcutDef {
        final String id;
        final String label;
        final String action;

        ShortcutDef(String id, String label, String action) {
            this.id = id;
            this.label = label;
            this.action = action;
        }
    }

    static final List<ShortcutDef> ALL_SHORTCUTS = java.util.Arrays.asList(
        new ShortcutDef("copy", "Copy", "copy"),
        new ShortcutDef("paste", "Paste", "paste"),
        new ShortcutDef("undo", "Undo", "undo"),
        new ShortcutDef("save", "Save", "save"),
        new ShortcutDef("snip", "Capture", "snip"),
        new ShortcutDef("task-view", "Windows", "task-view"),
        new ShortcutDef("desktop", "Desktop", "desktop"),
        new ShortcutDef("switch-window", "Switch", "switch-window"),
        new ShortcutDef("play-pause", "Play/Pause", "play-pause"),
        new ShortcutDef("mute", "Mute", "mute"),
        new ShortcutDef("slide-prev", "Prev Slide", "slide-prev"),
        new ShortcutDef("slide-next", "Next Slide", "slide-next"),
        new ShortcutDef("lock", "Lock", "lock")
    );

    private EcosystemFeatures() {}

    static SharedPreferences prefs(Context ctx) {
        return ctx == null ? null : ctx.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    static boolean isClipboardSyncEnabled(Context ctx) {
        SharedPreferences sp = prefs(ctx);
        return sp == null || sp.getBoolean(KEY_CLIPBOARD_SYNC, true);
    }

    static void setClipboardSyncEnabled(Context ctx, boolean enabled) {
        SharedPreferences sp = prefs(ctx);
        if (sp != null) sp.edit().putBoolean(KEY_CLIPBOARD_SYNC, enabled).apply();
    }

    static boolean isNotifPreviewsEnabled(Context ctx) {
        SharedPreferences sp = prefs(ctx);
        return sp == null || sp.getBoolean(KEY_NOTIF_PREVIEWS, true);
    }

    static void setNotifPreviewsEnabled(Context ctx, boolean enabled) {
        SharedPreferences sp = prefs(ctx);
        if (sp != null) sp.edit().putBoolean(KEY_NOTIF_PREVIEWS, enabled).apply();
    }

    static boolean isOtpHelperEnabled(Context ctx) {
        SharedPreferences sp = prefs(ctx);
        return sp == null || sp.getBoolean(KEY_OTP_HELPER, true);
    }

    static void setOtpHelperEnabled(Context ctx, boolean enabled) {
        SharedPreferences sp = prefs(ctx);
        if (sp != null) sp.edit().putBoolean(KEY_OTP_HELPER, enabled).apply();
    }

    static List<ShortcutDef> shortcutsForPresetOrCustom(Context ctx, String preset) {
        String p = preset == null ? "" : preset.trim();
        List<String> ids;
        if ("Media".equalsIgnoreCase(p)) {
            ids = java.util.Arrays.asList("play-pause", "mute", "switch-window", "desktop", "copy", "paste");
        } else if ("Presentation".equalsIgnoreCase(p)) {
            ids = java.util.Arrays.asList("slide-prev", "slide-next", "play-pause", "desktop", "lock");
        } else if ("Accounting".equalsIgnoreCase(p)) {
            ids = java.util.Arrays.asList("copy", "paste", "undo", "save", "switch-window", "snip");
        } else {
            SharedPreferences sp = prefs(ctx);
            String saved = sp != null ? sp.getString(KEY_CUSTOM_SHORTCUTS, "") : "";
            if (saved != null && !saved.isEmpty()) {
                ids = new ArrayList<>();
                for (String token : saved.split(",")) {
                    String clean = token.trim();
                    if (!clean.isEmpty()) ids.add(clean);
                }
            } else {
                ids = java.util.Arrays.asList("copy", "paste", "undo", "snip", "task-view", "lock");
            }
        }
        ArrayList<ShortcutDef> out = new ArrayList<>();
        for (String id : ids) {
            for (ShortcutDef d : ALL_SHORTCUTS) {
                if (d.id.equals(id)) {
                    out.add(d);
                    break;
                }
            }
        }
        return out;
    }

    static void saveCustomShortcutIds(Context ctx, List<String> ids) {
        SharedPreferences sp = prefs(ctx);
        if (sp == null || ids == null) return;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < ids.size(); i++) {
            if (i > 0) sb.append(',');
            sb.append(ids.get(i));
        }
        sp.edit().putString(KEY_CUSTOM_SHORTCUTS, sb.toString()).apply();
    }

    static String extractVerificationCode(String title, String text) {
        String combined = ((title == null ? "" : title) + " " + (text == null ? "" : text)).trim();
        if (combined.length() < 4 || !OTP_KEYWORD.matcher(combined).find()) return "";
        Matcher m = OTP_DIGITS.matcher(combined);
        return m.find() ? m.group(1) : "";
    }

    static String serializeNotificationList(List<NotificationItem> items) {
        try {
            JSONArray arr = new JSONArray();
            int limit = Math.min(6, items.size());
            for (int i = 0; i < limit; i++) {
                NotificationItem n = items.get(i);
                JSONObject o = new JSONObject();
                o.put("Key", truncate(n.key, 180));
                o.put("App", truncate(n.app, 48));
                o.put("Title", truncate(n.title, 110));
                o.put("Text", truncate(n.text, 240));
                o.put("PostTimeMs", n.postTimeMs);
                o.put("CanReply", n.canReply);
                if (!n.otpCode.isEmpty()) o.put("OtpCode", truncate(n.otpCode, 12));
                arr.put(o);
            }
            return arr.toString();
        } catch (Exception ignored) {
            return "[]";
        }
    }

    static List<NotificationItem> parseNotificationList(String json) {
        if (json == null || json.isEmpty()) return Collections.emptyList();
        try {
            JSONArray arr = new JSONArray(json);
            ArrayList<NotificationItem> out = new ArrayList<>(arr.length());
            for (int i = 0; i < arr.length() && i < 10; i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o == null) continue;
                out.add(new NotificationItem(
                    o.optString("Key", ""),
                    o.optString("App", ""),
                    o.optString("Title", ""),
                    o.optString("Text", ""),
                    o.optLong("PostTimeMs", 0L),
                    o.optBoolean("CanReply", false),
                    o.optString("OtpCode", "")
                ));
            }
            return Collections.unmodifiableList(out);
        } catch (Exception ignored) {
            return Collections.emptyList();
        }
    }

    static ProfileSummary parseProfileState(String json) {
        if (json == null || json.isEmpty()) {
            return new ProfileSummary("Work", true, true, true, "Default",
                java.util.Arrays.asList("Work", "Presentation", "Away"));
        }
        try {
            JSONObject o = new JSONObject(json);
            JSONArray arr = o.optJSONArray("profiles");
            ArrayList<String> profs = new ArrayList<>();
            if (arr != null) {
                for (int i = 0; i < arr.length(); i++) {
                    String s = arr.optString(i, "").trim();
                    if (!s.isEmpty()) profs.add(s);
                }
            }
            if (profs.isEmpty()) {
                profs.add("Work");
                profs.add("Presentation");
                profs.add("Away");
            }
            return new ProfileSummary(
                o.optString("activeProfile", "Work"),
                o.optBoolean("clipboardSync", true),
                o.optBoolean("notifPreviews", true),
                o.optBoolean("otpHelper", true),
                o.optString("shortcutPreset", "Default"),
                Collections.unmodifiableList(profs));
        } catch (Exception ignored) {
            return new ProfileSummary("Work", true, true, true, "Default",
                java.util.Arrays.asList("Work", "Presentation", "Away"));
        }
    }

    static String computeShortHash(String kind, byte[] raw) {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            md.update((kind + "|").getBytes(StandardCharsets.UTF_8));
            md.update(raw);
            byte[] dig = md.digest();
            StringBuilder sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) {
                sb.append(String.format(Locale.ROOT, "%02X", dig[i] & 0xFF));
            }
            return sb.toString();
        } catch (Exception ignored) {
            return "";
        }
    }

    static final class ChunkAssembler {
        private static final class Slot {
            byte[][] parts;
            int received;
            String tag;
            int width;
            int height;
            long createdMs;
        }
        private final Map<Integer, Slot> pending = new HashMap<>();

        static final class Result {
            final byte[] data;
            final String tag;
            final int width;
            final int height;
            Result(byte[] data, String tag, int width, int height) {
                this.data = data;
                this.tag = tag;
                this.width = width;
                this.height = height;
            }
        }

        synchronized Result addChunk(int frameId, int index, int total, int width, int height,
                String b64Payload, String tag) {
            if (frameId <= 0 || total < 1 || total > 180 || index < 0 || index >= total ||
                b64Payload == null || b64Payload.isEmpty()) {
                return null;
            }
            byte[] part;
            try {
                part = java.util.Base64.getDecoder().decode(b64Payload);
            } catch (Exception ignored) {
                return null;
            }
            if (total == 1) {
                return new Result(part, tag == null ? "" : tag, width, height);
            }
            long now = SystemClock.uptimeMillis();
            pending.entrySet().removeIf(e -> now - e.getValue().createdMs > 12000L);
            Slot slot = pending.get(frameId);
            if (slot == null || slot.parts.length != total) {
                slot = new Slot();
                slot.parts = new byte[total][];
                slot.tag = tag == null ? "" : tag;
                slot.width = width;
                slot.height = height;
                slot.createdMs = now;
                pending.put(frameId, slot);
            }
            if (tag != null && !tag.isEmpty() && slot.tag.isEmpty()) slot.tag = tag;
            if (slot.parts[index] == null) slot.received++;
            slot.parts[index] = part;
            if (slot.received < total) return null;
            pending.remove(frameId);
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            for (byte[] p : slot.parts) {
                if (p != null) bos.write(p, 0, p.length);
            }
            return new Result(bos.toByteArray(), slot.tag, slot.width, slot.height);
        }
    }

    static String sanitizeFileName(String raw) {
        String name = raw == null ? "" : raw.trim();
        int slash = Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\'));
        if (slash >= 0) name = name.substring(slash + 1);
        name = name.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_");
        if (name.isEmpty() || ".".equals(name) || "..".equals(name)) name = "desktouch-file.bin";
        if (name.length() > 140) name = name.substring(name.length() - 140);
        return name;
    }

    static final class InboundFileWriter {
        final int transferId;
        final String fileName;
        final File tempFile;
        final FileOutputStream out;
        final int expectedBytes;
        int receivedBytes;

        InboundFileWriter(int transferId, String fileName, File tempFile, FileOutputStream out, int expectedBytes) {
            this.transferId = transferId;
            this.fileName = fileName;
            this.tempFile = tempFile;
            this.out = out;
            this.expectedBytes = expectedBytes;
        }
    }

    static String saveCompletedDownload(Context context, String sanitizedName, File tempFile) {
        if (context == null || tempFile == null || !tempFile.exists()) return null;
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                ContentValues cv = new ContentValues();
                cv.put(MediaStore.Downloads.DISPLAY_NAME, sanitizedName);
                cv.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/DeskTouch");
                cv.put(MediaStore.Downloads.IS_PENDING, 1);
                ContentResolver cr = context.getContentResolver();
                Uri uri = cr.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, cv);
                if (uri != null) {
                    try (InputStream in = new java.io.FileInputStream(tempFile);
                         OutputStream out = cr.openOutputStream(uri)) {
                        if (out != null) {
                            byte[] buf = new byte[8192];
                            int n;
                            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                        }
                    }
                    cv.clear();
                    cv.put(MediaStore.Downloads.IS_PENDING, 0);
                    cr.update(uri, cv, null, null);
                    tempFile.delete();
                    return "Downloads/DeskTouch/" + sanitizedName;
                }
            }
            File dir = new File(Environment.getExternalStoragePublicDirectory(
                Environment.DIRECTORY_DOWNLOADS), "DeskTouch");
            if (!dir.exists()) dir.mkdirs();
            File dst = new File(dir, sanitizedName);
            try (InputStream in = new java.io.FileInputStream(tempFile);
                 FileOutputStream out = new FileOutputStream(dst)) {
                byte[] buf = new byte[8192];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            }
            tempFile.delete();
            return dst.getAbsolutePath();
        } catch (Exception ignored) {
            return null;
        }
    }

    static String queryUriFileName(Context context, Uri uri) {
        if (context == null || uri == null) return "shared-file";
        try (Cursor c = context.getContentResolver().query(uri,
                new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                int idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (idx >= 0) {
                    String n = c.getString(idx);
                    if (n != null && !n.trim().isEmpty()) return n.trim();
                }
            }
        } catch (Exception ignored) {}
        String seg = uri.getLastPathSegment();
        return seg == null || seg.isEmpty() ? "shared-file" : sanitizeFileName(seg);
    }

    static long queryUriSize(Context context, Uri uri) {
        if (context == null || uri == null) return -1L;
        try (Cursor c = context.getContentResolver().query(uri,
                new String[]{OpenableColumns.SIZE}, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                int idx = c.getColumnIndex(OpenableColumns.SIZE);
                if (idx >= 0) return c.getLong(idx);
            }
        } catch (Exception ignored) {}
        return -1L;
    }

    private static String truncate(String s, int maxLen) {
        if (s == null) return "";
        String t = s.trim();
        return t.length() <= maxLen ? t : t.substring(0, maxLen);
    }
}
