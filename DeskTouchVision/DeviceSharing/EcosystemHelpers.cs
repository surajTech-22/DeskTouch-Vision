using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DeskTouchVision;

namespace DeskTouch.DeviceShare;

internal sealed record DeviceBatteryState(
    int Percent,
    bool IsCharging,
    string SignalLabel = "");

internal sealed record NotificationPreviewItem(
    string Key,
    string App,
    string Title,
    string Text,
    long PostTimeMs,
    bool CanReply,
    string? OtpCode = null);

internal sealed record OtpOfferPayload(
    string App,
    string Code,
    string Title);

internal sealed record ScreenProfilePreset(
    string Name,
    string Edge,
    double Sensitivity,
    bool ClipboardSync,
    bool NotificationPreviews,
    string ShortcutPreset,
    bool ExpandMediaBar);

internal sealed record AppAutomationRule(
    string ProcessPattern,
    string ShortcutPreset,
    bool ExpandMediaBar,
    string? PreferredTarget = null);

internal sealed class ProfilesAndRulesConfig
{
    public string ActiveProfile { get; set; } = "Work";
    public bool ClipboardSyncEnabled { get; set; } = true;
    public bool NotificationPreviewsEnabled { get; set; } = true;
    public bool OtpHelperEnabled { get; set; } = true;
    public bool LowBatteryAlertEnabled { get; set; } = true;
    public string ActiveShortcutPreset { get; set; } = "Default";
    public List<ScreenProfilePreset> Profiles { get; set; } = new();
    public List<AppAutomationRule> Rules { get; set; } = new();
}

internal static class SharedClipboardHelper
{
    internal const int MaxTextChars = 2800;
    internal const int MaxImageBytes = 380 * 1024;
    private static readonly object Gate = new();
    private static string _lastCapturedHash = "";
    private static string _lastAppliedHash = "";
    private static long _suppressUntilTicks;

    internal static string ComputeHash(string kind, byte[] raw)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(kind + "|");
        byte[] all = new byte[prefix.Length + raw.Length];
        Buffer.BlockCopy(prefix, 0, all, 0, prefix.Length);
        Buffer.BlockCopy(raw, 0, all, prefix.Length, raw.Length);
        return Convert.ToHexString(SHA256.HashData(all))[..16];
    }

    internal static (bool Changed, string Kind, string Hash, string PayloadText, byte[]? ImagePng) TryCaptureOnStaThread()
    {
        try
        {
            if (Environment.TickCount64 < Volatile.Read(ref _suppressUntilTicks))
                return default;
            if (Clipboard.ContainsText())
            {
                string txt = Clipboard.GetText() ?? "";
                if (string.IsNullOrEmpty(txt)) return default;
                if (txt.Length > MaxTextChars)
                    txt = txt[..MaxTextChars];
                byte[] utf8 = Encoding.UTF8.GetBytes(txt);
                string hash = ComputeHash("text", utf8);
                lock (Gate)
                {
                    if (hash == _lastAppliedHash || hash == _lastCapturedHash)
                        return default;
                    _lastCapturedHash = hash;
                }
                return (true, "text", hash, txt, null);
            }
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img == null) return default;
                using var scaled = ScaleBitmapIfLarge(img, 960);
                using var ms = new MemoryStream();
                scaled.Save(ms, ImageFormat.Png);
                byte[] png = ms.ToArray();
                if (png.Length > MaxImageBytes)
                    return default;
                string hash = ComputeHash("image", png);
                lock (Gate)
                {
                    if (hash == _lastAppliedHash || hash == _lastCapturedHash)
                        return default;
                    _lastCapturedHash = hash;
                }
                return (true, "image", hash, "", png);
            }
        }
        catch { }
        return default;
    }

    internal static void SuppressForMs(int ms = 1200) =>
        Volatile.Write(ref _suppressUntilTicks, Environment.TickCount64 + Math.Max(200, ms));

    internal static bool TryApplyTextOnStaThread(string text, string? hash = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            string h = string.IsNullOrWhiteSpace(hash) ? ComputeHash("text", utf8) : hash!;
            lock (Gate)
            {
                if (h == _lastCapturedHash && h == _lastAppliedHash)
                    return false;
                _lastAppliedHash = h;
                _lastCapturedHash = h;
            }
            SuppressForMs(1400);
            Clipboard.SetText(text);
            return true;
        }
        catch { return false; }
    }

    internal static bool TryApplyImageOnStaThread(byte[] pngBytes, string? hash = null)
    {
        if (pngBytes == null || pngBytes.Length == 0 || pngBytes.Length > MaxImageBytes)
            return false;
        try
        {
            string h = string.IsNullOrWhiteSpace(hash) ? ComputeHash("image", pngBytes) : hash!;
            lock (Gate)
            {
                _lastAppliedHash = h;
                _lastCapturedHash = h;
            }
            SuppressForMs(1400);
            using var ms = new MemoryStream(pngBytes);
            using var bmp = new Bitmap(ms);
            Clipboard.SetImage((Image)bmp.Clone());
            return true;
        }
        catch { return false; }
    }

    private static Bitmap ScaleBitmapIfLarge(Image src, int maxDim)
    {
        int w = src.Width, h = src.Height;
        if (w <= maxDim && h <= maxDim)
            return new Bitmap(src);
        double ratio = Math.Min((double)maxDim / Math.Max(1, w), (double)maxDim / Math.Max(1, h));
        int nw = Math.Max(1, (int)Math.Round(w * ratio));
        int nh = Math.Max(1, (int)Math.Round(h * ratio));
        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, nw, nh);
        return dst;
    }

    internal static string SerializeClipboardTextPacket(string hash, string text) =>
        JsonSerializer.Serialize(new { h = hash, t = text });

    internal static (string Hash, string Text)? ParseClipboardTextPacket(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string h = doc.RootElement.TryGetProperty("h", out var hEl) ? (hEl.GetString() ?? "") : "";
            string t = doc.RootElement.TryGetProperty("t", out var tEl) ? (tEl.GetString() ?? "") : "";
            return string.IsNullOrEmpty(t) ? null : (h, t);
        }
        catch { return null; }
    }
}

