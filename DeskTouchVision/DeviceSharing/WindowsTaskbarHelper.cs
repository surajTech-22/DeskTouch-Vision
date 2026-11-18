using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Captures the local Windows user session's live taskbar (open application
/// windows + pinned taskbar shortcuts + cached PNG icons) and executes
/// strictly allowlisted focus/launch actions from paired endpoints.
/// Never accepts file paths or arbitrary commands from the network.
/// </summary>
internal static class WindowsTaskbarHelper
{
    internal const string ActionStart = "__start__";
    internal const string ActionSearch = "__search__";

    internal sealed record TaskbarItemDto(
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("n")] string Title,
        [property: JsonPropertyName("p")] string App,
        [property: JsonPropertyName("a")] bool Active,
        [property: JsonPropertyName("m")] bool Minimized,
        [property: JsonPropertyName("r")] bool Running,
        [property: JsonPropertyName("h")] string IconHash);

    internal sealed record TaskbarStateDto(
        [property: JsonPropertyName("targetId")] int TargetId,
        [property: JsonPropertyName("items")] TaskbarItemDto[] Items);

    internal sealed record TaskbarCapture(
        string StateJson,
        string Signature,
        IReadOnlyDictionary<string, string> IconsByHash);

    private readonly record struct ActionTarget(IntPtr Hwnd, string? PinnedLnkPath);
    private readonly record struct CachedIcon(string Hash, string Base64, long CachedTicks);

    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WmGetIcon = 0x007F;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int GclpHIcon = -14;
    private const int GclpHIconSm = -34;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const int MaxTaskbarItems = 15;
    private const int MaxIconCacheEntries = 96;

    private static readonly ConcurrentDictionary<string, CachedIcon> IconMemo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TargetGate = new();
    private static Dictionary<string, ActionTarget> _activeTargets = new(StringComparer.Ordinal);
    private static long _lastPinnedScanTicks;
    private static IReadOnlyList<(string Name, string LnkPath, string? TargetExe)> _cachedPinned = Array.Empty<(string, string, string?)>();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr hwnd, int index);

    private static IntPtr GetClassIconHandle(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetClassLongPtr64(hwnd, index) : new IntPtr(unchecked((int)GetClassLong32(hwnd, index)));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttrs, ref SHFILEINFO shfi, uint sizeFileInfo, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Win.INPUT[] inputs, int cbSize);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCom { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    internal static TaskbarCapture CaptureSnapshot(int targetId = 0)
    {
        var items = new List<TaskbarItemDto>();
        var nextTargets = new Dictionary<string, ActionTarget>(StringComparer.Ordinal);
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        var runningItems = EnumerateRunningWindows();
        var pinned = ReadPinnedShortcuts();

        // Map pinned order cleanly so open windows retain consistent pinned slots.
        var pinnedMatchByIndex = new int[runningItems.Count];
        Array.Fill(pinnedMatchByIndex, int.MaxValue);
        var matchedPinnedIndices = new HashSet<int>();

        for (int p = 0; p < pinned.Count; p++)
        {
            var pin = pinned[p];
            for (int r = 0; r < runningItems.Count; r++)
            {
                if (IsSameApp(runningItems[r].ExePath, runningItems[r].ProcessName, runningItems[r].Title, pin.TargetExe, pin.Name))
                {
                    if (pinnedMatchByIndex[r] == int.MaxValue)
                        pinnedMatchByIndex[r] = p;
                    matchedPinnedIndices.Add(p);
                }
            }
        }

        var orderedRunning = runningItems
            .Select((win, idx) => (Win: win, PinnedOrder: pinnedMatchByIndex[idx], Index: idx))
            .OrderBy(x => x.PinnedOrder)
            .ThenBy(x => x.Win.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Win.Hwnd.ToInt64())
            .Take(MaxTaskbarItems)
            .ToList();

        foreach (var entry in orderedRunning)
        {
            var w = entry.Win;
            string key = "w" + w.Hwnd.ToInt64().ToString("X");
            var (hash, b64) = ResolveWindowIcon(w.Hwnd, w.ExePath, w.ProcessName);
            if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(b64))
                icons[hash] = b64;
            nextTargets[key] = new ActionTarget(w.Hwnd, null);
            items.Add(new TaskbarItemDto(
                key,
                TrimLabel(CleanWindowTitle(w.Title, w.FriendlyApp), 24),
                TrimLabel(w.FriendlyApp, 18),
                w.Active,
                w.Minimized,
                Running: true,
                hash));
        }

        for (int p = 0; p < pinned.Count && items.Count < MaxTaskbarItems; p++)
        {
            if (matchedPinnedIndices.Contains(p)) continue;
            var pin = pinned[p];
            string key = "p" + p;
            var (hash, b64) = ResolveFileIcon(pin.LnkPath);
            if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(b64))
                icons[hash] = b64;
            nextTargets[key] = new ActionTarget(IntPtr.Zero, pin.LnkPath);
            items.Add(new TaskbarItemDto(
                key,
                TrimLabel(pin.Name, 20),
                TrimLabel(pin.Name, 18),
                Active: false,
                Minimized: false,
                Running: false,
                hash));
        }

        lock (TargetGate)
            _activeTargets = nextTargets;

        string json = SerializeCapped(targetId, items) ?? "{\"targetId\":0,\"items\":[]}";
        var sigSb = new StringBuilder(items.Count * 32);
        sigSb.Append(targetId);
        foreach (var item in items)
        {
            sigSb.Append('|').Append(item.Key).Append(':')
                .Append(item.Active ? '1' : '0')
                .Append(item.Minimized ? '1' : '0')
                .Append(item.Running ? '1' : '0').Append(':')
                .Append(item.Title).Append(':').Append(item.IconHash);
        }
        return new TaskbarCapture(json, sigSb.ToString(), icons);
    }

    internal static string? SerializeCapped(int targetId, IReadOnlyList<TaskbarItemDto> items, int maxUtf8Bytes = 7500)
    {
        if (items.Count == 0)
            return JsonSerializer.Serialize(new TaskbarStateDto(targetId, Array.Empty<TaskbarItemDto>()));
        for (int take = items.Count; take >= 0; take--)
        {
            var slice = take == items.Count ? items.ToArray() : items.Take(take).ToArray();
            string json = JsonSerializer.Serialize(new TaskbarStateDto(targetId, slice));
            if (Encoding.UTF8.GetByteCount(json) <= maxUtf8Bytes)
                return json;
        }
        return null;
    }

    internal static bool IsValidActionKeyFormat(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 24) return false;
        if (key is ActionStart or ActionSearch) return true;
        if (key[0] is not ('w' or 'p')) return false;
        for (int i = 1; i < key.Length; i++)
        {
            char c = key[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return key.Length > 1;
    }

    internal static bool ExecuteAction(string? key)
    {
        if (!IsValidActionKeyFormat(key)) return false;
        if (key == ActionStart)
            return SendWinChord(0);
        if (key == ActionSearch)
            return SendWinChord(0x53); // 'S' -> Win+S Search

        ActionTarget target;
        lock (TargetGate)
        {
            if (!_activeTargets.TryGetValue(key!, out target))
                return false;
        }

        if (target.Hwnd != IntPtr.Zero && IsWindow(target.Hwnd))
            return FocusWindow(target.Hwnd);

        if (!string.IsNullOrWhiteSpace(target.PinnedLnkPath) && File.Exists(target.PinnedLnkPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target.PinnedLnkPath) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static bool FocusWindow(IntPtr hwnd)
    {
        IntPtr fg = Win.GetForegroundWindow();
        uint fgTid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint curTid = GetCurrentThreadId();
        bool attached = false;
        try
        {
            Win.AllowSetForegroundWindow(Win.ASFW_ANY);
            if (fgTid != 0 && fgTid != curTid)
                attached = AttachThreadInput(curTid, fgTid, true);
            if (Win.IsIconic(hwnd))
                Win.ShowWindow(hwnd, Win.SW_RESTORE);
            else
                Win.ShowWindow(hwnd, Win.SW_SHOW);
            Win.BringWindowToTop(hwnd);
            return Win.SetForegroundWindow(hwnd);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached)
                AttachThreadInput(curTid, fgTid, false);
        }
    }

    private static bool SendWinChord(ushort secondaryVk)
    {
        const ushort vkLWin = 0x5B;
        var list = new List<Win.INPUT>(4)
        {
            new() { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = vkLWin, Flags = Win.KEYEVENTF_EXTENDEDKEY } } }
        };
        if (secondaryVk != 0)
        {
            list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = secondaryVk } } });
            list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = secondaryVk, Flags = Win.KEYEVENTF_KEYUP } } });
        }
        list.Add(new Win.INPUT { Type = Win.INPUT_KEYBOARD, Data = new Win.INPUTUNION { Key = new Win.KEYBDINPUT { Vk = vkLWin, Flags = Win.KEYEVENTF_EXTENDEDKEY | Win.KEYEVENTF_KEYUP } } });
        var arr = list.ToArray();
        return SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win.INPUT>()) == arr.Length;
    }

    private sealed record RunningWindowInfo(
        IntPtr Hwnd,
        uint ProcessId,
        string ProcessName,
        string? ExePath,
        string FriendlyApp,
        string Title,
        bool Active,
        bool Minimized);

    private static List<RunningWindowInfo> EnumerateRunningWindows()
    {
        IntPtr fg = Win.GetForegroundWindow();
        var list = new List<RunningWindowInfo>(16);
        EnumWindows((hwnd, enumParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            long exStyle = GetExStyle(hwnd);
            if ((exStyle & WsExToolWindow) != 0) return true;
            bool hasAppWindow = (exStyle & WsExAppWindow) != 0;
            if ((exStyle & WsExNoActivate) != 0 && !hasAppWindow) return true;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero && !hasAppWindow) return true;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(Math.Min(len + 2, 256));
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title) || IsShellChromeTitle(title))
                return true;

            bool minimized = Win.IsIconic(hwnd);
            if (!minimized)
            {
                if (!GetWindowRect(hwnd, out var rc) || rc.Right - rc.Left < 40 || rc.Bottom - rc.Top < 24)
                    return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            string? exePath = QueryProcessPath(pid);
            string procName = !string.IsNullOrEmpty(exePath)
                ? Path.GetFileNameWithoutExtension(exePath)
                : "app";
            if (string.Equals(procName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(procName, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(procName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(procName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                return true;

            string friendly = FriendlyAppName(procName, title);
            list.Add(new RunningWindowInfo(hwnd, pid, procName, exePath, friendly, title, hwnd == fg, minimized));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsShellChromeTitle(string title) =>
        title is "Program Manager" or "Windows Input Experience" or "Microsoft Text Input Application"
            or "PopupHost" or "Default IME" or "Windows Shell Experience Host";

    private static string? QueryProcessPath(uint pid)
    {
        if (pid == 0) return null;
        IntPtr proc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (proc == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(520);
            uint cap = (uint)sb.Capacity;
            return QueryFullProcessImageName(proc, 0, sb, ref cap) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(proc);
        }
    }

    private static IReadOnlyList<(string Name, string LnkPath, string? TargetExe)> ReadPinnedShortcuts()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastPinnedScanTicks) < 15000 && _cachedPinned.Count > 0)
            return _cachedPinned;
        Volatile.Write(ref _lastPinnedScanTicks, now);
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
            if (!Directory.Exists(dir))
            {
                _cachedPinned = Array.Empty<(string, string, string?)>();
                return _cachedPinned;
            }
            var links = new List<(string Name, string LnkPath, string? TargetExe)>();
            foreach (string file in Directory.EnumerateFiles(dir, "*.lnk").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file).Trim();
                if (string.IsNullOrEmpty(name)) continue;
                string? targetExe = ResolveLnkTarget(file);
                links.Add((name, file, targetExe));
                if (links.Count >= 16) break;
            }
            _cachedPinned = links;
            return links;
        }
        catch
        {
            return _cachedPinned;
        }
    }

    private static string? ResolveLnkTarget(string lnkPath)
    {
        object? comObj = null;
        try
        {
            comObj = new ShellLinkCom();
            ((IPersistFile)comObj).Load(lnkPath, 0);
            var sb = new StringBuilder(260);
            ((IShellLinkW)comObj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string p = sb.ToString().Trim();
            return p.Length > 0 ? p : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
                Marshal.FinalReleaseComObject(comObj);
        }
    }

    private static bool IsSameApp(string? runExe, string runProc, string runTitle, string? pinExe, string pinName)
    {
        if (!string.IsNullOrEmpty(runExe) && !string.IsNullOrEmpty(pinExe) &&
            string.Equals(Path.GetFileNameWithoutExtension(runExe), Path.GetFileNameWithoutExtension(pinExe), StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(runProc, pinName, StringComparison.OrdinalIgnoreCase))
            return true;
        string cleanPin = pinName.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        string cleanProc = runProc.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        if (cleanPin.Length >= 3 && cleanProc.Contains(cleanPin, StringComparison.OrdinalIgnoreCase))
            return true;
        if (runProc.Equals("msedge", StringComparison.OrdinalIgnoreCase) && pinName.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            return true;
        if (runProc.Equals("explorer", StringComparison.OrdinalIgnoreCase) && pinName.Contains("Explorer", StringComparison.OrdinalIgnoreCase))
            return true;
        if (runProc.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) && pinName.Contains("Terminal", StringComparison.OrdinalIgnoreCase))
            return true;
        return pinName.Length >= 4 && runTitle.Contains(pinName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyAppName(string procName, string title)
    {
        if (string.Equals(procName, "chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (string.Equals(procName, "msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase)) return "Explorer";
        if (string.Equals(procName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase)) return "Terminal";
        if (string.Equals(procName, "Code", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        if (string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return CleanWindowTitle(title, "App");
        return procName;
    }

    private static string CleanWindowTitle(string title, string fallbackApp)
    {
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        string trimmed = dash > 2 ? title[..dash].Trim() : title.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallbackApp : trimmed;
    }

    private static string TrimLabel(string value, int maxLen)
    {
        string clean = (value ?? "").Trim();
        return clean.Length <= maxLen ? clean : clean[..(maxLen - 1)] + "…";
    }

    private static (string Hash, string Base64) ResolveWindowIcon(IntPtr hwnd, string? exePath, string procName)
    {
        string cacheKey = !string.IsNullOrEmpty(exePath) ? "exe:" + exePath : "hwnd:" + hwnd.ToInt64();
        if (IconMemo.TryGetValue(cacheKey, out var cached) &&
            Environment.TickCount64 - cached.CachedTicks < 120_000)
        {
            return (cached.Hash, cached.Base64);
        }

        IntPtr hIcon = IntPtr.Zero;
        if (SendMessageTimeout(hwnd, WmGetIcon, (IntPtr)1, IntPtr.Zero, SmtoAbortIfHung, 60, out var big) != IntPtr.Zero && big != IntPtr.Zero)
            hIcon = big;
        else if (SendMessageTimeout(hwnd, WmGetIcon, (IntPtr)2, IntPtr.Zero, SmtoAbortIfHung, 60, out var sm2) != IntPtr.Zero && sm2 != IntPtr.Zero)
            hIcon = sm2;
        else if (SendMessageTimeout(hwnd, WmGetIcon, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 60, out var sm) != IntPtr.Zero && sm != IntPtr.Zero)
            hIcon = sm;
        else
        {
            hIcon = GetClassIconHandle(hwnd, GclpHIcon);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassIconHandle(hwnd, GclpHIconSm);
        }

        if (hIcon != IntPtr.Zero)
        {
            var encoded = EncodeIconHandleToPng(hIcon);
            if (encoded != null)
            {
                MemoizeIcon(cacheKey, encoded.Value.Hash, encoded.Value.Base64);
                return encoded.Value;
            }
        }

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            return ResolveFileIcon(exePath, cacheKey);

        return ("", "");
    }

    private static (string Hash, string Base64) ResolveFileIcon(string path, string? overrideKey = null)
    {
        string cacheKey = overrideKey ?? ("file:" + path);
        if (IconMemo.TryGetValue(cacheKey, out var cached) &&
            Environment.TickCount64 - cached.CachedTicks < 120_000)
        {
            return (cached.Hash, cached.Base64);
        }

        var shfi = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
        if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                var encoded = EncodeIconHandleToPng(shfi.hIcon);
                if (encoded != null)
                {
                    MemoizeIcon(cacheKey, encoded.Value.Hash, encoded.Value.Base64);
                    return encoded.Value;
                }
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
        return ("", "");
    }

    private static (string Hash, string Base64)? EncodeIconHandleToPng(IntPtr hIcon)
    {
        try
        {
            using Icon ico = Icon.FromHandle(hIcon);
            foreach (int size in new[] { 20, 16 })
            {
                using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawIcon(ico, new Rectangle(0, 0, size, size));
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                byte[] png = ms.ToArray();
                string b64 = Convert.ToBase64String(png);
                if (b64.Length <= 4800)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(png))[..8];
                    return (hash, b64);
                }
            }
        }
        catch { }
        return null;
    }

    private static void MemoizeIcon(string cacheKey, string hash, string b64)
    {
        if (IconMemo.Count >= MaxIconCacheEntries) IconMemo.Clear();
        IconMemo[cacheKey] = new CachedIcon(hash, b64, Environment.TickCount64);
    }
}
