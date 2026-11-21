using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Compact tray flyout anchored above the taskbar chevron area showing connected
/// devices, a count-only notification pill above each device, and 1-click switching.
/// </summary>
internal sealed class TrayDeviceFlyout : Form
{
    private sealed class DeviceCardControl : Control
    {
        private BridgeDeviceItemDto _device;
        private bool _hovered;
        private Point _hoverPoint;
        private readonly Action<BridgeDeviceItemDto> _onSelect;
        private readonly Action<BridgeDeviceItemDto> _onTogglePay;
        private readonly Action<BridgeDeviceItemDto, Point>? _onOpenNotifs;
        private readonly Action<BridgeDeviceItemDto>? _onToggleCamera;
        private readonly Action<BridgeDeviceItemDto>? _onToggleMic;
        private Rectangle _payRect;
        private Rectangle _badgeRect;
        private Rectangle _camRect;
        private Rectangle _micRect;

        internal DeviceCardControl(
            BridgeDeviceItemDto device,
            int cardWidth,
            int cardHeight,
            Action<BridgeDeviceItemDto> onSelect,
            Action<BridgeDeviceItemDto> onTogglePay,
            Action<BridgeDeviceItemDto, Point>? onOpenNotifs = null,
            Action<BridgeDeviceItemDto>? onToggleCamera = null,
            Action<BridgeDeviceItemDto>? onToggleMic = null)
        {
            _device = device;
            _onSelect = onSelect;
            _onTogglePay = onTogglePay;
            _onOpenNotifs = onOpenNotifs;
            _onToggleCamera = onToggleCamera;
            _onToggleMic = onToggleMic;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(cardWidth, cardHeight);
            Margin = new Padding(5, 2, 5, 6);
        }

        internal void UpdateDevice(BridgeDeviceItemDto device)
        {
            if (_device == device) return;
            _device = device;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            _hoverPoint = e is MouseEventArgs me ? me.Location : PointToClient(Cursor.Position);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_hovered) _hovered = true;
            bool wasInPill = _camRect.Contains(_hoverPoint) || _micRect.Contains(_hoverPoint) ||
                             _payRect.Contains(_hoverPoint) || _badgeRect.Contains(_hoverPoint);
            _hoverPoint = e.Location;
            bool nowInPill = _camRect.Contains(_hoverPoint) || _micRect.Contains(_hoverPoint) ||
                             _payRect.Contains(_hoverPoint) || _badgeRect.Contains(_hoverPoint);
            if (wasInPill != nowInPill || nowInPill)
                Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_badgeRect.IsEmpty && _badgeRect.Contains(e.Location) && _onOpenNotifs != null)
            {
                _onOpenNotifs(_device, PointToScreen(e.Location));
                return;
            }
            if (!_payRect.IsEmpty && _payRect.Contains(e.Location))
            {
                _onTogglePay(_device);
                return;
            }
            if (!_camRect.IsEmpty && _camRect.Contains(e.Location) && _onToggleCamera != null)
            {
                _onToggleCamera(_device);
                return;
            }
            if (!_micRect.IsEmpty && _micRect.Contains(e.Location) && _onToggleMic != null)
            {
                _onToggleMic(_device);
                return;
            }
            _onSelect(_device);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Notification pill centered strictly ABOVE the device card.
            string notifText = FormatNotificationBadge(_device.NotificationCount);
            Color notifBg = _device.NotificationCount > 0
                ? Color.FromArgb(56, 189, 248)
                : Color.FromArgb(51, 65, 85);
            Color notifFg = _device.NotificationCount > 0
                ? Color.FromArgb(9, 17, 30)
                : Color.FromArgb(203, 213, 225);