internal sealed class FileDropHelper
{
    internal const int MaxFileSizeBytes = 25 * 1024 * 1024;
    internal const int ChunkRawBytes = 3600;
    private static int _nextTransferId = 100;

    internal sealed class InboundTransfer
    {
        internal int TransferId;
        internal int SourcePeerId;
        internal string FileName = "";
        internal string TargetPath = "";
        internal int ExpectedBytes;
        internal int ReceivedBytes;
        internal long LastActivityTicks;
        internal FileStream? Stream;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, InboundTransfer> _inbound = new(StringComparer.Ordinal);
    private readonly BufferedChunkAssembler _clipboardImgAssembler = new();

    internal static int NextTransferId()
    {
        int next = Interlocked.Increment(ref _nextTransferId);
        return next > 0 ? next : Interlocked.Exchange(ref _nextTransferId, 100);
    }

    internal static string GetReceiveFolder()
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (!Directory.Exists(downloads))
            downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string dir = Path.Combine(downloads, "DeskTouch Received");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string SanitizeFileName(string? rawName)
    {
        string name = Path.GetFileName((rawName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return "received-file.bin";
        foreach (char bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        if (name.Length > 140)
            name = name[^140..];
        return name;
    }

    internal static string EnsureUniqueTargetPath(string fileName)
    {
        string safe = SanitizeFileName(fileName);
        string dir = GetReceiveFolder();
        string stem = Path.GetFileNameWithoutExtension(safe);
        string ext = Path.GetExtension(safe);
        string candidate = Path.Combine(dir, safe);
        int idx = 1;
        while (File.Exists(candidate) && idx < 500)
        {
            candidate = Path.Combine(dir, $"{stem} ({idx++}){ext}");
        }
        return candidate;
    }

    internal bool BeginInbound(int sourcePeerId, int transferId, int expectedBytes, string? fileName, out string? error)
    {
        error = null;
        if (transferId <= 0 || expectedBytes <= 0 || expectedBytes > MaxFileSizeBytes)
        {
            error = "File exceeds 25 MB safety cap or is empty.";
            return false;
        }
        string target = EnsureUniqueTargetPath(fileName ?? "file");
        string key = $"{sourcePeerId}:{transferId}";
        lock (_gate)
        {
            PruneStaleLocked();
            if (_inbound.Count >= 6)
            {
                error = "Too many simultaneous file transfers.";
                return false;
            }
            if (_inbound.ContainsKey(key))
            {
                error = "Duplicate file transfer.";
                return false;
            }
            var stream = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 8192);
            _inbound[key] = new InboundTransfer
            {
                TransferId = transferId,
                SourcePeerId = sourcePeerId,
                FileName = Path.GetFileName(target),
                TargetPath = target,
                ExpectedBytes = expectedBytes,
                ReceivedBytes = 0,
                LastActivityTicks = Environment.TickCount64,
                Stream = stream
            };
        }
        return true;
    }

    internal bool AppendInboundChunk(int sourcePeerId, int transferId, string? b64Data)
    {
        if (string.IsNullOrEmpty(b64Data)) return false;
        byte[] chunk;
        try { chunk = Convert.FromBase64String(b64Data); }
        catch { return false; }
        string key = $"{sourcePeerId}:{transferId}";
        lock (_gate)
        {
            if (!_inbound.TryGetValue(key, out var tx) || tx.Stream == null)
                return false;
            if (tx.ReceivedBytes + chunk.Length > tx.ExpectedBytes + 1024)
            {
                AbortLocked(key, tx);
                return false;
            }
            tx.Stream.Write(chunk, 0, chunk.Length);
            tx.ReceivedBytes += chunk.Length;
            tx.LastActivityTicks = Environment.TickCount64;
            return true;
        }
    }

    internal string? CompleteInbound(int sourcePeerId, int transferId)
    {
        string key = $"{sourcePeerId}:{transferId}";
        lock (_gate)
        {
            if (!_inbound.Remove(key, out var tx))
                return null;
            try
            {
                tx.Stream?.Flush();
                tx.Stream?.Dispose();
                tx.Stream = null;
                string part = tx.TargetPath + ".part";
                if (!File.Exists(part)) return null;
                File.Move(part, tx.TargetPath, overwrite: true);
                return tx.TargetPath;
            }
            catch
            {
                AbortLocked(key, tx);
                return null;
            }
        }
    }

    internal void CancelAllForPeer(int sourcePeerId)
    {
        lock (_gate)
        {
            foreach (var key in _inbound.Keys.Where(k => k.StartsWith(sourcePeerId + ":", StringComparison.Ordinal)).ToArray())
            {
                if (_inbound.Remove(key, out var tx))
                    AbortLocked(key, tx);
            }
        }
    }

    private void PruneStaleLocked()
    {
        long now = Environment.TickCount64;
        foreach (var kv in _inbound.ToArray())
        {
            if (now - kv.Value.LastActivityTicks > 45000)
            {
                _inbound.Remove(kv.Key);
                AbortLocked(kv.Key, kv.Value);
            }
        }
    }

    private static void AbortLocked(string _, InboundTransfer tx)
    {
        try { tx.Stream?.Dispose(); } catch { }
        try { if (File.Exists(tx.TargetPath + ".part")) File.Delete(tx.TargetPath + ".part"); } catch { }
    }

    internal static IReadOnlyList<Packet> BuildImageClipboardChunks(string hash, byte[] png)
    {
        int id = NextTransferId();
        var list = new List<Packet>();
        int totalChunks = (png.Length + ChunkRawBytes - 1) / ChunkRawBytes;
        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * ChunkRawBytes;
            int len = Math.Min(ChunkRawBytes, png.Length - offset);
            string payload = (i == 0 ? hash + "|" : "") + Convert.ToBase64String(png, offset, len);
            list.Add(new Packet("clipboard-img-chunk", id, i, totalChunks, 0, payload));
        }
        return list;
    }

    internal (string Hash, byte[] Png)? AcceptClipboardImgChunk(int id, int index, int total, string? text)
    {
        if (id <= 0 || total is < 1 or > 150 || index < 0 || index >= total || string.IsNullOrEmpty(text))
            return null;
        string hash = "";
        string b64 = text;
        if (index == 0)
        {
            int sep = text.IndexOf('|');
            if (sep is >= 4 and <= 24)
            {
                hash = text[..sep];
                b64 = text[(sep + 1)..];
            }
        }
        if (total == 1)
        {
            try { return (hash, Convert.FromBase64String(b64)); }
            catch { return null; }
        }

        var assembled = _clipboardImgAssembler.AddChunk(id, index, total, b64, hash);
        return assembled.Complete ? (assembled.Tag, assembled.Data) : null;
    }

    internal sealed class BufferedChunkAssembler
    {
        private readonly Dictionary<int, (byte[][] Parts, int Received, string Tag, long CreatedTicks)> _pending = new();

        internal (bool Complete, string Tag, byte[] Data) AddChunk(int frameId, int index, int total, string? payload, string tag = "")
        {
            if (frameId <= 0 || total is < 1 or > 200 || index < 0 || index >= total || string.IsNullOrEmpty(payload))
                return default;
            byte[] part;
            try { part = Convert.FromBase64String(payload); }
            catch { return default; }
            lock (_pending)
            {
                long now = Environment.TickCount64;
                foreach (int old in _pending.Where(kv => now - kv.Value.CreatedTicks > 15000).Select(kv => kv.Key).ToArray())
                    _pending.Remove(old);
                if (!_pending.TryGetValue(frameId, out var slot) || slot.Parts.Length != total)
                    slot = (new byte[total][], 0, tag, now);
                if (!string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(slot.Tag))
                    slot.Tag = tag;
                if (slot.Parts[index] == null)
                    slot.Received++;
                slot.Parts[index] = part;
                _pending[frameId] = slot;
                if (slot.Received < total)
                    return default;
                _pending.Remove(frameId);
                using var ms = new MemoryStream();
                foreach (var p in slot.Parts)
                    ms.Write(p, 0, p.Length);
                return (true, slot.Tag, ms.ToArray());
            }
        }
    }
}

internal static class ScreenPreviewHelper
{
    private static int _nextFrameId = 1;
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static byte[] EncodeJpeg(Bitmap dstBmp, long quality)
    {
        using var ms = new MemoryStream();
        if (JpegCodec != null)
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            dstBmp.Save(ms, JpegCodec, ep);
        }
        else
        {
            dstBmp.Save(ms, ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    internal static (int FrameId, int PackedDims, byte[] JpegBytes) CaptureMonitorPreviewRaw(
        int monitorIndex = 0, bool highRes = false)
    {
        try
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0) return default;
            int idx = monitorIndex >= 0 && monitorIndex < screens.Length ? monitorIndex : 0;
            Rectangle bounds = screens[idx].Bounds;
            if (bounds.Width < 16 || bounds.Height < 16) return default;

            int targetMaxWidth = highRes ? 1040 : 540;
            long initialQuality = highRes ? 60L : 54L;
            int softUdpTargetBytes = highRes ? 52 * 1024 : 26 * 1024;
            int secondPassThreshold = highRes ? 96 * 1024 : 56 * 1024;
            int hardMaxBytes = highRes ? 200 * 1024 : 120 * 1024;
            int dstW = Math.Min(targetMaxWidth, bounds.Width);
            int dstH = Math.Max(40, (int)Math.Round(bounds.Height * (dstW / (double)bounds.Width)));

            using var srcBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var gSrc = Graphics.FromImage(srcBmp))
            {
                gSrc.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            using var dstBmp = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
            using (var gDst = Graphics.FromImage(dstBmp))
            {
                gDst.InterpolationMode = highRes ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
                gDst.DrawImage(srcBmp, 0, 0, dstW, dstH);
            }

            byte[] jpeg = EncodeJpeg(dstBmp, initialQuality);
            if (jpeg.Length > softUdpTargetBytes)
            {
                jpeg = EncodeJpeg(dstBmp, highRes ? 44L : 38L);
                if (jpeg.Length > secondPassThreshold)
                    jpeg = EncodeJpeg(dstBmp, 32L);
            }
            if (jpeg.Length == 0 || jpeg.Length > hardMaxBytes)
                return default;
            int frameId = Interlocked.Increment(ref _nextFrameId);
            int packedDims = (dstW << 16) | (dstH & 0xFFFF);
            return (frameId, packedDims, jpeg);
        }
        catch
        {
            return default;
        }
    }

