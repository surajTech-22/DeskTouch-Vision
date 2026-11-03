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
internal sealed class DisplaySettingsForm : Form, IUpdateCloseHost
{
    private readonly DisplayArrangementControl _display = new();
    private readonly Label _status = new();
    private readonly Label _summary = new();
    private readonly Button _add = Button("Add devices", true);
    private readonly Button _copyCode = Button("Copy pairing code");
    private readonly Button _disconnect = Button("Disconnect");
    private readonly Button _trustDevice = Button("Trust device");
    private readonly Button _manageTrusted = Button("Trusted devices");
    private readonly Button _nativeDisplaySettings = Button("Windows display settings");
    private readonly Button _androidDownload = Button("Get Android app");
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
    private readonly TrustedDeviceStore? _trusted;
    private readonly string? _trustLoadError;
    private System.Windows.Forms.Timer? _trustedScanTimer;
    private bool _scanningTrusted;
    private bool _manualPairDialogOpen;
    private DateTime _retryTrustedAfter = DateTime.MinValue;
    private string _currentFingerprint = "";
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _startupMenu;
    private bool _exitRequested;
    private bool _trayTipShown;
    private readonly bool _startInTray;
    private volatile bool _isHiddenInTray;
    private ReceiverServer? _receiver;
    private NearbyComputerDiscovery? _discovery;
    private PhysicalInputMonitor? _nativeInput;
    private HostSession? _host;
    private ConnectedPeer? _incomingPeer;
    private SourceCapture? _capture;
    private MonitorEdge _preferredEdge = MonitorEdge.Right;
    private bool _connecting;
    private string _remoteAddress = "";

