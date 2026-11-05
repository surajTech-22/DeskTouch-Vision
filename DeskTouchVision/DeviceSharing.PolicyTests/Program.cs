using DeskTouch.DeviceShare;

// Windows Wi-Fi roaming must tolerate brief scheduling transitions without
// holding an abandoned authenticated lease indefinitely.
if (ReceiverServer.PeerIdleGraceSeconds < 40 ||
    ReceiverServer.PeerIdleGraceSeconds > 65)
    throw new Exception("Windows heartbeat idle grace is outside the safe window");

static void Check(bool test, string message)
{
    if (!test) throw new Exception("Input priority regression: " + message);
}

// Native Windows DISPLAYn identifiers must be stable PER computer. The
// previous UI incorrectly numbered every PC and phone in one running sequence.
Check(DisplayArrangementControl.DisplayNumber(new MonitorRect(
    @"\\.\DISPLAY2", 0, 0, 1920, 1080, true), 1) == 2,
    "Windows display 2 must remain number 2, even when primary.");
Check(DisplayArrangementControl.DisplayNumber(new MonitorRect(
    @"\\.\DISPLAY1", 0, 0, 1920, 1080, true), 8) == 1,
    "Remote Windows monitor numbering must not inherit local count.");
Check(DisplayArrangementControl.DisplayNumber(new MonitorRect(
    "Windows screen", 0, 0, 1080, 2400, true), 1) == 1,
    "Another Windows computer starts monitor numbering at one.");
Check(DisplayArrangementControl.CleanDeviceName("PC\r\nName", "Unknown") ==
    "PC  Name", "Device title must not introduce newlines.");
Console.WriteLine("PASS: native Windows display numbering and safe computer names.");
var testDisplay = new MonitorTopology(new[] {
    new MonitorRect("Remote Windows screen",0,0,1920,1080,true)
});
Check(ReceiverServer.AllowsForwardInput(new ConnectedPeer("Desktop",testDisplay,
    SupportsReverseInput:true,Platform:"Windows")),
    "Windows PC must retain duplex keyboard/mouse authority");
Check(!ReceiverServer.AllowsForwardInput(new ConnectedPeer("Unsupported peer",testDisplay,
    SupportsReverseInput:true,Platform:"Android")),
    "A legacy mobile peer must never claim Windows input");
var legacyHello=System.Text.Json.JsonSerializer.Serialize(new PeerHello(
    "Unsupported device",testDisplay.ToWire(),Platform:"Android"));
bool oldClientRejected=false;
try { ReceiverServer.ReadHello(legacyHello); }
catch(InvalidDataException) { oldClientRejected=true; }
Check(oldClientRejected,"Windows server must reject Android during authenticated hello");
var currentHello=System.Text.Json.JsonSerializer.Serialize(new PeerHello(
    "Windows PC",testDisplay.ToWire(),Platform:"Windows"));
Check(ReceiverServer.ReadHello(currentHello).Platform=="Windows",
    "Authenticated Windows hello must remain available");
Console.WriteLine("PASS: Windows-only input authority and Android rejection at handshake.");

var fpOne = new string('A',64);
var fpTwo = new string('B',64);
var local1=System.Net.IPAddress.Parse("192.168.1.11");
var local2=System.Net.IPAddress.Parse("192.168.1.12");
var discoveredOne=NearbyComputerDiscovery.ParseReply("DT1-READY|PC1|"+fpOne,local1);
var discoveredTwo=NearbyComputerDiscovery.ParseReply("DT1-BUSY|PC2|"+fpTwo,local2);
Check(discoveredOne != null && discoveredOne.CanReceive &&
      discoveredTwo != null && !discoveredTwo.CanReceive,
    "Windows LAN discovery must show available and busy peers accurately");
Check(NearbyComputerDiscovery.ParseReply("DT1-MOBILE|Legacy|"+fpOne,local1)==null,
    "Windows-only discovery must reject obsolete mobile beacons");
var threeDevices=NearbyComputerDiscovery.DistinctDevices(new[]{
    discoveredOne!, discoveredTwo!, discoveredOne!});
Check(threeDevices.Count==2,"Duplicate Windows beacon must not create duplicate monitor");
var discoveredHub=new NearbyComputer("HUB","192.168.1.25",
    new string('D',64),CanReceive:true,IsHub:true);
var discoveredLeaf=new NearbyComputer("LEAF","192.168.1.26",
    new string('E',64),CanReceive:true,IsHub:false);
var sortedEnvironment=NearbyComputerDiscovery.DistinctDevices(
    new[]{discoveredLeaf,discoveredHub});
Check(sortedEnvironment[0].IsHub && sortedEnvironment[0].Name=="HUB",
    "LAN discovery must prefer the existing hub to avoid split sessions");
Check(NearbyComputerDiscovery.ParseReply("DT1-READY|Fake|BAD",local1)==null,
    "Invalid discovery fingerprint must be rejected");
Check(WindowsStartup.Command(@"C:\\Program Files\\DeskTouch\\DeskTouch.DeviceShare.exe")
    .EndsWith("\" --tray",StringComparison.Ordinal),
    "Windows startup must use tray mode");
Console.WriteLine("PASS: Windows-only discovery, hub priority, deduplication, startup.");

// Screen-edge handoff uses the existing authenticated Windows environment
// rather than opening a new connection whenever a cursor crosses screens.
var inlinePeer=new EnvironmentDisplayTile(-5,"WORKSTATION-B","Windows",1,true);
var offlinePeer=new EnvironmentDisplayTile(-6,"Busy workstation","Windows",1,false);
var leafRoute=new EnvironmentRouteOrder(0,new[]{inlinePeer,offlinePeer},MonitorEdge.Left);
Check(leafRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Left)==0 &&
      leafRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Right)==-5,
    "Windows leaf must route between hub and next authenticated PC");
Check(leafRoute.Neighbor(-5,MonitorEdge.Left)==EnvironmentRouteOrder.Local &&
      leafRoute.Neighbor(0,MonitorEdge.Right)==EnvironmentRouteOrder.Local,
    "Crossing back from either Windows PC returns to source");
Check(!leafRoute.Order.Contains(-6) &&
      leafRoute.Neighbor(-5,MonitorEdge.Right)==null,
    "Unavailable Windows input target must never steal handoff");
var hubRoute=new EnvironmentRouteOrder(7,
    new[]{new EnvironmentDisplayTile(8,"WORKSTATION-C","Windows",1,true)},
    MonitorEdge.Right);
Check(hubRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Right)==7 &&
      hubRoute.Neighbor(7,MonitorEdge.Right)==8 &&
      hubRoute.Neighbor(8,MonitorEdge.Left)==7,
    "Three Windows PCs must hand off within one persistent environment");
Check(SourceCapture.ShouldDropDuplicateRaw(1000,1020) &&
    !SourceCapture.ShouldDropDuplicateRaw(1000,1040) &&
    !SourceCapture.ShouldDropDuplicateRaw(0,1030),
    "Windows touchpad/raw input must not double count the same movement");
Console.WriteLine("PASS: environment-wide edge routing plus raw input duplicate suppression.");

Check(SourceCapture.ScaleRawDelta(1, 10) >= 1,
    "Slow physical mouse movement must remain visible on the remote PC");
Check(SourceCapture.ScaleRawDelta(10, 10) > 10,
    "Remote Windows mouse should honor native sensitivity instead of raw slow deltas");
Check(SourceCapture.ScaleRawDelta(10, 20) >
    SourceCapture.ScaleRawDelta(10, 10),
    "Windows pointer speed settings must affect remote movement");
Check(SourceCapture.ScaleRawDelta(0, 10) == 0,
    "Idle mouse must not generate artificial movement");
Console.WriteLine("PASS: speed-aware Windows raw mouse scaling preserves slow movements.");

// An idle, discoverable DeskTouch listener must never install global input
// hooks or grant remote authority. Only a connected, monitored peer can claim.
Check(!InputClaimLifecyclePolicy.CanGrant(false, false, false),
    "No remote input authority is permitted while no peer is paired.");
Check(!InputClaimLifecyclePolicy.CanGrant(false, true, false),
    "A stale remote-active flag cannot bypass inactive physical monitoring.");
Check(InputClaimLifecyclePolicy.CanGrant(true, false, false),
    "An authenticated monitored peer may request input authority.");
Check(InputClaimLifecyclePolicy.CanGrant(true, true, false),
    "Independent Android-to-hub and hub-to-PC input grants must coexist safely.");
Check(!PhysicalInputMonitor.IsLocallyDirected(true) &&
    PhysicalInputMonitor.IsLocallyDirected(false),
    "Physical input forwarded to another device cannot revoke a separate hub controller");
Check(!InputClaimLifecyclePolicy.CanGrant(true, false, true),
    "Forward remote claim must be refused during pending reverse handoff.");