    internal static (int FrameId, int PackedDims, IReadOnlyList<Packet> Chunks) CaptureMonitorPreview(
        int monitorIndex = 0, bool highRes = false)
    {
        var raw = CaptureMonitorPreviewRaw(monitorIndex, highRes);
        if (raw.JpegBytes == null || raw.JpegBytes.Length == 0)
            return default;
        const int chunkSize = 3600;
        int total = (raw.JpegBytes.Length + chunkSize - 1) / chunkSize;
        var packets = new List<Packet>(total);
        for (int i = 0; i < total; i++)
        {
            int offset = i * chunkSize;
            int len = Math.Min(chunkSize, raw.JpegBytes.Length - offset);
            string b64 = Convert.ToBase64String(raw.JpegBytes, offset, len);
            packets.Add(new Packet("screen-preview-chunk", raw.FrameId, i, total, raw.PackedDims, b64));
        }
        return (raw.FrameId, raw.PackedDims, packets);
    }
}

internal static class SystemMediaPowerHelper
{
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_MONITORPOWER = new(0xF170);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static readonly object BrightnessGate = new();
    private static int _cachedBrightness = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    internal static (string ProcessName, string WindowTitle) GetForegroundAppInfo()
    {
        try
        {
            IntPtr hwnd = Win.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("", "");
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            string proc = "";
            if (pid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    proc = p.ProcessName;
                }
                catch { }
            }
            var sb = new StringBuilder(160);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            return (proc, sb.ToString().Trim());
        }
        catch { return ("", ""); }
    }

