using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using DeskTouchVision;
using QRCoder;

namespace DeskTouch.DeviceShare;

internal sealed record RepairReport(
    int StoppedStaleUpdaters,
    int StoppedDuplicateProcesses,
    int CleanedDebrisFolders,
    bool FirewallApplied,
    string FirewallNote);

internal static class BackgroundAndNetworkRepairHelper
{
    internal static RepairReport RunSelfRepair(string? allowLanScriptPath = null)
    {
        int stoppedUpdaters = 0;
        int stoppedDupes = 0;
        int cleanedFolders = 0;
        bool firewallApplied = false;
        string firewallNote = "";

        int currentPid = Environment.ProcessId;
        DateTime updaterCutoff = DateTime.UtcNow.AddSeconds(-15);

        // 1. Clean up stuck Update-DeskTouch.ps1 PowerShell processes so updates don't deadlock.
        try
        {
            foreach (int pid in FindStaleUpdaterPids(updaterCutoff))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: false);
                        p.WaitForExit(1500);
                        stoppedUpdaters++;
                    }
                }
                catch { }
            }
        }
        catch { }

        // 2. Clean up duplicate/orphaned DeskTouch processes in the same session.
        int mySession = Process.GetCurrentProcess().SessionId;
        foreach (string procName in new[] { "DeskTouchVision", "DeskTouch.DeviceShare" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        if (p.Id == currentPid || p.SessionId != mySession || p.HasExited) continue;
                        if (procName == "DeskTouch.DeviceShare")
                        {
                            _ = p.CloseMainWindow();
                            if (!p.WaitForExit(1200))
                                p.Kill(entireProcessTree: false);
                        }
                        else
                        {
                            p.Kill(entireProcessTree: false);
                        }
                        stoppedDupes++;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        // 3. Remove leftover backup and old temp update folders.
        try
        {
            string programsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs");
            if (Directory.Exists(programsDir))
            {
                foreach (string dir in Directory.GetDirectories(programsDir, "DeskTouchVision.backup-*"))
                {
                    try { Directory.Delete(dir, true); cleanedFolders++; } catch { }
                }
            }
        }
        catch { }

        try
        {
            string tempUpdate = Path.Combine(Path.GetTempPath(), "DeskTouchVision-Update");
            if (Directory.Exists(tempUpdate))
            {
                foreach (string sub in Directory.GetDirectories(tempUpdate))
                {
                    try
                    {
                        if (Directory.GetCreationTimeUtc(sub) < DateTime.UtcNow.AddMinutes(-5))
                        {
                            Directory.Delete(sub, true);
                            cleanedFolders++;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 4. Refresh Windows Private Network Firewall rule if ALLOW_PRIVATE_LAN.ps1 is present.
        string script = !string.IsNullOrWhiteSpace(allowLanScriptPath)
            ? allowLanScriptPath
            : Path.Combine(AppContext.BaseDirectory, "ALLOW_PRIVATE_LAN.ps1");
        if (File.Exists(script))
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\""
                });
                proc?.WaitForExit(12000);
                firewallApplied = proc != null && proc.ExitCode == 0;
                firewallNote = firewallApplied
                    ? "Private network firewall rules refreshed."
                    : "Firewall prompt closed before completing.";
            }
            catch (Exception ex)
            {
                firewallNote = "Firewall step skipped: " + ex.Message;
            }
        }
        else
        {
            firewallNote = "Network discovery & listeners refreshed.";
        }

        return new RepairReport(stoppedUpdaters, stoppedDupes, cleanedFolders, firewallApplied, firewallNote);
    }

    private static List<int> FindStaleUpdaterPids(DateTime utcCutoff)
    {
        var list = new List<int>();
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='powershell.exe'\\\" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'Update-DeskTouch[.]ps1' } | ForEach-Object { $_.ProcessId }\"",
            });
            if (proc == null) return list;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3500);
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), out int pid))
                {
                    try
                    {
                        using var target = Process.GetProcessById(pid);
                        if (!target.HasExited && target.StartTime.ToUniversalTime() <= utcCutoff)
                            list.Add(pid);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return list;
    }
}

internal sealed class PairingQrDialog : Form
{
    private readonly ComboBox _adapterPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly PictureBox _qrBox = new();
    private readonly Label _codeLabel = new();
    private readonly Label _linkInfo = new();
    private readonly string _fingerprint;
    private readonly string _secret;
    private readonly string _shortCode;
    private string _currentPayload = "";

