using System.Diagnostics;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Automatically configures localhost USB data-cable forwarding (`adb reverse`)
/// for discovery (TCP 37378) and TLS control/input (TCP 37377) whenever an
/// authorized Android device is attached over USB. Fails closed and never
/// bridges wireless ADB targets or alters firewall/pairing rules.
/// </summary>
internal sealed class AdbCableBridge : IDisposable
{
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, long> _bridgedSerials = new(StringComparer.Ordinal);
    private readonly Task _loop;
    private int _disposed;

    internal AdbCableBridge(Action<string>? log = null)
    {
        _log = log;
        _loop = Task.Run(RunAsync);
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

    internal static IReadOnlyList<string> ParseWiredAdbSerials(string adbDevicesOutput)
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
            // Never install a reverse loop over wireless ADB (ip:port or mDNS TLS).
            if (serial.Contains(':') ||
                serial.Contains("_adb-tls", StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add(serial))
                result.Add(serial);
        }
        return result;
    }

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
        var present = new HashSet<string>(wiredSerials, StringComparer.Ordinal);
        foreach (string gone in _bridgedSerials.Keys.Where(s => !present.Contains(s)).ToList())
        {
            _bridgedSerials.Remove(gone);
            _log?.Invoke($"Data cable bridge disconnected for USB device ({gone}).");
        }

        long nowMs = Environment.TickCount64;
        foreach (string serial in wiredSerials)
        {
            bool newlyAttached = !_bridgedSerials.TryGetValue(serial, out long lastCheckedMs);
            if (!newlyAttached && nowMs - lastCheckedMs < 15000)
                continue;

            var (listOk, listOut) = await RunAdbAsync(adbPath, token, "-s", serial, "reverse", "--list");
            if (listOk && HasReverseMappings(listOut, Wire.Port, NearbyComputerDiscovery.DiscoveryPort))
            {
                _bridgedSerials[serial] = nowMs;
                if (newlyAttached)
                    _log?.Invoke($"Data cable bridge active for USB device ({serial}) on ports {Wire.Port}/{NearbyComputerDiscovery.DiscoveryPort}.");
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
                    _log?.Invoke($"Data cable bridge ready for USB device ({serial}) on ports {Wire.Port}/{NearbyComputerDiscovery.DiscoveryPort}.");
            }
        }
    }

    private static async Task<(bool Ok, string StdOut)> RunAdbAsync(
        string adbPath, CancellationToken token, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (string arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start())
                return (false, string.Empty);

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _wake.Release(); } catch (Exception) { }
        try { _wake.Dispose(); } catch (Exception) { }
        _stop.Dispose();
    }
}
