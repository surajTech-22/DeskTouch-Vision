using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using QRCoder;

namespace DeskTouch.DeviceShare;

/// <summary>
/// A QR scanner target for the Android APK inside the SAME DeskTouch GitHub release.
/// Never show a QR pointing at a nonexistent or unverified release asset.
/// Android uses its built-in camera to scan and its built-in APK installer.
/// </summary>
internal sealed class AndroidDownloadDialog : Form
{
    private const string LatestApi =
        "https://api.github.com/repos/surajTech-22/DeskTouch-Vision/releases/latest";
    private const string ReleasesWeb =
        "https://github.com/surajTech-22/DeskTouch-Vision/releases/latest";
    private const string AndroidAsset = "DeskTouchVision-android-preview.apk";
    private readonly Label _description = new();
    private readonly PictureBox _qr = new();
    private readonly LinkLabel _link = new();
    private readonly Button _copy = new();
    private string? _url;

    internal AndroidDownloadDialog()
    {
        Text = "DeskTouch Vision — Android download";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(31, 32, 36);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(480, 590);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        var title = new Label
        {
            Text = "Get DeskTouch Vision for Android",
            Font = new Font("Segoe UI Semibold", 18),
            Dock = DockStyle.Top, Height = 63,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _description.Text = "Checking the latest official Android release...";
        _description.Dock = DockStyle.Top;
        _description.Height = 80;
        _description.TextAlign = ContentAlignment.MiddleCenter;
        _description.ForeColor = Color.FromArgb(183, 199, 218);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.Dock = DockStyle.Top;
        _qr.Height = 320;
        _qr.BackColor = Color.White;
        _qr.Padding = new Padding(14);
        _qr.Visible = false;
        _link.Text = "View the DeskTouch GitHub releases";
        _link.LinkColor = Color.FromArgb(159, 201, 255);
        _link.ActiveLinkColor = Color.White;
        _link.Dock = DockStyle.Top;
        _link.Height = 48;
        _link.TextAlign = ContentAlignment.MiddleCenter;
        _link.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo(_url ?? ReleasesWeb) { UseShellExecute = true });
        _copy.Text = "Copy Android download link";
        _copy.Dock = DockStyle.Bottom;
        _copy.Height = 44;
        _copy.Enabled = false;
        _copy.Click += (_, _) =>
        {
            if (_url != null) Clipboard.SetText(_url);
        };
        Controls.Add(_copy);
        Controls.Add(_link);
        Controls.Add(_qr);
        Controls.Add(_description);
        Controls.Add(title);
        Shown += async (_, _) => await LoadAssetAsync();
        FormClosed += (_, _) => _qr.Image?.Dispose();
    }

    private async Task LoadAssetAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskTouchVision/0.3.1");
            using var response = await http.GetAsync(LatestApi);
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tag = data.RootElement.GetProperty("tag_name").GetString() ?? "";
            foreach (var asset in data.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AndroidAsset) continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                    parsed.Scheme != Uri.UriSchemeHttps ||
                    parsed.Host != "github.com") continue;
                if (IsDisposed) return;
                _url = parsed.AbsoluteUri;
                using var code = QRCodeGenerator.GenerateQrCode(_url, QRCodeGenerator.ECCLevel.Q);
                using var bytes = new PngByteQRCode(code);
                using var stream = new MemoryStream(bytes.GetGraphic(12));
                using var bitmap = new Bitmap(stream);
                _qr.Image = new Bitmap(bitmap);
                _qr.Visible = true;
                _description.Text = "Scan this QR using your Android Camera.\n" +
                    $"DeskTouch Android {tag} — Android installs the signed APK in place.";
                _copy.Enabled = true;
                _link.Text = "Open official Android APK on GitHub";
                return;
            }
            _description.Text = "An Android installable APK is not yet published for " +
                tag + ". The Windows release remains available.\n" +
                "The QR will appear automatically after an APK is attached.";
        }
        catch (Exception ex)
        {
            _description.Text = "Cannot verify the Android download right now.\n" +
                "No unverified APK or QR is displayed.\n" + ex.Message;
        }
    }
}