    internal static void PressVirtualKey(ushort vk, bool extended = false)
    {
        uint ext = extended ? Win.KEYEVENTF_EXTENDEDKEY : 0u;
        _ = Win.SendInput(2, new[]
        {
            new Win.INPUT
            {
                Type = Win.INPUT_KEYBOARD,
                Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = vk, Flags = ext } }
            },
            new Win.INPUT
            {
                Type = Win.INPUT_KEYBOARD,
                Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = vk, Flags = ext | Win.KEYEVENTF_KEYUP } }
            }
        }, Marshal.SizeOf<Win.INPUT>());
    }

    internal static void PressCombo(ushort modifierVk, ushort keyVk, ushort modifier2Vk = 0)
    {
        var list = new List<Win.INPUT>
        {
            new() { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = modifierVk } } }
        };
        if (modifier2Vk != 0)
            list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = modifier2Vk } } });
        list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = keyVk } } });
        list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = keyVk, Flags = Win.KEYEVENTF_KEYUP } } });
        if (modifier2Vk != 0)
            list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = modifier2Vk, Flags = Win.KEYEVENTF_KEYUP } } });
        list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = modifierVk, Flags = Win.KEYEVENTF_KEYUP } } });
        var arr = list.ToArray();
        _ = Win.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win.INPUT>());
    }

    internal static bool IsValidMediaAction(string? action) => action is
        "play-pause" or "next" or "prev" or "mute" or
        "vol-up" or "vol-down" or "brightness-up" or "brightness-down" or
        "slide-next" or "slide-prev" or "copy" or "paste" or "undo" or
        "snip" or "task-view" or "desktop" or "switch-window" or "save" or "alt-f4";

    internal static bool IsValidPowerAction(string? action) => action is
        "lock" or "lock-all" or "screen-off" or "wake";

    internal static bool ExecuteMediaAction(string? action, int value = 0)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        switch (action)
        {
            case "play-pause": PressVirtualKey(0xB3, extended: true); return true;
            case "next": PressVirtualKey(0xB0, extended: true); return true;
            case "prev": PressVirtualKey(0xB1, extended: true); return true;
            case "mute": PressVirtualKey(0xAD, extended: true); return true;
            case "vol-up":
                for (int i = 0; i < Math.Clamp(value <= 0 ? 1 : value, 1, 5); i++)
                    PressVirtualKey(0xAF, extended: true);
                return true;
            case "vol-down":
                for (int i = 0; i < Math.Clamp(value <= 0 ? 1 : value, 1, 5); i++)
                    PressVirtualKey(0xAE, extended: true);
                return true;
            case "slide-next": PressVirtualKey(0x22, extended: true); return true; // PageDown
            case "slide-prev": PressVirtualKey(0x21, extended: true); return true; // PageUp
            case "copy": PressCombo(0xA2, 0x43); return true; // Ctrl+C
            case "paste": PressCombo(0xA2, 0x56); return true; // Ctrl+V
            case "undo": PressCombo(0xA2, 0x5A); return true; // Ctrl+Z
            case "save": PressCombo(0xA2, 0x53); return true; // Ctrl+S
            case "snip": PressCombo(0x5B, 0x53, 0xA0); return true; // Win+Shift+S
            case "task-view": PressCombo(0x5B, 0x09); return true; // Win+Tab
            case "desktop": PressCombo(0x5B, 0x44); return true; // Win+D
            case "switch-window": PressCombo(0xA4, 0x09); return true; // Alt+Tab
            case "brightness-up":
            case "brightness-down":
                int delta = action == "brightness-up" ? 12 : -12;
                AdjustDisplayBrightnessAsync(delta);
                return true;
            default:
                return false;
        }
    }

    internal static bool ExecutePowerAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        switch (action)
        {
            case "lock":
            case "lock-all":
                return LockWorkStation();
            case "screen-off":
                _ = Task.Run(() =>
                {
                    Thread.Sleep(150);
                    _ = SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SYSCOMMAND,
                        SC_MONITORPOWER, new IntPtr(2), 0x0002, 1200, out _);
                });
                return true;
            case "wake":
                _ = SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
                _ = SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SYSCOMMAND,
                    SC_MONITORPOWER, new IntPtr(-1), 0x0002, 800, out _);
                _ = Win.SendInput(1, new[]
                {
                    new Win.INPUT
                    {
                        Type = Win.INPUT_MOUSE,
                        Data = new Win.INPUTUNION { Mouse = new Win.MOUSEINPUT { Flags = Win.MOUSEEVENTF_MOVE } }
                    }
                }, Marshal.SizeOf<Win.INPUT>());
                return true;
            default:
                return false;
        }
    }

    private static void AdjustDisplayBrightnessAsync(int delta)
    {
        _ = Task.Run(() =>
        {
            try
            {
                int target;
                lock (BrightnessGate)
                {
                    int cur = _cachedBrightness >= 0 ? _cachedBrightness : 70;
                    target = Math.Clamp(cur + delta, 10, 100);
                    _cachedBrightness = target;
                }
                string script =
                    $"$cur=(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty CurrentBrightness);" +
                    $"if($null -ne $cur){{ $n=[Math]::Max(10,[Math]::Min(100,[int]$cur+({delta}))); " +
                    $"(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods) | Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=1;Brightness=[byte]$n}} | Out-Null }}";
                using var proc = Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(2500);
            }
            catch { }
        });
    }

    internal static string[] CollectLocalMacAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
                .Select(n => BitConverter.ToString(n.GetPhysicalAddress().GetAddressBytes()))
                .Where(s => s.Length >= 17)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    internal static int SendWakeOnLan(IEnumerable<string> macStrings)
    {
        int sent = 0;
        foreach (string raw in macStrings)
        {
            string hex = Regex.Replace(raw ?? "", "[^0-9A-Fa-f]", "");
            if (hex.Length != 12) continue;
            byte[] mac = Convert.FromHexString(hex);
            byte[] magic = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) magic[i] = 0xFF;
            for (int i = 1; i <= 16; i++)
                Buffer.BlockCopy(mac, 0, magic, i * 6, 6);
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Broadcast, 7));
                sent++;
            }
            catch { }
        }
        return sent;
    }
}

