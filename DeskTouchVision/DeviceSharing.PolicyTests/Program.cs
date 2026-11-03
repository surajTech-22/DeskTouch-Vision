using DeskTouch.DeviceShare;

static void Check(bool test, string message)
{
    if (!test) throw new Exception("Input priority regression: " + message);
}

// A LAN with two Windows PCs and one Android phone must not be collapsed to
// a single PC, and busy receivers must be visible but not connectable.
var fpOne = new string('A', 64);
var fpTwo = new string('B', 64);
var fpMobile = new string('C', 64);
var local1 = System.Net.IPAddress.Parse("192.168.1.11");
var local2 = System.Net.IPAddress.Parse("192.168.1.12");
var phone = System.Net.IPAddress.Parse("192.168.1.2");
var discoveredOne = NearbyComputerDiscovery.ParseReply("DT1-READY|PC1|" + fpOne, local1);
var discoveredTwo = NearbyComputerDiscovery.ParseReply("DT1-BUSY|PC2|" + fpTwo, local2);
var discoveredMobile = NearbyComputerDiscovery.ParseReply("DT1-MOBILE|Phone|" + fpMobile, phone);
Check(discoveredOne != null && discoveredOne.CanReceive && !discoveredOne.IsAndroid,
    "Ready PC discovery must remain connectable");
Check(discoveredTwo != null && !discoveredTwo.CanReceive && !discoveredTwo.IsAndroid,
    "Busy second Windows PC must remain visible but not falsely available");
Check(discoveredMobile != null && discoveredMobile.IsAndroid && !discoveredMobile.CanReceive,
    "Android discovery must not pretend the phone is a Windows TCP receiver");
var threeDevices = NearbyComputerDiscovery.DistinctDevices(new [] {
    discoveredOne!, discoveredTwo!, discoveredMobile!, discoveredOne!
});
Check(threeDevices.Count == 3 && threeDevices.Count(p => !p.IsAndroid) == 2,
    "Two distinct Windows PCs and Android must all be visible without duplicates");
Check(NearbyComputerDiscovery.ParseReply("DT1-READY|Fake|BAD",
    local1) == null, "Invalid discovery identity must never be accepted");
Check(WindowsStartup.Command(@"C:\Program Files\DeskTouch\DeskTouch.DeviceShare.exe")
    .EndsWith("\" --tray", StringComparison.Ordinal),
    "Windows sign-in command must launch the sharing app to tray");
Console.WriteLine("PASS: two Windows peers, busy device, Android beacon and sign-in startup command.");

// An idle, discoverable DeskTouch listener must never install global input
// hooks or grant remote authority. Only a connected, monitored peer can claim.
Check(!InputClaimLifecyclePolicy.CanGrant(false, false, false),
    "No remote input authority is permitted while no peer is paired.");
Check(!InputClaimLifecyclePolicy.CanGrant(false, true, false),
    "A stale remote-active flag cannot bypass inactive physical monitoring.");
Check(InputClaimLifecyclePolicy.CanGrant(true, false, false),
    "An authenticated monitored peer may request input authority.");
Check(!InputClaimLifecyclePolicy.CanGrant(true, true, false),
    "Forward remote claim must be refused during reverse input control.");
Check(!InputClaimLifecyclePolicy.CanGrant(true, false, true),
    "Forward remote claim must be refused during pending reverse handoff.");
Console.WriteLine("PASS: discovery-only startup cannot grant input; global monitoring required for sharing.");

// Multiple authenticated TCP sessions may coexist, but only a single remote
// device is ever allowed to drive this Windows cursor and keyboard at once.
long multiClock = 200000;
var multiPriority = new LocalInputPriority(() => multiClock);
var shared = new SharedPeerAuthority(multiPriority);
multiClock += LocalInputPriority.IdleBeforeRemoteMilliseconds;
Check(shared.TryClaim(101), "First Android phone should acquire input");
Check(!shared.TryClaim(202), "Second phone must not inject while the first controls Windows");
Check(shared.IsOwner(101) && !shared.IsOwner(202), "Authority must be peer-specific");
shared.Release(202);
Check(shared.IsOwner(101), "Non-owner disconnect may not revoke active controller");
shared.Release(101);
Check(shared.TryClaim(202), "Waiting phone can take control when previous peer releases");
multiPriority.RecordPhysicalActivity();
Check(!multiPriority.CanApplyRemote, "Physical Windows mouse immediately revokes remote injection");
shared.ReleaseAll();
Check(shared.Owner == 0, "All peer claims removed on native override");
Console.WriteLine("PASS: simultaneous connections with exclusive controller, safe disconnect and local override.");

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
    long firstConnectedPeerId = 0;
    receiver.PeerDisconnected += peerId =>
    {
        if (peerId == Interlocked.Read(ref firstConnectedPeerId))
            ended.TrySetResult(true);
    };
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
        Interlocked.Exchange(ref firstConnectedPeerId, peer.SessionId);
        var pong = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.HeartbeatAcknowledged += () => pong.TrySetResult(true);
        Check(session.TrySend(new Packet("ping")),
            "Android-compatible heartbeat ping must be accepted.");
        Check(await pong.Task.WaitAsync(testTimeout.Token),
            "Windows must send pong to prevent Android read timeout.");
        Check(peer.Name == Environment.MachineName &&
            peer.Monitors.Monitors.Count >= 1,
            "The receiving PC must show the authenticated source monitor layout");
        // The second authenticated peer must remain connected at the SAME
        // time as the first. Disconnecting it may never close or revoke the
        // original TLS session or remove its display topology.
        using (var second = await HostSession.ConnectWithApprovalAsync(
            "127.0.0.1", receiver.Fingerprint, message => status.Enqueue(message),
            testTimeout.Token))
        {
            for (int i = 0; i < 40 && receiver.ConnectedCount < 2; i++)
                await Task.Delay(50, testTimeout.Token);
            Check(receiver.ConnectedCount == 2,
                "Two simultaneous authenticated Windows clients must be accepted");
            Check(receiver.CanAcceptNewPeer,
                "Connected receiver must still accept an Android third device");
            var secondId = receiver.ConnectedPeers.Single(p =>
                p.SessionId != peer.SessionId).SessionId;
            receiver.DisconnectPeer(secondId);
            for (int i = 0; i < 40 && receiver.ConnectedCount > 1; i++)
                await Task.Delay(50, testTimeout.Token);
            Check(receiver.ConnectedCount == 1 &&
                  receiver.ConnectedPeers.Single().SessionId == peer.SessionId,
                "Disconnecting one client must retain the first peer identity");
            Check(session.TrySend(new Packet("ping")),
                "Original device remains connected after second peer disconnect");
        }
        Console.WriteLine("PASS: two simultaneous TLS clients; independent disconnect; third slot ready.");

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
