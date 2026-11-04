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
    private readonly Label _environmentHeader = new()
    {
        Dock=DockStyle.Right, Width=235, TextAlign=ContentAlignment.MiddleRight,
        ForeColor=Color.FromArgb(186,216,241),
        Font=new Font("Segoe UI Semibold",10),
        Text="One environment"
    };
    private readonly ComboBox _targetChoice = new()
    {
        Name = "ConnectedDeviceTarget", Dock = DockStyle.Right,
        Width = 256, DropDownStyle = ComboBoxStyle.DropDownList,
        Font = new Font("Segoe UI", 9)
    };
    private bool _updatingTargetChoice;
    private bool _captureUsesInbound;
    private int _captureInboundPeerId;
    private sealed record TargetOption(int Id, string Name)
    {
        public override string ToString() => Name;
    }
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
        _targetChoice.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingTargetChoice && _targetChoice.SelectedItem is TargetOption target)
                SelectConnectedTarget(target.Id);
        };
        // Every connected phone/PC is selected by clicking its SCREEN tile.
        // The previous two-option dropdown falsely implied two independent
        // one-to-one links and was coupled to destructive input-hook switching.
        header.Controls.Add(_environmentHeader);

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
        _display.EnvironmentDeviceSelected += SelectConnectedTarget;
        _display.EnvironmentArrangementChanged += SavePreferredEdge;
        _display.EnvironmentMemberMoved += OnEnvironmentMemberMoved;
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
        _copyCode.Enabled = _receiver?.CanAcceptNewPeer == true &&
            !_connecting && _host == null;
        _disconnect.Enabled = _host != null ||
            _receiver?.ConnectedPeerCount > 0 || _connecting;
        var localScreens = _display.Local?.Monitors.Count ?? 0;
        if (_host != null)
        {
            int members=_host.EnvironmentMembers.Count;
            int android=_host.EnvironmentMembers.Count(m=>m.Platform=="Android");
            _summary.Text = $"{Environment.MachineName}  ·  {localScreens} display(s)     +     " +
                            $"{_remoteAddress}  ·  {_host.RemoteMonitors.Monitors.Count} display(s)" +
                            $"     ·     {members} environment device(s), {android} Android";
        }
        else if (_incomingPeer != null)
            _summary.Text = $"{Environment.MachineName}  ·  {localScreens} display(s)     +     " +
                            $"{_incomingPeer.Name}  ·  {_incomingPeer.Monitors.Monitors.Count} display(s)  ·  " +
                            $"{1+(_receiver?.ConnectedPeerCount??0)} devices in one environment" +
                            $" ({_receiver?.ConnectedPeers.Count(p=>p.Peer.Platform=="Android")??0} Android)";
        else
            _summary.Text = $"{Environment.MachineName}  ·  {localScreens} Windows display(s)" +
                            (_receiver != null ? "     •     Ready to pair" : "     •     Not available to other PCs");
    }

    private void RefreshDisplays()
    {
        try
        {
            _display.LocalDeviceName = Environment.MachineName;
            _display.Local = MonitorTopology.Capture();
            // Device placement is an ENVIRONMENT property, not a transient
            // keyboard-authority selection. Selecting Android must never
            // move the Windows PC from the left side to another tile.
            var tiles = new List<EnvironmentDisplayTile>();
            int baseTargetId=0;
            if(_host!=null)
            {
                // A leaf Windows PC is always arranged next to its hub.
                var hub=_host.EnvironmentMembers.FirstOrDefault(m=>m.Id==0);
                var baseTopology=hub==null ?
                    _host.RemoteMonitors : MonitorTopology.FromWire(hub.Layout);
                _display.Remote=baseTopology;
                _display.RemoteDeviceName=hub?.Name ?? _remoteAddress;
                foreach(var member in _host.EnvironmentMembers)
                {
                    if(member.Id==0 || member.Id==_host.OwnEnvironmentMemberId)continue;
                    tiles.Add(new EnvironmentDisplayTile(-member.Id,member.Name,
                        member.Platform,MonitorTopology.FromWire(member.Layout).Monitors.Count,
                        member.SupportsReverseInput,member.StableIdentity));
                }
            }
            else
            {
                var connected=_receiver?.ConnectedPeers ??
                    Array.Empty<(int Id,ConnectedPeer Peer)>();
                // First connected Windows PC is a permanent layout anchor.
                // A selected Android phone remains beside it, not in its slot.
                var main=connected.Where(p=>p.Peer.Platform=="Windows")
                    .OrderBy(p=>p.Id).FirstOrDefault();
                if(main.Peer==null)
                    main=connected.OrderBy(p=>p.Id).FirstOrDefault();
                baseTargetId=main.Id;
                _display.Remote=main.Peer?.Monitors;
                _display.RemoteDeviceName=main.Peer?.Name ?? "Connected device";
                foreach(var (id,peer) in connected)
                {
                    if(id==baseTargetId)continue;
                    tiles.Add(new EnvironmentDisplayTile(id,peer.Name,
                        peer.Platform,peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput,peer.DeviceFingerprint));
                }
            }
            _display.EnvironmentDevices =
                DisplayArrangementControl.ProjectEnvironment(tiles,baseTargetId);
            UpdateTargetChoice();
            UpdateButtons();
        }
        catch (Exception ex) { Status("Windows display detection failed: " + ex.Message); }
    }

    private void UpdateTargetChoice()
    {
        _updatingTargetChoice = true;
        try
        {
            int selected = _captureUsesInbound || _host == null
                ? _receiver?.SelectedPeerId ?? 0 :
                  -_host.SelectedEnvironmentTargetId;
            _targetChoice.Items.Clear();
            if (_host != null)
            {
                _targetChoice.Items.Add(new TargetOption(0,
                    "Environment hub  •  " + (_host.EnvironmentMembers
                        .FirstOrDefault(m => m.Id==0)?.Name ?? _remoteAddress)));
                foreach(var member in _host.EnvironmentMembers)
                {
                    if(member.Id==0 || member.Id==_host.OwnEnvironmentMemberId ||
                        !member.SupportsReverseInput) continue;
                    _targetChoice.Items.Add(new TargetOption(-member.Id,
                        "In same environment  •  " + member.Platform + "  •  " + member.Name));
                }
            }
            if (_receiver != null)
                foreach (var (id, peer) in _receiver.ConnectedPeers)
                    _targetChoice.Items.Add(new TargetOption(id,
                        peer.Platform + "  •  " + peer.Name));
            for (int i = 0; i < _targetChoice.Items.Count; i++)
                if (_targetChoice.Items[i] is TargetOption item && item.Id == selected)
                    _targetChoice.SelectedIndex = i;
            if (_targetChoice.SelectedIndex == -1 && _targetChoice.Items.Count > 0)
                _targetChoice.SelectedIndex = 0;
            _targetChoice.Visible = false;
            int online=_host!=null ? Math.Max(1,_host.EnvironmentMembers.Count) :
                1+(_receiver?.ConnectedPeerCount??0);
            _environmentHeader.Text=$"One environment  •  {online} devices";
        }
        finally { _updatingTargetChoice = false; }
    }

    private EnvironmentRouteOrder? ConnectedEnvironmentOrder()
    {
        if(_display.Remote==null)return null;
        int baseId;
        if(_host!=null)baseId=0;
        else
        {
            var peers=_receiver?.ConnectedPeers ??
                Array.Empty<(int Id,ConnectedPeer Peer)>();
            var basePeer=peers.Where(p=>p.Peer.Platform=="Windows")
                .OrderBy(p=>p.Id).FirstOrDefault();
            if(basePeer.Peer==null)
                basePeer=peers.OrderBy(p=>p.Id).FirstOrDefault();
            if(basePeer.Peer==null)return null;
            baseId=basePeer.Id;
        }
        return new EnvironmentRouteOrder(
            _display.ArrangedTargetOrder(baseId));
    }

    private int CurrentInputTargetId() =>
        _captureUsesInbound || _host==null
            ? _receiver?.SelectedPeerId ?? EnvironmentRouteOrder.Local
            : -_host.SelectedEnvironmentTargetId;

    private bool NavigateEnvironmentFromCapture(MonitorEdge edge,double fraction)
    {
        var order=ConnectedEnvironmentOrder();
        if(order==null)return false;
        bool remote=_capture?.RemoteActive==true;
        int starting=remote?CurrentInputTargetId():EnvironmentRouteOrder.Local;
        int baseId=_host!=null ? 0 :
            _receiver?.ConnectedPeers.Where(p=>p.Peer.Platform=="Windows")
                .OrderBy(p=>p.Id).Select(p=>p.Id).FirstOrDefault()??0;
        if(baseId==0 && _host==null)
            baseId=_receiver?.ConnectedPeers.OrderBy(p=>p.Id)
                .Select(p=>p.Id).FirstOrDefault()??0;
        // A visual member in the same direction is NOT necessarily an
        // adjacent screen. Check the exact boundary and crossing position.
        // No fallback to sorted connection order or arbitrary left/right PC.
        int? destination=_display.AdjacentTarget(starting,edge,baseId,fraction);
        if(destination==null)return false;
        if(destination==EnvironmentRouteOrder.Local)
        {
            _capture?.ReturnLocal();
            return true;
        }
        if(destination.Value==CurrentInputTargetId())
        {
            // A user may have clicked the phone tile earlier without
            // taking authority. Crossing the edge of THAT already-selected
            // device must still request its first input grant.
            if(!remote && _capture?.IsArmed==true)
            {
                _capture.BeginLinkedHandoff(edge,fraction);
                return true;
            }
            return false;
        }
        // One authenticated hub, one keyboard/mouse hook pair: retarget the
        // SAME SourceCapture rather than disposing global hooks during the
        // cursor-edge transition. The destination still grants its own lease.
        return SelectConnectedTargetCore(destination.Value,edge,fraction,true);
    }

    private void SelectConnectedTarget(int id) =>
        SelectConnectedTargetCore(id,_preferredEdge,0.5,false);

    private bool _retargetingInput;
    private bool SelectConnectedTargetCore(int id,MonitorEdge edge,
        double fraction,bool requestHandoff)
    {
        if(_retargetingInput)return false;
        _retargetingInput=true;
        try
        {
            IInputSession? next=null;
            ConnectedPeer? selectedPeer=null;
            bool wasControllingRemote=_capture?.RemoteActive==true;
            if(id<=0 && _host!=null)
            {
                int chosen=id<0 ? -id : 0;
                if(_capture!=null && !_captureUsesInbound &&
                    _host.SelectedEnvironmentTargetId==chosen)
                    return false;
                // A release MUST go over the PREVIOUS authenticated route;
                // changing the hub destination first would release the wrong
                // phone/PC and leave keyboard authority stranded.
                _capture?.ReturnLocal();
                if(!_host.SelectEnvironmentTarget(chosen))
                {
                    Status("Environment destination is disconnected or input permission is disabled.");
                    RefreshDisplays();return false;
                }
                _captureUsesInbound=false;
                _captureInboundPeerId=0;
                next=_host;
            }
            else if(id>0 && _receiver!=null)
            {
                var peer=_receiver.ConnectedPeers.FirstOrDefault(p=>p.Id==id);
                if(peer.Peer==null || !peer.Peer.SupportsReverseInput)
                {
                    Status("Connected device does not currently allow keyboard/mouse control.");
                    return false;
                }
                if(_capture!=null && _captureUsesInbound &&
                    _captureInboundPeerId==id)return false;
                _capture?.ReturnLocal();
                if(!_receiver.SelectConnectedPeer(id))return false;
                _captureUsesInbound=true;
                _captureInboundPeerId=id;
                _incomingPeer=peer.Peer;
                selectedPeer=peer.Peer;
                next=_receiver.BindSelectedInput();
            }
            if(next==null)return false;
            // No SourceCapture.Dispose()/Arm() cycle on device switch: that
            // discarded physical key/mouse events and caused intermittent
            // Windows/Android handoff failures.
            if(_capture!=null)
                _capture.Retarget(next,edge,
                    requestHandoff&&wasControllingRemote);
            else
            {
                _capture=new SourceCapture(this,next,Status,edge,
                    preserveRawInput:true);
                _capture.NavigateEnvironment=NavigateEnvironmentFromCapture;
                _capture.Arm();
            }
            if(requestHandoff)
                _capture.BeginLinkedHandoff(edge,fraction);
            RefreshDisplays();
            Status("Destination: "+(selectedPeer?.Name ??
                _host?.RemoteSystemName ?? "shared environment")+
                ". All connected devices remain online.");
            return true;
        }
        catch(Exception ex)
        {
            Status("Input destination could not be changed: "+ex.Message);
            return false;
        }
        finally { _retargetingInput=false; }
    }

    private void ArmSelectedInbound()
    {
        if (_receiver == null || _incomingPeer?.SupportsReverseInput != true ||
            !_captureUsesInbound && _host != null) return;
        if (_capture != null) return;
        try
        {
            _capture = new SourceCapture(this, _receiver.BindSelectedInput(), Status,
                _preferredEdge, preserveRawInput: true);
            _capture.NavigateEnvironment=NavigateEnvironmentFromCapture;
            _capture.Arm();
            _captureInboundPeerId=_receiver.SelectedPeerId;
            Status("Windows controls " + _incomingPeer.Name +
                " across the existing authenticated connection.");
        }
        catch (Exception ex)
        {
            _capture?.Dispose();
            _capture = null;
            Status("Input capture unavailable: " + ex.Message);
        }
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
            _nativeInput.IsOutboundInputCaptured=() =>
                _capture?.OutboundInputReserved==true;
            _receiver = new ReceiverServer(Status, _nativeInput.Priority, ConfirmPeerAsync,
                _trusted, ConfirmTrustAsync);
            _receiver.PeerConnected += OnIncomingPeerConnected;
            _receiver.PeerDisconnected += OnIncomingPeerDisconnected;
            _receiver.PeerListChanged += OnPeerListChanged;
            _receiver.InitializeEnvironmentLayout(_display.StoredMemberOffsets);
            _receiver.EnvironmentLayoutChanged += HandleSharedEnvironmentLayout;
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
                    _discovery = new NearbyComputerDiscovery(_receiver.Fingerprint,
                        () => Volatile.Read(ref _receiver)?.CanAcceptNewPeer == true,
                        () =>
                        {
                            var neighbours = _receiver?.ConnectedDeviceFingerprints.ToList()
                                ?? new List<string>();
                            if (_host != null && _currentFingerprint.Length == 64)
                                neighbours.Add(_currentFingerprint);
                            return neighbours;
                        }, () => _host == null);
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
            _receiver.PeerListChanged -= OnPeerListChanged;
            _receiver.EnvironmentLayoutChanged -= HandleSharedEnvironmentLayout;
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

    private void OnPeerListChanged() => PostToUi(() =>
    {
        // An Android permission may be restored or disabled on the SAME TLS
        // session, without dropping the entire environment. Keep diagram,
        // keyboard-target availability and active input grant consistent.
        if(_captureUsesInbound && _receiver!=null)
        {
            int id=_receiver.SelectedPeerId;
            var active=_receiver.ConnectedPeers.FirstOrDefault(p=>p.Id==id);
            if(active.Peer!=null)
            {
                _incomingPeer=active.Peer;
                if(!active.Peer.SupportsReverseInput &&
                    _captureInboundPeerId==id && _capture!=null)
                {
                    _capture.ReturnLocal();
                    _capture.Dispose();_capture=null;
                    _captureInboundPeerId=0;
                }
                if(active.Peer.SupportsReverseInput && _capture==null &&
                    _nativeInput?.IsRunning==true)
                    ArmSelectedInbound();
            }
        }
        RefreshDisplays();
    });

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
                Status("Input monitoring unavailable; connection rejected: " + ex.Message);
                _receiver.DisconnectPeer();
                return;
            }
            // A peer becoming connected is not permission to steal keyboard
            // authority from a DIFFERENT selected Windows or Android target.
            // Use the receiver's actual selected ID, never the most recent
            // discovery event, and keep an existing capture for that same peer.
            int selectedId=_receiver.SelectedPeerId;
            if(selectedId==0){ RefreshDisplays();return; }
            var selected=_receiver.ConnectedPeers.FirstOrDefault(p=>p.Id==selectedId);
            if(selected.Peer==null)return;
            _incomingPeer=selected.Peer;
            if (_host == null) _captureUsesInbound = true;
            if (_captureUsesInbound && _incomingPeer.SupportsReverseInput)
            {
                if(_capture!=null && _captureInboundPeerId!=selectedId)
                {
                    _capture.ReturnLocal();_capture.Dispose();_capture=null;
                    _captureInboundPeerId=0;
                }
                ArmSelectedInbound();
            }
            else if (peer.Platform == "Android" && !peer.SupportsReverseInput)
            {
                Status("Android connected. Enable DeskTouch Accessibility control " +
                    "on the phone, then reconnect to allow Windows mouse and keyboard.");
            }
            RefreshDisplays();
        });
    }

    private void OnIncomingPeerDisconnected()
    {
        PostToUi(() =>
        {
            int selectedId=_receiver?.SelectedPeerId ?? 0;
            // A selection change raises "previous disconnected" even when
            // its TLS session is still online. Never dispose a just-armed
            // capture for the next selected device on that callback.
            if(selectedId>0)
            {
                var selected=_receiver!.ConnectedPeers.FirstOrDefault(p=>p.Id==selectedId);
                if(selected.Peer!=null)
                {
                    _incomingPeer=selected.Peer;
                    if(_captureUsesInbound && _captureInboundPeerId!=selectedId)
                    {
                        _capture?.ReturnLocal();
                        _capture?.Dispose();_capture=null;
                        _captureInboundPeerId=0;
                        ArmSelectedInbound();
                    }
                    RefreshDisplays();
                    return;
                }
            }
            if(_captureUsesInbound)
            {
                _capture?.ReturnLocal();_capture?.Dispose();_capture=null;
                _captureInboundPeerId=0;
            }
            _incomingPeer=null;
            if(_host!=null && _captureUsesInbound)
            {
                _captureUsesInbound=false;
                SelectConnectedTarget(0);
            }
            RefreshDisplays();
        });
    }

    private void OnIncomingPeerTopologyChanged(ConnectedPeer peer)
    {
        PostToUi(() =>
        {
            // Ignore nonselected members when updating the active pointer
            // geometry. A phone's layout update must not re-map the PC's
            // mouse coordinates (or steal keyboard authority).
            int selectedId=_receiver?.SelectedPeerId??0;
            var selected=_receiver?.ConnectedPeers
                .FirstOrDefault(p=>p.Id==selectedId).Peer;
            if(selected!=null && selected.Equals(peer))
            {
                _incomingPeer=selected;
                if((_captureUsesInbound||_host==null) &&
                    selectedId==_captureInboundPeerId)
                    _capture?.RefreshRemoteGeometry(peer.Monitors);
            }
            RefreshDisplays();
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
            Status($"Pairing code copied for {Environment.MachineName}. Paste it into Add devices on the other PC. Keep it private.");
        }
        catch (Exception ex) { Status("Unable to copy pairing code: " + ex.Message); }
    }

    private async Task AddComputerAsync()
    {
        if (_connecting || _closing.IsCancellationRequested) return;
        // The Windows client joins ONE environment. Adding another machine
        // must never silently drop the existing hub or keyboard lease.
        if(_host != null || (_receiver?.ConnectedPeerCount ?? 0)>0)
        {
            var members=_host!=null ? _host.EnvironmentMembers
                .Select(x=>x.Platform+" • "+x.Name).Distinct().ToArray()
                : _receiver?.ConnectedPeers.Select(x=>x.Peer.Platform+" • "+x.Peer.Name)
                    .ToArray() ?? Array.Empty<string>();
            string known=members.Length>0 ? string.Join(Environment.NewLine,members) :
                "Your current environment hub";
            MessageBox.Show(this,
                "You are already connected to a shared DeskTouch environment." +
                Environment.NewLine+Environment.NewLine+
                "Connected members:"+Environment.NewLine+known+
                Environment.NewLine+Environment.NewLine+
                "To add another PC or phone, open DeskTouch on that device and " +
                "join this environment's Windows hub. Existing devices remain " +
                "connected; no session needs to be replaced.",
                "DeskTouch shared environment",MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using var dialog = new PairComputerDialog();
        _manualPairDialogOpen = true;
        DialogResult choice;
        try { choice = dialog.ShowDialog(this); }
        finally { _manualPairDialogOpen = false; }
        if (choice != DialogResult.OK || _connecting || _closing.IsCancellationRequested) return;
        _connecting = true;
        // Never tear down Windows's incoming TLS listener when this PC also
        // initiates an outgoing connection. Other mobiles and PCs may connect
        // concurrently. The same authenticated hub arbitrates input authority.
        UpdateButtons();
        Status("Connecting securely. The other computer may ask its user to approve...");
        try
        {
            void OnFailure(string reason)
            {
                Status(reason);
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(DisconnectHost); }
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
            if (_incomingPeer != null) OnIncomingPeerConnected(_incomingPeer);
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
        // Receiver and outgoing host share a single local physical-input
        // monitor; never create competing global hook registrations.
        _capture?.Dispose();
        _capture = null;
        _nativeInput ??= new PhysicalInputMonitor(this);
        _nativeInput.IsOutboundInputCaptured=() =>
            _capture?.OutboundInputReserved==true;
        _nativeInput.Start();
        session.AttachReverseInput(_nativeInput.Priority,
            () => _capture == null ||
                (!_capture.RemoteActive && !_capture.IsClaimPending));
        session.AuthorityDiagnostic += Status;
        session.InputDeliveryWarning += Status;
        _captureUsesInbound = false;
        _capture = new SourceCapture(this, session, Status, _preferredEdge,
            preserveRawInput: true);
        _capture.NavigateEnvironment=NavigateEnvironmentFromCapture;
        session.AuthorityControlPacket += HandleRemoteState;
        session.RemoteTopologyChanged += HandleRemoteTopology;
        session.RemoteNameChanged += HandleRemoteName;
        session.EnvironmentRosterChanged += HandleEnvironmentRoster;
        session.EnvironmentLayoutChanged += HandleSharedEnvironmentLayout;
        if(session.SharedLayout.Count>0)
            _display.RestoreMemberOffsets(session.SharedLayout);
        if (session.RemoteSystemName is { Length: > 0 } actual)
            _remoteAddress = DisplayArrangementControl.CleanDeviceName(actual,
                _remoteAddress);
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
            _closing.IsCancellationRequested ||
            _trusted?.AutoConnectEnabled != true || _trusted.Outgoing.Count == 0 ||
            DateTime.UtcNow < _retryTrustedAfter || _receiver == null)
            return;
        _scanningTrusted = true;
        try
        {
            // Discovery finds CURRENT network addresses; the saved file has
            // NO IP address, only the receiver's persistent TLS fingerprint.
            var peers = await NearbyComputerDiscovery.FindAsync(_closing.Token);
            var remembered = peers.Select(p => new { Peer = p,
                Saved = _trusted.FindAutoConnect(p.Fingerprint) }).ToArray();
            // Reconnect to the existing environment hub; never silently
            // start a second, isolated PC-to-PC session with a leaf when a
            // real hub is discoverable but has not yet been approved.
            bool hubAvailable = peers.Any(p => p.IsHub && p.CanReceive);
            var found = remembered.FirstOrDefault(x =>
                x.Saved != null && x.Peer.IsHub);
            if(found == null && !hubAvailable)
                found = remembered.FirstOrDefault(x => x.Saved != null);
            if (found == null || _host != null ||
                _connecting || !_trusted.AutoConnectEnabled)
                return;
            _connecting = true;
            // Outbound auto-connect must not make Windows disappear or mark
            // it Busy to other incoming Android/Windows devices.
            Status($"Trusted {found.Saved!.Name} discovered. Verifying persistent device identity...");
            try
            {
                void OnFailure(string reason)
                {
                    Status(reason);
                    PostToUi(DisconnectHost);
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
        PostToUi(() => { if (!_captureUsesInbound) ApplyAuthorityState(packet, reverse: false); });

    private void HandleReverseState(Packet packet) =>
        PostToUi(() => { if (_captureUsesInbound || _host == null)
            ApplyAuthorityState(packet, reverse: true); });

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

    private void OnEnvironmentMemberMoved(string key,PointF point)
    {
        // Send a dragged member's layout position to the authenticated hub;
        // never create an independent PC-to-PC layout or another pairing.
        if(_host!=null)
        {
            if(!_host.PublishEnvironmentPlacement(key,point))
                Status("Could not synchronize this device position to the environment hub.");
        }
        else if(_receiver!=null && !_receiver.UpdateEnvironmentPlacement(key,point))
            Status("Cannot synchronize position for an offline or untrusted device.");
    }

    private void HandleSharedEnvironmentLayout(
        IReadOnlyDictionary<string,PointF> canonicalPositions) => PostToUi(() =>
    {
        _display.RestoreMemberOffsets(canonicalPositions);
        SavePreferredEdge();
        // Applying a hub snapshot MUST NOT re-send it as a local drag.
        RefreshDisplays();
    });

    private void HandleEnvironmentRoster() => PostToUi(() =>
    {
        if(_host == null) return;
        // The authenticated hub provides current members. One connection
        // stays alive while target keyboard/mouse authority changes.
        UpdateTargetChoice();
        RefreshDisplays();
        Status($"Shared environment: {_host.EnvironmentMembers.Count} available devices.");
    });

    private void HandleRemoteName(string actualName)
    {
        PostToUi(() =>
        {
            _remoteAddress = DisplayArrangementControl.CleanDeviceName(actualName,
                _remoteAddress);
            RefreshDisplays();
        });
    }

    private void HandleRemoteTopology(MonitorTopology topology)
    {
        PostToUi(() =>
        {
            if (!_captureUsesInbound)
                _capture?.RefreshRemoteGeometry(topology);
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
        if (!_captureUsesInbound) _host?.PublishSourcePlacement(edge);
        if (_captureUsesInbound || _host == null)
            _receiver?.PublishPlacement(Opposite(edge));
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
            _host.InputDeliveryWarning -= Status;
            _host.RemoteTopologyChanged -= HandleRemoteTopology;
            _host.RemoteNameChanged -= HandleRemoteName;
            _host.EnvironmentRosterChanged -= HandleEnvironmentRoster;
            _host.EnvironmentLayoutChanged -= HandleSharedEnvironmentLayout;
            _host.RemotePlacementChanged -= HandleRemotePlacement;
        }
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        // Outgoing controller also hosts a native priority monitor for
        // incoming reverse claims. It belongs to this host session.
        if (hadHost && _receiver?.ConnectedPeerCount == 0)
        {
            _nativeInput?.Dispose();
            // Keep this same safety monitor object available for the receiver
            // to arm when the next authenticated peer arrives.
            if (_receiver == null) _nativeInput = null;
        }
        _remoteAddress = "";
        _currentFingerprint = "";
        _captureUsesInbound = _incomingPeer != null;
        _display.Remote = null;
        if (_incomingPeer != null && !_closing.IsCancellationRequested)
            OnIncomingPeerConnected(_incomingPeer);
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
            if(doc.RootElement.TryGetProperty("MemberPositions",out var positions) &&
                positions.ValueKind==JsonValueKind.Object)
            {
                var offsets=new Dictionary<string,PointF>(StringComparer.Ordinal);
                foreach(var p in positions.EnumerateObject())
                {
                    if(p.Name.Length>150 || p.Value.ValueKind!=JsonValueKind.Object)
                        continue;
                    if(p.Value.TryGetProperty("X",out var x) &&
                        p.Value.TryGetProperty("Y",out var y) &&
                        x.TryGetSingle(out var px) && y.TryGetSingle(out var py))
                        offsets[p.Name]=new PointF(px,py);
                }
                _display.RestoreMemberOffsets(offsets);
            }
        }
        catch { /* invalid layout is ignored; Windows topology is unchanged */ }
    }

    private void SavePreferredEdge()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_layoutPath)!);
            File.WriteAllText(_layoutPath, JsonSerializer.Serialize(new {
                RemoteEdge = _preferredEdge.ToString(),
                MemberPositions = _display.StoredMemberOffsets.ToDictionary(
                    p=>p.Key,p=>new { X=p.Value.X,Y=p.Value.Y })
            }));
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
                if (!_captureUsesInbound)
                    _capture?.UpdateLayouts(current, _host.RemoteMonitors);
                _host.PublishSourceTopology(current);
            }
            if (_receiver != null)
            {
                if (_incomingPeer != null && (_captureUsesInbound || _host == null))
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
