using System.Drawing;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal sealed record SharedShelfEntry(
    string Id,
    string Kind,
    string Title,
    string Preview,
    string PayloadText,
    string FilePath,
    long ByteSize,
    string SourceDevice,
    long TimestampMs);

/// <summary>
/// Bounded recent-history shelf for shared clipboard items, one-time codes, and
/// transferred files so users can re-copy or open recent transfers cleanly.
/// </summary>
internal sealed class SharedShelfStore
{
    private const int MaxEntries = 25;
    private readonly object _gate = new();
    private readonly string _storagePath;
    private readonly List<SharedShelfEntry> _entries = new();

    internal event Action? Changed;

    internal SharedShelfStore(string? storagePath = null)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskTouchVision", "shared-shelf.json")
            : storagePath;
        Load();
    }

    internal IReadOnlyList<SharedShelfEntry> ListEntries(string? kindFilter = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(kindFilter) || kindFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
                return _entries.ToArray();
            return _entries.Where(e => string.Equals(e.Kind, kindFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    internal void AddClipboardText(string text, string sourceDevice)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string trimmed = text.Trim();
        string preview = SingleLinePreview(trimmed, 120);
        AddOrPromote(new SharedShelfEntry(
            Guid.NewGuid().ToString("N")[..10],
            "clip",
            "Shared Clipboard Text",
            preview,
            trimmed.Length > 2800 ? trimmed[..2800] : trimmed,
            "",
            trimmed.Length,
            CleanSource(sourceDevice),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            dedupKey: "clip|" + trimmed);
    }

    internal void AddClipboardImage(int byteCount, string sourceDevice)
    {
        AddOrPromote(new SharedShelfEntry(
            Guid.NewGuid().ToString("N")[..10],
            "clip",
            "Shared Clipboard Image",
            $"PNG screenshot ({Math.Max(1, byteCount / 1024)} KB)",
            "",
            "",
            byteCount,
            CleanSource(sourceDevice),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            dedupKey: null);
    }

    internal void AddOtp(string app, string code, string title, string sourceDevice)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        string cleanApp = string.IsNullOrWhiteSpace(app) ? "Verification" : app.Trim();
        string preview = string.IsNullOrWhiteSpace(title)
            ? $"Code {code}"
            : $"{title.Trim()} • {code}";
        AddOrPromote(new SharedShelfEntry(
            Guid.NewGuid().ToString("N")[..10],
            "otp",
            $"Code {code} ({cleanApp})",
            SingleLinePreview(preview, 110),
            code.Trim(),
            "",
            code.Length,
            CleanSource(sourceDevice),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            dedupKey: "otp|" + code.Trim());
    }

    internal void AddFile(string filePath, string directionLabel, string sourceDevice)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        string name = Path.GetFileName(filePath);
        long size = 0;
        try { if (File.Exists(filePath)) size = new FileInfo(filePath).Length; } catch { }
        string sizeText = size >= 1024 * 1024
            ? $"{size / (1024.0 * 1024.0):F1} MB"
            : $"{Math.Max(1, size / 1024)} KB";
        AddOrPromote(new SharedShelfEntry(
            Guid.NewGuid().ToString("N")[..10],
            "file",
            name,
            $"{directionLabel} • {sizeText}",
            filePath,
            filePath,
            size,
            CleanSource(sourceDevice),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            dedupKey: "file|" + filePath + "|" + directionLabel);
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private void AddOrPromote(SharedShelfEntry entry, string? dedupKey)
    {
        lock (_gate)
        {
            if (dedupKey != null)
            {
                _entries.RemoveAll(e =>
                    (e.Kind == "clip" && ("clip|" + e.PayloadText) == dedupKey) ||
                    (e.Kind == "otp" && ("otp|" + e.PayloadText) == dedupKey) ||
                    (e.Kind == "file" && ("file|" + e.FilePath + "|" + (e.Preview.Split('•')[0].Trim())) == dedupKey));
            }
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;
            var loaded = JsonSerializer.Deserialize<List<SharedShelfEntry>>(File.ReadAllText(_storagePath));
            if (loaded == null) return;
            lock (_gate)
            {
                _entries.Clear();
                _entries.AddRange(loaded.Take(MaxEntries));
            }
        }
        catch { }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(_entries));
        }
        catch { }
    }

    private static string CleanSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? Environment.MachineName : source.Trim();

    private static string SingleLinePreview(string text, int maxLen)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= maxLen ? flat : flat[..(maxLen - 1)] + "…";
    }
}

internal sealed class SharedShelfDialog : Form
{
    private readonly SharedShelfStore _store;
    private readonly ListBox _list = new();
    private readonly Label _detail = new();
    private readonly Button _copyBtn = new();
    private readonly Button _openBtn = new();
    private readonly Button _folderBtn = new();
    private string _filter = "all";
    private List<SharedShelfEntry> _visible = new();

    internal SharedShelfDialog(SharedShelfStore store)
    {
        _store = store;
        Text = "DeskTouch Vision — Recent Shared Shelf";
        Size = new Size(680, 500);
        MinimumSize = new Size(540, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(17, 23, 34);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(14, 8, 14, 6),
            BackColor = Color.FromArgb(22, 31, 48),
            WrapContents = false
        };
        foreach (var (lbl, key) in new[] { ("All", "all"), ("Clips", "clip"), ("Codes", "otp"), ("Files", "file") })
        {
            var tab = MakeButton(lbl, key == "all");
            tab.Click += (_, _) =>
            {
                _filter = key;
                foreach (Control c in header.Controls)
                {
                    if (c is Button btn)
                    {
                        bool active = ReferenceEquals(btn, tab);
                        btn.BackColor = active ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59);
                    }
                }
                Reload();
            };
            header.Controls.Add(tab);
        }
        var clearBtn = MakeButton("Clear History", false);
        clearBtn.Margin = new Padding(18, 0, 0, 0);
        clearBtn.Click += (_, _) =>
        {
            _store.Clear();
            Reload();
        };
        header.Controls.Add(clearBtn);

        _list.Dock = DockStyle.Fill;
        _list.BackColor = Color.FromArgb(15, 22, 34);
        _list.ForeColor = Color.White;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.ItemHeight = 24;
        _list.SelectedIndexChanged += (_, _) => UpdateSelection();
        _list.DoubleClick += (_, _) => CopyOrOpenPrimary();

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 98,
            Padding = new Padding(14, 8, 14, 10),
            BackColor = Color.FromArgb(22, 31, 48)
        };
        _detail.Dock = DockStyle.Fill;
        _detail.ForeColor = Color.FromArgb(186, 200, 220);
        _detail.AutoEllipsis = true;
        _detail.Text = "Select an item to re-copy text or open a shared file.";

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        ConfigureActionButton(_copyBtn, "Copy to Clipboard", true);
        ConfigureActionButton(_openBtn, "Open File", false);
        ConfigureActionButton(_folderBtn, "Open Folder", false);
        _copyBtn.Click += (_, _) => CopySelected();
        _openBtn.Click += (_, _) => OpenSelectedFile();
        _folderBtn.Click += (_, _) => OpenSelectedFolder();
        btnRow.Controls.AddRange(new Control[] { _copyBtn, _openBtn, _folderBtn });

        footer.Controls.Add(_detail);
        footer.Controls.Add(btnRow);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8) };
        body.Controls.Add(_list);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        _store.Changed += OnStoreChanged;
        FormClosed += (_, _) => _store.Changed -= OnStoreChanged;
        Reload();
    }

    private void OnStoreChanged()
    {
        if (!IsDisposed && IsHandleCreated)
            try { BeginInvoke(Reload); } catch { }
    }

    private void Reload()
    {
        _visible = _store.ListEntries(_filter).ToList();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var item in _visible)
            {
                string tag = item.Kind switch
                {
                    "otp" => "[CODE]",
                    "file" => "[FILE]",
                    _ => "[CLIP]"
                };
                var when = DateTimeOffset.FromUnixTimeMilliseconds(item.TimestampMs).LocalDateTime;
                _list.Items.Add($"{tag} {item.Title}  —  {item.Preview}  ({item.SourceDevice}, {when:t})");
            }
            if (_list.Items.Count > 0 && _list.SelectedIndex < 0)
                _list.SelectedIndex = 0;
        }
        finally { _list.EndUpdate(); }
        UpdateSelection();
    }

    private SharedShelfEntry? SelectedEntry() =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _visible.Count
            ? _visible[_list.SelectedIndex]
            : null;

    private void UpdateSelection()
    {
        var entry = SelectedEntry();
        if (entry == null)
        {
            _detail.Text = "No recent items match this filter yet.";
            _copyBtn.Enabled = false;
            _openBtn.Enabled = false;
            _folderBtn.Enabled = false;
            return;
        }
        _detail.Text = entry.Kind == "file"
            ? $"{entry.Title}\n{entry.FilePath}"
            : $"{entry.Title} ({entry.SourceDevice})\n{entry.Preview}";
        _copyBtn.Enabled = !string.IsNullOrEmpty(entry.PayloadText);
        bool hasFile = entry.Kind == "file" && !string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath);
        _openBtn.Enabled = hasFile;
        _folderBtn.Enabled = hasFile;
    }

    private void CopyOrOpenPrimary()
    {
        var entry = SelectedEntry();
        if (entry == null) return;
        if (entry.Kind == "file") OpenSelectedFile();
        else CopySelected();
    }

    private void CopySelected()
    {
        var entry = SelectedEntry();
        if (entry == null || string.IsNullOrEmpty(entry.PayloadText)) return;
        try
        {
            Clipboard.SetText(entry.PayloadText);
            _detail.Text = "Copied cleanly to your Windows clipboard.";
        }
        catch { }
    }

    private void OpenSelectedFile()
    {
        var entry = SelectedEntry();
        if (entry == null || string.IsNullOrEmpty(entry.FilePath) || !File.Exists(entry.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true }); } catch { }
    }

    private void OpenSelectedFolder()
    {
        var entry = SelectedEntry();
        if (entry == null || string.IsNullOrEmpty(entry.FilePath) || !File.Exists(entry.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button();
        ConfigureActionButton(b, text, primary);
        return b;
    }

    private static void ConfigureActionButton(Button b, string text, bool primary)
    {
        b.Text = text;
        b.AutoSize = true;
        b.Height = 29;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105);
        b.FlatAppearance.BorderSize = 1;
        b.BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59);
        b.ForeColor = Color.White;
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(0, 0, 8, 0);
    }
}