    internal PairingQrDialog(string fingerprint, string secret, string shortCode)
    {
        _fingerprint = fingerprint;
        _secret = secret;
        _shortCode = shortCode;

        Text = "DeskTouch Vision — Scan QR to Pair";
        ClientSize = new Size(440, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(17, 23, 34);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "Quick-Scan Pairing Code",
            Font = new Font("Segoe UI Semibold", 17),
            Dock = DockStyle.Top,
            Height = 46,
            TextAlign = ContentAlignment.MiddleCenter
        };
        var sub = new Label
        {
            Text = "Open DeskTouch Vision on your Android phone, tap Scan QR, and point at this square.",
            ForeColor = Color.FromArgb(186, 200, 220),
            Dock = DockStyle.Top,
            Height = 42,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(16, 0, 16, 4)
        };

        var adapterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(44, 4, 16, 4)
        };
        var ipLbl = new Label { Text = "Network IP:", AutoSize = true, Margin = new Padding(0, 5, 8, 0), ForeColor = Color.FromArgb(186, 200, 220) };
        var ips = GetCandidateIPv4Addresses();
        foreach (string ip in ips)
            _adapterPicker.Items.Add(ip);
        if (_adapterPicker.Items.Count > 0) _adapterPicker.SelectedIndex = 0;
        _adapterPicker.SelectedIndexChanged += (_, _) => RenderQr();
        adapterRow.Controls.Add(ipLbl);
        adapterRow.Controls.Add(_adapterPicker);

        _qrBox.Dock = DockStyle.Top;
        _qrBox.Height = 290;
        _qrBox.BackColor = Color.White;
        _qrBox.Padding = new Padding(14);
        _qrBox.SizeMode = PictureBoxSizeMode.Zoom;

        _codeLabel.Dock = DockStyle.Top;
        _codeLabel.Height = 42;
        _codeLabel.Font = new Font("Consolas", 17, FontStyle.Bold);
        _codeLabel.ForeColor = Color.FromArgb(56, 189, 248);
        _codeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _codeLabel.Text = string.IsNullOrWhiteSpace(shortCode) ? "Scan with phone camera" : $"Code: {shortCode}";

        _linkInfo.Dock = DockStyle.Top;
        _linkInfo.Height = 30;
        _linkInfo.ForeColor = Color.FromArgb(148, 163, 184);
        _linkInfo.TextAlign = ContentAlignment.MiddleCenter;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(70, 8, 20, 8)
        };
        var copyLinkBtn = MakeBtn("Copy Pairing Link", true);
        copyLinkBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_currentPayload))
            {
                Clipboard.SetText(_currentPayload);
                _linkInfo.Text = "Full pairing link copied to clipboard.";
            }
        };
        var copyCodeBtn = MakeBtn("Copy Short Code", false);
        copyCodeBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_shortCode))
            {
                Clipboard.SetText(_shortCode);
                _linkInfo.Text = $"Code {_shortCode} copied to clipboard.";
            }
        };
        buttons.Controls.Add(copyLinkBtn);
        buttons.Controls.Add(copyCodeBtn);

        Controls.Add(buttons);
        Controls.Add(_linkInfo);
        Controls.Add(_codeLabel);
        Controls.Add(_qrBox);
        Controls.Add(adapterRow);
        Controls.Add(sub);
        Controls.Add(title);

        FormClosed += (_, _) => _qrBox.Image?.Dispose();
        RenderQr();
    }

    internal static string BuildDirectQrPayload(string ip, string fingerprint, string secret) =>
        $"DT1|{ip.Trim()}|{fingerprint.Trim().ToUpperInvariant()}|{secret.Trim().ToUpperInvariant()}";

    internal static string[] GetCandidateIPv4Addresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
                .Select(x => x.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(ip => ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void RenderQr()
    {
        string? ip = _adapterPicker.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(ip))
        {
            _currentPayload = "";
            _linkInfo.Text = "Connect this computer to a wireless network or Ethernet first.";
            return;
        }
        _currentPayload = BuildDirectQrPayload(ip, _fingerprint, _secret);
        try
        {
            using var code = QRCodeGenerator.GenerateQrCode(_currentPayload, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(code);
            using var ms = new MemoryStream(png.GetGraphic(10));
            using var bmp = new Bitmap(ms);
            _qrBox.Image?.Dispose();
            _qrBox.Image = new Bitmap(bmp);
            _linkInfo.Text = $"Ready on {ip} • Direct encrypted pairing";
        }
        catch (Exception ex)
        {
            _linkInfo.Text = "Could not render QR: " + ex.Message;
        }
    }

    private static Button MakeBtn(string text, bool primary) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = primary ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105), BorderSize = 1 },
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 10, 0)
    };
}

