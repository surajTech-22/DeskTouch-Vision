using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeskTouchVision;

/// <summary>
/// Release-driven updates for the future separate DeskTouch-Vision repository.
/// The application works fully offline; only this optional updater contacts
/// GitHub/ntfy. Releases are downloaded ONLY from the configured official repo,
/// checked against a SHA-256 asset, and staged outside the running app.
/// The repo and release must exist before this is operational.
/// </summary>
internal static class UpdateClient
{
    private const string Owner = "surajTech-22";
    private const string Repo = "DeskTouch-Vision";
    private const string Topic = "desktouch-vision-surajtech22-qrp9f4x2";
    private const string ArchiveName = "DeskTouchVision-win-x64.zip";
    private const string ChecksumName = "DeskTouchVision-win-x64.zip.sha256";
    private static readonly Version CurrentVersion = new(0, 1, 0);
    private static readonly HttpClient Http = MakeHttp();
    private static int _updateStarted;

    private static HttpClient MakeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskTouchVision", "0.1.0"));
        return c;
    }

    internal static void StartEventUpdates(Form window)
    {
        _ = Task.Run(async () =>
        {
            // One initial check. There is NO interval polling for versions.
            try { await TryInstallNewVersion(window, false); } catch { }
            // Release notifications are events over SSE, not an hourly polling loop.
            while (!window.IsDisposed)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://ntfy.sh/{Topic}/sse");
                    using var response = await Http.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    while (!reader.EndOfStream && !window.IsDisposed)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var notice = JsonDocument.Parse(line[6..]);
                            if (notice.RootElement.TryGetProperty("message", out var msg) &&
                                msg.GetString()?.StartsWith("desktouch-release:", StringComparison.Ordinal) == true)
                                await TryInstallNewVersion(window, false);
                        }
                        catch (JsonException) { }
                    }
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        });
    }

    internal static async Task CheckManuallyAsync(Form window)
    {
        try
        {
            bool updated = await TryInstallNewVersion(window, true);
            if (!updated && !window.IsDisposed)
                window.BeginInvoke(() => MessageBox.Show(window,
                    "No newer published DeskTouch release is available, or the new repository has no releases yet.",
                    "DeskTouch updates", MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (Exception ex)
        {
            if (!window.IsDisposed)
                window.BeginInvoke(() => MessageBox.Show(window,
                    "Update check unavailable: " + ex.Message + "\n\nCamera features still work offline.",
                    "DeskTouch updates", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    private static async Task<bool> TryInstallNewVersion(Form window, bool manual)
    {
        if (window.IsDisposed || Interlocked.CompareExchange(ref _updateStarted, 0, 0) != 0) return false;
        var uri = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var res = await Http.GetAsync(uri);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        res.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = json.RootElement;
        string? tag = root.GetProperty("tag_name").GetString();
        if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var releaseVersion) ||
            releaseVersion <= CurrentVersion) return false;
        string? archiveUrl = null, checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var link = asset.GetProperty("browser_download_url").GetString();
            if (name == ArchiveName) archiveUrl = link;
            if (name == ChecksumName) checksumUrl = link;
        }
        if (archiveUrl == null || checksumUrl == null) return false;
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(checksumUrl, UriKind.Absolute, out var b) ||
            !string.Equals(a.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(b.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !a.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase) ||
            !b.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected release asset URL.");
        if (Interlocked.CompareExchange(ref _updateStarted, 1, 0) != 0) return false;
        try
        {
            // Download to a new temporary folder to avoid overwriting the running app.
            string rootTemp = Path.Combine(Path.GetTempPath(), "DeskTouchVision-Update", releaseVersion.ToString());
            Directory.CreateDirectory(rootTemp);
            string zipPath = Path.Combine(rootTemp, ArchiveName);
            string hashText = (await Http.GetStringAsync(checksumUrl)).Trim();
            var hash = hashText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid checksum file.");
            await using (var target = File.Create(zipPath))
            await using (var source = await Http.GetStreamAsync(archiveUrl))
                await source.CopyToAsync(target);
            string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath)));
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release archive failed SHA-256 verification.");
            string dir = AppContext.BaseDirectory;
            string script = Path.Combine(dir, "Update-DeskTouch.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Updater not installed", script);
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                "-File", script, "-Package", zipPath, "-Target", dir,
                "-CurrentPid", Environment.ProcessId.ToString(), "-ExpectedHash", hash })
                psi.ArgumentList.Add(argument);
            Process.Start(psi);
            if (!window.IsDisposed)
            {
                window.BeginInvoke(() =>
                {
                    if (!window.IsDisposed)
                    {
                        window.Close(); // Releases webcam and exits app; updater completes install.
                    }
                });
            }
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _updateStarted, 0);
            if (!manual) return false;
            throw;
        }
    }
}
