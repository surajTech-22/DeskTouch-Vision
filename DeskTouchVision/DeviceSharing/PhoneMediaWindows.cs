using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal static class PhoneCanvasMath
{
    internal static (int NormX, int NormY) MapCanvasTouchToNormalized(
        int touchX, int touchY, int canvasW, int canvasH, int frameW, int frameH)
    {
        if (canvasW <= 1 || canvasH <= 1) return (5000, 5000);
        if (frameW <= 0 || frameH <= 0)
        {
            int nx = (int)Math.Round(Math.Clamp(touchX / (double)(canvasW - 1), 0.0, 1.0) * 10000.0);
            int ny = (int)Math.Round(Math.Clamp(touchY / (double)(canvasH - 1), 0.0, 1.0) * 10000.0);
            return (nx, ny);
        }

        double scale = Math.Min(canvasW / (double)frameW, canvasH / (double)frameH);
        double drawnW = frameW * scale;
        double drawnH = frameH * scale;
        double offsetX = (canvasW - drawnW) * 0.5;
        double offsetY = (canvasH - drawnH) * 0.5;
        double u = Math.Clamp((touchX - offsetX) / Math.Max(1.0, drawnW), 0.0, 1.0);
        double v = Math.Clamp((touchY - offsetY) / Math.Max(1.0, drawnH), 0.0, 1.0);
        return ((int)Math.Round(u * 10000.0), (int)Math.Round(v * 10000.0));
    }
}

/// <summary>
/// Interactive resizable window on Windows mirroring the connected Android phone's
/// live screen and forwarding mouse taps/swipes, Back/Home/Recents, and typing.
/// </summary>
internal sealed class PhoneScreenForm : Form
{
    private readonly int _peerId;
    private readonly Action<int, bool> _watchScreen;
    private readonly Action<int, int, int, int> _sendTouch;
    private readonly Action<int, int> _sendNav;
    private readonly Action<int, string> _sendText;

    private readonly Panel _canvas = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _quickType = new();
    private readonly Button _backBtn = MakeBarBtn("◁ Back");
    private readonly Button _homeBtn = MakeBarBtn("○ Home");
    private readonly Button _recentsBtn = MakeBarBtn("▢ Recents");
    private Bitmap? _frame;
    private readonly object _frameLock = new();
    private bool _mouseDragging;
    private int _frameCount;
    private long _fpsWindowStartMs = Environment.TickCount64;

    internal PhoneScreenForm(
        int peerId,
        string deviceName,
        Action<int, bool> watchScreen,
        Action<int, int, int, int> sendTouch,
        Action<int, int> sendNav,
        Action<int, string> sendText)
    {
        _peerId = peerId;
        _watchScreen = watchScreen;
        _sendTouch = sendTouch;
        _sendNav = sendNav;
        _sendText = sendText;

        Text = $"DeskTouch Vision — Phone Screen ({deviceName})";
        Size = new Size(430, 820);
        MinimumSize = new Size(340, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 18, 30);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(20, 29, 46),
            Padding = new Padding(10, 7, 10, 5),
            WrapContents = false
        };
        _backBtn.Click += (_, _) => _sendNav(_peerId, 1);
        _homeBtn.Click += (_, _) => _sendNav(_peerId, 2);
        _recentsBtn.Click += (_, _) => _sendNav(_peerId, 3);
        var wakeBtn = MakeBarBtn("Wake");
        wakeBtn.Click += (_, _) => _sendNav(_peerId, 4);

        _statusLabel.Text = "Starting phone screen stream…";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(125, 211, 252);
        _statusLabel.Margin = new Padding(8, 5, 0, 0);

        topBar.Controls.AddRange(new Control[] { _backBtn, _homeBtn, _recentsBtn, wakeBtn, _statusLabel });

