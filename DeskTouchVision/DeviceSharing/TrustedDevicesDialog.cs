using System.Drawing;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>Explicitly manage and revoke per-user trusted LAN peer grants.</summary>
internal sealed class TrustedDevicesDialog : Form
{
    private sealed record DeviceItem(string Name, string Id, bool Outbound, bool Auto)
    {
        public override string ToString() => Outbound
            ? $"Connect to {Name}   —   Auto-connect {(Auto ? "ON" : "OFF")}"
            : $"Allow incoming from {Name}   —   Trusted";
    }

    private readonly TrustedDeviceStore _trusted;
    private readonly ListBox _devices = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 10),
        BackColor = Color.FromArgb(44, 46, 53),
        ForeColor = Color.WhiteSmoke
    };
    private readonly Button _toggle = new()
    {
        Text = "Toggle auto-connect",
        Width = 165, Height = 35,
        FlatStyle = FlatStyle.Flat
    };
    private readonly Button _forget = new()
    {
        Text = "Forget device",
        Width = 122, Height = 35,
        FlatStyle = FlatStyle.Flat
    };

    internal TrustedDevicesDialog(TrustedDeviceStore trusted)
    {
        _trusted = trusted;
        Text = "DeskTouch — Trusted devices";
        BackColor = Color.FromArgb(31, 32, 36);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(650, 370);
        MinimumSize = new Size(535, 320);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            Dock = DockStyle.Top, Height = 71,
            Text = "Trusted computers\nAuto-connect only to verified, explicitly trusted devices.",
            Padding = new Padding(14, 9, 10, 3)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 53, WrapContents = false,
            AutoScroll = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(9, 7, 9, 6)
        };
        var close = new Button
        {
            Text = "Close", DialogResult = DialogResult.OK, Width = 95, Height = 35
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_forget);
        buttons.Controls.Add(_toggle);
        Controls.Add(_devices);
        Controls.Add(buttons);
        Controls.Add(heading);

        _devices.SelectedIndexChanged += (_, _) => UpdateSelection();
        _toggle.Click += (_, _) => ToggleSelected();
        _forget.Click += (_, _) => ForgetSelected();
        RefreshList();
    }

    private void RefreshList()
    {
        _devices.BeginUpdate();
        try
        {
            _devices.Items.Clear();
            foreach (var x in _trusted.Outgoing)
                _devices.Items.Add(new DeviceItem(
                    x.Name, x.Fingerprint, true, x.AutoConnect));
            foreach (var x in _trusted.Incoming)
                _devices.Items.Add(new DeviceItem(
                    x.Name, x.TokenSha256, false, false));
            if (_devices.Items.Count > 0) _devices.SelectedIndex = 0;
        }
        finally { _devices.EndUpdate(); }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var item = _devices.SelectedItem as DeviceItem;
        _toggle.Enabled = item?.Outbound == true;
        _forget.Enabled = item != null;
    }

    private void ToggleSelected()
    {
        if (_devices.SelectedItem is not DeviceItem { Outbound: true } item) return;
        try { _trusted.SetAutoConnect(item.Id, !item.Auto); RefreshList(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message,
            "Unable to change trusted device", MessageBoxButtons.OK,
            MessageBoxIcon.Warning); }
    }

    private void ForgetSelected()
    {
        if (_devices.SelectedItem is not DeviceItem item) return;
        if (MessageBox.Show(this,
            $"Forget {item.Name}? Future connections will require new approval.\n\n" +
            "An already-connected session is not forcibly ended.",
            "Remove trusted access?", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            if (item.Outbound) _trusted.ForgetOutgoing(item.Id);
            else _trusted.ForgetIncoming(item.Id);
            RefreshList();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message,
            "Unable to forget trusted device", MessageBoxButtons.OK,
            MessageBoxIcon.Warning); }
    }
}
