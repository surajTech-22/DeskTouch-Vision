using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal sealed class PairComputerDialog : Form
{
    private sealed record NearbyItem(NearbyComputer Peer)
    {
        public override string ToString() =>
            $"{(Peer.IsAndroid ? "Android" : Peer.IsHub ? $"Environment hub ({Peer.MemberCount} devices)" : "Windows member")}  •  {Peer.Name}  •  {Peer.Address}  •  " +
            (Peer.IsAndroid ? "Join from phone" :
             Peer.CanReceive ? "Accepting members" : "At capacity");
    }

    private readonly ListBox _nearby = new() { Dock = DockStyle.Fill,
        IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11), BackColor = Color.White };
    private readonly TextBox _manual = new() { Dock = DockStyle.Fill,
        Multiline = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "Enter custom pairing code or paste full DT1 link" };
    private readonly Label _discoveryStatus = new() { Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(85, 89, 94), AutoEllipsis = true };
    private readonly Label _error = new() { Dock = DockStyle.Bottom, Height = 32,
        ForeColor = Color.FromArgb(165, 48, 48), Padding = new Padding(20, 5, 10, 5) };
    private readonly Button _refresh = new() { Text = "Scan devices", Dock = DockStyle.Right, Width = 116,
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 241, 243) };
    private readonly Button _repair = new() { Text = "Fix LAN access", Dock = DockStyle.Right, Width = 125,
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 241, 243) };
    private readonly CancellationTokenSource _cancel = new();
    private bool _searching;

    internal string Address { get; private set; } = "";
    internal string Fingerprint { get; private set; } = "";
    internal string Secret { get; private set; } = "";
    internal string ComputerName { get; private set; } = "";
    internal bool UseRemoteApproval { get; private set; }

    internal PairComputerDialog()
    {
        Text = "Add devices";
        BackColor = Color.FromArgb(244, 244, 247);
        ForeColor = Color.FromArgb(26, 27, 30);
        ClientSize = new Size(640, 475);
        MinimumSize = new Size(530, 440);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var title = new Label { Dock = DockStyle.Top, Height = 52,
            Text = "Add devices", Font = new Font("Segoe UI Semibold", 22),
            Padding = new Padding(19, 9, 10, 0) };
        var intro = new Label { Dock = DockStyle.Top, Height = 60,
            Text = "Join one DeskTouch environment hub; its existing PCs and phones appear automatically.\n" +
                   "Each member connects once to the hub, never separately to every device.",
            Padding = new Padding(20, 3, 10, 6) };

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
            RowCount = 5, Padding = new Padding(18, 6, 18, 5) };
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var nearbyHeader = new Panel { Dock = DockStyle.Fill };
        var listTitle = new Label { Dock = DockStyle.Fill,
            Text = "Nearby devices", Font = new Font("Segoe UI Semibold", 11),
            TextAlign = ContentAlignment.MiddleLeft };
        nearbyHeader.Controls.Add(listTitle);
        nearbyHeader.Controls.Add(_refresh);
        nearbyHeader.Controls.Add(_repair);
        fields.Controls.Add(nearbyHeader, 0, 0);
        fields.Controls.Add(_nearby, 0, 1);
        fields.Controls.Add(_discoveryStatus, 0, 2);
        fields.Controls.Add(new Label { Dock = DockStyle.Fill,
            Text = "Not listed? Paste a pairing code instead",
            Font = new Font("Segoe UI Semibold", 10),
            TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        fields.Controls.Add(_manual, 0, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 53,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 6) };
        var connect = new Button { Text = "Connect", Width = 103, Height = 33,
            BackColor = Color.FromArgb(0, 94, 183), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", Width = 94, Height = 33,
            DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        buttons.Controls.Add(connect);
        buttons.Controls.Add(cancel);
        Controls.Add(fields);
        Controls.Add(_error);
        Controls.Add(buttons);
        Controls.Add(intro);
        Controls.Add(title);
        AcceptButton = connect;
        CancelButton = cancel;
        connect.Click += async (_, _) => await AcceptSelectionAsync();
        _nearby.DoubleClick += async (_, _) => await AcceptSelectionAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _repair.Click += (_, _) => RepairPrivateLanFirewall();
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => _cancel.Cancel();
    }

    private async Task RefreshAsync()
    {
        if (_searching || _cancel.IsCancellationRequested) return;
        _searching = true;
        _refresh.Enabled = false;
        _discoveryStatus.Text = "Searching the local network...";
        try
        {
            var peers = await NearbyComputerDiscovery.FindAsync(_cancel.Token);
            if (IsDisposed || Disposing) return;
            var localIPs = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _nearby.BeginUpdate();
            try
            {
                _nearby.Items.Clear();
                foreach (var peer in peers.Where(p => !localIPs.Contains(p.Address)))
                    _nearby.Items.Add(new NearbyItem(peer));
                if (_nearby.Items.Count > 0) _nearby.SelectedIndex = 0;
            }
            finally { _nearby.EndUpdate(); }
            int mobileCount = _nearby.Items.Cast<NearbyItem>().Count(x => x.Peer.IsAndroid);
            int windowsCount = _nearby.Items.Count - mobileCount;
            _discoveryStatus.Text = _nearby.Items.Count == 0
                ? "No devices found. Keep DeskTouch running on each PC/phone; check LAN firewall."
                : $"{windowsCount} Windows PC(s), {mobileCount} Android phone(s). Busy PCs stay visible.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _discoveryStatus.Text = "Network scan unavailable: " + ex.Message; }
        finally
        {
            _searching = false;
            if (!IsDisposed) _refresh.Enabled = true;
        }
    }

    internal static NearbyComputer? FindPeerByCode(
        IEnumerable<NearbyComputer> peers,
        string rawCode,
        out bool matchedCustomTag)
    {
        matchedCustomTag = false;
        string normalized = PairingCodeHelper.NormalizeCustomCode(rawCode);
        if (normalized.Length == 0)
            return null;

        var candidates = peers.Where(p => !p.IsAndroid).ToArray();
        foreach (var peer in candidates)
        {
            string expectedTag = PairingCodeHelper.ComputeDiscoveryTag(peer.Fingerprint, normalized);
            if (!string.IsNullOrEmpty(expectedTag) &&
                string.Equals(peer.PairingTag, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                matchedCustomTag = true;
                return peer;
            }
        }

        if (normalized.Length >= 6 && normalized.All(Uri.IsHexDigit))
        {
            var hexMatches = candidates
                .Where(p => p.Fingerprint.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (hexMatches.Length == 1)
                return hexMatches[0];
        }
        return null;
    }

    private async Task AcceptSelectionAsync()
    {
        string manual = _manual.Text.Trim();
        if (!string.IsNullOrWhiteSpace(manual))
        {
            if (manual.Contains('|'))
            {
                var parts = manual.Split('|');
                var parsedAddrs = parts.Length == 4 ? CrossNetworkRendezvous.ParseCandidateAddresses(parts[1]) : Array.Empty<IPAddress>();
                string rawSecret = parts.Length == 4 ? parts[3].Trim().ToUpperInvariant() : "";
                bool validHexSecret = rawSecret.Length == 32 && rawSecret.All(Uri.IsHexDigit);
                bool validCustomSecret = !string.IsNullOrEmpty(PairingCodeHelper.NormalizeCustomCode(rawSecret));
                if (parts.Length != 4 || !parts[0].Trim().Equals("DT1", StringComparison.OrdinalIgnoreCase) ||
                    parsedAddrs.Count == 0 ||
                    parts[2].Trim().Length != 64 || !parts[2].Trim().All(Uri.IsHexDigit) ||
                    (!validHexSecret && !validCustomSecret))
                {
                    _error.Text = "Invalid pairing link. Copy the full link or enter a pairing code.";
                    return;
                }
                Address = string.Join(",", parsedAddrs.Select(a => a.ToString()));
                Fingerprint = parts[2].Trim().ToUpperInvariant();
                Secret = validHexSecret ? rawSecret : PairingCodeHelper.NormalizeCustomCode(rawSecret);
                ComputerName = parsedAddrs[0].ToString();
                UseRemoteApproval = false;
            }
            else if (CrossNetworkRendezvous.ParseCandidateAddresses(manual).Count > 0)
            {
                _error.Text = "For a direct IP, paste the full DT1|ip|fingerprint|secret link or enter the 4–32 character Pairing Code.";
                return;
            }
            else
            {
                var codeMatched = FindPeerByCode(
                    _nearby.Items.Cast<NearbyItem>().Select(x => x.Peer),
                    manual,
                    out bool matchedCustomTag);
                if (codeMatched == null)
                {
                    _discoveryStatus.Text = "Not on same Wi-Fi — resolving pairing code across networks...";
                    _error.Text = "";
                    codeMatched = await CrossNetworkRendezvous.ResolveByCodeAsync(manual, _cancel.Token);
                    if (codeMatched != null)
                        matchedCustomTag = true;
                }
                if (codeMatched == null)
                {
                    _error.Text = "No computer matched that pairing code on local Wi-Fi or cross-network rendezvous. Check the code or paste the full DT1 link.";
                    return;
                }
                if (!codeMatched.CanReceive)
                {
                    _error.Text = "That Windows endpoint is at capacity. The existing environment stays connected.";
                    return;
                }
                Address = codeMatched.Address;
                Fingerprint = codeMatched.Fingerprint;
                ComputerName = codeMatched.Name;
                if (matchedCustomTag)
                {
                    Secret = PairingCodeHelper.NormalizeCustomCode(manual);
                    UseRemoteApproval = false;
                }
                else
                {
                    Secret = "";
                    UseRemoteApproval = true;
                }
            }
        }
        else if (_nearby.SelectedItem is NearbyItem selected)
        {
            if (selected.Peer.IsAndroid)
            {
                _error.Text = "On that Android device, select this Windows PC to connect.";
                return;
            }
            if (!selected.Peer.CanReceive)
            {
                _error.Text = "That Windows endpoint is at capacity. The existing environment stays connected.";
                return;
            }
            int maxHubCount = _nearby.Items.Cast<NearbyItem>()
                .Where(item => !item.Peer.IsAndroid && item.Peer.CanReceive)
                .Select(item => item.Peer.IsHub ? Math.Max(2, item.Peer.MemberCount) : 1)
                .DefaultIfEmpty(1)
                .Max();
            int chosenCount = selected.Peer.IsHub ? Math.Max(2, selected.Peer.MemberCount) : 1;
            if (!selected.Peer.IsHub && _nearby.Items.Cast<NearbyItem>()
                .Any(item => item.Peer.IsHub && item.Peer.CanReceive))
            {
                _error.Text = "Join the available environment hub, not an already joined member PC.";
                return;
            }
            if (maxHubCount >= 2 && chosenCount < maxHubCount)
            {
                _error.Text = $"Join the larger {maxHubCount}-device environment hub so all devices stay together.";
                return;
            }
            Address = selected.Peer.Address;
            Fingerprint = selected.Peer.Fingerprint;
            ComputerName = selected.Peer.Name;
            Secret = "";
            UseRemoteApproval = true;
        }
        else
        {
            _error.Text = "Select an available Windows PC, or paste a pairing code.";
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RepairPrivateLanFirewall()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "ALLOW_PRIVATE_LAN.ps1");
        if (!File.Exists(script))
        {
            _error.Text = "LAN repair script is missing; update the Windows app.";
            return;
        }
        if (MessageBox.Show(this,
            "Allow DeskTouch Vision to accept connections from this private local network?\n\n" +
            "Windows will ask for administrator approval. Only TCP 37377 and UDP 37378 " +
            "for the DeskTouch sharing app on Private networks are affected.",
            "Repair private LAN access", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"",
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(info);
            _error.Text = "Approve Windows UAC, then scan devices again.";
        }
        catch (Exception ex)
        {
            _error.Text = "LAN repair not approved: " + ex.Message;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancel.Dispose();
        base.Dispose(disposing);
    }
}
