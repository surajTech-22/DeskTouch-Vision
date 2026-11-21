using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    private readonly TextBox _pairingCodeInput = new()
    {
        Name = "PairingCodeEditor",
        Width = 172,
        MaxLength = 32,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(12, 18, 28),
        ForeColor = Color.FromArgb(226, 232, 240),
        Font = new Font("Consolas", 10.5f, FontStyle.Bold),
        PlaceholderText = "Set custom code"
    };
    private readonly Button _savePairingCode = Button("Set code");
    private readonly Button _copyCode = Button("Copy code");
    private readonly Button _copyFullLink = Button("Copy full link");
    private string _customPairingCode = PairingCodeHelper.LoadOrCreateCode();
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
    private bool _startInTrayPendingHide;
    private System.Windows.Forms.Timer? _topMostPulseTimer;
    private bool _prePulseTopMost;
    private bool _topMostPulseActive;
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
        BackColor = Color.FromArgb(17, 23, 34)
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
    private readonly WindowsNotificationCounter _notificationCounter = new();
    private readonly DeskSpaceDeckBridge _deckBridge;
    private TrayDeviceFlyout? _trayFlyout;
    private ProfilesAndRulesConfig _ecosystemConfig = AutomationAndProfilesStore.Load();
    private NotificationPreviewFlyout? _notifFlyout;
    private OtpBannerForm? _otpBanner;
    private readonly SharedShelfStore _sharedShelf = new();
    private readonly RemoteMicBridgeController _sharedMic = new();
    private bool _localReceiverSharingMedia;
    private bool _localHostSharingMedia;
    private string _localReceiverShareSummary = "";
    private string _localHostShareSummary = "";
    private PhoneScreenForm? _phoneScreenForm;
    private PhoneWebcamForm? _phoneWebcamForm;
    private SharedShelfDialog? _sharedShelfDialog;
    private readonly Button _qrPairButton = Button("Scan QR", primary: true);
    private readonly Button _repairButton = Button("Fix Background & Network");
    private readonly Button _profileButton = Button("Profile: Work");
    private readonly Button _clipboardToggleButton = Button("Clipboard: On");
    private readonly Button _sendFileButton = Button("Send file");
    private readonly Button _phoneScreenButton = Button("Phone Screen");
    private readonly Button _phoneWebcamButton = Button("Camera & Mic");
    private readonly Button _shelfButton = Button("Recent Shelf");
    private readonly Button _lockAllButton = Button("Lock all");
    private readonly Button _wakeAllButton = Button("Wake PCs");
    private readonly Button _rulesButton = Button("Profiles & rules");
    private System.Windows.Forms.Timer? _otpClipboardClearTimer;
    private string _activeForegroundApp = "";
    private string _activeShortcutPreset = "Default";
    private bool _clipboardListenerAttached;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    internal DisplaySettingsForm(bool startInTray = false)
    {
        _startInTray = startInTray;
        try { _trusted = new TrustedDeviceStore(); }
        catch (Exception ex) { _trustLoadError = ex.Message; }
        _deckBridge = new DeskSpaceDeckBridge(
            targetId => PostToUi(() => SwitchDirectlyToTarget(targetId)),
            () => PostToUi(ReturnControlLocalFromExternal),
            peerId => PostToUi(() => PausePhoneForPaymentsFromBridge(peerId)),
            () => PostToUi(ShowFromTray),
            onOpenNotifications: id => PostToUi(() => OpenNotificationsFlyoutForTarget(id, null, Cursor.Position)),
            onToggleClipboard: () => PostToUi(ToggleClipboardSync),
            onDropFile: (id, path) => PostToUi(() => HandleDeckFileDrop(id, path)),
            onPowerAction: (id, action) => PostToUi(() => ExecutePowerActionFromUi(id, action)),
            onMediaAction: (id, action) => PostToUi(() => ExecuteMediaActionFromUi(id, action)),
            onSwitchProfile: name => PostToUi(() => ApplyScreenProfile(name)));
        _notificationCounter.EnablePreviewCapture = _ecosystemConfig.NotificationPreviewsEnabled;
        _notificationCounter.CountChanged += count => PostToUi(() => OnLocalNotificationCountChanged(count));
        _notificationCounter.ItemsChanged += items => PostToUi(() =>
        {
            if (_ecosystemConfig.NotificationPreviewsEnabled)
                _receiver?.PublishLocalNotifications(items);
        });
        _activeShortcutPreset = _ecosystemConfig.ActiveShortcutPreset;
        Text = "DeskTouch Vision — Displays";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(17, 23, 34);
        ForeColor = Color.FromArgb(241, 245, 249);
        ClientSize = new Size(1060, 728);
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Icon = LoadAppIcon();
        _startInTrayPendingHide = startInTray;
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
        _repairButton.Name = "FixBackgroundNetworkButton";
        _repairButton.Width = 180;
        _repairButton.Dock = DockStyle.Right;
        _repairButton.Margin = new Padding(5);
        _repairButton.Click += async (_, _) => await RunOneClickRepairAsync();
        header.Controls.Add(_manageTrusted);
        header.Controls.Add(_repairButton);
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

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 342,
            Padding = new Padding(24, 8, 24, 7), BackColor = BackColor };
        var pairingBar = new Panel { Dock = DockStyle.Top, Height = 38,
            Padding = new Padding(10, 4, 10, 4), BackColor = Color.FromArgb(24, 33, 49) };
        pairingBar.Paint += (_, pe) =>
        {
            using var borderPen = new Pen(Color.FromArgb(46, 62, 88), 1f);
            pe.Graphics.DrawRectangle(borderPen, 0, 0, Math.Max(1, pairingBar.Width - 1), Math.Max(1, pairingBar.Height - 1));
        };
        var pairingFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        var pairingLabel = new Label
        {
            Text = "Pairing code:",
            AutoSize = true,
            ForeColor = Color.FromArgb(186, 216, 241),
            Font = new Font("Segoe UI Semibold", 9.5f),
            Margin = new Padding(0, 5, 8, 0)
        };
        _pairingCodeInput.Text = _customPairingCode;
        _pairingCodeInput.Height = 27;
        _pairingCodeInput.Margin = new Padding(0, 1, 8, 0);
        _savePairingCode.Height = 28;
        _savePairingCode.Margin = new Padding(0, 0, 8, 0);
        _copyCode.Height = 28;
        _copyCode.Margin = new Padding(0, 0, 8, 0);
        _copyFullLink.Height = 28;
        _copyFullLink.Margin = new Padding(0, 0, 8, 0);
        _qrPairButton.Width = 86;
        _qrPairButton.Height = 28;
        _qrPairButton.Margin = new Padding(0, 0, 10, 0);
        var pairingHint = new Label
        {
            Text = "Editable (4-32 letters/digits). Scan QR on phone or enter code.",
            AutoSize = true,
            ForeColor = Color.FromArgb(165, 173, 188),
            Font = new Font("Segoe UI", 8.5f),
            Margin = new Padding(0, 6, 0, 0)
        };
        pairingFlow.Controls.Add(pairingLabel);
        pairingFlow.Controls.Add(_pairingCodeInput);
        pairingFlow.Controls.Add(_savePairingCode);
        pairingFlow.Controls.Add(_copyCode);
        pairingFlow.Controls.Add(_copyFullLink);
        pairingFlow.Controls.Add(_qrPairButton);
        pairingFlow.Controls.Add(pairingHint);
        pairingBar.Controls.Add(pairingFlow);

        var ecosystemBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 4, 0, 2),
            BackColor = BackColor
        };
        _phoneScreenButton.Width = 112;
        _phoneWebcamButton.Width = 116;
        _shelfButton.Width = 104;
        _profileButton.Width = 140;
        _clipboardToggleButton.Width = 122;
        _sendFileButton.Width = 96;
        _lockAllButton.Width = 86;
        _wakeAllButton.Width = 94;
        _rulesButton.Width = 132;
        foreach (var eb in new[] { _phoneScreenButton, _phoneWebcamButton, _shelfButton, _profileButton, _clipboardToggleButton, _sendFileButton, _lockAllButton, _wakeAllButton, _rulesButton })
        {
            eb.Height = 29;
            eb.Margin = new Padding(0, 0, 8, 0);
            ecosystemBar.Controls.Add(eb);
        }
        _phoneScreenButton.Click += (_, _) => OpenPhoneScreenWindow();
        _phoneWebcamButton.Click += (_, _) => OpenPhoneWebcamWindow();
        _shelfButton.Click += (_, _) => OpenSharedShelfDialog();
        _profileButton.Click += (_, _) => CycleScreenProfile();
        _clipboardToggleButton.Click += (_, _) => ToggleClipboardSync();
        _sendFileButton.Click += (_, _) => PromptAndSendFile(CurrentInputTargetId());
        _lockAllButton.Click += (_, _) => ExecutePowerActionFromUi(0, "lock-all");
        _wakeAllButton.Click += (_, _) => WakeRememberedComputers();
        _rulesButton.Click += (_, _) => OpenProfilesRulesDialog();
        RefreshEcosystemButtonsUi();

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48,
            Name = "ConnectionActions",
            FlowDirection = FlowDirection.LeftToRight, AutoSize = false,
            // Wrapping creates a hidden second row beneath the fixed-height
            // action bar at high Windows DPI. Keep one stable visible row.
            WrapContents = false, AutoScroll = true };
        _pausePhonePayments.Click += (_, _) => PausePhoneForPayments();
        _pausePhonePaymentsTray.Click += (_, _) => PausePhoneForPayments();
        _shortcuts.Click += (_, _) => ShowConnectedShortcuts();
        foreach (var b in new[] { _add, _trustDevice, _shortcuts, _pausePhonePayments, _disconnect, _nativeDisplaySettings, _androidDownload })
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
            BackColor = Color.FromArgb(24, 33, 49),
            Padding = new Padding(14, 10, 12, 7) };
        deviceInfo.Paint += (_, pe) =>
        {
            using var borderPen = new Pen(Color.FromArgb(46, 62, 88), 1f);
            pe.Graphics.DrawRectangle(borderPen, 0, 0, Math.Max(1, deviceInfo.Width - 1), Math.Max(1, deviceInfo.Height - 1));
        };
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
        bottom.Controls.Add(ecosystemBar);
        bottom.Controls.Add(pairingBar);
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
        _savePairingCode.Click += (_, _) => SaveCustomPairingCode();
        _pairingCodeInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SaveCustomPairingCode();
            }
        };
        _copyCode.Click += (_, _) => CopyShortPairingCode();
        _copyFullLink.Click += (_, _) => CopyPairingCode();
        _qrPairButton.Click += (_, _) => ShowPairingQrDialog();
        _disconnect.Click += (_, _) => DisconnectByUser();
        _trustDevice.Click += async (_, _) => await TrustConnectedDeviceAsync();
        _manageTrusted.Click += (_, _) => ManageTrustedDevices();
        _nativeDisplaySettings.Click += (_, _) => OpenNativeDisplays();
        _androidDownload.Click += (_, _) => { using var view = new AndroidDownloadDialog(); view.ShowDialog(this); };
        _checkUpdates.LinkClicked += async (_, _) =>
            await UpdateClient.CheckManuallyAsync(this);
        _display.AddComputerRequested += async () => await AddComputerAsync();
        _display.EnvironmentDeviceSelected += SelectConnectedTarget;
        _display.EnvironmentArrangementChanged += () =>
        {
            SavePreferredEdge();
            PublishEnvironmentSpatialLayout();
            PublishDeckBridgeSnapshot();
            RefreshTrayFlyoutIfVisible();
        };
        _display.EnvironmentMemberMoved += OnEnvironmentMemberMoved;
        _display.RemoteEdgeChanged += OnRemoteEdgeChanged;
        _display.NotificationPillClicked += OpenNotificationsFlyoutForTarget;
        _display.FileDroppedOnDevice += SendFilesToTargetFromDiagram;

        LoadPreferredEdge();
        _display.RemoteEdge = _preferredEdge;
        Shown += (_, _) =>
        {
            if (_exitRequested)
            {
                BeginInvoke(ExitDeskTouch);
                return;
            }
            bool justUpdated = false;
            try
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
                _liveLinkTimer.Tick += (_, _) =>
                {
                    UpdateLiveLinkDisplay();
                    PublishDeckBridgeSnapshot();
                };
                _liveLinkTimer.Start();
                _deckBridge.Start();
                PublishDeckBridgeSnapshot();
                if (_trustLoadError != null)
                    Status("Trusted device settings unavailable; reconnect disabled: " + _trustLoadError);
                justUpdated = ConsumeJustUpdatedMarker();
            }
            finally
            {
                _startInTrayPendingHide = false;
                if (_startInTray && !_showRequestedBeforeShown)
                {
                    HideToTray(showBalloon: false);
                    if (justUpdated && _trayIcon != null && !_trayTipShown)
                    {
                        _trayTipShown = true;
                        _trayIcon.BalloonTipTitle = $"DeskTouch Vision updated to v{MonitorTopology.CurrentAppVersion()}";
                        _trayIcon.BalloonTipText = "Updated automatically in the background. Click here to open Displays.";
                        try { _trayIcon.ShowBalloonTip(5000); } catch { }
                    }
                    if (_showRequestedBeforeShown)
                        ShowFromTray();
                }
                else
                {
                    BringWindowToForegroundSafely();
                    if (justUpdated)
                    {
                        _ = Task.Delay(250, _closing.Token).ContinueWith(
                            _ => PostToUi(BringWindowToForegroundSafely), TaskScheduler.Default);
                    }
                }
            }
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible && !_startInTrayPendingHide && !_isHiddenInTray)
            {
                if (Opacity < 1.0) Opacity = 1.0;
                if (WindowState != FormWindowState.Minimized) ClampToVisibleMonitor();
            }
        };
        Activated += (_, _) =>
        {
            if (!_startInTrayPendingHide && !_isHiddenInTray && Visible)
            {
                if (Opacity < 1.0) Opacity = 1.0;
                if (WindowState != FormWindowState.Minimized) ClampToVisibleMonitor();
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
        _trayMenu.Items.Add("Phone Screen Window", null, (_, _) =>
        {
            ShowFromTray();
            OpenPhoneScreenWindow();
        });
        _trayMenu.Items.Add("Camera & Mic", null, (_, _) =>
        {
            ShowFromTray();
            OpenPhoneWebcamWindow();
        });
        _trayMenu.Items.Add("Recent Shared Shelf", null, (_, _) =>
        {
            ShowFromTray();
            OpenSharedShelfDialog();
        });
        _trayMenu.Items.Add("Fix Background & Network", null, async (_, _) =>
        {
            ShowFromTray();
            await RunOneClickRepairAsync();
        });
        _trayMenu.Items.Add("Pair Wireless Android Bridge...", null, (_, _) =>
        {
            ShowFromTray();
            OpenWirelessBridgePairDialog();
        });
        _trayMenu.Items.Add("Profiles & Automation Rules...", null, (_, _) =>
        {
            ShowFromTray();
            OpenProfilesRulesDialog();
        });
        _trayMenu.Items.Add("Toggle Shared Clipboard", null, (_, _) => ToggleClipboardSync());
        _trayMenu.Items.Add("Lock All Connected Computers", null, (_, _) => ExecutePowerActionFromUi(0, "lock-all"));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit DeskTouch", null, (_, _) => ExitDeskTouch());

        _trayMenu.Opening += OnTrayMenuOpening;
        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? LoadAppIcon(),
            Text = "DeskTouch Vision - Windows device sharing",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += OnTrayIconMouseClick;
        _trayIcon.DoubleClick += (_, _) =>
        {
            _trayFlyout?.Hide();
            ShowFromTray();
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            _trayFlyout?.Hide();
            ShowFromTray();
        };
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
        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int round = 2;
            _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
        try
        {
            _clipboardListenerAttached = AddClipboardFormatListener(Handle);
        }
        catch { }
        if (_exitRequested && !IsDisposed) PostToUi(ExitDeskTouch);
    }

    private void ClampToVisibleMonitor()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var clamped = EnsureVisibleBounds(
            Bounds,
            screens.Select(s => s.WorkingArea).ToArray(),
            Screen.FromControl(this).WorkingArea);
        if (clamped != Bounds)
            Bounds = clamped;
    }

    private void EndForegroundTopMostPulse()
    {
        _topMostPulseTimer?.Stop();
        if (!_topMostPulseActive) return;
        _topMostPulseActive = false;
        if (!IsDisposed && IsHandleCreated && !_prePulseTopMost)
            Win.RestoreNonTopMost(Handle);
    }

    private void ScheduleForegroundTopMostPulse()
    {
        if (IsDisposed || !IsHandleCreated || _isHiddenInTray || _exitRequested) return;
        if (!_topMostPulseActive)
        {
            _prePulseTopMost = TopMost;
            _topMostPulseActive = true;
        }
        Win.RaiseAboveWindowsTemporarily(Handle);
        Win.ForceWindowToFront(Handle);
        Activate();
        BringToFront();

        _topMostPulseTimer ??= CreateTopMostPulseTimer();
        _topMostPulseTimer.Stop();
        _topMostPulseTimer.Start();
    }

    private System.Windows.Forms.Timer CreateTopMostPulseTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 110 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsDisposed || _closing.IsCancellationRequested || _exitRequested) return;
            EndForegroundTopMostPulse();
            if (_isHiddenInTray || !Visible || !IsHandleCreated) return;
            IntPtr target = Win.GetActivationTarget(Handle);
            if (Win.GetForegroundWindow() != target)
            {
                Win.ForceWindowToFront(Handle);
                Activate();
            }
        };
        return timer;
    }

    private void BringWindowToForegroundSafely()
    {
        if (IsDisposed || _exitRequested || _closing.IsCancellationRequested) return;
        _startInTrayPendingHide = false;
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
        ClampToVisibleMonitor();

        if (!IsHandleCreated) return;
        IntPtr target = Win.GetActivationTarget(Handle);
        Win.ForceWindowToFront(Handle);
        Activate();
        BringToFront();

        // Hold a real 110ms HWND_TOPMOST pulse + deferred re-activation whenever another
        // window or Explorer/flyout dismissal could otherwise keep DeskTouch in the taskbar.
        if (Win.GetForegroundWindow() != target || !_topMostPulseActive)
            ScheduleForegroundTopMostPulse();
    }

    private static bool ConsumeJustUpdatedMarker()
    {
        try
        {
            string marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskTouchVision", "just-updated.txt");
            if (!File.Exists(marker)) return false;
            string raw = File.ReadAllText(marker).Trim();
            File.Delete(marker);
            if (long.TryParse(raw, out long ms))
            {
                long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms;
                return ageMs is >= -60000 and <= 180000;
            }
            return true;
        }
        catch
        {
            return false;
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
        _startInTrayPendingHide = false;
        EndForegroundTopMostPulse();
        _isHiddenInTray = true;
        // Hide() naturally removes an invisible Form from the taskbar
        // WITHOUT changing ShowInTaskbar (which can recreate the HWND).
        // The same handle must keep receiving raw mouse input even in tray.
        // The UI message loop continues to receive GitHub release SSE events.
        Opacity = 1.0;
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
        _liveLinkTimer?.Stop();
        StopActiveRemoteMic();
        _sharedMic.Dispose();
        WindowsAudioDeviceBridge.RestorePreviousDefaultCaptureDeviceIfChanged();
        _receiver?.StopLocalMediaSharing();
        _host?.StopLocalMediaSharing();
        // Revoke remote control and close the TLS peer before destroying
        // Windows input hooks or receiver listening state.
        DisconnectHost();
        StopListening();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private void ExitDeskTouch()
    {
        _exitRequested = true;
        foreach (Form owned in OwnedForms)
        {
            try { owned.Close(); } catch { }
        }
        Close();
    }

    bool IUpdateCloseHost.IsHiddenInTray => _isHiddenInTray;

    void IUpdateCloseHost.CloseForVerifiedUpdate() => ExitDeskTouch();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static Button Button(string name, bool primary = false) => new()
    {
        Text = name, Width = name switch {
            "Windows display settings" => 178, "Pause phone for payments" => 176,
            "Copy pairing code" => 155, "Copy full link" => 116,
            "Add devices" => 145, "Get Android app" => 150, _ => 112 },
        FlatStyle = FlatStyle.Flat,
        FlatAppearance =
        {
            BorderColor = primary ? Color.FromArgb(96, 165, 250) : Color.FromArgb(51, 68, 94),
            BorderSize = 1,
            MouseOverBackColor = primary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(42, 56, 78),
            MouseDownBackColor = primary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(23, 32, 48)
        },
        ForeColor = Color.FromArgb(241, 245, 249),
        BackColor = primary ? Color.FromArgb(37, 99, 235)
                            : Color.FromArgb(30, 41, 59),
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI Semibold", 9f)
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
        _copyCode.Enabled = !string.IsNullOrWhiteSpace(_customPairingCode);
        _copyFullLink.Enabled = _receiver?.CanAcceptNewPeer == true &&
            !_connecting && _host == null;
        _savePairingCode.Enabled = !_closing.IsCancellationRequested;
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
        string splitText = !string.IsNullOrWhiteSpace(snap.SplitPathsSummary) ? $"  •  {snap.SplitPathsSummary}" : "";
        Color color = snap.RttMs is > 0 and <= 20
            ? Color.FromArgb(136, 224, 168)
            : (snap.RttMs > 60 ? Color.FromArgb(245, 200, 120) : Color.FromArgb(170, 212, 245));
        return (
            color,
            $"Live link [{snap.PeerName}]: {bandText}{speedText}  •  {pingText}  •  jitter {snap.JitterMs:F1} ms{lossText}{splitText}{kbText}"
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
            _display.LocalNotificationCount = _notificationCounter.CurrentCount;
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
                _display.RemoteNotificationCount = hub?.NotificationCount ?? -1;
                _display.RemoteTargetId = 0;
                _display.RemoteBatteryPercent = hub?.BatteryPercent ?? -1;
                _display.RemoteIsCharging = hub?.IsCharging ?? false;
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
                        localDirect?.Peer.FormatScreenInfo() ?? memberTopology.DescribeSummary(member.Platform),
                        localDirect is { } direct && direct.Peer.NotificationCount >= 0
                            ? direct.Peer.NotificationCount
                            : member.NotificationCount,
                        localDirect is { } dBatt && dBatt.Peer.BatteryPercent >= 0 ? dBatt.Peer.BatteryPercent : member.BatteryPercent,
                        localDirect is { } dChg ? dChg.Peer.IsCharging : member.IsCharging,
                        localDirect is { } dSig ? dSig.Peer.SignalLabel : ""));
                }
                foreach (var (id, peer) in GetDistinctLocalReceiverPeersWhenHosted())
                {
                    tiles.Add(new EnvironmentDisplayTile(id, peer.Name,
                        peer.Platform, peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput, peer.DeviceFingerprint,
                        peer.Monitors, peer.FormatScreenInfo(), peer.NotificationCount,
                        peer.BatteryPercent, peer.IsCharging, peer.SignalLabel));
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
                _display.RemoteNotificationCount = main.Peer?.NotificationCount ?? -1;
                _display.RemoteTargetId = baseTargetId;
                _display.RemoteBatteryPercent = main.Peer?.BatteryPercent ?? -1;
                _display.RemoteIsCharging = main.Peer?.IsCharging ?? false;
                var shownIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var (id,peer) in connected)
                {
                    shownIdentities.Add(ReceiverServer.LayoutIdentity(peer));
                    if(id==baseTargetId)continue;
                    tiles.Add(new EnvironmentDisplayTile(id,peer.Name,
                        peer.Platform,peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput,peer.DeviceFingerprint,
                        peer.Monitors,peer.FormatScreenInfo(),peer.NotificationCount,
                        peer.BatteryPercent,peer.IsCharging,peer.SignalLabel));
                }
                foreach(var (vid, peer, _) in bridged)
                {
                    if(!shownIdentities.Add(ReceiverServer.LayoutIdentity(peer))) continue;
                    if(vid==baseTargetId) continue;
                    tiles.Add(new EnvironmentDisplayTile(vid, peer.Name,
                        peer.Platform, peer.Monitors.Monitors.Count,
                        peer.SupportsReverseInput, peer.DeviceFingerprint,
                        peer.Monitors, peer.FormatScreenInfo(),
                        NotificationCount: peer.NotificationCount,
                        BatteryPercent: peer.BatteryPercent, IsCharging: peer.IsCharging));
                }
            }
            _display.EnvironmentDevices =
                DisplayArrangementControl.ProjectEnvironment(tiles,baseTargetId);
            PublishEnvironmentSpatialLayout(baseTargetId);
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

    private bool CurrentDestinationSupportsFastReverseKey()
    {
        if(_captureUsesInbound || _host==null)
        {
            int id=_receiver?.SelectedPeerId??0;
            if(id<=0)return false;
            var peer=_receiver?.ConnectedPeers.FirstOrDefault(p=>p.Id==id).Peer
                ?? _receiver?.BridgedPeers.FirstOrDefault(b=>b.VirtualId==id).Peer;
            return peer!=null && ReceiverServer.SupportsFastReverseKey(peer.Platform, peer.AppVersion);
        }
        int target=_host.SelectedEnvironmentTargetId;
        var member=_host.EnvironmentMembers.FirstOrDefault(m=>m.Id==target);
        if(member==null || member.Platform!="Android")return false;
        string? ver=null;
        try { ver=MonitorTopology.FromWire(member.Layout).Monitors.FirstOrDefault()?.AppVersion; }
        catch { }
        return ReceiverServer.SupportsFastReverseKey(member.Platform, ver);
    }

    private void AttachCaptureHandlers(SourceCapture capture)
    {
        capture.SetPointerSensitivity(_pointerSensitivity);
        capture.NavigateEnvironment=NavigateEnvironmentFromCapture;
        capture.DestinationIsAndroid=CurrentDestinationIsAndroid;
        capture.DestinationSupportsFastReverseKey=CurrentDestinationSupportsFastReverseKey;
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
            PublishDeckBridgeSnapshot();
            RefreshTrayFlyoutIfVisible();
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
            _receiver.ReleaseIdleOutboundForPhoneClaim = () => PostToUi(() =>
            {
                if (_nativeInput?.Priority.CanApplyRemote == true && _capture?.RemoteActive == true)
                    ReturnControlLocalFromExternal();
            });
            _receiver.ClipboardTextReceived += (h, t) => PostToUi(() =>
            {
                if (_ecosystemConfig.ClipboardSyncEnabled)
                {
                    SharedClipboardHelper.TryApplyTextOnStaThread(t, h);
                    _sharedShelf.AddClipboardText(t, "Connected device");
                }
            });
            _receiver.ClipboardImageReceived += (h, png) => PostToUi(() =>
            {
                if (_ecosystemConfig.ClipboardSyncEnabled)
                {
                    SharedClipboardHelper.TryApplyImageOnStaThread(png, h);
                    _sharedShelf.AddClipboardImage(png.Length, "Connected device");
                }
            });
            _receiver.FileReceived += (fromDev, savedPath) => PostToUi(() => OnIncomingFileSaved(fromDev, savedPath));
            _receiver.OtpOfferReceived += (app, code, title) => PostToUi(() => ShowOtpOfferToast(app, code, title));
            _receiver.PhoneScreenFrameReceived += (peerId, jpg) => PostToUi(() =>
            {
                if (_phoneScreenForm is { IsDisposed: false } f && f.PeerId == peerId)
                    f.UpdateFrame(jpg);
            });
            _receiver.PhoneScreenStatusReceived += (peerId, stateCode, msg) => PostToUi(() =>
            {
                if (_phoneScreenForm is { IsDisposed: false } f && f.PeerId == peerId)
                    f.UpdateStreamStatus(stateCode, msg);
            });
            _receiver.PhoneCameraFrameReceived += (peerId, jpg) => PostToUi(() =>
            {
                if (_phoneWebcamForm is { IsDisposed: false } f && !f.SourceViaHost && f.PeerId == peerId)
                    f.UpdateCameraFrame(jpg);
            });
            _receiver.PhoneCameraStatusReceived += (peerId, stateCode, msg) => PostToUi(() =>
            {
                if (_phoneWebcamForm is { IsDisposed: false } f && !f.SourceViaHost && f.PeerId == peerId && !string.IsNullOrWhiteSpace(msg))
                    f.UpdateStatus(msg);
            });
            _receiver.PhoneMicAudioReceived += (peerId, pcm, rate, seq) =>
            {
                try
                {
                    _sharedMic.HandleIncomingAudio(peerId, viaHost: false, pcm, rate, seq);
                }
                catch { }
            };
            _receiver.PhoneMicStatusReceived += (peerId, stateCode, msg) => PostToUi(() =>
            {
                if (_sharedMic.IsActiveFor(peerId, viaHost: false) && !string.IsNullOrWhiteSpace(msg))
                {
                    _sharedMic.MicStatusNote = stateCode == 1 ? "Mic Live" : msg;
                    if (stateCode == 2)
                    {
                        Status(msg);
                        _trayIcon?.ShowBalloonTip(5000, "Phone Microphone Action Needed", msg, ToolTipIcon.Info);
                    }
                }
                if (_phoneWebcamForm is { IsDisposed: false } f && !f.SourceViaHost && f.PeerId == peerId)
                    f.UpdateMicStatus(stateCode, msg);
            });
            _receiver.LocalMediaShareStateChanged += (cam, mic, summary) => PostToUi(() =>
            {
                HandleLocalMediaShareState(fromHost: false, cam, mic, summary);
            });
            _receiver.LowBatteryDetected += (devName, pct, _) => PostToUi(() =>
            {
                if (_ecosystemConfig.LowBatteryAlertEnabled)
                    _trayIcon?.ShowBalloonTip(5000, "Low Phone Battery", $"{devName} battery is at {pct}% (unplugged).", ToolTipIcon.Warning);
            });
            _receiver.RemoteProfileApplyRequested += profileName => PostToUi(() => ApplyScreenProfile(profileName));
            _receiver.ForegroundAppChanged += (proc, title) => PostToUi(() => EvaluateForegroundAutomationRule(proc, title));
            _receiver.BroadcastProfileState(AutomationAndProfilesStore.SerializeSummaryForAndroid(_ecosystemConfig));
            _receiver.Start();
            _receiver.SetCustomPairingCode(_customPairingCode);
            TriggerCrossNetworkRendezvousPublish();
            _receiver.SetLocalNotificationCount(_notificationCounter.CurrentCount);
            if (_adbBridge?.CurrentShellBridge is { } existingBridge)
                _receiver.SetShellBridgeAuth(existingBridge.Port, existingBridge.Secret, forceBroadcast: true);
            if (_adbBridge?.CurrentStatus is { } existingBridgeStatus)
                _receiver.SetBridgeStatus(existingBridgeStatus.State, existingBridgeStatus.Detail,
                    existingBridgeStatus.PairPortHint, existingBridgeStatus.ConnectPortHint);
            _receiver.AndroidBridgeHintReceived += (peerIp, usb, wifi, cPort, pPort, allowed, ready) =>
                _adbBridge?.UpdateAndroidDebugHint(peerIp, usb, wifi, cPort, pPort, allowed, ready);
            _receiver.ShellBridgeRefreshRequested += () =>
                _adbBridge?.TriggerRefresh();
            _receiver.WirelessAdbPairRequested += (peerIp, pPort, code, cPort) =>
                _adbBridge != null
                    ? _adbBridge.PairWirelessDebuggingAsync(peerIp, pPort, code, cPort)
                    : Task.FromResult((false, "Virtual Bridge service is not started yet on Windows."));
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
                        () => _host != null ? 0 : (Volatile.Read(ref _receiver)?.ActiveEnvironmentDeviceCount ?? 1),
                        _customPairingCode);
                    _discovery.Start();
                    _adbBridge ??= new AdbCableBridge(
                        LogDiagnostic,
                        onShellBridgeReady: (port, secret) =>
                            Volatile.Read(ref _receiver)?.SetShellBridgeAuth(port, secret, forceBroadcast: true),
                        onBridgeDetached: () =>
                        {
                            Volatile.Read(ref _receiver)?.NotifyCableDetached();
                            BroadcastOnlinePresence();
                        },
                        onCableBridgeReady: () =>
                            Volatile.Read(ref _receiver)?.NotifyCableBridgeReady(),
                        onBridgeStatusChanged: status =>
                            Volatile.Read(ref _receiver)?.SetBridgeStatus(
                                status.State, status.Detail, status.PairPortHint, status.ConnectPortHint));
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
        if (_phoneWebcamForm is { IsDisposed: false } wf)
            wf.UpdateSources(GetAvailableMediaSources());
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
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
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
            PublishEnvironmentSpatialLayout();
            PublishDeckBridgeSnapshot();
            RefreshTrayFlyoutIfVisible();
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

    private long _lastRendezvousPublishMs;
    private long _lastWanTrustedProbeMs;

    private void TriggerCrossNetworkRendezvousPublish(bool force = false)
    {
        var recv = _receiver;
        if (recv == null || _closing.IsCancellationRequested) return;
        long now = Environment.TickCount64;
        if (!force && _lastRendezvousPublishMs != 0 && now - _lastRendezvousPublishMs < 180_000)
            return;
        _lastRendezvousPublishMs = now;
        string fp = recv.Fingerprint;
        string code = _customPairingCode;
        bool isHub = _host == null;
        int count = Math.Max(1, recv.ActiveEnvironmentDeviceCount);
        _ = Task.Run(() => CrossNetworkRendezvous.PublishPresenceAsync(fp, code, isHub, count, _closing.Token));
    }

    private void SaveCustomPairingCode()
    {
        try
        {
            string saved = PairingCodeHelper.SaveCode(_pairingCodeInput.Text);
            _customPairingCode = saved;
            _pairingCodeInput.Text = saved;
            _receiver?.SetCustomPairingCode(saved);
            _discovery?.SetCustomPairingCode(saved);
            BroadcastOnlinePresence();
            TriggerCrossNetworkRendezvousPublish(force: true);
            Status($"Pairing code updated for {Environment.MachineName}: {saved} (published for cross-network pairing)");
        }
        catch (ArgumentException ex)
        {
            Status(ex.Message);
        }
    }

    private void CopyShortPairingCode()
    {
        try
        {
            string code = PairingCodeHelper.NormalizeCustomCode(_pairingCodeInput.Text);
            if (code.Length == 0)
                code = _customPairingCode;
            if (string.IsNullOrEmpty(code))
            {
                Status("Enter a 4–32 character pairing code first.");
                return;
            }
            TriggerCrossNetworkRendezvousPublish(force: true);
            Clipboard.SetText(code);
            Status($"Pairing code {code} copied. Works on same Wi-Fi or across different networks / 5G.");
        }
        catch (Exception ex) { Status("Unable to copy pairing code: " + ex.Message); }
    }

    private void CopyPairingCode()
    {
        if (_receiver == null) { Status("This computer is currently controlling another PC."); return; }
        try
        {
            var allEndpoints = CrossNetworkRendezvous.CollectLocalAndGlobalAddresses();
            if (allEndpoints.Count == 0)
            {
                Status("No local IPv4 or global IPv6 address found. Connect to a network first.");
                return;
            }
            TriggerCrossNetworkRendezvousPublish(force: true);
            string multiAddr = string.Join(",", allEndpoints);
            // Pairing secret is available only by a deliberate Copy action.
            var code = $"DT1|{multiAddr}|{_receiver.Fingerprint}|{_receiver.Secret}";
            Clipboard.SetText(code);
            Status($"Multi-network pairing link copied ({allEndpoints.Count} endpoint(s)). Keep it private.");
        }
        catch (Exception ex) { Status("Unable to copy full pairing link: " + ex.Message); }
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
        session.PublishNotificationCount(_notificationCounter.CurrentCount);
        session.EnvironmentLayoutChanged += HandleSharedEnvironmentLayout;
        session.SubRoutePacketReceived += OnHostSubRoutePacket;
        session.ClipboardTextReceived += (h, t) => PostToUi(() =>
        {
            if (_ecosystemConfig.ClipboardSyncEnabled)
            {
                SharedClipboardHelper.TryApplyTextOnStaThread(t, h);
                _sharedShelf.AddClipboardText(t, name);
            }
        });
        session.ClipboardImageReceived += (h, png) => PostToUi(() =>
        {
            if (_ecosystemConfig.ClipboardSyncEnabled)
            {
                SharedClipboardHelper.TryApplyImageOnStaThread(png, h);
                _sharedShelf.AddClipboardImage(png.Length, name);
            }
        });
        session.FileReceived += (fromDev, savedPath) => PostToUi(() => OnIncomingFileSaved(fromDev, savedPath));
        session.OtpOfferReceived += (app, code, title) => PostToUi(() => ShowOtpOfferToast(app, code, title));
        session.RemoteCameraFrameReceived += OnHostRemoteCameraFrame;
        session.RemoteCameraStatusReceived += OnHostRemoteCameraStatus;
        session.RemoteMicAudioReceived += OnHostRemoteMicAudio;
        session.RemoteMicStatusReceived += OnHostRemoteMicStatus;
        session.LocalMediaShareStateChanged += OnHostLocalMediaShareState;
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
            TriggerCrossNetworkRendezvousPublish(force: false);
            // Discovery finds CURRENT network addresses; the saved file has
            // NO IP address, only the receiver's persistent TLS fingerprint.
            var targetFingerprints = _trusted.Outgoing
                .Select(x => x.Fingerprint)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var peers = (await NearbyComputerDiscovery.FindFastAsync(
                targetFingerprints, _closing.Token)).ToList();
            if (peers.Count == 0 && Environment.TickCount64 - _lastWanTrustedProbeMs >= 25_000)
            {
                _lastWanTrustedProbeMs = Environment.TickCount64;
                foreach (string targetFp in targetFingerprints)
                {
                    var wanPeer = await CrossNetworkRendezvous.ResolveByFingerprintAsync(targetFp, _closing.Token);
                    if (wanPeer != null)
                        peers.Add(wanPeer);
                }
            }
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
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
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
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
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
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
        if (_phoneWebcamForm is { IsDisposed: false } wf)
            wf.UpdateSources(GetAvailableMediaSources());
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
        PublishEnvironmentSpatialLayout();
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
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
            _host.RemoteCameraFrameReceived -= OnHostRemoteCameraFrame;
            _host.RemoteCameraStatusReceived -= OnHostRemoteCameraStatus;
            _host.RemoteMicAudioReceived -= OnHostRemoteMicAudio;
            _host.RemoteMicStatusReceived -= OnHostRemoteMicStatus;
            _host.LocalMediaShareStateChanged -= OnHostLocalMediaShareState;
            _host.MutualTrustOfferRequested = null;
        }
        _receiver?.ReleaseAllBridgedClaims();
        _receiver?.SetUpstreamRosterOverlay(null, null);
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        if (_sharedMic.IsActive && _sharedMic.ActiveViaHost)
            _sharedMic.Stop(restoreDefaultMic: true);
        HandleLocalMediaShareState(fromHost: true, false, false, "");
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
        if (message.Msg == 0x0112 /* WM_SYSCOMMAND */ && !_exitRequested && !_isHiddenInTray)
        {
            int sysCmd = unchecked((int)(message.WParam.ToInt64() & 0xFFF0));
            if (sysCmd == 0xF120 /* SC_RESTORE */ || sysCmd == 0xF030 /* SC_MAXIMIZE */)
            {
                base.WndProc(ref message);
                PostToUi(BringWindowToForegroundSafely);
                return;
            }
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
        if (message.Msg == WM_CLIPBOARDUPDATE && !_closing.IsCancellationRequested && _ecosystemConfig.ClipboardSyncEnabled)
        {
            HandleLocalClipboardUpdated();
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
            _topMostPulseTimer?.Stop();
            _topMostPulseTimer?.Dispose();
            _topMostPulseTimer = null;
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
            _trayFlyout?.Hide();
            _trayFlyout?.Dispose();
            _trayFlyout = null;
            _notifFlyout?.Hide();
            _notifFlyout?.Dispose();
            _notifFlyout = null;
            _otpBanner?.Hide();
            _otpBanner?.Dispose();
            _otpBanner = null;
            _otpClipboardClearTimer?.Stop();
            _otpClipboardClearTimer?.Dispose();
            _otpClipboardClearTimer = null;
            if (_clipboardListenerAttached && IsHandleCreated)
            {
                try { RemoveClipboardFormatListener(Handle); } catch { }
                _clipboardListenerAttached = false;
            }
            _trayMenu?.Dispose();
            _trayMenu = null;
            _notificationCounter.Dispose();
            _deckBridge.Dispose();
            _closing.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnLocalNotificationCountChanged(int count)
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        _display.LocalNotificationCount = count;
        _receiver?.SetLocalNotificationCount(count);
        _host?.PublishNotificationCount(count);
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _closing.IsCancellationRequested || IsDisposed)
            return;
        _trayFlyout ??= new TrayDeviceFlyout(
            SwitchDirectlyToTarget,
            ReturnControlLocalFromExternal,
            PausePhoneForPaymentsFromBridge,
            ShowFromTray,
            (id, name, pt) => OpenNotificationsFlyoutForTarget(id, name, pt),
            d => OpenMediaForTrayDevice(d, wantCamera: true, wantMic: false),
            d => OpenMediaForTrayDevice(d, wantCamera: false, wantMic: true));
        _trayFlyout.ToggleNearCursor(BuildBridgeSnapshot());
    }

    private void OnTrayMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_trayMenu == null || _closing.IsCancellationRequested || IsDisposed) return;
        for (int i = _trayMenu.Items.Count - 1; i >= 0; i--)
        {
            if (Equals(_trayMenu.Items[i].Tag, "dynamic-device"))
                _trayMenu.Items.RemoveAt(i);
        }

        var snap = BuildBridgeSnapshot();
        int insertAt = 0;
        var header = new ToolStripMenuItem($"DeskTouch Devices ({snap.ConnectedCount} connected)")
        {
            Enabled = false,
            Tag = "dynamic-device"
        };
        _trayMenu.Items.Insert(insertAt++, header);

        foreach (var d in snap.Devices)
        {
            string badge = d.NotificationCount >= 0 ? $"  [{d.NotificationCount}]" : "";
            string label = d.IsLocal
                ? $"This Computer ({d.Name}){badge}"
                : $"{d.Name} ({d.Platform}){badge}";
            var item = new ToolStripMenuItem(label)
            {
                Checked = d.IsControlling || (d.IsSelected && d.IsLocal),
                Enabled = d.SupportsInput,
                Tag = "dynamic-device"
            };
            var captured = d;
            item.Click += (_, _) =>
            {
                if (captured.IsLocal)
                    ReturnControlLocalFromExternal();
                else if (captured.SupportsInput)
                    SwitchDirectlyToTarget(captured.Id);
            };
            _trayMenu.Items.Insert(insertAt++, item);
        }

        if (_sharedMic.IsActive)
        {
            var stopMicItem = new ToolStripMenuItem($"Stop Active Mic ({_sharedMic.SourceName} • {_sharedMic.DescribeActiveRoute()})")
            {
                Tag = "dynamic-device"
            };
            stopMicItem.Click += (_, _) => StopActiveRemoteMic();
            _trayMenu.Items.Insert(insertAt++, stopMicItem);
        }

        if (_localReceiverSharingMedia || _localHostSharingMedia)
        {
            var stopLocalShareItem = new ToolStripMenuItem("Stop Sharing This PC's Camera/Mic")
            {
                Tag = "dynamic-device"
            };
            stopLocalShareItem.Click += (_, _) =>
            {
                _receiver?.StopLocalMediaSharing();
                _host?.StopLocalMediaSharing();
                HandleLocalMediaShareState(fromHost: false, false, false, "");
                HandleLocalMediaShareState(fromHost: true, false, false, "");
            };
            _trayMenu.Items.Insert(insertAt++, stopLocalShareItem);
        }

        _trayMenu.Items.Insert(insertAt, new ToolStripSeparator { Tag = "dynamic-device" });
    }

    private void SwitchDirectlyToTarget(int targetId)
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        if (targetId == EnvironmentRouteOrder.Local)
        {
            ReturnControlLocalFromExternal();
            return;
        }
        EnsureCaptureArmedIfConnected();
        var actualEdge = _display.EdgeToward(EnvironmentRouteOrder.Local, targetId, CurrentBaseTargetId())
            ?? _preferredEdge;
        if (targetId == CurrentInputTargetId() && _capture?.IsArmed == true)
        {
            if (!_capture.RemoteActive)
            {
                _capture.SetEdge(actualEdge);
                _capture.BeginDirectHandoff();
            }
        }
        else
        {
            SelectConnectedTargetCore(targetId, actualEdge, 0.5, requestHandoff: true, directHandoff: true);
        }
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
    }

    private void ReturnControlLocalFromExternal()
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        _capture?.ReturnLocal();
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
    }

    private void PausePhoneForPaymentsFromBridge(int peerId)
    {
        if (_receiver == null || _closing.IsCancellationRequested || IsDisposed) return;
        int sent = _receiver.PauseConnectedAndroidForPayments(peerId > 0 ? peerId : null);
        if (sent == 0 && peerId > 0)
            sent = _receiver.PauseConnectedAndroidForPayments(null);
        if (sent > 0)
            Status("Sent payment-safe pause request to your phone so payment apps work normally.");
        PublishDeckBridgeSnapshot();
        RefreshTrayFlyoutIfVisible();
    }

    private void PublishEnvironmentSpatialLayout(int? explicitBaseTargetId = null)
    {
        if (_receiver == null || _closing.IsCancellationRequested || IsDisposed)
            return;
        try
        {
            int baseId = explicitBaseTargetId ?? CurrentBaseTargetId();
            var boundsByTarget = _display.GetArrangedDeviceBounds(baseId)
                .ToDictionary(x => x.TargetId, x => x.Bounds);
            if (boundsByTarget.Count == 0)
                return;

            var entries = new List<(string Key, RectangleF Bounds)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddEntry(string key, int targetId)
            {
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) return;
                if (boundsByTarget.TryGetValue(targetId, out var rect) && rect.Width > 0 && rect.Height > 0)
                    entries.Add((key, rect));
            }

            AddEntry(_receiver.Fingerprint, EnvironmentRouteOrder.Local);
            if (_host != null)
            {
                foreach (var member in _host.EnvironmentMembers)
                {
                    if (member.Id == _host.OwnEnvironmentMemberId) continue;
                    var localDirect = FindLocalReceiverPeerByIdentity(member.StableIdentity);
                    int targetId = localDirect?.Id ?? -member.Id;
                    string key = !string.IsNullOrWhiteSpace(member.StableIdentity)
                        ? member.StableIdentity
                        : member.Platform + "|" + member.Name;
                    AddEntry(key, targetId);
                }
                foreach (var (id, peer) in GetDistinctLocalReceiverPeersWhenHosted())
                    AddEntry(ReceiverServer.LayoutIdentity(peer), id);
            }
            else
            {
                foreach (var (id, peer) in _receiver.ConnectedPeers)
                    AddEntry(ReceiverServer.LayoutIdentity(peer), id);
                foreach (var (vid, peer, _) in _receiver.BridgedPeers)
                    AddEntry(ReceiverServer.LayoutIdentity(peer), vid);
            }

            var normalized = DeskSpaceDeckBridge.ComputeNormalizedLayout(
                entries,
                e => e.Key,
                e => e.Bounds);
            _receiver.SetEnvironmentSpatialLayout(normalized);
        }
        catch { }
    }

    private void RefreshTrayFlyoutIfVisible()
    {
        if (_trayFlyout is { Visible: true, IsDisposed: false })
            _trayFlyout.UpdateSnapshot(BuildBridgeSnapshot());
    }

    private void PublishDeckBridgeSnapshot()
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        _deckBridge.PublishSnapshot(BuildBridgeSnapshot());
    }

    private BridgeDeviceSnapshotDto BuildBridgeSnapshot()
    {
        int localNotif = _notificationCounter.CurrentCount;
        int currentTarget = CurrentInputTargetId();
        bool isRemoteActive = _capture?.RemoteActive == true;

        var items = new List<BridgeDeviceItemDto>
        {
            new BridgeDeviceItemDto(
                EnvironmentRouteOrder.Local,
                Environment.MachineName,
                "Windows",
                IsLocal: true,
                IsSelected: !isRemoteActive,
                IsControlling: !isRemoteActive,
                SupportsInput: true,
                NotificationCount: localNotif,
                StatusText: isRemoteActive ? "Click to return" : "Controlling")
        };

        var wf = _phoneWebcamForm;
        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRemote(int id, string name, string platform, bool supportsInput, int notifCount, string identity,
            int battPct = -1, bool chg = false, string sig = "", bool hasCam = false, bool hasMic = false)
        {
            if (!string.IsNullOrWhiteSpace(identity) && !seenIdentities.Add(identity))
                return;
            bool selected = id == currentTarget;
            bool controlling = isRemoteActive && selected;
            string status = !supportsInput
                ? (platform == "Android" ? "Paused" : "Unavailable")
                : (controlling ? "Controlling" : (selected ? "Selected" : "Connected"));
            bool isHostSource = id <= 0;
            int sourceId = isHostSource ? -id : id;
            bool activeMediaMatch = wf is { IsDisposed: false } &&
                wf.SourceViaHost == isHostSource && wf.PeerId == sourceId;
            bool micActive = hasMic && (_sharedMic.IsActiveFor(sourceId, isHostSource) ||
                                        (activeMediaMatch && wf!.IsMicStreaming));
            items.Add(new BridgeDeviceItemDto(
                id, name, platform, IsLocal: false,
                IsSelected: selected, IsControlling: controlling,
                SupportsInput: supportsInput,
                NotificationCount: notifCount >= 0 ? notifCount : -1,
                StatusText: status,
                BatteryPercent: battPct,
                IsCharging: chg,
                SignalLabel: sig,
                HasCamera: hasCam,
                HasMicrophone: hasMic,
                IsCameraActive: hasCam && activeMediaMatch && wf!.IsCameraStreaming,
                IsMicrophoneActive: micActive));
        }

        if (_host != null)
        {
            foreach (var member in _host.EnvironmentMembers)
            {
                if (member.Id == _host.OwnEnvironmentMemberId) continue;
                var localDirect = FindLocalReceiverPeerByIdentity(member.StableIdentity);
                int targetId = localDirect?.Id ?? -member.Id;
                bool supportsInput = localDirect?.Peer.SupportsReverseInput ?? member.SupportsReverseInput;
                int notif = localDirect is { } direct && direct.Peer.NotificationCount >= 0
                    ? direct.Peer.NotificationCount
                    : member.NotificationCount;
                int batt = localDirect is { } dB && dB.Peer.BatteryPercent >= 0 ? dB.Peer.BatteryPercent : member.BatteryPercent;
                bool chg = localDirect is { } dC ? dC.Peer.IsCharging : member.IsCharging;
                bool hasCam = localDirect is { } dCam ? dCam.Peer.HasCamera : member.HasCamera;
                bool hasMic = localDirect is { } dMic ? dMic.Peer.HasMicrophone : member.HasMicrophone;
                string identity = !string.IsNullOrWhiteSpace(member.StableIdentity)
                    ? member.StableIdentity
                    : member.Platform + "|" + member.Name;
                AddRemote(targetId, member.Name, member.Platform, supportsInput, notif, identity, batt, chg,
                    hasCam: hasCam, hasMic: hasMic);
            }
            foreach (var (id, peer) in GetDistinctLocalReceiverPeersWhenHosted())
            {
                AddRemote(id, peer.Name, peer.Platform, peer.SupportsReverseInput,
                    peer.NotificationCount, ReceiverServer.LayoutIdentity(peer),
                    peer.BatteryPercent, peer.IsCharging, peer.SignalLabel,
                    peer.HasCamera, peer.HasMicrophone);
            }
        }
        else if (_receiver != null)
        {
            foreach (var (id, peer) in _receiver.ConnectedPeers.OrderBy(p => p.Id))
            {
                AddRemote(id, peer.Name, peer.Platform, peer.SupportsReverseInput,
                    peer.NotificationCount, ReceiverServer.LayoutIdentity(peer),
                    peer.BatteryPercent, peer.IsCharging, peer.SignalLabel,
                    peer.HasCamera, peer.HasMicrophone);
            }
            foreach (var (vid, peer, _) in _receiver.BridgedPeers)
            {
                AddRemote(vid, peer.Name, peer.Platform, peer.SupportsReverseInput,
                    peer.NotificationCount, ReceiverServer.LayoutIdentity(peer),
                    peer.BatteryPercent, peer.IsCharging, peer.SignalLabel);
            }
        }

        var arrangedBounds = _display.GetArrangedDeviceBounds(CurrentBaseTargetId())
            .ToDictionary(x => x.TargetId, x => x.Bounds);
        var orderedItems = DeskSpaceDeckBridge.NormalizeAndOrderDevices(items, arrangedBounds);

        int connectedCount = Math.Max(0, orderedItems.Count - 1);
        bool hasKnown = orderedItems.Any(d => d.NotificationCount >= 0);
        int totalNotif = orderedItems.Where(d => d.NotificationCount >= 0).Sum(d => d.NotificationCount);
        string activeName = orderedItems.FirstOrDefault(d => d.IsControlling)?.Name ?? Environment.MachineName;

        return new BridgeDeviceSnapshotDto(
            Version: 1,
            UpdatedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LocalName: Environment.MachineName,
            LocalNotificationCount: localNotif,
            ActiveTargetId: isRemoteActive ? currentTarget : EnvironmentRouteOrder.Local,
            IsRemoteActive: isRemoteActive,
            ActiveDeviceName: activeName,
            ConnectedCount: connectedCount,
            TotalNotificationCount: totalNotif,
            HasAnyKnownNotificationCount: hasKnown,
            Devices: orderedItems,
            ActiveProfile: _ecosystemConfig.ActiveProfile,
            ClipboardSyncEnabled: _ecosystemConfig.ClipboardSyncEnabled,
            ActiveForegroundApp: _activeForegroundApp,
            ActiveShortcutPreset: _activeShortcutPreset);
    }

    private void RefreshEcosystemButtonsUi()
    {
        _profileButton.Text = $"Profile: {_ecosystemConfig.ActiveProfile}";
        _clipboardToggleButton.Text = _ecosystemConfig.ClipboardSyncEnabled ? "Clipboard: On" : "Clipboard: Off";
    }

    private void HandleLocalClipboardUpdated()
    {
        var cap = SharedClipboardHelper.TryCaptureOnStaThread();
        if (!cap.Changed) return;
        if (cap.Kind == "text")
        {
            _receiver?.BroadcastClipboardTextAsync(cap.Hash, cap.PayloadText);
            _host?.SendClipboardText(cap.Hash, cap.PayloadText);
            _sharedShelf.AddClipboardText(cap.PayloadText, Environment.MachineName);
        }
        else if (cap.Kind == "image" && cap.ImagePng != null)
        {
            _receiver?.BroadcastClipboardImageAsync(cap.Hash, cap.ImagePng);
            _host?.SendClipboardImage(cap.Hash, cap.ImagePng);
            _sharedShelf.AddClipboardImage(cap.ImagePng.Length, Environment.MachineName);
        }
    }

    private void ToggleClipboardSync()
    {
        _ecosystemConfig.ClipboardSyncEnabled = !_ecosystemConfig.ClipboardSyncEnabled;
        AutomationAndProfilesStore.Save(_ecosystemConfig);
        RefreshEcosystemButtonsUi();
        _receiver?.BroadcastProfileState(AutomationAndProfilesStore.SerializeSummaryForAndroid(_ecosystemConfig));
        PublishDeckBridgeSnapshot();
        Status(_ecosystemConfig.ClipboardSyncEnabled
            ? "Shared clipboard enabled across connected devices."
            : "Shared clipboard paused.");
    }

    private void CycleScreenProfile()
    {
        var list = _ecosystemConfig.Profiles;
        if (list.Count == 0) return;
        int cur = list.FindIndex(p => string.Equals(p.Name, _ecosystemConfig.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        int next = (cur + 1) % list.Count;
        ApplyScreenProfile(list[next].Name);
    }

    private void ApplyScreenProfile(string profileName)
    {
        var preset = _ecosystemConfig.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return;
        _ecosystemConfig.ActiveProfile = preset.Name;
        _ecosystemConfig.ClipboardSyncEnabled = preset.ClipboardSync;
        _ecosystemConfig.NotificationPreviewsEnabled = preset.NotificationPreviews;
        _ecosystemConfig.ActiveShortcutPreset = preset.ShortcutPreset;
        _activeShortcutPreset = preset.ShortcutPreset;
        _notificationCounter.EnablePreviewCapture = preset.NotificationPreviews;

        if (Enum.TryParse<MonitorEdge>(preset.Edge, true, out var edge))
        {
            _preferredEdge = edge;
            _display.RemoteEdge = edge;
            _capture?.SetEdge(edge);
        }
        int pct = Math.Clamp((int)Math.Round(preset.Sensitivity * 100.0),
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

        AutomationAndProfilesStore.Save(_ecosystemConfig);
        SavePreferredEdge();
        RefreshEcosystemButtonsUi();
        _receiver?.BroadcastProfileState(AutomationAndProfilesStore.SerializeSummaryForAndroid(_ecosystemConfig));
        PublishDeckBridgeSnapshot();
        Status($"Activated '{preset.Name}' profile.");
    }

    private void OpenProfilesRulesDialog()
    {
        using var dlg = new ProfilesRulesDialog(
            _ecosystemConfig,
            name => ApplyScreenProfile(name),
            cfg =>
            {
                _ecosystemConfig = cfg;
                _notificationCounter.EnablePreviewCapture = cfg.NotificationPreviewsEnabled;
                AutomationAndProfilesStore.Save(cfg);
                RefreshEcosystemButtonsUi();
                _receiver?.BroadcastProfileState(AutomationAndProfilesStore.SerializeSummaryForAndroid(cfg));
                PublishDeckBridgeSnapshot();
            });
        dlg.ShowDialog(this);
    }

    private void EvaluateForegroundAutomationRule(string proc, string title)
    {
        _activeForegroundApp = proc;
        var match = AutomationAndProfilesStore.MatchRule(_ecosystemConfig, proc, title);
        string nextPreset = match != null ? match.ShortcutPreset : _ecosystemConfig.ActiveShortcutPreset;
        if (!string.Equals(_activeShortcutPreset, nextPreset, StringComparison.OrdinalIgnoreCase))
        {
            _activeShortcutPreset = nextPreset;
            PublishDeckBridgeSnapshot();
        }
    }

    private void OpenNotificationsFlyoutForTarget(int targetId, string? fallbackName, Point screenAnchor)
    {
        string devName = fallbackName ?? (targetId == EnvironmentRouteOrder.Local ? Environment.MachineName : "Device");
        NotificationPreviewItem[] items;
        if (targetId == EnvironmentRouteOrder.Local)
        {
            _notificationCounter.EnablePreviewCapture = true;
            items = _notificationCounter.CurrentItems;
        }
        else if (targetId > 0 && _receiver != null)
        {
            items = _receiver.GetNotificationsForPeer(targetId);
        }
        else if (_host != null)
        {
            items = _host.GetNotificationsForTarget(-targetId);
        }
        else
        {
            items = Array.Empty<NotificationPreviewItem>();
        }

        _notifFlyout ??= new NotificationPreviewFlyout(
            onDismiss: (id, key) =>
            {
                if (id == EnvironmentRouteOrder.Local)
                    WindowsNotificationCounter.DismissLocalNotification(key);
                else if (id > 0)
                    _receiver?.SendNotificationDismiss(id, key);
                else
                    _host?.SendNotificationDismiss(-id, key);
            },
            onReply: (id, key, reply) =>
            {
                if (id > 0) _receiver?.SendNotificationReply(id, key, reply);
                else _host?.SendNotificationReply(-id, key, reply);
                Status($"Sent reply to notification on {devName}.");
            },
            onCopyOtp: CopyOneTimeCodeWithAutoClear);

        _notifFlyout.ShowForDevice(targetId, devName, items, screenAnchor);
    }

    private void ShowOtpOfferToast(string app, string code, string title)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        _sharedShelf.AddOtp(app, code, title, "Phone");
        if (!_ecosystemConfig.OtpHelperEnabled) return;
        _otpBanner ??= new OtpBannerForm(CopyOneTimeCodeWithAutoClear);
        _otpBanner.ShowCode(app, code);
        Status($"Verification code received from {app}.");
    }

    private void CopyOneTimeCodeWithAutoClear(string code)
    {
        try
        {
            SharedClipboardHelper.TryApplyTextOnStaThread(code);
            Status("Verification code copied cleanly. Clipboard will clear automatically in 45 seconds.");
            _otpClipboardClearTimer?.Stop();
            _otpClipboardClearTimer ??= new System.Windows.Forms.Timer { Interval = 45000 };
            _otpClipboardClearTimer.Tick += (_, _) =>
            {
                _otpClipboardClearTimer?.Stop();
                try
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == code)
                        Clipboard.Clear();
                }
                catch { }
            };
            _otpClipboardClearTimer.Start();
        }
        catch { }
    }

    private void HandleDeckFileDrop(int targetId, string? filePath)
    {
        int resolvedTarget = targetId == EnvironmentRouteOrder.Local ? PickFirstFileDropTargetId() : targetId;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            PromptAndSendFile(resolvedTarget);
            return;
        }
        if (resolvedTarget != int.MinValue && resolvedTarget != EnvironmentRouteOrder.Local && File.Exists(filePath))
            SendFilesToTargetFromDiagram(resolvedTarget, new[] { filePath });
    }

    private int PickFirstFileDropTargetId()
    {
        if (_receiver != null)
        {
            int sel = _receiver.SelectedPeerId;
            if (sel > 0) return sel;
            var first = _receiver.ConnectedPeers.FirstOrDefault();
            if (first.Id > 0) return first.Id;
        }
        if (_host != null)
        {
            int sel = _host.SelectedEnvironmentTargetId;
            return sel > 0 ? -sel : 0;
        }
        return int.MinValue;
    }

    private void PromptAndSendFile(int preferredTargetId)
    {
        int target = preferredTargetId is EnvironmentRouteOrder.Local or 0 && _receiver != null
            ? PickFirstFileDropTargetId()
            : preferredTargetId;
        if (target == int.MinValue)
        {
            Status("Connect a computer or phone first to send a file.");
            return;
        }
        using var ofd = new OpenFileDialog
        {
            Title = "Select a file or photo to send across DeskTouch",
            Multiselect = true
        };
        if (ofd.ShowDialog(this) == DialogResult.OK && ofd.FileNames.Length > 0)
            SendFilesToTargetFromDiagram(target, ofd.FileNames);
    }

    private void SendFilesToTargetFromDiagram(int targetId, string[] files)
    {
        if (files == null || files.Length == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (string path in files)
            {
                if (!File.Exists(path)) continue;
                bool ok = targetId > 0 && _receiver != null
                    ? await _receiver.SendFileToTargetAsync(targetId, path)
                    : (_host != null && await _host.SendFileToMemberAsync(targetId < 0 ? -targetId : 0, path));
                PostToUi(() =>
                {
                    if (ok) _sharedShelf.AddFile(path, "Sent", Environment.MachineName);
                    Status(ok
                        ? $"Sent '{Path.GetFileName(path)}' cleanly."
                        : $"Could not send '{Path.GetFileName(path)}' (max 25 MB).");
                });
            }
        });
    }

    private void OnIncomingFileSaved(string fromDevice, string savedPath)
    {
        string name = Path.GetFileName(savedPath);
        _sharedShelf.AddFile(savedPath, "Received", fromDevice);
        Status($"Received '{name}' from {fromDevice} in Downloads\\DeskTouch Received.");
        _trayIcon?.ShowBalloonTip(4000, $"File from {fromDevice}", $"Saved '{name}' to Downloads\\DeskTouch Received.", ToolTipIcon.Info);
    }

    private (int Id, string Name)? PickConnectedAndroidPeer()
    {
        if (_receiver == null) return null;
        int sel = _receiver.SelectedPeerId;
        var peers = _receiver.ConnectedPeers;
        var preferred = peers.FirstOrDefault(p => p.Id == sel && p.Peer.Platform == "Android");
        if (preferred.Peer != null) return (preferred.Id, preferred.Peer.Name);
        var any = peers.FirstOrDefault(p => p.Peer.Platform == "Android");
        return any.Peer != null ? (any.Id, any.Peer.Name) : null;
    }

    private void OpenPhoneScreenWindow()
    {
        var phone = PickConnectedAndroidPeer();
        if (phone == null)
        {
            MessageBox.Show(this,
                "Connect an Android phone to this computer first to mirror and tap its screen here.",
                "DeskTouch Vision — Phone Screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_phoneScreenForm is { IsDisposed: false } existing && existing.PeerId == phone.Value.Id)
        {
            existing.Activate();
            return;
        }
        _phoneScreenForm?.Close();
        _phoneScreenForm = new PhoneScreenForm(
            phone.Value.Id,
            phone.Value.Name,
            watchScreen: (id, en) => _receiver?.SendPhoneScreenWatch(id, en),
            sendTouch: (id, act, nx, ny) => _receiver?.SendPhoneTouch(id, act, nx, ny),
            sendNav: (id, nav) => _receiver?.SendPhoneNav(id, nav),
            sendText: (id, txt) => _receiver?.SendPhoneText(id, txt));
        _phoneScreenForm.Show(this);
    }

    private void OnHostRemoteCameraFrame(int sourceId, byte[] jpg) => PostToUi(() =>
    {
        if (_phoneWebcamForm is { IsDisposed: false } f && f.SourceViaHost && f.PeerId == sourceId)
            f.UpdateCameraFrame(jpg);
    });

    private void OnHostRemoteCameraStatus(int sourceId, int stateCode, string? msg) => PostToUi(() =>
    {
        if (_phoneWebcamForm is { IsDisposed: false } f && f.SourceViaHost && f.PeerId == sourceId && !string.IsNullOrWhiteSpace(msg))
            f.UpdateStatus(msg);
    });

    private void OnHostRemoteMicAudio(int sourceId, byte[] pcm, int rate, int seq)
    {
        try
        {
            _sharedMic.HandleIncomingAudio(sourceId, viaHost: true, pcm, rate, seq);
        }
        catch { }
    }

    private void OnHostRemoteMicStatus(int sourceId, int stateCode, string? msg) => PostToUi(() =>
    {
        if (_sharedMic.IsActiveFor(sourceId, viaHost: true) && !string.IsNullOrWhiteSpace(msg))
        {
            _sharedMic.MicStatusNote = stateCode == 1 ? "Mic Live" : msg;
            if (stateCode == 2)
            {
                Status(msg);
                _trayIcon?.ShowBalloonTip(5000, "Phone Microphone Action Needed", msg, ToolTipIcon.Info);
            }
        }
        if (_phoneWebcamForm is { IsDisposed: false } f && f.SourceViaHost && f.PeerId == sourceId)
            f.UpdateMicStatus(stateCode, msg);
    });

    private void OnHostLocalMediaShareState(bool cam, bool mic, string summary) => PostToUi(() =>
    {
        HandleLocalMediaShareState(fromHost: true, cam, mic, summary);
    });

    private void HandleLocalMediaShareState(bool fromHost, bool cam, bool mic, string summary)
    {
        bool active = (cam || mic) && !string.IsNullOrWhiteSpace(summary);
        bool wasSharing = _localReceiverSharingMedia || _localHostSharingMedia;
        if (fromHost)
        {
            _localHostSharingMedia = active;
            _localHostShareSummary = active ? summary : "";
        }
        else
        {
            _localReceiverSharingMedia = active;
            _localReceiverShareSummary = active ? summary : "";
        }

        if (active)
        {
            Status(summary);
        }
        else if (_localReceiverSharingMedia && !string.IsNullOrWhiteSpace(_localReceiverShareSummary))
        {
            Status(_localReceiverShareSummary);
        }
        else if (_localHostSharingMedia && !string.IsNullOrWhiteSpace(_localHostShareSummary))
        {
            Status(_localHostShareSummary);
        }
        else if (wasSharing)
        {
            Status("Stopped sharing this PC's camera/microphone.");
        }
    }

    private void SendWatchCameraForSource(MediaSourceOption src, bool enable, int lens, bool torch, bool mic)
    {
        if (src.ViaHost) _host?.SendRemoteCameraWatch(src.Id, enable, lens, torch, mic);
        else _receiver?.SendPhoneCameraWatch(src.Id, enable, lens, torch, mic);
    }

    private void SendWatchMicForSource(MediaSourceOption src, bool enable) =>
        SendWatchMicById(src.Id, src.ViaHost, enable);

    private void SendWatchMicById(int sourceId, bool viaHost, bool enable)
    {
        if (sourceId < 0) return;
        if (viaHost) _host?.SendRemoteMicWatch(sourceId, enable);
        else _receiver?.SendPhoneMicWatch(sourceId, enable);
    }

    private string GetTransportLabelForSource(MediaSourceOption src) =>
        src.ViaHost
            ? (_host?.MediaTransportLabel ?? "TCP Fallback")
            : (_receiver?.GetMediaTransportLabel(src.Id) ?? "TCP Fallback");

    private void StopActiveRemoteMic()
    {
        if (!_sharedMic.IsActive) return;
        int prevId = _sharedMic.ActiveSourceId;
        bool prevViaHost = _sharedMic.ActiveViaHost;
        string prevName = _sharedMic.SourceName;
        try { SendWatchMicById(prevId, prevViaHost, false); } catch { }
        _sharedMic.Stop(restoreDefaultMic: true);
        if (_phoneWebcamForm is { IsDisposed: false } wf)
            wf.SyncExternalMicState();
        Status($"Stopped microphone from {prevName}.");
        RefreshTrayFlyoutIfVisible();
        PublishDeckBridgeSnapshot();
    }

    private void ToggleBackgroundMicForSource(MediaSourceOption chosen)
    {
        if (!chosen.HasMicrophone)
        {
            Status($"{chosen.Name} does not report a microphone.");
            return;
        }
        if (_sharedMic.IsActiveFor(chosen.Id, chosen.ViaHost))
        {
            SendWatchMicForSource(chosen, false);
            _sharedMic.Stop(restoreDefaultMic: true);
            if (_phoneWebcamForm is { IsDisposed: false } wf)
                wf.SyncExternalMicState();
            Status($"Stopped background microphone from {chosen.Name}.");
            RefreshTrayFlyoutIfVisible();
            PublishDeckBridgeSnapshot();
            return;
        }

        if (_sharedMic.IsActive)
        {
            SendWatchMicById(_sharedMic.ActiveSourceId, _sharedMic.ActiveViaHost, false);
        }
        _sharedMic.SetActiveSource(chosen.Id, chosen.ViaHost, chosen.Name,
            backgroundMode: true, autoSwitchDefault: true);
        SendWatchMicForSource(chosen, true);
        if (_phoneWebcamForm is { IsDisposed: false } openWf)
            openWf.SyncExternalMicState();
        string transport = GetTransportLabelForSource(chosen);
        Status($"Background microphone active: {chosen.Name} → {_sharedMic.DescribeActiveRoute()} ({transport})");
        RefreshTrayFlyoutIfVisible();
        PublishDeckBridgeSnapshot();
    }

    private List<MediaSourceOption> GetAvailableMediaSources()
    {
        var list = new List<MediaSourceOption>();
        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_receiver != null)
        {
            foreach (var p in _receiver.ConnectedPeers.OrderBy(p => p.Id))
            {
                bool isAndroid = string.Equals(p.Peer.Platform, "Android", StringComparison.OrdinalIgnoreCase);
                bool isWindows = string.Equals(p.Peer.Platform, "Windows", StringComparison.OrdinalIgnoreCase);
                if (!isAndroid && !isWindows) continue;
                string identity = ReceiverServer.LayoutIdentity(p.Peer);
                if (!string.IsNullOrWhiteSpace(identity))
                    seenIdentities.Add(identity);
                if (!p.Peer.HasCamera && !p.Peer.HasMicrophone) continue;
                list.Add(new MediaSourceOption(
                    p.Id, p.Peer.Name, p.Peer.Platform,
                    SupportsTorch: isAndroid,
                    ViaHost: false,
                    HasCamera: p.Peer.HasCamera,
                    HasMicrophone: p.Peer.HasMicrophone));
            }
        }
        if (_host != null)
        {
            var members = _host.EnvironmentMembers;
            var hubMember = members.FirstOrDefault(m => m.Id == 0);
            if (hubMember != null &&
                (string.IsNullOrWhiteSpace(hubMember.StableIdentity) || seenIdentities.Add(hubMember.StableIdentity)) &&
                (hubMember.HasCamera || hubMember.HasMicrophone))
            {
                string hubName = !string.IsNullOrWhiteSpace(hubMember.Name)
                    ? hubMember.Name
                    : (!string.IsNullOrWhiteSpace(_remoteAddress) ? _remoteAddress : "Host PC");
                list.Add(new MediaSourceOption(
                    0, hubName, "Windows",
                    SupportsTorch: false,
                    ViaHost: true,
                    HasCamera: hubMember.HasCamera,
                    HasMicrophone: hubMember.HasMicrophone));
            }
            foreach (var m in members)
            {
                if (m.Id == 0 || m.Id == _host.OwnEnvironmentMemberId) continue;
                if (!string.IsNullOrWhiteSpace(m.StableIdentity) && !seenIdentities.Add(m.StableIdentity))
                    continue;
                bool isAndroid = string.Equals(m.Platform, "Android", StringComparison.OrdinalIgnoreCase);
                bool isWindows = string.Equals(m.Platform, "Windows", StringComparison.OrdinalIgnoreCase);
                if (!isAndroid && !isWindows) continue;
                if (!m.HasCamera && !m.HasMicrophone) continue;
                list.Add(new MediaSourceOption(
                    m.Id, m.Name, m.Platform,
                    SupportsTorch: isAndroid,
                    ViaHost: true,
                    HasCamera: m.HasCamera,
                    HasMicrophone: m.HasMicrophone));
            }
        }
        return list;
    }

    private void ShowPhoneWebcamForm(
        List<MediaSourceOption> sources,
        MediaSourceOption initial,
        bool initialCameraOn,
        bool initialMicOn)
    {
        _phoneWebcamForm?.Close();
        _phoneWebcamForm = new PhoneWebcamForm(
            sources,
            initial,
            initialCameraOn,
            initialMicOn,
            _sharedMic,
            watchCamera: SendWatchCameraForSource,
            watchMic: SendWatchMicForSource,
            isFastUdpActive: src => src.ViaHost
                ? _host?.IsFastMediaUdpActive == true
                : _receiver?.IsFastMediaUdpActive(src.Id) == true,
            getTransportLabel: GetTransportLabelForSource,
            onMediaStateChanged: () => PostToUi(() =>
            {
                RefreshTrayFlyoutIfVisible();
                PublishDeckBridgeSnapshot();
            }));
        _phoneWebcamForm.Show(this);
        Win.ForceWindowToFront(_phoneWebcamForm.Handle);
    }

    private void OpenMediaForTrayDevice(BridgeDeviceItemDto device, bool wantCamera, bool wantMic)
    {
        if (_closing.IsCancellationRequested || IsDisposed) return;
        var sources = GetAvailableMediaSources();
        if (sources.Count == 0)
        {
            Status("No connected phone or PC currently reports a camera or microphone.");
            return;
        }
        int matchIdx = sources.FindIndex(s =>
            device.Id > 0 ? (!s.ViaHost && s.Id == device.Id) : (s.ViaHost && s.Id == -device.Id));
        if (matchIdx < 0)
        {
            matchIdx = sources.FindIndex(s =>
                string.Equals(s.Name, device.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Platform, device.Platform, StringComparison.OrdinalIgnoreCase));
        }
        if (matchIdx < 0)
        {
            Status($"{device.Name} does not have a live camera/microphone stream available.");
            return;
        }

        var chosen = sources[matchIdx];
        if (wantMic && !wantCamera)
        {
            // Headless background tray microphone toggle: never open or activate PhoneWebcamForm.
            ToggleBackgroundMicForSource(chosen);
            return;
        }

        if (_phoneWebcamForm is { IsDisposed: false } existing)
        {
            if (wantCamera && !wantMic &&
                existing.PeerId == chosen.Id &&
                existing.SourceViaHost == chosen.ViaHost &&
                existing.IsCameraStreaming)
            {
                existing.Close();
                RefreshTrayFlyoutIfVisible();
                PublishDeckBridgeSnapshot();
                return;
            }
            existing.UpdateSources(sources);
            existing.ApplyExternalSelection(chosen, wantCamera, wantMic);
            if (!existing.Visible) existing.Show(this);
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            Win.ForceWindowToFront(existing.Handle);
            existing.Activate();
            RefreshTrayFlyoutIfVisible();
            PublishDeckBridgeSnapshot();
            return;
        }

        ShowPhoneWebcamForm(sources, chosen,
            initialCameraOn: wantCamera && chosen.HasCamera,
            initialMicOn: (wantMic && chosen.HasMicrophone) || _sharedMic.IsActiveFor(chosen.Id, chosen.ViaHost));
    }

    private void OpenPhoneWebcamWindow()
    {
        var sources = GetAvailableMediaSources();
        if (sources.Count == 0)
        {
            MessageBox.Show(this,
                "No connected phone or PC currently advertises a camera or microphone.",
                "DeskTouch Vision — Camera & Mic", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_phoneWebcamForm is { IsDisposed: false } existing)
        {
            existing.UpdateSources(sources);
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            Win.ForceWindowToFront(existing.Handle);
            existing.Activate();
            return;
        }
        MediaSourceOption initial = sources[0];
        if (_receiver != null)
        {
            int sel = _receiver.SelectedPeerId;
            int selIdx = sources.FindIndex(s => !s.ViaHost && s.Id == sel);
            int androidIdx = sources.FindIndex(s => s.Platform == "Android");
            if (selIdx >= 0) initial = sources[selIdx];
            else if (androidIdx >= 0) initial = sources[androidIdx];
        }
        ShowPhoneWebcamForm(sources, initial, initialCameraOn: initial.HasCamera, initialMicOn: initial.HasMicrophone);
    }

    private void OpenSharedShelfDialog()
    {
        if (_sharedShelfDialog is { IsDisposed: false } existing)
        {
            existing.Activate();
            return;
        }
        _sharedShelfDialog = new SharedShelfDialog(_sharedShelf);
        _sharedShelfDialog.Show(this);
    }

    private void ShowPairingQrDialog()
    {
        if (_receiver == null)
        {
            Status("Switch back to ready mode on this computer before generating a pairing QR.");
            return;
        }
        TriggerCrossNetworkRendezvousPublish(force: true);
        using var dlg = new PairingQrDialog(_receiver.Fingerprint, _receiver.Secret, _customPairingCode);
        dlg.ShowDialog(this);
    }

    private async Task RunOneClickRepairAsync()
    {
        _repairButton.Enabled = false;
        Status("Checking and fixing background processes and network access...");
        try
        {
            var report = await Task.Run(() => BackgroundAndNetworkRepairHelper.RunSelfRepair());
            try
            {
                _discovery?.Dispose();
                _discovery = null;
                if (_receiver != null && _host == null)
                {
                    _discovery = new NearbyComputerDiscovery(_receiver.Fingerprint,
                        () => Volatile.Read(ref _receiver)?.CanAcceptNewPeer == true,
                        () => _receiver?.ConnectedDeviceFingerprints.ToList() ?? new List<string>(),
                        () => _host == null,
                        () => Volatile.Read(ref _receiver)?.ActiveEnvironmentDeviceCount ?? 1,
                        _customPairingCode);
                    _discovery.Start();
                }
            }
            catch { }

            _adbBridge?.TriggerRefresh();
            var bridgeSnap = _adbBridge?.CurrentStatus;
            string bridgeSummary = bridgeSnap != null
                ? $"\nVirtual Bridge: {bridgeSnap.Detail}"
                : "";
            string summary =
                $"Stopped stuck updater helpers: {report.StoppedStaleUpdaters}\n" +
                $"Cleared duplicate background processes: {report.StoppedDuplicateProcesses}\n" +
                $"Cleaned old temporary folders: {report.CleanedDebrisFolders}\n" +
                $"{report.FirewallNote}{bridgeSummary}";
            Status("Background & network check finished.");
            if (!IsDisposed)
            {
                MessageBox.Show(this, summary, "DeskTouch Vision — Fix Background & Network",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            if (!IsDisposed) _repairButton.Enabled = true;
        }
    }

    private void OpenWirelessBridgePairDialog()
    {
        if (_adbBridge == null)
        {
            MessageBox.Show(this, "DeskTouch device sharing is still starting up. Try again in a moment.",
                "DeskTouch Vision", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new WirelessBridgePairDialog(
            _adbBridge.CurrentStatus,
            (ip, pPort, code, cPort) => _adbBridge.PairWirelessDebuggingAsync(ip, pPort, code, cPort));
        dlg.ShowDialog(this);
    }

    private void ExecutePowerActionFromUi(int targetId, string action)
    {
        if (_receiver != null) _receiver.SendPowerAction(targetId, action);
        else if (_host != null) _host.SendPowerAction(targetId <= 0 ? 0 : -targetId, action);
        else SystemMediaPowerHelper.ExecutePowerAction(action);
        Status($"Executed power command ({action}).");
    }

    private void ExecuteMediaActionFromUi(int targetId, string action)
    {
        if (_receiver != null) _receiver.SendMediaAction(targetId, action);
        else if (_host != null) _host.SendMediaAction(targetId <= 0 ? 0 : -targetId, action);
        else SystemMediaPowerHelper.ExecuteMediaAction(action);
    }

    private void WakeRememberedComputers()
    {
        if (_receiver != null) _receiver.SendPowerAction(0, "wake");
        var macs = new List<string>();
        if (_trusted != null && _receiver != null)
        {
            foreach (var d in _trusted.Outgoing)
                macs.AddRange(_receiver.GetRememberedMacs(d.Fingerprint));
        }
        int woken = macs.Count > 0 ? SystemMediaPowerHelper.SendWakeOnLan(macs.Distinct()) : 0;
        Status(woken > 0
            ? $"Sent Wake-on-LAN signal to {woken} trusted network adapter(s)."
            : "Screen wake signal sent to connected devices.");
    }
}
