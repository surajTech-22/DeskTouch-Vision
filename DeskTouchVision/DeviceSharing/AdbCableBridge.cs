using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Automatically configures localhost USB data-cable forwarding (`adb reverse`)
/// for discovery (TCP 37378) and TLS control/input (TCP 37377), manages
/// Wireless Debugging auto-connect + one-time pairing, and launches
/// DeskTouch's Accessibility-free `/dev/uhid` `ShellInputDaemon` over authorized
/// ADB (`app_process`) with one-time stdin credentials.
/// </summary>
internal sealed class AdbCableBridge : IDisposable
{
    private const string AndroidAppPackage = "com.desktouch.vision";
    private const string ShellPortFile = "/data/local/tmp/dt_s.port";
    private const string ShellPidFile = "/data/local/tmp/dt_s.pid";
    private const string ShellTokenFile = "/data/local/tmp/dt_s.tok";
    private const string ShellApkFile = "/data/local/tmp/dt_s.apk";
    private const string ShellErrFile = "/data/local/tmp/dt_s.err";
    private const long BridgeRecheckIntervalMs = 12000;
    private const long HealthyShellVerifyIntervalMs = 45000;
    private const long WirelessConnectRetryMs = 12000;
    private const long HintFreshWindowMs = 120000;
    private static readonly string[] PlatformToolsRuntimeFiles =
    [
        "adb.exe",
        "AdbWinApi.dll",
        "AdbWinUsbApi.dll",
        "NOTICE.txt"
    ];

    internal sealed record BridgeStatusReport(
        string State,
        string Detail,
        int PairPortHint,
        int ConnectPortHint);

    private sealed record AndroidDebugHint(
        string? PeerIp,
        bool UsbDebug,
        bool WifiDebug,
        int ConnectPort,
        int PairPort,
        bool BridgeAllowed,
        bool BridgeReady,
        long UpdatedMs);