Console.WriteLine("PASS: discovery-only startup cannot grant input; global monitoring required for sharing.");

long fakeClock = 100000;
var priority = new LocalInputPriority(() => fakeClock);
int nativePreemptions = 0;
priority.PhysicalOverride += () => nativePreemptions++;

Check(!priority.RemoteGranted, "Local should be owner when app starts");
Check(!priority.TryClaim(), "Cannot take control immediately after launching app");

fakeClock += LocalInputPriority.IdleBeforeRemoteMilliseconds;
Check(priority.TryClaim(), "Idle receiver must allow explicit claim");
Check(priority.CanApplyRemote, "A granted remote peer may inject input");
Check(priority.TryClaim(), "Re-claim while granted must not revoke");

priority.RecordPhysicalActivity();
Check(!priority.RemoteGranted, "Physical keyboard/mouse must instantly revoke remote");
Check(!priority.CanApplyRemote, "Remote input must be rejected after native event");
Check(nativePreemptions == 1, "Native override must notify exactly once");

priority.RecordPhysicalActivity();
Check(nativePreemptions == 1, "Repeated physical movement must not flood revoke callbacks");
Check(!priority.TryClaim(), "Must reject reclaim while local keyboard/mouse active");

fakeClock += LocalInputPriority.IdleBeforeRemoteMilliseconds - 1;
Check(!priority.TryClaim(), "Must wait until idle threshold");
fakeClock++;
Check(priority.TryClaim(), "Should permit a new deliberate claim after idleness");
Check(priority.CanApplyRemote, "New remote session permitted only after claim");
priority.ReleaseRemote();
Check(!priority.CanApplyRemote, "Releasing control must keep local authority");

// Windows Extended Desktop seams must take precedence over DeskTouch edges.
var extended = new MonitorTopology(new[]
{
    new MonitorRect(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
    new MonitorRect(@"\\.\DISPLAY2", 1920, 0, 2560, 1440, false)
});
Check(!extended.IsExternalEdge(new System.Drawing.Point(1919, 600), MonitorEdge.Right),
    "Native Windows monitor seam must NOT switch to another computer");
Check(extended.IsExternalEdge(new System.Drawing.Point(4479, 600), MonitorEdge.Right),
    "Outermost right Windows monitor should switch");
Check(extended.IsExternalEdge(new System.Drawing.Point(0, 600), MonitorEdge.Left),
    "Outer left Windows boundary should switch");
Check(extended.IsExternalEdge(new System.Drawing.Point(300, 0), MonitorEdge.Top),
    "Outermost top Windows boundary should switch");
Check(extended.IsExternalEdge(new System.Drawing.Point(300, 1079), MonitorEdge.Bottom),
    "Outermost bottom Windows boundary should switch");
Check(extended.EntryPoint(MonitorEdge.Left, 0.5).X == 0,
    "Receiver must map entrance to actual leftmost physical monitor");
Check(extended.EntryPoint(MonitorEdge.Right, 0.5).X == 4479,
    "Receiver must map entrance to actual rightmost physical monitor");

var nonrectangular = new MonitorTopology(new[]
{
    new MonitorRect("A", -1920, -500, 1920, 1080, true),
    new MonitorRect("B", 0, 0, 2560, 1440, false)
});
var mapped = nonrectangular.ClampToRealMonitor(new System.Drawing.Point(120, -400));
Check(nonrectangular.MonitorAt(mapped) != null,
    "Mapped remote cursor must never land in unoccupied virtual-screen gaps");

// A saturated cursor can stop generating ordinary WM_MOUSEMOVE, but raw
// mouse motion continues. The new handoff policy must still recognize a
// *deliberate outward push* within a generous physical-pixel edge band.
Check(EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(4479, 600), MonitorEdge.Right, 9, 0),
    "Right-edge RAW motion must hand off while Windows clamps the cursor");
Check(EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(4468, 600), MonitorEdge.Right, 9, 0),
    "Fast mouse may hand off shortly before its final screen pixel");
Check(!EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(4448, 600), MonitorEdge.Right, 9, 0),
    "Handoff must not trigger far from an outer boundary");
Check(!EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(4479, 600), MonitorEdge.Right, -2, 0),
    "Moving inward at the boundary must never switch computers");
Check(!EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(4479, 600), MonitorEdge.Right, 0, 0),
    "Resting at the boundary must never switch computers");
Check(!EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(1919, 600), MonitorEdge.Right, 8, 0),
    "Native Windows display seams must never hand off to a remote PC");
Check(EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(0, 900), MonitorEdge.Left, -5, 0),
    "Left screen boundary must recognize outward motion");
Check(EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(250, 0), MonitorEdge.Top, 0, -5),
    "Top screen boundary must recognize outward motion");
Check(EdgeHandoffPolicy.ShouldRequest(extended,
    new System.Drawing.Point(250, 1079), MonitorEdge.Bottom, 0, 5),
    "Bottom screen boundary must recognize outward motion");
Check(EdgeHandoffPolicy.ShouldRequest(nonrectangular,
    new System.Drawing.Point(-1920, -450), MonitorEdge.Left, -3, 0),
    "Negative-coordinate multi-monitor layouts must recognize edges");

// Slow physical mouse movement can end with the cursor clamped at the
// final Windows pixel. No further WM_MOUSEMOVE is guaranteed, so a recent
// outward approach must survive until a short UI timer tick completes it.
var slow = new SlowEdgeApproach();
slow.Observe(extended, new System.Drawing.Point(4464, 600),
    MonitorEdge.Right, 1, 0, 1000);
Check(!slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 1100), "Slow edge must not switch immediately on hover");
Check(slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 1190), "Slow outward movement must switch when cursor is clamped");
Check(!slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 1240), "Slow edge intent is consumed once, not a repeated hover trigger");
slow.Observe(extended, new System.Drawing.Point(4466, 600),
    MonitorEdge.Right, 1, 0, 2000);
slow.Observe(extended, new System.Drawing.Point(4464, 600),
    MonitorEdge.Right, -2, 0, 2010);
Check(!slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 2400), "Moving back inward cancels edge handoff intent");
slow.Observe(extended, new System.Drawing.Point(4466, 600),
    MonitorEdge.Right, 1, 0, 3000);
Check(!slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 4500), "Stale edge intent cannot switch after a pause");
slow.Observe(extended, new System.Drawing.Point(1919, 600),
    MonitorEdge.Right, 1, 0, 6000);
Check(!slow.Ready(extended, new System.Drawing.Point(1919, 600),
    MonitorEdge.Right, 6250), "Internal Windows extended-monitor seam must never switch");
slow.Observe(extended, new System.Drawing.Point(3, 600),
    MonitorEdge.Left, -1, 0, 7000);
Check(slow.Ready(extended, new System.Drawing.Point(0, 600),
    MonitorEdge.Left, 7230), "Slow outward left crossing must switch");
slow.Observe(extended, new System.Drawing.Point(300, 4),
    MonitorEdge.Top, 0, -1, 8000);
Check(slow.Ready(extended, new System.Drawing.Point(300, 0),
    MonitorEdge.Top, 8200), "Slow outward top crossing must switch");
slow.Observe(extended, new System.Drawing.Point(300, 1075),
    MonitorEdge.Bottom, 0, 1, 9000);
Check(slow.Ready(extended, new System.Drawing.Point(300, 1079),
    MonitorEdge.Bottom, 9200), "Slow outward bottom crossing must switch");
slow.Reset();
Check(!slow.Ready(extended, new System.Drawing.Point(4479, 600),
    MonitorEdge.Right, 9300), "Resetting session removes all edge intent");
Console.WriteLine("PASS: slow-pixel edge handoff with Windows cursor clamp, four directions, intent expiry and native seams.");

Check(DisplaySettingsForm.ShouldStayInTray(
    System.Windows.Forms.CloseReason.UserClosing, false),
    "Clicking the user X button must stay in tray and preserve pairing");
Check(!DisplaySettingsForm.ShouldStayInTray(
    System.Windows.Forms.CloseReason.UserClosing, true),
    "Explicit tray Exit must close and disconnect");
Check(!DisplaySettingsForm.ShouldStayInTray(
    System.Windows.Forms.CloseReason.WindowsShutDown, false),
    "Windows shutdown must release hooks and close cleanly");
Check(!DisplaySettingsForm.ShouldStayInTray(
    System.Windows.Forms.CloseReason.ApplicationExitCall, false),
    "Verified update or application exit must release sharing input hooks");
Console.WriteLine("PASS: user Close hides to tray; intentional Exit/update shuts down.");

var monitorTile = new System.Drawing.RectangleF(640, 130, 130, 85);
var rightClamped = DisplayArrangementControl.ConstrainDrag(
    monitorTile, new System.Drawing.Size(800, 350), new System.Drawing.Point(300, 0));
