using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Copies the Windows display geometry. No virtual display drivers or Windows
/// display setting changes: native Extended Desktop remains authoritative.
/// </summary>
internal sealed record MonitorRect(string Name, int X, int Y, int Width, int Height, bool Primary)
{
    internal Rectangle Bounds => new(X, Y, Width, Height);
}

internal enum MonitorEdge { Left, Right, Top, Bottom }

internal sealed class MonitorTopology
{
    internal IReadOnlyList<MonitorRect> Monitors { get; }
    internal Rectangle Bounds { get; }

    internal MonitorTopology(IEnumerable<MonitorRect> screens)
    {
        var entries = screens.ToArray();
        if (entries.Length is < 1 or > 20) throw new InvalidDataException("Invalid display count.");
        foreach (var monitor in entries)
        {
            if (monitor.Width is < 1 or > 32768 || monitor.Height is < 1 or > 32768 ||
                monitor.X is < -100000 or > 100000 || monitor.Y is < -100000 or > 100000 ||
                string.IsNullOrWhiteSpace(monitor.Name) || monitor.Name.Length > 150)
                throw new InvalidDataException("Invalid monitor geometry.");
        }
        Monitors = entries;
        var minX = entries.Min(m => m.X);
        var minY = entries.Min(m => m.Y);
        var maxX = entries.Max(m => (long)m.X + m.Width);
        var maxY = entries.Max(m => (long)m.Y + m.Height);
        if (maxX - minX is < 1 or > 100000 || maxY - minY is < 1 or > 100000)
            throw new InvalidDataException("Invalid virtual display size.");
        Bounds = Rectangle.FromLTRB(minX, minY, (int)maxX, (int)maxY);
    }

    internal static MonitorTopology Capture() => new(
        Screen.AllScreens.Select(s => new MonitorRect(
            s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary)));

    internal string ToWire() => JsonSerializer.Serialize(Monitors);

    internal static MonitorTopology FromWire(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
            throw new InvalidDataException("Missing or oversized display layout.");
        var screens = JsonSerializer.Deserialize<MonitorRect[]>(json)
            ?? throw new InvalidDataException("No monitors were described.");
        return new MonitorTopology(screens);
    }

    internal MonitorRect? MonitorAt(Point p) =>
        Monitors.FirstOrDefault(m => m.Bounds.Contains(p));

    internal bool IsExternalEdge(Point p, MonitorEdge edge, int threshold = 3)
    {
        var monitor = MonitorAt(p);
        if (monitor == null) return false;
        var bounds = monitor.Bounds;
        var adjacent = edge switch
        {
            MonitorEdge.Left => new Point(bounds.Left - 1, p.Y),
            MonitorEdge.Right => new Point(bounds.Right, p.Y),
            MonitorEdge.Top => new Point(p.X, bounds.Top - 1),
            MonitorEdge.Bottom => new Point(p.X, bounds.Bottom),
            _ => p
        };
        bool touching = edge switch
        {
            MonitorEdge.Left => p.X <= bounds.Left + threshold - 1,
            MonitorEdge.Right => p.X >= bounds.Right - threshold,
            MonitorEdge.Top => p.Y <= bounds.Top + threshold - 1,
            MonitorEdge.Bottom => p.Y >= bounds.Bottom - threshold,
            _ => false
        };
        // Internal Windows extended-monitor seams never trigger PC switching.
        return touching && MonitorAt(adjacent) == null;
    }

    internal double EdgeFraction(Point p, MonitorEdge edge)
    {
        double fraction = edge is MonitorEdge.Left or MonitorEdge.Right
            ? (double)(p.Y - Bounds.Top) / Math.Max(1, Bounds.Height - 1)
            : (double)(p.X - Bounds.Left) / Math.Max(1, Bounds.Width - 1);
        return Math.Clamp(fraction, 0d, 1d);
    }

    /// <summary>
    /// Maps an incoming cursor to a REAL monitor's exposed physical edge,
    /// never into an unoccupied rectangle of the virtual bounding box.
    /// </summary>
    internal Point EntryPoint(MonitorEdge edge, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        bool horizontal = edge is MonitorEdge.Left or MonitorEdge.Right;
        var desired = horizontal
            ? Bounds.Top + (int)Math.Round(fraction * Math.Max(0, Bounds.Height - 1))
            : Bounds.Left + (int)Math.Round(fraction * Math.Max(0, Bounds.Width - 1));
        Point best = default;
        double bestCost = double.PositiveInfinity;
        foreach (var m in Monitors)
        {
            var b = m.Bounds;
            var p = edge switch
            {
                MonitorEdge.Left => new Point(b.Left, Math.Clamp(desired, b.Top, b.Bottom - 1)),
                MonitorEdge.Right => new Point(b.Right - 1, Math.Clamp(desired, b.Top, b.Bottom - 1)),
                MonitorEdge.Top => new Point(Math.Clamp(desired, b.Left, b.Right - 1), b.Top),
                MonitorEdge.Bottom => new Point(Math.Clamp(desired, b.Left, b.Right - 1), b.Bottom - 1),
                _ => throw new ArgumentOutOfRangeException(nameof(edge))
            };
            if (!IsExternalEdge(p, edge, 1)) continue;
            var d = horizontal ? Math.Abs((double)desired - p.Y) : Math.Abs((double)desired - p.X);
            // Prefer the outermost physical monitor for a tie.
            double outer = edge switch
            {
                MonitorEdge.Left => b.Left - Bounds.Left,
                MonitorEdge.Right => Bounds.Right - b.Right,
                MonitorEdge.Top => b.Top - Bounds.Top,
                MonitorEdge.Bottom => Bounds.Bottom - b.Bottom,
                _ => 0
            };
            var cost = d * 1000 + outer;
            if (cost < bestCost) { bestCost = cost; best = p; }
        }
        if (double.IsPositiveInfinity(bestCost))
            throw new InvalidDataException("No exposed entry edge on receiver.");
        return best;
    }

    internal Point ClampToRealMonitor(Point desired)
    {
        if (MonitorAt(desired) != null) return desired;
        double bestDistance = double.PositiveInfinity;
        Point closest = default;
        foreach (var monitor in Monitors)
        {
            var b = monitor.Bounds;
            var p = new Point(Math.Clamp(desired.X, b.Left, b.Right - 1),
                Math.Clamp(desired.Y, b.Top, b.Bottom - 1));
            double dist = (double)(desired.X - p.X) * (desired.X - p.X)
                + (double)(desired.Y - p.Y) * (desired.Y - p.Y);
            if (dist < bestDistance) { bestDistance = dist; closest = p; }
        }
        return closest;
    }
}
