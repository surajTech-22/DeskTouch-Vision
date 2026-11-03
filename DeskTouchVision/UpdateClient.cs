using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace DeskTouchVision;

/// <summary>
/// A window can stay alive in the tray during ordinary user Close, but an
/// independently verified installer must ask it to release the existing TLS
/// peer and input hooks before files are replaced. Both Windows applications
/// use the same update engine and the same independent installed package.
/// </summary>
internal interface IUpdateCloseHost
{
    bool IsHiddenInTray { get; }
    void CloseForVerifiedUpdate();
}

/// <summary>
/// Independent release-event updater for both the DeskTouch launcher and the
/// Windows Displays application. Avoids false "up to date" when a background
/// download is already running, logs failures, and verifies release archives.
/// </summary>
internal static class UpdateClient
{
    private const string Owner = "surajTech-22";
    private const string Repo = "DeskTouch-Vision";
    private const string Topic = "desktouch-vision-surajtech22-qrp9f4x2";
    private const string ArchiveName = "DeskTouchVision-win-x64.zip";
    private const string ChecksumName = ArchiveName + ".sha256";
    private static readonly Version CurrentVersion =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 2, 2, 0);
    private static readonly HttpClient Downloads = MakeHttp(false);
    private static readonly HttpClient Events = MakeHttp(true);
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
    private static int _started;

    private enum CheckResult { UpToDate, NoReleases, InstallationStarted, InstallationBusy }

    // Probed synchronously. Mutex ownership is thread-affine: NEVER await
    // between WaitOne and ReleaseMutex; that would strand the lock and cause
    // subsequent DeskTouch upgrades to be rejected as duplicates.
    private static bool IsInstallationRunning()
    {
        using var gate = new Mutex(false, @"Local\DeskTouchVision.Installer");
        bool acquired = false;
        try
        {
            acquired = gate.WaitOne(0);
            return !acquired;
        }
        catch (AbandonedMutexException)
        {
            // Windows automatically releases mutexes when the owner dies.
            // This attempt now owns the abandoned mutex; unlock it below.
            acquired = true;
            return false;
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
        }
    }

    private static HttpClient MakeHttp(bool stream)
    {
        var client = new HttpClient
        {
            Timeout = stream ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(12)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DeskTouchVision", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static void Log(string message)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskTouchVision", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "update-check.log"),
                DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch { /* Failed logging must never affect local Windows input. */ }
    }

    private static void Post(Form window, Action action)
    {
        try
        {
            if (!window.IsDisposed && window.IsHandleCreated)
                window.BeginInvoke(() => { if (!window.IsDisposed) action(); });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static void SetStatus(Form window, string status)
    {
        Post(window, () => window.Text = "DeskTouch Vision — " + status);
    }

    internal static void StartEventUpdates(Form window)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var lifecycle = new CancellationTokenSource();
        window.FormClosed += (_, _) => lifecycle.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                try { await CheckForUpdatesAsync(window, manual: false, lifecycle.Token); }
                catch (Exception ex) { Log("Automatic startup update failed: " + ex.Message); }

                // The local input-sharing program works even if this SSE stream
                // is unreachable. No interval-based version polling is used.
                while (!lifecycle.IsCancellationRequested)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get,
                            $"https://ntfy.sh/{Topic}/sse");
                        using var response = await Events.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, lifecycle.Token);
                        response.EnsureSuccessStatusCode();
                        using var stream = await response.Content.ReadAsStreamAsync(lifecycle.Token);
                        using var reader = new StreamReader(stream);
                        while (!lifecycle.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(lifecycle.Token);
                            if (line == null) break;
                            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                            try
                            {
                                using var msg = JsonDocument.Parse(line[6..]);
                                if (msg.RootElement.TryGetProperty("message", out var text) &&
                                    text.GetString()?.StartsWith("desktouch-release:", StringComparison.Ordinal) == true)
                                    await CheckForUpdatesAsync(window, manual: false, lifecycle.Token);
                            }
                            catch (JsonException) { /* unrelated notifications */ }
                            catch (Exception ex) { Log("Release notification update: " + ex.Message); }
                        }
                    }
                    catch (OperationCanceledException) when (lifecycle.IsCancellationRequested) { break; }
                    catch (Exception ex) { Log("Release event stream disconnected: " + ex.Message); }
                    await Task.Delay(TimeSpan.FromSeconds(15), lifecycle.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { lifecycle.Dispose(); }
        });
    }

    internal static async Task CheckManuallyAsync(Form window)
    {
        if (window.IsDisposed) return;
        SetStatus(window, "checking GitHub releases");
        try
        {
            // Manual requests WAIT for the automatic startup check/download to
            // finish; they never mistake an in-progress update for "no release".
            var result = await CheckForUpdatesAsync(window, manual: true, CancellationToken.None);
            if (window.IsDisposed || result == CheckResult.InstallationStarted) return;
            string message = result switch
            {
                CheckResult.NoReleases =>
                    "No published releases were returned by the official DeskTouch repository.",
                CheckResult.InstallationBusy =>
                    "Another verified DeskTouch installer is active. This check did not download another copy or close your Displays window. Check the updater log if the installer remains busy.",
                _ => $"This installation is already current (v{CurrentVersion.ToString(3)})."
            };
            SetStatus(window, "Displays");
            Post(window, () => MessageBox.Show(window, message, "DeskTouch updates",
                MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (Exception ex)
        {
            SetStatus(window, "Displays — update check failed");
            Log("Manual update failed: " + ex);
            Post(window, () => MessageBox.Show(window,
                "The update could not be checked or installed.\n\n" + ex.Message +
                "\n\nDiagnostics: %LOCALAPPDATA%\\DeskTouchVision\\logs\\update-check.log" +
                "\nInstaller: https://github.com/surajTech-22/DeskTouch-Vision/releases/latest",
                "DeskTouch updates", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    private static async Task<CheckResult> CheckForUpdatesAsync(
        Form window, bool manual, CancellationToken cancellation)
    {
        // Automatic events never start a duplicate update in progress.
        if (!manual && !await UpdateGate.WaitAsync(0, cancellation))
            return CheckResult.UpToDate;
        if (manual) await UpdateGate.WaitAsync(cancellation);
        try
        {
            if (window.IsDisposed) return CheckResult.UpToDate;
            using var response = await Downloads.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return CheckResult.NoReleases;
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var latest))
                throw new InvalidDataException("GitHub returned an invalid DeskTouch release version.");
            if (latest <= CurrentVersion) return CheckResult.UpToDate;
            if (IsInstallationRunning())
            {
                Log($"Installer lock already owned; skipping redundant download for v{latest.ToString(3)}.");
                return CheckResult.InstallationBusy;
            }

            string? archiveUrl = null, checksumUrl = null;
            foreach (var item in root.GetProperty("assets").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                if (name == ArchiveName)
                    archiveUrl = item.GetProperty("browser_download_url").GetString();
                if (name == ChecksumName)
                    checksumUrl = item.GetProperty("browser_download_url").GetString();
            }
            if (archiveUrl == null || checksumUrl == null)
                throw new InvalidDataException($"Release v{latest} does not contain the required installer and checksum.");
            ValidateReleaseUrl(archiveUrl);
            ValidateReleaseUrl(checksumUrl);

            string installedRoot = AppContext.BaseDirectory;
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(installedRoot))
                .Equals("DeviceSharing", StringComparison.OrdinalIgnoreCase))
                installedRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(installedRoot))!.FullName;
            string expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "DeskTouchVision");
            if (!Path.GetFullPath(installedRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(expectedRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "DeskTouch is running from an extracted or nonstandard folder. Run INSTALL_DESKTOUCH.cmd from the release once to install updates properly.");
            string updater = Path.Combine(installedRoot, "Update-DeskTouch.ps1");
            if (!File.Exists(updater))
                throw new FileNotFoundException("Installed updater script missing; reinstall DeskTouch from GitHub.", updater);

            string downloadFolder = Path.Combine(Path.GetTempPath(), "DeskTouchVision-Update",
                latest.ToString() + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(downloadFolder);
            string archivePath = Path.Combine(downloadFolder, ArchiveName);

            try
            {
                SetStatus(window, $"downloading verified update v{latest.ToString(3)}");
                Log($"Update available: installed={CurrentVersion.ToString(3)}, release={latest.ToString(3)}");
                string checksumText = (await Downloads.GetStringAsync(checksumUrl, cancellation)).Trim();
                string? expectedHash = checksumText.Split(
                    [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (expectedHash == null || expectedHash.Length != 64 ||
                    !expectedHash.All(Uri.IsHexDigit) ||
                    !checksumText.Contains(ArchiveName, StringComparison.Ordinal))
                    throw new InvalidDataException("The release SHA-256 checksum file is malformed.");

                await using (var destination = new FileStream(
                    archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await using (var source = await Downloads.GetStreamAsync(archiveUrl, cancellation))
                    await source.CopyToAsync(destination, cancellation);

                await using var hashStream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                string actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(hashStream, cancellation));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded DeskTouch archive failed SHA-256 verification.");

                // CRITICAL v0.2.4: PowerShell inherits the child's current
                // working directory. When that directory is INSIDE the app
                // being updated, Windows prevents Move-Item from renaming it.
                // Copy the updater to TEMP and explicitly start it outside
                // the installed directory; neither its script nor its CWD
                // may retain a lock on DeskTouchVision/DeviceSharing.
                string detachedUpdater = Path.Combine(downloadFolder,
                    "Update-DeskTouch.ps1");
                File.Copy(updater, detachedUpdater, overwrite: false);
                string safeWorkingDirectory = Path.GetFullPath(downloadFolder);
                if (safeWorkingDirectory.StartsWith(
                    Path.GetFullPath(expectedRoot).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Updater working directory is inside the app folder.");

                var process = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = safeWorkingDirectory
                };
                foreach (var arg in new[] {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                    "-File", detachedUpdater, "-Package", archivePath,
                    "-Target", expectedRoot,
                    "-CurrentPid", Environment.ProcessId.ToString(),
                    "-ExpectedHash", expectedHash })
                    process.ArgumentList.Add(arg);
                // Each machine independently follows the GitHub release SSE
                // while running in the tray. On successful auto-update,
                // restart in that same quiet tray state instead of popping a
                // new desktop window onto either computer.
                if (window is IUpdateCloseHost host && host.IsHiddenInTray)
                    process.ArgumentList.Add("-StartInTray");
                if (IsInstallationRunning())
                {
                    Log($"Another installer acquired the lock while v{latest.ToString(3)} was downloading; no second host launched.");
                    try { Directory.Delete(downloadFolder, true); } catch { }
                    return CheckResult.InstallationBusy;
                }
                if (Process.Start(process) == null)
                    throw new InvalidOperationException("Unable to start the independent update installer.");
                Log("Checksum verified; detached TEMP updater launched outside installed directory. Closing old DeskTouch window.");
                SetStatus(window, "installing update");
                Post(window, () =>
                {
                    if (window is IUpdateCloseHost sharingHost)
                        sharingHost.CloseForVerifiedUpdate();
                    else window.Close();
                });
                return CheckResult.InstallationStarted;
            }
            catch
            {
                try { Directory.Delete(downloadFolder, true); } catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            Log("Update check failed: " + ex.Message);
            if (manual) throw;
            return CheckResult.UpToDate; // background error is logged, never shown as "current"
        }
        finally { UpdateGate.Release(); }
    }

    private static void ValidateReleaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A release download URL was not hosted in the official DeskTouch repository.");
    }
}