        var typeBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Color.FromArgb(20, 29, 46),
            Padding = new Padding(10, 7, 10, 7)
        };
        _quickType.Dock = DockStyle.Fill;
        _quickType.BackColor = Color.FromArgb(12, 18, 30);
        _quickType.ForeColor = Color.White;
        _quickType.BorderStyle = BorderStyle.FixedSingle;
        _quickType.PlaceholderText = "Type text to send to phone (Enter sends)…";
        _quickType.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(_quickType.Text))
            {
                e.SuppressKeyPress = true;
                _sendText(_peerId, _quickType.Text);
                _quickType.Text = "";
            }
        };
        var sendTypeBtn = MakeBarBtn("Send", primary: true);
        sendTypeBtn.Dock = DockStyle.Right;
        sendTypeBtn.Width = 68;
        sendTypeBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_quickType.Text))
            {
                _sendText(_peerId, _quickType.Text);
                _quickType.Text = "";
            }
        };
        typeBar.Controls.Add(_quickType);
        typeBar.Controls.Add(sendTypeBtn);

        _canvas.Dock = DockStyle.Fill;
        _canvas.BackColor = Color.FromArgb(8, 12, 20);
        _canvas.Cursor = Cursors.Hand;
        EnableDoubleBuffering(_canvas);
        _canvas.Paint += PaintScreenCanvas;
        _canvas.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _canvas.Focus();
            _mouseDragging = true;
            var (nx, ny) = MapPointer(e.X, e.Y);
            _sendTouch(_peerId, 0, nx, ny);
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (!_mouseDragging) return;
            var (nx, ny) = MapPointer(e.X, e.Y);
            _sendTouch(_peerId, 1, nx, ny);
        };
        _canvas.MouseUp += (_, e) =>
        {
            if (!_mouseDragging) return;
            _mouseDragging = false;
            var (nx, ny) = MapPointer(e.X, e.Y);
            _sendTouch(_peerId, 2, nx, ny);
        };

        Controls.Add(_canvas);
        Controls.Add(typeBar);
        Controls.Add(topBar);

        Shown += (_, _) => _watchScreen(_peerId, true);
        FormClosed += (_, _) =>
        {
            _watchScreen(_peerId, false);
            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = null;
            }
        };
    }

    internal int PeerId => _peerId;

    internal void UpdateFrame(byte[] jpegBytes)
    {
        if (IsDisposed || jpegBytes.Length == 0) return;
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            using var decoded = new Bitmap(ms);
            var copy = new Bitmap(decoded);
            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = copy;
            }
            _frameCount++;
            long now = Environment.TickCount64;
            if (now - _fpsWindowStartMs >= 1000)
            {
                double fps = _frameCount * 1000.0 / Math.Max(1, now - _fpsWindowStartMs);
                _frameCount = 0;
                _fpsWindowStartMs = now;
                _statusLabel.Text = $"Live • {fps:F0} FPS";
            }
            else if (_statusLabel.Text.StartsWith("Starting", StringComparison.Ordinal) ||
                     _statusLabel.Text.StartsWith("Tap ", StringComparison.Ordinal))
            {
                _statusLabel.Text = "Live";
            }
            _canvas.Invalidate();
        }
        catch { }
    }

    internal void UpdateStreamStatus(int stateCode, string? message)
    {
        if (IsDisposed) return;
        _statusLabel.Text = stateCode switch
        {
            2 => "Tap 'Share Phone Screen' on phone",
            3 => "Paused for payment privacy",
            1 => string.IsNullOrWhiteSpace(message) ? "Live" : message,
            _ => string.IsNullOrWhiteSpace(message) ? "Idle" : message
        };
    }

    private (int NormX, int NormY) MapPointer(int x, int y)
    {
        int fw = 0, fh = 0;
        lock (_frameLock)
        {
            if (_frame != null) { fw = _frame.Width; fh = _frame.Height; }
        }
        return PhoneCanvasMath.MapCanvasTouchToNormalized(x, y, _canvas.Width, _canvas.Height, fw, fh);
    }

    private void PaintScreenCanvas(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(8, 12, 20));
        Bitmap? bmpSnapshot = null;
        lock (_frameLock)
        {
            if (_frame != null) bmpSnapshot = new Bitmap(_frame);
        }
        if (bmpSnapshot == null)
        {
            using var f = new Font("Segoe UI", 10.5f);
            TextRenderer.DrawText(
                e.Graphics,
                "Waiting for phone screen frames…\n\nOn Android, tap 'Share Phone Screen' once if prompted.\nBack / Home / Recents buttons work anytime.",
                f,
                _canvas.ClientRectangle,
                Color.FromArgb(148, 163, 184),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        using (bmpSnapshot)
        {
            double scale = Math.Min(
                _canvas.Width / (double)bmpSnapshot.Width,
                _canvas.Height / (double)bmpSnapshot.Height);
            int w = Math.Max(1, (int)Math.Round(bmpSnapshot.Width * scale));
            int h = Math.Max(1, (int)Math.Round(bmpSnapshot.Height * scale));
            int x = (_canvas.Width - w) / 2;
            int y = (_canvas.Height - h) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(bmpSnapshot, new Rectangle(x, y, w, h));
        }
    }

    private static Button MakeBarBtn(string text, bool primary = false) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 28,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = primary ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105), BorderSize = 1 },
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 6, 0)
    };

    private static void EnableDoubleBuffering(Control c)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(c, true);
        }
        catch { }
    }
}

/// <summary>
/// Loopback-only MJPEG HTTP server (127.0.0.1) so OBS, browsers, or video tools can
/// consume DeskTouch's wireless phone camera stream without custom kernel drivers.
/// </summary>
internal sealed class LocalMjpegServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<int, NetworkStream> _clients = new();
    private int _nextClientId;
    private volatile bool _disposed;

    internal int Port { get; private set; }
    internal string StreamUrl => Port > 0 ? $"http://127.0.0.1:{Port}/video.mjpeg" : "";

    internal void Start()
    {
        if (Port > 0) return;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    internal void PushJpegFrame(byte[] jpeg)
    {
        if (_disposed || jpeg.Length == 0 || _clients.IsEmpty) return;
        byte[] header = Encoding.ASCII.GetBytes(
            $"--desktouchframe\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
        byte[] tail = "\r\n"u8.ToArray();
        foreach (var kv in _clients)
        {
            try
            {
                kv.Value.Write(header);
                kv.Value.Write(jpeg);
                kv.Value.Write(tail);
                kv.Value.Flush();
            }
            catch
            {
                if (_clients.TryRemove(kv.Key, out var dead))
                    try { dead.Dispose(); } catch { }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                var stream = client.GetStream();
                byte[] buf = new byte[1024];
                _ = await stream.ReadAsync(buf);
                byte[] resp = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nCache-Control: no-cache\r\nPragma: no-cache\r\nConnection: close\r\n" +
                    "Content-Type: multipart/x-mixed-replace; boundary=desktouchframe\r\n\r\n");
                await stream.WriteAsync(resp);
                await stream.FlushAsync();
                int id = Interlocked.Increment(ref _nextClientId);
                _clients[id] = stream;
            }
            catch when (_disposed) { break; }
            catch { await Task.Delay(50); }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _listener.Stop(); } catch { }
        foreach (var kv in _clients)
            try { kv.Value.Dispose(); } catch { }
        _clients.Clear();
    }
}