internal sealed class WirelessBridgePairDialog : Form
{
    private readonly TextBox _endpointBox = new() { Dock = DockStyle.Top, Height = 28 };
    private readonly TextBox _codeBox = new() { Dock = DockStyle.Top, Height = 28 };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Top, Height = 44 };
    private readonly Button _pairBtn = new()
    {
        Text = "Pair & Activate",
        AutoSize = true,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(37, 99, 235),
        ForeColor = Color.White,
        Cursor = Cursors.Hand
    };
    private readonly Func<string?, int, string, int, Task<(bool Ok, string Message)>> _pairAction;
    private readonly int _connectPortHint;

    internal WirelessBridgePairDialog(
        AdbCableBridge.BridgeStatusReport status,
        Func<string?, int, string, int, Task<(bool Ok, string Message)>> pairAction)
    {
        _pairAction = pairAction;
        _connectPortHint = status.ConnectPortHint;

        Text = "DeskTouch Vision — Pair Wireless Android Bridge";
        ClientSize = new Size(440, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(17, 23, 34);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(18);

        var title = new Label
        {
            Text = "One-Time Wireless Debugging Pairing",
            Font = new Font("Segoe UI Semibold", 14),
            Dock = DockStyle.Top,
            Height = 32
        };
        var guide = new Label
        {
            Text = "On Android: Developer options -> Wireless debugging -> Pair device with pairing code.\nLeave IP:Port blank to auto-detect, or type the Port/IP:Port shown on phone.",
            ForeColor = Color.FromArgb(186, 200, 220),
            Dock = DockStyle.Top,
            Height = 46
        };
        var epLbl = new Label { Text = "Pairing IP:Port (optional — auto-detected if blank):", Dock = DockStyle.Top, Height = 22 };
        if (status.PairPortHint > 0) _endpointBox.Text = status.PairPortHint.ToString();
        var codeLbl = new Label { Text = "6-digit Wi-Fi pairing code:", Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 6, 0, 0) };

        _statusLabel.ForeColor = Color.FromArgb(148, 163, 184);
        _statusLabel.Text = status.Detail;
        _statusLabel.Padding = new Padding(0, 8, 0, 0);

        _pairBtn.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
        _pairBtn.FlatAppearance.BorderSize = 1;
        _pairBtn.Click += async (_, _) => await ExecutePairAsync();

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        btnRow.Controls.Add(_pairBtn);

        Controls.Add(btnRow);
        Controls.Add(_statusLabel);
        Controls.Add(_codeBox);
        Controls.Add(codeLbl);
        Controls.Add(_endpointBox);
        Controls.Add(epLbl);
        Controls.Add(guide);
        Controls.Add(title);
    }

    private async Task ExecutePairAsync()
    {
        _pairBtn.Enabled = false;
        _statusLabel.ForeColor = Color.FromArgb(148, 163, 184);
        _statusLabel.Text = "Pairing Wireless Debugging and starting Virtual Bridge...";
        try
        {
            string rawEp = _endpointBox.Text.Trim();
            string? ip = null;
            int port = 0;
            if (!string.IsNullOrEmpty(rawEp))
            {
                int colon = rawEp.LastIndexOf(':');
                if (colon > 0)
                {
                    ip = rawEp[..colon].Trim();
                    _ = int.TryParse(rawEp[(colon + 1)..].Trim(), out port);
                }
                else
                {
                    _ = int.TryParse(rawEp, out port);
                }
            }
            var (ok, msg) = await _pairAction(ip, port, _codeBox.Text.Trim(), _connectPortHint);
            _statusLabel.ForeColor = ok ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            _statusLabel.Text = msg;
        }
        finally
        {
            if (!IsDisposed) _pairBtn.Enabled = true;
        }
    }
}
