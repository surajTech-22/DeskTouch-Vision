using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;

namespace DeskTouchVision;

/// <summary>
/// Offline visual feasibility app, NOT a production keyboard/mouse replacement.
/// Camera frames are not transmitted or saved. Keyboard boundaries are manually
/// calibrated; movement overlays do NOT assert that a key has been pressed.
/// </summary>
public sealed class MainForm : Form
{
    const int FrameWidth = 640, FrameHeight = 480;
    private VideoCapture? _camera;
    private Mat? _referenceGray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(9, 18, 31), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(12), AutoSize = false };
    private readonly Label _info = new() { Dock = DockStyle.Top, Height = 104, ForeColor = Color.FromArgb(178, 192, 210),
       Padding = new Padding(10), Text = "STAGE 1 - CALIBRATION AND MOTION STUDY ONLY.\\n" +
       "The physical keyboard and mouse must be visible to this camera.\\n" +
       "This preview DOES NOT type or click; motion is not proof of contact." };
    private readonly Label _title = new() { Text = "DeskTouch Vision", Dock = DockStyle.Top, Height = 53,
        Font = new Font("Segoe UI Semibold", 21), ForeColor = Color.White, Padding = new Padding(10, 6, 0, 0) };
    private readonly Button _start = MakeButton("Start camera");
    private readonly Button _keyboard = MakeButton("Set keyboard area");
    private readonly Button _mouse = MakeButton("Set mouse area");
    private readonly Button _reference = MakeButton("Capture idle reference");
    private readonly Button _save = MakeButton("Save calibration");
    private readonly Button _updates = MakeButton("Check updates");
    private readonly Button _stop = MakeButton("Stop / privacy");
    private Rectangle? _keyboardRect, _mouseRect;
    private System.Drawing.Point? _first;
    private SelectionMode _selection = SelectionMode.None;
    private int _frames;
    private string _report = "Camera is off. No keypresses or mouse clicks are generated.";
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTouchVision", "camera-calibration.json");
    private static readonly string[] Rows = ["QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];
    private enum SelectionMode { None, Keyboard, Mouse }
    private sealed class Calibration
    {
        public Rectangle? Keyboard { get; set; }
        public Rectangle? Mouse { get; set; }
    }

    public MainForm()
    {
        Text = "DeskTouch Vision 0.1.0 — offline feasibility preview";
        BackColor = Color.FromArgb(16, 25, 40);
        ClientSize = new System.Drawing.Size(1080, 680);
        MinimumSize = new System.Drawing.Size(850, 540);
        StartPosition = FormStartPosition.CenterScreen;

        var sidebar = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Color.FromArgb(27, 40, 61),
            Padding = new Padding(14) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 335, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true };
        foreach (var b in new[] { _start, _keyboard, _mouse, _reference, _save, _updates, _stop })
        {
            b.Width = 275;
            b.Height = 38;
            b.Margin = new Padding(0, 3, 0, 5);
            buttons.Controls.Add(b);
        }

        sidebar.Controls.Add(_status);
        sidebar.Controls.Add(buttons);
        sidebar.Controls.Add(_info);
        sidebar.Controls.Add(_title);
        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        main.Controls.Add(_preview);
        Controls.Add(main);
        Controls.Add(sidebar);

        _timer = new System.Windows.Forms.Timer { Interval = 110 };
        _timer.Tick += (_, _) => NextFrame();
        _start.Click += (_, _) => StartCamera();
        _stop.Click += (_, _) => StopCamera();
        _keyboard.Click += (_, _) => SetMode(SelectionMode.Keyboard);
        _mouse.Click += (_, _) => SetMode(SelectionMode.Mouse);
        _reference.Click += (_, _) => CaptureReference();
        _save.Click += (_, _) => SaveCalibration();
        _updates.Click += async (_, _) => await UpdateClient.CheckManuallyAsync(this);
        _preview.MouseClick += PreviewClick;
        FormClosing += (_, _) => StopCamera();
        Shown += (_, _) => UpdateClient.StartEventUpdates(this);
        LoadCalibration();
        RefreshStatus();
    }

    private static Button MakeButton(string label) => new()
    {
        Text = label, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(48, 72, 106),
        ForeColor = Color.White, Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand
    };

    private void StartCamera()
    {
        if (_camera != null) return;
        try
        {
            _camera = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
            if (!_camera.IsOpened()) throw new InvalidOperationException("No accessible webcam found.");
            _camera.Set(VideoCaptureProperties.FrameWidth, FrameWidth);
            _camera.Set(VideoCaptureProperties.FrameHeight, FrameHeight);
            _timer.Start();
            _report = "Camera started. Point the webcam at the devices to calibrate.";
        }
        catch (Exception ex)
        {
            _report = "Camera unavailable: " + ex.Message + ". Check Windows camera permissions.";
            StopCamera(false);
        }
        RefreshStatus();
    }

    private void StopCamera(bool clearStatus = true)
    {
        _timer.Stop();
        _camera?.Release();
        _camera?.Dispose();
        _camera = null;
        _referenceGray?.Dispose();
        _referenceGray = null;
        _preview.Image?.Dispose();
        _preview.Image = null;
        if (clearStatus) _report = "Camera stopped. No background capture or recording.";
        RefreshStatus();
    }

    private void NextFrame()
    {
        if (_camera == null) return;
        try
        {
            using var raw = new Mat();
            if (!_camera.Read(raw) || raw.Empty()) { _report = "No video frame; try restarting the camera."; RefreshStatus(); return; }
            using var frame = new Mat();
            Cv2.Resize(raw, frame, new CvSize(FrameWidth, FrameHeight));
            using var canvas = frame.Clone();
            double score = 0;

            if (_referenceGray != null)
            {
                using var gray = new Mat();
                using var diff = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new CvSize(5, 5), 0);
                Cv2.Absdiff(_referenceGray, gray, diff);
                if (_keyboardRect is { } kb)
                {
                    foreach (var row in Enumerable.Range(0, Rows.Length))
                    {
                        var rowText = Rows[row];
                        for (int col = 0; col < rowText.Length; col++)
                        {
                            var cell = new CvRect(kb.Left + col * kb.Width / rowText.Length,
                                kb.Top + row * kb.Height / 3,
                                Math.Max(1, kb.Width / rowText.Length),
                                Math.Max(1, kb.Height / 3));
                            cell = Clip(cell);
                            if (cell.Width == 0 || cell.Height == 0) continue;
                            using var crop = new Mat(diff, cell);
                            double motion = Cv2.Mean(crop).Val0;
                            var ink = motion > 28 ? Scalar.OrangeRed : new Scalar(80, 215, 120);
                            Cv2.Rectangle(canvas, cell, ink, 1);
                            if (motion > 28) score = Math.Max(score, motion);
                            Cv2.PutText(canvas, rowText[col].ToString(), new CvPoint(cell.X + 3, cell.Y + 17),
                                HersheyFonts.HersheySimplex, .4, ink, 1);
                        }
                    }
                }
                if (_mouseRect is { } mouse)
                {
                    var rect = Clip(new CvRect(mouse.Left, mouse.Top, mouse.Width, mouse.Height));
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        using var crop = new Mat(diff, rect);
                        double motion = Cv2.Mean(crop).Val0;
                        Cv2.Rectangle(canvas, rect, motion > 28 ? Scalar.OrangeRed : Scalar.Cyan, 2);
                        Cv2.PutText(canvas, "Mouse area movement: " + motion.ToString("0.0"),
                            new CvPoint(rect.Left, Math.Max(15, rect.Top - 8)),
                            HersheyFonts.HersheySimplex, .45, Scalar.White, 1);
                    }
                }
            }
            else
            {
                DrawOutline(canvas, _keyboardRect, "Keyboard: manual QWERTY approximation", Scalar.LimeGreen);
                DrawOutline(canvas, _mouseRect, "Mouse study area", Scalar.Cyan);
            }
            Cv2.PutText(canvas, "NO INPUT INJECTION / OFFLINE", new CvPoint(10, 24),
                HersheyFonts.HersheySimplex, .58, Scalar.Yellow, 2);
            Cv2.ImEncode(".bmp", canvas, out var bytes);
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            var owned = new Bitmap(bitmap);
            var previous = _preview.Image;
            _preview.Image = owned;
            previous?.Dispose();
            if (++_frames % 12 == 0)
            {
                _report = _referenceGray == null
                  ? "Camera active. Select visible device corners, then capture idle reference."
                  : $"Motion-only study active (peak keyboard cell difference {score:0.0}). NOT a recognized keypress.";
                RefreshStatus();
            }
        }
        catch (Exception ex)
        {
            _report = "Camera processing failed: " + ex.Message;
            StopCamera(false);
            RefreshStatus();
        }
    }

    private static CvRect Clip(CvRect src)
    {
        var x = Math.Clamp(src.X, 0, FrameWidth);
        var y = Math.Clamp(src.Y, 0, FrameHeight);
        var r = Math.Clamp(src.Right, x, FrameWidth);
        var b = Math.Clamp(src.Bottom, y, FrameHeight);
        return new CvRect(x, y, r - x, b - y);
    }

    private static void DrawOutline(Mat image, Rectangle? region, string label, Scalar color)
    {
        if (region is not { } r) return;
        Cv2.Rectangle(image, new CvRect(r.Left, r.Top, r.Width, r.Height), color, 2);
        Cv2.PutText(image, label, new CvPoint(r.Left, Math.Max(36, r.Top - 8)),
            HersheyFonts.HersheySimplex, .45, color, 1);
    }

    private void CaptureReference()
    {
        if (_camera == null) { _report = "Start camera first."; RefreshStatus(); return; }
        using var raw = new Mat();
        using var resized = new Mat();
        if (!_camera.Read(raw) || raw.Empty()) { _report = "Cannot read camera."; RefreshStatus(); return; }
        Cv2.Resize(raw, resized, new CvSize(FrameWidth, FrameHeight));
        _referenceGray?.Dispose();
        _referenceGray = new Mat();
        Cv2.CvtColor(resized, _referenceGray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(_referenceGray, _referenceGray, new CvSize(5, 5), 0);
        _report = "Idle reference captured. Motion overlay enabled. False positives expected.";
        RefreshStatus();
    }

    private void SetMode(SelectionMode mode)
    {
        _selection = mode;
        _first = null;
        _report = "Click the TOP-LEFT, then BOTTOM-RIGHT of the " + mode.ToString().ToLowerInvariant() + " in the camera preview.";
        RefreshStatus();
    }

    private void PreviewClick(object? sender, MouseEventArgs e)
    {
        if (_selection == SelectionMode.None || _preview.Image == null) return;
        // PictureBox Zoom adds letterboxing; reverse its transformation.
        var ratio = Math.Min((float)_preview.ClientSize.Width / FrameWidth,
            (float)_preview.ClientSize.Height / FrameHeight);
        var offsetX = (_preview.Width - FrameWidth * ratio) / 2f;
        var offsetY = (_preview.Height - FrameHeight * ratio) / 2f;
        int x = (int)((e.X - offsetX) / ratio);
        int y = (int)((e.Y - offsetY) / ratio);
        if (x < 0 || y < 0 || x >= FrameWidth || y >= FrameHeight) return;
        if (_first == null)
        {
            _first = new System.Drawing.Point(x, y);
            _report = "First corner selected. Now select the opposite corner.";
        }
        else
        {
            var a = _first.Value;
            var region = Rectangle.FromLTRB(Math.Min(a.X, x), Math.Min(a.Y, y),
                Math.Max(a.X, x), Math.Max(a.Y, y));
            if (region.Width > 40 && region.Height > 30)
            {
                if (_selection == SelectionMode.Keyboard) _keyboardRect = region;
                else _mouseRect = region;
                _referenceGray?.Dispose();
                _referenceGray = null;
                _report = _selection + " rectangle calibrated. Capture idle reference to study visual motion.";
            }
            else _report = "Calibration area too small. Try again.";
            _selection = SelectionMode.None;
            _first = null;
        }
        RefreshStatus();
    }

    private void SaveCalibration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(
            new Calibration { Keyboard = _keyboardRect, Mouse = _mouseRect },
            new JsonSerializerOptions { WriteIndented = true }));
        _report = "Saved rectangle coordinates locally. No images or typed text saved.";
        RefreshStatus();
    }

    private void LoadCalibration()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var c = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(_configPath));
            _keyboardRect = c?.Keyboard;
            _mouseRect = c?.Mouse;
        }
        catch { _report = "Calibration file invalid; starting without saved areas."; }
    }

    private void RefreshStatus()
    {
        _status.Text = "CURRENT STATUS\\n\\n" + _report +
          "\\n\\nVision preview: " + (_camera != null ? "ON" : "OFF") +
          "\\nKeyboard calibration: " + (_keyboardRect != null ? "SET" : "NOT SET") +
          "\\nMouse calibration: " + (_mouseRect != null ? "SET" : "NOT SET") +
          "\\nOffline camera processing: YES" +
          "\\nActual keyboard events: DISABLED" +
          "\\nActual mouse events: DISABLED" +
          "\\n\\nPrivacy: live video frames stay in memory." +
          "\\nNormal keyboard and mouse are never blocked.";
    }
}