internal static class DeviceTelemetryAndNotifModel
{
    private static readonly Regex OtpKeywordPattern = new(
        @"(?i)\b(?:otp|one[-\s]?time|verification|verify|passcode|security\s*code|login\s*code|auth\s*code|pin)\b",
        RegexOptions.Compiled);
    private static readonly Regex CodeDigitsPattern = new(
        @"(?<!\d)(\d{4,8})(?!\d)",
        RegexOptions.Compiled);

    internal static string? ExtractVerificationCode(string? title, string? text)
    {
        string combined = ((title ?? "") + " " + (text ?? "")).Trim();
        if (combined.Length < 4 || !OtpKeywordPattern.IsMatch(combined))
            return null;
        var match = CodeDigitsPattern.Match(combined);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string SerializeNotificationList(IReadOnlyList<NotificationPreviewItem> items)
    {
        var trimmed = items
            .Take(6)
            .Select(n => new NotificationPreviewItem(
                Truncate(n.Key, 180),
                Truncate(n.App, 48),
                Truncate(n.Title, 110),
                Truncate(n.Text, 240),
                n.PostTimeMs,
                n.CanReply,
                string.IsNullOrWhiteSpace(n.OtpCode) ? null : Truncate(n.OtpCode, 12)))
            .ToArray();
        return JsonSerializer.Serialize(trimmed);
    }

    internal static NotificationPreviewItem[] ParseNotificationList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<NotificationPreviewItem>();
        try
        {
            return JsonSerializer.Deserialize<NotificationPreviewItem[]>(json) ?? Array.Empty<NotificationPreviewItem>();
        }
        catch { return Array.Empty<NotificationPreviewItem>(); }
    }

    private static string Truncate(string? value, int maxLen)
    {
        string s = (value ?? "").Trim();
        return s.Length <= maxLen ? s : s[..maxLen];
    }
}