Check(rightClamped.X == 22,
    "Dragging a connected PC beyond the right window edge must keep it visible");
var leftClamped = DisplayArrangementControl.ConstrainDrag(
    monitorTile, new System.Drawing.Size(800, 350), new System.Drawing.Point(-1000, 0));
Check(leftClamped.X == -632,
    "Dragging a remote screen beyond the left window edge must keep it visible");
var bottomClamped = DisplayArrangementControl.ConstrainDrag(
    monitorTile, new System.Drawing.Size(800, 350), new System.Drawing.Point(0, 1000));
Check(bottomClamped.Y == 127,
    "Dragging past bottom must not hide the connected screen group");
Console.WriteLine("PASS: clipped-pointer raw edge handoff, four outward directions, internal seams and UI drag bounds.");

Console.WriteLine("PASS: Windows native priority, two-monitor seams, all four outer edges, and nonrectangular monitor layouts.");


// Real TLS/auth/monitor-mapping integration test on loopback, no remote inputs.
{
    var status = new System.Collections.Concurrent.ConcurrentQueue<string>();
    // Monotonic time is controllable for authority permission tests; no
    // physical mouse or keyboard is disabled on the Windows CI runner.
    long authorityClock = 10000;
    var receiverPriority = new LocalInputPriority(() => Interlocked.Read(ref authorityClock));
    var hostPriority = new LocalInputPriority(() => Interlocked.Read(ref authorityClock));
    using var receiver = new ReceiverServer(message => status.Enqueue(message),
        receiverPriority, (_, _) => Task.FromResult(true));
    var connected = new TaskCompletionSource<ConnectedPeer>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var remoteArrangement = new TaskCompletionSource<MonitorEdge>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var remoteRefresh = new TaskCompletionSource<ConnectedPeer>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.PeerConnected += peer => connected.TrySetResult(peer);
    receiver.PeerPlacementChanged += edge => remoteArrangement.TrySetResult(edge);
    receiver.PeerTopologyChanged += peer => remoteRefresh.TrySetResult(peer);
    receiver.PeerDisconnected += () => ended.TrySetResult(true);
    receiver.Start();
    using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try
    {
        using var session = await HostSession.ConnectWithApprovalAsync(
            "127.0.0.1", receiver.Fingerprint, message => status.Enqueue(message),
            testTimeout.Token);
        Check(session.RemoteMonitors.Monitors.Count >= 1,
            "Authenticated TLS receiver must return at least one Windows display");
        var peer = await connected.Task.WaitAsync(testTimeout.Token);
        Check(peer.Name == Environment.MachineName &&
            peer.Monitors.Monitors.Count >= 1,
            "The receiving PC must show the authenticated source monitor layout");
        var heartbeatAck=new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.HeartbeatAcknowledged += () => heartbeatAck.TrySetResult(true);
        Check(session.TrySend(new Packet("ping")),
            "Authenticated Android-style ping must be accepted.");
        Check(await heartbeatAck.Task.WaitAsync(testTimeout.Token),
            "Receiver must answer pong before Android's 22-second read timeout.");
        Console.WriteLine("PASS: Windows sends authenticated heartbeat pong to Android.");

        session.PublishSourcePlacement(MonitorEdge.Right);
        Check(await remoteArrangement.Task.WaitAsync(testTimeout.Token) == MonitorEdge.Right,
            "Receiver should mirror source PC monitor placement");
        session.PublishSourceTopology(MonitorTopology.Capture());
        var updated = await remoteRefresh.Task.WaitAsync(testTimeout.Token);
        Check(updated.Name == Environment.MachineName && updated.Monitors.Monitors.Count >= 1,
            "Receiver should retain identity when monitor layout updates");

        var incomingArrangement = new TaskCompletionSource<MonitorEdge>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingRefresh = new TaskCompletionSource<MonitorTopology>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.RemotePlacementChanged += e => incomingArrangement.TrySetResult(e);
        session.RemoteTopologyChanged += m => incomingRefresh.TrySetResult(m);
        receiver.PublishPlacement(MonitorEdge.Left);
        Check(await incomingArrangement.Task.WaitAsync(testTimeout.Token) == MonitorEdge.Left,
            "Source should follow a monitor placement change made on receiving PC");
        receiver.PublishLocalTopology(MonitorTopology.Capture());
        Check((await incomingRefresh.Task.WaitAsync(testTimeout.Token)).Monitors.Count >= 1,
            "Receiver display changes should sync to source without reconnecting");

        // Physical mouse movement can generate huge bursts. Dropping OLD
        // movement coordinates rather than disconnecting is safe as long as
        // the latest position eventually reaches the receiver.
        for (int i = 0; i < 5000; i++)
            Check(session.TrySend(new Packet("move", i % 1800, i % 950)),
                "High-frequency mouse motion must never disconnect the session");
        await Task.Delay(3500, testTimeout.Token);
        Check(!ended.Task.IsCompleted,
            "A stable paired connection must persist after display synchronization and mouse bursts");

        // TRUE BIDIRECTIONAL INPUT AUTHORITY over the SAME authenticated TLS
        // connection, not two independent outgoing pairing sessions.
        // Both devices can request/control the other; native input on the
        // destination always revokes granted authority.
        session.AttachReverseInput(hostPriority, () => true);
        Interlocked.Add(ref authorityClock, 3000);
        var forwardGrant = new TaskCompletionSource<Packet>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardApplied = new TaskCompletionSource<Packet>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseGrant = new TaskCompletionSource<Packet>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseApplied = new TaskCompletionSource<Packet>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reverseRevoked = new TaskCompletionSource<Packet>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.AuthorityControlPacket += packet =>
        {
            if (packet.Type == "granted") forwardGrant.TrySetResult(packet);
            if (packet.Type == "active") forwardApplied.TrySetResult(packet);
        };
        receiver.ReverseControlMessage += packet =>
        {
            if (packet.Type == "reverse-granted") reverseGrant.TrySetResult(packet);
            if (packet.Type == "reverse-active") reverseApplied.TrySetResult(packet);
            if (packet.Type == "reverse-local-active") reverseRevoked.TrySetResult(packet);
        };
        Check(session.TrySend(new Packet("claim", 101)),
            "Source PC must be able to request authority over receiver PC");
        Check((await forwardGrant.Task.WaitAsync(testTimeout.Token)).A == 101,
            "Receiver must echo exact forward handoff authority ID");
        await Task.Delay(130, testTimeout.Token);
        Check(!forwardApplied.Task.IsCompleted,
            "Forward grant alone must NOT claim keyboard/cursor authority");
        var point = System.Windows.Forms.Cursor.Position;
        Check(session.TrySend(new Packet("move", point.X, point.Y)),
            "Source must send first cursor position to complete authority handoff");
        Check((await forwardApplied.Task.WaitAsync(testTimeout.Token)).A == 101,
            "Receiver must confirm a successfully applied forward cursor event");
        Check(session.TrySend(new Packet("release", 101)),
            "Source must release its own forward authority ID");
        await Task.Delay(250, testTimeout.Token);

        Check(((IInputSession)receiver).RemoteMonitors.Monitors.Count >= 1,
            "The receiving PC must know original source monitors for REVERSE control");
        Check(((IInputSession)receiver).TrySend(new Packet("claim", 202)),
            "Receiving PC must initiate reverse control on the existing TLS pairing");
        Check((await reverseGrant.Task.WaitAsync(testTimeout.Token)).A == 202,
            "Original source must explicitly grant the reverse authority ID");
        await Task.Delay(130, testTimeout.Token);
        Check(!reverseApplied.Task.IsCompleted,
            "Reverse grant alone must NOT capture native mouse or keyboard");
        Check(((IInputSession)receiver).TrySend(new Packet("move", point.X, point.Y)),
            "Receiving PC must transmit reverse cursor movement");
        Check((await reverseApplied.Task.WaitAsync(testTimeout.Token)).A == 202,
            "Original source must acknowledge successful reverse cursor application");
        hostPriority.RecordPhysicalActivity();
        Check((await reverseRevoked.Task.WaitAsync(testTimeout.Token)).A == 202,
            "Physical native input on original source must revoke reverse authority");
        Check(!hostPriority.RemoteGranted,
            "Physical input override must release reverse input ownership");
        Check(((IInputSession)receiver).TrySend(new Packet("release", 202)),
            "Reverse PC must explicitly release authority on return");
        Check(!ended.Task.IsCompleted,
            "Forward and reverse claims must not disconnect the authenticated pairing");

        var claims = new AuthorityRequestTracker();
        int abandonedClaim = claims.Begin();
        claims.Cancel();
        int newClaim = claims.Begin();
        Check(newClaim != abandonedClaim && !claims.Accept(abandonedClaim) &&
            claims.Accept(newClaim),
            "A delayed acknowledgment from an abandoned handoff cannot take authority");
        Check(ReverseInputProtocol.TryDecode(
            ReverseInputProtocol.Encode(new Packet("key", 2, 0, 1, 65)),
            out var decoded) && decoded.Type == "key",
            "Reverse protocol must preserve keyboard and mouse packet structure");
        Check(!ReverseInputProtocol.TryDecode(new Packet("reverse-auth-trusted"),
            out _), "Reverse input parser must reject identity and trust protocol commands");
        Console.WriteLine("PASS: BOTH DIRECTIONS authority grant + cursor-applied ACK, native override, stale claim rollback and same TLS pairing.");
    }
    catch
    {
        Console.Error.WriteLine("RECEIVER STATUS: " + string.Join(" | ", status));
        throw;
    }
    Console.WriteLine("PASS: Windows loopback TLS pairing, explicit receiver approval and monitor exchange.");
    // The original v0.2.0 manual pairing flow must remain functional.
    await Task.Delay(250);
    try
    {
        using var manual = await HostSession.ConnectAsync(
            "127.0.0.1", receiver.Fingerprint, receiver.Secret,
            message => status.Enqueue(message), testTimeout.Token);
        Check(manual.RemoteMonitors.Monitors.Count >= 1,
            "Manual pairing code must authenticate and exchange monitors");
    }
    catch
    {
        Console.Error.WriteLine("MANUAL RECEIVER STATUS: " + string.Join(" | ", status));
        throw;
    }
    Console.WriteLine("PASS: Windows loopback TLS manual pairing code and monitor exchange.");
}