    private readonly string _layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "device-sharing-layout.json");

    internal DisplaySettingsForm(bool startInTray = false)
    {
        _startInTray = startInTray;
        try { _trusted = new TrustedDeviceStore(); }
        catch (Exception ex) { _trustLoadError = ex.Message; }
        Text = "DeskTouch Vision — Displays";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(31, 32, 36);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(1000, 690);
        MinimumSize = new Size(780, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 115,
            BackColor = BackColor, Padding = new Padding(24, 10, 16, 8) };
        var title = new Label { Dock = DockStyle.Top, Height = 59,
            Text = "Displays", Font = new Font("Segoe UI Semibold", 29),
            ForeColor = Color.WhiteSmoke };
        var subtitle = new Label { Dock = DockStyle.Fill,
            Text = "Arrange connected devices while keeping your Windows display layout unchanged.",
            ForeColor = Color.FromArgb(188, 190, 198), AutoEllipsis = true };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        _manageTrusted.Name = "TrustedDevicesButton";
        _manageTrusted.Text = "Trusted devices";
        _manageTrusted.Width = 128;
        _manageTrusted.Dock = DockStyle.Right;
        _manageTrusted.Margin = new Padding(5);
        header.Controls.Add(_manageTrusted);
        header.Controls.Add(_checkUpdates);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 208,
            Padding = new Padding(24, 10, 24, 7), BackColor = BackColor };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52,
            Name = "ConnectionActions",
            FlowDirection = FlowDirection.LeftToRight, AutoSize = false,
            // Wrapping creates a hidden second row beneath the fixed-height
            // action bar at high Windows DPI. Keep one stable visible row.
            WrapContents = false, AutoScroll = true };
        foreach (var b in new[] { _add, _trustDevice, _copyCode, _disconnect, _nativeDisplaySettings, _androidDownload })
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
        var explanation = new Label { Dock = DockStyle.Fill,
            Text = "Your built-in or connected keyboard and mouse always take priority when used. " +
                   "Drag the connected PC in the diagram to choose its screen edge. " +
                   "Emergency return: Ctrl + Alt + Shift + F12.",
            ForeColor = Color.FromArgb(179, 184, 193) };
        // WinForms docks in reverse z-order: add Fill before Top to give
        // the title its own space instead of covering the explanation.
        deviceInfo.Controls.Add(explanation);
        deviceInfo.Controls.Add(_summary);
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
        _disconnect.Click += (_, _) => DisconnectByUser();
        _trustDevice.Click += async (_, _) => await TrustConnectedDeviceAsync();
        _manageTrusted.Click += (_, _) => ManageTrustedDevices();
        _nativeDisplaySettings.Click += (_, _) => OpenNativeDisplays();
        _androidDownload.Click += (_, _) => { using var view = new AndroidDownloadDialog(); view.ShowDialog(this); };
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
            try
            {
                WindowsStartup.EnsureDefault();
                if (_startupMenu != null) _startupMenu.Checked = WindowsStartup.Enabled;
            }
            catch (Exception ex) { Status("Windows sign-in startup could not be enabled: " + ex.Message); }
            UpdateClient.StartEventUpdates(this);
            _trustedScanTimer = new System.Windows.Forms.Timer { Interval = 8500 };
            _trustedScanTimer.Tick += async (_, _) => await ScanTrustedAsync();
            _trustedScanTimer.Start();
            if (_trustLoadError != null)
                Status("Trusted device settings unavailable; reconnect disabled: " + _trustLoadError);
            if (_startInTray) HideToTray(showBalloon: false);
        };
        InitializeTray();
        FormClosing += OnDeskTouchFormClosing;
        UpdateButtons();
    }

    internal bool IsRunningInTray => _isHiddenInTray;
    internal bool HasTrayIcon => _trayIcon?.Visible == true;

    internal static bool ShouldStayInTray(CloseReason reason, bool intentionalExit) =>
        reason == CloseReason.UserClosing && !intentionalExit;

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Show Displays", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add("Check for updates", null, async (_, _) =>
        {
            ShowFromTray();
            await UpdateClient.CheckManuallyAsync(this);
        });
        _trayMenu.Items.Add("Trusted devices", null, (_, _) =>
        {
            ShowFromTray();
            ManageTrustedDevices();
        });
        _trayMenu.Items.Add("Get Android app", null, (_, _) =>
        {
            ShowFromTray();
            using var view = new AndroidDownloadDialog();
            view.ShowDialog(this);
        });
        _trayMenu.Items.Add("Disconnect", null, (_, _) => DisconnectByUser());
        _startupMenu = new ToolStripMenuItem("Launch in tray when Windows starts")
        {
            Checked = WindowsStartup.Enabled, CheckOnClick = false
        };
        _startupMenu.Click += (_, _) =>
        {
            try
            {
                WindowsStartup.SetEnabled(!WindowsStartup.Enabled);
                _startupMenu.Checked = WindowsStartup.Enabled;
                Status(_startupMenu.Checked
                    ? "Windows sign-in startup enabled. DeskTouch will launch in the tray."
                    : "Windows sign-in startup disabled by user.");
            }
            catch (Exception ex) { Status("Could not change Windows Startup Apps: " + ex.Message); }
        };
        _trayMenu.Items.Add(_startupMenu);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit DeskTouch", null, (_, _) => ExitDeskTouch());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DeskTouch Vision - Windows device sharing",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    internal void ShowFromTray()
    {
        if (IsDisposed || _exitRequested || _closing.IsCancellationRequested) return;
        _isHiddenInTray = false;
        // Do not toggle ShowInTaskbar: WinForms may recreate the HWND, which
        // would invalidate Raw Input registrations and break mouse handoff.
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray(bool showBalloon = true)
    {
        _isHiddenInTray = true;
        // Hide() naturally removes an invisible Form from the taskbar
        // WITHOUT changing ShowInTaskbar (which can recreate the HWND).
        // The same handle must keep receiving raw mouse input even in tray.
        // The UI message loop continues to receive GitHub release SSE events.
        Hide();
        if (showBalloon && !_trayTipShown && _trayIcon != null)
        {
            _trayTipShown = true;
            _trayIcon.BalloonTipTitle = "DeskTouch keeps sharing in the tray";
            _trayIcon.BalloonTipText =
                "The computers stay paired. Double-click the tray icon to show Displays.";
            try { _trayIcon.ShowBalloonTip(2500); }
            catch (InvalidOperationException) { /* Tray remains active. */ }
        }
    }

    private void OnDeskTouchFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (ShouldStayInTray(args.CloseReason, _exitRequested))
        {
            args.Cancel = true;
            HideToTray();
            return;
        }
        _closing.Cancel();
        _trustedScanTimer?.Stop();
        // Revoke remote control and close the TLS peer before destroying
        // Windows input hooks or receiver listening state.
        DisconnectHost();
        StopListening();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private void ExitDeskTouch()
    {
        _exitRequested = true;
        Close();
    }

    bool IUpdateCloseHost.IsHiddenInTray => _isHiddenInTray;

    void IUpdateCloseHost.CloseForVerifiedUpdate() => ExitDeskTouch();

    private static Button Button(string name, bool primary = false) => new()
    {
        Text = name, Width = name switch {
            "Windows display settings" => 178, "Copy pairing code" => 155,
            "Add devices" => 145, "Get Android app" => 150, _ => 100 },
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
        try
        {
            string logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskTouchVision", "logs");
            Directory.CreateDirectory(logs);
            // Status strings intentionally contain no pairing secrets, keys or
            // raw keyboard events. Keep connection-stage errors diagnosable.
            File.AppendAllText(Path.Combine(logs, "connection.log"),
                DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch { /* Never block Windows input for diagnostic logging. */ }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        _trustDevice.Enabled = _trusted != null && _host != null &&
            !string.IsNullOrEmpty(_currentFingerprint) &&
            !_trusted.IsTrustedReceiver(_currentFingerprint);
        _trustDevice.Visible = _trusted != null && _host != null &&
            !string.IsNullOrEmpty(_currentFingerprint) &&
            !_trusted.IsTrustedReceiver(_currentFingerprint);
        _manageTrusted.Enabled = _trusted != null;
        _add.Enabled = !_connecting && !_closing.IsCancellationRequested;
        _copyCode.Enabled = _receiver != null && _incomingPeer == null &&
            !_connecting && _host == null;
        _disconnect.Enabled = _host != null || _incomingPeer != null || _connecting;
        var localScreens = _display.Local?.Monitors.Count ?? 0;
        if (_host != null)
            _summary.Text = $"This computer  ·  {localScreens} Windows display(s)     +     " +
                            $"Connected computer  ·  {_host.RemoteMonitors.Monitors.Count} display(s)";
        else if (_incomingPeer != null)
            _summary.Text = $"This computer  ·  {localScreens} Windows display(s)     +     " +
                            $"{_incomingPeer.Name}  ·  {_incomingPeer.Monitors.Monitors.Count} display(s)  ·  Two-way sharing";
        else
            _summary.Text = $"This computer  ·  {localScreens} Windows display(s)" +
                            (_receiver != null ? "     •     Ready to pair" : "     •     Not available to other PCs");
    }

    private void RefreshDisplays()
    {
        try
        {
            _display.Local = MonitorTopology.Capture();
            _display.Remote = _host?.RemoteMonitors ?? _incomingPeer?.Monitors;
            UpdateButtons();
        }
        catch (Exception ex) { Status("Windows display detection failed: " + ex.Message); }
    }

    private void StartListening()
    {
        if (_receiver != null || _host != null || _connecting || _closing.IsCancellationRequested) return;
        try
        {
            // Discovery and trusted auto-connect do not need global keyboard
            // or mouse hooks. Arm input monitoring only after TLS pairing has
            // authenticated an active incoming peer.
            _nativeInput = new PhysicalInputMonitor(this);
            _receiver = new ReceiverServer(Status, _nativeInput.Priority, ConfirmPeerAsync,
                _trusted, ConfirmTrustAsync);
            _receiver.PeerConnected += OnIncomingPeerConnected;
            _receiver.PeerDisconnected += OnIncomingPeerDisconnected;
            _receiver.PeerTopologyChanged += OnIncomingPeerTopologyChanged;
            _receiver.PeerPlacementChanged += OnIncomingPeerPlacementChanged;
            _receiver.ReverseControlMessage += HandleReverseState;
            _receiver.AllowForwardClaim = () =>
                InputClaimLifecyclePolicy.CanGrant(_nativeInput?.IsRunning == true,
                    _capture?.RemoteActive == true, _capture?.IsClaimPending == true);
            _receiver.Start();
            try
            {
                // Preserve UDP discovery even when the PC becomes an outgoing
                // controller. Busy PCs remain visible instead of disappearing.
                if (_discovery == null)
                {
                    _discovery = new NearbyComputerDiscovery(_receiver.Fingerprint, () =>
                    {
                        var receiver = Volatile.Read(ref _receiver);
                        return receiver != null && receiver.CanAcceptNewPeer &&
                            _host == null && !_connecting;
                    });
                    _discovery.Start();
                }
                Status("Ready for trusted device discovery. No global input hooks while unpaired.");
            }
            catch (Exception ex)
            {
                _discovery?.Dispose();
                _discovery = null;
                Status("Ready to pair by code. LAN discovery unavailable: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            StopListening();
            Status("Local sharing service unavailable: " + ex.Message);
        }
    }

    private void StopListening(bool preserveDiscovery = false)
    {
        // Receiving PC also owns a SourceCapture when paired. Always unhook
        // before disposing its LocalInputPriority and raw input registration.
        if (_host == null)
        {
            _capture?.Dispose();
            _capture = null;
        }
        if (!preserveDiscovery)
        {
            _discovery?.Dispose();
            _discovery = null;
        }
        if (_receiver != null)
        {
            _receiver.PeerConnected -= OnIncomingPeerConnected;
            _receiver.PeerDisconnected -= OnIncomingPeerDisconnected;
            _receiver.PeerTopologyChanged -= OnIncomingPeerTopologyChanged;
            _receiver.PeerPlacementChanged -= OnIncomingPeerPlacementChanged;
            _receiver.ReverseControlMessage -= HandleReverseState;
            _receiver.Dispose();
        }
        _receiver = null;
        _incomingPeer = null;
        _nativeInput?.Dispose();
        _nativeInput = null;
        UpdateButtons();
    }

    private static MonitorEdge Opposite(MonitorEdge edge) => edge switch
    {
        MonitorEdge.Left => MonitorEdge.Right,
        MonitorEdge.Right => MonitorEdge.Left,
        MonitorEdge.Top => MonitorEdge.Bottom,
        MonitorEdge.Bottom => MonitorEdge.Top,
        _ => MonitorEdge.Right
    };

    private void OnIncomingPeerConnected(ConnectedPeer peer)
    {
        PostToUi(() =>
        {
            if (_receiver == null) return;
            try
            {
                _nativeInput?.Start();
                if (_nativeInput?.IsRunning != true)
                    throw new InvalidOperationException("Native input safety monitor could not start.");
            }
            catch (Exception ex)
            {
                // Never allow remote SendInput injection if the native local
                // priority monitor cannot be established. Disconnect the peer.
                Status("Safe input monitoring unavailable; pairing disconnected: " + ex.Message);
                _receiver.DisconnectPeer();
                return;
            }
            _incomingPeer = peer;
            RefreshDisplays();
            if (_receiver != null && _capture == null)
            {
                try
                {
                    // The receiver MUST also be able to become the source
                    // without opening a second TLS socket or re-pairing.
                    _capture = new SourceCapture(this, _receiver, Status,
                        _preferredEdge, preserveRawInput: true);
                    _capture.Arm();
                    Status($"Two-way authority ready with {peer.Name}. Both PCs can initiate an edge handoff.");
                }
                catch (Exception ex)
                {
                    _capture?.Dispose();
                    _capture = null;
                    Status("Reverse keyboard/mouse capture unavailable: " + ex.Message);
                }
            }
        });
    }

    private void OnIncomingPeerDisconnected()
    {
        PostToUi(() =>
        {
            // Active reverse authority must stop immediately on network loss;
            // never leave hooks swallowing physical keyboard/mouse input.
            if (_host == null)
            {
                _capture?.Dispose();
                _capture = null;
                // Remove system-wide hooks again on disconnect, but keep the
                // same priority instance for the next authenticated peer.
                _nativeInput?.Dispose();
            }
            _incomingPeer = null;
            RefreshDisplays();
            Status("Peer disconnected. Input hooks released; discovery remains available.");
        });
    }

    private void OnIncomingPeerTopologyChanged(ConnectedPeer peer)
    {
        PostToUi(() =>
        {
            _incomingPeer = peer;
            _capture?.UpdateLayouts(MonitorTopology.Capture(), peer.Monitors);
            RefreshDisplays();
            Status($"Two-way Windows monitor layout synchronized with {peer.Name}; pairing retained.");
        });
    }

    private void OnIncomingPeerPlacementChanged(MonitorEdge sourceEdge)
    {
        PostToUi(() =>
        {
            _preferredEdge = Opposite(sourceEdge);
            _display.RemoteEdge = _preferredEdge;
            SavePreferredEdge();
            _capture?.SetEdge(_preferredEdge);
            Status("Both computers' outside-edge handoff positions synchronized.");
        });
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated || _closing.IsCancellationRequested) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !_closing.IsCancellationRequested) action();
            });
        }
        catch (InvalidOperationException) { }
    }

    private async Task<bool> ConfirmPeerAsync(string peer, CancellationToken cancellation)
    {
        if (IsDisposed || !IsHandleCreated || cancellation.IsCancellationRequested)
            return false;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => completion.TrySetResult(false));
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || cancellation.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                var safePeer = peer.Replace("\r", " ").Replace("\n", " ");
                // A receiving computer should visibly request local approval
                // even if its Displays window was minimized in the background.
                if (!Visible) ShowFromTray();
                else if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Activate();
                var result = MessageBox.Show(this,
                    $"Allow {safePeer} to share its keyboard and mouse with this computer?\n\n" +
                    "Your own keyboard, mouse and touchpad always have priority. " +
                    "You can disconnect at any time.",
                    "DeskTouch - Allow connection?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                completion.TrySetResult(result == DialogResult.Yes);
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return await completion.Task;
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
        if (_connecting || _closing.IsCancellationRequested) return;
        using var dialog = new PairComputerDialog();
        _manualPairDialogOpen = true;
        DialogResult choice;
        try { choice = dialog.ShowDialog(this); }
        finally { _manualPairDialogOpen = false; }
        if (choice != DialogResult.OK || _connecting || _closing.IsCancellationRequested) return;
        if (_host != null || _incomingPeer != null)
        {
            // One active input owner at a time. Switching requires explicit
            // confirmation and never claims a second simultaneous session.
            var confirm = MessageBox.Show(this,
                "Another device currently owns the remote input session.\n\n" +
                "Disconnect that session and switch to " + dialog.ComputerName + "?",
                "Switch DeskTouch device?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
            DisconnectHost();
            _receiver?.DisconnectPeer();
            _incomingPeer = null;
        }
        _connecting = true;
        StopListening(preserveDiscovery: true);
        UpdateButtons();
        Status("Connecting securely. The other computer may ask its user to approve...");
        try
        {
            void OnFailure(string reason)
            {
                Status(reason);
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(DisconnectAndListen); }
                catch (InvalidOperationException) { }
            }
            var candidate = dialog.UseRemoteApproval
                ? await HostSession.ConnectWithApprovalAsync(dialog.Address,
                    dialog.Fingerprint, OnFailure, _closing.Token)
                : await HostSession.ConnectAsync(dialog.Address, dialog.Fingerprint,
                    dialog.Secret, OnFailure, _closing.Token);
            if (IsDisposed || _closing.IsCancellationRequested)
            {
                candidate.Dispose();
                return;
            }
            AdoptHost(candidate, dialog.ComputerName, dialog.Fingerprint);
            Status($"Connected to {_remoteAddress}. Move to the {_preferredEdge.ToString().ToLowerInvariant()} outer edge to share input.");
        }
        catch (Exception ex)
        {
            DisconnectHost();
            Status("Connection failed: " + ex.Message);
            if (!_closing.IsCancellationRequested && !IsDisposed)
                MessageBox.Show(this,
                    ex.Message +
                    "\n\nIf discovery works but TCP port 37377 is blocked, allow DeskTouch.DeviceShare.exe in Windows Firewall on PRIVATE networks on the receiving PC. " +
                    "For a port test, run CHECK_CONNECTION.ps1 from the downloaded installer.",
                    "Unable to connect Windows computers",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connecting = false;
            if (_host == null) StartListening();
            UpdateButtons();
        }
    }

    private void AdoptHost(HostSession session, string name, string fingerprint)
    {
        _host = session;
        _remoteAddress = name;
        _currentFingerprint = fingerprint;
        _nativeInput = new PhysicalInputMonitor(this);
        _nativeInput.Start();
        session.AttachReverseInput(_nativeInput.Priority,
            () => _capture == null ||
                (!_capture.RemoteActive && !_capture.IsClaimPending));
        session.AuthorityDiagnostic += Status;
        _capture = new SourceCapture(this, session, Status, _preferredEdge,
            preserveRawInput: true);
        session.AuthorityControlPacket += HandleRemoteState;
        session.RemoteTopologyChanged += HandleRemoteTopology;
        session.RemotePlacementChanged += HandleRemotePlacement;
        RefreshDisplays();
        _capture.Arm();
        session.PublishSourcePlacement(_preferredEdge);
        UpdateButtons();
    }

    private async Task<bool> ConfirmTrustAsync(string name, CancellationToken cancellation)
    {
        if (IsDisposed || !IsHandleCreated || cancellation.IsCancellationRequested)
            return false;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => completion.TrySetResult(false));
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || cancellation.IsCancellationRequested)
                {
                    completion.TrySetResult(false);
                    return;
                }
                if (!Visible) ShowFromTray();
                Activate();
                string safeName = name.Replace("\r", " ").Replace("\n", " ");
                var result = MessageBox.Show(this,
                    $"Trust {safeName} for automatic reconnection?\n\n" +
                    "After approval, this computer can accept connections from the trusted " +
                    "device when it is on the same reachable local network, without asking again. " +
                    "Native keyboard and mouse always retain priority. " +
                    "You can revoke trust using Trusted devices.",
                    "DeskTouch — Trust this device?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                completion.TrySetResult(result == DialogResult.Yes);
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return await completion.Task;
    }

    private async Task TrustConnectedDeviceAsync()
    {
        if (_host == null || _trusted == null ||
            string.IsNullOrEmpty(_currentFingerprint)) return;
        var name = _remoteAddress;
        var fingerprint = _currentFingerprint;
        if (MessageBox.Show(this,
            $"Trust {name}?\n\nAfter the other computer approves, DeskTouch will " +
            "recognize its permanent encrypted Windows identity, even when its " +
            "IP address changes. Both computers must remain reachable on the LAN.\n\n" +
            "You may revoke trust at any time.",
            "Trust connected computer?", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _trustDevice.Enabled = false;
        try
        {
            var token = await _host.RequestTrustAsync(_closing.Token);
            if (token == null)
            {
                Status("Trusted device request declined or interrupted. Normal pairing remains available.");
                return;
            }
            _trusted.TrustOutgoing(name, fingerprint, token);
            _trusted.AutoConnectEnabled = true;
            Status($"{name} trusted. Automatic reconnect enabled when this verified computer is discovered.");
        }
        catch (Exception ex) { Status("Could not save trusted device: " + ex.Message); }
        finally { UpdateButtons(); }
    }

    private void ManageTrustedDevices()
    {
        if (_trusted == null) return;
        using var dialog = new TrustedDevicesDialog(_trusted);
        dialog.ShowDialog(this);
        UpdateButtons();
    }

    private void DisconnectByUser()
    {
        // An intentional Disconnect must not instantly reconnect the same PC.
        // Pause automatic reconnection until explicitly turned on again.
        if (_trusted != null && _trusted.AutoConnectEnabled)
        {
            try { _trusted.AutoConnectEnabled = false; }
            catch (Exception ex) { Status("Could not pause automatic reconnect: " + ex.Message); }
        }
        DisconnectAndListen();
        UpdateButtons();
    }

    private async Task ScanTrustedAsync()
    {
        if (_scanningTrusted || _connecting || _manualPairDialogOpen || _host != null ||
            _incomingPeer != null || _closing.IsCancellationRequested ||
            _trusted?.AutoConnectEnabled != true || _trusted.Outgoing.Count == 0 ||
            DateTime.UtcNow < _retryTrustedAfter || _receiver == null)
            return;
        _scanningTrusted = true;
        try
        {
            // Discovery finds CURRENT network addresses; the saved file has
            // NO IP address, only the receiver's persistent TLS fingerprint.
            var peers = await NearbyComputerDiscovery.FindAsync(_closing.Token);
            var found = peers.Select(p => new { Peer = p,
                Saved = _trusted.FindAutoConnect(p.Fingerprint) })
                .FirstOrDefault(x => x.Saved != null);
            if (found == null || _host != null || _incomingPeer != null ||
                _connecting || !_trusted.AutoConnectEnabled)
                return;
            _connecting = true;
            StopListening(preserveDiscovery: true);
            Status($"Trusted {found.Saved!.Name} discovered. Verifying persistent device identity...");
            try
            {
                void OnFailure(string reason)
                {
                    Status(reason);
                    PostToUi(DisconnectAndListen);
                }
                // The token is bound to the saved pinned TLS certificate.
                // Never use a matching name or previously seen IP as proof.
                var session = await HostSession.ConnectTrustedAsync(
                    found.Peer.Address, found.Saved.Fingerprint, found.Saved.Token,
                    OnFailure, _closing.Token);
                if (IsDisposed || _closing.IsCancellationRequested ||
                    !_trusted.AutoConnectEnabled)
                {
                    session.Dispose();
                    return;
                }
                AdoptHost(session, found.Saved.Name, found.Saved.Fingerprint);
                Status($"Trusted computer {found.Saved.Name} reconnected automatically.");
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Status("Trusted reconnection failed; will check again: " + ex.Message);
                _retryTrustedAfter = DateTime.UtcNow.AddSeconds(25);
            }
            finally
            {
                _connecting = false;
                if (_host == null && !_closing.IsCancellationRequested) StartListening();
                UpdateButtons();
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _retryTrustedAfter = DateTime.UtcNow.AddSeconds(25);
            Status("Trusted device discovery unavailable: " + ex.Message);
        }
        finally { _scanningTrusted = false; }
    }

    private void HandleRemoteState(Packet packet) =>
        PostToUi(() => ApplyAuthorityState(packet, reverse: false));

    private void HandleReverseState(Packet packet) =>
        PostToUi(() => ApplyAuthorityState(packet, reverse: true));

    private void ApplyAuthorityState(Packet packet, bool reverse)
    {
        if (_capture == null) return;
        // Reverse packets travel over the authenticated server-side stream.
        // Both handlers call exactly the same lease-checked state machine.
        string operation = reverse && packet.Type.StartsWith("reverse-",
            StringComparison.Ordinal) ? packet.Type["reverse-".Length..] : packet.Type;
        switch (operation)
        {
            case "granted": _capture.GrantRemote(packet.A); break;
            case "active": _capture.ConfirmRemoteAuthority(packet.A); break;
            case "busy": _capture.ReceiverBusy(packet.A); break;
            case "local-active": _capture.ReceiverNativePriority(packet.A); break;
        }
    }

    private void HandleRemoteTopology(MonitorTopology topology)
    {
        PostToUi(() =>
        {
            _capture?.UpdateLayouts(MonitorTopology.Capture(), topology);
            RefreshDisplays();
            Status("Remote Windows monitor layout updated without disconnecting.");
        });
    }

    private void HandleRemotePlacement(MonitorEdge sourceEdge)
    {
        PostToUi(() =>
        {
            _preferredEdge = sourceEdge;
            _display.RemoteEdge = sourceEdge;
            SavePreferredEdge();
            _capture?.SetEdge(sourceEdge);
            Status("Display placement synchronized; connection remains active.");
        });
    }

    private void OnRemoteEdgeChanged(MonitorEdge edge)
    {
        _preferredEdge = edge;
        SavePreferredEdge();
        _capture?.SetEdge(edge);
        _host?.PublishSourcePlacement(edge);
        if (_incomingPeer != null) _receiver?.PublishPlacement(Opposite(edge));
        Status($"Connected computer placed {_preferredEdge.ToString().ToLowerInvariant()}. " +
               "Move to that outside edge to switch between computers.");
    }

    private void DisconnectHost()
    {
        bool hadHost = _host != null;
        if (_host != null)
        {
            _host.AuthorityControlPacket -= HandleRemoteState;
            _host.AuthorityDiagnostic -= Status;
            _host.RemoteTopologyChanged -= HandleRemoteTopology;
            _host.RemotePlacementChanged -= HandleRemotePlacement;
        }
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        // Outgoing controller also hosts a native priority monitor for
        // incoming reverse claims. It belongs to this host session.
        if (hadHost)
        {
            _nativeInput?.Dispose();
            _nativeInput = null;
        }
        _remoteAddress = "";
        _currentFingerprint = "";
        _display.Remote = null;
        UpdateButtons();
    }

    private void DisconnectAndListen()
    {
        DisconnectHost();
        if (_incomingPeer != null)
        {
            _receiver?.DisconnectPeer();
            _incomingPeer = null;
            RefreshDisplays();
        }
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
        if (IsDisposed || _closing.IsCancellationRequested) return;
        try
        {
            var current = MonitorTopology.Capture();
            // Windows sometimes broadcasts WM_DISPLAYCHANGE without any
            // actual topology change. It must NEVER tear down pairing.
            if (_display.Local?.ToWire() == current.ToWire()) return;
            _display.Local = current;
            if (_host != null)
            {
                _capture?.UpdateLayouts(current, _host.RemoteMonitors);
                _host.PublishSourceTopology(current);
            }
            if (_receiver != null)
            {
                if (_incomingPeer != null)
                    _capture?.UpdateLayouts(current, _incomingPeer.Monitors);
                _receiver.PublishLocalTopology(current);
            }
            UpdateButtons();
            Status("Windows monitors changed; synchronized display layout without reconnecting.");
        }
        catch (Exception ex)
        {
            Status("Unable to refresh Windows display layout: " + ex.Message);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Program.ShowDisplaysMessage && !_exitRequested)
        {
            ShowFromTray();
            return;
        }
        if (message.Msg == Win.WM_INPUT)
        {
            // Crucial for TWO-WAY input: native priority detection AND mouse
            // capture must see the same real raw event on either computer.
            // Previously receiving mode discarded it before SourceCapture.
            _nativeInput?.HandleRawInput(message.LParam);
            _capture?.OnRawInput(message.LParam);
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
            _trustedScanTimer?.Dispose();
            _trustedScanTimer = null;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _trayMenu?.Dispose();
            _trayMenu = null;
            _closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