    private readonly Action<string>? _log;
    private readonly Action<int, string>? _onShellBridgeReady;
    private readonly Action? _onBridgeDetached;
    private readonly Action? _onCableBridgeReady;
    private readonly Action<BridgeStatusReport>? _onBridgeStatusChanged;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, long> _bridgedSerials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Port, string Secret, long LastCheckedMs)> _shellBridges =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _wirelessConnectBackoffUntilMs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AndroidDebugHint> _androidHints =
        new(StringComparer.OrdinalIgnoreCase);
    private long _lastMdnsScanMs;
    private readonly HashSet<string> _loggedMissingAppSerials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedDaemonErrors = new(StringComparer.Ordinal);
    private readonly object _shellGate = new();
    private (int Port, string Secret)? _currentShellBridge;
    private BridgeStatusReport _currentStatus = new("debug_off",
        "Standby: plug in USB cable (USB debugging ON) or enable Wireless debugging.", 0, 0);
    private string? _lastDaemonError;
    private long _stagedLastCheckedMs;
    private string? _stagedAdbCache;
    private readonly Task _loop;
    private int _disposed;

    internal AdbCableBridge(
        Action<string>? log = null,
        Action<int, string>? onShellBridgeReady = null,
        Action? onBridgeDetached = null,
        Action? onCableBridgeReady = null,
        Action<BridgeStatusReport>? onBridgeStatusChanged = null)
    {
        _log = log;
        _onShellBridgeReady = onShellBridgeReady;
        _onBridgeDetached = onBridgeDetached;
        _onCableBridgeReady = onCableBridgeReady;
        _onBridgeStatusChanged = onBridgeStatusChanged;
        _loop = Task.Run(RunAsync);
    }

    internal (int Port, string Secret)? CurrentShellBridge
    {
        get { lock (_shellGate) return _currentShellBridge; }
    }

    internal BridgeStatusReport CurrentStatus
    {
        get { lock (_shellGate) return _currentStatus; }
    }

    internal void TriggerRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    internal void UpdateAndroidDebugHint(
        string? peerIp,
        bool usbDebug,
        bool wifiDebug,
        int connectPort,
        int pairPort,
        bool bridgeAllowed,
        bool bridgeReady)
    {
        string? normalizedIp = string.IsNullOrWhiteSpace(peerIp) ? null : peerIp.Trim();
        string key = normalizedIp ?? "__default__";
        long nowMs = Environment.TickCount64;
        AndroidDebugHint? prev = _androidHints.TryGetValue(key, out var existing) ? existing : null;
        var next = new AndroidDebugHint(normalizedIp, usbDebug, wifiDebug,
            connectPort > 0 && connectPort <= 65535 ? connectPort : 0,
            pairPort > 0 && pairPort <= 65535 ? pairPort : 0,
            bridgeAllowed, bridgeReady, nowMs);
        _androidHints[key] = next;

        bool wakeNow = false;
        if (next.ConnectPort > 0 && normalizedIp != null)
        {
            string ep = $"{normalizedIp}:{next.ConnectPort}";
            if (prev == null || prev.ConnectPort != next.ConnectPort || !prev.WifiDebug)
            {
                lock (_shellGate) _wirelessConnectBackoffUntilMs.Remove(ep);
                wakeNow = true;
            }
        }
        if (prev == null || prev.UsbDebug != usbDebug || prev.WifiDebug != wifiDebug || !bridgeReady)
        {
            if (bridgeAllowed && (usbDebug || wifiDebug || next.ConnectPort > 0))
                wakeNow = true;
        }
        if (wakeNow)
            TriggerRefresh();
    }

    internal static string? NormalizeIpv4Host(IPAddress? address)
    {
        if (address == null) return null;
        IPAddress unmapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (unmapped.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(unmapped))
            return null;
        return unmapped.ToString();
    }

    internal static string? ResolveAdbPath(
        string? explicitOverride = null,
        string? localAppData = null,
        string? androidHome = null,
        string? androidSdkRoot = null,
        string? pathEnv = null)
    {
        string? envOverride = explicitOverride ?? Environment.GetEnvironmentVariable("DESKTOUCH_ADB_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe")
        };

        string? local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(Path.Combine(local, "DeskTouchVision", "platform-tools", "adb.exe"));
            candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        }

        foreach (string? sdk in new[]
        {
            androidHome ?? Environment.GetEnvironmentVariable("ANDROID_HOME"),
            androidSdkRoot ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
        })
        {
            if (!string.IsNullOrWhiteSpace(sdk))
                candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        }

        candidates.Add(@"C:\platform-tools\adb.exe");

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception) { }
        }

        string? pathValue = pathEnv ?? Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "adb.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception) { }
            }
        }

        return null;
    }

    private string? ResolveRuntimeAdbPath()
    {
        string? envOverride = Environment.GetEnvironmentVariable("DESKTOUCH_ADB_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        // Stage bundled platform-tools into %LOCALAPPDATA%\DeskTouchVision\platform-tools so a
        // persistent `adb.exe` server never locks the application install directory during updates.
        string bundledDir = Path.Combine(AppContext.BaseDirectory, "platform-tools");
        string bundledAdb = Path.Combine(bundledDir, "adb.exe");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(bundledAdb))
        {
            long nowMs = Environment.TickCount64;
            if (_stagedAdbCache != null && File.Exists(_stagedAdbCache) &&
                nowMs - Volatile.Read(ref _stagedLastCheckedMs) < 60000)
            {
                return _stagedAdbCache;
            }
            Volatile.Write(ref _stagedLastCheckedMs, nowMs);
            string stagedDir = Path.Combine(local, "DeskTouchVision", "platform-tools");
            string stagedAdb = Path.Combine(stagedDir, "adb.exe");
            try
            {
                Directory.CreateDirectory(stagedDir);
                foreach (string name in PlatformToolsRuntimeFiles)
                {
                    string src = Path.Combine(bundledDir, name);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(stagedDir, name);
                    bool needsCopy = !File.Exists(dst);
                    if (!needsCopy)
                    {
                        var si = new FileInfo(src);
                        var di = new FileInfo(dst);
                        needsCopy = si.Length != di.Length || si.LastWriteTimeUtc != di.LastWriteTimeUtc;
                    }
                    if (needsCopy)
                    {
                        try
                        {
                            File.Copy(src, dst, overwrite: true);
                        }
                        catch (IOException) when (File.Exists(dst))
                        {
                            // Keep using the active staged binary if an ADB server is already running.
                        }
                        catch (UnauthorizedAccessException) when (File.Exists(dst))
                        {
                        }
                    }
                }
                if (File.Exists(stagedAdb))
                {
                    _stagedAdbCache = stagedAdb;
                    return stagedAdb;
                }
            }
            catch (Exception) { }
        }

        return ResolveAdbPath();
    }

    internal static IReadOnlyList<string> ParseWiredAdbSerials(string adbDevicesOutput) =>
        ParseAdbSerialsByState(adbDevicesOutput, expectedState: "device", wiredOnly: true, wirelessOnly: false);

    internal static IReadOnlyList<string> ParseConnectedAdbSerials(string adbDevicesOutput) =>
        ParseAdbSerialsByState(adbDevicesOutput, expectedState: "device", wiredOnly: false, wirelessOnly: false);

    internal static IReadOnlyList<string> ParseAdbUnauthorizedSerials(string adbDevicesOutput) =>
        ParseAdbSerialsByState(adbDevicesOutput, expectedState: "unauthorized", wiredOnly: false, wirelessOnly: false);

    internal static IReadOnlyList<string> ParseOfflineWirelessSerials(string adbDevicesOutput) =>
        ParseAdbSerialsByState(adbDevicesOutput, expectedState: "offline", wiredOnly: false, wirelessOnly: true);

    private static IReadOnlyList<string> ParseAdbSerialsByState(
        string adbDevicesOutput, string expectedState, bool wiredOnly, bool wirelessOnly)
    {
        if (string.IsNullOrWhiteSpace(adbDevicesOutput))
            return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(adbDevicesOutput);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;

            string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[1], expectedState, StringComparison.Ordinal))
                continue;

            string serial = parts[0];
            bool isWireless = serial.Contains(':') ||
                serial.Contains("_adb-tls", StringComparison.OrdinalIgnoreCase);
            if (wiredOnly && isWireless) continue;
            if (wirelessOnly && !isWireless) continue;

            if (seen.Add(serial))
                result.Add(serial);
        }
        return result;
    }

    internal static IReadOnlyList<string> ParseMdnsTlsConnectEndpoints(string mdnsOutput) =>
        ParseMdnsServiceEndpoints(mdnsOutput, "_adb-tls-connect._tcp");

    internal static IReadOnlyList<string> ParseMdnsTlsPairingEndpoints(string mdnsOutput) =>
        ParseMdnsServiceEndpoints(mdnsOutput, "_adb-tls-pairing._tcp");

    private static IReadOnlyList<string> ParseMdnsServiceEndpoints(string mdnsOutput, string serviceTypeToken)
    {
        if (string.IsNullOrWhiteSpace(mdnsOutput))
            return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(mdnsOutput);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!parts[1].Contains(serviceTypeToken, StringComparison.OrdinalIgnoreCase))
                continue;
            string endpoint = parts[^1].Trim();
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || colon >= endpoint.Length - 1) continue;
            if (!int.TryParse(endpoint[(colon + 1)..], out int port) || port is <= 0 or > 65535)
                continue;
            if (seen.Add(endpoint))
                result.Add(endpoint);
        }
        return result;
    }

    internal static int ExtractPortFromEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return 0;
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon >= endpoint.Length - 1) return 0;
        return int.TryParse(endpoint[(colon + 1)..], out int port) && port is > 0 and <= 65535 ? port : 0;
    }

    internal static string? ExtractShellFailureReason(string launchOut)
    {
        if (string.IsNullOrWhiteSpace(launchOut)) return null;
        using var reader = new StringReader(launchOut);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string t = line.Trim();
            if (t.StartsWith("NO_PORT:", StringComparison.Ordinal))
            {
                string detail = t["NO_PORT:".Length..].Trim();
                return detail.Length > 0 ? detail : null;
            }
        }
        return null;
    }

    internal static int ParsePortOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return 0;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("PORT=", StringComparison.Ordinal))
                trimmed = trimmed["PORT=".Length..].Trim();
            if (int.TryParse(trimmed, out int port) && port > 0 && port <= 65535)
                return port;
        }
        return 0;
    }

    internal static string BuildShellCheckScript(int expectedPort) =>
        $"APK=$(pm path {AndroidAppPackage} 2>/dev/null | head -n 1 | cut -d: -f2 | tr -d '\\r'); " +
        "[ -n \"$APK\" ] || { echo NO_APP; exit 0; }; " +
        $"PID=$(cat {ShellPidFile} 2>/dev/null | tr -d '\\r\\n '); " +
        $"PORT=$(cat {ShellPortFile} 2>/dev/null | tr -d '\\r\\n '); " +
        $"APK_RUN=$(cat {ShellApkFile} 2>/dev/null | tr -d '\\r\\n '); " +
        $"if [ -n \"$PID\" ] && kill -0 \"$PID\" 2>/dev/null && [ \"$PORT\" = \"{expectedPort}\" ] && [ \"$APK_RUN\" = \"$APK\" ]; then echo \"PORT=$PORT\"; else echo NEEDED; fi";

    internal static string BuildShellDaemonScript() =>
        $"umask 077; if [ -s {ShellPidFile} ]; then OP=$(cat {ShellPidFile} 2>/dev/null | tr -d '\\r\\n '); [ -n \"$OP\" ] && [ \"$OP\" != \"$$\" ] && kill \"$OP\" 2>/dev/null || true; fi; " +
        $"rm -f {ShellPortFile} {ShellPidFile} {ShellTokenFile} {ShellApkFile} {ShellErrFile}; " +
        $"APK=$(pm path {AndroidAppPackage} 2>/dev/null | head -n 1 | cut -d: -f2 | tr -d '\\r'); " +
        "[ -n \"$APK\" ] || { echo NO_APP; exit 0; }; " +
        "IFS= read -r TK || exit 2; " +
        $"printf '%s\\n' \"$TK\" > {ShellTokenFile}; unset TK; " +
        $"printf '%s\\n' \"$APK\" > {ShellApkFile}; " +
        $"{{ exec < {ShellTokenFile}; rm -f {ShellTokenFile}; CLASSPATH=\"$APK\" exec setsid /system/bin/app_process /system/bin com.desktouch.vision.ShellInputDaemon --pid-file {ShellPidFile} --port-file {ShellPortFile} >/dev/null 2>{ShellErrFile}; }} </dev/null >/dev/null 2>&1 & " +
        "i=0; while [ \"$i\" -lt 40 ]; do " +
        $"[ -s {ShellPortFile} ] && {{ P=$(cat {ShellPortFile} 2>/dev/null | tr -d '\\r\\n '); [ -n \"$P\" ] && {{ echo \"PORT=$P\"; exit 0; }}; }}; " +
        "sleep 0.05 2>/dev/null || sleep 1; i=$((i+1)); done; " +
        $"ERR=$(head -n 1 {ShellErrFile} 2>/dev/null | tr -d '\\r'); rm -f {ShellErrFile}; " +
        "[ -n \"$ERR\" ] && echo \"NO_PORT:$ERR\" || echo NO_PORT";

    internal static bool HasReverseMappings(string reverseListOutput, params int[] ports)
    {
        if (string.IsNullOrWhiteSpace(reverseListOutput) || ports.Length == 0)
            return false;

        foreach (int port in ports)
        {
            string rule = $"tcp:{port}";
            bool matched = false;
            using var reader = new StringReader(reverseListOutput);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < parts.Length; i++)
                {
                    if (string.Equals(parts[i], rule, StringComparison.Ordinal) &&
                        string.Equals(parts[i + 1], rule, StringComparison.Ordinal))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched) break;
            }
            if (!matched) return false;
        }
        return true;
    }

    internal async Task<(bool Ok, string Message)> PairWirelessDebuggingAsync(
        string? peerIp, int explicitPairPort, string pairingCode, int explicitConnectPort)
    {
        string digits = new string((pairingCode ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 4 || digits.Length > 8)
            return (false, "Enter the 6-digit pairing code shown under 'Pair device with pairing code' on your phone.");

        string? adbPath = ResolveRuntimeAdbPath();
        if (adbPath == null)
            return (false, "ADB platform tools were not found on this Windows PC.");

        string? normalizedIp = string.IsNullOrWhiteSpace(peerIp) ? null : peerIp.Trim();
        string? pairTarget = null;
        string? mdnsOutSnapshot = null;
        for (int attempt = 0; attempt < 2 && pairTarget == null; attempt++)
        {
            if (attempt > 0) await Task.Delay(450, _stop.Token);
            var (mdnsOk, mdnsOut) = await RunAdbAsync(adbPath, _stop.Token, "mdns", "services");
            if (mdnsOk)
            {
                mdnsOutSnapshot = mdnsOut;
                IReadOnlyList<string> pairingEps = ParseMdnsTlsPairingEndpoints(mdnsOut);
                if (normalizedIp != null)
                {
                    pairTarget = pairingEps.FirstOrDefault(ep =>
                        ep.StartsWith(normalizedIp + ":", StringComparison.OrdinalIgnoreCase));
                }
                pairTarget ??= pairingEps.FirstOrDefault();
            }
        }
        if (pairTarget == null && normalizedIp != null && explicitPairPort is > 0 and <= 65535)
            pairTarget = $"{normalizedIp}:{explicitPairPort}";

        if (pairTarget == null)
        {
            return (false,
                "Could not find the phone's Wireless Debugging pairing port. Keep 'Pair device with pairing code' open on your phone or enter the Port shown there.");
        }

        var (_, pairOut) = await RunAdbWithStdinAsync(
            adbPath, null, _stop.Token, includeStdErr: true, "pair", pairTarget, digits);
        if (!pairOut.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            string reason = pairOut.Trim();
            if (reason.Length > 140) reason = reason[..140];
            _log?.Invoke($"Wireless debugging pairing failed for {pairTarget}: {reason}");
            return (false, string.IsNullOrWhiteSpace(reason)
                ? "Pairing was rejected. Double-check the 6-digit code and port while the phone popup stays open."
                : $"Pairing failed ({pairTarget}): {reason}");
        }

        _log?.Invoke($"Wireless debugging paired with {pairTarget}; connecting Virtual Bridge...");
        lock (_shellGate) _wirelessConnectBackoffUntilMs.Clear();
        _lastMdnsScanMs = 0;

        // Immediately connect to the live Wireless Debugging port so Virtual Bridge activates right away.
        var connectCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(mdnsOutSnapshot))
            connectCandidates.AddRange(ParseMdnsTlsConnectEndpoints(mdnsOutSnapshot));
        var (freshMdnsOk, freshMdns) = await RunAdbAsync(adbPath, _stop.Token, "mdns", "services");
        if (freshMdnsOk)
            connectCandidates.AddRange(ParseMdnsTlsConnectEndpoints(freshMdns));
        if (normalizedIp != null && explicitConnectPort is > 0 and <= 65535)
            connectCandidates.Add($"{normalizedIp}:{explicitConnectPort}");
        foreach (AndroidDebugHint hint in _androidHints.Values)
        {
            if (hint.WifiDebug && hint.ConnectPort > 0 && !string.IsNullOrWhiteSpace(hint.PeerIp))
                connectCandidates.Add($"{hint.PeerIp}:{hint.ConnectPort}");
        }

        foreach (string ep in connectCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (cOk, cOut) = await RunAdbAsync(adbPath, _stop.Token, "connect", ep);
            if (cOk && (cOut.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                        cOut.Contains("already connected", StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        await SyncBridgesOnceAsync(adbPath, _stop.Token);
        TriggerRefresh();
        return CurrentShellBridge != null
            ? (true, "Wireless Debugging paired and Virtual Bridge is now ACTIVE!")
            : (true, "Wireless Debugging paired! Connecting Virtual Bridge now...");
    }

    private void ReportBridgeStatus(string state, string detail, int pairPortHint, int connectPortHint)
    {
        var next = new BridgeStatusReport(state, detail, pairPortHint, connectPortHint);
        bool changed = false;
        lock (_shellGate)
        {
            if (_currentStatus != next)
            {
                _currentStatus = next;
                changed = true;
            }
        }
        if (changed)
            _onBridgeStatusChanged?.Invoke(next);
    }

    private async Task RunAsync()
    {
        bool loggedMissingAdb = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                string? adbPath = ResolveRuntimeAdbPath();
                if (adbPath != null)
                {
                    loggedMissingAdb = false;
                    await SyncBridgesOnceAsync(adbPath, _stop.Token);
                }
                else
                {
                    if (!loggedMissingAdb)
                    {
                        loggedMissingAdb = true;
                        _log?.Invoke("Virtual Bridge in standby: adb.exe not found on this Windows PC.");
                    }
                    ReportBridgeStatus("adb_missing",
                        "ADB platform tools missing on Windows; reinstall latest DeskTouchVision-Install.exe.", 0, 0);
                }

                int delayMs = adbPath == null ? 15000 : (_bridgedSerials.Count > 0 || CurrentShellBridge != null ? 3000 : 4500);
                bool signaled = await _wake.WaitAsync(delayMs, _stop.Token);
                if (signaled)
                {
                    // Allow USB enumeration to finish before querying adb devices.
                    await Task.Delay(350, _stop.Token);
                    while (_wake.Wait(0)) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task SyncBridgesOnceAsync(string adbPath, CancellationToken token)
    {
        var (ok, devicesOut) = await RunAdbAsync(adbPath, token, "devices");
        if (!ok) return;

        IReadOnlyList<string> staleOfflineWireless = ParseOfflineWirelessSerials(devicesOut);
        if (staleOfflineWireless.Count > 0)
        {
            foreach (string deadEp in staleOfflineWireless)
                await RunAdbAsync(adbPath, token, "disconnect", deadEp);
            var (reOk, cleanOut) = await RunAdbAsync(adbPath, token, "devices");
            if (reOk) devicesOut = cleanOut;
        }

        var wirelessScan = await TryAutoConnectWirelessDebuggingAsync(adbPath, devicesOut, token);
        if (wirelessScan.AnyConnected)
        {
            var (reOk, refreshedOut) = await RunAdbAsync(adbPath, token, "devices");
            if (reOk) devicesOut = refreshedOut;
        }

        IReadOnlyList<string> wiredSerials = ParseWiredAdbSerials(devicesOut);
        if (wiredSerials.Count > 0)
            ElevateAdbProcesses();
        var present = new HashSet<string>(wiredSerials, StringComparer.Ordinal);
        bool anyDetached = false;
        foreach (string gone in _bridgedSerials.Keys.Where(s => !present.Contains(s)).ToList())
        {
            _bridgedSerials.Remove(gone);
            anyDetached = true;
            _log?.Invoke($"Data cable bridge disconnected for USB device ({gone}).");
        }
        if (anyDetached)
        {
            _onBridgeDetached?.Invoke();
        }

        long nowMs = Environment.TickCount64;
        bool wiredBecameReady = false;
        foreach (string serial in wiredSerials)
        {
            bool newlyAttached = !_bridgedSerials.TryGetValue(serial, out long lastCheckedMs);
            if (!newlyAttached && nowMs - lastCheckedMs < BridgeRecheckIntervalMs)
                continue;

            var (listOk, listOut) = await RunAdbAsync(adbPath, token, "-s", serial, "reverse", "--list");
            if (listOk && HasReverseMappings(listOut, Wire.Port, NearbyComputerDiscovery.DiscoveryPort))
            {
                _bridgedSerials[serial] = nowMs;
                if (newlyAttached)
                {
                    wiredBecameReady = true;
                    _log?.Invoke($"Data cable bridge active for USB device ({serial}) on ports {Wire.Port}/{NearbyComputerDiscovery.DiscoveryPort}.");
                }
                continue;
            }

            var (r1, _) = await RunAdbAsync(adbPath, token,
                "-s", serial, "reverse", $"tcp:{Wire.Port}", $"tcp:{Wire.Port}");
            var (r2, _) = await RunAdbAsync(adbPath, token,
                "-s", serial, "reverse", $"tcp:{NearbyComputerDiscovery.DiscoveryPort}",
                $"tcp:{NearbyComputerDiscovery.DiscoveryPort}");
            if (r1 && r2)
            {
                _bridgedSerials[serial] = nowMs;
                if (newlyAttached)
                {
                    wiredBecameReady = true;
                    _log?.Invoke($"Data cable bridge ready for USB device ({serial}) on ports {Wire.Port}/{NearbyComputerDiscovery.DiscoveryPort}.");
                }
            }
        }

        await SyncShellInputDaemonsAsync(adbPath, devicesOut, nowMs, token);
        PublishDiagnosticState(devicesOut, nowMs, wirelessScan);
        if (wiredBecameReady)
            _onCableBridgeReady?.Invoke();
    }

    private void PublishDiagnosticState(
        string devicesOut, long nowMs, WirelessScanOutcome wirelessScan)
    {
        if (CurrentShellBridge is { } active)
        {
            ReportBridgeStatus("active",
                $"Virtual Bridge active on loopback port {active.Port}.", 0, active.Port);
            return;
        }
        if (ParseAdbUnauthorizedSerials(devicesOut).Count > 0)
        {
            ReportBridgeStatus("usb_unauthorized",
                "Tap 'Allow USB debugging' popup on your phone screen (tick Always allow).", 0, 0);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_lastDaemonError))
        {
            ReportBridgeStatus("daemon_error",
                $"Virtual Bridge start failed: {_lastDaemonError}", 0, 0);
            return;
        }

        bool anyWifiDebug = false;
        bool anyUsbDebug = false;
        int hintPairPort = 0;
        int hintConnectPort = 0;
        foreach (AndroidDebugHint hint in _androidHints.Values)
        {
            if (nowMs - hint.UpdatedMs > HintFreshWindowMs) continue;
            if (hint.WifiDebug) anyWifiDebug = true;
            if (hint.UsbDebug) anyUsbDebug = true;
            if (hint.PairPort > 0) hintPairPort = hint.PairPort;
            if (hint.ConnectPort > 0) hintConnectPort = hint.ConnectPort;
        }

        int bestPairPort = wirelessScan.PairPortHint > 0 ? wirelessScan.PairPortHint : hintPairPort;
        int bestConnectPort = wirelessScan.ConnectPortHint > 0 ? wirelessScan.ConnectPortHint : hintConnectPort;

        if (wirelessScan.ConnectRejectedOrUnpaired || anyWifiDebug || bestConnectPort > 0)
        {
            ReportBridgeStatus("needs_wireless_pair",
                "Wireless Debugging detected — pair once with the 6-digit code in Android Developer options -> Wireless debugging.",
                bestPairPort, bestConnectPort);
            return;
        }
        if (anyUsbDebug)
        {
            ReportBridgeStatus("usb_cable_needed",
                "USB Debugging is enabled — plug in a data cable or turn ON Wireless debugging.", 0, 0);
            return;
        }
        ReportBridgeStatus("debug_off",
            "Standby: plug in USB cable (USB debugging ON) or enable Wireless debugging.", 0, 0);
    }

    private readonly record struct WirelessScanOutcome(
        bool AnyConnected,
        bool ConnectRejectedOrUnpaired,
        int ConnectPortHint,
        int PairPortHint);

    private async Task<WirelessScanOutcome> TryAutoConnectWirelessDebuggingAsync(
        string adbPath, string devicesOut, CancellationToken token)
    {
        IReadOnlyList<string> connectedSerials = ParseConnectedAdbSerials(devicesOut);
        if (connectedSerials.Count > 0)
            return default;

        long nowMs = Environment.TickCount64;
        var candidateEndpoints = new List<string>();
        var seenEp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool forceBecauseFreshHint = false;
        int hintPairPort = 0;

        foreach (AndroidDebugHint hint in _androidHints.Values)
        {
            if (nowMs - hint.UpdatedMs > HintFreshWindowMs) continue;
            if (hint.PairPort > 0) hintPairPort = hint.PairPort;
            if (hint.WifiDebug && hint.ConnectPort > 0 && !string.IsNullOrWhiteSpace(hint.PeerIp))
            {
                string ep = $"{hint.PeerIp}:{hint.ConnectPort}";
                if (seenEp.Add(ep)) candidateEndpoints.Add(ep);
                bool hasBackoff;
                lock (_shellGate) hasBackoff = _wirelessConnectBackoffUntilMs.ContainsKey(ep);
                if (!hasBackoff) forceBecauseFreshHint = true;
            }
        }

        if (!forceBecauseFreshHint && _lastMdnsScanMs > 0 && nowMs - _lastMdnsScanMs < 6000 && candidateEndpoints.Count == 0)
            return new WirelessScanOutcome(false, false, 0, hintPairPort);
        _lastMdnsScanMs = nowMs;

        int mdnsPairPort = 0;
        var (mdnsOk, mdnsOut) = await RunAdbAsync(adbPath, token, "mdns", "services");
        if (mdnsOk)
        {
            foreach (string ep in ParseMdnsTlsConnectEndpoints(mdnsOut))
            {
                if (seenEp.Add(ep)) candidateEndpoints.Add(ep);
            }
            string? pairEp = ParseMdnsTlsPairingEndpoints(mdnsOut).FirstOrDefault();
            mdnsPairPort = ExtractPortFromEndpoint(pairEp);
        }

        int finalPairPort = mdnsPairPort > 0 ? mdnsPairPort : hintPairPort;
        int firstConnectPort = ExtractPortFromEndpoint(candidateEndpoints.FirstOrDefault());

        bool anyConnected = false;
        bool anyRejected = false;
        foreach (string endpoint in candidateEndpoints)
        {
            bool skippedByBackoff;
            lock (_shellGate)
            {
                skippedByBackoff = _wirelessConnectBackoffUntilMs.TryGetValue(endpoint, out long retryAfterMs)
                    && nowMs < retryAfterMs;
            }
            if (skippedByBackoff)
            {
                anyRejected = true;
                continue;
            }
            var (connOk, connOut) = await RunAdbAsync(adbPath, token, "connect", endpoint);
            bool succeeded = connOk &&
                (connOut.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                 connOut.Contains("already connected", StringComparison.OrdinalIgnoreCase)) &&
                !connOut.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) &&
                !connOut.Contains("cannot connect", StringComparison.OrdinalIgnoreCase);
            if (succeeded)
            {
                lock (_shellGate) _wirelessConnectBackoffUntilMs.Remove(endpoint);
                _log?.Invoke($"Wireless debugging auto-connected ({endpoint}); preparing Virtual Bridge.");
                anyConnected = true;
            }
            else
            {
                lock (_shellGate) _wirelessConnectBackoffUntilMs[endpoint] = nowMs + WirelessConnectRetryMs;
                anyRejected = true;
            }
        }
        return new WirelessScanOutcome(anyConnected, anyRejected, firstConnectPort, finalPairPort);
    }

    private async Task SyncShellInputDaemonsAsync(
        string adbPath, string devicesOut, long nowMs, CancellationToken token)
    {
        IReadOnlyList<string> connectedSerials = ParseConnectedAdbSerials(devicesOut);
        var present = new HashSet<string>(connectedSerials, StringComparer.Ordinal);
        foreach (string gone in _shellBridges.Keys.Where(s => !present.Contains(s)).ToList())
        {
            _shellBridges.Remove(gone);
            _loggedMissingAppSerials.Remove(gone);
        }
        if (_shellBridges.Count == 0)
        {
            lock (_shellGate) _currentShellBridge = null;
        }
        if (connectedSerials.Count == 0)
        {
            _lastDaemonError = null;
        }

        foreach (string serial in connectedSerials)
        {
            bool hasKnown = _shellBridges.TryGetValue(serial, out var existing);
            // Avoid polling `pm path` and spawning heavy binder queries every 15s
            // while the Virtual Bridge daemon is already confirmed alive.
            if (hasKnown && nowMs - existing.LastCheckedMs < HealthyShellVerifyIntervalMs)
                continue;

            int expectedPort = hasKnown ? existing.Port : 0;
            var (checkOk, checkOut) = await RunAdbAsync(
                adbPath, token, "-s", serial, "shell", BuildShellCheckScript(expectedPort));
            if (!checkOk) continue;

            if (checkOut.Contains("NO_APP", StringComparison.Ordinal))
            {
                if (_loggedMissingAppSerials.Add(serial))
                    _log?.Invoke($"Virtual Bridge waiting: DeskTouch Vision app is not installed yet on device ({serial}).");
                continue;
            }
            _loggedMissingAppSerials.Remove(serial);

            if (hasKnown && ParsePortOutput(checkOut) == existing.Port)
            {
                _shellBridges[serial] = (existing.Port, existing.Secret, nowMs);
                bool restored = false;
                lock (_shellGate)
                {
                    if (_currentShellBridge == null)
                    {
                        _currentShellBridge = (existing.Port, existing.Secret);
                        restored = true;
                    }
                }
                if (restored)
                    _onShellBridgeReady?.Invoke(existing.Port, existing.Secret);
                continue;
            }

            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var (launchOk, launchOut) = await RunAdbWithStdinAsync(
                adbPath, secret + "\n", token, includeStdErr: false, "-s", serial, "shell", BuildShellDaemonScript());
            if (!launchOk) continue;

            int port = ParsePortOutput(launchOut);
            if (port <= 0)
            {
                string? errDetail = ExtractShellFailureReason(launchOut);
                _lastDaemonError = errDetail ?? "no loopback port reported";
                string errKey = $"{serial}:{_lastDaemonError}";
                if (_loggedDaemonErrors.Add(errKey))
                    _log?.Invoke($"Virtual Bridge launch on device ({serial}) failed: {_lastDaemonError}.");
                continue;
            }

            _lastDaemonError = null;
            _loggedDaemonErrors.Clear();
            _shellBridges[serial] = (port, secret, nowMs);
            lock (_shellGate)
                _currentShellBridge = (port, secret);
            _log?.Invoke($"Accessibility-free Virtual Bridge ready on device ({serial}) loopback port {port}.");
            _onShellBridgeReady?.Invoke(port, secret);
        }
    }

    private static Task<(bool Ok, string StdOut)> RunAdbAsync(
        string adbPath, CancellationToken token, params string[] arguments) =>
        RunAdbWithStdinAsync(adbPath, null, token, includeStdErr: false, arguments);

    private static async Task<(bool Ok, string StdOut)> RunAdbWithStdinAsync(
        string adbPath, string? stdInText, CancellationToken token, bool includeStdErr, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    // Start adb from its own folder so a cold-started adb server never
                    // holds a directory lock on DeskTouchVision during in-place updates.
                    WorkingDirectory = Path.GetDirectoryName(adbPath) ?? Environment.SystemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdInText != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.Environment["ADB_MDNS_OPENSCREEN"] = "1";
            foreach (string arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start())
                return (false, string.Empty);

            if (stdInText != null)
            {
                byte[] inputBytes = Encoding.ASCII.GetBytes(stdInText);
                await process.StandardInput.BaseStream.WriteAsync(inputBytes, token);
                await process.StandardInput.BaseStream.FlushAsync(token);
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(12000);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string combined = includeStdErr && !string.IsNullOrWhiteSpace(stderr)
                    ? stdout + "\n" + stderr
                    : stdout;
                return (process.ExitCode == 0, combined);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Kill only this client process so a spawning adb server is not killed mid-boot.
                try { process.Kill(); } catch (Exception) { }
                return (false, string.Empty);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return (false, string.Empty);
        }
    }

    private static void ElevateAdbProcesses()
    {
        try
        {
            foreach (Process proc in Process.GetProcessesByName("adb"))
            {
                try
                {
                    if (proc.PriorityClass is ProcessPriorityClass.Normal
                        or ProcessPriorityClass.BelowNormal
                        or ProcessPriorityClass.Idle)
                    {
                        proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                }
                catch (Exception) { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _wake.Release(); } catch (Exception) { }
        try { _wake.Dispose(); } catch (Exception) { }
        _stop.Dispose();
    }
}