// TRUSTED DEVICE REGRESSIONS: persistent cryptographic identity and signed
// permission decision; the trusted list stores NO remembered IP addresses.
var trustRoot = Path.Combine(Path.GetTempPath(),
    "DeskTouch-trusted-lan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(trustRoot);
try
{
    var identityPath = Path.Combine(trustRoot, "receiver-identity.dpapi");
    var inboundPath = Path.Combine(trustRoot, "incoming.dpapi");
    var outboundPath = Path.Combine(trustRoot, "outgoing.dpapi");
    string fingerprint;
    using (var cert = PersistentReceiverIdentity.LoadOrCreate(identityPath))
    {
        Check(cert.HasPrivateKey, "Permanent TLS device identity must include its private key");
        fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(cert.RawData));
    }
    using (var reloaded = PersistentReceiverIdentity.LoadOrCreate(identityPath))
    {
        var fingerprintAfterRestart = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(reloaded.RawData));
        Check(fingerprintAfterRestart == fingerprint,
            "Permanent cryptographic device ID must be unchanged across process restarts");
    }

    var receiverGrants = new TrustedDeviceStore(inboundPath);
    var senderGrants = new TrustedDeviceStore(outboundPath);
    var approvalCount = 0;
    var receiver = new ReceiverServer(_ => { },
        new LocalInputPriority(), (_, _) =>
        {
            approvalCount++;
            return Task.FromResult(true);
        }, receiverGrants, (_, _) =>
        {
            approvalCount++;
            return Task.FromResult(true);
        }, identityPath);
    try
    {
        Check(receiver.Fingerprint == fingerprint,
            "Discovery must advertise the same device identity after a receiver restart");
        receiver.Start();
        using var trustTestDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string token;
        using (var first = await HostSession.ConnectWithApprovalAsync(
            "127.0.0.1", fingerprint, _ => { }, trustTestDeadline.Token))
        {
            token = await first.RequestTrustAsync(trustTestDeadline.Token) ??
                throw new Exception("Trusted enrollment requires receiver's explicit approval");
            senderGrants.TrustOutgoing("Trusted Windows receiver", fingerprint, token);
            Check(approvalCount == 2, "Initial pairing and trusted enrollment each require explicit consent");
        }
        var restartSender = new TrustedDeviceStore(outboundPath);
        var restartReceiver = new TrustedDeviceStore(inboundPath);
        Check(restartSender.Outgoing.Count == 1 &&
            restartReceiver.Incoming.Count == 1,
            "Trusted identity and authorization must persist under the Windows user's DPAPI");
        Check(restartSender.Outgoing[0].GetType().GetProperty("Address") == null,
            "Saved trusted receiver must not contain ANY IP address");
        Check(restartSender.FindAutoConnect(fingerprint)?.Token == token,
            "Restarted sender must recognize permanent receiver fingerprint, not IP");
        var homeWifi = new NearbyComputer("My PC", "192.168.1.40", fingerprint);
        var officeLan = new NearbyComputer("Renamed PC", "10.7.8.90", fingerprint);
        var impersonator = new NearbyComputer("My PC", "10.7.8.90",
            new string('A', 64));
        Check(restartSender.FindAutoConnect(homeWifi.Fingerprint) != null &&
            restartSender.FindAutoConnect(officeLan.Fingerprint) != null,
            "Changing Wi-Fi, DHCP address or computer label must not lose trusted device");
        Check(restartSender.FindAutoConnect(impersonator.Fingerprint) == null,
            "A familiar name or address is never sufficient to impersonate a trusted computer");
        // Receiver must finish the old TLS session before another connects.
        await Task.Delay(800, trustTestDeadline.Token);
        using (var trustedSession = await HostSession.ConnectTrustedAsync(
            "127.0.0.1", fingerprint, token, _ => { }, trustTestDeadline.Token))
        {
            Check(trustedSession.RemoteMonitors.Monitors.Count > 0,
                "Trusted reconnect must exchange native Windows display layout");
            Check(approvalCount == 2,
                "Trusted reconnect must NOT prompt again after explicit enrollment");
        }
        await Task.Delay(800, trustTestDeadline.Token);
        restartSender.SetAutoConnect(fingerprint, false);
        Check(restartSender.FindAutoConnect(fingerprint) == null,
            "User must be able to pause automatic reconnect for an individual device");
        restartSender.SetAutoConnect(fingerprint, true);
        restartSender.AutoConnectEnabled = false;
        Check(!new TrustedDeviceStore(outboundPath).AutoConnectEnabled,
            "Manual disconnect must persist pause of automatic reconnection");
        restartSender.AutoConnectEnabled = true;
        receiverGrants.ForgetIncoming(receiverGrants.Incoming[0].TokenSha256);
        using var attemptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var rejected = await HostSession.ConnectTrustedAsync(
                "127.0.0.1", fingerprint, token, _ => { }, attemptDeadline.Token);
            throw new Exception("Revoked trusted grant incorrectly accepted");
        }
        catch (IOException) { /* expected unapproved attempt */ }
        restartSender.ForgetOutgoing(fingerprint);
        Check(new TrustedDeviceStore(outboundPath).Outgoing.Count == 0,
            "Forgetting a trusted receiver must erase saved auto-connect authorization");
        Check(approvalCount == 2,
            "Revoked authorization must not silently fall back to receiver approval");
        Console.WriteLine("PASS: persistent Windows cryptographic identity, two-sided trust enrollment, no remembered IP, silent trusted reconnect and revocation.");
    }
    finally { receiver.Dispose(); }
}
finally
{
    try { Directory.Delete(trustRoot, recursive: true); } catch { }
}