internal readonly record struct MediaSourceOption(
    int Id,
    string Name,
    string Platform,
    bool SupportsTorch,
    bool ViaHost = false,
    bool HasCamera = true,
    bool HasMicrophone = true)
{
    public override string ToString() => $"{Name} ({Platform})";
}

/// <summary>
/// Compatibility wrapper backed by pinned unmanaged WAVEHDR ring buffers in ManagedAudioOutputEngine.
/// </summary>
internal sealed class WaveOutAudioPlayer : IDisposable
{
    private readonly ManagedAudioOutputEngine _engine;

    internal WaveOutAudioPlayer(uint deviceId = 0xFFFFFFFF) =>
        _engine = new ManagedAudioOutputEngine(deviceId);

    internal void PlayPcm16Mono(byte[] pcm, int sampleRate = 16000) =>
        _engine.EnqueuePcm16Mono(pcm, sampleRate);

    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// Live multi-system &amp; phone Camera + Microphone viewer on Windows with Clean Window Mode,
/// loopback MJPEG stream for OBS/browsers, Front/Back flip, Torch, Rotate, Fast UDP status,
/// and direct native Windows microphone routing via VB-Cable / Virtual Audio Cable.
/// </summary>
internal sealed class PhoneWebcamForm : Form
{
    private const uint WaveMapperId = 0xFFFFFFFF;

    private readonly Action<MediaSourceOption, bool, int, bool, bool> _watchCamera;
    private readonly Action<MediaSourceOption, bool> _watchMic;
    private readonly Func<MediaSourceOption, bool> _isFastUdpActive;
    private readonly Func<MediaSourceOption, string> _getTransportLabel;
    private readonly RemoteMicBridgeController _sharedMic;
    private readonly Action? _onMediaStateChanged;

    private readonly FlowLayoutPanel _toolbar = new();
    private readonly FlowLayoutPanel _micBar = new();
    private readonly Panel _canvas = new();
    private readonly ComboBox _sourceCombo = new();
    private readonly Button _camBtn = MakeBtn("Camera: On", true);
    private readonly Button _micBtn = MakeBtn("Mic: On", true);
    private readonly Button _lensBtn = MakeBtn("Lens: Back");
    private readonly Button _torchBtn = MakeBtn("Torch: Off");
    private readonly Label _transportBadge = new();
    private readonly Label _statusLabel = new();

    private readonly Label _virtMicLabel = new();
    private readonly CheckBox _autoDefaultMicCheck = new();
    private readonly Button _installDriverBtn = MakeBtn("Install Native Mic Driver", true);
    private readonly Button _rescanAudioBtn = MakeBtn("Re-scan");
    private readonly CheckBox _hearMonitorCheck = new();
    private readonly ComboBox _outputCombo = new();
    private readonly TrackBar _gainTrack = new();
    private readonly Panel _vuMeter = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly LocalMjpegServer _mjpeg = new();

    private IReadOnlyList<WaveOutDeviceInfo> _waveOutDevices = Array.Empty<WaveOutDeviceInfo>();
    private VirtualMicMatch _virtualMic;
    private MediaSourceOption _currentSource;
    private bool _suppressEvents;
    private volatile bool _camOn = true;
    private volatile bool _micOn = true;
    private int _lens;
    private bool _torch;
    private int _rotateDeg;
    private bool _cleanView;
    private int _fpsCount;
    private long _fpsStartMs = Environment.TickCount64;
    private float _vuLevel;
    private int _timerTick;
    private string _micStateNote = "";
    private string _lastCameraStatusText = "";
    private bool _installingDriver;

    private Bitmap? _frame;
    private readonly object _frameLock = new();

    internal PhoneWebcamForm(
        IReadOnlyList<MediaSourceOption> sources,
        MediaSourceOption initialSource,
        bool initialCameraOn,
        bool initialMicOn,
        RemoteMicBridgeController sharedMic,
        Action<MediaSourceOption, bool, int, bool, bool> watchCamera,
        Action<MediaSourceOption, bool> watchMic,
        Func<MediaSourceOption, bool> isFastUdpActive,
        Func<MediaSourceOption, string>? getTransportLabel = null,
        Action? onMediaStateChanged = null)
    {
        _sharedMic = sharedMic;
        _watchCamera = watchCamera;
        _watchMic = watchMic;
        _isFastUdpActive = isFastUdpActive;
        _getTransportLabel = getTransportLabel ?? (src => isFastUdpActive(src) ? "Fast UDP" : "TCP Fallback");
        _onMediaStateChanged = onMediaStateChanged;
        _currentSource = initialSource;
        _camOn = initialSource.HasCamera && initialCameraOn;
        _micOn = initialSource.HasMicrophone && (initialMicOn || _sharedMic.IsActiveFor(initialSource.Id, initialSource.ViaHost));

        Text = $"DeskTouch Vision — Camera & Mic ({initialSource.Name})";
        Size = new Size(920, 620);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 18, 30);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;

