using DeskTouch.DeviceShare;

static void Check(bool test, string message)
{
    if (!test) throw new Exception("Input priority regression: " + message);
}

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

Console.WriteLine("PASS: Windows native priority, two-monitor seams, all four outer edges, and nonrectangular monitor layouts.");


// Real TLS/auth/monitor-mapping integration test on loopback, no remote inputs.
{
    var status = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var receiver = new ReceiverServer(message => status.Enqueue(message),
        new LocalInputPriority(), (_, _) => Task.FromResult(true));
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