// Live multi-peer LAN transport regression: two simultaneous authenticated
// TLS clients must remain connected, and disconnecting one must not sever the
// other. No hooks or synthetic input are installed by this test.
var multipointRoot=Path.Combine(Path.GetTempPath(),
    "DeskTouch-MultiPeer-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(multipointRoot);
try
{
    var multiPriority=new LocalInputPriority();
    using var multiServer=new ReceiverServer(
        _=>{},multiPriority,(_,__)=>Task.FromResult(true),
        identityPath:Path.Combine(multipointRoot,"identity.bin"));
    multiServer.AllowForwardClaim=()=>false; // no Windows SendInput in CI.
    multiServer.Start();
    Check(multiServer.CanAcceptNewPeer,"Fresh hub must accept a peer");
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
    using var peer1=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        multiServer.Fingerprint,_=>{},timeout.Token);
    using var peer2=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        multiServer.Fingerprint,_=>{},timeout.Token);
    for(int t=0;t<30 && multiServer.ConnectedPeerCount<2;t++)
        await Task.Delay(100,timeout.Token);
    Check(multiServer.ConnectedPeerCount==2,
        "Windows receiver must keep TWO authenticated TLS peers at once");
    Check(multiServer.CanAcceptNewPeer,
        "Windows must remain discoverable/ready with two connected devices");
    // Connected Windows peers must not acquire the keyboard/mouse lease
    // merely by joining the existing TLS environment.
    Check(!multiPriority.RemoteGranted,
        "Authentication or discovery must not grant remote input");
    Console.WriteLine("PASS: multiple authenticated Windows peers stay online without stealing input.");
    peer1.Dispose();
    for(int t=0;t<30 && multiServer.ConnectedPeerCount!=1;t++)
        await Task.Delay(100,timeout.Token);
    Check(multiServer.ConnectedPeerCount==1,
        "Disconnecting one peer must preserve the other");
    using var peer3=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        multiServer.Fingerprint,_=>{},timeout.Token);
    for(int t=0;t<30 && multiServer.ConnectedPeerCount<2;t++)
        await Task.Delay(100,timeout.Token);
    Check(multiServer.ConnectedPeerCount==2,
        "Third device must connect while the second stays online");
    Check(!multiPriority.RemoteGranted,
        "Connected peers must not steal local keyboard/mouse control");
    // An explicit claim may use the mouse/keyboard only when there is no
    // different active lease owner. No move/key is injected in this test.
    multiServer.AllowForwardClaim=()=>true;
    await Task.Delay(LocalInputPriority.IdleBeforeRemoteMilliseconds+150,timeout.Token);
    var firstGrant=new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var secondBusy=new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var secondGrant=new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    peer2.AuthorityControlPacket += p => {
        if(p.A==1001 && p.Type=="granted")firstGrant.TrySetResult(p.Type);
    };
    peer3.AuthorityControlPacket += p => {
        if(p.A==2001 && p.Type=="busy")secondBusy.TrySetResult(p.Type);
        if(p.A==2002 && p.Type=="granted")secondGrant.TrySetResult(p.Type);
    };
    Check(peer2.TrySend(new Packet("claim",1001)),"First lease request must queue");
    await firstGrant.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Check(peer3.TrySend(new Packet("claim",2001)),"Second lease request must queue");
    await secondBusy.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Check(peer2.TrySend(new Packet("release",1001)),"First owner must release");
    await Task.Delay(150,timeout.Token);
    Check(peer3.TrySend(new Packet("claim",2002)),
        "Second device must request a new lease after release");
    await secondGrant.Task.WaitAsync(TimeSpan.FromSeconds(8));
    peer3.TrySend(new Packet("release",2002));
    for(int t=0;t<50 && multiPriority.RemoteGranted;t++)
        await Task.Delay(40,timeout.Token);

    // REGRESSION: a phone or other client that claims the HUB INPUT must not
    // hijack the hub operator's separately selected remote display. Both
    // destinations may have one controller simultaneously without severing TLS.
    var selectedWindows=multiServer.ConnectedPeers.OrderBy(x=>x.Id).First();
    var phoneController=multiServer.ConnectedPeers.OrderBy(x=>x.Id).Last();
    Check(multiServer.SelectConnectedPeer(selectedWindows.Id),
        "Windows hub must select its remote Windows destination");
    var destinationInput=new LocalInputPriority();
    peer2.AttachReverseInput(destinationInput,()=>true);
    await Task.Delay(LocalInputPriority.IdleBeforeRemoteMilliseconds+100,timeout.Token);
    var remoteGrant=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var hubGrant=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    multiServer.ReverseControlMessage+=p=>{
        if(p.Type=="reverse-granted" && p.A==3100)remoteGrant.TrySetResult(p.A);
    };
    peer3.AuthorityControlPacket+=p=>{
        if(p.Type=="granted"&&p.A==3200)hubGrant.TrySetResult(p.A);
    };
    IInputSession pcDestination=multiServer.BindSelectedInput();
    Check(pcDestination.TrySend(new Packet("claim",3100)),
        "Hub must send the reverse control request to the selected PC");
    Check(await remoteGrant.Task.WaitAsync(timeout.Token)==3100 &&
        destinationInput.RemoteGranted,
        "Hub operator must control the selected PC independently");
    // Use the REAL form's policy, not a hard-coded test grant. A previous
    // release denied this exact case whenever a reverse capture was active.
    multiServer.AllowForwardClaim=()=>InputClaimLifecyclePolicy.CanGrant(
        true,destinationInput.RemoteGranted,false);
    Check(peer3.TrySend(new Packet("claim",3200)),
        "Another authenticated device must be able to claim the hub concurrently");
    Check(await hubGrant.Task.WaitAsync(timeout.Token)==3200,
        "Hub should grant this second independent destination");
    Check(multiServer.SelectedPeerId==selectedWindows.Id &&
        destinationInput.RemoteGranted && multiPriority.RemoteGranted &&
        multiServer.ConnectedPeerCount==2,
        "A different Windows PC controlling the hub must NOT switch, disconnect or revoke hub-to-PC input");
    Check(peer3.TrySend(new Packet("release",3200)),
        "Hub control can be released independently of Windows remote control");
    Check(pcDestination.TrySend(new Packet("release",3100)),
        "Windows remote control can release without dropping its source");
    for(int t=0;t<50 &&
        (destinationInput.RemoteGranted || multiPriority.RemoteGranted);t++)
        await Task.Delay(40,timeout.Token);
    Check(!destinationInput.RemoteGranted && !multiPriority.RemoteGranted,
        "Independent lease releases must not strand keyboard/mouse authority");
    Console.WriteLine("PASS: Windows peer->hub AND hub->Windows keyboard authority concurrently; no capture selection theft.");

    // The Windows keyboard/mouse capture is pinned to its original TLS peer.
    // Selecting another device must never misroute a delayed key or release.
    var original = multiServer.ConnectedPeers.OrderBy(x => x.Id).First();
    var alternate = multiServer.ConnectedPeers.OrderBy(x => x.Id).Last();
    Check(multiServer.SelectConnectedPeer(original.Id),
        "First authenticated Windows peer should be selectable");
    IInputSession bound = multiServer.BindSelectedInput();
    Check(multiServer.SelectConnectedPeer(alternate.Id),
        "Second authenticated Windows peer should be selectable without disconnect");
    Check(bound.TrySend(new Packet("release",3003)),
        "Unowned stale release is a safe no-op and cannot reset another environment member");
    peer2.Dispose();
    for(int t=0;t<30 && multiServer.ConnectedPeerCount!=1;t++)
        await Task.Delay(100,timeout.Token);
    Check(multiServer.SelectedPeerId==alternate.Id,
        "Switching targets must retain the other connected Windows peer");
    Check(!bound.TrySend(new Packet("release",3004)),
        "Old capture must not route a stale release into the newly selected peer");
    Console.WriteLine("PASS: simultaneous TLS devices, exclusive authority and pinned input routing on target switches.");
}
finally
{
    try{Directory.Delete(multipointRoot,true);}catch{}
}

