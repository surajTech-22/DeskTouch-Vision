using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Windows Display Settings-style overview. Native Windows monitors are read
/// only; only the placement of an AUTHENTICATED remote computer is draggable.
/// </summary>
internal sealed record EnvironmentDisplayTile(int TargetId,string Name,string Platform,
    int ScreenCount,bool SupportsInput,string? StableIdentity=null)
{
    internal bool IsAndroid => Platform=="Android";
    // Prefer a persistent authenticated device identity. The hub roster only
    // exposes a connection-scoped route ID, so names are a UI fallback until
    // the peer advertises its stable ID (never IP based).
    internal string LayoutKey => !string.IsNullOrWhiteSpace(StableIdentity)
        ? StableIdentity : Platform+"|"+Name;
    // v2 stores one ABSOLUTE screen position relative to the hub's monitor
    // origin. Old saved local-view offsets remain readable until re-dragged.
    internal string CanonicalLayoutKey => "hub@"+LayoutKey;
}

// Logical routing order follows the EXISTING inline screen arrangement.
// A local keyboard has one active destination at a time, while every other
// authenticated environment member stays connected and independently usable.
internal sealed class EnvironmentRouteOrder
{
    internal const int Local = int.MinValue;
    private readonly IReadOnlyList<int> _order;
    internal IReadOnlyList<int> Order => _order;
    internal EnvironmentRouteOrder(IReadOnlyList<int> arranged)
    {
        _order=arranged.Distinct().ToArray();
    }
    internal EnvironmentRouteOrder(int baseTargetId,
        IReadOnlyList<EnvironmentDisplayTile> extraDevices, MonitorEdge baseEdge)
    {
        var list=new List<int>();
        if(baseEdge==MonitorEdge.Left)list.Add(baseTargetId);
        list.Add(Local);
        if(baseEdge!=MonitorEdge.Left)list.Add(baseTargetId);
        foreach(var member in extraDevices)
            if(member.SupportsInput && member.TargetId!=baseTargetId &&
                !list.Contains(member.TargetId))
                list.Add(member.TargetId);
        _order=list;
    }
    internal int? Neighbor(int currentTargetId, MonitorEdge toward)
    {
        if(toward is not (MonitorEdge.Left or MonitorEdge.Right))
            return null;
        int index=-1;
        for(int i=0;i<_order.Count;i++)
            if(_order[i]==currentTargetId){index=i;break;}
        if(index<0)return null;
        int next=index+(toward==MonitorEdge.Right?1:-1);
        return next>=0&&next<_order.Count?_order[next]:null;
    }
}

