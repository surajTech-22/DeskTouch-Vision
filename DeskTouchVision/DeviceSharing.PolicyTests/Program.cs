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
