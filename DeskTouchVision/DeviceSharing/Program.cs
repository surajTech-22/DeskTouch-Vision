using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Drawing;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ShareForm());
    }
}

internal sealed class ShareForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Height = 65, Dock = DockStyle.Bottom,
        Padding = new Padding(10), ForeColor = Color.White, BackColor = Color.FromArgb(31, 48, 72) };
    private readonly TextBox _localIPs = Text(""), _fingerprint = Text(""), _secret = Text("");
    private readonly TextBox _remoteIp = Text("192.168.1."), _remoteFingerprint = Text(""), _remoteSecret = Text("");
    private readonly Button _listen = Btn("Start receiver on this PC");
    private readonly Button _stopReceive = Btn("Stop receiver");
    private readonly Button _connect = Btn("Connect to receiver");
    private readonly Button _disconnect = Btn("Disconnect / return local");
    private readonly Button _arm = Btn("Arm right-edge switching");
    private readonly Button _return = Btn("Return cursor to local PC");
    private readonly MonitorMap _localMonitorMap = new();
    private readonly MonitorMap _remoteMonitorMap = new();
    private readonly MonitorMap _receiverMonitorMap = new();
    private readonly CheckBox _accept = new() { Text = "I understand control leaves this computer until I return it",
        ForeColor = Color.White, AutoSize = true, Padding = new Padding(3, 6, 0, 0) };

    private ReceiverServer? _receiver;
    private PhysicalInputMonitor? _nativeInput;
    private HostSession? _host;
    private SourceCapture? _capture;

    internal ShareForm()
    {
        Text = "DeskTouch Vision - Windows Device Share (Experimental 0.2)";
        ClientSize = new Size(960, 670);
        MinimumSize = new Size(830, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 26, 43);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        KeyPreview = true;

        var title = new Label { Text = "Windows Device Sharing", Dock = DockStyle.Top, Height = 70,
            Font = new Font("Segoe UI Semibold", 23), ForeColor = Color.White,
            Padding = new Padding(18, 12, 0, 0) };
        var subtitle = new Label {
            Text = "Every PC's physical keyboard/mouse always wins; remote control pauses immediately on native activity.\n" +
                   "Windows Extended Desktop is preserved; only external edges transfer between computers.",
            Dock = DockStyle.Top, Height = 73, ForeColor = Color.FromArgb(186, 208, 229),
            Padding = new Padding(22, 1, 15, 4) };
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(15), BackColor = BackColor };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.Controls.Add(ReceiverColumn(), 0, 0);
        columns.Controls.Add(HostColumn(), 1, 0);

        Controls.Add(columns);
        Controls.Add(_status);
        Controls.Add(subtitle);
        Controls.Add(title);
        _localIPs.ReadOnly = _fingerprint.ReadOnly = _secret.ReadOnly = true;
        _stopReceive.Enabled = _disconnect.Enabled = _arm.Enabled = _return.Enabled = false;

        _listen.Click += (_, _) => StartReceiver();
        _stopReceive.Click += (_, _) => StopReceiver();
        _connect.Click += async (_, _) => await ConnectHostAsync();
        _disconnect.Click += (_, _) => DisconnectHost();
        _arm.Click += (_, _) => Arm();
        _return.Click += (_, _) => _capture?.ReturnLocal();
        Shown += (_, _) =>
        {
            RefreshMonitorMaps();
            Status("Windows extended displays detected. Start receiver on PC 2; connect from PC 1.");
        };
        FormClosing += (_, _) => { DisconnectHost(); StopReceiver(); };
    }

    private Control ReceiverColumn()
    {
        var panel = MakeColumn("PC 2 — RECEIVE input", "Run this app on the computer you want to control.");
        panel.Controls.Add(new Label { Text = "This PC's native Windows monitor layout",
            AutoSize = true, ForeColor = Color.White });
        panel.Controls.Add(_receiverMonitorMap);
        Add(panel, "Receiver LAN IPv4 (copy to PC 1)", _localIPs);
        Add(panel, "Receiver certificate SHA-256 (copy to PC 1)", _fingerprint);
        Add(panel, "Secret pairing key (copy to PC 1; keep private)", _secret);
        panel.Controls.Add(_listen);
        panel.Controls.Add(_stopReceive);
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(405, 0),
            Text = "Built-in keyboard, touchpad and any physically connected mouse ALWAYS have priority. " +
                   "After local activity, remote access can be requested again once this PC is idle for 2 seconds. " +
                   "Windows firewall: allow trusted PRIVATE networks only.",
            ForeColor = Color.FromArgb(180, 193, 213) });
        return panel;
    }

    private Control HostColumn()
    {
        var panel = MakeColumn("PC 1 — SHARE keyboard + mouse",
            "Physical keyboard and mouse stay connected to this source PC.");
        panel.Controls.Add(new Label { Text = "Local extended displays (read directly from Windows)",
            AutoSize = true, ForeColor = Color.White });
        panel.Controls.Add(_localMonitorMap);
        panel.Controls.Add(new Label { Text = "Remote extended displays (after pairing)",
            AutoSize = true, ForeColor = Color.White });
        panel.Controls.Add(_remoteMonitorMap);
        Add(panel, "PC 2 receiver IPv4", _remoteIp);
        Add(panel, "PC 2 fingerprint", _remoteFingerprint);
        Add(panel, "PC 2 secret pairing key", _remoteSecret);
        panel.Controls.Add(_accept);
        panel.Controls.Add(_connect);
        panel.Controls.Add(_arm);
        panel.Controls.Add(_return);
        panel.Controls.Add(_disconnect);
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(405, 0),
            Text = "1. Connect  2. Arm  3. Move pointer past RIGHT edge to hand off. " +
                   "Move past LEFT edge of receiver to return. Emergency return: Ctrl+Alt+Shift+F12. " +
                   "Closing this window restores local control.",
            ForeColor = Color.FromArgb(180, 193, 213) });
        return panel;
    }

    private static FlowLayoutPanel MakeColumn(string heading, string desc)
    {
        var result = new FlowLayoutPanel { Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(10),
            BackColor = Color.FromArgb(24, 38, 59) };
        result.Controls.Add(new Label { Text = heading, Height = 39, Width = 410,
            ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 16) });
        result.Controls.Add(new Label { Text = desc, Height = 42, Width = 410,
            ForeColor = Color.FromArgb(175, 200, 218) });
        return result;
    }

    private static TextBox Text(string value) => new() { Text = value,
        Width = 408, Height = 32, BackColor = Color.FromArgb(40, 58, 82),
        ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private static Button Btn(string label) => new() { Text = label, Height = 35, Width = 408,
        ForeColor = Color.White, BackColor = Color.FromArgb(45, 86, 132),
        FlatStyle = FlatStyle.Flat };
    private static void Add(FlowLayoutPanel panel, string label, TextBox entry)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.White,
            Padding = new Padding(0, 8, 0, 0) });
        panel.Controls.Add(entry);
    }

    private void Status(string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { if (IsHandleCreated) BeginInvoke(() => Status(msg)); } catch (InvalidOperationException) { }
            return;
        }
        _status.Text = "STATUS — " + msg;
    }

    private void RefreshMonitorMaps()
    {
        try
        {
            var topology = MonitorTopology.Capture();
            _localMonitorMap.Layout = topology;
            _receiverMonitorMap.Layout = topology;
            _remoteMonitorMap.Layout = _host?.RemoteMonitors;
        }
        catch (Exception ex)
        {
            Status("Windows display enumeration failed: " + ex.Message);
        }
    }

    private void HandleDisplayChange()
    {
        // Windows may move, remove, resize, rotate or add a native monitor.
        // Do not keep using stale geometry, or the cursor could jump off-screen.
        DisconnectHost();
        StopReceiver();
        RefreshMonitorMaps();
        Status("Windows extended-monitor layout changed. Pair again to refresh both PCs' display coordinates.");
    }

    private void StartReceiver()
    {
        if (_receiver != null || _host != null) return;
        try
        {
            _nativeInput = new PhysicalInputMonitor(this);
            _nativeInput.Start();
            _receiver = new ReceiverServer(Status, _nativeInput.Priority);
            _receiver.Start();
            _localIPs.Text = string.Join(" / ", Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()));
            _fingerprint.Text = _receiver.Fingerprint;
            _secret.Text = _receiver.Secret;
            _listen.Enabled = false;
            _stopReceive.Enabled = true;
            _connect.Enabled = false;
            Status("Receiver listening on TCP " + Wire.Port + ". Share the IP, fingerprint and pairing key with PC 1.");
        }
        catch (Exception ex)
        {
            StopReceiver();
            Status("Receiver setup failed: " + ex.Message);
        }
    }

    private void StopReceiver()
    {
        _receiver?.Dispose();
        _receiver = null;
        _nativeInput?.Dispose();
        _nativeInput = null;
        _fingerprint.Clear(); _secret.Clear();
        _listen.Enabled = true;
        _stopReceive.Enabled = false;
        _connect.Enabled = _host == null;
    }

    private async Task ConnectHostAsync()
    {
        if (_host != null || _receiver != null || !_accept.Checked)
        {
            Status("Check the authorization box and ensure receiver mode is not active.");
            return;
        }
        _connect.Enabled = false;
        Status("Connecting via authenticated local encrypted connection...");
        try
        {
            _host = await HostSession.ConnectAsync(_remoteIp.Text, _remoteFingerprint.Text,
                _remoteSecret.Text, msg => {
                    Status(msg);
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(DisconnectHost); }
                    catch (InvalidOperationException) { }
                }, CancellationToken.None);
            if (IsDisposed) { _host.Dispose(); return; }
            _capture = new SourceCapture(this, _host, Status);
            _host.RemoteControlState += HandleRemoteControlState;
            RefreshMonitorMaps();
            _arm.Enabled = _disconnect.Enabled = true;
            _return.Enabled = true;
            Status("Connected to PC 2. Press ARM, then cross the RIGHT edge of this PC.");
        }
        catch (Exception ex)
        {
            Status("Connection failed: " + ex.Message);
            _connect.Enabled = true;
        }
    }

    private void HandleRemoteControlState(string result)
    {
        if (IsDisposed || !IsHandleCreated) return;
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

    private void Arm()
    {
        try
        {
            _capture?.Arm();
            _arm.Enabled = false;
        }
        catch (Exception ex) { Status("Capture setup failed: " + ex.Message); }
    }

    private void DisconnectHost()
    {
        if (_host != null) _host.RemoteControlState -= HandleRemoteControlState;
        _capture?.Dispose();
        _capture = null;
        _host?.Dispose();
        _host = null;
        _connect.Enabled = _receiver == null;
        _remoteMonitorMap.Layout = null;
        _disconnect.Enabled = _arm.Enabled = _return.Enabled = false;
        Status("Disconnected. Keyboard and mouse stay local.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win.WM_INPUT)
        {
            // Only one role can register Raw Input on this form at a time.
            // Receiver detects native physical input; source forwards input.
            if (_nativeInput != null) _nativeInput.HandleRawInput(m.LParam);
            else _capture?.OnRawInput(m.LParam);
        }
        if (m.Msg == 0x007E && IsHandleCreated) // WM_DISPLAYCHANGE
        {
            // Do not mutate connection / controls during a Windows message callback.
            BeginInvoke(HandleDisplayChange);
        }
        base.WndProc(ref m);
    }
}
