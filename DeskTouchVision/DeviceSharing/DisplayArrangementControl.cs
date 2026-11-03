using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Windows Display Settings-style overview. Native Windows monitors are read
/// only; only the placement of an AUTHENTICATED remote computer is draggable.
/// </summary>
internal sealed class DisplayArrangementControl : Control
{
    private MonitorTopology? _local, _remote;
    private MonitorEdge _remoteEdge = MonitorEdge.Right;
    private RectangleF _remoteHit;
    private RectangleF _remoteBaseHit;
    private RectangleF _localHit;
    private RectangleF _placeholderHit;
    private bool _draggingRemote;
    private Point _dragOrigin;
    private Point _dragCurrent;

    internal event Action? AddComputerRequested;
    internal event Action<MonitorEdge>? RemoteEdgeChanged;

    internal MonitorTopology? Local
    {
        get => _local;
        set { _local = value; Invalidate(); }
    }
    internal MonitorTopology? Remote
    {
        get => _remote;
        set { _remote = value; Invalidate(); }
    }
    internal MonitorEdge RemoteEdge
    {
        get => _remoteEdge;
        set { _remoteEdge = value; Invalidate(); }
    }

    internal DisplayArrangementControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(30, 31, 36);
        ForeColor = Color.White;
        DoubleBuffered = true;
        Cursor = Cursors.Default;
        MinimumSize = new Size(300, 160);
        TabStop = true;
    }

    private static RectangleF Fit(Rectangle r, Rectangle source, float scale, float x, float y)
        => new(x + (r.Left - source.Left) * scale,
            y + (r.Top - source.Top) * scale,
            r.Width * scale, r.Height * scale);

    internal static Point ConstrainDrag(RectangleF remoteTile, Size viewport, Point delta)
    {
        // Dragging a connected PC beyond the window used to make the entire
        // tile disappear until mouse release. Retain it on-screen throughout.
        const int margin = 8;
        int left = (int)Math.Ceiling(margin - remoteTile.Left);
        int right = (int)Math.Floor(viewport.Width - margin - remoteTile.Right);
        int top = (int)Math.Ceiling(margin - remoteTile.Top);
        int bottom = (int)Math.Floor(viewport.Height - margin - remoteTile.Bottom);
        return new Point(
            left <= right ? Math.Clamp(delta.X, left, right) : 0,
            top <= bottom ? Math.Clamp(delta.Y, top, bottom) : 0);
    }

    private Point ClampedDragPoint(Point attempted)
    {
        var delta = ConstrainDrag(_remoteBaseHit, ClientSize,
            new Point(attempted.X - _dragOrigin.X, attempted.Y - _dragOrigin.Y));
        return new Point(_dragOrigin.X + delta.X, _dragOrigin.Y + delta.Y);
    }

    private static Rectangle Placement(Rectangle local, Rectangle remote, MonitorEdge edge)
    {
        const int gap = 110;
        int x, y;
        switch (edge)
        {
            case MonitorEdge.Left:
                x = local.Left - gap - remote.Width;
                y = local.Top + (local.Height - remote.Height) / 2;
                break;
            case MonitorEdge.Top:
                x = local.Left + (local.Width - remote.Width) / 2;
                y = local.Top - gap - remote.Height;
                break;
            case MonitorEdge.Bottom:
                x = local.Left + (local.Width - remote.Width) / 2;
                y = local.Bottom + gap;
                break;
            default:
                x = local.Right + gap;
                y = local.Top + (local.Height - remote.Height) / 2;
                break;
        }
        return new Rectangle(x, y, remote.Width, remote.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        _remoteHit = RectangleF.Empty;
        _remoteBaseHit = RectangleF.Empty;
        _localHit = RectangleF.Empty;
        _placeholderHit = RectangleF.Empty;

        if (_local == null)
        {
            DrawCenter(g, "Windows display information is unavailable", ClientRectangle,
                Brushes.Gainsboro, 12f);
            return;
        }

        var l = _local.Bounds;
        var r = _remote?.Bounds;
        var remotePos = r is null
            ? Placement(l, new Rectangle(0, 0, Math.Max(900, l.Width * 2 / 3),
                Math.Max(540, l.Height * 2 / 3)), _remoteEdge)
            : Placement(l, r.Value, _remoteEdge);

        // Local monitors are fixed by Windows. Only remote computer is placed
        // next to those displays. This does not call Windows ChangeDisplaySettings.
        var full = Rectangle.Union(l, remotePos);
        const float pad = 38;
        float availableX = Math.Max(90, Width - pad * 2);
        float availableY = Math.Max(70, Height - pad * 2);
        float scale = Math.Min(availableX / Math.Max(1, full.Width),
            availableY / Math.Max(1, full.Height));
        scale = Math.Max(.001f, scale);
        float offsetX = (Width - full.Width * scale) / 2f;
        float offsetY = (Height - full.Height * scale) / 2f;

        _localHit = new RectangleF(
            offsetX + (l.Left - full.Left) * scale,
            offsetY + (l.Top - full.Top) * scale,
            l.Width * scale, l.Height * scale);
        int id = 1;
        foreach (var m in _local.Monitors.OrderBy(m => m.Primary ? 0 : 1))
        {
            var tile = Fit(m.Bounds, full, scale, offsetX, offsetY);
            DrawScreen(g, tile, id++.ToString(), m.Primary, true);
        }

        if (_remote is { } remote)
        {
            var group = new RectangleF(offsetX + (remotePos.Left - full.Left) * scale,
                offsetY + (remotePos.Top - full.Top) * scale,
                remotePos.Width * scale, remotePos.Height * scale);
            _remoteBaseHit = group;
            if (_draggingRemote)
            {
                group.X += _dragCurrent.X - _dragOrigin.X;
                group.Y += _dragCurrent.Y - _dragOrigin.Y;
            }
            _remoteHit = group;
            foreach (var m in remote.Monitors.OrderBy(m => m.Primary ? 0 : 1))
            {
                var rect = new Rectangle(
                    remotePos.Left + m.X - remote.Bounds.Left,
                    remotePos.Top + m.Y - remote.Bounds.Top, m.Width, m.Height);
                var tile = Fit(rect, full, scale, offsetX, offsetY);
                if (_draggingRemote)
                    tile.Offset(_dragCurrent.X - _dragOrigin.X, _dragCurrent.Y - _dragOrigin.Y);
                DrawScreen(g, tile, id++.ToString(), false, false);
            }
            if (group.Width > 75)
            {
                using var font = new Font("Segoe UI", 9f, FontStyle.Regular);
                DrawCenter(g, "Connected PC  •  drag to arrange", new RectangleF(
                    group.Left, group.Bottom + 3, group.Width, 20),
                    Brushes.Gainsboro, 9f);
            }
        }
        else
        {
            _placeholderHit = new RectangleF(
                offsetX + (remotePos.Left - full.Left) * scale,
                offsetY + (remotePos.Top - full.Top) * scale,
                remotePos.Width * scale, remotePos.Height * scale);
            DrawPlaceholder(g, _placeholderHit);
        }
        DrawCenter(g, "Windows native displays", new RectangleF(6, 5, Width - 12, 19),
            Brushes.LightGray, 9f);
    }

    private static void DrawPlaceholder(Graphics g, RectangleF r)
    {
        using var pen = new Pen(Color.FromArgb(100, 135, 151), 1.4f)
            { DashStyle = DashStyle.Dash };
        using var bg = new SolidBrush(Color.FromArgb(39, 42, 49));
        using var path = Rounded(r, 10);
        g.FillPath(bg, path);
        g.DrawPath(pen, path);
        DrawCenter(g, "+  Add a computer", r, Brushes.LightGray, 12);
    }

    private static void DrawScreen(Graphics g, RectangleF rect, string number, bool primary, bool local)
    {
        if (rect.Width < 5 || rect.Height < 5) return;
        using var path = Rounded(rect, 9);
        using var fill = new SolidBrush(local
            ? Color.FromArgb(66, 119, 203)
            : Color.FromArgb(54, 136, 131));
        using var outline = new Pen(primary ? Color.WhiteSmoke :
            Color.FromArgb(170, 190, 205), primary ? 2.2f : 1.2f);
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        // Scale the label to BOTH dimensions and skip it for small screens.
        // Otherwise a large numeric label looked like a collapsing tile at
        // high display resolutions or when the window was resized.
        if (rect.Width >= 30 && rect.Height >= 24)
            DrawCenter(g, number, rect, Brushes.White,
                Math.Clamp(Math.Min(rect.Height / 2.5f, rect.Width / 3f), 9, 28));
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float rad = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 4f);
        float d = rad * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawCenter(Graphics g, string content, RectangleF rect,
        Brush color, float fontSize)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular);
        using var format = new StringFormat { Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(content, font, color, rect, format);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_remote != null && _remoteHit.Contains(e.Location))
        {
            _draggingRemote = true;
            _dragOrigin = _dragCurrent = e.Location;
            Capture = true;
            Cursor = Cursors.SizeAll;
        }
        else if (_remote == null && _placeholderHit.Contains(e.Location))
            AddComputerRequested?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingRemote)
        {
            _dragCurrent = ClampedDragPoint(e.Location);
            Invalidate();
        }
        else Cursor = _remote != null && _remoteHit.Contains(e.Location)
            ? Cursors.SizeAll
            : _remote == null && _placeholderHit.Contains(e.Location)
                ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_draggingRemote) return;
        _dragCurrent = ClampedDragPoint(e.Location);
        _draggingRemote = false;
        Capture = false;
        Cursor = Cursors.Default;
        var dx = _dragCurrent.X - _dragOrigin.X;
        var dy = _dragCurrent.Y - _dragOrigin.Y;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 25)
        {
            // Judge the DROP LOCATION relative to the Windows monitor group,
            // not merely the direction of a short drag.
            var remoteCenter = new PointF(
                _remoteBaseHit.Left + dx + _remoteBaseHit.Width / 2,
                _remoteBaseHit.Top + dy + _remoteBaseHit.Height / 2);
            var localCenter = new PointF(
                _localHit.Left + _localHit.Width / 2,
                _localHit.Top + _localHit.Height / 2);
            float horizontal = remoteCenter.X - localCenter.X;
            float vertical = remoteCenter.Y - localCenter.Y;
            var next = Math.Abs(horizontal) >= Math.Abs(vertical)
                ? horizontal >= 0 ? MonitorEdge.Right : MonitorEdge.Left
                : vertical >= 0 ? MonitorEdge.Bottom : MonitorEdge.Top;
            RemoteEdge = next;
            RemoteEdgeChanged?.Invoke(next);
        }
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_draggingRemote && !Capture)
        {
            _draggingRemote = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        if (_draggingRemote)
        {
            _draggingRemote = false;
            Capture = false;
            Cursor = Cursors.Default;
        }
        base.OnResize(e);
        Invalidate();
    }
}