internal sealed class DisplayArrangementControl : Control
{
    private MonitorTopology? _local, _remote;
    private string _localDeviceName = Environment.MachineName;
    private string _remoteDeviceName = "Connected device";
    internal string LocalDeviceName
    {
        get => _localDeviceName;
        set { _localDeviceName = CleanDeviceName(value, Environment.MachineName); Invalidate(); }
    }
    internal string RemoteDeviceName
    {
        get => _remoteDeviceName;
        set { _remoteDeviceName = CleanDeviceName(value, "Connected device"); Invalidate(); }
    }
    internal static string CleanDeviceName(string? name, string fallback)
    {
        string clean = (name ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length == 0 ? fallback : clean.Length > 55 ? clean[..55] : clean;
    }
    internal static int DisplayNumber(MonitorRect monitor, int fallback)
    {
        // The Windows \\.\DISPLAYn monitor identifier is authoritative.
        // Enumerating other PCs must not increment the local numbers.
        var match = System.Text.RegularExpressions.Regex.Match(
            monitor.Name, @"DISPLAY(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int number)
            && number > 0 && number <= 99 ? number : fallback;
    }
    private MonitorEdge _remoteEdge = MonitorEdge.Right;
    private RectangleF _remoteHit;
    private RectangleF _remoteBaseHit;
    private RectangleF _localHit;
    private RectangleF _placeholderHit;
    private bool _draggingRemote;
    private Point _dragOrigin;
    private Point _dragCurrent;
    private IReadOnlyList<EnvironmentDisplayTile> _environmentDevices =
        Array.Empty<EnvironmentDisplayTile>();
    private readonly List<(RectangleF Hit,int TargetId)> _environmentHits = new();
    private readonly Dictionary<string,PointF> _memberOffsets =
        new(StringComparer.Ordinal);
    private int _draggingMemberId=int.MinValue;
    private RectangleF _draggingMemberHit;
    private float _layoutScale=1f;
    private System.Drawing.Point _hubWorldOrigin;
    private readonly Dictionary<int,Rectangle> _worldMemberRects=new();
    internal bool LocalIsEnvironmentHub { get; set; } = true;
    internal static Rectangle ResolveMemberWorld(Rectangle defaultWorld,
        Rectangle hubBounds,PointF? canonical,PointF? legacy)
    {
        if(canonical is { } p)
            return new Rectangle(
                hubBounds.Left+(int)Math.Round(p.X),
                hubBounds.Top+(int)Math.Round(p.Y),
                defaultWorld.Width,defaultWorld.Height);
        if(legacy is { } old)
        {
            defaultWorld.Offset((int)Math.Round(old.X),
                (int)Math.Round(old.Y));
        }
        return defaultWorld;
    }
    internal event Action? EnvironmentArrangementChanged;
    internal event Action<string,PointF>? EnvironmentMemberMoved;

    internal IReadOnlyDictionary<string,PointF> StoredMemberOffsets =>
        new Dictionary<string,PointF>(_memberOffsets,StringComparer.Ordinal);
    internal void RestoreMemberOffsets(IReadOnlyDictionary<string,PointF> offsets)
    {
        _memberOffsets.Clear();
        foreach(var pair in offsets)
            if(pair.Key.Length is > 0 and < 150 &&
                float.IsFinite(pair.Value.X) && float.IsFinite(pair.Value.Y) &&
                Math.Abs(pair.Value.X)<90000 && Math.Abs(pair.Value.Y)<90000)
                _memberOffsets[pair.Key]=pair.Value;
        Invalidate();
    }
    internal IReadOnlyList<(int TargetId,RectangleF Hit)> OnlineHitRegions =>
        _environmentHits.Select(x=>(x.TargetId,x.Hit)).ToArray();

    internal int? AdjacentTarget(int currentId,MonitorEdge direction,
        int baseTargetId,double fraction=0.5)
    {
        // Only the ACTUAL shared screen edge can grant a transition. A screen
        // somewhere in a direction is not a neighbor. Treat the phone's drag
        // rectangle and both Windows monitor groups in the same diagram.
        var regions=new List<(int Id,RectangleF Bounds)> {
            (EnvironmentRouteOrder.Local,_localHit),(baseTargetId,_remoteHit)
        };
        foreach(var item in _environmentHits)
            if(_environmentDevices.Any(d=>d.TargetId==item.TargetId &&
                d.SupportsInput))
                regions.Add((item.TargetId,item.Hit));
        return SpatialNeighbor(regions,currentId,direction,fraction);
    }

    internal static int? SpatialNeighbor(
        IReadOnlyList<(int Id,RectangleF Bounds)> regions,
        int currentId,MonitorEdge direction,double fraction=0.5)
    {
        var origin=regions.FirstOrDefault(r=>r.Id==currentId);
        if(origin.Bounds.Width<=0||origin.Bounds.Height<=0)return null;
        var bounds=origin.Bounds;
        fraction=Math.Clamp(double.IsFinite(fraction)?fraction:0.5,0,1);
        bool horizontal=direction is MonitorEdge.Left or MonitorEdge.Right;
        float cross=(float)(horizontal
            ?bounds.Top+fraction*bounds.Height
            :bounds.Left+fraction*bounds.Width);
        int? best=null;
        double nearest=double.PositiveInfinity;
        foreach(var item in regions)
        {
            if(item.Id==currentId || item.Bounds.Width<=0 ||
                item.Bounds.Height<=0)continue;
            var target=item.Bounds;
            // Positive gap is space BETWEEN the two edges; negative gap
            // means overlapping screens. Neither a distant display nor
            // an overlay dropped into the middle of another is a route.
            double gap=direction switch {
                MonitorEdge.Right=>target.Left-bounds.Right,
                MonitorEdge.Left=>bounds.Left-target.Right,
                MonitorEdge.Bottom=>target.Top-bounds.Bottom,
                MonitorEdge.Top=>bounds.Top-target.Bottom,
                _=>double.PositiveInfinity
            };
            double allowance=Math.Max(12,Math.Min(42,
                Math.Min(horizontal?bounds.Width:bounds.Height,
                    horizontal?target.Width:target.Height)*0.19));
            if(gap < -4 || gap>allowance)continue;
            float from=horizontal?target.Top:target.Left;
            float to=horizontal?target.Bottom:target.Right;
            // A pointer crossing the TOP part of one screen must not leap
            // to a phone attached to the bottom part of another screen.
            float tolerance=2f;
            if(cross < from-tolerance || cross>to+tolerance)continue;
            double distance=Math.Max(0,gap);
            if(distance<nearest){nearest=distance;best=item.Id;}
        }
        return best;
    }

    internal IReadOnlyList<int> ArrangedTargetOrder(int baseTargetId)
    {
        // A physical screen is at most one input destination; all its native
        // monitors remain inside the same peer and aren't separate pairings.
        var arranged=new List<(int Id,float Center)> {
            (EnvironmentRouteOrder.Local,_localHit.Left+_localHit.Width/2),
            (baseTargetId,_remoteHit.Left+_remoteHit.Width/2)
        };
        foreach(var (hit,id) in _environmentHits)
            if(_environmentDevices.Any(d=>d.TargetId==id && d.SupportsInput))
                arranged.Add((id,hit.Left+hit.Width/2));
        return arranged.OrderBy(item=>item.Center).Select(item=>item.Id)
            .Distinct().ToArray();
    }

    // This is an authenticated, online-only snapshot of the same environment.
    // A phone is never fabricated from LAN discovery or merely remembered trust.
    internal IReadOnlyList<EnvironmentDisplayTile> EnvironmentDevices
    {
        get => _environmentDevices;
        set {
            _environmentDevices=value?.ToArray() ?? Array.Empty<EnvironmentDisplayTile>();
            Invalidate();
        }
    }
    internal event Action<int>? EnvironmentDeviceSelected;
    internal static IReadOnlyList<EnvironmentDisplayTile> ProjectEnvironment(
        IEnumerable<EnvironmentDisplayTile> online,int selectedTargetId)
    {
        return online.Where(p=>p.TargetId!=selectedTargetId)
            .GroupBy(p=>p.TargetId).Select(g=>g.First())
            .OrderBy(p=>p.IsAndroid?1:0)
            .ThenBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
        Name = "DisplayDiagram";
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

    internal static RectangleF SnapToConnectedScreen(RectangleF proposed,
        IReadOnlyList<RectangleF> otherScreens)
    {
        // A screen dropped inside another screen has no meaningful border.
        // Snap to the nearest FREE exterior edge (all four directions).
        // This also makes a dragged phone reachable by cursor movement.
        RectangleF best=proposed;
        double distance=double.PositiveInfinity;
        foreach(var anchor in otherScreens)
        {
            if(anchor.Width<1 || anchor.Height<1)continue;
            const float gap=8f;
            foreach(var side in new[]{MonitorEdge.Left,MonitorEdge.Right,
                MonitorEdge.Top,MonitorEdge.Bottom})
            {
                float x=proposed.X,y=proposed.Y;
                float centerX=Math.Clamp(proposed.Left+proposed.Width/2,
                    anchor.Left+3,anchor.Right-3);
                float centerY=Math.Clamp(proposed.Top+proposed.Height/2,
                    anchor.Top+3,anchor.Bottom-3);
                switch(side)
                {
                    case MonitorEdge.Left: x=anchor.Left-proposed.Width-gap;
                        y=centerY-proposed.Height/2;break;
                    case MonitorEdge.Right: x=anchor.Right+gap;
                        y=centerY-proposed.Height/2;break;
                    case MonitorEdge.Top: y=anchor.Top-proposed.Height-gap;
                        x=centerX-proposed.Width/2;break;
                    case MonitorEdge.Bottom:y=anchor.Bottom+gap;
                        x=centerX-proposed.Width/2;break;
                }
                var candidate=new RectangleF(x,y,proposed.Width,proposed.Height);
                if(otherScreens.Any(other=>
                    RectangleF.Intersect(candidate,other).Width>2 &&
                    RectangleF.Intersect(candidate,other).Height>2))continue;
                double dx=candidate.X-proposed.X,dy=candidate.Y-proposed.Y;
                double cost=dx*dx+dy*dy;
                if(cost<distance){distance=cost;best=candidate;}
            }
        }
        return best;
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
        _environmentHits.Clear();
        _worldMemberRects.Clear();

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

        // All authenticated screens belong in the SAME display arrangement.
        // Their preview rectangles are visual placements only: Android is
        // never registered as a native Windows monitor or virtual driver.
        var core = Rectangle.Union(l, remotePos);
        // Both Windows hosts use the SAME physical SURAJ hub screen origin.
        // Local/remote Windows screen preview origins can otherwise differ
        // even when every peer has received the identical drag offset.
        var hubBounds=LocalIsEnvironmentHub ? l : remotePos;
        _hubWorldOrigin=hubBounds.Location;
        var extras=ComposeInlineDevices(core,_environmentDevices)
            .Select(extra=>{
                PointF? canonical=_memberOffsets.TryGetValue(
                    extra.Device.CanonicalLayoutKey,out var position)
                    ? position:null;
                PointF? legacy=_memberOffsets.TryGetValue(
                    extra.Device.LayoutKey,out var old) ? old:null;
                var moved=ResolveMemberWorld(extra.World,hubBounds,
                    canonical,legacy);
                _worldMemberRects[extra.Device.TargetId]=moved;
                return (extra.Device,World:moved);
            }).ToArray();
        var full = core;
        foreach(var additional in extras)
            full = Rectangle.Union(full,additional.World);

        const float pad = 36;
        float scale = Math.Min(Math.Max(70, Width - pad * 2) / Math.Max(1, full.Width),
            Math.Max(60, Height - pad * 2) / Math.Max(1, full.Height));
        scale = Math.Max(.001f,scale);
        _layoutScale=scale;
        float offsetX = (Width - full.Width * scale) / 2f;
        float offsetY = (Height - full.Height * scale) / 2f;

        _localHit = Fit(l,full,scale,offsetX,offsetY);
        int localOrdinal = 0;
        foreach (var m in _local.Monitors.OrderBy(m => m.Primary ? 0 : 1))
        {
            var tile = Fit(m.Bounds, full, scale, offsetX, offsetY);
            DrawScreen(g, tile, LocalDeviceName,
                DisplayNumber(m, ++localOrdinal), m.Primary, true);
        }

        if (_remote is { } remote)
        {
            var group = Fit(remotePos, full, scale, offsetX, offsetY);
            _remoteBaseHit = group;
            if (_draggingRemote)
            {
                group.X += _dragCurrent.X - _dragOrigin.X;
                group.Y += _dragCurrent.Y - _dragOrigin.Y;
            }
            _remoteHit = group;
            int remoteOrdinal = 0;
            foreach (var m in remote.Monitors.OrderBy(m => m.Primary ? 0 : 1))
            {
                var rect = new Rectangle(
                    remotePos.Left + m.X - remote.Bounds.Left,
                    remotePos.Top + m.Y - remote.Bounds.Top, m.Width, m.Height);
                var tile = Fit(rect, full, scale, offsetX, offsetY);
                if (_draggingRemote)
                    tile.Offset(_dragCurrent.X - _dragOrigin.X, _dragCurrent.Y - _dragOrigin.Y);
                DrawScreen(g, tile, RemoteDeviceName,
                    DisplayNumber(m, ++remoteOrdinal), m.Primary, false);
            }
        }
        else if(extras.Length == 0)
        {
            _placeholderHit = Fit(remotePos, full, scale, offsetX, offsetY);
            DrawPlaceholder(g, _placeholderHit);
        }

        // Extra Windows PCs and Android phones use the exact same screen
        // tiles/labels as native and selected displays, never a footer/deck.
        foreach(var extra in extras)
        {
            var group = Fit(extra.World,full,scale,offsetX,offsetY);
            if(extra.Device.TargetId==_draggingMemberId)
                group.Offset(_dragCurrent.X-_dragOrigin.X,
                    _dragCurrent.Y-_dragOrigin.Y);
            int count=Math.Clamp(extra.Device.ScreenCount,1,8);
            float gap=Math.Min(7,group.Width / Math.Max(3,count*8));
            float screenWidth=(group.Width-gap*(count-1))/count;
            for(int index=0;index<count;index++)
            {
                var screen=new RectangleF(group.Left+(screenWidth+gap)*index,
                    group.Top,screenWidth,group.Height);
                DrawScreen(g,screen,extra.Device.Name,index+1,false,false,
                    extra.Device.IsAndroid?"Android":null);
            }
            _environmentHits.Add((group,extra.Device.TargetId));
        }
    }

    internal static IReadOnlyList<(EnvironmentDisplayTile Device,Rectangle World)>
        ComposeInlineDevices(Rectangle mainScreenArea,
            IReadOnlyList<EnvironmentDisplayTile> members)
    {
        var result=new List<(EnvironmentDisplayTile Device,Rectangle World)>();
        // Connection tiles represent REACHABLE screen neighbors, not distant
        // floating thumbnails. The previous 90+ world-pixel gap could exceed
        // SpatialNeighbor's (scale-dependent) allowed crossing distance on
        // wide Displays windows, especially for narrow portrait phones.
        // Use a small stable world-space seam so every default connected
        // screen can be reached without the user manually dragging it first.
        int connectedGap=Math.Clamp(mainScreenArea.Height/140,8,24);
        int nextX=mainScreenArea.Right+connectedGap;
        foreach(var device in members)
        {
            int count=Math.Clamp(device.ScreenCount,1,8);
            int height=Math.Max(380,
                (int)(mainScreenArea.Height * (device.IsAndroid ? 0.88 : 0.79)));
            int width=device.IsAndroid
                ? Math.Max(300,(int)(height*.64))
                : Math.Max(420,(int)(height*.95*count));
            var preview=new Rectangle(nextX,
                mainScreenArea.Top+(mainScreenArea.Height-height)/2,width,height);
            result.Add((device,preview));
            nextX=preview.Right+connectedGap;
        }
        return result;
    }

    private static void DrawPlaceholder(Graphics g, RectangleF r)
    {
        using var pen = new Pen(Color.FromArgb(100, 135, 151), 1.4f)
            { DashStyle = DashStyle.Dash };
        using var bg = new SolidBrush(Color.FromArgb(39, 42, 49));
        using var path = Rounded(r, 10);
        g.FillPath(bg, path);
        g.DrawPath(pen, path);
        DrawCenter(g, "+  Add devices", r, Brushes.LightGray, 12);
    }

    private static void DrawScreen(Graphics g, RectangleF rect, string deviceName,
        int displayNumber, bool primary, bool local, string? platform = null)
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
        if (rect.Width < 38 || rect.Height < 28) return;
        float labelHeight = platform=="Android"
            ? Math.Min(52,Math.Max(26,rect.Height*.36f))
            : Math.Min(27, Math.Max(13, rect.Height * .23f));
        var title = new RectangleF(rect.Left + 4, rect.Top + 3,
            Math.Max(1, rect.Width - 8), labelHeight);
        using var nameFont = new Font("Segoe UI Semibold",
            Math.Clamp(Math.Min(rect.Width / 12f, rect.Height / 8f), 7, 11),
            FontStyle.Bold);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = platform=="Android"
                ? StringTrimming.EllipsisWord : StringTrimming.EllipsisCharacter,
            FormatFlags = platform=="Android"
                ? (StringFormatFlags)0 : StringFormatFlags.NoWrap
        };
        g.DrawString(deviceName, nameFont, Brushes.White, title, format);
        var numberTile = new RectangleF(rect.Left + 4,
            Math.Min(rect.Bottom - 17, rect.Top + labelHeight + 5),
            Math.Max(1, rect.Width - 8),
            Math.Min(15, Math.Max(1, rect.Height - labelHeight - 7)));
        if (numberTile.Height >= 9)
            DrawCenter(g, platform == "Android"
                ? $"Android · {displayNumber}" : $"Display {displayNumber}",
                numberTile, Brushes.WhiteSmoke, Math.Clamp(rect.Width / 19, 7, 9));
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
        foreach(var item in _environmentHits)
        {
            if(item.Hit.Contains(e.Location))
            {
                // Treat a press as the START of a native-monitor-style drag.
                // A tap on release still selects the member; dragging never
                // tears down an input session or requires another pairing.
                _draggingMemberId=item.TargetId;
                _draggingMemberHit=item.Hit;
                _dragOrigin=_dragCurrent=e.Location;
                Capture=true;
                Cursor=Cursors.SizeAll;
                return;
            }
        }
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
        if (_draggingMemberId!=int.MinValue)
        {
            var delta=ConstrainDrag(_draggingMemberHit,ClientSize,
                new Point(e.X-_dragOrigin.X,e.Y-_dragOrigin.Y));
            _dragCurrent=new Point(_dragOrigin.X+delta.X,_dragOrigin.Y+delta.Y);
            Invalidate();
        }
        else if (_draggingRemote)
        {
            _dragCurrent = ClampedDragPoint(e.Location);
            Invalidate();
        }
        else Cursor = _environmentHits.Any(item=>item.Hit.Contains(e.Location))
            ? Cursors.SizeAll
            : _remote != null && _remoteHit.Contains(e.Location)
                ? Cursors.SizeAll
                : _remote == null && _placeholderHit.Contains(e.Location)
                    ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(_draggingMemberId!=int.MinValue)
        {
            int id=_draggingMemberId;
            int memberDeltaX=_dragCurrent.X-_dragOrigin.X;
            int memberDeltaY=_dragCurrent.Y-_dragOrigin.Y;
            _draggingMemberId=int.MinValue;
            Capture=false;
            Cursor=Cursors.Default;
            if(Math.Max(Math.Abs(memberDeltaX),Math.Abs(memberDeltaY)) < 8)
                EnvironmentDeviceSelected?.Invoke(id);
            else
            {
                var member=_environmentDevices.FirstOrDefault(d=>d.TargetId==id);
                if(member!=null)
                {
                    string key=member.CanonicalLayoutKey;
                    if(!_worldMemberRects.TryGetValue(id,out var memberWorld))
                        return;
                    var wanted=new RectangleF(_draggingMemberHit.X+memberDeltaX,
                        _draggingMemberHit.Y+memberDeltaY,
                        _draggingMemberHit.Width,_draggingMemberHit.Height);
                    var occupied=new List<RectangleF>{_localHit};
                    if(_remoteHit.Width>0)occupied.Add(_remoteHit);
                    foreach(var other in _environmentHits)
                        if(other.TargetId!=id)occupied.Add(other.Hit);
                    var snapped=SnapToConnectedScreen(wanted,occupied);
                    float scale=Math.Max(.001f,_layoutScale);
                    // Convert the FINAL snapped viewport position back into
                    // world units relative to the one hub reference. Never
                    // publish a local view's change-from-default offset.
                    var position=new PointF(
                        Math.Clamp(memberWorld.Left-
                            _hubWorldOrigin.X+
                            (snapped.X-_draggingMemberHit.X)/scale,
                            -80000,80000),
                        Math.Clamp(memberWorld.Top-
                            _hubWorldOrigin.Y+
                            (snapped.Y-_draggingMemberHit.Y)/scale,
                            -80000,80000));
                    _memberOffsets.Remove(member.LayoutKey);
                    _memberOffsets[key]=position;
                    EnvironmentArrangementChanged?.Invoke();
                    EnvironmentMemberMoved?.Invoke(key,_memberOffsets[key]);
                }
            }
            Invalidate();
            return;
        }
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
        if(_draggingMemberId!=int.MinValue && !Capture)
        {
            _draggingMemberId=int.MinValue;
            Cursor=Cursors.Default;
            Invalidate();
        }
        if (_draggingRemote && !Capture)
        {
            _draggingRemote = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        if(_draggingMemberId!=int.MinValue)
        {
            _draggingMemberId=int.MinValue;
            Capture=false;
        }
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
