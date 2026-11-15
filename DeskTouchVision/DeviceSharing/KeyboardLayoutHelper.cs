using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Inspects the active Windows physical keyboard layout and presents verified
/// Android external-keyboard shortcut bindings reported by the paired phone.
/// </summary>
internal static class KeyboardLayoutHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardLayoutNameW([Out] StringBuilder pwszKLID);

    internal static string DescribeCurrentWindowsLayout()
    {
        try
        {
            IntPtr hkl = GetKeyboardLayout(0);
            int langId = unchecked((short)((long)hkl & 0xFFFF));
            CultureInfo? culture = langId > 0 ? CultureInfo.GetCultureInfo(langId) : null;
            var klidBuf = new StringBuilder(16);
            string klid = GetKeyboardLayoutNameW(klidBuf) ? klidBuf.ToString() : "";
            string friendly = culture?.DisplayName ?? InputLanguage.CurrentInputLanguage?.LayoutName ?? "Standard Keyboard";
            return string.IsNullOrWhiteSpace(klid)
                ? friendly
                : $"{friendly} ({DescribeKlid(klid)})";
        }
        catch
        {
            return "Standard Windows Keyboard";
        }
    }

    internal static string DescribeKlid(string klid)
    {
        if (string.IsNullOrWhiteSpace(klid)) return "Standard";
        return klid.Trim().ToUpperInvariant() switch
        {
            "00000409" => "US QWERTY",
            "00020409" => "US International",
            "00000809" => "UK QWERTY",
            "00004009" => "English India",
            "00000407" => "German QWERTZ",
            "0000040C" => "French AZERTY",
            "00000411" => "Japanese",
            _ => "Layout " + klid.Trim()
        };
    }

    internal sealed record AndroidBindingItem(string Action, string Keys, string Status, bool Ready);

    internal sealed record AndroidKeyboardSummary(
        string Keyboard,
        bool External,
        bool FullUsb,
        string PcLayout,
        string Mode,
        IReadOnlyList<AndroidBindingItem> Bindings);

    internal static AndroidKeyboardSummary? TryParseWireSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string kb = ReadString(root, "keyboard", "Connected Keyboard");
            bool external = ReadBool(root, "external");
            bool fullUsb = ReadBool(root, "fullUsb");
            string pcLayout = ReadString(root, "pcLayout", DescribeCurrentWindowsLayout());
            string mode = ReadString(root, "mode", "");
            var list = new List<AndroidBindingItem>();
            if (root.TryGetProperty("bindings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string action = ReadString(item, "action", "");
                    string keys = ReadString(item, "keys", "");
                    string status = ReadString(item, "status", "");
                    bool ready = ReadBool(item, "ready");
                    if (!string.IsNullOrWhiteSpace(action) && !string.IsNullOrWhiteSpace(keys))
                        list.Add(new AndroidBindingItem(action, keys, status, ready));
                    if (list.Count >= 32) break;
                }
            }
            return new AndroidKeyboardSummary(kb, external, fullUsb, pcLayout, mode, list);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement obj, string prop, string fallback)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
            return fallback;
        string s = (el.GetString() ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length == 0 ? fallback : s.Length > 180 ? s[..180] : s;
    }

    private static bool ReadBool(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.True;
}

internal sealed class KeyboardShortcutsDialog : Form
{
    internal KeyboardShortcutsDialog(
        string? peerName,
        string windowsLayout,
        KeyboardLayoutHelper.AndroidKeyboardSummary? phoneSummary,
        string? liveLinkText)
    {
        Text = "Connected Keyboard & Shortcuts";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(31, 32, 36);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(520, 380);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(16, 12, 16, 4),
            ForeColor = Color.FromArgb(220, 232, 246),
            Text =
                $"Computer keyboard layout: {windowsLayout}\r\n" +
                $"Phone ({peerName ?? "Not connected"}): " +
                (phoneSummary != null
                    ? $"{phoneSummary.Keyboard}  •  {phoneSummary.Mode}"
                    : "Connect a phone to read its active keyboard bindings.") +
                (string.IsNullOrWhiteSpace(liveLinkText) ? "" : $"\r\nLive link: {liveLinkText}")
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(41, 43, 49),
            ForeColor = Color.WhiteSmoke
        };
        list.Columns.Add("Shortcut action", 180);
        list.Columns.Add("Bound keys", 215);
        list.Columns.Add("Status on current connection", 210);

        if (phoneSummary != null && phoneSummary.Bindings.Count > 0)
        {
            foreach (var b in phoneSummary.Bindings)
            {
                var row = new ListViewItem(b.Action);
                row.SubItems.Add(b.Keys);
                row.SubItems.Add(b.Status);
                row.ForeColor = b.Ready
                    ? Color.FromArgb(182, 235, 198)
                    : Color.FromArgb(245, 206, 138);
                list.Items.Add(row);
            }
        }
        else
        {
            var note = new ListViewItem("Waiting for phone connection");
            note.SubItems.Add("—");
            note.SubItems.Add("Shortcut bindings update live once connected");
            list.Items.Add(note);
        }

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(16, 8, 16, 10) };
        var noteLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(176, 182, 192),
            Text = "Windows keeps Windows + L (Lock) and Control + Alt + Delete reserved on the computer."
        };
        var closeBtn = new Button
        {
            Text = "Close",
            Dock = DockStyle.Right,
            Width = 96,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(56, 58, 65),
            ForeColor = Color.White
        };
        closeBtn.Click += (_, _) => Close();
        footer.Controls.Add(noteLabel);
        footer.Controls.Add(closeBtn);

        Controls.Add(list);
        Controls.Add(footer);
        Controls.Add(header);
    }
}