            using var badgeFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            Size badgeTextSize = TextRenderer.MeasureText(notifText, badgeFont);
            int badgeW = Math.Max(34, Math.Min(Width - 16, badgeTextSize.Width + 16));
            int badgeH = 20;
            var badgeRect = new Rectangle((Width - badgeW) / 2, 0, badgeW, badgeH);
            _badgeRect = badgeRect;
            using (var badgePath = RoundedRect(badgeRect, 10))
            using (var badgeBrush = new SolidBrush(notifBg))
            {
                g.FillPath(badgeBrush, badgePath);
            }
            TextRenderer.DrawText(g, notifText, badgeFont, badgeRect, notifFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Device card below the notification pill.
            var cardRect = new Rectangle(1, 24, Width - 3, Height - 26);
            bool active = _device.IsControlling || (_device.IsSelected && _device.IsLocal);
            Color cardFill = active
                ? Color.FromArgb(30, 58, 95)
                : (_hovered ? Color.FromArgb(38, 49, 68) : Color.FromArgb(26, 34, 49));
            Color borderColor = active
                ? Color.FromArgb(56, 189, 248)
                : (_hovered ? Color.FromArgb(100, 116, 139) : Color.FromArgb(51, 65, 85));

            using (var cardPath = RoundedRect(cardRect, 10))
            using (var fillBrush = new SolidBrush(cardFill))
            using (var borderPen = new Pen(borderColor, active ? 1.8f : 1.0f))
            {
                g.FillPath(fillBrush, cardPath);
                g.DrawPath(borderPen, cardPath);
            }

            string battText = _device.BatteryPercent >= 0
                ? (_device.IsCharging ? $" • ⚡{_device.BatteryPercent}%" : $" • {_device.BatteryPercent}%")
                : "";
            string tag = (_device.IsLocal ? "THIS PC" : (_device.Platform == "Android" ? "PHONE" : "PC")) + battText;
            Color tagColor = _device.IsLocal
                ? Color.FromArgb(147, 197, 253)
                : (_device.Platform == "Android"
                    ? Color.FromArgb(110, 231, 183)
                    : Color.FromArgb(125, 211, 252));
            using var tagFont = new Font("Segoe UI Semibold", 7.2f, FontStyle.Bold);
            var tagRect = new Rectangle(cardRect.Left + 8, cardRect.Top + 6, cardRect.Width - 16, 14);
            TextRenderer.DrawText(g, tag, tagFont, tagRect, tagColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using var nameFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            var nameRect = new Rectangle(cardRect.Left + 8, cardRect.Top + 21, cardRect.Width - 16, 20);
            TextRenderer.DrawText(g, _device.Name, nameFont, nameRect, Color.FromArgb(241, 245, 249),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            _payRect = Rectangle.Empty;
            _camRect = Rectangle.Empty;
            _micRect = Rectangle.Empty;

            bool showMediaStrip = !_device.IsLocal && (_device.HasCamera || _device.HasMicrophone);
            int statusY = showMediaStrip ? (cardRect.Bottom - 47) : (cardRect.Bottom - 23);

            using var statusFont = new Font("Segoe UI", 7.8f);
            if (!_device.IsLocal && _device.Platform == "Android" && _device.SupportsInput)
            {
                var statusRect = new Rectangle(cardRect.Left + 8, statusY, cardRect.Width - 68, 17);
                TextRenderer.DrawText(g, _device.StatusText, statusFont, statusRect,
                    active ? Color.FromArgb(186, 230, 253) : Color.FromArgb(148, 163, 184),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                _payRect = new Rectangle(cardRect.Right - 58, statusY - 1, 51, 18);
                using var payPath = RoundedRect(_payRect, 5);
                using var payBrush = new SolidBrush(Color.FromArgb(66, 32, 48));
                using var payPen = new Pen(Color.FromArgb(244, 114, 182), 1.0f);
                g.FillPath(payBrush, payPath);
                g.DrawPath(payPen, payPath);
                using var payFont = new Font("Segoe UI Semibold", 6.8f, FontStyle.Bold);
                TextRenderer.DrawText(g, "Pay Off", payFont, _payRect, Color.FromArgb(251, 207, 232),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                var statusRect = new Rectangle(cardRect.Left + 8, statusY, cardRect.Width - 16, 17);
                TextRenderer.DrawText(g, _device.StatusText, statusFont, statusRect,
                    active ? Color.FromArgb(186, 230, 253) : Color.FromArgb(148, 163, 184),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (showMediaStrip)
            {
                int nextX = cardRect.Left + 8;
                int pillY = cardRect.Bottom - 25;
                using var mediaFont = new Font("Segoe UI Semibold", 7.2f, FontStyle.Bold);

                if (_device.HasCamera)
                {
                    _camRect = new Rectangle(nextX, pillY, 50, 19);
                    bool camOn = _device.IsCameraActive;
                    bool camHover = _hovered && _camRect.Contains(_hoverPoint);
                    Color camFill = camOn
                        ? Color.FromArgb(37, 99, 235)
                        : (camHover ? Color.FromArgb(45, 58, 82) : Color.FromArgb(30, 41, 59));
                    Color camBorder = camOn || camHover
                        ? Color.FromArgb(96, 165, 250)
                        : Color.FromArgb(71, 85, 105);
                    using var camPath = RoundedRect(_camRect, 5);
                    using var camBrush = new SolidBrush(camFill);
                    using var camPen = new Pen(camBorder, 1.0f);
                    g.FillPath(camBrush, camPath);
                    g.DrawPath(camPen, camPath);
                    TextRenderer.DrawText(g, "Cam", mediaFont, _camRect,
                        camOn ? Color.White : Color.FromArgb(226, 232, 240),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    nextX += 56;
                }

                if (_device.HasMicrophone)
                {
                    _micRect = new Rectangle(nextX, pillY, 50, 19);
                    bool micOn = _device.IsMicrophoneActive;
                    bool micHover = _hovered && _micRect.Contains(_hoverPoint);
                    Color micFill = micOn
                        ? Color.FromArgb(5, 150, 105)
                        : (micHover ? Color.FromArgb(36, 62, 58) : Color.FromArgb(30, 41, 59));
                    Color micBorder = micOn || micHover
                        ? Color.FromArgb(52, 211, 153)
                        : Color.FromArgb(71, 85, 105);
                    using var micPath = RoundedRect(_micRect, 5);
                    using var micBrush = new SolidBrush(micFill);
                    using var micPen = new Pen(micBorder, 1.0f);
                    g.FillPath(micBrush, micPath);
                    g.DrawPath(micPen, micPath);
                    TextRenderer.DrawText(g, "Mic", mediaFont, _micRect,
                        micOn ? Color.White : Color.FromArgb(226, 232, 240),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    private readonly Label _titleLabel = new()
    {
        Text = "DeskTouch Devices",
        Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
        ForeColor = Color.FromArgb(241, 245, 249),
        AutoSize = true
    };
    private readonly Label _summaryPill = new()
    {
        Text = "0 connected",
        Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
        ForeColor = Color.FromArgb(125, 211, 252),
        BackColor = Color.FromArgb(23, 37, 58),
        Padding = new Padding(7, 3, 7, 3),
        AutoSize = true
    };
    private readonly Button _openButton = new()
    {
        Text = "Open",
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI Semibold", 8.5f),
        BackColor = Color.FromArgb(30, 41, 59),
        ForeColor = Color.FromArgb(226, 232, 240),
        Width = 60,
        Height = 26,
        Cursor = Cursors.Hand
    };
    private readonly FlowLayoutPanel _cardsPanel = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        AutoScroll = true,
        BackColor = Color.FromArgb(15, 23, 42),
        Padding = new Padding(10, 8, 10, 4)
    };
    private readonly Label _footerLabel = new()
    {
        Dock = DockStyle.Bottom,
        Height = 26,
        Text = "Click a device to switch mouse & keyboard • Notification counts above each device",
        Font = new Font("Segoe UI", 8f),
        ForeColor = Color.FromArgb(148, 163, 184),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 0, 14, 4)
    };

    private readonly Action<int> _onSwitchTarget;
    private readonly Action _onReturnLocal;
    private readonly Action<int> _onTogglePay;
    private readonly Action _onOpenMainWindow;
    private readonly Action<int, string, Point>? _onOpenNotifications;
    private readonly Action<BridgeDeviceItemDto>? _onToggleCamera;
    private readonly Action<BridgeDeviceItemDto>? _onToggleMic;
    private BridgeDeviceSnapshotDto? _snapshot;

    internal TrayDeviceFlyout(
        Action<int> onSwitchTarget,
        Action onReturnLocal,
        Action<int> onTogglePay,
        Action onOpenMainWindow,
        Action<int, string, Point>? onOpenNotifications = null,
        Action<BridgeDeviceItemDto>? onToggleCamera = null,
        Action<BridgeDeviceItemDto>? onToggleMic = null)
    {
        _onSwitchTarget = onSwitchTarget;
        _onReturnLocal = onReturnLocal;
        _onTogglePay = onTogglePay;
        _onOpenMainWindow = onOpenMainWindow;
        _onOpenNotifications = onOpenNotifications;
        _onToggleCamera = onToggleCamera;
        _onToggleMic = onToggleMic;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        StartPosition = FormStartPosition.Manual;

        _openButton.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _openButton.FlatAppearance.BorderSize = 1;
        _openButton.Click += (_, _) =>
        {
            Hide();
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(_onOpenMainWindow);
            else
                _onOpenMainWindow();
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(18, 28, 48),
            Padding = new Padding(14, 9, 12, 6)
        };
        var leftHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        _titleLabel.Margin = new Padding(0, 2, 8, 0);
        _summaryPill.Margin = new Padding(0, 2, 0, 0);
        leftHeader.Controls.Add(_titleLabel);
        leftHeader.Controls.Add(_summaryPill);
        _openButton.Dock = DockStyle.Right;
        header.Controls.Add(leftHeader);
        header.Controls.Add(_openButton);

        Controls.Add(_cardsPanel);
        Controls.Add(_footerLabel);
        Controls.Add(header);

        Deactivate += (_, _) =>
        {
            if (Visible && !IsDisposed) Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };
    }

    internal static string FormatNotificationBadge(int count) =>
        count >= 0 ? count.ToString() : "—";

    internal void UpdateSnapshot(BridgeDeviceSnapshotDto snapshot)
    {
        _snapshot = snapshot;
        _summaryPill.Text = snapshot.ConnectedCount == 1
            ? "1 connected"
            : $"{snapshot.ConnectedCount} connected";
        _footerLabel.Text = snapshot.IsRemoteActive
            ? $"Controlling {snapshot.ActiveDeviceName} • Click This PC to return"
            : "Click a card to switch input • Use Cam / Mic buttons below supported devices";

        bool hasMediaButtons = snapshot.Devices.Any(d => !d.IsLocal && (d.HasCamera || d.HasMicrophone));
        int cardW = hasMediaButtons ? 148 : 134;
        int cardH = hasMediaButtons ? 118 : 96;
        RebuildCards(snapshot, cardW, cardH);
        ResizeToFitCards(snapshot.Devices.Count, cardW, cardH);
    }

    internal void ToggleNearCursor(BridgeDeviceSnapshotDto snapshot)
    {
        if (Visible)
        {
            Hide();
            return;
        }
        UpdateSnapshot(snapshot);
        PositionAboveTray(Cursor.Position);
        Show();
        Activate();
    }

    private void RebuildCards(BridgeDeviceSnapshotDto snapshot, int cardW, int cardH)
    {
        _cardsPanel.SuspendLayout();
        try
        {
            foreach (Control existing in _cardsPanel.Controls)
                existing.Dispose();
            _cardsPanel.Controls.Clear();

            foreach (var device in snapshot.Devices)
            {
                var card = new DeviceCardControl(
                    device,
                    cardW,
                    cardH,
                    d =>
                    {
                        Hide();
                        if (d.IsLocal)
                            _onReturnLocal();
                        else if (d.SupportsInput)
                            _onSwitchTarget(d.Id);
                    },
                    d =>
                    {
                        _onTogglePay(d.Id);
                    },
                    (d, pt) =>
                    {
                        Hide();
                        _onOpenNotifications?.Invoke(d.Id, d.Name, pt);
                    },
                    _onToggleCamera != null
                        ? d =>
                        {
                            Hide();
                            if (!IsDisposed && IsHandleCreated)
                                BeginInvoke(() => _onToggleCamera(d));
                            else
                                _onToggleCamera(d);
                        }
                        : null,
                    _onToggleMic != null
                        ? d =>
                        {
                            Hide();
                            if (!IsDisposed && IsHandleCreated)
                                BeginInvoke(() => _onToggleMic(d));
                            else
                                _onToggleMic(d);
                        }
                        : null);
                _cardsPanel.Controls.Add(card);
            }
        }
        finally
        {
            _cardsPanel.ResumeLayout(true);
        }
    }

    private void ResizeToFitCards(int count, int cardW, int cardH)
    {
        int visible = Math.Clamp(count, 1, 8);
        int cols = Math.Min(3, visible);
        int rows = (int)Math.Ceiling(visible / 3.0);
        int width = Math.Max(316, cols * (cardW + 10) + 28);
        int height = 44 + 26 + rows * (cardH + 8) + 18;
        Size = new Size(width, Math.Min(500, height));
    }

    private void PositionAboveTray(Point anchorScreenPoint)
    {
        var screen = Screen.FromPoint(anchorScreenPoint);
        var work = screen.WorkingArea;
        int x = Math.Clamp(anchorScreenPoint.X - Width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - Width - 8));
        int y = Math.Clamp(work.Bottom - Height - 8, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - Height - 8));
        Location = new Point(x, y);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        using var borderPen = new Pen(Color.FromArgb(51, 65, 85), 1.5f);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