internal static class AutomationAndProfilesStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    internal static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "profiles-and-rules.json");

    internal static ProfilesAndRulesConfig CreateDefault() => new()
    {
        ActiveProfile = "Work",
        ClipboardSyncEnabled = true,
        NotificationPreviewsEnabled = true,
        OtpHelperEnabled = true,
        LowBatteryAlertEnabled = true,
        ActiveShortcutPreset = "Default",
        Profiles =
        {
            new ScreenProfilePreset("Work", "Right", 1.0, true, true, "Default", false),
            new ScreenProfilePreset("Presentation", "Right", 0.9, false, false, "Presentation", true),
            new ScreenProfilePreset("Away", "Right", 1.0, false, false, "Media", true)
        },
        Rules =
        {
            new AppAutomationRule("vlc", "Media", true),
            new AppAutomationRule("spotify", "Media", true),
            new AppAutomationRule("wmplayer", "Media", true),
            new AppAutomationRule("powerpnt", "Presentation", true),
            new AppAutomationRule("tally", "Accounting", false),
            new AppAutomationRule("excel", "Accounting", false),
            new AppAutomationRule("winman", "Accounting", false)
        }
    };

    internal static ProfilesAndRulesConfig Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<ProfilesAndRulesConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null)
                    {
                        if (cfg.Profiles.Count == 0) cfg.Profiles = CreateDefault().Profiles;
                        if (cfg.Rules.Count == 0) cfg.Rules = CreateDefault().Rules;
                        return cfg;
                    }
                }
            }
            catch { }
            var def = CreateDefault();
            Save(def);
            return def;
        }
    }

    internal static void Save(ProfilesAndRulesConfig config)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
            }
            catch { }
        }
    }

    internal static AppAutomationRule? MatchRule(ProfilesAndRulesConfig config, string? processName, string? windowTitle = null)
    {
        string haystack = ((processName ?? "") + " " + (windowTitle ?? "")).Trim();
        if (haystack.Length == 0) return null;
        foreach (var rule in config.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ProcessPattern)) continue;
            if (haystack.Contains(rule.ProcessPattern.Trim(), StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    internal static string SerializeSummaryForAndroid(ProfilesAndRulesConfig cfg) =>
        JsonSerializer.Serialize(new
        {
            activeProfile = cfg.ActiveProfile,
            clipboardSync = cfg.ClipboardSyncEnabled,
            notifPreviews = cfg.NotificationPreviewsEnabled,
            otpHelper = cfg.OtpHelperEnabled,
            shortcutPreset = cfg.ActiveShortcutPreset,
            profiles = cfg.Profiles.Select(p => p.Name).ToArray()
        });
}

internal sealed class NotificationPreviewFlyout : Form
{
    private readonly FlowLayoutPanel _list = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(10, 8, 10, 8),
        BackColor = Color.FromArgb(15, 23, 42)
    };
    private readonly Label _headerLabel = new()
    {
        Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
        ForeColor = Color.FromArgb(241, 245, 249),
        AutoSize = true
    };
    private readonly Action<int, string> _onDismiss;
    private readonly Action<int, string, string> _onReply;
    private readonly Action<string> _onCopyOtp;
    private int _targetId;

    internal NotificationPreviewFlyout(
        Action<int, string> onDismiss,
        Action<int, string, string> onReply,
        Action<string> onCopyOtp)
    {
        _onDismiss = onDismiss;
        _onReply = onReply;
        _onCopyOtp = onCopyOtp;

        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(370, 420);
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Text = "Device Notifications";

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(12, 8, 12, 6),
            BackColor = Color.FromArgb(22, 33, 54)
        };
        header.Controls.Add(_headerLabel);
        Controls.Add(_list);
        Controls.Add(header);
        Deactivate += (_, _) => Hide();
    }

    internal void ShowForDevice(int targetId, string deviceName, NotificationPreviewItem[] items, Point screenAnchor)
    {
        _targetId = targetId;
        _headerLabel.Text = $"{deviceName} — Notifications ({items.Length})";
        _list.SuspendLayout();
        _list.Controls.Clear();

        if (items.Length == 0)
        {
            _list.Controls.Add(new Label
            {
                Text = "No active notification previews yet.\nEnsure notification preview sharing is enabled in DeskTouch Setup on your phone.",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9.2f),
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                Padding = new Padding(4, 12, 4, 8)
            });
        }
        else
        {
            foreach (var item in items)
            {
                var card = new Panel
                {
                    Width = 326,
                    AutoSize = true,
                    Padding = new Padding(10, 8, 10, 8),
                    Margin = new Padding(0, 0, 0, 8),
                    BackColor = Color.FromArgb(26, 36, 56)
                };
                var stack = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true
                };
                stack.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(item.App) ? item.Title : $"{item.App} • {item.Title}",
                    Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(125, 211, 252),
                    AutoSize = true,
                    MaximumSize = new Size(302, 0)
                });
                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    stack.Controls.Add(new Label
                    {
                        Text = item.Text,
                        Font = new Font("Segoe UI", 8.8f),
                        ForeColor = Color.FromArgb(226, 232, 240),
                        AutoSize = true,
                        MaximumSize = new Size(302, 0),
                        Margin = new Padding(0, 3, 0, 4)
                    });
                }

                var btnRow = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoSize = true,
                    Margin = new Padding(0, 4, 0, 0)
                };
                if (!string.IsNullOrWhiteSpace(item.OtpCode))
                {
                    var otpBtn = MakeActionBtn($"Copy Code: {item.OtpCode}", true);
                    string codeCopy = item.OtpCode!;
                    otpBtn.Click += (_, _) => _onCopyOtp(codeCopy);
                    btnRow.Controls.Add(otpBtn);
                }
                if (item.CanReply)
                {
                    var replyBox = new TextBox
                    {
                        Width = 158,
                        BackColor = Color.FromArgb(15, 23, 42),
                        ForeColor = Color.White,
                        BorderStyle = BorderStyle.FixedSingle,
                        PlaceholderText = "Quick reply..."
                    };
                    var sendBtn = MakeActionBtn("Reply", true);
                    sendBtn.Click += (_, _) =>
                    {
                        string reply = replyBox.Text.Trim();
                        if (!string.IsNullOrEmpty(reply))
                        {
                            _onReply(_targetId, item.Key, reply);
                            replyBox.Text = "";
                        }
                    };
                    btnRow.Controls.Add(replyBox);
                    btnRow.Controls.Add(sendBtn);
                }
                var dismissBtn = MakeActionBtn("Dismiss", false);
                dismissBtn.Click += (_, _) =>
                {
                    _onDismiss(_targetId, item.Key);
                    card.Visible = false;
                };
                btnRow.Controls.Add(dismissBtn);
                stack.Controls.Add(btnRow);
                card.Controls.Add(stack);
                _list.Controls.Add(card);
            }
        }
        _list.ResumeLayout();

        var wa = Screen.FromPoint(screenAnchor).WorkingArea;
        int x = Math.Clamp(screenAnchor.X - Width / 2, wa.Left + 8, wa.Right - Width - 8);
        int y = Math.Clamp(screenAnchor.Y + 10, wa.Top + 8, wa.Bottom - Height - 8);
        Location = new Point(x, y);
        Show();
        Activate();
    }

    private static Button MakeActionBtn(string text, bool accent) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = accent ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105), BorderSize = 1 },
        BackColor = accent ? Color.FromArgb(29, 78, 216) : Color.FromArgb(30, 41, 59),
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 8f),
        Margin = new Padding(0, 2, 6, 2),
        Cursor = Cursors.Hand
    };
}

