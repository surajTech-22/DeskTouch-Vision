using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;
using DeskTouchVision;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Single unified Windows display arrangement experience. Native monitors
/// retain Windows ownership; paired PCs are supplemental keyboard/mouse
/// targets, not virtual Windows monitors or hardware replacements.
/// </summary>
internal sealed class DisplaySettingsForm : Form
{
    private readonly DisplayArrangementControl _display = new();
    private readonly Label _status = new();
    private readonly Label _summary = new();
    private readonly Button _add = Button("Add a computer", true);
    private readonly Button _copyCode = Button("Copy pairing code");
    private readonly Button _disconnect = Button("Disconnect");
    private readonly Button _nativeDisplaySettings = Button("Windows display settings");
    private readonly LinkLabel _checkUpdates = new()
    {
        Text = "Check for updates",
        Dock = DockStyle.Right,
        Width = 132,
        Height = 34,
        TextAlign = ContentAlignment.MiddleCenter,
        LinkColor = Color.FromArgb(159, 201, 255),
        ActiveLinkColor = Color.White,
        VisitedLinkColor = Color.FromArgb(159, 201, 255)
    };

    private readonly CancellationTokenSource _closing = new();
    private ReceiverServer? _receiver;
    private PhysicalInputMonitor? _nativeInput;
    private HostSession? _host;
    private SourceCapture? _capture;
    private MonitorEdge _preferredEdge = MonitorEdge.Right;
    private bool _connecting;
    private string _remoteAddress = "";