// Shared environment: THREE clients join ONE TLS hub. The controller
// selects another authenticated member by hub-issued connection ID without
// pairing to that target. Two competing sources never control one destination.
{
    long clock=12000;
    using var environment=new ReceiverServer(_=>{},
        new LocalInputPriority(()=>Interlocked.Read(ref clock)),
        (_,__)=>Task.FromResult(true));
    environment.AllowForwardClaim=()=>false; // never inject into CI runner
    environment.Start();
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
    using var mobileSource=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        environment.Fingerprint,_=>{},deadline.Token);
    using var desktop=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        environment.Fingerprint,_=>{},deadline.Token);
    using var otherPhone=await HostSession.ConnectWithApprovalAsync("127.0.0.1",
        environment.Fingerprint,_=>{},deadline.Token);
    for(int i=0;i<80 && mobileSource.EnvironmentMembers.Count<4;i++)
        await Task.Delay(75,deadline.Token);
    Check(mobileSource.EnvironmentMembers.Count==4 &&
        mobileSource.EnvironmentMembers.Single(m=>m.Id==0).Platform=="Windows",
        "One hub must advertise itself and ALL three simultaneous members");
    var routed=mobileSource.EnvironmentMembers
        .Where(m=>m.Id>0).OrderBy(m=>m.Id).Skip(1).First();
    Check(!string.IsNullOrWhiteSpace(routed.StableIdentity),
        "Every connected member must have a stable layout identity");
    var sharedKey=routed.StableIdentity!;
    Check(mobileSource.PublishEnvironmentPlacement(sharedKey,
        new System.Drawing.PointF(620,-155)),
        "One member must be able to publish a dragged screen to the authenticated hub");
    for(int i=0;i<100 &&
        (!otherPhone.SharedLayout.TryGetValue(sharedKey,out var position) ||
        position.X!=620 || position.Y!=-155);i++)
        await Task.Delay(40,deadline.Token);
    Check(otherPhone.SharedLayout.TryGetValue(sharedKey,out var firstPosition) &&
        firstPosition.X==620 && firstPosition.Y==-155,
        "Other Windows PC must receive the SAME placement without manual copying");
    Check(environment.EnvironmentPositions.ContainsKey(sharedKey),
        "Windows hub must store the authoritative layout");
    Check(otherPhone.PublishEnvironmentPlacement(sharedKey,
        new System.Drawing.PointF(-180,235)),
        "Drag on another Windows PC must update the same shared environment");
    for(int i=0;i<100 &&
        (!mobileSource.SharedLayout.TryGetValue(sharedKey,out var position) ||
        position.X!=-180 || position.Y!=235);i++)
        await Task.Delay(40,deadline.Token);
    Check(mobileSource.SharedLayout.TryGetValue(sharedKey,out var secondPosition) &&
        secondPosition.X==-180 && secondPosition.Y==235,
        "Drag on second PC must synchronize back to first PC through hub");
    // v0.3.17 canonical coordinates use the SURAJ hub as the SAME
    // reference on either PC, even when the local preview has another
    // world-coordinate origin. The old per-PC offset produced a different
    // arrangement after exactly the same drag broadcast.
    string canonicalKey="hub@"+sharedKey;
    var hubPeer=new ConnectedPeer("Second PC",
        MonitorTopology.Capture(),DeviceFingerprint:sharedKey);
    Check(ReceiverServer.OwnsPlacement(hubPeer,canonicalKey) &&
        !ReceiverServer.OwnsPlacement(hubPeer,"hub@IMPOSTOR"),
        "Canonical layout writes must be tied to authenticated stable identity");
    var phoneDefaultOnHub=new System.Drawing.Rectangle(5200,400,440,800);
    var phoneDefaultOnLeaf=new System.Drawing.Rectangle(3100,400,440,800);
    var canonicalOffset=new System.Drawing.PointF(2700,260);
    var hubWorld=DisplayArrangementControl.ResolveMemberWorld(
        phoneDefaultOnHub,new System.Drawing.Rectangle(200,50,1700,1000),
        canonicalOffset,null);
    var leafWorld=DisplayArrangementControl.ResolveMemberWorld(
        phoneDefaultOnLeaf,new System.Drawing.Rectangle(-1900,50,1700,1000),
        canonicalOffset,null);
    Check(hubWorld.Left-200==leafWorld.Left+1900 &&
        hubWorld.Top-50==leafWorld.Top-50,
        "Hub and second PC must render the exact SAME canonical Android placement");
    Check(mobileSource.PublishEnvironmentPlacement(canonicalKey,canonicalOffset),
        "Hub must accept canonical Android position based on stable device identity");
    for(int i=0;i<100 &&
        !otherPhone.SharedLayout.ContainsKey(canonicalKey);i++)
        await Task.Delay(40,deadline.Token);
    Check(otherPhone.SharedLayout.TryGetValue(canonicalKey,out var synced) &&
        synced==canonicalOffset &&
        !environment.EnvironmentPositions.ContainsKey(sharedKey),
        "Shared canonical layout must replace old local offset on all participants");
    Check(environment.ConnectedPeerCount==3,
        "Layout broadcast must not reconnect or drop any client session");
    Console.WriteLine("PASS: bidirectional shared screen layout replication across three TLS clients.");

    Check(mobileSource.SelectEnvironmentTarget(routed.Id),
        "Android-style controller must route across the original hub connection");
    Check(mobileSource.SelectedEnvironmentTargetId==routed.Id,
        "Selected input target must be an environment member, not another socket");
    var destinationPriority=new LocalInputPriority(
        ()=>Interlocked.Read(ref clock));
    desktop.AttachReverseInput(destinationPriority,()=>true);
    Interlocked.Add(ref clock,3000);
    var granted=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var appliedRouted=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var competitorBusy=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var competitorGrant=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var revoked=new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    mobileSource.AuthorityControlPacket += p=>{
        if(p.Type=="granted"&&p.A==501)granted.TrySetResult(p.A);
        if(p.Type=="active"&&p.A==501)appliedRouted.TrySetResult(p.A);
    };
    otherPhone.AuthorityControlPacket += p=>{
        if(p.Type=="busy"&&p.A==502)competitorBusy.TrySetResult(p.A);
        if(p.Type=="granted"&&p.A==503)competitorGrant.TrySetResult(p.A);
        if(p.Type=="local-active"&&p.A==503)revoked.TrySetResult(p.A);
    };
    Check(mobileSource.TrySend(new Packet("claim",501)),"Controller claim must queue");
    Check(await granted.Task.WaitAsync(deadline.Token)==501,
        "Destination's actual reverse grant must be relayed back to source");
    Check(destinationPriority.RemoteGranted,
        "Destination rather than hub must hold the remote input lease");
    Check(!appliedRouted.Task.IsCompleted,
        "Remote grant alone MUST NOT activate keyboard before cursor application");
    var sourceCursor=System.Windows.Forms.Cursor.Position;
    var appliedStarted=System.Diagnostics.Stopwatch.StartNew();
    Check(mobileSource.TrySend(new Packet("move",sourceCursor.X,sourceCursor.Y)),
        "Remote cursor movement must follow routed grant");
    Check(await appliedRouted.Task.WaitAsync(deadline.Token)==501,
        "Second Windows PC must apply routed first cursor and return real active ACK");
    appliedStarted.Stop();
    Console.WriteLine($"MEASURED: routed claim-to-ACK after grant {appliedStarted.ElapsedMilliseconds} ms (CI loopback only).");
    Check(otherPhone.SelectEnvironmentTarget(routed.Id),
        "Second source must view same environment member without pairing to it");
    Check(otherPhone.TrySend(new Packet("claim",502)),
        "Competing controller claim should reach environment hub");
    Check(await competitorBusy.Task.WaitAsync(deadline.Token)==502,
        "Two peers cannot own keyboard/mouse authority on the same destination");
    Check(mobileSource.TrySend(new Packet("release",501)),
        "Original controller must release routed lease");
    for(int i=0;i<60&&destinationPriority.RemoteGranted;i++)
        await Task.Delay(50,deadline.Token);
    Check(!destinationPriority.RemoteGranted,
        "Releasing source must free the destination's actual input authority");
    Check(otherPhone.TrySend(new Packet("claim",503)),
        "Second source may request control once first source releases it");
    Check(await competitorGrant.Task.WaitAsync(deadline.Token)==503,
        "New source must receive fresh destination grant");
    destinationPriority.RecordPhysicalActivity();
    Check(await revoked.Task.WaitAsync(deadline.Token)==503,
        "Physical activity at destination must revoke the routed source lease");
    Check(mobileSource.SelectEnvironmentTarget(0) &&
        mobileSource.SelectedEnvironmentTargetId==0,
        "Returning to environment hub cannot disconnect other connected members");
    Check(environment.ConnectedPeerCount==3,
        "All devices must remain in one environment while authority changes");
    // Hub-local control is also exclusive. A target switch must deliver the
    // previous target's approved reset on the PREVIOUS peer session, not drop
    // it because another peer is now selected.
    Interlocked.Add(ref clock,3000);
    Check(environment.SelectConnectedPeer(routed.Id),
        "Hub should be able to select the routed desktop as a local target");
    IInputSession hubCapture=environment.BindSelectedInput();
    Check(hubCapture.TrySend(new Packet("claim",700)),
        "Hub-local claim must queue on original authenticated target");
    for(int i=0;i<50&&!destinationPriority.RemoteGranted;i++)
        await Task.Delay(50,deadline.Token);
    Check(destinationPriority.RemoteGranted,
        "Hub-local claim must acquire only that member's destination lease");
    var anotherId=environment.ConnectedPeers
        .Where(p=>p.Id!=routed.Id).OrderBy(p=>p.Id).First().Id;
    Check(environment.SelectConnectedPeer(anotherId),
        "Selecting a different member must not sever any environment connection");
    for(int i=0;i<50&&destinationPriority.RemoteGranted;i++)
        await Task.Delay(50,deadline.Token);
    Check(!destinationPriority.RemoteGranted,
        "Original peer's reset must arrive even after hub's target changes");
    Check(environment.ConnectedPeerCount==3,
        "Authority reset must not terminate any concurrent environment connection");
    Console.WriteLine("PASS: one hub, three members, routed keyboard authority, competing claims, native priority and old-target release.");
}