internal sealed class OtpBannerForm : Form
{
    private readonly Label _title = new()
    {
        Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
        ForeColor = Color.FromArgb(125, 211, 252),
        Dock = DockStyle.Top,
        Height = 22
    };
    private readonly Label _body = new()
    {
        Font = new Font("Consolas", 13f, FontStyle.Bold),
        ForeColor = Color.White,
        Dock = DockStyle.Top,
        Height = 26
    };
    private readonly Button _copyBtn = new()
    {
        Text = "Copy & Auto-Clear",
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(37, 99, 235),
        ForeColor = Color.White,
        Width = 140,
        Height = 28,
        Cursor = Cursors.Hand
    };
    private readonly Button _dismissBtn = new()
    {
        Text = "Dismiss",
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(30, 41, 59),
        ForeColor = Color.FromArgb(226, 232, 240),
        Width = 76,
        Height = 28,
        Cursor = Cursors.Hand
    };
    private readonly System.Windows.Forms.Timer _expireTimer = new() { Interval = 45000 };
    private string _currentCode = "";
    private readonly Action<string> _onCopy;

    internal OtpBannerForm(Action<string> onCopy)
    {
        _onCopy = onCopy;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 108);
        BackColor = Color.FromArgb(18, 28, 46);
        Padding = new Padding(14, 10, 14, 10);

        Paint += (_, e) =>
        {
            using var p = new Pen(Color.FromArgb(56, 189, 248), 1.5f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        };

        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.LeftToRight };
        _copyBtn.FlatAppearance.BorderSize = 0;
        _dismissBtn.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_currentCode)) _onCopy(_currentCode);
            Hide();
        };
        _dismissBtn.Click += (_, _) => Hide();
        row.Controls.Add(_copyBtn);
        row.Controls.Add(_dismissBtn);

        Controls.Add(row);
        Controls.Add(_body);
        Controls.Add(_title);
        _expireTimer.Tick += (_, _) => { _expireTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    internal void ShowCode(string app, string code)
    {
        _currentCode = code;
        _title.Text = $"Verification code from {app}";
        _body.Text = code;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        _expireTimer.Stop();
        _expireTimer.Start();
        if (!Visible) Show();
    }
}

internal sealed class ProfilesRulesDialog : Form
{
    internal ProfilesRulesDialog(
        ProfilesAndRulesConfig config,
        Action<string> applyProfile,
        Action<ProfilesAndRulesConfig> saveConfig)
    {
        Text = "DeskTouch Profiles & Smart Rules";
        Size = new Size(640, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(17, 23, 34);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var profilesTab = new TabPage("Screen Profiles") { BackColor = Color.FromArgb(20, 28, 42), Padding = new Padding(12) };
        var rulesTab = new TabPage("Per-App Rules & Privacy") { BackColor = Color.FromArgb(20, 28, 42), Padding = new Padding(12) };

        var profileList = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 22, 34),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ItemHeight = 24
        };
        void RefreshProfiles()
        {
            profileList.Items.Clear();
            foreach (var p in config.Profiles)
            {
                string activeTag = string.Equals(p.Name, config.ActiveProfile, StringComparison.OrdinalIgnoreCase) ? " [ACTIVE]" : "";
                profileList.Items.Add($"{p.Name}{activeTag}  —  Edge: {p.Edge}, Sens: {(int)(p.Sensitivity * 100)}%, Shortcuts: {p.ShortcutPreset}");
            }
        }
        RefreshProfiles();