    private readonly string _layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "device-sharing-layout.json");

    internal DisplaySettingsForm()
    {
        Text = "DeskTouch Vision — Displays";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(31, 32, 36);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(1000, 690);
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 115,
            BackColor = BackColor, Padding = new Padding(24, 10, 16, 8) };
        var title = new Label { Dock = DockStyle.Top, Height = 59,
            Text = "Displays", Font = new Font("Segoe UI Semibold", 29),
            ForeColor = Color.WhiteSmoke };
        var subtitle = new Label { Dock = DockStyle.Fill,
            Text = "Arrange your computers like Windows monitors. Your existing Windows display layout remains unchanged.",
            ForeColor = Color.FromArgb(188, 190, 198), AutoEllipsis = true };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        header.Controls.Add(_checkUpdates);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 183,
            Padding = new Padding(24, 10, 24, 7), BackColor = BackColor };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52,
            FlowDirection = FlowDirection.LeftToRight, AutoSize = false, WrapContents = true };
        foreach (var b in new[] { _add, _copyCode, _disconnect, _nativeDisplaySettings })
        {
            b.Height = 35;
            b.Margin = new Padding(0, 2, 10, 5);
            actions.Controls.Add(b);
        }
        var deviceInfo = new Panel { Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(41, 43, 49),
            Padding = new Padding(14, 10, 12, 7) };
        _summary.Dock = DockStyle.Top;
        _summary.Height = 39;
        _summary.Font = new Font("Segoe UI Semibold", 11);
        _summary.ForeColor = Color.FromArgb(230, 231, 234);
        _summary.AutoEllipsis = true;
        deviceInfo.Controls.Add(_summary);

        var explanation = new Label { Dock = DockStyle.Fill,
            Text = "Your built-in or connected keyboard and mouse always take priority when used. " +
                   "Drag the connected PC in the diagram to choose its screen edge. " +
                   "Emergency return: Ctrl + Alt + Shift + F12.",
            ForeColor = Color.FromArgb(179, 184, 193) };
        deviceInfo.Controls.Add(explanation);
        explanation.BringToFront();
        _status.Dock = DockStyle.Bottom;
        _status.Height = 43;
        _status.Padding = new Padding(10, 9, 10, 6);
        _status.ForeColor = Color.FromArgb(191, 215, 238);
        _status.AutoEllipsis = true;

        bottom.Controls.Add(deviceInfo);
        bottom.Controls.Add(actions);
        bottom.Controls.Add(_status);

        var arrangePanel = new Panel { Dock = DockStyle.Fill,
            Padding = new Padding(24, 6, 24, 14) };
        var caption = new Label { Dock = DockStyle.Top, Height = 39,
            Text = "Rearrange your displays",
            Font = new Font("Segoe UI Semibold", 15),
            ForeColor = Color.WhiteSmoke };
        var hint = new Label { Dock = DockStyle.Bottom, Height = 28,
            Text = "Windows controls your monitors. DeskTouch links another computer at the chosen outside edge.",
            ForeColor = Color.FromArgb(181, 184, 192) };
        arrangePanel.Controls.Add(_display);
        arrangePanel.Controls.Add(caption);
        arrangePanel.Controls.Add(hint);

        Controls.Add(arrangePanel);
        Controls.Add(bottom);
        Controls.Add(header);

        _add.Click += async (_, _) => await AddComputerAsync();
        _copyCode.Click += (_, _) => CopyPairingCode();
        _disconnect.Click += (_, _) => DisconnectAndListen();
        _nativeDisplaySettings.Click += (_, _) => OpenNativeDisplays();
        _checkUpdates.LinkClicked += async (_, _) =>
            await UpdateClient.CheckManuallyAsync(this);
        _display.AddComputerRequested += async () => await AddComputerAsync();
        _display.RemoteEdgeChanged += OnRemoteEdgeChanged;

        LoadPreferredEdge();
        _display.RemoteEdge = _preferredEdge;
        Shown += (_, _) =>
        {
            RefreshDisplays();
            StartListening();
            UpdateClient.StartEventUpdates(this);
        };
        FormClosing += (_, _) =>
        {
            _closing.Cancel();
            DisconnectHost();
            StopListening();
        };
        UpdateButtons();
    }

    private static Button Button(string name, bool primary = false) => new()
    {
        Text = name, Width = name switch {
            "Windows display settings" => 178, "Copy pairing code" => 155,
            "Add a computer" => 150, _ => 100 },
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = Color.FromArgb(85, 89, 100), BorderSize = 1 },
        ForeColor = Color.White,
        BackColor = primary ? Color.FromArgb(27, 104, 190)
                            : Color.FromArgb(56, 58, 65),
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI", 9)
    };

    private void Status(string message)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { if (IsHandleCreated) BeginInvoke(() => Status(message)); }
            catch (InvalidOperationException) { }
            return;
        }
        _status.Text = message;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        _add.Enabled = _host == null && !_connecting;
        _copyCode.Enabled = _receiver != null && !_connecting && _host == null;
        _disconnect.Enabled = _host != null || _connecting;
        var localScreens = _display.Local?.Monitors.Count ?? 0;
        if (_host != null)
            _summary.Text = $"This computer  ·  {localScreens} Windows display(s)     +     " +
                            $"Connected computer  ·  {_host.RemoteMonitors.Monitors.Count} display(s)";
        else
            _summary.Text = $"This computer  ·  {localScreens} Windows display(s)" +
                            (_receiver != null ? "     •     Ready to pair" : "     •     Not available to other PCs");
    }

    private void RefreshDisplays()
    {
        try
        {
            _display.Local = MonitorTopology.Capture();
            _display.Remote = _host?.RemoteMonitors;
            UpdateButtons();
        }
        catch (Exception ex) { Status("Windows display detection failed: " + ex.Message); }
    }

    private void StartListening()
    {
        if (_receiver != null || _host != null || _connecting || _closing.IsCancellationRequested) return;
        try
        {
            _nativeInput = new PhysicalInputMonitor(this);
            _nativeInput.Start();
            _receiver = new ReceiverServer(Status, _nativeInput.Priority);
            _receiver.Start();
            Status("Ready. On another computer, copy its pairing code, then select Add a computer here.");
        }
        catch (Exception ex)
        {
            StopListening();
            Status("Local sharing service unavailable: " + ex.Message);
        }
    }

    private void StopListening()
    {
        _receiver?.Dispose();
        _receiver = null;
        _nativeInput?.Dispose();
        _nativeInput = null;
        UpdateButtons();
    }

    private void CopyPairingCode()
    {
        if (_receiver == null) { Status("This computer is currently controlling another PC."); return; }
        try
        {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
                .OrderByDescending(x => x.GetAddressBytes()[0] == 192 ||
                                        x.GetAddressBytes()[0] == 10 ||
                                        x.GetAddressBytes()[0] == 172)
                .ToArray();
            var address = addresses.FirstOrDefault();
            if (address == null) { Status("No local IPv4 address. Join a private Wi-Fi or Ethernet network."); return; }
            // Pairing secret is available only by a deliberate Copy action.
            var code = $"DT1|{address}|{_receiver.Fingerprint}|{_receiver.Secret}";
            Clipboard.SetText(code);
            Status($"Pairing code copied for {Environment.MachineName}. Paste it into Add a computer on the other PC. Keep it private.");
        }
        catch (Exception ex) { Status("Unable to copy pairing code: " + ex.Message); }
    }

    private async Task AddComputerAsync()
    {
        if (_host != null || _connecting || _closing.IsCancellationRequested) return;
        using var dialog = new PairComputerDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _connecting = true;
        StopListening();
        UpdateButtons();
        Status("Pairing with the selected Windows computer over the local network...");
        try
        {
            var candidate = await HostSession.ConnectAsync(dialog.Address, dialog.Fingerprint,
                dialog.Secret, reason =>
                {
                    Status(reason);
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(DisconnectAndListen); }
                    catch (InvalidOperationException) { }
                }, _closing.Token);
            if (IsDisposed || _closing.IsCancellationRequested)
            {
                candidate.Dispose();
                return;
            }
            _host = candidate;
            _remoteAddress = dialog.Address;
            _capture = new SourceCapture(this, _host, Status, _preferredEdge);
            _host.RemoteControlState += HandleRemoteState;
            RefreshDisplays();
            _capture.Arm(); // authorized pairing arms edge switching, not remote control itself
            Status($"Connected to {_remoteAddress}. Move to the {_preferredEdge.ToString().ToLowerInvariant()} outer edge to share input.");
        }
        catch (Exception ex)
        {
            DisconnectHost();
            Status("Connection failed: " + ex.Message);
        }
        finally
        {
            _connecting = false;
            if (_host == null) StartListening();
            UpdateButtons();
        }
    }

    private void HandleRemoteState(string result)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (result == "granted") _capture?.GrantRemote();
                else if (result == "busy") _capture?.ReceiverBusy();
                else if (result == "local-active") _capture?.ReceiverNativePriority();
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnRemoteEdgeChanged(MonitorEdge edge)
    {
        _preferredEdge = edge;
        SavePreferredEdge();
        _capture?.SetEdge(edge);
        Status($"Connected computer placed {_preferredEdge.ToString().ToLowerInvariant()}. " +
               "Move to that outside edge to switch between computers.");
    }

    private void DisconnectHost()
    {
        if (_host != null) _host.RemoteControlState -= HandleRemoteState;
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        _remoteAddress = "";
        _display.Remote = null;
        UpdateButtons();
    }

    private void DisconnectAndListen()
    {
        DisconnectHost();
        if (!_closing.IsCancellationRequested) StartListening();
        Status("Disconnected. Windows keyboard and mouse remain local.");
    }

    private void LoadPreferredEdge()
    {
        try
        {
            if (!File.Exists(_layoutPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_layoutPath));
            if (doc.RootElement.TryGetProperty("RemoteEdge", out var edge) &&
                Enum.TryParse<MonitorEdge>(edge.GetString(), true, out var parsed))
                _preferredEdge = parsed;
        }
        catch { /* invalid layout is ignored; Windows topology is unchanged */ }
    }

    private void SavePreferredEdge()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_layoutPath)!);
            File.WriteAllText(_layoutPath, JsonSerializer.Serialize(new {
                RemoteEdge = _preferredEdge.ToString() }));
        }
        catch { Status("Could not save display arrangement for next time."); }
    }

    private void OpenNativeDisplays()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }
        catch (Exception ex) { Status("Windows display settings unavailable: " + ex.Message); }
    }

    private void OnDisplayChanged()
    {
        DisconnectHost();
        StopListening();
        RefreshDisplays();
        StartListening();
        Status("Windows monitor configuration changed. Layout refreshed; re-pair a connected computer if needed.");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Win.WM_INPUT)
        {
            if (_nativeInput != null) _nativeInput.HandleRawInput(message.LParam);
            else _capture?.OnRawInput(message.LParam);
        }
        if (message.Msg == 0x007E && IsHandleCreated && !Disposing)
        {
            try { BeginInvoke(OnDisplayChanged); }
            catch (InvalidOperationException) { }
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Cancel();
            _closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
