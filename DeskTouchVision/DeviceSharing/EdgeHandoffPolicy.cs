using System.Drawing;

namespace DeskTouch.DeviceShare;

/// <summary>
/// A physical outward movement at an exposed Windows monitor edge requests
/// control of the adjacent computer. Raw mouse deltas remain available when
/// Windows clamps the visible pointer at that boundary.
/// </summary>
internal static class EdgeHandoffPolicy
{
    // A three-pixel band missed fast mice and mixed-DPI setups. This still
    // requires positive movement OUT of the Windows desktop, never a hover.
    internal const int EdgeZonePixels = 14;

    internal static bool MovingOutward(MonitorEdge edge, int deltaX, int deltaY) =>
        edge switch
        {
            MonitorEdge.Right => deltaX > 0,
            MonitorEdge.Left => deltaX < 0,
            MonitorEdge.Top => deltaY < 0,
            MonitorEdge.Bottom => deltaY > 0,
            _ => false
        };

    internal static bool ShouldRequest(
        MonitorTopology nativeDisplays, Point physicalCursor, MonitorEdge edge,
        int deltaX, int deltaY)
    {
        return MovingOutward(edge, deltaX, deltaY) &&
            nativeDisplays.IsExternalEdge(physicalCursor, edge, EdgeZonePixels);
    }
}

 
/// <summary>
/// Slow movement may deliver a few small outward input packets, then no more
/// cursor motion once Windows clamps at the last physical pixel. Remember that
/// deliberate approach briefly; a UI message-pump tick completes handoff when
/// the pointer reaches an EXPOSED monitor edge. Ordinary idle hover, inward
/// movement and native Windows seams never create a candidate.
/// </summary>
internal sealed class SlowEdgeApproach
{
    internal const int ApproachZonePixels = 24;
    internal const int FinalEdgePixels = 3;
    internal const int MinimumApproachMilliseconds = 170;
    internal const int IntentExpiryMilliseconds = 1100;

    private bool _armed;
    private MonitorEdge _edge;
    private long _since;
    private long _lastMovement;
    private readonly object _gate = new();

    internal void Reset()
    {
        lock (_gate) _armed = false;
    }

    internal void Observe(MonitorTopology nativeMonitors, Point cursor,
        MonitorEdge edge, int dx, int dy, long timestamp)
    {
        lock (_gate)
        {
            if (EdgeHandoffPolicy.MovingOutward(edge, dx, dy))
            {
                if (!nativeMonitors.IsExternalEdge(cursor, edge, ApproachZonePixels))
                {
                    _armed = false;
                    return;
                }
                if (!_armed || _edge != edge ||
                    timestamp - _lastMovement > IntentExpiryMilliseconds)
                    _since = timestamp;
                _edge = edge;
                _lastMovement = timestamp;
                _armed = true;
            }
            else if (EdgeHandoffPolicy.MovingOutward(edge, -dx, -dy))
            {
                // Native mouse moved inward: cancel the old approach intent.
                _armed = false;
            }
        }
    }

    internal bool Ready(MonitorTopology nativeMonitors, Point cursor,
        MonitorEdge edge, long timestamp)
    {
        lock (_gate)
        {
            if (!_armed || edge != _edge) return false;
            if (timestamp - _lastMovement > IntentExpiryMilliseconds)
            {
                _armed = false;
                return false;
            }
            if (timestamp - _since < MinimumApproachMilliseconds)
                return false;
            if (!nativeMonitors.IsExternalEdge(cursor, edge, FinalEdgePixels))
                return false;
            _armed = false; // one deliberate attempt, never a retry loop
            return true;
        }
    }
}
