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