// Stress: a hub must keep up to eight approved simultaneous TLS participants
// in ONE roster, refuse an extra connection, and admit a replacement after a
// member disconnects without severing any other peer.
{
    using var capacityHub = new ReceiverServer(_=>{},new LocalInputPriority(),
        (_,__)=>Task.FromResult(true));
    capacityHub.AllowForwardClaim=()=>false;
    capacityHub.Start();
    using var capTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var endpoints = new List<HostSession>();
    try
    {
        for(int i=0;i<8;i++)
            endpoints.Add(await HostSession.ConnectWithApprovalAsync("127.0.0.1",
                capacityHub.Fingerprint,_=>{},capTimeout.Token));
        for(int retry=0;retry<120&&capacityHub.ConnectedPeerCount!=8;retry++)
            await Task.Delay(40,capTimeout.Token);
        Check(capacityHub.ConnectedPeerCount==8 && !capacityHub.CanAcceptNewPeer,
            "Hub must permit eight independently authenticated members and enforce capacity");
        for(int retry=0;retry<120 && endpoints[0].EnvironmentMembers.Count!=9;retry++)
            await Task.Delay(40,capTimeout.Token);
        Check(endpoints[0].EnvironmentMembers.Count==9,
            "Full hub roster must contain eight live peers plus the hub itself");
        endpoints[7].Dispose();
        endpoints.RemoveAt(7);
        for(int retry=0;retry<120&&capacityHub.ConnectedPeerCount!=7;retry++)
            await Task.Delay(40,capTimeout.Token);
        Check(capacityHub.ConnectedPeerCount==7 && capacityHub.CanAcceptNewPeer,
            "Disconnecting one member must restore one available hub slot");
        endpoints.Add(await HostSession.ConnectWithApprovalAsync("127.0.0.1",
            capacityHub.Fingerprint,_=>{},capTimeout.Token));
        for(int retry=0;retry<120&&capacityHub.ConnectedPeerCount!=8;retry++)
            await Task.Delay(40,capTimeout.Token);
        Check(capacityHub.ConnectedPeerCount==8,
            "Replacement member must join without disrupting the seven connected peers");
        Check(endpoints.Take(7).All(peer=>peer.EnvironmentMembers.Count>=8),
            "Existing participants must continue receiving environment membership updates");
        Console.WriteLine("PASS: hub saturation at eight authenticated peers, disconnect, slot reuse and roster continuity.");
    }
    finally {foreach(var endpoint in endpoints)endpoint.Dispose();}
}

// Every Windows computer must have one selectable monitor tile. The active
// peer is not duplicated, and an unavailable peer cannot block edge handoff.
{
    var devices=new[]{
        new EnvironmentDisplayTile(0,"SURAJ","Windows",1,true),
        new EnvironmentDisplayTile(-2,"WORKSTATION-B","Windows",1,true),
        new EnvironmentDisplayTile(-3,"WORKSTATION-C","Windows",1,false),
        new EnvironmentDisplayTile(-4,"DESKTOP-IQ1KSJ9","Windows",2,true),
        new EnvironmentDisplayTile(-2,"duplicate roster callback","Windows",1,true)
    };
    var visible=DisplayArrangementControl.ProjectEnvironment(devices,0);
    Check(visible.Count==3 &&
          visible.Select(x=>x.TargetId).Distinct().Count()==3,
        "Windows environment must deduplicate each connected monitor group");
    Check(visible.All(x=>x.Platform=="Windows") &&
          visible.Any(x=>!x.SupportsInput),
        "Windows-only display must preserve busy connected computers without input");
    Check(!visible.Any(x=>x.TargetId==0) &&
          DisplayArrangementControl.ProjectEnvironment(devices,-2)
              .Any(x=>x.TargetId==0),
        "Selected monitor group must not appear twice");
    var desktopArea=new System.Drawing.Rectangle(0,0,3800,1080);
    var inline=DisplayArrangementControl.ComposeInlineDevices(desktopArea,visible);
    Check(inline.Count==3 && inline[0].World.Left>=desktopArea.Right,
        "All authenticated Windows screens must occupy the same diagram row");
    Check(inline.All(x=>x.World.Top<desktopArea.Bottom &&
          x.World.Bottom>desktopArea.Top),
        "No Windows peer can disappear into an unrelated bottom dock");
    Check(inline.Zip(inline.Skip(1),(a,b)=>b.World.Left>=a.World.Right)
          .All(x=>x),
        "Monitor hit targets must not overlap even with 2+ remote PCs");
    var wideArea=new System.Drawing.Rectangle(0,0,5000,900);
    var preview=DisplayArrangementControl.ComposeInlineDevices(wideArea,
        new[]{new EnvironmentDisplayTile(93,"REMOTE-WINDOWS","Windows",1,true)})[0].World;
    foreach(float scale in new[]{.12f,.24f,.45f})
    {
        var regions=new List<(int Id,System.Drawing.RectangleF Bounds)>{
            (7,new System.Drawing.RectangleF(0,0,wideArea.Width*scale,wideArea.Height*scale)),
            (93,new System.Drawing.RectangleF(preview.X*scale,preview.Y*scale,
                preview.Width*scale,preview.Height*scale))
        };
        double fraction=(preview.Top+preview.Height/2d)/wideArea.Height;
        Check(DisplayArrangementControl.SpatialNeighbor(regions,7,
            MonitorEdge.Right,fraction)==93 &&
              DisplayArrangementControl.SpatialNeighbor(regions,93,
                  MonitorEdge.Left,.5)==7,
            "Windows edge handoff must remain reachable at diagram zoom "+scale);
    }
    var viewOnly=DisplayArrangementControl.ProjectEnvironment(
        new[]{
            new EnvironmentDisplayTile(91,"Busy Windows PC","Windows",1,false),
            new EnvironmentDisplayTile(93,"Available Windows PC","Windows",1,true)
        },0);
    Check(viewOnly.First().TargetId==93 && viewOnly.Last().TargetId==91,
        "Unavailable Windows computers cannot block active screen-edge handoff");
    var spatial=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (EnvironmentRouteOrder.Local,new(0,0,300,220)),
        (7,new(330,0,300,220)),
        (-5,new(330,250,165,255)),
        (-6,new(-190,0,165,260))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(spatial,7,
        MonitorEdge.Bottom)==-5 &&
        DisplayArrangementControl.SpatialNeighbor(spatial,-5,
        MonitorEdge.Top)==7 &&
        DisplayArrangementControl.SpatialNeighbor(spatial,
        EnvironmentRouteOrder.Local,MonitorEdge.Left)==-6,
        "Dragged Windows monitor groups must support all physical outside edges");
    var farScreens=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(900,0,220,220))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(farScreens,1,
        MonitorEdge.Right)==null,
        "Distant display must not steal the right-edge input handoff");
    var wrongBand=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(310,175,170,220))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(wrongBand,1,
        MonitorEdge.Right,0.1)==null &&
        DisplayArrangementControl.SpatialNeighbor(wrongBand,1,
        MonitorEdge.Right,0.95)==2,
        "Only the touching band of the monitor edge can be a neighbor");
    var overlap=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(150,40,100,160))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(overlap,1,
        MonitorEdge.Right)==null,
        "Overlapping monitor previews must not become fake outside boundaries");
    var snapped=DisplayArrangementControl.SnapToConnectedScreen(
        new System.Drawing.RectangleF(155,60,90,150),
        new[]{new System.Drawing.RectangleF(0,0,300,220)});
    Check(System.Drawing.RectangleF.Intersect(snapped,
        new System.Drawing.RectangleF(0,0,300,220)).IsEmpty,
        "Dragging remote PC over native monitor must snap it outside");
    var liveRoute=new EnvironmentRouteOrder(
        new[]{EnvironmentRouteOrder.Local,7,-2,-3});
    Check(liveRoute.Neighbor(7,MonitorEdge.Right)==-2 &&
          liveRoute.Neighbor(-2,MonitorEdge.Left)==7,
        "Input must route between neighboring Windows peers without repa iring");
    var reversedRoute=new EnvironmentRouteOrder(
        new[]{-2,EnvironmentRouteOrder.Local,7});
    Check(reversedRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Left)==-2 &&
          reversedRoute.Neighbor(-2,MonitorEdge.Right)==EnvironmentRouteOrder.Local,
        "Moving Windows peer to the opposite side reverses edge navigation");
    Console.WriteLine("PASS: Windows-only multi-monitor routing and no duplicate peers.");
}

