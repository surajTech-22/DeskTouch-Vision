using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Read-only map of Windows' actual extended desktop geometry. No Windows
/// display settings are modified; each computer keeps its native configuration.
/// </summary>
internal sealed class MonitorMap : Control
{
    private MonitorTopology? _layout;
    internal MonitorTopology? Layout
    {
        get => _layout;
        set { _layout = value; Invalidate(); }
    }

    internal MonitorMap()
    {
        Width = 400;
        Height = 110;
        BackColor = Color.FromArgb(13, 27, 46);
        ForeColor = Color.White;
        DoubleBuffered = true;
        Margin = new Padding(0, 5, 0, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var pale = new SolidBrush(Color.FromArgb(175, 190, 209));
        if (_layout == null)
        {
            g.DrawString("No monitor layout received", Font, pale, new PointF(10, 36));
            return;
        }
        var bounds = _layout.Bounds;
        float scale = Math.Min((Width - 22f) / Math.Max(1, bounds.Width),
            (Height - 30f) / Math.Max(1, bounds.Height));
        float dx = 10 + (Width - 20 - bounds.Width * scale) / 2f;
        float dy = 16 + (Height - 26 - bounds.Height * scale) / 2f;
        foreach (var monitor in _layout.Monitors)
        {
            var rect = new RectangleF(dx + (monitor.X - bounds.Left) * scale,
                dy + (monitor.Y - bounds.Top) * scale,
                Math.Max(14, monitor.Width * scale),
                Math.Max(11, monitor.Height * scale));
            using var background = new SolidBrush(monitor.Primary
                ? Color.FromArgb(39, 112, 166) : Color.FromArgb(55, 78, 107));
            using var stroke = new Pen(monitor.Primary
                ? Color.FromArgb(115, 215, 247) : Color.FromArgb(145, 161, 184), 1.5f);
            g.FillRectangle(background, rect);
            g.DrawRectangle(stroke, rect);
            string label = (monitor.Primary ? "★ " : "") +
                monitor.Name.Replace(@"\\.\\", "").Replace("DISPLAY", "D");
            if (rect.Width > 37 && rect.Height > 18)
                g.DrawString(label, Font, Brushes.WhiteSmoke,
                    new RectangleF(rect.X + 3, rect.Y + 2, rect.Width - 5, rect.Height - 3));
        }
        g.DrawString($"{_layout.Monitors.Count} Windows display(s) — automatic layout",
            Font, pale, new PointF(8, 0));
    }
}
