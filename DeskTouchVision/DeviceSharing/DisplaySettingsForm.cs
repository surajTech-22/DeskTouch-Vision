using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Forms;
using DeskTouchVision;
using Microsoft.Win32;

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
    private readonly Label _liveLinkLabel = new()
    {
        Name = "LiveLinkStatus",
        Dock = DockStyle.Top,
        Height = 20,
        Font = new Font("Segoe UI Semibold", 9),
        ForeColor = Color.FromArgb(148, 214, 178),
        AutoEllipsis = true,
        Text = "Live link: Waiting for connected phone or computer"
    };
    private readonly Button _add = Button("Add devices", true);
    private readonly Button _copyCode = Button("Copy pairing code");
    private readonly Button _disconnect = Button("Disconnect");
    private readonly Button _pausePhonePayments = Button("Pause phone for payments");
    private readonly ToolStripMenuItem _pausePhonePaymentsTray = new("Pause phone for payments");
    private readonly Button _shortcuts = Button("Shortcuts");
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
    private System.Windows.Forms.Timer? _liveLinkTimer;
    private bool _scanningTrusted;
    private bool _manualPairDialogOpen;
    private DateTime _retryTrustedAfter = DateTime.MinValue;
    private string _currentFingerprint = "";
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private bool _formDisposed;
    private ToolStripMenuItem? _startupMenu;
    private volatile bool _exitRequested;
    private bool _trayTipShown;
    private readonly bool _startInTray;
    private volatile bool _isHiddenInTray;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _exitWait;
    private volatile bool _showRequestedBeforeShown;
    private ReceiverServer? _receiver;
    private NearbyComputerDiscovery? _discovery;
    private AdbCableBridge? _adbBridge;
    private PhysicalInputMonitor? _nativeInput;
    private HostSession? _host;
    private ConnectedPeer? _incomingPeer;
    private SourceCapture? _capture;
    private MonitorEdge _preferredEdge = MonitorEdge.Right;
    private double _pointerSensitivity = 1.0;
    private readonly TrackBar _sensitivityBar = new()
    {
        Name = "PointerSensitivitySlider",
        Minimum = 25,
        Maximum = 250,
        Value = 100,
        SmallChange = 5,
        LargeChange = 25,
        TickFrequency = 25,
        AutoSize = false,
        Height = 32,
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(31, 32, 36)
    };
    private readonly Label _sensitivityValue = new()
    {
        Name = "PointerSensitivityValue",
        Text = "100%",
        Dock = DockStyle.Right,
        Width = 62,
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = Color.FromArgb(191, 215, 238)
    };
    private bool _updatingSensitivityUi;
    private bool _connecting;
    private string _remoteAddress = "";
    private readonly int _taskbarCreatedMessage = unchecked((int)Win.RegisterWindowMessage("TaskbarCreated"));
    private bool _performedLegacySolicitAttempt;

    private static Icon LoadAppIcon()
    {
        try
        {
            string exe = Application.ExecutablePath;
            if (File.Exists(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

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
        Icon = LoadAppIcon();
        if (startInTray) Opacity = 0;

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

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 266,
            Padding = new Padding(24, 10, 24, 7), BackColor = BackColor };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52,
            Name = "ConnectionActions",
            FlowDirection = FlowDirection.LeftToRight, AutoSize = false,
            // Wrapping creates a hidden second row beneath the fixed-height
            // action bar at high Windows DPI. Keep one stable visible row.
            WrapContents = false, AutoScroll = true };
        _pausePhonePayments.Click += (_, _) => PausePhoneForPayments();
        _pausePhonePaymentsTray.Click += (_, _) => PausePhoneForPayments();
        _shortcuts.Click += (_, _) => ShowConnectedShortcuts();
        foreach (var b in new[] { _add, _trustDevice, _copyCode, _shortcuts, _pausePhonePayments, _disconnect, _nativeDisplaySettings, _androidDownload })
        {
            b.Height = 35;
            b.Margin = new Padding(0, 2, 10, 5);
            actions.Controls.Add(b);
        }
        var sensitivityRow = new Panel { Dock = DockStyle.Top, Height = 40,
            Padding = new Padding(0, 2, 0, 4), BackColor = BackColor };
        var sensitivityLabel = new Label
        {
            Text = "Mouse sensitivity",
            Dock = DockStyle.Left,
            Width = 156,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(204, 210, 220)
        };
        _sensitivityBar.ValueChanged += (_, _) =>
        {
            if (_updatingSensitivityUi) return;
            _pointerSensitivity = _sensitivityBar.Value / 100.0;
            _sensitivityValue.Text = _sensitivityBar.Value + "%";
            _capture?.SetPointerSensitivity(_pointerSensitivity);
        };
        _sensitivityBar.MouseUp += (_, _) => SavePreferredEdge();
        _sensitivityBar.KeyUp += (_, _) => SavePreferredEdge();
        sensitivityRow.Controls.Add(_sensitivityBar);
        sensitivityRow.Controls.Add(_sensitivityValue);
        sensitivityRow.Controls.Add(sensitivityLabel);
        var deviceInfo = new Panel { Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(41, 43, 49),
            Padding = new Padding(14, 10, 12, 7) };
        _summary.Dock = DockStyle.Top;
        _summary.Height = 39;
        _summary.Font = new Font("Segoe UI Semibold", 11);
        _summary.ForeColor = Color.FromArgb(230, 231, 234);
        _summary.AutoEllipsis = true;
        var explanation = new Label { Dock = DockStyle.Fill,
            Text = "Your keyboard and mouse always take priority when used. " +
                   "Drag devices in the diagram to arrange screen edges. " +
                   "Phone toggle: Ctrl + Alt + M  •  Emergency return: Ctrl + Alt + Shift + F12.",
            ForeColor = Color.FromArgb(179, 184, 193) };
        // WinForms docks in reverse z-order: add Fill before Top to give
        // the title and live link status their own space instead of covering the explanation.
        deviceInfo.Controls.Add(explanation);
        deviceInfo.Controls.Add(_liveLinkLabel);
        deviceInfo.Controls.Add(_summary);
        _status.Dock = DockStyle.Bottom;
        _status.Height = 43;
        _status.Padding = new Padding(10, 9, 10, 6);
        _status.ForeColor = Color.FromArgb(191, 215, 238);
        _status.AutoEllipsis = true;

        bottom.Controls.Add(deviceInfo);
        bottom.Controls.Add(sensitivityRow);
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
            if (_exitRequested)
            {
                BeginInvoke(ExitDeskTouch);
                return;
            }
            RefreshDisplays();
            StartListening();
            try
            {
                WindowsStartup.EnsureDefault();
                if (_startupMenu != null) _startupMenu.Checked = WindowsStartup.Enabled;
            }
            catch (Exception ex) { Status("Windows sign-in startup could not be enabled: " + ex.Message); }
            UpdateClient.StartEventUpdates(this);
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _trustedScanTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _trustedScanTimer.Tick += async (_, _) =>
            {
                EnsureCaptureArmedIfConnected();
                await ScanTrustedAsync();
            };
            _trustedScanTimer.Start();
            _ = Task.Delay(450, _closing.Token).ContinueWith(
                _ => PostToUi(() => _ = ScanTrustedAsync()),
                TaskScheduler.Default);
            _liveLinkTimer = new System.Windows.Forms.Timer { Interval = 750 };
            _liveLinkTimer.Tick += (_, _) => UpdateLiveLinkDisplay();
            _liveLinkTimer.Start();
            if (_trustLoadError != null)
                Status("Trusted device settings unavailable; reconnect disabled: " + _trustLoadError);
            if (_startInTray && !_showRequestedBeforeShown)
            {
                HideToTray(showBalloon: false);
                if (_showRequestedBeforeShown)
                    ShowFromTray();
            }
            else
            {
                BringWindowToForegroundSafely();
            }
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
        _trayMenu.Items.Add(_pausePhonePaymentsTray);
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
            Icon = Icon ?? LoadAppIcon(),
            Text = "DeskTouch Vision - Windows device sharing",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    internal static Rectangle EnsureVisibleBounds(
        Rectangle bounds,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea)
    {
        foreach (var area in workingAreas)
        {
            var visible = Rectangle.Intersect(bounds, area);
            if (visible.Width >= 160 && visible.Height >= 90 &&
                bounds.Top >= area.Top - 8)
                return bounds;
        }
        if (fallbackWorkingArea.Width <= 0 || fallbackWorkingArea.Height <= 0)
            return bounds;
        int w = Math.Min(Math.Max(1, bounds.Width), fallbackWorkingArea.Width);
        int h = Math.Min(Math.Max(1, bounds.Height), fallbackWorkingArea.Height);
        int x = fallbackWorkingArea.Left + Math.Max(0, (fallbackWorkingArea.Width - w) / 2);
        int y = fallbackWorkingArea.Top + Math.Max(0, (fallbackWorkingArea.Height - h) / 2);
        return new Rectangle(x, y, w, h);
    }

    internal void EnableLifecycleSignals()
    {
        if (_showSignal != null) return;
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, timedOut) =>
            {
                if (timedOut || _closing.IsCancellationRequested) return;
                _showRequestedBeforeShown = true;
                if (!IsDisposed && IsHandleCreated) PostToUi(ShowFromTray);
            }, null, Timeout.Infinite, executeOnlyOnce: false);

            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, timedOut) =>
            {
                if (timedOut || _closing.IsCancellationRequested) return;
                _exitRequested = true;
                if (!IsDisposed && IsHandleCreated) PostToUi(ExitDeskTouch);
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch { /* HWND_BROADCAST and CloseMainWindow remain active as fallback. */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_exitRequested && !IsDisposed) PostToUi(ExitDeskTouch);
    }

    private void BringWindowToForegroundSafely()
    {
        if (IsDisposed || _exitRequested || _closing.IsCancellationRequested) return;
        // Do not toggle ShowInTaskbar: WinForms may recreate the HWND, which
        // would invalidate Raw Input registrations and break mouse handoff.
        Opacity = 1.0;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized || (IsHandleCreated && Win.IsIconic(Handle)))
        {
            WindowState = FormWindowState.Normal;
            if (IsHandleCreated) Win.ShowWindow(Handle, Win.SW_RESTORE);
        }
        else if (IsHandleCreated)
        {
            Win.ShowWindow(Handle, Win.SW_SHOW);
        }

        // Clamp bounds only after restoring from Minimized so Windows' (-32000, -32000)
        // iconic coordinates cannot corrupt restored screen placement.
        var screens = Screen.AllScreens;
        if (screens.Length > 0)
        {
            var clamped = EnsureVisibleBounds(
                Bounds,
                screens.Select(s => s.WorkingArea).ToArray(),
                Screen.FromControl(this).WorkingArea);
            if (clamped != Bounds)
                Bounds = clamped;
        }

        if (IsHandleCreated)
        {
            Win.BringWindowToTop(Handle);
            Win.SetForegroundWindow(Handle);
        }
        Activate();
        BringToFront();

        // If Windows 11 Foreground Lock kept another window on top, briefly pulse TopMost.
        if (IsHandleCreated && Win.GetForegroundWindow() != Handle)
        {
            bool prevTopMost = TopMost;
            try
            {
                TopMost = true;
                Win.BringWindowToTop(Handle);
                Win.SetForegroundWindow(Handle);
                Activate();
            }
            finally
            {
                TopMost = prevTopMost;
            }
        }
    }

    internal void ShowFromTray()
    {
        if (IsDisposed || _exitRequested || _closing.IsCancellationRequested) return;
        _showRequestedBeforeShown = true;
        _isHiddenInTray = false;
        BringWindowToForegroundSafely();
        BroadcastOnlinePresence();
    }

    private void HideToTray(bool showBalloon = true)
    {
        _isHiddenInTray = true;
        // Hide() naturally removes an invisible Form from the taskbar
        // WITHOUT changing ShowInTaskbar (which can recreate the HWND).
        // The same handle must keep receiving raw mouse input even in tray.
        // The UI message loop continues to receive GitHub release SSE events.
        Hide();
        Opacity = 1.0;
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
        _liveLinkTimer?.Stop();
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
            "Add devices" => 145, "Get Android app" => 150, _ => 112 },
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = Color.FromArgb(85, 89, 100), BorderSize = 1 },
        ForeColor = Color.White,
        BackColor = primary ? Color.FromArgb(27, 104, 190)
                            : Color.FromArgb(56, 58, 65),
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI", 9)
    };

    internal static void RotateLogIfNeeded(string logPath, long maxBytes = 512 * 1024)
    {
        var info = new FileInfo(logPath);
        if (!info.Exists || info.Length <= maxBytes) return;
        string backup = Path.Combine(info.DirectoryName!, "connection.prev.log");
        File.Move(logPath, backup, overwrite: true);
    }

    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private int _logWriterStarted;

    private static void BoostProcessResponsiveness()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            if (current.PriorityClass == ProcessPriorityClass.Normal)
                current.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch { /* Best-effort process priority; never fail sharing if restricted. */ }
    }

    private async Task WriteLogsAsync()
    {
        try
        {
            string logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskTouchVision", "logs");
            string logFile = Path.Combine(logs, "connection.log");
            await foreach (string line in _logChannel.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(logs);
                    RotateLogIfNeeded(logFile);
                    await File.AppendAllTextAsync(logFile, line);
                }
                catch { /* Never block Windows input for diagnostic logging. */ }
            }
        }
        catch { }
    }

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
        if (Interlocked.CompareExchange(ref _logWriterStarted, 1, 0) == 0)
            _ = Task.Run(WriteLogsAsync);
        // Status strings intentionally contain no pairing secrets, keys or
        // raw keyboard events. Keep connection-stage errors diagnosable.
        _logChannel.Writer.TryWrite(
            DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        UpdateButtons();
    }

    private void LogDiagnostic(string message)
    {
        if (IsDisposed || Disposing) return;
        if (Interlocked.CompareExchange(ref _logWriterStarted, 1, 0) == 0)
            _ = Task.Run(WriteLogsAsync);
        _logChannel.Writer.TryWrite(
            DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
    }

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        bool canTrustOutbound = _trusted != null && _host != null &&
            !string.IsNullOrEmpty(_currentFingerprint) &&
            !_trusted.HasBidirectionalTrust(_remoteAddress, _currentFingerprint);
        bool inboundIsWindows = string.Equals(_incomingPeer?.Platform, "Windows", StringComparison.Ordinal) ||
            _receiver?.ConnectedPeers.Any(p =>
                string.Equals(p.Peer.Platform, "Windows", StringComparison.Ordinal) &&
                string.Equals(p.Peer.Name, _receiver.ConnectedPeerName, StringComparison.OrdinalIgnoreCase)) == true;
        bool canTrustInbound = _trusted != null && _host == null && _receiver != null &&
            !string.IsNullOrEmpty(_receiver.ConnectedPeerName) &&
            (inboundIsWindows
                ? !_trusted.HasBidirectionalTrust(_receiver.ConnectedPeerName, _receiver.ConnectedPeerFingerprint)
                : !_trusted.IsTrustedIncoming(_receiver.ConnectedPeerName, _receiver.ConnectedPeerFingerprint));
        bool canTrust = canTrustOutbound || canTrustInbound;
        _trustDevice.Enabled = canTrust;
        _trustDevice.Visible = canTrust;
        _manageTrusted.Enabled = _trusted != null;
        _add.Enabled = !_connecting && !_closing.IsCancellationRequested;
        _copyCode.Enabled = _receiver?.CanAcceptNewPeer == true &&
            !_connecting && _host == null;
        bool canPausePhone = _receiver?.HasReadyAndroidPeer ?? false;
        _pausePhonePayments.Enabled = canPausePhone;
        _pausePhonePaymentsTray.Enabled = canPausePhone;
        _disconnect.Enabled = _host != null ||
            _receiver?.ConnectedPeerCount > 0 || _connecting;
        var localScreens = _display.Local?.Monitors.Count ?? 0;
        var localDesc = _display.Local?.DescribeSummary("Windows") ?? $"{localScreens} display(s)";
        if (_host != null)
        {
            var localExtra = GetDistinctLocalReceiverPeersWhenHosted();
            int members = _host.EnvironmentMembers.Count + localExtra.Length;
            var phoneMember = _host.EnvironmentMembers.FirstOrDefault(m => m.Platform == "Android");
            string phoneInfo = phoneMember != null
                ? $"  +  {phoneMember.Name} ({MonitorTopology.FromWire(phoneMember.Layout).DescribeSummary("Android")})"
                : "";
            if (string.IsNullOrEmpty(phoneInfo))
            {
                var localPhone = localExtra.Select(p => p.Peer).FirstOrDefault(p => p.Platform == "Android");
                if (localPhone != null)
                    phoneInfo = $"  +  {localPhone.Name} ({localPhone.FormatScreenInfo()})";
            }
            _summary.Text = $"{Environment.MachineName} ({localDesc})  +  " +
                            $"{_remoteAddress} ({_host.RemoteMonitors.DescribeSummary("Windows")})" +
                            $"{phoneInfo}  ·  {members} device(s)";
        }
        else if (_incomingPeer != null)
        {
            var phonePeer = _incomingPeer.Platform == "Android"
                ? _incomingPeer
                : _receiver?.ConnectedPeers.Select(p => p.Peer).FirstOrDefault(p => p.Platform == "Android")
                  ?? _receiver?.BridgedPeers.Select(b => b.Peer).FirstOrDefault(p => p.Platform == "Android");
            string extraPhone = phonePeer != null && !ReferenceEquals(phonePeer, _incomingPeer) &&
                !string.Equals(phonePeer.Name, _incomingPeer.Name, StringComparison.OrdinalIgnoreCase)
                ? $"  +  {phonePeer.Name} ({phonePeer.FormatScreenInfo()})"
                : "";
            int totalEnv = _receiver?.ActiveEnvironmentDeviceCount ?? (1 + (_receiver?.ConnectedPeerCount ?? 0));
            _summary.Text = $"{Environment.MachineName} ({localDesc})  +  " +
                            $"{_incomingPeer.Name} ({_incomingPeer.FormatScreenInfo()}){extraPhone}  ·  " +
                            $"{totalEnv} in environment";
        }
        else
            _summary.Text = $"{Environment.MachineName}  ·  {localDesc}" +
                            (_receiver != null ? "     •     Ready to pair" : "     •     Not available to other PCs");
        UpdateLiveLinkDisplay();
    }

    private void UpdateLiveLinkDisplay()
    {
        if (IsDisposed) return;
        var snap = (_host != null && !(_captureUsesInbound && _capture?.RemoteActive == true))
            ? _host.GetActiveLinkSummary()
            : _receiver?.GetActiveLinkSummary();
        var (color, text) = FormatLiveLinkDisplay(
            snap,
            KeyboardLayoutHelper.DescribeCurrentWindowsLayout());
        _liveLinkLabel.ForeColor = color;
        _liveLinkLabel.Text = text;
    }

    internal static (Color Color, string Text) FormatLiveLinkDisplay(
        ReceiverServer.ActiveLinkSnapshot? snap,
        string currentWindowsLayout)
    {
        if (snap is not { Connected: true })
        {
            return (
                Color.FromArgb(172, 184, 198),
                "Live link: Waiting for connection  •  Keyboard: " + currentWindowsLayout
            );
        }
        var kb = KeyboardLayoutHelper.TryParseWireSummary(snap.KeyboardInfoJson);
        string kbText = kb != null ? $"  •  Keyboard: {kb.Keyboard}" : "";
        if (snap.TransportBand == "cable")
        {
            return (
                Color.FromArgb(136, 224, 168),
                $"Live link [{snap.PeerName}]: Data cable{kbText}"
            );
        }
        string bandText = snap.TransportBand switch
        {
            "cable" => "Data cable",
            "cellular" => "Mobile network",
            "ethernet" => "Wired network (Ethernet)",
            "5GHz" => "5 GHz wireless",
            "6GHz" => "6 GHz wireless",
            "2.4GHz" => "2.4 GHz wireless",
            _ => "Wireless"
        };
        string speedText = snap.LinkSpeedMbps >= 1000
            ? $" ({(snap.LinkSpeedMbps % 1000 == 0 ? (snap.LinkSpeedMbps / 1000).ToString() : (snap.LinkSpeedMbps / 1000.0).ToString("F1"))} Gbps)"
            : (snap.LinkSpeedMbps > 0 ? $" ({snap.LinkSpeedMbps} Mbps)" : "");
        string pingText = snap.RttMs > 0 ? $"{snap.RttMs:F1} ms ping" : "measuring ping...";
        string lossText = snap.LossPermille >= 1.0 ? $"  •  loss {snap.LossPermille / 10.0:F1}%" : "";
        Color color = snap.RttMs is > 0 and <= 20
            ? Color.FromArgb(136, 224, 168)
            : (snap.RttMs > 60 ? Color.FromArgb(245, 200, 120) : Color.FromArgb(170, 212, 245));
        return (
            color,
            $"Live link [{snap.PeerName}]: {bandText}{speedText}  •  {pingText}  •  jitter {snap.JitterMs:F1} ms{lossText}{kbText}"
        );
    }

    private void ShowConnectedShortcuts()
    {
        var snap = (_host != null && !(_captureUsesInbound && _capture?.RemoteActive == true))
            ? _host.GetActiveLinkSummary()
            : _receiver?.GetActiveLinkSummary();
        var phoneSummary = KeyboardLayoutHelper.TryParseWireSummary(snap?.KeyboardInfoJson);
        using var dialog = new KeyboardShortcutsDialog(
            snap is { Connected: true } ? snap.PeerName : null,
            KeyboardLayoutHelper.DescribeCurrentWindowsLayout(),
            phoneSummary,
            _liveLinkLabel.Text);
        dialog.ShowDialog(this);
    }

    private (int Id, ConnectedPeer Peer)[] GetDistinctLocalReceiverPeersWhenHosted()
    {
        if (_host == null || _receiver == null)
            return Array.Empty<(int Id, ConnectedPeer Peer)>();
        var hostedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_currentFingerprint))
            hostedIds.Add(_currentFingerprint);
        foreach (var member in _host.EnvironmentMembers)
        {
            if (!string.IsNullOrWhiteSpace(member.StableIdentity))
                hostedIds.Add(member.StableIdentity);
        }
        return _receiver.ConnectedPeers
            .Where(item =>
            {
                string layoutId = ReceiverServer.LayoutIdentity(item.Peer);
                if (hostedIds.Contains(layoutId)) return false;
                return string.IsNullOrWhiteSpace(item.Peer.DeviceFingerprint) ||
                       !hostedIds.Contains(item.Peer.DeviceFingerprint);
            })
            .ToArray();
    }

    private (int Id, ConnectedPeer Peer)? FindLocalReceiverPeerByIdentity(string? stableIdentity)
    {
        if (_receiver == null || string.IsNullOrWhiteSpace(stableIdentity)) return null;
        foreach (var (id, peer) in _receiver.ConnectedPeers)
        {
            if (string.Equals(ReceiverServer.LayoutIdentity(peer), stableIdentity, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(peer.DeviceFingerprint) &&
                 string.Equals(peer.DeviceFingerprint, stableIdentity, StringComparison.OrdinalIgnoreCase)))
                return (id, peer);
        }
        return null;
    }

    private void RefreshDisplays()
    {
        try
        {
            _display.LocalDeviceName = Environment.MachineName;
            _display.LocalIsEnvironmentHub = _host == null;
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
                _display.RemotePlatform=hub?.Platform;
                _display.RemoteScreenDetails=hub?.Platform == "Android"
                    ? baseTopology.DescribeSummary("Android") : null;
                foreach(var member in _host.EnvironmentMembers)
                {
                    if(member.Id==0 || member.Id==_host.OwnEnvironmentMemberId)continue;
                    var memberTopology = MonitorTopology.FromWire(member.Layout);
                    var localDirect = FindLocalReceiverPeerByIdentity(member.StableIdentity);
                    int tileTargetId = localDirect?.Id ?? -member.Id;
                    tiles.Add(new EnvironmentDisplayTile(tileTargetId,member.Name,
                        member.Platform,memberTopology.Monitors.Count,
                        localDirect?.Peer.SupportsReverseInput ?? member.SupportsReverseInput,
                        member.StableIdentity,
                        localDirect?.Peer.Monitors ?? memberTopology,
                        localDirect?.Peer.FormatScreenInfo() ?? memberTopology.DescribeSummary(member.Platform)));
                }
                foreach (var (id, peer) in GetDistinctLocalReceiverPeersWhenHosted())
                {
                    tiles.Add(new EnvironmentDisplayTile(id, peer.Name,
                        peer.Platform, peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput, peer.DeviceFingerprint,
                        peer.Monitors, peer.FormatScreenInfo()));
                }
            }
            else
            {
                var connected=_receiver?.ConnectedPeers ??
                    Array.Empty<(int Id,ConnectedPeer Peer)>();
                var bridged=_receiver?.BridgedPeers ??
                    Array.Empty<(int VirtualId, ConnectedPeer Peer, int BridgeSessionId)>();
                // First connected Windows PC is a permanent layout anchor.
                // A selected Android phone remains beside it, not in its slot.
                var main=connected.Where(p=>p.Peer.Platform=="Windows")
                    .OrderBy(p=>p.Id).FirstOrDefault();
                if(main.Peer==null)
                    main=connected.OrderBy(p=>p.Id).FirstOrDefault();
                if(main.Peer==null && bridged.Count>0)
                    main=(bridged[0].VirtualId, bridged[0].Peer);
                baseTargetId=main.Id;
                _display.Remote=main.Peer?.Monitors;
                _display.RemoteDeviceName=main.Peer?.Name ?? "Connected device";
                _display.RemotePlatform=main.Peer?.Platform;
                _display.RemoteScreenDetails=main.Peer != null
                    ? main.Peer.FormatScreenInfo() : null;
                var shownIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var (id,peer) in connected)
                {
                    shownIdentities.Add(ReceiverServer.LayoutIdentity(peer));
                    if(id==baseTargetId)continue;
                    tiles.Add(new EnvironmentDisplayTile(id,peer.Name,
                        peer.Platform,peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput,peer.DeviceFingerprint,
                        peer.Monitors,peer.FormatScreenInfo()));
                }
                foreach(var (vid, peer, _) in bridged)
                {
                    if(!shownIdentities.Add(ReceiverServer.LayoutIdentity(peer))) continue;
                    if(vid==baseTargetId) continue;
                    tiles.Add(new EnvironmentDisplayTile(vid, peer.Name,
                        peer.Platform, peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput, peer.DeviceFingerprint,
                        peer.Monitors, peer.FormatScreenInfo()));
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
            var addedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_host != null)
            {
                _targetChoice.Items.Add(new TargetOption(0,
                    "Environment hub  •  " + (_host.EnvironmentMembers
                        .FirstOrDefault(m => m.Id==0)?.Name ?? _remoteAddress)));
                foreach(var member in _host.EnvironmentMembers)
                {
                    if(member.Id==0 || member.Id==_host.OwnEnvironmentMemberId) continue;
                    if(!string.IsNullOrWhiteSpace(member.StableIdentity))
                        addedIdentities.Add(member.StableIdentity);
                    var localDirect = FindLocalReceiverPeerByIdentity(member.StableIdentity);
                    if(localDirect != null)
                    {
                        if(localDirect.Value.Peer.SupportsReverseInput)
                            _targetChoice.Items.Add(new TargetOption(localDirect.Value.Id,
                                localDirect.Value.Peer.Platform + "  •  " + localDirect.Value.Peer.Name));
                        continue;
                    }
                    if(!member.SupportsReverseInput) continue;
                    _targetChoice.Items.Add(new TargetOption(-member.Id,
                        "In same environment  •  " + member.Platform + "  •  " + member.Name));
                }
            }
            if (_receiver != null)
            {
                foreach (var (id, peer) in _receiver.ConnectedPeers)
                {
                    addedIdentities.Add(ReceiverServer.LayoutIdentity(peer));
                    _targetChoice.Items.Add(new TargetOption(id,
                        peer.Platform + "  •  " + peer.Name));
                }
                if (_host == null)
                {
                    foreach (var (vid, peer, _) in _receiver.BridgedPeers)
                    {
                        if (!addedIdentities.Add(ReceiverServer.LayoutIdentity(peer))) continue;
                        _targetChoice.Items.Add(new TargetOption(vid,
                            "In same environment  •  " + peer.Platform + "  •  " + peer.Name));
                    }
                }
            }
            for (int i = 0; i < _targetChoice.Items.Count; i++)
                if (_targetChoice.Items[i] is TargetOption item && item.Id == selected)
                    _targetChoice.SelectedIndex = i;
            if (_targetChoice.SelectedIndex == -1 && _targetChoice.Items.Count > 0)
                _targetChoice.SelectedIndex = 0;
            _targetChoice.Visible = false;
            int online=_host!=null
                ? Math.Max(1,_host.EnvironmentMembers.Count) + GetDistinctLocalReceiverPeersWhenHosted().Length
                : (_receiver?.ActiveEnvironmentDeviceCount ?? 1);
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

    private bool CurrentDestinationIsAndroid()
    {
        if(_captureUsesInbound || _host==null)
        {
            int id=_receiver?.SelectedPeerId??0;
            return id>0 && (_receiver?.ConnectedPeers
                .Any(p=>p.Id==id && p.Peer.Platform=="Android")==true ||
                _receiver?.BridgedPeers
                    .Any(b=>b.VirtualId==id && b.Peer.Platform=="Android")==true);
        }
        int target=_host.SelectedEnvironmentTargetId;
        return target>0 && _host.EnvironmentMembers.Any(
            m=>m.Id==target && m.Platform=="Android" &&
                m.SupportsReverseInput);
    }

    private void AttachCaptureHandlers(SourceCapture capture)
    {
        capture.SetPointerSensitivity(_pointerSensitivity);
        capture.NavigateEnvironment=NavigateEnvironmentFromCapture;
        capture.DestinationIsAndroid=CurrentDestinationIsAndroid;
        capture.ToggleMobileRequested+=JumpToMobileOrReturn;
    }

    private int? FindReadyAndroidTargetInReceiver(out bool pausedFound)
    {
        var peers = _receiver?.ConnectedPeers ?? Array.Empty<(int Id, ConnectedPeer Peer)>();
        var bridged = _receiver?.BridgedPeers ?? Array.Empty<(int VirtualId, ConnectedPeer Peer, int BridgeSessionId)>();
        pausedFound = peers.Any(p => p.Peer.Platform == "Android") ||
                      bridged.Any(b => b.Peer.Platform == "Android");
        int current = _receiver?.SelectedPeerId ?? 0;
        var currentAndroid = peers.FirstOrDefault(
            p => p.Id == current && p.Peer.Platform == "Android" && p.Peer.SupportsReverseInput);
        if (currentAndroid.Peer != null) return currentAndroid.Id;
        var currentBridged = bridged.FirstOrDefault(
            b => b.VirtualId == current && b.Peer.Platform == "Android" && b.Peer.SupportsReverseInput);
        if (currentBridged.Peer != null) return currentBridged.VirtualId;
        var firstReady = peers.FirstOrDefault(
            p => p.Peer.Platform == "Android" && p.Peer.SupportsReverseInput);
        if (firstReady.Peer != null) return firstReady.Id;
        var firstBridged = bridged.FirstOrDefault(
            b => b.Peer.Platform == "Android" && b.Peer.SupportsReverseInput);
        return firstBridged.Peer != null ? firstBridged.VirtualId : null;
    }

    private int? FindReadyAndroidTargetInHost(out bool pausedFound)
    {
        if (_host == null)
        {
            pausedFound = false;
            return null;
        }
        pausedFound = _host.EnvironmentMembers.Any(m => m.Platform == "Android");
        int current = _host.SelectedEnvironmentTargetId;
        var readyMember = _host.EnvironmentMembers.FirstOrDefault(
            m => m.Id == current && m.Platform == "Android" && m.SupportsReverseInput)
            ?? _host.EnvironmentMembers.FirstOrDefault(
                m => m.Platform == "Android" && m.SupportsReverseInput);
        if (readyMember == null) return null;
        var localDirect = FindLocalReceiverPeerByIdentity(readyMember.StableIdentity);
        return localDirect?.Id ?? -readyMember.Id;
    }

    private void JumpToMobileOrReturn()
    {
        if(IsDisposed || _closing.IsCancellationRequested)return;
        if(_capture?.RemoteActive==true)
        {
            _capture.ReturnLocal();
            return;
        }
        bool pausedInbound = false;
        bool pausedHost = false;
        int? androidTargetId = (_captureUsesInbound || _host == null)
            ? FindReadyAndroidTargetInReceiver(out pausedInbound) ?? FindReadyAndroidTargetInHost(out pausedHost)
            : FindReadyAndroidTargetInHost(out pausedHost) ?? FindReadyAndroidTargetInReceiver(out pausedInbound);
        if(androidTargetId==null)
        {
            if(pausedInbound || pausedHost)
                Status("Android control is paused or off on the phone. Tap Resume in the phone app.");
            else
                Status("No Android phone connected yet. Open DeskTouch on your phone to connect.");
            return;
        }
        var actualEdge=_display.EdgeToward(EnvironmentRouteOrder.Local,androidTargetId.Value,
            CurrentBaseTargetId())??_preferredEdge;
        if(androidTargetId.Value==CurrentInputTargetId() && _capture?.IsArmed==true)
        {
            _capture.SetEdge(actualEdge);
            _capture.BeginDirectHandoff();
            return;
        }
        SelectConnectedTargetCore(androidTargetId.Value,actualEdge,0.5,
            requestHandoff:true,directHandoff:true);
    }

    private int CurrentInputTargetId() =>
        _captureUsesInbound || _host==null
            ? _receiver?.SelectedPeerId ?? EnvironmentRouteOrder.Local
            : -_host.SelectedEnvironmentTargetId;

    private int CurrentBaseTargetId()
    {
        if(_host!=null)return 0;
        int baseId=_receiver?.ConnectedPeers.Where(p=>p.Peer.Platform=="Windows")
            .OrderBy(p=>p.Id).Select(p=>p.Id).FirstOrDefault()??0;
        return baseId!=0 ? baseId :
            _receiver?.ConnectedPeers.OrderBy(p=>p.Id)
                .Select(p=>p.Id).FirstOrDefault()??0;
    }

    private bool NavigateEnvironmentFromCapture(MonitorEdge edge,double fraction)
    {
        var order=ConnectedEnvironmentOrder();
        if(order==null)return false;
        bool remote=_capture?.RemoteActive==true;
        int starting=remote?CurrentInputTargetId():EnvironmentRouteOrder.Local;
        int baseId=CurrentBaseTargetId();
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
            // A user may have clicked the phone/PC tile earlier without
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
        double fraction,bool requestHandoff,bool directHandoff = false)
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
                var directPeer=_receiver.ConnectedPeers.FirstOrDefault(p=>p.Id==id);
                ConnectedPeer? targetPeer = directPeer.Peer;
                if (targetPeer == null)
                    targetPeer = _receiver.BridgedPeers.FirstOrDefault(b => b.VirtualId == id).Peer;
                if(targetPeer==null || !targetPeer.SupportsReverseInput)
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
                _incomingPeer=targetPeer;
                selectedPeer=targetPeer;
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
                AttachCaptureHandlers(_capture);
                _capture.Arm();
            }
            if(requestHandoff)
            {
                if(directHandoff)
                    _capture.BeginDirectHandoff();
                else
                    _capture.BeginLinkedHandoff(edge,fraction);
            }
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

    private void EnsureCaptureArmedIfConnected()
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        bool hasInboundReady = _receiver != null && (_host == null || _captureUsesInbound) &&
            (_receiver.ConnectedPeers.Any(p => p.Id == _receiver.SelectedPeerId && p.Peer.SupportsReverseInput) ||
             _receiver.BridgedPeers.Any(b => b.VirtualId == _receiver.SelectedPeerId && b.Peer.SupportsReverseInput));
        bool hasOutbound = _host != null;
        if (!hasInboundReady && !hasOutbound) return;
        if (_nativeInput?.IsRunning != true)
        {
            try { _nativeInput?.Start(); }
            catch { return; }
        }
        if (_capture == null && _nativeInput?.IsRunning == true)
        {
            if (_host != null && !_captureUsesInbound)
            {
                _capture = new SourceCapture(this, _host, Status, _preferredEdge, preserveRawInput: true);
                AttachCaptureHandlers(_capture);
                _capture.Arm();
            }
            else if (hasInboundReady && _receiver != null)
            {
                ArmSelectedInbound();
            }
        }
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
            AttachCaptureHandlers(_capture);
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
            BoostProcessResponsiveness();
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
            _receiver.PeerKeyboardInfoChanged += () => PostToUi(UpdateLiveLinkDisplay);
            _receiver.ReverseControlMessage += HandleReverseState;
            _receiver.HubYieldRequested += OnHubYieldRequested;
            _receiver.DiagnosticLog += LogDiagnostic;
            _receiver.AllowForwardClaim = () =>
                InputClaimLifecyclePolicy.CanGrant(_nativeInput?.IsRunning == true,
                    _capture?.RemoteActive == true, _capture?.IsClaimPending == true);
            _receiver.Start();
            if (_adbBridge?.CurrentShellBridge is { } existingBridge)
                _receiver.SetShellBridgeAuth(existingBridge.Port, existingBridge.Secret);
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
                        },
                        () => _host == null,
                        () => _host != null ? 0 : (Volatile.Read(ref _receiver)?.ActiveEnvironmentDeviceCount ?? 1));
                    _discovery.Start();
                    _adbBridge ??= new AdbCableBridge(
                        LogDiagnostic,
                        onShellBridgeReady: (port, secret) =>
                            Volatile.Read(ref _receiver)?.SetShellBridgeAuth(port, secret),
                        onBridgeDetached: () =>
                        {
                            Volatile.Read(ref _receiver)?.NotifyCableDetached();
                            BroadcastOnlinePresence();
                        },
                        onCableBridgeReady: () =>
                            Volatile.Read(ref _receiver)?.NotifyCableBridgeReady());
                    BroadcastOnlinePresence();
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
            _adbBridge?.Dispose();
            _adbBridge = null;
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
            _receiver.HubYieldRequested -= OnHubYieldRequested;
            _receiver.DiagnosticLog -= LogDiagnostic;
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

    private ConnectedPeer? FindReceiverDirectOrBridgedPeer(int id)
    {
        if (_receiver == null || id <= 0) return null;
        return _receiver.ConnectedPeers.FirstOrDefault(p => p.Id == id).Peer
            ?? _receiver.BridgedPeers.FirstOrDefault(b => b.VirtualId == id).Peer;
    }

    private void OnPeerListChanged() => PostToUi(() =>
    {
        // A Windows peer's controls may be updated on the SAME TLS
        // session, without dropping the entire environment. Keep diagram,
        // keyboard-target availability and active input grant consistent.
        if (_host != null && _receiver != null)
            _host.PublishSubPeers(_receiver.GetSubPeersSnapshotForUpstream());
        if(_captureUsesInbound && _receiver!=null)
        {
            int id=_receiver.SelectedPeerId;
            var activePeer = FindReceiverDirectOrBridgedPeer(id);
            if(activePeer != null)
            {
                _incomingPeer=activePeer;
                if(!activePeer.SupportsReverseInput &&
                    _captureInboundPeerId==id && _capture!=null)
                {
                    _capture.ReturnLocal();
                    _capture.Dispose();_capture=null;
                    _captureInboundPeerId=0;
                }
                if(activePeer.SupportsReverseInput && _capture==null &&
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
            if (_host != null)
                _host.PublishSubPeers(_receiver.GetSubPeersSnapshotForUpstream());
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
            // authority from a DIFFERENT selected Windows target.
            // Use the receiver's actual selected ID, never the most recent
            // discovery event, and keep an existing capture for that same peer.
            int selectedId=_receiver.SelectedPeerId;
            if(selectedId==0){ RefreshDisplays();return; }
            var selectedPeer = FindReceiverDirectOrBridgedPeer(selectedId);
            if(selectedPeer==null)return;
            _incomingPeer=selectedPeer;
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
                    "on the phone to share mouse and keyboard.");
            }
            RefreshDisplays();
            ResolveDualComputerLoopIfNeeded();
        });
    }

    private void OnIncomingPeerDisconnected()
    {
        PostToUi(() =>
        {
            if (_host != null && _receiver != null)
                _host.PublishSubPeers(_receiver.GetSubPeersSnapshotForUpstream());
            int selectedId=_receiver?.SelectedPeerId ?? 0;
            // A selection change raises "previous disconnected" even when
            // its TLS session is still online. Never dispose a just-armed
            // capture for the next selected device on that callback.
            if(selectedId>0)
            {
                var selectedPeer = FindReceiverDirectOrBridgedPeer(selectedId);
                if(selectedPeer != null)
                {
                    _incomingPeer=selectedPeer;
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
            // geometry. Another PC's layout update must not re-map the target's
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
                else BringWindowToForegroundSafely();
                var result = MessageBox.Show(this,
                    $"Allow {safePeer} to share its keyboard and mouse with this computer?\n\n" +
                    "Your own keyboard, mouse and touchpad always have priority. " +
                    "You can disconnect at any time.",
                    "DeskTouch - Allow connection?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                completion.TrySetResult(result == DialogResult.Yes);
                if (result == DialogResult.Yes)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(120);
                        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(UpdateButtons); }
                        catch (Exception) { }
                    });
                }
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

    internal enum HubAutoConnectDecision
    {
        None,
        KeepLocalHubWaitForRemote,
        SolicitRemoteToYieldToLocalHub,
        JoinRemoteHub
    }

    internal static HubAutoConnectDecision EvaluateHubAutoConnect(
        int localEnvironmentDeviceCount,
        bool localHasHost,
        string? localFingerprint,
        IReadOnlyCollection<ConnectedPeer> receiverPeers,
        NearbyComputer? candidate,
        bool allowLegacyIdleSolicitation = false)
    {
        if (localHasHost || candidate == null || !candidate.CanReceive || candidate.IsAndroid)
            return HubAutoConnectDecision.None;
        if (receiverPeers != null && receiverPeers.Any(p =>
            !string.IsNullOrWhiteSpace(p.DeviceFingerprint) &&
            string.Equals(p.DeviceFingerprint, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase)))
            return HubAutoConnectDecision.None;
        if (receiverPeers != null && receiverPeers.Any(p =>
            string.Equals(p.Platform, "Windows", StringComparison.Ordinal)))
            return HubAutoConnectDecision.None;

        int localCount = Math.Max(1, localEnvironmentDeviceCount);
        int remoteCount = candidate.IsHub ? Math.Max(2, candidate.MemberCount) : Math.Max(1, candidate.MemberCount);

        if (remoteCount > localCount)
            return HubAutoConnectDecision.JoinRemoteHub;

        if (remoteCount < localCount)
        {
            return allowLegacyIdleSolicitation && remoteCount == 1
                ? HubAutoConnectDecision.SolicitRemoteToYieldToLocalHub
                : HubAutoConnectDecision.KeepLocalHubWaitForRemote;
        }

        // Equal device count: deterministic lexicographic tie-break so one
        // computer stays Hub while the other joins outbound cleanly.
        if (!ShouldYieldOutboundToBecomeHub(localFingerprint, candidate.Fingerprint))
            return HubAutoConnectDecision.JoinRemoteHub;

        // If two standalone idle PCs meet and legacy 1-way trust prevented the
        // other PC from initiating outbound, let the higher-fp initiator try once.
        return allowLegacyIdleSolicitation && localCount == 1
            ? HubAutoConnectDecision.JoinRemoteHub
            : HubAutoConnectDecision.KeepLocalHubWaitForRemote;
    }

    internal static bool ShouldBlockAutoConnectBecauseReceiverActive(
        IReadOnlyCollection<ConnectedPeer> receiverPeers,
        string? candidateFingerprint)
    {
        if (receiverPeers == null || receiverPeers.Count == 0)
            return false;
        foreach (var peer in receiverPeers)
        {
            if (string.Equals(peer.Platform, "Windows", StringComparison.Ordinal))
                return true;
            if (!string.IsNullOrWhiteSpace(candidateFingerprint) &&
                !string.IsNullOrWhiteSpace(peer.DeviceFingerprint) &&
                string.Equals(peer.DeviceFingerprint, candidateFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool ShouldYieldOutboundToBecomeHub(
        string? localFingerprint,
        string? remoteFingerprint)
    {
        if (string.IsNullOrWhiteSpace(localFingerprint) ||
            string.IsNullOrWhiteSpace(remoteFingerprint) ||
            localFingerprint.Length != 64 || remoteFingerprint.Length != 64)
            return false;
        return string.Compare(localFingerprint, remoteFingerprint,
            StringComparison.OrdinalIgnoreCase) > 0;
    }

    internal static Dictionary<string, PointF> MergeRemoteEnvironmentOffsets(
        IReadOnlyDictionary<string, PointF> remotePositions,
        IReadOnlyDictionary<string, PointF> localStoredOffsets,
        IReadOnlyCollection<ConnectedPeer> localReceiverPeers)
    {
        var merged = new Dictionary<string, PointF>(remotePositions, StringComparer.Ordinal);
        if (localStoredOffsets == null || localReceiverPeers == null || localReceiverPeers.Count == 0)
            return merged;
        foreach (var (key, point) in localStoredOffsets)
        {
            if (merged.ContainsKey(key)) continue;
            if (localReceiverPeers.Any(peer => ReceiverServer.OwnsPlacement(peer, key)))
                merged[key] = point;
        }
        return merged;
    }

    private void OnHubYieldRequested(string remoteFingerprint, int remoteCount) =>
        PostToUi(() => _ = HandleHubYieldInviteAsync(remoteFingerprint, remoteCount));

    private async Task HandleHubYieldInviteAsync(string remoteFingerprint, int remoteCount)
    {
        if (_host != null || _connecting || _receiver == null || _closing.IsCancellationRequested)
            return;
        int localCount = _receiver.ActiveEnvironmentDeviceCount;
        bool shouldYield = remoteCount > localCount ||
            (remoteCount == localCount && !ShouldYieldOutboundToBecomeHub(_receiver.Fingerprint, remoteFingerprint));
        if (!shouldYield) return;
        _receiver.DisconnectPeerByFingerprint(remoteFingerprint);
        _retryTrustedAfter = DateTime.MinValue;
        try { await Task.Delay(250, _closing.Token); }
        catch (OperationCanceledException) { return; }
        await ScanTrustedAsync();
    }

    private bool _resolvingDualLoop;

    private void ResolveDualComputerLoopIfNeeded()
    {
        if (_resolvingDualLoop || _host == null || _receiver == null || _currentFingerprint.Length != 64)
            return;
        bool samePeerInbound = _receiver.ConnectedPeers.Any(p =>
            string.Equals(p.Peer.Platform, "Windows", StringComparison.Ordinal) &&
            string.Equals(p.Peer.DeviceFingerprint, _currentFingerprint, StringComparison.OrdinalIgnoreCase));
        if (!samePeerInbound) return;

        int localWithoutLoop = Math.Max(1, _receiver.ActiveEnvironmentDeviceCount - 1);
        int remoteWithoutLoop = Math.Max(1, _host.EnvironmentMembers.Count - 1);
        bool keepLocalHub = localWithoutLoop > remoteWithoutLoop ||
            (localWithoutLoop == remoteWithoutLoop &&
             ShouldYieldOutboundToBecomeHub(_receiver.Fingerprint, _currentFingerprint));
        if (!keepLocalHub) return;

        _resolvingDualLoop = true;
        try
        {
            string peerLabel = _remoteAddress;
            DisconnectHost();
            _receiver.DisconnectPeerByFingerprint(_currentFingerprint);
            _ = Task.Delay(200, _closing.Token).ContinueWith(
                _ => PostToUi(() => Status($"Kept {Environment.MachineName}'s larger environment hub with {peerLabel}.")),
                TaskScheduler.Default);
        }
        finally { _resolvingDualLoop = false; }
    }

    private async Task AddComputerAsync()
    {
        if (_connecting || _closing.IsCancellationRequested) return;
        // The Windows client joins ONE environment. Adding another machine
        // must never silently drop an active Windows hub or keyboard lease.
        bool hasWindowsReceiverPeer = _receiver?.ConnectedPeers.Any(
            p => string.Equals(p.Peer.Platform, "Windows", StringComparison.Ordinal)) == true;
        if (_host != null || hasWindowsReceiverPeer)
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
        if (_receiver != null && _receiver.ConnectedDeviceFingerprints.Any(
            fp => string.Equals(fp, dialog.Fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            Status("That computer is already connected in this shared environment.");
            return;
        }
        _connecting = true;
        // Never tear down Windows's incoming TLS listener when this PC also
        // initiates an outgoing connection. Other mobiles and PCs may connect
        // concurrently. The same authenticated hub arbitrates input authority.
        UpdateButtons();
        Status("Connecting securely to the shared DeskTouch environment...");
        try
        {
            void OnFailure(string reason)
            {
                Status(reason);
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(DisconnectHost); }
                catch (InvalidOperationException) { }
            }
            HostSession candidate;
            var savedTrust = dialog.UseRemoteApproval
                ? _trusted?.FindAnyOutgoing(dialog.Fingerprint)
                : null;
            if (savedTrust != null)
            {
                try
                {
                    candidate = await HostSession.ConnectTrustedAsync(dialog.Address,
                        dialog.Fingerprint, savedTrust.Token, OnFailure, _closing.Token);
                    if (_trusted != null)
                    {
                        _trusted.SetAutoConnect(dialog.Fingerprint, true);
                        _trusted.AutoConnectEnabled = true;
                    }
                }
                catch (Exception ex) when (!_closing.IsCancellationRequested &&
                    (ex is System.Security.Authentication.AuthenticationException ||
                     ex.InnerException is System.Security.Authentication.AuthenticationException))
                {
                    Status("Saved trust was not recognized on that computer; requesting fresh approval...");
                    candidate = await HostSession.ConnectWithApprovalAsync(dialog.Address,
                        dialog.Fingerprint, OnFailure, _closing.Token);
                }
            }
            else if (dialog.UseRemoteApproval)
            {
                candidate = await HostSession.ConnectWithApprovalAsync(dialog.Address,
                    dialog.Fingerprint, OnFailure, _closing.Token);
            }
            else
            {
                candidate = await HostSession.ConnectAsync(dialog.Address,
                    dialog.Fingerprint, dialog.Secret, OnFailure, _closing.Token);
            }
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
        BoostProcessResponsiveness();
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
        session.DiagnosticLog += LogDiagnostic;
        session.InputDeliveryWarning += Status;
        _captureUsesInbound = false;
        _capture = new SourceCapture(this, session, Status, _preferredEdge,
            preserveRawInput: true);
        AttachCaptureHandlers(_capture);
        session.AuthorityControlPacket += HandleRemoteState;
        session.RemoteTopologyChanged += HandleRemoteTopology;
        session.RemoteNameChanged += HandleRemoteName;
        session.EnvironmentRosterChanged += HandleEnvironmentRoster;
        session.EnvironmentLayoutChanged += HandleSharedEnvironmentLayout;
        session.SubRoutePacketReceived += OnHostSubRoutePacket;
        session.MutualTrustOfferRequested = ConfirmMutualTrustOfferAsync;
        if (_receiver != null)
        {
            session.PublishSubPeers(_receiver.GetSubPeersSnapshotForUpstream());
            if (session.EnvironmentMembers.Count > 0)
                _receiver.SetUpstreamRosterOverlay(session.EnvironmentMembers.ToArray(), session.OwnEnvironmentMemberId);
        }
        if (_trusted != null)
            _ = session.TryEnsureMutualTrustAsync(_trusted, name, _closing.Token);
        if(session.SharedLayout.Count>0)
        {
            var localPeers = _receiver?.ConnectedPeers.Select(p => p.Peer).ToArray()
                ?? Array.Empty<ConnectedPeer>();
            _display.RestoreMemberOffsets(MergeRemoteEnvironmentOffsets(
                session.SharedLayout, _display.StoredMemberOffsets, localPeers));
        }
        if (session.RemoteSystemName is { Length: > 0 } actual)
            _remoteAddress = DisplayArrangementControl.CleanDeviceName(actual,
                _remoteAddress);
        session.RemotePlacementChanged += HandleRemotePlacement;
        RefreshDisplays();
        _capture.Arm();
        session.PublishSourcePlacement(_preferredEdge);
        UpdateButtons();
        ResolveDualComputerLoopIfNeeded();
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
                else BringWindowToForegroundSafely();
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
                if (result == DialogResult.Yes)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(120);
                        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(UpdateButtons); }
                        catch (Exception) { }
                    });
                }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return await completion.Task;
    }

    private void OnHostSubRoutePacket(int localId, Packet inner, Func<Packet, Task> replyAsync)
    {
        var receiver = Volatile.Read(ref _receiver);
        if (receiver != null)
            _ = receiver.DispatchBridgedCommandAsync(localId, inner, replyAsync);
    }

    private async Task<bool> ConfirmMutualTrustOfferAsync(
        string remoteName,
        string remoteFingerprint,
        string outgoingToken,
        string reciprocalToken,
        CancellationToken cancellation)
    {
        if (!await ConfirmTrustAsync(remoteName, cancellation))
            return false;
        if (_trusted == null) return false;
        _trusted.TrustOutgoing(remoteName, remoteFingerprint, outgoingToken);
        if (!string.IsNullOrWhiteSpace(reciprocalToken))
            _trusted.AllowIncoming(remoteName, reciprocalToken, remoteFingerprint);
        _trusted.AutoConnectEnabled = true;
        PostToUi(() =>
        {
            Status($"Two-way trusted device saved: {remoteName}");
            UpdateButtons();
        });
        return true;
    }

    private async Task TrustConnectedDeviceAsync()
    {
        if (_trusted == null) return;
        if (_host == null)
        {
            if (_receiver == null) return;
            var peerName = _receiver.ConnectedPeerName;
            if (string.IsNullOrWhiteSpace(peerName)) return;
            bool isWinPeer = string.Equals(_incomingPeer?.Platform, "Windows", StringComparison.Ordinal);
            string msg = isWinPeer
                ? $"Trust {peerName} in both directions?\n\nOnce confirmed, both computers can reconnect automatically in either direction whenever reachable on your local network."
                : $"Trust {peerName}?\n\nOnce confirmed, {peerName} can reconnect automatically whenever it is reachable over data cable or wireless.";
            if (MessageBox.Show(this, msg,
                "DeskTouch — Trust connected device?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
                return;
            _trustDevice.Enabled = false;
            try
            {
                bool saved = await _receiver.OfferTrustToConnectedAsync(_trusted, _closing.Token);
                if (!saved)
                {
                    Status("Trusted device offer was declined or timed out.");
                    return;
                }
                Status($"{peerName} trusted and saved for automatic reconnection.");
            }
            catch (Exception ex) { Status("Could not save trusted device: " + ex.Message); }
            finally { UpdateButtons(); }
            return;
        }
        if (string.IsNullOrEmpty(_currentFingerprint)) return;
        var name = _remoteAddress;
        var fingerprint = _currentFingerprint;
        if (MessageBox.Show(this,
            $"Trust {name} in both directions?\n\nAfter the other computer approves, DeskTouch will " +
            "save two-way automatic reconnection so both computers rejoin the larger hub even when IP addresses change.\n\n" +
            "You may revoke trust at any time.",
            "Trust connected computer?", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _trustDevice.Enabled = false;
        try
        {
            bool saved = await _host.RequestMutualTrustAsync(_trusted, name, _closing.Token);
            if (!saved)
            {
                Status("Trusted device request declined or interrupted. Normal pairing remains available.");
                return;
            }
            Status($"{name} trusted in both directions. Automatic reconnect enabled.");
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
        var initialReceiverPeers = _receiver.ConnectedPeers.Select(p => p.Peer).ToArray();
        if (initialReceiverPeers.Any(p => string.Equals(p.Platform, "Windows", StringComparison.Ordinal)))
            return;
        _scanningTrusted = true;
        try
        {
            // Discovery finds CURRENT network addresses; the saved file has
            // NO IP address, only the receiver's persistent TLS fingerprint.
            var targetFingerprints = _trusted.Outgoing
                .Select(x => x.Fingerprint)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var peers = await NearbyComputerDiscovery.FindFastAsync(
                targetFingerprints, _closing.Token);
            var candidates = peers
                .Where(p => !p.IsAndroid && p.CanReceive)
                .Select(p => new { Peer = p, Saved = _trusted.FindAutoConnect(p.Fingerprint) })
                .Where(x => x.Saved != null)
                .OrderByDescending(x => x.Peer.IsHub ? Math.Max(2, x.Peer.MemberCount) : Math.Max(1, x.Peer.MemberCount))
                .ThenBy(x => x.Peer.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var found = candidates.FirstOrDefault();
            var latestReceiverPeers = _receiver?.ConnectedPeers.Select(p => p.Peer).ToArray()
                ?? Array.Empty<ConnectedPeer>();
            if (found == null || _receiver == null || _host != null ||
                _connecting || !_trusted.AutoConnectEnabled)
                return;

            int localCount = _receiver.ActiveEnvironmentDeviceCount;
            bool allowSolicit = !_performedLegacySolicitAttempt;
            var decision = EvaluateHubAutoConnect(
                localCount,
                _host != null,
                _receiver.Fingerprint,
                latestReceiverPeers,
                found.Peer,
                allowLegacyIdleSolicitation: allowSolicit);

            if (decision is HubAutoConnectDecision.None or HubAutoConnectDecision.KeepLocalHubWaitForRemote)
                return;

            if (decision == HubAutoConnectDecision.SolicitRemoteToYieldToLocalHub)
            {
                _performedLegacySolicitAttempt = true;
                try
                {
                    using var probeSession = await HostSession.ConnectTrustedAsync(
                        found.Peer.Address, found.Saved!.Fingerprint, found.Saved.Token,
                        _ => { }, _closing.Token);
                    await probeSession.TryEnsureMutualTrustAsync(_trusted, found.Saved.Name, _closing.Token);
                    probeSession.SendHubYieldInvite(localCount);
                    await Task.Delay(250, _closing.Token);
                    Status($"Invited {found.Saved.Name} to join {Environment.MachineName}'s {localCount}-device environment hub.");
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
                catch { }
                return;
            }

            _connecting = true;
            // Outbound auto-connect must not make Windows disappear or mark
            // it Busy to other incoming Windows computers.
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
                _retryTrustedAfter = DateTime.UtcNow.AddSeconds(15);
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
            _retryTrustedAfter = DateTime.UtcNow.AddSeconds(15);
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
            bool localOnlyPeer = _receiver?.ConnectedPeers.Any(
                p => ReceiverServer.OwnsPlacement(p.Peer, key)) == true &&
                !_host.EnvironmentMembers.Any(m =>
                    string.Equals(m.StableIdentity, key, StringComparison.Ordinal) ||
                    string.Equals("hub@" + m.StableIdentity, key, StringComparison.Ordinal) ||
                    string.Equals(m.Platform + "|" + m.Name, key, StringComparison.Ordinal) ||
                    string.Equals("hub@" + m.Platform + "|" + m.Name, key, StringComparison.Ordinal));
            if (localOnlyPeer) return;
            if(!_host.PublishEnvironmentPlacement(key,point))
                Status("Could not synchronize this device position to the environment hub.");
        }
        else if(_receiver!=null && !_receiver.UpdateEnvironmentPlacement(key,point))
            Status("Cannot synchronize position for an offline or untrusted device.");
    }

    private void HandleSharedEnvironmentLayout(
        IReadOnlyDictionary<string,PointF> canonicalPositions) => PostToUi(() =>
    {
        if(canonicalPositions.Count==0)return;
        var localPeers = _receiver?.ConnectedPeers.Select(p => p.Peer).ToArray()
            ?? Array.Empty<ConnectedPeer>();
        _display.RestoreMemberOffsets(MergeRemoteEnvironmentOffsets(
            canonicalPositions, _display.StoredMemberOffsets, localPeers));
        SavePreferredEdge();
        // Applying a hub snapshot MUST NOT re-send it as a local drag.
        RefreshDisplays();
    });

    private void HandleEnvironmentRoster() => PostToUi(() =>
    {
        if(_host == null) return;
        // The authenticated hub provides current members. One connection
        // stays alive while target keyboard/mouse authority changes.
        _receiver?.SetUpstreamRosterOverlay(
            _host.EnvironmentMembers.ToArray(),
            _host.OwnEnvironmentMemberId);
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
            _host.DiagnosticLog -= LogDiagnostic;
            _host.InputDeliveryWarning -= Status;
            _host.RemoteTopologyChanged -= HandleRemoteTopology;
            _host.RemoteNameChanged -= HandleRemoteName;
            _host.EnvironmentRosterChanged -= HandleEnvironmentRoster;
            _host.EnvironmentLayoutChanged -= HandleSharedEnvironmentLayout;
            _host.RemotePlacementChanged -= HandleRemotePlacement;
            _host.SubRoutePacketReceived -= OnHostSubRoutePacket;
            _host.MutualTrustOfferRequested = null;
        }
        _receiver?.ReleaseAllBridgedClaims();
        _receiver?.SetUpstreamRosterOverlay(null, null);
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
        else
            RefreshDisplays();
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
            if (doc.RootElement.TryGetProperty("PointerSensitivity", out var sens) &&
                sens.TryGetDouble(out var mult) && !double.IsNaN(mult) && !double.IsInfinity(mult))
            {
                int pct = Math.Clamp((int)Math.Round(mult * 100.0),
                    _sensitivityBar.Minimum, _sensitivityBar.Maximum);
                _pointerSensitivity = pct / 100.0;
                _updatingSensitivityUi = true;
                try
                {
                    _sensitivityBar.Value = pct;
                    _sensitivityValue.Text = pct + "%";
                }
                finally { _updatingSensitivityUi = false; }
                _capture?.SetPointerSensitivity(_pointerSensitivity);
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
                PointerSensitivity = Math.Round(_pointerSensitivity, 2),
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
        if (_taskbarCreatedMessage != 0 && message.Msg == _taskbarCreatedMessage &&
            _trayIcon != null && !IsDisposed && !_exitRequested)
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        }
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
            if (_capture?.OutboundInputReserved != true)
                _nativeInput?.HandleRawInput(message.LParam);
            _capture?.OnRawInput(message.LParam);
        }
        if (message.Msg == 0x007E && IsHandleCreated && !Disposing)
        {
            try { BeginInvoke(OnDisplayChanged); }
            catch (InvalidOperationException) { }
        }
        if (message.Msg == 0x0219 && !_closing.IsCancellationRequested)
        {
            _adbBridge?.TriggerRefresh();
        }
        if (message.Msg == 0x0051 && !_closing.IsCancellationRequested)
        {
            _receiver?.BroadcastKeyboardLayoutAsync();
            PostToUi(UpdateLiveLinkDisplay);
        }
        base.WndProc(ref message);
    }

    private void BroadcastOnlinePresence()
    {
        string? fp = _receiver?.Fingerprint;
        if (string.IsNullOrWhiteSpace(fp) || _closing.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try { await NearbyComputerDiscovery.BroadcastOnlineAnnouncementAsync(fp, _closing.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _closing.IsCancellationRequested) return;
        BroadcastOnlinePresence();
        PostToUi(() =>
        {
            _performedLegacySolicitAttempt = false;
            _retryTrustedAfter = DateTime.MinValue;
            EnsureCaptureArmedIfConnected();
            _ = ScanTrustedAsync();
        });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (_closing.IsCancellationRequested) return;
        if (e.Reason is not (SessionSwitchReason.SessionUnlock or
            SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionLogon))
            return;
        BroadcastOnlinePresence();
        PostToUi(() =>
        {
            _retryTrustedAfter = DateTime.MinValue;
            EnsureCaptureArmedIfConnected();
            _ = ScanTrustedAsync();
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_closing.IsCancellationRequested) return;
        BroadcastOnlinePresence();
        PostToUi(() =>
        {
            _retryTrustedAfter = DateTime.MinValue;
            _ = ScanTrustedAsync();
        });
    }

    private void PausePhoneForPayments()
    {
        if (_receiver == null) return;
        int sent = _receiver.PauseConnectedAndroidForPayments();
        if (sent > 0)
        {
            _pausePhonePayments.Enabled = false;
            _pausePhonePaymentsTray.Enabled = false;
            Status("Sent payment-safe pause request to your phone so payment apps work normally.");
        }
        else
        {
            Status("Phone Accessibility is already turned off or no ready phone is connected.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_formDisposed)
            {
                base.Dispose(disposing);
                return;
            }
            _formDisposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            try { _closing.Cancel(); } catch (ObjectDisposedException) { }
            _logChannel.Writer.TryComplete();
            _trustedScanTimer?.Dispose();
            _trustedScanTimer = null;
            _liveLinkTimer?.Dispose();
            _liveLinkTimer = null;
            _showWait?.Unregister(null);
            _showWait = null;
            _exitWait?.Unregister(null);
            _exitWait = null;
            _showSignal?.Dispose();
            _showSignal = null;
            _exitSignal?.Dispose();
            _exitSignal = null;
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
