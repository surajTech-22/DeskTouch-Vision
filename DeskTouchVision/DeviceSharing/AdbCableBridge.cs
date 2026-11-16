using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Automatically configures localhost USB data-cable forwarding (`adb reverse`)
/// for discovery (TCP 37378) and TLS control/input (TCP 37377), and launches
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
    private const long BridgeRecheckIntervalMs = 12000;
    private const long HealthyShellVerifyIntervalMs = 45000;

    private readonly Action<string>? _log;
    private readonly Action<int, string>? _onShellBridgeReady;
    private readonly Action? _onBridgeDetached;
    private readonly Action? _onCableBridgeReady;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, long> _bridgedSerials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Port, string Secret, long LastCheckedMs)> _shellBridges =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedMissingAppSerials = new(StringComparer.Ordinal);
    private readonly object _shellGate = new();
    private (int Port, string Secret)? _currentShellBridge;
    private readonly Task _loop;
    private int _disposed;

    internal AdbCableBridge(
        Action<string>? log = null,
        Action<int, string>? onShellBridgeReady = null,
        Action? onBridgeDetached = null,
        Action? onCableBridgeReady = null)
    {
        _log = log;
        _onShellBridgeReady = onShellBridgeReady;
        _onBridgeDetached = onBridgeDetached;
        _onCableBridgeReady = onCableBridgeReady;
        _loop = Task.Run(RunAsync);
    }

    internal (int Port, string Secret)? CurrentShellBridge
    {
        get { lock (_shellGate) return _currentShellBridge; }
    }

    internal void TriggerRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
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
            candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));

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

    internal static IReadOnlyList<string> ParseWiredAdbSerials(string adbDevicesOutput) =>
        ParseAdbSerials(adbDevicesOutput, wiredOnly: true);

    internal static IReadOnlyList<string> ParseConnectedAdbSerials(string adbDevicesOutput) =>
        ParseAdbSerials(adbDevicesOutput, wiredOnly: false);

    private static IReadOnlyList<string> ParseAdbSerials(string adbDevicesOutput, bool wiredOnly)
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
            if (parts.Length < 2 || !string.Equals(parts[1], "device", StringComparison.Ordinal))
                continue;

            string serial = parts[0];
            if (wiredOnly && (serial.Contains(':') ||
                serial.Contains("_adb-tls", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (seen.Add(serial))
                result.Add(serial);
        }
        return result;
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
        $"rm -f {ShellPortFile} {ShellPidFile} {ShellTokenFile} {ShellApkFile}; " +
        $"APK=$(pm path {AndroidAppPackage} 2>/dev/null | head -n 1 | cut -d: -f2 | tr -d '\\r'); " +
        "[ -n \"$APK\" ] || { echo NO_APP; exit 0; }; " +
        "IFS= read -r TK || exit 2; " +
        $"printf '%s\\n' \"$TK\" > {ShellTokenFile}; unset TK; " +
        $"printf '%s\\n' \"$APK\" > {ShellApkFile}; " +
        $"{{ exec < {ShellTokenFile}; rm -f {ShellTokenFile}; CLASSPATH=\"$APK\" exec setsid /system/bin/app_process /system/bin com.desktouch.vision.ShellInputDaemon --pid-file {ShellPidFile} --port-file {ShellPortFile} >/dev/null 2>&1; }} </dev/null >/dev/null 2>&1 & " +
        "i=0; while [ \"$i\" -lt 40 ]; do " +
        $"[ -s {ShellPortFile} ] && {{ P=$(cat {ShellPortFile} 2>/dev/null | tr -d '\\r\\n '); [ -n \"$P\" ] && {{ echo \"PORT=$P\"; exit 0; }}; }}; " +
        "sleep 0.05 2>/dev/null || sleep 1; i=$((i+1)); done; echo NO_PORT";

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

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                string? adbPath = ResolveAdbPath();
                if (adbPath != null)
                {
                    await SyncBridgesOnceAsync(adbPath, _stop.Token);
                }

                int delayMs = adbPath == null ? 15000 : (_bridgedSerials.Count > 0 ? 3000 : 4500);
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
        if (wiredBecameReady)
            _onCableBridgeReady?.Invoke();
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

        foreach (string serial in connectedSerials)
        {
            bool hasKnown = _shellBridges.TryGetValue(serial, out var existing);
            // Avoid polling `pm path` and spawning heavy binder queries every 15s
            // over USB while the Virtual Bridge daemon is already confirmed alive.
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
                adbPath, secret + "\n", token, "-s", serial, "shell", BuildShellDaemonScript());
            if (!launchOk) continue;

            int port = ParsePortOutput(launchOut);
            if (port <= 0)
            {
                _log?.Invoke($"Virtual Bridge launch on device ({serial}) did not report a loopback port.");
                continue;
            }

            _shellBridges[serial] = (port, secret, nowMs);
            lock (_shellGate)
                _currentShellBridge = (port, secret);
            _log?.Invoke($"Accessibility-free Virtual Bridge ready on device ({serial}) loopback port {port}.");
            _onShellBridgeReady?.Invoke(port, secret);
        }
    }

    private static Task<(bool Ok, string StdOut)> RunAdbAsync(
        string adbPath, CancellationToken token, params string[] arguments) =>
        RunAdbWithStdinAsync(adbPath, null, token, arguments);

    private static async Task<(bool Ok, string StdOut)> RunAdbWithStdinAsync(
        string adbPath, string? stdInText, CancellationToken token, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    // Start adb from its own SDK folder so a cold-started adb server never
                    // holds a directory lock on DeskTouchVision during in-place updates.
                    WorkingDirectory = Path.GetDirectoryName(adbPath) ?? Environment.SystemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdInText != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
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
            // Cold-starting the adb server on Windows can take ~5s before `devices` returns.
            timeoutCts.CancelAfter(10000);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                string stdout = await stdoutTask;
                _ = await stderrTask;
                return (process.ExitCode == 0, stdout);
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