        var profileBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 8, 0, 0) };
        var applyBtn = MakeDarkBtn("Activate Selected", true);
        applyBtn.Click += (_, _) =>
        {
            int idx = profileList.SelectedIndex;
            if (idx >= 0 && idx < config.Profiles.Count)
            {
                applyProfile(config.Profiles[idx].Name);
                RefreshProfiles();
            }
        };
        var nameInput = new TextBox
        {
            Width = 150,
            BackColor = Color.FromArgb(15, 22, 34),
            ForeColor = Color.White,
            PlaceholderText = "New profile name"
        };
        var saveAsBtn = MakeDarkBtn("Save Current Settings As", false);
        saveAsBtn.Click += (_, _) =>
        {
            string name = nameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var cur = config.Profiles.FirstOrDefault(x => string.Equals(x.Name, config.ActiveProfile, StringComparison.OrdinalIgnoreCase));
            config.Profiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            config.Profiles.Add(new ScreenProfilePreset(
                name,
                cur?.Edge ?? "Right",
                cur?.Sensitivity ?? 1.0,
                config.ClipboardSyncEnabled,
                config.NotificationPreviewsEnabled,
                config.ActiveShortcutPreset,
                cur?.ExpandMediaBar ?? false));
            config.ActiveProfile = name;
            saveConfig(config);
            applyProfile(name);
            nameInput.Text = "";
            RefreshProfiles();
        };
        profileBtns.Controls.AddRange(new Control[] { applyBtn, nameInput, saveAsBtn });
        profilesTab.Controls.Add(profileList);
        profilesTab.Controls.Add(profileBtns);

        var privacyPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 112, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var cbClip = new CheckBox { Text = "Automatic Shared Clipboard (text & images across devices)", Checked = config.ClipboardSyncEnabled, AutoSize = true, ForeColor = Color.White };
        var cbNotif = new CheckBox { Text = "Notification Previews & Quick Reply (click badges to view)", Checked = config.NotificationPreviewsEnabled, AutoSize = true, ForeColor = Color.White };
        var cbOtp = new CheckBox { Text = "One-Time Verification Code Helper (auto-clears after 45s)", Checked = config.OtpHelperEnabled, AutoSize = true, ForeColor = Color.White };
        var cbBatt = new CheckBox { Text = "Low Phone Battery Alert (< 15% and unplugged)", Checked = config.LowBatteryAlertEnabled, AutoSize = true, ForeColor = Color.White };
        void SyncToggles(object? s, EventArgs e)
        {
            config.ClipboardSyncEnabled = cbClip.Checked;
            config.NotificationPreviewsEnabled = cbNotif.Checked;
            config.OtpHelperEnabled = cbOtp.Checked;
            config.LowBatteryAlertEnabled = cbBatt.Checked;
            saveConfig(config);
        }
        cbClip.CheckedChanged += SyncToggles;
        cbNotif.CheckedChanged += SyncToggles;
        cbOtp.CheckedChanged += SyncToggles;
        cbBatt.CheckedChanged += SyncToggles;
        privacyPanel.Controls.AddRange(new Control[] { cbClip, cbNotif, cbOtp, cbBatt });

        var rulesList = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 22, 34),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        void RefreshRules()
        {
            rulesList.Items.Clear();
            foreach (var r in config.Rules)
                rulesList.Items.Add($"When focused app matches '{r.ProcessPattern}' → Shortcuts: {r.ShortcutPreset}" + (r.ExpandMediaBar ? " + Media Bar" : ""));
        }
        RefreshRules();

        var ruleAddPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 8, 0, 0) };
        var procInput = new TextBox { Width = 140, BackColor = Color.FromArgb(15, 22, 34), ForeColor = Color.White, PlaceholderText = "App name (e.g. zoom)" };
        var presetBox = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        presetBox.Items.AddRange(new object[] { "Default", "Media", "Presentation", "Accounting" });
        presetBox.SelectedIndex = 0;
        var expandMediaCb = new CheckBox { Text = "Show Media", AutoSize = true, ForeColor = Color.White, Margin = new Padding(6, 5, 6, 0) };
        var addRuleBtn = MakeDarkBtn("Add Rule", true);
        addRuleBtn.Click += (_, _) =>
        {
            string pat = procInput.Text.Trim();
            if (string.IsNullOrEmpty(pat)) return;
            config.Rules.RemoveAll(r => string.Equals(r.ProcessPattern, pat, StringComparison.OrdinalIgnoreCase));
            config.Rules.Add(new AppAutomationRule(pat, presetBox.SelectedItem?.ToString() ?? "Default", expandMediaCb.Checked));
            saveConfig(config);
            procInput.Text = "";
            RefreshRules();
        };
        var delRuleBtn = MakeDarkBtn("Remove Selected", false);
        delRuleBtn.Click += (_, _) =>
        {
            int idx = rulesList.SelectedIndex;
            if (idx >= 0 && idx < config.Rules.Count)
            {
                config.Rules.RemoveAt(idx);
                saveConfig(config);
                RefreshRules();
            }
        };
        ruleAddPanel.Controls.AddRange(new Control[] { procInput, presetBox, expandMediaCb, addRuleBtn, delRuleBtn });
        rulesTab.Controls.Add(rulesList);
        rulesTab.Controls.Add(ruleAddPanel);
        rulesTab.Controls.Add(privacyPanel);

        tabs.TabPages.Add(profilesTab);
        tabs.TabPages.Add(rulesTab);
        Controls.Add(tabs);
    }

    private static Button MakeDarkBtn(string text, bool primary) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = primary ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105), BorderSize = 1 },
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 8, 0)
    };
}
