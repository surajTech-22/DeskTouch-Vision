using System.Drawing;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>Single-step secure connection: paste one pairing code copied on the other PC.</summary>
internal sealed class PairComputerDialog : Form
{
    private readonly TextBox _code = new()
    {
        Multiline = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 11),
        PlaceholderText = "DT1|192.168.1.20|certificate fingerprint|pairing secret",
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _error = new() { Dock = DockStyle.Bottom, Height = 36,
        ForeColor = Color.FromArgb(173, 45, 50), AutoEllipsis = true };
    internal string Address { get; private set; } = "";
    internal string Fingerprint { get; private set; } = "";
    internal string Secret { get; private set; } = "";

    internal PairComputerDialog()
    {
        Text = "Add a Windows computer";
        BackColor = Color.FromArgb(244, 244, 246);
        ForeColor = Color.FromArgb(26, 26, 28);
        ClientSize = new Size(570, 315);
        MinimumSize = new Size(460, 290);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;

        var title = new Label { Dock = DockStyle.Top, Height = 62,
            Text = "Connect another computer",
            Font = new Font("Segoe UI Semibold", 17), Padding = new Padding(20, 13, 10, 6) };
        var description = new Label { Dock = DockStyle.Top, Height = 65,
            Text = "Open DeskTouch on the other PC and select “Copy pairing code”.\n" +
                   "Paste that code here. Only someone with the code can connect.",
            Padding = new Padding(20, 7, 14, 5) };

        var inputBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 7, 18, 10) };
        inputBox.Controls.Add(_code);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 7, 12, 7) };
        var connect = new Button { Text = "Connect", Width = 105, Height = 32,
            DialogResult = DialogResult.None, BackColor = Color.FromArgb(0, 94, 183),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 32,
            DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        connect.Click += (_, _) => TryAccept();
        buttons.Controls.Add(connect);
        buttons.Controls.Add(cancel);
        Controls.Add(inputBox);
        Controls.Add(_error);
        Controls.Add(buttons);
        Controls.Add(description);
        Controls.Add(title);
        AcceptButton = connect;
        CancelButton = cancel;
    }

    private void TryAccept()
    {
        var parts = _code.Text.Trim().Split('|');
        if (parts.Length != 4 || parts[0].Trim() != "DT1" ||
            !IPAddress.TryParse(parts[1].Trim(), out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            parts[2].Trim().Length != 64 || !parts[2].Trim().All(Uri.IsHexDigit) ||
            parts[3].Trim().Length != 32 || !parts[3].Trim().All(Uri.IsHexDigit))
        {
            _error.Text = "Invalid pairing code. Copy the complete code from the other Windows computer.";
            return;
        }
        Address = address.ToString();
        Fingerprint = parts[2].Trim().ToUpperInvariant();
        Secret = parts[3].Trim().ToUpperInvariant();
        DialogResult = DialogResult.OK;
        Close();
    }
}