        BuildTopToolbar(sources, initialSource);
        BuildNativeMicBar();

        _canvas.Dock = DockStyle.Fill;
        _canvas.BackColor = Color.Black;
        EnableDoubleBuffering(_canvas);
        _canvas.Paint += PaintVideoCanvas;
        _canvas.DoubleClick += (_, _) => ToggleCleanView();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _cleanView) ToggleCleanView();
        };

        Controls.Add(_canvas);
        Controls.Add(_micBar);
        Controls.Add(_toolbar);

        _uiTimer.Interval = 50;
        _uiTimer.Tick += OnUiTimerTick;

        Shown += (_, _) =>
        {
            _mjpeg.Start();
            if (_micOn && !_sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost))
            {
                _sharedMic.SetActiveSource(_currentSource.Id, _currentSource.ViaHost, _currentSource.Name,
                    backgroundMode: false, autoSwitchDefault: true);
            }
            RefreshAudioDevicesAndRouting(initialAutoSwitch: _micOn && !_sharedMic.IsBackgroundMode);
            UpdateTransportBadge();
            StartWatchingCurrentSource();
            _onMediaStateChanged?.Invoke();
            _uiTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _camOn = false;
            bool keepMicInBackground = _sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost) &&
                                       _sharedMic.IsBackgroundMode;
            _micOn = keepMicInBackground;
            try
            {
                _watchCamera(_currentSource, false, _lens, false, keepMicInBackground);
                if (!keepMicInBackground)
                    _watchMic(_currentSource, false);
            }
            catch { }
            if (!keepMicInBackground && _sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost))
                _sharedMic.Stop(restoreDefaultMic: true);
            _onMediaStateChanged?.Invoke();
            _mjpeg.Dispose();
            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = null;
            }
        };
    }

    internal int PeerId => _currentSource.Id;
    internal bool SourceViaHost => _currentSource.ViaHost;
    internal bool IsCameraStreaming => !IsDisposed && _camOn;
    internal bool IsMicStreaming => !IsDisposed && _micOn;

    private void BuildTopToolbar(IReadOnlyList<MediaSourceOption> sources, MediaSourceOption initialSource)
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.AutoSize = true;
        _toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _toolbar.WrapContents = true;
        _toolbar.BackColor = Color.FromArgb(20, 29, 46);
        _toolbar.Padding = new Padding(10, 8, 10, 6);

        _sourceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _sourceCombo.Width = 200;
        _sourceCombo.BackColor = Color.FromArgb(30, 41, 59);
        _sourceCombo.ForeColor = Color.White;
        _sourceCombo.Margin = new Padding(0, 2, 8, 2);
        PopulateSourceCombo(sources, initialSource.Id, initialSource.ViaHost);
        _sourceCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_sourceCombo.SelectedItem is MediaSourceOption next &&
                (next.Id != _currentSource.Id || next.ViaHost != _currentSource.ViaHost))
                SwitchSource(next);
        };

        _camBtn.Click += (_, _) =>
        {
            if (!_currentSource.HasCamera) return;
            _camOn = !_camOn;
            UpdateMediaToggleButtons();
            if (!_camOn)
            {
                lock (_frameLock)
                {
                    _frame?.Dispose();
                    _frame = null;
                }
                _canvas.Invalidate();
            }
            SyncWatchState();
            _onMediaStateChanged?.Invoke();
        };

        _micBtn.Click += (_, _) =>
        {
            if (!_currentSource.HasMicrophone) return;
            _micOn = !_micOn;
            UpdateMediaToggleButtons();
            if (!_micOn)
            {
                if (_sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost))
                    _sharedMic.Stop(restoreDefaultMic: true);
            }
            else
            {
                _sharedMic.SetActiveSource(_currentSource.Id, _currentSource.ViaHost, _currentSource.Name,
                    backgroundMode: false, autoSwitchDefault: _autoDefaultMicCheck.Checked);
                RefreshAudioDevicesAndRouting(initialAutoSwitch: false);
            }
            SyncWatchState(micOnlyChange: true);
            _onMediaStateChanged?.Invoke();
        };

        _lensBtn.Click += (_, _) =>
        {
            _lens = _lens == 0 ? 1 : 0;
            UpdateSourceCapabilityButtons();
            if (_lens == 1 && _currentSource.Platform == "Android")
                _torch = false;
            UpdateSourceCapabilityButtons();
            SyncWatchState();
        };

        _torchBtn.Click += (_, _) =>
        {
            if (!_currentSource.SupportsTorch) return;
            if (_lens == 1) _lens = 0;
            _torch = !_torch;
            UpdateSourceCapabilityButtons();
            SyncWatchState();
        };

        var rotBtn = MakeBtn("Rotate 90°");
        rotBtn.Click += (_, _) => { _rotateDeg = (_rotateDeg + 90) % 360; _canvas.Invalidate(); };

        var snapBtn = MakeBtn("Snapshot");
        snapBtn.Click += (_, _) => CopySnapshot();

        var urlBtn = MakeBtn("Copy Stream URL", true);
        urlBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_mjpeg.StreamUrl))
            {
                Clipboard.SetText(_mjpeg.StreamUrl);
                _statusLabel.Text = $"Copied {_mjpeg.StreamUrl}";
            }
        };

        var cleanBtn = MakeBtn("Clean View");
        cleanBtn.Click += (_, _) => ToggleCleanView();

        _transportBadge.Text = "TCP";
        _transportBadge.AutoSize = true;
        _transportBadge.Padding = new Padding(6, 3, 6, 3);
        _transportBadge.Margin = new Padding(2, 3, 6, 2);
        _transportBadge.BackColor = Color.FromArgb(120, 53, 15);
        _transportBadge.ForeColor = Color.FromArgb(253, 230, 138);
        _transportBadge.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        _statusLabel.Text = "Connecting media stream…";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(125, 211, 252);
        _statusLabel.Margin = new Padding(4, 6, 0, 2);

        UpdateSourceCapabilityButtons();
        UpdateMediaToggleButtons();

        _toolbar.Controls.AddRange(new Control[]
        {
            _sourceCombo, _camBtn, _micBtn, _lensBtn, _torchBtn, rotBtn,
            snapBtn, urlBtn, cleanBtn, _transportBadge, _statusLabel
        });
    }

    private void BuildNativeMicBar()
    {
        _micBar.Dock = DockStyle.Top;
        _micBar.AutoSize = true;
        _micBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _micBar.WrapContents = true;
        _micBar.BackColor = Color.FromArgb(15, 23, 42);
        _micBar.Padding = new Padding(10, 6, 10, 6);

        _virtMicLabel.Text = "Checking virtual mic driver…";
        _virtMicLabel.AutoSize = true;
        _virtMicLabel.Margin = new Padding(0, 6, 8, 2);
        _virtMicLabel.ForeColor = Color.FromArgb(148, 163, 184);

        _autoDefaultMicCheck.Text = "Default Windows Mic";
        _autoDefaultMicCheck.AutoSize = true;
        _autoDefaultMicCheck.ForeColor = Color.White;
        _autoDefaultMicCheck.Margin = new Padding(0, 5, 8, 2);
        _autoDefaultMicCheck.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_autoDefaultMicCheck.Checked && _virtualMic.Installed)
            {
                if (_sharedMic.SetDefaultWindowsMic(true))
                    UpdateStatus($"Windows default mic set to {_virtualMic.CaptureName}");
                else
                    UpdateStatus("Could not set default Windows mic automatically");
            }
            else if (!_autoDefaultMicCheck.Checked)
            {
                _sharedMic.SetDefaultWindowsMic(false);
                UpdateStatus("Restored previous default Windows mic");
            }
        };

        _installDriverBtn.Click += async (_, _) => await InstallVbCableFromUiAsync();
        _rescanAudioBtn.Click += (_, _) => RefreshAudioDevicesAndRouting(initialAutoSwitch: _autoDefaultMicCheck.Checked);

        _hearMonitorCheck.Text = "Hear Monitor";
        _hearMonitorCheck.AutoSize = true;
        _hearMonitorCheck.ForeColor = Color.White;
        _hearMonitorCheck.Margin = new Padding(4, 5, 6, 2);
        _hearMonitorCheck.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _sharedMic.SetHearMonitor(_hearMonitorCheck.Checked);
        };

        _outputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _outputCombo.Width = 175;
        _outputCombo.BackColor = Color.FromArgb(30, 41, 59);
        _outputCombo.ForeColor = Color.White;
        _outputCombo.Margin = new Padding(0, 2, 8, 2);
        _outputCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            ApplySelectedOutputDevice();
        };

        var gainLabel = new Label
        {
            Text = "Gain:",
            AutoSize = true,
            ForeColor = Color.FromArgb(203, 213, 225),
            Margin = new Padding(0, 6, 2, 2)
        };

        _gainTrack.Minimum = 50;
        _gainTrack.Maximum = 200;
        _gainTrack.Value = 100;
        _gainTrack.TickStyle = TickStyle.None;
        _gainTrack.AutoSize = false;
        _gainTrack.Size = new Size(92, 24);
        _gainTrack.Margin = new Padding(0, 3, 8, 2);
        _gainTrack.ValueChanged += (_, _) =>
        {
            _sharedMic.Gain = _gainTrack.Value / 100.0f;
        };

        _vuMeter.Size = new Size(108, 18);
        _vuMeter.Margin = new Padding(0, 5, 0, 2);
        _vuMeter.BackColor = Color.FromArgb(30, 41, 59);
        EnableDoubleBuffering(_vuMeter);
        _vuMeter.Paint += PaintVuMeter;

        _micBar.Controls.AddRange(new Control[]
        {
            _virtMicLabel, _autoDefaultMicCheck, _installDriverBtn, _rescanAudioBtn,
            _hearMonitorCheck, _outputCombo, gainLabel, _gainTrack, _vuMeter
        });
    }

    internal void UpdateSources(IReadOnlyList<MediaSourceOption> sources)
    {
        if (IsDisposed || sources == null) return;
        PopulateSourceCombo(sources, _currentSource.Id, _currentSource.ViaHost);
    }

    internal void SyncExternalMicState()
    {
        if (IsDisposed) return;
        _micOn = _currentSource.HasMicrophone && _sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost);
        UpdateMediaToggleButtons();
        RefreshAudioDevicesAndRouting(initialAutoSwitch: false);
        _canvas.Invalidate();
    }

    internal void ApplyExternalSelection(MediaSourceOption source, bool toggleCamera, bool toggleMic)
    {
        if (IsDisposed) return;
        if (source.Id == _currentSource.Id && source.ViaHost == _currentSource.ViaHost)
        {
            _currentSource = source;
            if (toggleCamera && source.HasCamera)
            {
                _camOn = !_camOn;
                if (!_camOn)
                {
                    lock (_frameLock)
                    {
                        _frame?.Dispose();
                        _frame = null;
                    }
                    _canvas.Invalidate();
                }
                SyncWatchState(micOnlyChange: false);
            }
            if (toggleMic && source.HasMicrophone)
            {
                _micOn = !_micOn;
                if (!_micOn)
                {
                    if (_sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost))
                        _sharedMic.Stop(restoreDefaultMic: true);
                }
                else
                {
                    _sharedMic.SetActiveSource(_currentSource.Id, _currentSource.ViaHost, _currentSource.Name,
                        backgroundMode: false, autoSwitchDefault: true);
                    RefreshAudioDevicesAndRouting(initialAutoSwitch: false);
                }
                SyncWatchState(micOnlyChange: true);
            }
            UpdateSourceCapabilityButtons();
            UpdateMediaToggleButtons();
            _onMediaStateChanged?.Invoke();
            return;
        }

        SwitchSource(source,
            explicitCam: source.HasCamera && toggleCamera,
            explicitMic: source.HasMicrophone && (toggleMic || _sharedMic.IsActiveFor(source.Id, source.ViaHost)));
    }

    internal void UpdateCameraFrame(byte[] jpegBytes)
    {
        if (IsDisposed || jpegBytes.Length == 0 || !_camOn) return;
        _mjpeg.PushJpegFrame(jpegBytes);
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            using var decoded = new Bitmap(ms);
            var copy = new Bitmap(decoded);
            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = copy;
            }
            _fpsCount++;
            long now = Environment.TickCount64;
            if (now - _fpsStartMs >= 1000)
            {
                double fps = _fpsCount * 1000.0 / Math.Max(1, now - _fpsStartMs);
                _fpsCount = 0;
                _fpsStartMs = now;
                string micSuffix = string.IsNullOrEmpty(_micStateNote) ? "" : $" • {_micStateNote}";
                _statusLabel.Text = $"Live • {fps:F0} FPS • {_mjpeg.StreamUrl}{micSuffix}";
            }
            _canvas.Invalidate();
        }
        catch { }
    }

    internal void HandleMicAudio(byte[] pcm, int sampleRate, int audioSeq = 0)
    {
        if (IsDisposed || !_micOn || pcm.Length == 0) return;
        _sharedMic.HandleIncomingAudio(_currentSource.Id, _currentSource.ViaHost, pcm, sampleRate, audioSeq);
    }

    internal void UpdateStatus(string text)
    {
        if (!IsDisposed && !string.IsNullOrWhiteSpace(text))
        {
            _lastCameraStatusText = text;
            _statusLabel.Text = text;
            if (_frame == null)
                _canvas.Invalidate();
        }
    }

    internal void UpdateMicStatus(int stateCode, string? text)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(text)) return;
        _micStateNote = stateCode == 1 ? "Mic Live" : text;
        if (!_camOn || _frame == null)
        {
            _statusLabel.Text = text;
            _canvas.Invalidate();
        }
    }

    private void PopulateSourceCombo(IReadOnlyList<MediaSourceOption> sources, int preferredId, bool preferredViaHost)
    {
        _suppressEvents = true;
        try
        {
            _sourceCombo.Items.Clear();
            int selectedIdx = -1;
            for (int i = 0; i < sources.Count; i++)
            {
                _sourceCombo.Items.Add(sources[i]);
                if (sources[i].Id == preferredId && sources[i].ViaHost == preferredViaHost)
                    selectedIdx = i;
            }
            if (selectedIdx < 0 && sources.Count > 0)
            {
                selectedIdx = 0;
                _currentSource = sources[0];
            }
            if (selectedIdx >= 0)
                _sourceCombo.SelectedIndex = selectedIdx;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void SwitchSource(MediaSourceOption next, bool? explicitCam = null, bool? explicitMic = null)
    {
        var oldSource = _currentSource;
        bool keepOldMic = _sharedMic.IsActiveFor(oldSource.Id, oldSource.ViaHost) &&
                          _sharedMic.IsBackgroundMode &&
                          !(next.HasMicrophone && (explicitMic ?? false));
        try
        {
            _watchCamera(oldSource, false, _lens, false, keepOldMic);
            if (!keepOldMic)
                _watchMic(oldSource, false);
        }
        catch { }
        if (!keepOldMic && _sharedMic.IsActiveFor(oldSource.Id, oldSource.ViaHost))
            _sharedMic.Stop(restoreDefaultMic: false);
        lock (_frameLock)
        {
            _frame?.Dispose();
            _frame = null;
        }
        _fpsCount = 0;
        _fpsStartMs = Environment.TickCount64;
        _micStateNote = "";
        _lastCameraStatusText = "";
        _currentSource = next;
        _camOn = next.HasCamera && (explicitCam ?? _camOn);
        _micOn = next.HasMicrophone && (explicitMic ?? _sharedMic.IsActiveFor(next.Id, next.ViaHost));
        if (_micOn && !_sharedMic.IsActiveFor(next.Id, next.ViaHost))
        {
            _sharedMic.SetActiveSource(next.Id, next.ViaHost, next.Name,
                backgroundMode: false, autoSwitchDefault: true);
        }
        _torch = false;
        _suppressEvents = true;
        try
        {
            for (int i = 0; i < _sourceCombo.Items.Count; i++)
            {
                if (_sourceCombo.Items[i] is MediaSourceOption opt &&
                    opt.Id == next.Id && opt.ViaHost == next.ViaHost)
                {
                    _sourceCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
        Text = $"DeskTouch Vision — Camera & Mic ({next.Name})";
        UpdateSourceCapabilityButtons();
        UpdateMediaToggleButtons();
        UpdateTransportBadge();
        UpdateStatus($"Connecting to {next.Name}…");
        _canvas.Invalidate();
        StartWatchingCurrentSource();
        _onMediaStateChanged?.Invoke();
    }

    private void StartWatchingCurrentSource()
    {
        _watchCamera(_currentSource, _camOn, _lens, _currentSource.SupportsTorch && _torch, _micOn);
        _watchMic(_currentSource, _micOn);
    }

    private void SyncWatchState(bool micOnlyChange = false)
    {
        if (micOnlyChange)
            _watchMic(_currentSource, _micOn);
        else
            _watchCamera(_currentSource, _camOn, _lens, _currentSource.SupportsTorch && _torch, _micOn);
    }

    private void UpdateMediaToggleButtons()
    {
        _camBtn.Enabled = _currentSource.HasCamera;
        _camBtn.Text = !_currentSource.HasCamera ? "Camera: N/A" : (_camOn ? "Camera: On" : "Camera: Off");
        _camBtn.BackColor = _camOn && _currentSource.HasCamera
            ? Color.FromArgb(37, 99, 235)
            : Color.FromArgb(51, 65, 85);

        _micBtn.Enabled = _currentSource.HasMicrophone;
        _micBtn.Text = !_currentSource.HasMicrophone ? "Mic: N/A" : (_micOn ? "Mic: On" : "Mic: Off");
        _micBtn.BackColor = _micOn && _currentSource.HasMicrophone
            ? Color.FromArgb(5, 150, 105)
            : Color.FromArgb(51, 65, 85);
    }

    private void UpdateSourceCapabilityButtons()
    {
        _lensBtn.Enabled = _currentSource.HasCamera;
        if (_currentSource.Platform == "Android")
        {
            _lensBtn.Text = _lens == 0 ? "Lens: Back" : "Lens: Front";
            _torchBtn.Enabled = _currentSource.SupportsTorch && _currentSource.HasCamera;
            _torchBtn.Text = _torch ? "Torch: On" : "Torch: Off";
        }
        else
        {
            _lensBtn.Text = _lens == 0 ? "Camera #1" : $"Camera #{Math.Abs(_lens) + 1}";
            _torchBtn.Enabled = false;
            _torchBtn.Text = "Torch: N/A";
        }
    }

    private void RefreshAudioDevicesAndRouting(bool initialAutoSwitch)
    {
        _virtualMic = _sharedMic.RefreshRouting(initialAutoSwitch);
        _waveOutDevices = _sharedMic.WaveOutDevices;

        _suppressEvents = true;
        try
        {
            _outputCombo.Items.Clear();
            _outputCombo.Items.Add("Monitor: Default Speakers");
            foreach (var d in _waveOutDevices)
            {
                if (_virtualMic.Installed && d.WaveOutId == _virtualMic.RenderWaveOutId) continue;
                _outputCombo.Items.Add($"{d.Name} (#{d.WaveOutId})");
            }
            if (_outputCombo.Items.Count > 0)
                _outputCombo.SelectedIndex = 0;

            if (_virtualMic.Installed)
            {
                _virtMicLabel.Text = $"Native Mic Active: {_virtualMic.CaptureName}";
                _virtMicLabel.ForeColor = Color.FromArgb(110, 231, 183);
                _installDriverBtn.Visible = false;
                _autoDefaultMicCheck.Enabled = true;
                _autoDefaultMicCheck.Checked = _sharedMic.AutoDefaultMicChecked;
                _hearMonitorCheck.Checked = _sharedMic.HearMonitorActive;
            }
            else
            {
                ApplySelectedOutputDevice();
                _virtMicLabel.Text = "Native Mic Driver not installed (using PC speaker monitor)";
                _virtMicLabel.ForeColor = Color.FromArgb(253, 224, 71);
                _installDriverBtn.Visible = true;
                _autoDefaultMicCheck.Checked = false;
                _autoDefaultMicCheck.Enabled = false;
                _hearMonitorCheck.Checked = true;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ApplySelectedOutputDevice()
    {
        uint selectedId = WaveMapperId;
        int idx = _outputCombo.SelectedIndex;
        if (idx > 0)
        {
            int cur = 0;
            foreach (var d in _waveOutDevices)
            {
                if (_virtualMic.Installed && d.WaveOutId == _virtualMic.RenderWaveOutId) continue;
                cur++;
                if (cur == idx)
                {
                    selectedId = d.WaveOutId;
                    break;
                }
            }
        }
        _sharedMic.SetMonitorOutputDevice(selectedId);
    }

    private async Task InstallVbCableFromUiAsync()
    {
        if (_installingDriver) return;
        _installingDriver = true;
        _installDriverBtn.Enabled = false;
        try
        {
            var result = await WindowsAudioDeviceBridge.InstallVbCableDriverAsync(msg =>
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(() => _virtMicLabel.Text = msg);
            });
            if (IsDisposed) return;
            UpdateStatus(result.Message);
            RefreshAudioDevicesAndRouting(initialAutoSwitch: result.Installed);
        }
        finally
        {
            _installingDriver = false;
            if (!IsDisposed) _installDriverBtn.Enabled = true;
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        float lvl = _micOn && _sharedMic.IsActiveFor(_currentSource.Id, _currentSource.ViaHost)
            ? _sharedMic.CurrentLevel
            : 0f;
        if (Math.Abs(lvl - _vuLevel) > 0.01f)
        {
            _vuLevel = lvl;
            _vuMeter.Invalidate();
        }
        if ((++_timerTick % 10) == 0)
            UpdateTransportBadge();
    }

    private void UpdateTransportBadge()
    {
        bool udp = _isFastUdpActive(_currentSource);
        string want = _getTransportLabel(_currentSource);
        if (string.IsNullOrWhiteSpace(want))
            want = udp ? "Fast UDP" : "TCP Fallback";
        if (string.Equals(_transportBadge.Text, want, StringComparison.Ordinal)) return;
        _transportBadge.Text = want;
        _transportBadge.BackColor = udp ? Color.FromArgb(6, 78, 59) : Color.FromArgb(120, 53, 15);
        _transportBadge.ForeColor = udp ? Color.FromArgb(110, 231, 183) : Color.FromArgb(253, 230, 138);
    }

    private void PaintVuMeter(object? sender, PaintEventArgs e)
    {
        var r = _vuMeter.ClientRectangle;
        e.Graphics.Clear(Color.FromArgb(30, 41, 59));
        int fillW = (int)Math.Round(Math.Clamp(_vuLevel, 0f, 1f) * Math.Max(0, r.Width - 4));
        if (fillW > 0)
        {
            Color c = _vuLevel > 0.88f
                ? Color.FromArgb(239, 68, 68)
                : _vuLevel > 0.65f
                    ? Color.FromArgb(245, 158, 11)
                    : Color.FromArgb(34, 197, 94);
            using var b = new SolidBrush(c);
            e.Graphics.FillRectangle(b, 2, 2, fillW, Math.Max(2, r.Height - 4));
        }
        using var pen = new Pen(Color.FromArgb(71, 85, 105));
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
    }

    private void ToggleCleanView()
    {
        _cleanView = !_cleanView;
        _toolbar.Visible = !_cleanView;
        _micBar.Visible = !_cleanView;
        FormBorderStyle = _cleanView ? FormBorderStyle.None : FormBorderStyle.Sizable;
    }

    private void CopySnapshot()
    {
        lock (_frameLock)
        {
            if (_frame == null) return;
            try
            {
                Clipboard.SetImage(new Bitmap(_frame));
                _statusLabel.Text = "Snapshot copied to clipboard.";
            }
            catch { }
        }
    }

    private void PaintVideoCanvas(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        Bitmap? snap = null;
        lock (_frameLock)
        {
            if (_frame != null) snap = new Bitmap(_frame);
        }
        if (snap == null)
        {
            using var f = new Font("Segoe UI", 10.5f);
            string statusLine = !string.IsNullOrWhiteSpace(_lastCameraStatusText)
                ? $"\n\nStatus: {_lastCameraStatusText}"
                : "";
            string waitingMsg = _camOn
                ? $"Waiting for live video from {_currentSource.Name}…{statusLine}\n\nDouble-click video or use 'Clean View' to capture cleanly inside Zoom, Meet, Discord, or OBS."
                : $"Microphone-only mode active for {_currentSource.Name}.\n\nTurn 'Camera: On' above anytime to enable video.";
            TextRenderer.DrawText(
                e.Graphics,
                waitingMsg,
                f,
                _canvas.ClientRectangle,
                Color.FromArgb(148, 163, 184),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        using (snap)
        {
            if (_rotateDeg == 90) snap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            else if (_rotateDeg == 180) snap.RotateFlip(RotateFlipType.Rotate180FlipNone);
            else if (_rotateDeg == 270) snap.RotateFlip(RotateFlipType.Rotate270FlipNone);

            double scale = Math.Min(
                _canvas.Width / (double)snap.Width,
                _canvas.Height / (double)snap.Height);
            int w = Math.Max(1, (int)Math.Round(snap.Width * scale));
            int h = Math.Max(1, (int)Math.Round(snap.Height * scale));
            int x = (_canvas.Width - w) / 2;
            int y = (_canvas.Height - h) / 2;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(snap, new Rectangle(x, y, w, h));
        }
    }

    private static Button MakeBtn(string text, bool primary = false) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 29,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = primary ? Color.FromArgb(56, 189, 248) : Color.FromArgb(71, 85, 105), BorderSize = 1 },
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(30, 41, 59),
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 6, 2)
    };

    private static void EnableDoubleBuffering(Control c)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(c, true);
        }
        catch { }
    }
}
