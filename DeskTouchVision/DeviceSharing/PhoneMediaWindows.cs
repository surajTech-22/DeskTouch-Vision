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

/// <summary>
/// Lightweight WinMM waveOut 16kHz 16-bit mono PCM monitor for incoming phone mic audio.
/// </summary>
internal sealed class WaveOutAudioPlayer : IDisposable
{
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const int WAVE_FORMAT_PCM = 1;
    private readonly object _gate = new();
    private IntPtr _hWaveOut;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    internal void PlayPcm16Mono(byte[] pcm, int sampleRate = 16000)
    {
        if (_disposed || pcm.Length == 0) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_hWaveOut == IntPtr.Zero)
            {
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_PCM,
                    nChannels = 1,
                    nSamplesPerSec = (uint)Math.Clamp(sampleRate, 8000, 48000),
                    wBitsPerSample = 16,
                    nBlockAlign = 2,
                    nAvgBytesPerSec = (uint)(Math.Clamp(sampleRate, 8000, 48000) * 2),
                    cbSize = 0
                };
                if (waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                {
                    _hWaveOut = IntPtr.Zero;
                    return;
                }
            }
            IntPtr dataPtr = Marshal.AllocHGlobal(pcm.Length);
            Marshal.Copy(pcm, 0, dataPtr, pcm.Length);
            var hdr = new WAVEHDR { lpData = dataPtr, dwBufferLength = (uint)pcm.Length };
            if (waveOutPrepareHeader(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>()) == 0)
            {
                _ = waveOutWrite(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>());
                _ = Task.Run(async () =>
                {
                    await Task.Delay(260);
                    try
                    {
                        lock (_gate)
                        {
                            if (_hWaveOut != IntPtr.Zero)
                                _ = waveOutUnprepareHeader(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>());
                        }
                    }
                    catch { }
                    finally { Marshal.FreeHGlobal(dataPtr); }
                });
            }
            else
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_hWaveOut != IntPtr.Zero)
            {
                try { _ = waveOutReset(_hWaveOut); } catch { }
                try { _ = waveOutClose(_hWaveOut); } catch { }
                _hWaveOut = IntPtr.Zero;
            }
        }
    }
}

/// <summary>
/// Live wireless Phone Camera & Microphone viewer on Windows with Clean Window Mode,
/// loopback MJPEG stream for OBS/browsers, Front/Back flip, Torch, Rotate, and Mic monitor.
/// </summary>
internal sealed class PhoneWebcamForm : Form
{
    private readonly int _peerId;
    private readonly Action<int, bool, int, bool, bool> _watchCamera;
    private readonly Panel _toolbar = new();
    private readonly Panel _canvas = new();
    private readonly Label _statusLabel = new();
    private readonly Button _lensBtn = MakeBtn("Lens: Back");
    private readonly Button _torchBtn = MakeBtn("Torch: Off");
    private readonly Button _micBtn = MakeBtn("Mic Audio: Off");
    private readonly LocalMjpegServer _mjpeg = new();
    private readonly WaveOutAudioPlayer _audioPlayer = new();
    private Bitmap? _frame;
    private readonly object _frameLock = new();
    private int _lens; // 0 = back, 1 = front
    private bool _torch;
    private bool _micOn;
    private int _rotateDeg;
    private bool _cleanView;
    private int _fpsCount;
    private long _fpsStartMs = Environment.TickCount64;

    internal PhoneWebcamForm(
        int peerId,
        string deviceName,
        Action<int, bool, int, bool, bool> watchCamera)
    {
        _peerId = peerId;
        _watchCamera = watchCamera;

        Text = $"DeskTouch Vision — Phone Camera & Mic ({deviceName})";
        Size = new Size(760, 520);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 18, 30);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;

        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = 46;
        _toolbar.BackColor = Color.FromArgb(20, 29, 46);
        _toolbar.Padding = new Padding(10, 8, 10, 6);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        _lensBtn.Click += (_, _) =>
        {
            _lens = _lens == 0 ? 1 : 0;
            _lensBtn.Text = _lens == 0 ? "Lens: Back" : "Lens: Front";
            if (_lens == 1) { _torch = false; _torchBtn.Text = "Torch: Off"; }
            _watchCamera(_peerId, true, _lens, _torch, _micOn);
        };
        _torchBtn.Click += (_, _) =>
        {
            if (_lens == 1) _lens = 0;
            _lensBtn.Text = "Lens: Back";
            _torch = !_torch;
            _torchBtn.Text = _torch ? "Torch: On" : "Torch: Off";
            _watchCamera(_peerId, true, _lens, _torch, _micOn);
        };
        var rotBtn = MakeBtn("Rotate 90°");
        rotBtn.Click += (_, _) => { _rotateDeg = (_rotateDeg + 90) % 360; _canvas.Invalidate(); };

        _micBtn.Click += (_, _) =>
        {
            _micOn = !_micOn;
            _micBtn.Text = _micOn ? "Mic Audio: On" : "Mic Audio: Off";
            _watchCamera(_peerId, true, _lens, _torch, _micOn);
        };

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

        _statusLabel.Text = "Waiting for camera permission on phone…";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(125, 211, 252);
        _statusLabel.Margin = new Padding(8, 5, 0, 0);

        flow.Controls.AddRange(new Control[] { _lensBtn, _torchBtn, rotBtn, _micBtn, snapBtn, urlBtn, cleanBtn, _statusLabel });
        _toolbar.Controls.Add(flow);

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
        Controls.Add(_toolbar);

        Shown += (_, _) =>
        {
            _mjpeg.Start();
            _watchCamera(_peerId, true, _lens, _torch, _micOn);
        };
        FormClosed += (_, _) =>
        {
            _watchCamera(_peerId, false, _lens, false, false);
            _mjpeg.Dispose();
            _audioPlayer.Dispose();
            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = null;
            }
        };
    }

    internal int PeerId => _peerId;

    internal void UpdateCameraFrame(byte[] jpegBytes)
    {
        if (IsDisposed || jpegBytes.Length == 0) return;
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
                _statusLabel.Text = $"Live • {fps:F0} FPS • {_mjpeg.StreamUrl}";
            }
            _canvas.Invalidate();
        }
        catch { }
    }

    internal void HandleMicAudio(byte[] pcm, int sampleRate)
    {
        if (IsDisposed || !_micOn || pcm.Length == 0) return;
        _audioPlayer.PlayPcm16Mono(pcm, sampleRate);
    }

    internal void UpdateStatus(string text)
    {
        if (!IsDisposed && !string.IsNullOrWhiteSpace(text))
            _statusLabel.Text = text;
    }

    private void ToggleCleanView()
    {
        _cleanView = !_cleanView;
        _toolbar.Visible = !_cleanView;
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
            TextRenderer.DrawText(
                e.Graphics,
                "Waiting for wireless phone camera feed…\n\nOn Android, tap 'Start Camera / Mic' when prompted.\nDouble-click video or use 'Clean View' to capture cleanly inside calls or OBS.",
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