// Windows Forms layout smoke test runs on a dedicated STA thread. It never
// displays a window or starts the LAN receiver, and it can run on GitHub's
// Windows test runner without touching real monitor/input settings.
var uiCompletion = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var uiThread = new Thread(() =>
{
    try
    {
        using var view = new DisplaySettingsForm();
        view.CreateControl();
        var actionRow = view.Controls.Find("ConnectionActions", true)
            .OfType<System.Windows.Forms.FlowLayoutPanel>().Single();
        Check(!actionRow.WrapContents && actionRow.AutoScroll,
            "High-DPI action buttons must not collapse into an invisible second row");
        Check(actionRow.Controls.OfType<System.Windows.Forms.Button>()
            .All(b=>!b.Text.Contains("Android",StringComparison.OrdinalIgnoreCase) &&
                    !b.Text.Contains("HID",StringComparison.OrdinalIgnoreCase) &&
                    !b.Text.Contains("Bluetooth",StringComparison.OrdinalIgnoreCase)),
            "Windows-only UI must not show legacy mobile or HID actions");
        var trustButton = view.Controls.Find("TrustedDevicesButton", true)
            .OfType<System.Windows.Forms.Button>().Single();
        trustButton.Parent?.PerformLayout();
        Check(trustButton.Width >= 110 && !trustButton.IsDisposed,
            "Trusted-device settings must retain a usable control area");
        var headerLinks = trustButton.Parent?.Controls
            .OfType<System.Windows.Forms.LinkLabel>().ToArray() ??
            Array.Empty<System.Windows.Forms.LinkLabel>();
        foreach (var link in headerLinks)
            Check(!trustButton.Bounds.IntersectsWith(link.Bounds),
                "Trusted devices must not overlap the existing Check for updates link");
        var diagram = view.Controls.Find("DisplayDiagram", true)
            .OfType<DisplayArrangementControl>().Single();
        diagram.Local = extended;
        diagram.Remote = nonrectangular;
        diagram.EnvironmentDevices =
            DisplayArrangementControl.ProjectEnvironment(new[] {
                new EnvironmentDisplayTile(-5,"WORKSTATION B","Windows",1,true),
                new EnvironmentDisplayTile(-6,"WORKSTATION C","Windows",1,false),
                new EnvironmentDisplayTile(-7,"Windows peer","Windows",2,true)
            },0);
        Check(diagram.EnvironmentDevices.Count==3 &&
            diagram.EnvironmentDevices.All(t=>t.Platform=="Windows"),
            "Display diagram must retain every connected Windows computer");

        foreach (var windowSize in new[] {
            new System.Drawing.Size(780, 600),
            new System.Drawing.Size(1000, 690) })
        {
            view.ClientSize = windowSize;
            view.PerformLayout();
            foreach (System.Windows.Forms.Control outer in view.Controls)
                outer.PerformLayout();
            diagram.Parent?.PerformLayout();
            diagram.PerformLayout();
            Check(diagram.Width > 250 && diagram.Height > 80,
                "Native display diagram must retain meaningful dimensions");
            using var rendered = new System.Drawing.Bitmap(
                diagram.Width, diagram.Height);
            diagram.DrawToBitmap(rendered,
                new System.Drawing.Rectangle(0, 0, diagram.Width, diagram.Height));
        }
        // Simulate physical mouse dragging on the actual Windows peer tile.
        // Prior builds only clicked it to swap a single target; this must
        // drag the Windows monitor while preserving input selection.
        using(var bitmap=new System.Drawing.Bitmap(diagram.Width,diagram.Height))
            diagram.DrawToBitmap(bitmap,
                new System.Drawing.Rectangle(0,0,diagram.Width,diagram.Height));
        var peerHit=diagram.OnlineHitRegions.First(x=>x.TargetId==-5).Hit;
        var origin=new System.Drawing.Point((int)peerHit.X+Math.Max(8,(int)peerHit.Width/3),
            (int)peerHit.Y+Math.Max(8,(int)peerHit.Height/3));
        int selections=0,placements=0;
        diagram.EnvironmentDeviceSelected+=_=>selections++;
        diagram.EnvironmentArrangementChanged+=()=>placements++;
        var down=typeof(DisplayArrangementControl).GetMethod("OnMouseDown",
            System.Reflection.BindingFlags.Instance|
            System.Reflection.BindingFlags.NonPublic)!;
        var move=typeof(DisplayArrangementControl).GetMethod("OnMouseMove",
            System.Reflection.BindingFlags.Instance|
            System.Reflection.BindingFlags.NonPublic)!;
        var up=typeof(DisplayArrangementControl).GetMethod("OnMouseUp",
            System.Reflection.BindingFlags.Instance|
            System.Reflection.BindingFlags.NonPublic)!;
        down.Invoke(diagram,new object[]{new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left,1,origin.X,origin.Y,0)});
        move.Invoke(diagram,new object[]{new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left,1,origin.X-42,origin.Y,0)});
        up.Invoke(diagram,new object[]{new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left,1,origin.X-42,origin.Y,0)});
        Check(placements==1&&selections==0 &&
            diagram.StoredMemberOffsets.Values.Any(p=>Math.Abs(p.X)>1),
            "Windows tile must drag and persist without creating another connection");
        // Press/release with no movement should select, not drag/reconnect.
        using(var bitmap2=new System.Drawing.Bitmap(diagram.Width,diagram.Height))
            diagram.DrawToBitmap(bitmap2,
                new System.Drawing.Rectangle(0,0,diagram.Width,diagram.Height));
        var movedHit=diagram.OnlineHitRegions.First(x=>x.TargetId==-5).Hit;
        int tapX=(int)(movedHit.X+movedHit.Width/2);
        int tapY=(int)(movedHit.Y+movedHit.Height/2);
        down.Invoke(diagram,new object[]{new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left,1,tapX,tapY,0)});
        up.Invoke(diagram,new object[]{new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.Left,1,tapX,tapY,0)});
        Check(selections==1&&placements==1,
            "Clicking a Windows peer selects it; dragging moves its screen tile");
        Check(view.Controls.OfType<System.Windows.Forms.Control>()
            .SelectMany(x=>x.Controls.OfType<System.Windows.Forms.ComboBox>())
            .All(c=>c.Name!="ConnectedDeviceTarget"),
            "The old two-item one-to-one target dropdown must not be presented");
        // One global hook owner must survive device-target changes. Using a
        // fake IInputSession checks exact claim/release ownership without
        // installing global keyboard or mouse hooks on the CI runner.
        var monitorA=new MonitorTopology(new[]{
            new MonitorRect("PC_A",0,0,1920,1080,true)});
        var monitorB=new MonitorTopology(new[]{
            new MonitorRect("PC_B",0,0,1280,1024,true)});
        var recorderA=new RecordingInputSession(monitorA);
        var recorderB=new RecordingInputSession(monitorB);
        using(var capture=new SourceCapture(diagram,recorderA,_=>{},
            MonitorEdge.Right,preserveRawInput:true))
        {
            var armed=typeof(SourceCapture).GetField("_armed",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)!;
            // Mark the pure state machine armed without creating OS hooks.
            armed.SetValue(capture,1);
            var enter=typeof(SourceCapture).GetMethod("EnterRemote",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)!;
            enter.Invoke(capture,new object[]{new System.Drawing.Point(1890,440)});
            Check(recorderA.Packets.Any(p=>p.Type=="claim"),
                "First Windows PC must receive the original claim");
            capture.Retarget(recorderB,MonitorEdge.Right);
            Check(recorderA.Packets.Any(p=>p.Type=="release") &&
                !recorderB.Packets.Any(p=>p.Type=="release") &&
                capture.SameInputSession(recorderB) &&
                capture.ActiveRemoteMonitors.Bounds==monitorB.Bounds,
                "Previous Windows PC must receive release before switching destination");
            capture.BeginLinkedHandoff(MonitorEdge.Right,0.4);
            Check(recorderB.Packets.Any(p=>p.Type=="claim") &&
                !recorderA.Packets.Skip(2).Any(p=>p.Type=="claim"),
                "Next Windows PC receives claim over existing capture without new hooks");
            capture.ReturnLocal();
            capture.Retarget(recorderA,MonitorEdge.Right,
                continuingRemoteHandoff:true);
            capture.BeginLinkedHandoff(MonitorEdge.Right,0.5);
            Check(capture.OutboundInputReserved,
                "A chained handoff must preserve independent incoming hub control while pending");
            capture.ReturnLocal();
            Check(!capture.OutboundInputReserved,
                "Cancel/timeout/emergency return must restore physical native-input priority");
        }
        // A real WinForms UserClosing event must hide the existing HWND,
        // not dispose the receiver/input hooks or close the TLS pairing.
        // We create the HWND without showing the form, so Shown/StartListening
        // is not called and this smoke test never opens the real LAN port.
        var nativeWindow = view.Handle;
        Check(nativeWindow != IntPtr.Zero && view.HasTrayIcon,
            "A tray icon must exist while the Displays window is running");
        view.Close();
        Check(!view.IsDisposed && !view.Visible && view.IsRunningInTray,
            "Closing the Displays window must keep the same application running in tray");
        Check(view.Handle == nativeWindow,
            "Hiding to tray must never recreate the HWND or break raw mouse input registration");
        // Unlike the user X, a verified updater must close the tray host so
        // the independent installer can replace files without locks.
        ((DeskTouchVision.IUpdateCloseHost)view).CloseForVerifiedUpdate();
        Check(view.IsDisposed,
            "A verified update must completely close tray host and free input hooks");
        uiCompletion.TrySetResult(true);
    }
    catch (Exception ex) { uiCompletion.TrySetException(ex); }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
await uiCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
uiThread.Join();
Console.WriteLine("PASS: Windows Displays UI renders at minimum and standard sizes without collapsed buttons.");

internal sealed class RecordingInputSession : IInputSession
{
    internal List<Packet> Packets { get; } = new();
    public MonitorTopology RemoteMonitors { get; }
    internal RecordingInputSession(MonitorTopology geometry) => RemoteMonitors=geometry;
    public bool TrySend(Packet packet){ Packets.Add(packet);return true; }
}
