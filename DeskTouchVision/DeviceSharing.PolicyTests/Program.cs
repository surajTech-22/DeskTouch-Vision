using DeskTouch.DeviceShare;

// Mobile idle timeouts must tolerate brief Android screen/Wi-Fi transitions,
// but never leave an abandoned authenticated TLS lease indefinitely.
if (ReceiverServer.PeerIdleGraceSeconds < 40 ||
    ReceiverServer.PeerIdleGraceSeconds > 65)
    throw new Exception("Android heartbeat idle grace is outside the safe window");

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
    "Android screen", 0, 0, 1080, 2400, true), 1) == 1,
    "Android screen gets its own first display number.");
Check(DisplayArrangementControl.CleanDeviceName("Phone\r\nName", "Unknown") ==
    "Phone  Name", "Device title may not introduce an extra line into the diagram.");
Console.WriteLine("PASS: actual computer names, per-device native Windows display numbers and Android 1.");
var testDisplay = new MonitorTopology(new[] {
    new MonitorRect("Android screen",0,0,1080,2400,true)
});
Check(!ReceiverServer.AllowsForwardInput(new ConnectedPeer("Phone",testDisplay,
    SupportsReverseInput:true,Platform:"Android")),
    "One-way Android mode must not claim Windows keyboard/mouse authority");
Check(ReceiverServer.AllowsForwardInput(new ConnectedPeer("Desktop",testDisplay,
    SupportsReverseInput:true,Platform:"Windows")),
    "Windows PCs must retain duplex authority");
Check(ReceiverServer.AllowsForwardInput(new ConnectedPeer("Approved phone",testDisplay,
    SupportsReverseInput:true,Platform:"Android",SupportsForwardInput:true)),
    "Only an Android session explicitly enabled for forward control may request an input lease");
var androidHello=System.Text.Json.JsonSerializer.Serialize(new PeerHello(
    "Xiaomi 22041216I",testDisplay.ToWire(),ReverseInputSupported:true,
    Platform:"Android",ForwardInputSupported:true));
var parsedAndroid=ReceiverServer.ReadHello(androidHello);
Check(parsedAndroid.Platform=="Android" && parsedAndroid.SupportsForwardInput &&
    parsedAndroid.SupportsReverseInput,
    "Authenticated Android hello must preserve bidirectional capability flags");
Check(ReceiverServer.AllowsAndroidCapability(
    new ConnectedPeer("Xiaomi",testDisplay,Platform:"Android"),
    new Packet("android-capability",1)),
    "An authenticated Android phone can advertise that Accessibility was restored");
Check(ReceiverServer.AllowsAndroidCapability(
    new ConnectedPeer("Xiaomi",testDisplay,Platform:"Android"),
    new Packet("android-capability",0)),
    "An Android phone can pause Accessibility for banking on an active TLS session");
Check(!ReceiverServer.AllowsAndroidCapability(
    new ConnectedPeer("Desktop",testDisplay,Platform:"Windows"),
    new Packet("android-capability",1)),
    "Windows peer cannot spoof Android Accessibility permission changes");
Check(!ReceiverServer.AllowsAndroidCapability(
    new ConnectedPeer("Xiaomi",testDisplay,Platform:"Android"),
    new Packet("android-capability",3)),
    "Unknown Android capability flags cannot grant keyboard authority");
Check(ReceiverServer.CanPauseAndroidForPayments(
    new ConnectedPeer("Xiaomi", testDisplay, Platform: "Android", SupportsReverseInput: true)) &&
    !ReceiverServer.CanPauseAndroidForPayments(
        new ConnectedPeer("Xiaomi", testDisplay, Platform: "Android", SupportsReverseInput: false)) &&
    !ReceiverServer.CanPauseAndroidForPayments(
        new ConnectedPeer("Desktop", testDisplay, Platform: "Windows", SupportsReverseInput: true)) &&
    !ReceiverServer.CanPauseAndroidForPayments(null) &&
    new Packet("pause-accessibility").Type == "pause-accessibility",
    "Windows payment-safe pause targets only active Android peers with Accessibility enabled");
Console.WriteLine("PASS: Android handshake, Accessibility toggle, payment-safe pause, and input opt-in enforced.");

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
var threeDevices = NearbyComputerDiscovery.DistinctDevices(new[] {
    discoveredOne!, discoveredTwo!, discoveredMobile!, discoveredOne!
});
Check(threeDevices.Count == 3 && threeDevices.Count(p => !p.IsAndroid) == 2,
    "Two distinct Windows PCs and Android must all be visible without duplicates");
var discoveredHub = new NearbyComputer("HUB", "192.168.1.25",
    new string('D',64),IsAndroid:false,CanReceive:true,IsHub:true);
var discoveredLeaf = new NearbyComputer("LEAF", "192.168.1.26",
    new string('E',64),IsAndroid:false,CanReceive:true,IsHub:false);
var sortedEnvironment=NearbyComputerDiscovery.DistinctDevices(
    new[]{discoveredLeaf,discoveredHub});
Check(sortedEnvironment[0].IsHub && sortedEnvironment[0].Name=="HUB",
    "Windows discovery must prioritize one environment hub over member endpoints");
Check(NearbyComputerDiscovery.ParseReply("DT1-READY|Fake|BAD",local1)==null,
    "Invalid discovery identity must never be accepted");
Check(WindowsStartup.Command(@"C:\\Program Files\\DeskTouch\\DeskTouch.DeviceShare.exe")
    .EndsWith("\" --tray",StringComparison.Ordinal),
    "Windows sign-in command must launch the sharing app to tray");
Console.WriteLine("PASS: two Windows peers, busy device, Android beacon and sign-in startup command.");

// The visual order SURAJ | native Windows screens | Android must
// remain a single input environment, not pairwise connections.
var inlineAndroid = new EnvironmentDisplayTile(-5,
    "Xiaomi 22041216I","Android",1,true);
var inlineDisabled = new EnvironmentDisplayTile(-6,
    "Android view only","Android",1,false);
var leafRoute = new EnvironmentRouteOrder(0,
    new[]{inlineAndroid,inlineDisabled},MonitorEdge.Left);
Check(leafRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Left)==0 &&
    leafRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Right)==-5,
    "Windows leaf routes left to hub and right to Android without changing TLS sessions");
Check(leafRoute.Neighbor(-5,MonitorEdge.Left)==EnvironmentRouteOrder.Local &&
    leafRoute.Neighbor(0,MonitorEdge.Right)==EnvironmentRouteOrder.Local,
    "Returning from phone/hub edge must restore local computer, not another peer");
Check(!leafRoute.Order.Contains(-6) &&
    leafRoute.Neighbor(-5,MonitorEdge.Right)==null,
    "View-only Android device must never receive an unauthorized automatic input claim");
var hubRoute=new EnvironmentRouteOrder(7,
    new[]{new EnvironmentDisplayTile(8,"Phone","Android",1,true)},
    MonitorEdge.Right);
Check(hubRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Right)==7 &&
    hubRoute.Neighbor(7,MonitorEdge.Right)==8 &&
    hubRoute.Neighbor(8,MonitorEdge.Left)==7,
    "Hub -> Windows -> Android -> Windows edge navigation uses one persistent environment");
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

// Verify authenticated FastUdpCodec encryption, AAD header binding, and replay rejection,
// plus FastMovePacer first-frame immediacy and reliable pre-click TCP coordinate flush.
{
    byte[] udpKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(FastUdpCodec.KeySize);
    using var srvCodec = new FastUdpCodec(udpKey, peerId: 9, isServer: true);
    using var cliCodec = new FastUdpCodec(udpKey, peerId: 9, isServer: false);
    using var otherCli = new FastUdpCodec(udpKey, peerId: 10, isServer: false);

    byte[] f1 = new byte[FastUdpCodec.FrameSize];
    Check(srvCodec.Seal(FastUdpOp.ReverseMove, 480, 960, 77, 10, f1) == FastUdpCodec.FrameSize,
        "FastUdpCodec must emit fixed 48-byte authenticated frame");
    Check(otherCli.TryOpen(f1, out _ ) == false,
        "Wrong peer ID cannot accept another peer's UDP packet");
    Check(cliCodec.TryOpen(f1, out var m1) && m1.Op == FastUdpOp.ReverseMove &&
        m1.A == 480 && m1.B == 960 && m1.Lease == 77,
        "Client must authenticate server UDP move");
    Check(!cliCodec.TryOpen(f1, out _),
        "Replayed UDP sequence must be rejected");

    byte[] f2 = new byte[FastUdpCodec.FrameSize];
    srvCodec.Seal(FastUdpOp.ReverseMove, 500, 990, 77, 14, f2);
    byte[] bad = (byte[])f2.Clone();
    bad[24] ^= 0x33;
    Check(!cliCodec.TryOpen(bad, out _),
        "Tampered UDP payload must fail AES-GCM authentication");
    Check(!srvCodec.TryOpen(f2, out _),
        "Server direction cannot decrypt its own server-originated frame");
    Check(cliCodec.TryOpen(f2, out var m2) && m2.A == 500 && m2.B == 990,
        "Legitimate next packet must still succeed after tampered frame");

    var udpSent = new List<Packet>();
    var tcpSent = new List<Packet>();
    using var pacer = new FastMovePacer(
        canSendUdp: () => true,
        sendUdp: pkt => { udpSent.Add(pkt); return true; },
        queueReliableTcp: pkt => tcpSent.Add(pkt));
    Check(pacer.TryHandleMove(new Packet("move", 10, 20)),
        "First move must go out immediately via FastUdp");
    Check(udpSent.Count == 1 && udpSent[0].A == 10 && udpSent[0].B == 20,
        "First UDP coordinate must transmit without delay");
    pacer.TryHandleMove(new Packet("move", 30, 40));
    pacer.TryHandleMove(new Packet("move", 55, 75));
    var sync = pacer.TakePendingReliableSync();
    Check(sync != null && sync.A == 55 && sync.B == 75,
        "Pre-click flush must extract the newest coordinate reliably for TCP before button press");
    Check(pacer.TakePendingReliableSync() == null,
        "Reliable sync must drain cleanly once flushed");

    pacer.SetMinIntervalMs(2.0);
    System.Threading.Thread.Sleep(5);
    Check(pacer.TryHandleMove(new Packet("move", 80, 90)),
        "Adjusted interval move must go out via FastUdp");
    var stats = pacer.TakeStats();
    Check(stats.UdpDirect >= 2,
        "FastMovePacer telemetry stats must count direct FastUdp dispatches");

    using (var clickPacer = new FastMovePacer(
        canSendUdp: () => true,
        sendUdp: _ => true,
        queueReliableTcp: _ => { }))
    {
        clickPacer.TryHandleMove(new Packet("move", 400, 900));
        clickPacer.TryHandleMove(new Packet("move", 455, 925)); // throttled inside MinUdpIntervalMs
        var preClickMove = clickPacer.TakeThrottledUdpMove();
        Check(preClickMove is { A: 455, B: 925 } &&
            clickPacer.TakeThrottledUdpMove() == null &&
            clickPacer.TakePendingReliableSync(skipIfUdpDelivered: true) == null,
            "Clicks/scrolls must flush pending throttled UDP move once without re-sending over TCP");
    }

    int seqCounter = 0;
    var orderedUdpMoves = new List<Packet>();
    using (var orderedPacer = new FastMovePacer(
        canSendUdp: () => true,
        sendUdp: pkt => { orderedUdpMoves.Add(pkt); return true; },
        queueReliableTcp: _ => { },
        nextSeq: () => Interlocked.Increment(ref seqCounter)))
    {
        orderedPacer.SetMinIntervalMs(6.5);
        orderedPacer.TryHandleMove(new Packet("move", 100, 200));
        orderedPacer.TryHandleMove(new Packet("move", 150, 260));
        Check(orderedPacer.FlushThrottledUdpMoveNow(),
            "Pre-click FlushThrottledUdpMoveNow must dispatch pending throttled move via ordered UDP gate");
        Check(orderedUdpMoves.Count == 2 &&
            orderedUdpMoves[0].C == 1 &&
            orderedUdpMoves[1].C == 2 && orderedUdpMoves[1].A == 150 &&
            orderedPacer.TakePendingReliableSync(skipIfUdpDelivered: true) == null,
            "Ordered move pacing must stamp monotonic move sequences and drain throttled buffer");
    }

    Check(Math.Abs(ReceiverServer.RecommendedUdpMinIntervalMs("Android", 120) - 6.5) < 0.01 &&
        Math.Abs(ReceiverServer.RecommendedUdpMinIntervalMs("Android", 90) - 8.0) < 0.01 &&
        Math.Abs(ReceiverServer.RecommendedUdpMinIntervalMs("Android", 60) - 10.0) < 0.01 &&
        Math.Abs(ReceiverServer.RecommendedUdpMinIntervalMs("Android", null) - ReceiverServer.AndroidUdpMinIntervalMs) < 0.01 &&
        Math.Abs(ReceiverServer.RecommendedUdpMinIntervalMs("Windows", 144) - 2.5) < 0.01,
        "RecommendedUdpMinIntervalMs must pace Android frames cleanly for display refresh rates without saturating 5 GHz airtime");

    // Verify generic adaptive pacing across live display refresh rates (50/60/90/120/144Hz)
    // and live network conditions (clean wired/fast link vs spiky/lossy wireless).
    {
        double wired144 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, rttMs: 2.0, minRttMs: 2.0, jitterMs: 0.5, lossPermille: 0);
        double cleanWifi144 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, rttMs: 5.5, minRttMs: 3.5, jitterMs: 2.5, lossPermille: 0);
        double normal50 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 50, rttMs: 10.0, minRttMs: 9.0, jitterMs: 2.0, lossPermille: 0);
        double busyWiFi = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 120, rttMs: 46.0, minRttMs: 7.0, jitterMs: 19.0, lossPermille: 24.0);
        double busy60Hz = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 60, rttMs: 55.0, minRttMs: 7.0, jitterMs: 25.0, lossPermille: 40.0);
        double winWired = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Windows", 144, rttMs: 1.8, minRttMs: 1.8, jitterMs: 0.4, lossPermille: 0);
        Check(wired144 is >= 4.2 and <= 6.0 &&
            cleanWifi144 is >= 4.2 and <= 5.5 &&
            normal50 >= 12.0 &&
            busyWiFi is > 8.0 and <= 14.0 &&
            busy60Hz is >= 10.0 and <= 18.5 &&
            winWired is >= 1.8 and <= 2.5,
            "ComputeAdaptiveUdpIntervalMs must adapt continuously without choking high-refresh wireless displays");

        // Verify transit-time jitter does NOT false-trigger during intermittent mouse pauses.
        var transitTracker = new AdaptiveLinkTracker();
        long[] pauseGapsMs = { 7, 7, 130, 7, 7, 95, 7, 110, 7, 7 };
        long rxMs = 1000;
        int txMs = 5000;
        for (ulong i = 1; i <= (ulong)pauseGapsMs.Length; i++)
        {
            long step = pauseGapsMs[i - 1];
            rxMs += step;
            txMs += (int)step;
            transitTracker.RecordInboundPacket(i, hasSenderTimestamp: true, txMs, rxMs);
        }
        Check(transitTracker.InboundJitterMs < 1.5,
            "Transit jitter must stay low across mouse pauses when transit latency is steady");

        rxMs += 40;
        txMs += 7; // +33ms real wireless transit delay spike
        transitTracker.RecordInboundPacket(11, hasSenderTimestamp: true, txMs, rxMs);
        rxMs += 2;
        txMs += 25; // catch-up burst
        transitTracker.RecordInboundPacket(12, hasSenderTimestamp: true, txMs, rxMs);
        Check(transitTracker.InboundJitterMs > 8.0,
            "Transit jitter must detect real network buffering and catch-up bursts");

        // Verify the 63-frame 2.4 GHz reorder window: buffered power-save
        // bursts are reordered, and a frame that still arrives after
        // settlement is reclassified from lost to late.
        var burstTracker = new AdaptiveLinkTracker("2.4GHz");
        long burstMs = 1000;
        for (ulong s = 1; s <= 30; s++)
        {
            burstMs += 16;
            burstTracker.RecordInboundPacket(s, true, burstMs);
        }
        for (ulong s = 40; s >= 31; s--)
        {
            burstMs += 1;
            burstTracker.RecordInboundPacket(s, true, burstMs);
        }
        Check(burstTracker.LostPackets == 0 && burstTracker.ReorderedPackets >= 9 &&
              burstTracker.InboundLossPermille < 1.0,
            "Buffered 2.4 GHz power-save bursts must count as reordered, not packet loss");

        var lateTracker = new AdaptiveLinkTracker("2.4GHz");
        long lateMs = 1000;
        for (ulong s = 1; s <= 69; s++)
        {
            lateMs += 8;
            lateTracker.RecordInboundPacket(s, true, lateMs);
        }
        for (ulong s = 71; s <= 133; s++)
        {
            lateMs += 8;
            lateTracker.RecordInboundPacket(s, true, lateMs);
        }
        Check(lateTracker.LostPackets == 1 && lateTracker.LatePackets == 0,
            "Frame missing past the 63-frame slack must settle as lost");
        lateMs += 8;
        lateTracker.RecordInboundPacket(70, true, lateMs);
        Check(lateTracker.LostPackets == 0 && lateTracker.LatePackets == 1,
            "Frame arriving after settlement must be reclassified as late, not lost");
        double lateLoss = lateTracker.InboundLossPermille;
        for (ulong s = 134; s <= 200; s++)
        {
            lateMs += 8;
            lateTracker.RecordInboundPacket(s, true, lateMs);
        }
        Check(lateLoss > 0 && lateTracker.InboundLossPermille < lateLoss * 0.25 && lateLoss < 100.0,
            "EWMA loss must decay briskly after a brief 2.4 GHz burst drop");
    }

    // Verify AdbCableBridge wired-device parsing & reverse-rule verification.
    {
        string sampleAdbDevices = """
            * daemon started successfully
            List of devices attached
            192.168.1.2:5555	device product:xagain model:22041216I
            adb-UK9DYX4XF6KZSWGA-AbCdEf._adb-tls-connect._tcp	device
            UNAUTHORIZED123	unauthorized
            OFFLINE456	offline
            UK9DYX4XF6KZSWGA	device usb:1-3 product:xagain model:22041216I
            UK9DYX4XF6KZSWGA	device usb:1-3
            SERIAL2ABC	device
            """;
        var serials = AdbCableBridge.ParseWiredAdbSerials(sampleAdbDevices);
        Check(serials.Count == 2 &&
            serials[0] == "UK9DYX4XF6KZSWGA" &&
            serials[1] == "SERIAL2ABC",
            "ParseWiredAdbSerials must select unique wired ADB serials and ignore wireless/unauthorized targets");

        string sampleReverse = """
            UsbFfs tcp:37377 tcp:37377
            UsbFfs tcp:37378 tcp:37378
            """;
        Check(AdbCableBridge.HasReverseMappings(sampleReverse, 37377, 37378) &&
            !AdbCableBridge.HasReverseMappings("UsbFfs tcp:37377 tcp:37377\n", 37377, 37378),
            "HasReverseMappings must require both control (37377) and discovery (37378) loopback rules");
    }

    // Verify NearbyComputerDiscovery loopback TCP beacon for direct data-cable discovery.
    {
        string testFp = new string('C', 64);
        using var cableBeacon = new NearbyComputerDiscovery(testFp,
            canAcceptConnections: () => true,
            isEnvironmentHub: () => true);
        cableBeacon.StartLoopbackOnly(0);
        using var probe = new System.Net.Sockets.TcpClient();
        probe.Connect(System.Net.IPAddress.Loopback, cableBeacon.LoopbackPort);
        using var reader = new StreamReader(probe.GetStream(), System.Text.Encoding.ASCII);
        string? line1 = reader.ReadLine();
        string? line2 = reader.ReadLine();
        Check(line1 != null && line1.StartsWith("DT1-READY|", StringComparison.Ordinal) &&
            line1.EndsWith("|" + testFp, StringComparison.Ordinal) &&
            line2 == "DT1-HUB|" + testFp,
            "NearbyComputerDiscovery loopback listener must advertise READY + HUB lines for data-cable Android clients");
    }

    // Verify 64-packet sliding anti-replay window + LinkPing/LinkFeedback + AdaptiveLinkTracker.
    {
        byte[] reorderKey = Enumerable.Range(51, 32).Select(x => (byte)x).ToArray();
        using var rsrv = new FastUdpCodec(reorderKey, 33, isServer: true);
        using var rcli = new FastUdpCodec(reorderKey, 33, isServer: false);
        byte[] p1 = new byte[FastUdpCodec.FrameSize];
        byte[] p2 = new byte[FastUdpCodec.FrameSize];
        byte[] p3 = new byte[FastUdpCodec.FrameSize];
        byte[] p4 = new byte[FastUdpCodec.FrameSize];
        byte[] p5 = new byte[FastUdpCodec.FrameSize];
        rsrv.Seal(FastUdpOp.LinkPing, 99, 65, 0, 1000, p1);
        rsrv.Seal(FastUdpOp.ReverseButton, 1, 1, 7, 1, p2);
        rsrv.Seal(FastUdpOp.ReverseMove, 100, 200, 7, 2, p3);
        rsrv.Seal(FastUdpOp.ReverseWheel, 120, 0, 7, 3, p4);
        rsrv.Seal(FastUdpOp.LinkFeedback, 99, AdaptiveLinkTracker.PackLossJitter(15, 6.2), 0, AdaptiveLinkTracker.PackDisplayRate(50, 200), p5);

        // Deliver seq 1, 3, 5, 4 (skipping 2 temporarily), then deliver reordered seq 2 cleanly.
        Check(rcli.TryOpen(p1, out var o1) && o1.Op == FastUdpOp.LinkPing && o1.A == 99 &&
            rcli.TryOpen(p3, out var o3) && o3.Seq == 3 &&
            rcli.TryOpen(p5, out var o5) && o5.Op == FastUdpOp.LinkFeedback &&
            rcli.TryOpen(p4, out var o4) && o4.Seq == 4 &&
            rcli.TryOpen(p2, out var o2) && o2.Op == FastUdpOp.ReverseButton && o2.Seq == 2 &&
            !rcli.TryOpen(p2, out _),
            "FastUdpCodec 64-packet sliding window must accept reordered legitimate frames while rejecting duplicates");

        var tracker = new AdaptiveLinkTracker();
        int pingTok = tracker.NextPingToken(1000);
        Check(tracker.OnFeedbackReceived(pingTok,
                AdaptiveLinkTracker.PackLossJitter(12, 8.5),
                AdaptiveLinkTracker.PackDisplayRate(144, 69),
                1028) &&
            tracker.PeerRefreshHz == 144 &&
            tracker.PeerFrameTenthsMs == 69 &&
            Math.Abs(tracker.SmoothedRttMs - 28.0) < 0.1,
            "AdaptiveLinkTracker must round-trip live RTT, loss, jitter, and peer refresh rate");

        ulong[] seqs = { 1, 2, 3, 4, 5, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
        long tickMs = 500;
        foreach (ulong s in seqs)
        {
            tickMs += 7;
            tracker.RecordInboundPacket(s, true, tickMs);
        }
        Check(tracker.ReorderedPackets >= 1 && tracker.LostPackets == 0,
            "Brief wireless reordering must not inflate real packet-loss counts");
        for (ulong s = 16; s <= 100; s++)
        {
            if (s == 18) continue;
            tickMs += 7;
            tracker.RecordInboundPacket(s, true, tickMs);
        }
        Check(tracker.LostPackets == 1,
            "Truly dropped packet past reorder slack must count cleanly as packet loss");

        using var dynPacer = new FastMovePacer(
            canSendUdp: () => true,
            sendUdp: _ => true,
            queueReliableTcp: _ => { });
        dynPacer.SetMinIntervalMs(14.5);
        dynPacer.SetWarmIntervalMs(36);
        Check(Math.Abs(dynPacer.EffectiveMinIntervalMs - 14.5) < 0.01,
            "FastMovePacer must expose live adaptive pacing interval");

        var idleTracker = new AdaptiveLinkTracker("2.4GHz");
        long idleMs = 5000;
        for (ulong s = 1; s <= 10; s++)
        {
            idleMs += 8;
            idleTracker.RecordInboundPacket(s, hasSenderTimestamp: false, 0, idleMs, interactiveOp: true);
        }
        for (ulong s = 11; s <= 120; s++)
        {
            idleMs += 24;
            if (s % 5 == 0) continue;
            idleTracker.RecordInboundPacket(s, hasSenderTimestamp: false, 0, idleMs, interactiveOp: false);
        }
        Check(idleTracker.LostPackets == 0 && idleTracker.InboundLossPermille < 0.01,
            "Idle keepalive/probe drops during mouse pause must not fabricate mouse packet loss");
        Check(!AdaptiveLinkTracker.HasSenderTimestamp(FastUdpOp.Warm) &&
              !AdaptiveLinkTracker.HasSenderTimestamp(FastUdpOp.LinkPing),
            "Coarse background Warm/LinkPing timers must never inject tick quantization into transit jitter");
    }

    byte[] extSrvKey = Enumerable.Range(21, 32).Select(x => (byte)x).ToArray();
    using (var srvCodec2 = new FastUdpCodec(extSrvKey, 12, isServer: true))
    using (var cliCodec2 = new FastUdpCodec(extSrvKey, 12, isServer: false))
    {
        byte[] fClaim = new byte[FastUdpCodec.FrameSize];
        byte[] fBtn = new byte[FastUdpCodec.FrameSize];
        byte[] fWheel = new byte[FastUdpCodec.FrameSize];
        byte[] fGrant = new byte[FastUdpCodec.FrameSize];
        byte[] fActive = new byte[FastUdpCodec.FrameSize];
        srvCodec2.Seal(FastUdpOp.ReverseClaim, 55, 0, 55, 9, fClaim);
        srvCodec2.Seal(FastUdpOp.ReverseButton, 1, 1, 55, 200, fBtn);
        srvCodec2.Seal(FastUdpOp.ReverseWheel, -120, 0, 55, 201, fWheel);
        cliCodec2.Seal(FastUdpOp.ReverseGranted, 55, 1, 55, 0, fGrant);
        cliCodec2.Seal(FastUdpOp.ReverseActive, 55, 0, 55, 0, fActive);
        Check(cliCodec2.TryOpen(fClaim, out var mClaim) && mClaim.Op == FastUdpOp.ReverseClaim && mClaim.TimestampMs == 9 &&
            cliCodec2.TryOpen(fBtn, out var mBtn) && mBtn.Op == FastUdpOp.ReverseButton && mBtn.TimestampMs == 200 &&
            cliCodec2.TryOpen(fWheel, out var mWheel) && mWheel.Op == FastUdpOp.ReverseWheel && mWheel.A == -120 && mWheel.TimestampMs == 201 &&
            srvCodec2.TryOpen(fGrant, out var mGrant) && mGrant.Op == FastUdpOp.ReverseGranted && mGrant.B == 1 &&
            srvCodec2.TryOpen(fActive, out var mActive) && mActive.Op == FastUdpOp.ReverseActive && mActive.Lease == 55,
            "FastUdpCodec must authenticate extended fast-lane claim/button/wheel/grant/active ops cleanly");
    }

    long warmEchoTicks = 0;
    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    Check(ReceiverServer.CanEmitAndroidWarmEcho(t0, ref warmEchoTicks) &&
        !ReceiverServer.CanEmitAndroidWarmEcho(t0 + (long)(0.05 * System.Diagnostics.Stopwatch.Frequency), ref warmEchoTicks) &&
        ReceiverServer.CanEmitAndroidWarmEcho(t0 + (long)(0.30 * System.Diagnostics.Stopwatch.Frequency), ref warmEchoTicks),
        "Android warm echo must be rate-limited so idle 5GHz airtime is never flooded");

    Check(ReceiverServer.ShouldEchoAndroidWarm("Android") &&
        !ReceiverServer.ShouldEchoAndroidWarm("Windows") &&
        !ReceiverServer.ShouldEchoAndroidWarm(null) &&
        ReceiverServer.AndroidUdpMinIntervalMs <= 10.0 &&
        ReceiverServer.AndroidUdpMinIntervalMs >= 5.0,
        "Android fast lane keeps display-paced 5 GHz timing and throttled interactive wireless echo");

    using var hub = new FastUdpHub();
    var tempPeer = hub.AddPeer(99, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0))!;
    byte[] peerKey = Convert.FromBase64String(tempPeer.Base64Key);
    // Simulate Windows Firewall dropping the client's initial t=0 probes by removing/adding the peer after construction.
    hub.RemovePeer(tempPeer);
    using var udpClient = new FastUdpClient(System.Net.IPAddress.Loopback, hub.Port, 2, peerKey);
    System.Threading.Thread.Sleep(40);
    Check(!udpClient.Confirmed,
        "Initial dropped probes must not self-confirm");
    var hubPeer = hub.AddPeer(2, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0))!;
    // Re-key client to match the real hub peer once the hub opens its firewall pinhole.
    using var udpClient2 = new FastUdpClient(
        System.Net.IPAddress.Loopback, hub.Port, 2, Convert.FromBase64String(hubPeer.Base64Key));
    hub.ConfigurePeerPort(hubPeer, udpClient2.LocalPort);
    var deadline = DateTime.UtcNow.AddMilliseconds(1500);
    while (DateTime.UtcNow < deadline && !(udpClient2.Confirmed && hubPeer.Confirmed))
    {
        if (!udpClient2.Confirmed)
            udpClient2.SendProbe();
        System.Threading.Thread.Sleep(20);
    }
    Check(udpClient2.Confirmed && hubPeer.Confirmed,
        "FastUdpClient must confirm bidirectional fast path even after pre-firewall probes drop");
}
Console.WriteLine("PASS: FastUdp AES-256-GCM replay protection, delayed firewall handshake, and move pacing verified.");

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

Check(DeskTouch.DeviceShare.Program.ShouldShowExistingInstance(Array.Empty<string>()) &&
    !DeskTouch.DeviceShare.Program.ShouldShowExistingInstance(new[] { "--tray" }) &&
    !DeskTouch.DeviceShare.Program.ShouldShowExistingInstance(new[] { "--TRAY" }),
    "Normal relaunch wakes an existing DeskTouch instance while --tray does not force popup");

var primaryWa = new System.Drawing.Rectangle(0, 0, 1920, 1032);
var visibleWindow = new System.Drawing.Rectangle(400, 180, 1000, 690);
Check(DisplaySettingsForm.EnsureVisibleBounds(visibleWindow, new[] { primaryWa }, primaryWa) == visibleWindow,
    "Window already visible inside a display keeps its saved position");
var offscreenWindow = new System.Drawing.Rectangle(-32000, -32000, 1000, 690);
var restoredOffscreen = DisplaySettingsForm.EnsureVisibleBounds(offscreenWindow, new[] { primaryWa }, primaryWa);
Check(restoredOffscreen.Location == new System.Drawing.Point(460, 171) &&
    restoredOffscreen.Size == new System.Drawing.Size(1000, 690),
    "Off-screen or minimized coordinates restore centered inside the visible working area");
var topHiddenWindow = new System.Drawing.Rectangle(300, -120, 1000, 690);
var restoredTop = DisplaySettingsForm.EnsureVisibleBounds(topHiddenWindow, new[] { primaryWa }, primaryWa);
Check(restoredTop.Top >= primaryWa.Top,
    "Window whose title bar drifted above the monitor top edge resets inside working area");
Console.WriteLine("PASS: Single-instance wakeup policy and visible window bounds clamping verified.");

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

// --- Shell Bridge daemon parsing and script hygiene invariants ---
string mixedAdbOutput = """
    List of devices attached
    UK9DYX4XF6KZSWGA       device product:xaga model:22041216I transport_id:4
    192.168.1.50:5555      device product:xaga transport_id:7
    offline_dev            offline
    unauth_dev             unauthorized
    """;
IReadOnlyList<string> wiredOnly = AdbCableBridge.ParseWiredAdbSerials(mixedAdbOutput);
IReadOnlyList<string> allConnected = AdbCableBridge.ParseConnectedAdbSerials(mixedAdbOutput);
Check(wiredOnly.Count == 1 && wiredOnly[0] == "UK9DYX4XF6KZSWGA",
    "Wired ADB serial filter must still exclude wireless targets");
Check(allConnected.Count == 2 && allConnected.Contains("192.168.1.50:5555"),
    "Connected ADB serial parser must include authorized wireless or USB devices for daemon launch");
Check(AdbCableBridge.ParsePortOutput("PORT=41234\n") == 41234 &&
    AdbCableBridge.ParsePortOutput("NO_PORT\n") == 0 &&
    AdbCableBridge.ParsePortOutput("NO_APP\n") == 0 &&
    AdbCableBridge.ParsePortOutput("PORT=99999\n") == 0,
    "Shell daemon port parser must validate TCP port bounds");
string daemonScript = AdbCableBridge.BuildShellDaemonScript();
string checkScript = AdbCableBridge.BuildShellCheckScript(41234);
Check(daemonScript.Contains("umask 077", StringComparison.Ordinal) &&
    daemonScript.Contains("IFS= read -r TK", StringComparison.Ordinal) &&
    daemonScript.Contains("com.desktouch.vision.ShellInputDaemon", StringComparison.Ordinal) &&
    daemonScript.Contains("--pid-file /data/local/tmp/dt_s.pid", StringComparison.Ordinal) &&
    daemonScript.Contains("rm -f /data/local/tmp/dt_s.tok", StringComparison.Ordinal) &&
    !daemonScript.Contains("pkill -f", StringComparison.Ordinal) &&
    checkScript.Contains("kill -0", StringComparison.Ordinal) &&
    !checkScript.Contains("NO_PORT", StringComparison.Ordinal) &&
    checkScript.Contains("41234", StringComparison.Ordinal),
    "Shell Bridge launcher script must pass token only via stdin 077 file unlinked before exec without pkill self-match");
Console.WriteLine("PASS: Accessibility-free Shell Bridge ADB parsers and launch scripts enforce safety invariants.");

// Verify live Windows Taskbar capture, wire-size bounds, and strict action token allowlist.
{
    Check(WindowsTaskbarHelper.IsValidActionKeyFormat("__start__") &&
        WindowsTaskbarHelper.IsValidActionKeyFormat("__search__") &&
        WindowsTaskbarHelper.IsValidActionKeyFormat("w1A2B3C") &&
        WindowsTaskbarHelper.IsValidActionKeyFormat("p0"),
        "WindowsTaskbarHelper must accept valid Start, Search, window, and pinned action tokens");
    Check(!WindowsTaskbarHelper.IsValidActionKeyFormat("") &&
        !WindowsTaskbarHelper.IsValidActionKeyFormat(null) &&
        !WindowsTaskbarHelper.IsValidActionKeyFormat("cmd.exe") &&
        !WindowsTaskbarHelper.IsValidActionKeyFormat("C:\\Windows\\System32\\cmd.exe") &&
        !WindowsTaskbarHelper.IsValidActionKeyFormat("w_bad_token") &&
        !WindowsTaskbarHelper.ExecuteAction("C:\\Windows\\System32\\cmd.exe") &&
        !WindowsTaskbarHelper.ExecuteAction("wDEADBEEF9999"),
        "WindowsTaskbarHelper must reject arbitrary file paths, shell commands, and unmapped window tokens");
    var sampleItems = Enumerable.Range(0, 15)
        .Select(i => new WindowsTaskbarHelper.TaskbarItemDto(
            $"w{i + 1:X}", $"App Window {i + 1}", $"App{i + 1}", i == 0, false, true, "A1B2C3D4"))
        .ToArray();
    string? cappedJson = WindowsTaskbarHelper.SerializeCapped(0, sampleItems, 7500);
    Check(!string.IsNullOrEmpty(cappedJson) &&
        System.Text.Encoding.UTF8.GetByteCount(cappedJson) <= 7500,
        "Serialized taskbar-state payload must always stay within the 8192-byte wire frame limit");
    var liveCapture = WindowsTaskbarHelper.CaptureSnapshot(0);
    Check(!string.IsNullOrEmpty(liveCapture.StateJson) &&
        System.Text.Encoding.UTF8.GetByteCount(liveCapture.StateJson) <= 7500,
        "Live WindowsTaskbarHelper capture must produce valid bounded JSON");
    Console.WriteLine("PASS: Live Windows Taskbar snapshot, icon framing bounds, and strict token allowlist verified.");
}

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
    using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        var point = primary != null ? new System.Drawing.Point(primary.Bounds.Left + 100, primary.Bounds.Top + 100) : new System.Drawing.Point(100, 100);
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
    Check(SourceCapture.AndroidPrintableKey(0x41,false)=="a" &&
        SourceCapture.AndroidPrintableKey(0x41,true)=="A" &&
        SourceCapture.AndroidPrintableKey(0x31,true)=="!" &&
        SourceCapture.AndroidPrintableKey(0x20,false)==" " &&
        SourceCapture.AndroidPrintableKey(0x70,false)==null,
        "Android text route must handle letters, shifted digits and preserve function keys");
    Check(SourceCapture.ShouldToggleMobileShortcut(true,0x4D,true,true,false) &&
        !SourceCapture.ShouldToggleMobileShortcut(false,0x4D,true,true,false) &&
        !SourceCapture.ShouldToggleMobileShortcut(true,0x4D,true,true,true) &&
        !SourceCapture.ShouldToggleMobileShortcut(true,0x41,true,true,false),
        "Ctrl+Alt+M down without Shift toggles PC<->Mobile cleanly");
    string logTestDir=Path.Combine(Path.GetTempPath(),"dt-log-rot-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(logTestDir);
    try
    {
        string testLog=Path.Combine(logTestDir,"connection.log");
        File.WriteAllText(testLog,new string('x',256));
        DisplaySettingsForm.RotateLogIfNeeded(testLog,maxBytes:1024);
        Check(File.Exists(testLog) && !File.Exists(Path.Combine(logTestDir,"connection.prev.log")),
            "Small connection.log stays in place");
        File.WriteAllText(testLog,new string('y',2048));
        DisplaySettingsForm.RotateLogIfNeeded(testLog,maxBytes:1024);
        Check(!File.Exists(testLog) && File.Exists(Path.Combine(logTestDir,"connection.prev.log")),
            "Oversized connection.log rotates cleanly to connection.prev.log");
    }
    finally { try{Directory.Delete(logTestDir,true);}catch{} }
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

// A hub/leaf roster can contain multiple phones plus Windows members.
// The selected target remains in the original draggable monitor arrangement;
// every OTHER online member must appear as a visible, selectable tile.
{
    var devices=new[] {
        new EnvironmentDisplayTile(0,"SURAJ","Windows",1,true),
        new EnvironmentDisplayTile(-2,"My Android Phone","Android",1,true),
        new EnvironmentDisplayTile(-3,"Second Android","Android",1,false),
        new EnvironmentDisplayTile(-4,"DESKTOP-IQ1KSJ9","Windows",2,true),
        new EnvironmentDisplayTile(-2,"duplicate roster callback","Android",1,true)
    };
    var visible=DisplayArrangementControl.ProjectEnvironment(devices,0);
    Check(visible.Count==3,
        "Environment diagram must show two Android phones and other Windows peers");
    Check(visible.Count(v=>v.IsAndroid)==2 &&
        visible.Any(v=>v.Name=="Second Android"&&!v.SupportsInput),
        "Android phones must appear even when input Accessibility is not enabled");
    Check(!visible.Any(v=>v.TargetId==0) &&
        visible.Select(v=>v.TargetId).Distinct().Count()==visible.Count,
        "Selected device must not be drawn twice or lose its route identity");
    Check(DisplayArrangementControl.ProjectEnvironment(devices,-2)
        .Any(v=>v.TargetId==0),
        "Hub must appear as another selectable member when Android is selected");
    var mainScreens=new System.Drawing.Rectangle(0,0,3800,1080);
    var inline=DisplayArrangementControl.ComposeInlineDevices(mainScreens,visible);
    Check(inline.Count==3 && inline[0].World.Left>=mainScreens.Right,
        "Android/Windows environment devices must render on the MAIN display row");
    Check(inline.All(item=>item.World.Top<mainScreens.Bottom &&
        item.World.Bottom>mainScreens.Top),
        "No authenticated phone may be moved into a separate bottom display deck");
    Check(inline.Zip(inline.Skip(1),(first,second)=>
        second.World.Left>=first.World.Right).All(ok=>ok),
        "Screens must have non-overlapping inline hit targets");
    Check(inline.Where(item=>item.Device.IsAndroid)
        .All(item=>item.World.Height>item.World.Width),
        "Android members must be displayed as portrait screen tiles, not footer labels");
    var phoneWorld=new System.Drawing.Rectangle(0,0,5000,900);
    var phonePreview=DisplayArrangementControl.ComposeInlineDevices(phoneWorld,
        new[]{new EnvironmentDisplayTile(93,"Xiaomi","Android",1,true)})[0].World;
    foreach(float scale in new[]{.12f,.24f,.45f})
    {
        var phoneRegions=new List<(int Id,System.Drawing.RectangleF Bounds)>{
            (7,new System.Drawing.RectangleF(0,0,
                phoneWorld.Width*scale,phoneWorld.Height*scale)),
            (93,new System.Drawing.RectangleF(
                phonePreview.X*scale,phonePreview.Y*scale,
                phonePreview.Width*scale,phonePreview.Height*scale))
        };
        double crossingFraction=(phonePreview.Top+phonePreview.Height/2d)/
            phoneWorld.Height;
        Check(DisplayArrangementControl.SpatialNeighbor(phoneRegions,7,
            MonitorEdge.Right,crossingFraction)==93,
            "Connected Android portrait tile must accept Windows right-edge " +
            "handoff at diagram scale "+scale);
        Check(DisplayArrangementControl.SpatialNeighbor(phoneRegions,93,
            MonitorEdge.Left,.5)==7,
            "Android left-edge return must reach its original Windows peer");
    }
    var viewOnlyBeforePhone=DisplayArrangementControl.ProjectEnvironment(
        new[]{
            new EnvironmentDisplayTile(91,"A view-only Android","Android",1,false),
            new EnvironmentDisplayTile(93,"Z controllable Xiaomi","Android",1,true)
        },0);
    Check(viewOnlyBeforePhone.First().TargetId==93 &&
        viewOnlyBeforePhone.Last().TargetId==91,
        "Non-input Android devices must not occupy the default adjacent " +
        "slot and block Windows-to-phone cursor entry");
    Console.WriteLine("PASS: Android default edge handoff independent of diagram zoom and view-only neighbors.");
    var spatial=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (EnvironmentRouteOrder.Local,new(0,0,300,220)),
        (7,new(330,0,300,220)),
        (-5,new(330,250,165,255)),
        (-6,new(-190,0,165,260))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(spatial,7,
        MonitorEdge.Bottom)==-5 &&
        DisplayArrangementControl.SpatialNeighbor(spatial,-5,
        MonitorEdge.Top)==7,
        "Dragging Xiaomi BELOW Windows must route keyboard/mouse through bottom/top edge");
    Check(DisplayArrangementControl.SpatialNeighbor(spatial,
        EnvironmentRouteOrder.Local,MonitorEdge.Left)==-6,
        "Dragging an authorized Android to the left of Windows must use its left edge");
    Check(DisplayArrangementControl.SpatialNeighbor(spatial,7,
        MonitorEdge.Left)==EnvironmentRouteOrder.Local,
        "PC must remain reachable when Android is visible below another PC");
    var farScreens=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(900,0,220,220))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(farScreens,1,
        MonitorEdge.Right)==null,
        "Distant screen on same side must never capture second PC's right edge");
    var wrongBand=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(310,175,170,220))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(wrongBand,1,
        MonitorEdge.Right,0.1)==null &&
        DisplayArrangementControl.SpatialNeighbor(wrongBand,1,
        MonitorEdge.Right,0.95)==2,
        "Only the touching vertical part of a screen edge can route to Android");
    var overlap=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,new(150,40,100,160))
    };
    Check(DisplayArrangementControl.SpatialNeighbor(overlap,1,
        MonitorEdge.Right)==null,
        "Overlapping Android preview is not a valid outside screen boundary");
    var attached=DisplayArrangementControl.SnapToConnectedScreen(
        new System.Drawing.RectangleF(155,60,90,150),
        new[]{new System.Drawing.RectangleF(0,0,300,220)});
    Check(System.Drawing.RectangleF.Intersect(attached,
        new System.Drawing.RectangleF(0,0,300,220)).IsEmpty,
        "Dragged phone dropped inside Windows must snap OUTSIDE the screen");
    var snappedScreens=new List<(int Id,System.Drawing.RectangleF Bounds)>{
        (1,new(0,0,300,220)),(2,attached)
    };
    Check(new[]{MonitorEdge.Left,MonitorEdge.Right,MonitorEdge.Top,MonitorEdge.Bottom}
        .Any(side=>DisplayArrangementControl.SpatialNeighbor(snappedScreens,
            1,side)==2),
        "Snapped Xiaomi must be reachable by crossing an actual adjacent edge");
    var liveRoute=new EnvironmentRouteOrder(
        new[]{EnvironmentRouteOrder.Local,7,-2,-3});
    Check(liveRoute.Neighbor(7,MonitorEdge.Right)==-2 &&
        liveRoute.Neighbor(-2,MonitorEdge.Left)==7,
        "Keyboard/mouse moves between neighboring PC and Android without separate pairing");
    var reversedRoute=new EnvironmentRouteOrder(
        new[]{-2,EnvironmentRouteOrder.Local,7});
    Check(reversedRoute.Neighbor(EnvironmentRouteOrder.Local,MonitorEdge.Left)==-2 &&
        reversedRoute.Neighbor(-2,MonitorEdge.Right)==EnvironmentRouteOrder.Local,
        "Dragging the phone to the other side reverses real cursor-edge routing");
    Console.WriteLine("PASS: multiple authenticated Android device tiles, view-only phones, and no duplicate Windows monitors.");
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
            .Any(b=>b.Text=="Get Android app") &&
            actionRow.Controls.OfType<System.Windows.Forms.Button>()
            .All(b=>!b.Text.Contains("HID",StringComparison.OrdinalIgnoreCase) &&
                    !b.Text.Contains("Bluetooth",StringComparison.OrdinalIgnoreCase)),
            "UI must include Get Android app without unused Bluetooth/HID buttons");
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
                new EnvironmentDisplayTile(-5,"Android Phone A","Android",1,true),
                new EnvironmentDisplayTile(-6,"Android Phone B","Android",1,false),
                new EnvironmentDisplayTile(-7,"Windows peer","Windows",2,true)
            },0);
        Check(diagram.EnvironmentDevices.Count==3 &&
            diagram.EnvironmentDevices.Count(t=>t.IsAndroid)==2,
            "Display diagram must retain every connected phone alongside native screens");

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
        // Simulate a physical mouse-down/move/up on the ACTUAL phone tile.
        // Prior builds only clicked it to swap a single target; this must
        // drag the phone while retaining independent mouse target selection.
        using(var bitmap=new System.Drawing.Bitmap(diagram.Width,diagram.Height))
            diagram.DrawToBitmap(bitmap,
                new System.Drawing.Rectangle(0,0,diagram.Width,diagram.Height));
        var phoneHit=diagram.OnlineHitRegions.First(x=>x.TargetId==-5).Hit;
        var origin=new System.Drawing.Point((int)phoneHit.X+Math.Max(8,(int)phoneHit.Width/3),
            (int)phoneHit.Y+Math.Max(8,(int)phoneHit.Height/3));
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
            "Android tile must DRAG and persist its position, not switch to a one-to-one target");
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
            "Tapping a phone should select it; dragging should only move its screen tile");
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
            new MonitorRect("ANDROID",0,0,1080,2400,true)});
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
            enter.Invoke(capture,new object[]{new System.Drawing.Point(1890,440),false});
            Check(recorderA.Packets.Any(p=>p.Type=="claim"),
                "First Windows PC must receive the original claim");
            capture.Retarget(recorderB,MonitorEdge.Right);
            Check(recorderA.Packets.Any(p=>p.Type=="release") &&
                !recorderB.Packets.Any(p=>p.Type=="release") &&
                capture.SameInputSession(recorderB) &&
                capture.ActiveRemoteMonitors.Bounds==monitorB.Bounds,
                "Old Windows PC must receive release before Android replaces the destination");
            capture.BeginLinkedHandoff(MonitorEdge.Right,0.4);
            Check(recorderB.Packets.Any(p=>p.Type=="claim") &&
                !recorderA.Packets.Skip(2).Any(p=>p.Type=="claim"),
                "Android receives the next claim over the existing capture, without new hook installation");
            capture.ReturnLocal();
            capture.Retarget(recorderA,MonitorEdge.Right,
                continuingRemoteHandoff:true);
            capture.BeginDirectHandoff();
            Check(recorderA.Packets.Count(p=>p.Type=="claim")>=2,
                "BeginDirectHandoff must issue claim for Ctrl+Alt+M shortcut transitions");
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

        // v0.3.28 phone display info, real-ratio inline preview, smooth Android mouse normalization,
        // modifier key-up cleanup on ReturnLocal, zero-stall UDP click sync, and DT1-ONLINE beacon.
        var portraitLayout = "[{\"Name\":\"Android screen\",\"X\":0,\"Y\":0,\"Width\":1080,\"Height\":2400,\"Primary\":true,\"DensityDpi\":440,\"RefreshHz\":120,\"RotationDegrees\":0,\"Orientation\":\"Portrait\",\"OsVersion\":\"Android 15\",\"AppVersion\":\"0.3.28\"}]";
        var helloJson = System.Text.Json.JsonSerializer.Serialize(new PeerHello(
            "Redmi Note", portraitLayout, true, "A".PadRight(64, 'A'), "Android", true,
            "Android 15", "0.3.28", 440, 120, 0));
        var parsedPeer = ReceiverServer.ReadHello(helloJson);
        string screenSummary = parsedPeer.FormatScreenInfo();
        Check(parsedPeer.OsVersion == "Android 15" && parsedPeer.AppVersion == "0.3.28" &&
            parsedPeer.DensityDpi == 440 && parsedPeer.RefreshHz == 120 &&
            screenSummary.Contains("1080 × 2400") && screenSummary.Contains("440 dpi") &&
            screenSummary.Contains("120 Hz") && screenSummary.Contains("Android 15"),
            "ConnectedPeer must preserve and format full Android display dimensions and info");

        var portraitTopo = MonitorTopology.FromWire(portraitLayout);
        var landscapeTopo = MonitorTopology.FromWire(
            "[{\"Name\":\"Android screen\",\"X\":0,\"Y\":0,\"Width\":2400,\"Height\":1080,\"Primary\":true,\"RotationDegrees\":90,\"Orientation\":\"Landscape\"}]");
        var composedPortrait = DisplayArrangementControl.ComposeInlineDevices(
            new System.Drawing.Rectangle(0, 0, 1920, 1080),
            new[] { new EnvironmentDisplayTile(1, "Phone", "Android", 1, true, "FP1", portraitTopo) });
        var composedLandscape = DisplayArrangementControl.ComposeInlineDevices(
            new System.Drawing.Rectangle(0, 0, 1920, 1080),
            new[] { new EnvironmentDisplayTile(1, "Phone", "Android", 1, true, "FP1", landscapeTopo) });
        Check(composedPortrait[0].World.Width < composedPortrait[0].World.Height,
            "Portrait phone tile must reflect vertical phone proportions");
        Check(composedLandscape[0].World.Width > composedLandscape[0].World.Height,
            "Landscape rotated phone tile must reflect horizontal phone proportions");

        double androidStep = SourceCapture.ScaleRawDeltaPrecise(20, 10, true, 440);
        double winStep = SourceCapture.ScaleRawDeltaPrecise(20, 10, false);
        Check(androidStep > 0 && androidStep < winStep,
            "Android mouse delta must be normalized rather than amplified with desktop 1.45x factor");
        Check(Math.Abs(SourceCapture.ScaleRawDeltaPrecise(20, 10, true, 440, 2.0) - androidStep * 2.0) < 0.01 &&
            Math.Abs(SourceCapture.ScaleRawDeltaPrecise(20, 10, false, null, 0.5) - winStep * 0.5) < 0.01,
            "Pointer sensitivity multiplier must scale both Android and Windows raw deltas cleanly");
        Check(SourceCapture.ModifiersForMask(1 | 2 | 4).SequenceEqual(new[] { 0x11, 0x12, 0x10 }),
            "ModifiersForMask must extract held Ctrl, Alt, and Shift virtual keys");

        var capSession = new RecordingInputSession(portraitTopo);
        using (var modCapture = new SourceCapture(diagram, capSession, _ => { }, MonitorEdge.Right, preserveRawInput: true))
        {
            typeof(SourceCapture).GetField("_active",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(modCapture, 1);
            typeof(SourceCapture).GetField("_activeClaim",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(modCapture, 77);
            typeof(SourceCapture).GetField("_remoteModifierDownMask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(modCapture, 5); // Ctrl + Shift
            modCapture.ReturnLocal();
            var relIdx = capSession.Packets.FindIndex(p => p.Type == "release");
            var keyUps = capSession.Packets.Take(relIdx).Where(p => p.Type == "key" && p.C == 0).Select(p => p.D).ToArray();
            Check(relIdx >= 2 && keyUps.Contains(0x11) && keyUps.Contains(0x10),
                "ReturnLocal must release held remote modifier keys before releasing the remote lease");
        }

        using (var pacer = new FastMovePacer(
            canSendUdp: () => true,
            sendUdp: _ => true,
            queueReliableTcp: _ => { }))
        {
            pacer.TryHandleMove(new Packet("move", 400, 900));
            Check(pacer.TakePendingReliableSync(skipIfUdpDelivered: true) == null,
                "Clicks must skip redundant pre-click TCP move flush when UDP already sent the latest move");
            pacer.TryHandleMove(new Packet("move", 420, 910));
            Check(pacer.TakePendingReliableSync(skipIfUdpDelivered: false)?.A == 420,
                "Reliable settle sync still returns latest move when requested");
        }

        using (var trailingEvent = new ManualResetEventSlim())
        {
            int fallbackCount = 0, trailingCount = 0;
            using var syncPacer = new FastMovePacer(
                canSendUdp: () => true,
                sendUdp: _ => true,
                queueReliableTcp: _ => Interlocked.Increment(ref fallbackCount),
                queueTrailingSync: _ => { Interlocked.Increment(ref trailingCount); trailingEvent.Set(); });
            syncPacer.TryHandleMove(new Packet("move", 300, 500));
            Check(trailingEvent.Wait(TimeSpan.FromSeconds(2)) &&
                fallbackCount == 0 && trailingCount == 1 &&
                syncPacer.TakeStats().TrailingTcpSync == 1,
                "Normal end-of-stroke trailing settle sync must route separately without inflating tcp_fallback");
        }

        using (var warmEvent = new ManualResetEventSlim())
        {
            int warmCount = 0;
            using var warmPacer = new FastMovePacer(
                canSendUdp: () => true,
                sendUdp: _ => true,
                queueReliableTcp: _ => { },
                sendWarm: () => { if (Interlocked.Increment(ref warmCount) >= 1) warmEvent.Set(); });
            warmPacer.SetWarmActive(true);
            warmPacer.SetWarmIntervalMs(22);
            warmPacer.TryHandleMove(new Packet("move", 320, 480));
            Check(warmEvent.Wait(TimeSpan.FromMilliseconds(90)) && warmCount >= 1,
                "FastMovePacer must emit idle warm keepalive promptly without waiting 130 ms for trailing TCP sync");
            warmEvent.Reset();
            Interlocked.Exchange(ref warmCount, 0);
            warmPacer.SetWarmActive(false);
            Check(warmEvent.Wait(TimeSpan.FromSeconds(2)) && warmCount >= 1,
                "FastMovePacer must keep warm keepalive window open briefly after leaving phone");
        }

        var onlinePeer = NearbyComputerDiscovery.ParseReply(
            "DT1-ONLINE|STUDIO-PC|" + new string('B', 64),
            System.Net.IPAddress.Parse("192.168.1.42"));
        Check(onlinePeer is { CanReceive: true, IsAndroid: false, Name: "STUDIO-PC" },
            "NearbyComputerDiscovery.ParseReply must recognize DT1-ONLINE announcements on private LAN");

        var dualLocal = new MonitorTopology(new[] {
            new MonitorRect(@"\\.\DISPLAY2", 0, 0, 1920, 1080, true),
            new MonitorRect(@"\\.\DISPLAY1", 1920, 182, 1366, 768, false)
        });
        foreach (var diagramSize in new[] {
            new System.Drawing.Size(780, 500),
            new System.Drawing.Size(1000, 620) })
        {
            using var phoneDiagram = new DisplayArrangementControl
            {
                Size = diagramSize,
                Local = dualLocal,
                Remote = portraitTopo,
                RemotePlatform = "Android"
            };
            foreach (var placementEdge in new[] {
                MonitorEdge.Left, MonitorEdge.Right, MonitorEdge.Bottom })
            {
                phoneDiagram.RemoteEdge = placementEdge;
                using var bmp = new System.Drawing.Bitmap(diagramSize.Width, diagramSize.Height);
                phoneDiagram.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, diagramSize.Width, diagramSize.Height));
                Check(phoneDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, placementEdge, 1, 0.5) == 1,
                    $"Portrait phone placed at {placementEdge} of dual Windows monitors must shift at {diagramSize}");
                if (placementEdge is MonitorEdge.Left or MonitorEdge.Right)
                {
                    Check(phoneDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, placementEdge, 1, 0.06) == 1 &&
                        phoneDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, placementEdge, 1, 0.94) == 1,
                        $"Side phone placement ({placementEdge}) must shift smoothly across upper and taskbar edges");
                }
                var returnEdge = placementEdge switch
                {
                    MonitorEdge.Left => MonitorEdge.Right,
                    MonitorEdge.Right => MonitorEdge.Left,
                    MonitorEdge.Bottom => MonitorEdge.Top,
                    _ => MonitorEdge.Bottom
                };
                Check(phoneDiagram.AdjacentTarget(1, returnEdge, 1, 0.5) == EnvironmentRouteOrder.Local,
                    $"Returning from phone placed at {placementEdge} via {returnEdge} must reach Windows");
            }
        }

        // Regression: multi-monitor uneven heights (DISPLAY2 1080p + DISPLAY1 768p)
        // must snap dragged phone flush to DISPLAY1, heal detached layouts, and match EdgeToward.
        var winPeerTopo = new MonitorTopology(new[] {
            new MonitorRect("WIN_PEER", 0, 0, 1920, 1080, true)
        });
        using (var multiDiagram = new DisplayArrangementControl
        {
            Size = new System.Drawing.Size(960, 560),
            Local = dualLocal,
            Remote = winPeerTopo,
            RemotePlatform = "Windows",
            RemoteEdge = MonitorEdge.Left,
            EnvironmentDevices = DisplayArrangementControl.ProjectEnvironment(new[] {
                new EnvironmentDisplayTile(-5, "Xiaomi", "Android", 1, true)
            }, 0)
        })
        {
            using (var bmp = new System.Drawing.Bitmap(960, 560))
                multiDiagram.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 960, 560));

            var localMonitorHitsField = typeof(DisplayArrangementControl).GetField("_localMonitorHits",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var localHits = (System.Drawing.RectangleF[])localMonitorHitsField.GetValue(multiDiagram)!;
            var display1Hit = localHits.OrderByDescending(r => r.Left).First();
            var phoneHitBefore = multiDiagram.OnlineHitRegions.First(x => x.TargetId == -5).Hit;
            int pStartX = (int)(phoneHitBefore.Left + phoneHitBefore.Width / 2f);
            int pStartY = (int)(phoneHitBefore.Top + phoneHitBefore.Height / 2f);
            int pDropX = (int)(display1Hit.Left + display1Hit.Width * 0.5f);
            int pDropY = (int)(display1Hit.Bottom + phoneHitBefore.Height / 2f + 2f);
            down.Invoke(multiDiagram, new object[] { new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, pStartX, pStartY, 0) });
            move.Invoke(multiDiagram, new object[] { new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, pDropX, pDropY, 0) });
            up.Invoke(multiDiagram, new object[] { new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, pDropX, pDropY, 0) });

            using (var bmp = new System.Drawing.Bitmap(960, 560))
                multiDiagram.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 960, 560));
            localHits = (System.Drawing.RectangleF[])localMonitorHitsField.GetValue(multiDiagram)!;
            display1Hit = localHits.OrderByDescending(r => r.Left).First();
            var phoneSnapped = multiDiagram.OnlineHitRegions.First(x => x.TargetId == -5).Hit;
            Check(phoneSnapped.Top >= display1Hit.Bottom - 2f && phoneSnapped.Top <= display1Hit.Bottom + 18f,
                "Portrait phone dragged under elevated DISPLAY1 must snap flush to DISPLAY1 without leaving a void");
            Check(multiDiagram.EdgeToward(EnvironmentRouteOrder.Local, -5, 1) == MonitorEdge.Bottom,
                "EdgeToward must report Bottom when portrait phone is placed below DISPLAY1");
            Check(multiDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, MonitorEdge.Bottom, 1, 0.80) == -5 &&
                multiDiagram.AdjacentTarget(-5, MonitorEdge.Top, 1, 0.5) == EnvironmentRouteOrder.Local,
                "Bottom placement under elevated DISPLAY1 must shift downward and return upward cleanly");

            // Stale/detached synced offset must auto-heal on paint rather than floating unreachable.
            var tile = multiDiagram.EnvironmentDevices.Single();
            multiDiagram.RestoreMemberOffsets(new Dictionary<string, System.Drawing.PointF>
            {
                [tile.CanonicalLayoutKey] = new System.Drawing.PointF(2870f, 1500f)
            });
            using (var bmp = new System.Drawing.Bitmap(960, 560))
                multiDiagram.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 960, 560));
            bool healedReachable = new[] { MonitorEdge.Left, MonitorEdge.Right, MonitorEdge.Top, MonitorEdge.Bottom }
                .Any(e => Enumerable.Range(1, 19).Any(i =>
                    multiDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, e, 1, i / 20.0) == -5));
            Check(healedReachable &&
                multiDiagram.EdgeToward(EnvironmentRouteOrder.Local, -5, 1) is { } healedEdge &&
                Enumerable.Range(1, 19).Any(i =>
                    multiDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, healedEdge, 1, i / 20.0) == -5),
                "Detached saved/synced layout offsets must auto-heal into shifting range");
        }

        var shortLaptop = new MonitorTopology(new[] {
            new MonitorRect(@"\\.\DISPLAY1", 0, 0, 1366, 768, true)
        });
        using (var shortDiagram = new DisplayArrangementControl
        {
            Size = new System.Drawing.Size(780, 500),
            Local = shortLaptop,
            Remote = portraitTopo,
            RemotePlatform = "Android",
            RemoteEdge = MonitorEdge.Right
        })
        {
            using (var bmp = new System.Drawing.Bitmap(780, 500))
                shortDiagram.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 780, 500));
            Check(shortDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, MonitorEdge.Right, 1, 0.03) == 1 &&
                shortDiagram.AdjacentTarget(EnvironmentRouteOrder.Local, MonitorEdge.Right, 1, 0.97) == 1 &&
                shortDiagram.AdjacentTarget(1, MonitorEdge.Left, 1, 0.03) == EnvironmentRouteOrder.Local &&
                shortDiagram.AdjacentTarget(1, MonitorEdge.Left, 1, 0.97) == EnvironmentRouteOrder.Local,
                "Centered portrait phone beside 768p display must not strand upper/lower corner transitions");
        }

        uiCompletion.TrySetResult(true);
    }
    catch (Exception ex) { uiCompletion.TrySetException(ex); }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
await uiCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
uiThread.Join();
Console.WriteLine("PASS: Windows Displays UI renders at minimum and standard sizes without collapsed buttons.");

Check(SourceCapture.NextKeyboardHealthAction(false, 0, 1, false, false) == SourceCapture.KeyboardHookAction.None,
    "Inactive remote control must never refresh or warn about keyboard hooks");
Check(SourceCapture.NextKeyboardHealthAction(true, 1, 5, false, false) == SourceCapture.KeyboardHookAction.None,
    "Keyboard hook receiving events must never trigger preemption refresh");
Check(SourceCapture.NextKeyboardHealthAction(true, 0, 1, false, false) == SourceCapture.KeyboardHookAction.RefreshHook &&
    SourceCapture.NextKeyboardHealthAction(true, 0, 2, true, false) == SourceCapture.KeyboardHookAction.None &&
    SourceCapture.NextKeyboardHealthAction(true, 0, 3, true, false) == SourceCapture.KeyboardHookAction.WarnBlocked &&
    SourceCapture.NextKeyboardHealthAction(true, 0, 4, true, true) == SourceCapture.KeyboardHookAction.None,
    "Keyboard health policy must refresh displaced hook on the first raw key and warn at 3 if still blocked");
Check(!SourceCapture.ShouldIgnoreRemoteWindowsKey(true, 0x5B) &&
    !SourceCapture.ShouldIgnoreRemoteWindowsKey(true, 0x5C) &&
    !SourceCapture.ShouldIgnoreRemoteWindowsKey(true, 0x41) &&
    !SourceCapture.ShouldIgnoreRemoteWindowsKey(false, 0x5B) &&
    !SourceCapture.ShouldIgnoreRemoteWindowsKey(false, 0x5C),
    "Both Android and Windows targets must preserve Windows/Meta keys for external-keyboard shortcuts");
Check(SourceCapture.ModifiersForMask(1 | 8 | 16).SequenceEqual(new[] { 0x11, 0x5B, 0x5C }),
    "Release modifier mask must release Ctrl and both Windows/Meta keys");
Console.WriteLine("PASS: Keyboard hook immediate self-healing and Windows/Meta key passthrough behave consistently.");

// Verify AdaptiveLinkTracker band shift resets stale 280ms baseline & adapts cadence by band.
var linkTracker = new AdaptiveLinkTracker("2.4GHz");
Check(linkTracker.ComputeProbeIntervalMs(true) >= 300,
    "2.4 GHz link must avoid 5 GHz high-frequency probe bursts");
linkTracker.RecordRttSample(280.0, 1000);
linkTracker.RecordRttSample(260.0, 1100);
Check(linkTracker.SmoothedRttMs > 200.0,
    "Slow 2.4 GHz spike establishes initial baseline");
Check(linkTracker.OnLinkProfileChanged("5GHz", 866, 2),
    "Band transition from 2.4 GHz to 5 GHz must signal profile change");
Check(linkTracker.SmoothedRttMs == 0 && linkTracker.ComputeProbeIntervalMs(true) < 200,
    "Roam to 5 GHz must clear stale 280ms baseline immediately and switch to fast cadence");
linkTracker.RecordRttSample(6.5, 1300);
Check(linkTracker.SmoothedRttMs < 15.0,
    "First 5 GHz sample after band shift must reflect live fast latency immediately");
Check(linkTracker.OnLinkProfileChanged("cable", 480, 3),
    "Transition to data cable must update band profile cleanly");
double pace5G = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 120, 6.0, 5.0, 1.5, 0.0, "5GHz");
double pace24G = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 120, 45.0, 18.0, 10.0, 5.0, "2.4GHz");
Check(pace5G <= 7.0 && pace24G >= 11.0 && pace24G <= 24.0,
    "Adaptive UDP pacing must keep 5 GHz crisp while spacing 2.4 GHz frames to prevent bufferbloat");
Console.WriteLine("PASS: Band-adaptive RTT tracking and pacing prevent 2.4 GHz bufferbloat and clear stale baselines.");

// Verify KeyboardLayoutHelper parsing and Windows UI live link status / Shortcuts button.
Check(KeyboardLayoutHelper.DescribeKlid("00000409") == "US QWERTY",
    "KeyboardLayoutHelper must recognize US QWERTY KLID");
var parsedKb = KeyboardLayoutHelper.TryParseWireSummary(
    """{"keyboard":"USB Keyboard","external":true,"fullUsb":true,"pcLayout":"English (India)","mode":"Full USB","bindings":[{"action":"Home","keys":"Meta+H","status":"Supported","ready":true}]}""");
Check(parsedKb != null && parsedKb.Keyboard == "USB Keyboard" && parsedKb.Bindings.Count == 1 && parsedKb.Bindings[0].Ready,
    "KeyboardLayoutHelper must parse valid Android keyboard binding metadata");
Check(KeyboardLayoutHelper.TryParseWireSummary("not-json") == null,
    "KeyboardLayoutHelper must reject invalid metadata cleanly");
var uiCheck2 = new TaskCompletionSource();
var uiThread2 = new Thread(() =>
{
    try
    {
        using var view = new DisplaySettingsForm();
        view.CreateControl();
        var actionRow = view.Controls.Find("ConnectionActions", true)
            .OfType<System.Windows.Forms.FlowLayoutPanel>().Single();
        var liveLink = view.Controls.Find("LiveLinkStatus", true)
            .OfType<System.Windows.Forms.Label>().Single();
        Check(actionRow.Controls.OfType<System.Windows.Forms.Button>().Any(b => b.Text == "Shortcuts") &&
            !string.IsNullOrWhiteSpace(liveLink.Text),
            "Windows UI must expose Shortcuts dialog button and live link readout");
        uiCheck2.SetResult();
    }
    catch (Exception ex) { uiCheck2.TrySetException(ex); }
});
uiThread2.SetApartmentState(ApartmentState.STA);
uiThread2.Start();
await uiCheck2.Task.WaitAsync(TimeSpan.FromSeconds(15));
uiThread2.Join();
Console.WriteLine("PASS: Keyboard binding metadata helper and Windows UI shortcuts/live-link controls verified.");

Check(DeskTouchVision.SingleInstanceActivation.IsDeskTouchMainWindowTitle("DeskTouch Vision — Displays") &&
    DeskTouchVision.SingleInstanceActivation.IsDeskTouchMainWindowTitle("DeskTouch Vision — checking updates") &&
    !DeskTouchVision.SingleInstanceActivation.IsDeskTouchMainWindowTitle("DeskTouch.DeviceShare") &&
    !DeskTouchVision.SingleInstanceActivation.IsDeskTouchMainWindowTitle(""),
    "Window matcher must identify only DeskTouch Vision Displays windows");

var surfacedMatch = new[] {
    new DeskTouchVision.SingleInstanceActivation.WindowMatch((IntPtr)1234, 42, Visible: true, Minimized: false, Responsive: true)
};
Check(DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        surfacedMatch, true, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(10)) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.SurfacedOnCurrentDesktop,
    "Visible responsive window on current desktop must complete wakeup cleanly");

Check(DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        Array.Empty<DeskTouchVision.SingleInstanceActivation.WindowMatch>(), false, TimeSpan.FromMilliseconds(40), null) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.NoExistingInstance,
    "Missing peer process must allow fresh startup without recovery delay");

Check(DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        Array.Empty<DeskTouchVision.SingleInstanceActivation.WindowMatch>(), true, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(12)) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.NeedsRecovery,
    "Established background/other-desktop process with zero windows on caller desktop must trigger fast recovery");

Check(DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        Array.Empty<DeskTouchVision.SingleInstanceActivation.WindowMatch>(), true, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1)) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.Waiting,
    "Brand-new process creating its initial HWND must be given startup grace before recovery");

var hiddenTrayMatch = new[] {
    new DeskTouchVision.SingleInstanceActivation.WindowMatch((IntPtr)1234, 42, Visible: false, Minimized: false, Responsive: true)
};
Check(DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        hiddenTrayMatch, true, TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(5)) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.Waiting &&
    DeskTouchVision.SingleInstanceActivation.EvaluateCurrentDesktopMatches(
        hiddenTrayMatch, true, TimeSpan.FromMilliseconds(1900), TimeSpan.FromMinutes(5)) ==
    DeskTouchVision.SingleInstanceActivation.WakeStepResult.NeedsRecovery,
    "Same-desktop tray window gets wake window to surface and recovers if stuck invisible");
Console.WriteLine("PASS: Single-instance desktop visibility check and stuck/other-desktop recovery logic verified.");

// Verify non-blocking trust-request approval keeps heartbeats responsive while dialog is open,
// and verify cable adaptive pacing + ADB APK version tracking.
{
    double cable144 = ReceiverServer.ComputeAdaptiveCableIntervalMs(144, 2.0);
    double cable144ModerateRtt = ReceiverServer.ComputeAdaptiveCableIntervalMs(144, 14.0);
    double cable60 = ReceiverServer.ComputeAdaptiveCableIntervalMs(60, 2.0);
    double cableBusy = ReceiverServer.ComputeAdaptiveCableIntervalMs(144, 28.0);
    Check(cable144 >= 2.0 && cable144 <= 3.0 && cable144ModerateRtt == cable144 && cable60 <= 6.0 && cableBusy > cable144 && cableBusy <= 6.0,
        "Cable pacing interval must stay well under half a 144Hz frame while tolerating brief RTT jitter");

    int packed = FastMovePacer.PackEdgePush(-24, 37);
    var (unpackedX, unpackedY) = FastMovePacer.UnpackEdgePush(packed);
    Check(unpackedX == -24 && unpackedY == 37 && FastMovePacer.PackEdgePush(0, 0) == 0,
        "Edge overflow push must pack and unpack signed deltas losslessly");
    Packet encodedMove = ReverseInputProtocol.Encode(new Packet("move", 1079, 2459, 0, packed));
    Check(ReverseInputProtocol.TryDecode(encodedMove, out var decodedMove) && decodedMove.D == packed,
        "ReverseInputProtocol must preserve packed edge-overflow delta D");

    var cableTracker = new AdaptiveLinkTracker();
    cableTracker.OnLinkProfileChanged("cable", 480, 0);
    Check(cableTracker.ComputeProbeIntervalMs(true) <= 45 && cableTracker.ComputeProbeIntervalMs(false) <= 140,
        "Cable ping probe cadence must stay tight enough to prevent USB idle wakeup latency spikes");

    int cableDispatched = 0;
    int cableTrailing = 0;
    bool cableLease = true;
    using var cablePacer = new FastMovePacer(
        canSendUdp: () => cableLease,
        sendUdp: _ => { Interlocked.Increment(ref cableDispatched); return true; },
        queueReliableTcp: _ => Interlocked.Increment(ref cableDispatched),
        sendWarm: null,
        queueTrailingSync: _ => Interlocked.Increment(ref cableTrailing));
    cablePacer.SetMinIntervalMs(cable144);
    for (int i = 0; i < 8; i++)
        Check(cablePacer.TryHandleMove(new Packet("move", 100 + i * 3, 200, i + 1, 1)),
            "Cable pacer must accept high-rate moves");
    Thread.Sleep(30);
    Check(Volatile.Read(ref cableDispatched) >= 2 && Volatile.Read(ref cableDispatched) <= 5 &&
        Volatile.Read(ref cableTrailing) == 0 &&
        cablePacer.TakePendingReliableSync(skipIfUdpDelivered: true) == null,
        "Cable pacer must coalesce bursts smoothly without duplicate trailing TCP writes");

    string checkScriptV45 = AdbCableBridge.BuildShellCheckScript(41234);
    string daemonScriptV45 = AdbCableBridge.BuildShellDaemonScript();
    Check(checkScriptV45.Contains("APK_RUN", StringComparison.Ordinal) &&
        daemonScriptV45.Contains("/data/local/tmp/dt_s.apk", StringComparison.Ordinal),
        "Shell daemon launcher and health check must track installed APK path across app updates");

    string nbTrustRoot = Path.Combine(Path.GetTempPath(), "DeskTouch-nb-trust-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(nbTrustRoot);
    try
    {
        var nbId = Path.Combine(nbTrustRoot, "id.dpapi");
        var nbIn = Path.Combine(nbTrustRoot, "in.dpapi");
        var nbGrants = new TrustedDeviceStore(nbIn);
        var dialogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var nbReceiver = new ReceiverServer(
            _ => { },
            new LocalInputPriority(),
            (_, _) => Task.FromResult(true),
            nbGrants,
            (_, _) =>
            {
                dialogEntered.TrySetResult();
                return dialogDecision.Task;
            },
            nbId);
        nbReceiver.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var host = await HostSession.ConnectWithApprovalAsync(
            "127.0.0.1", nbReceiver.Fingerprint, _ => { }, deadline.Token);
        var pongedDuringModal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HeartbeatAcknowledged += () => pongedDuringModal.TrySetResult();

        Task<string?> trustTask = host.RequestTrustAsync(deadline.Token);
        await dialogEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
        Check(host.TrySend(new Packet("ping")), "Must be able to send heartbeat ping during open trust modal");
        await pongedDuringModal.Task.WaitAsync(TimeSpan.FromSeconds(1), deadline.Token);

        dialogDecision.TrySetResult(true);
        string? grantedToken = await trustTask.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
        Check(!string.IsNullOrWhiteSpace(grantedToken) && nbGrants.Incoming.Count == 1,
            "Trust approval completing after heartbeat exchanges must save incoming trusted token");
    }
    finally
    {
        try { Directory.Delete(nbTrustRoot, recursive: true); } catch { }
    }

    // Verify loopback Android ("cable") session delivers the initial reverse-move
    // packet without clearing LatestReverseMove before ReverseSendLoopAsync flushes it.
    string cableTestRoot = Path.Combine(Path.GetTempPath(), "DeskTouch-cable-handoff-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cableTestRoot);
    try
    {
        var cableId = Path.Combine(cableTestRoot, "id.dpapi");
        var cableIn = Path.Combine(cableTestRoot, "in.dpapi");
        using var cableReceiver = new ReceiverServer(
            _ => { },
            new LocalInputPriority(),
            (_, _) => Task.FromResult(true),
            new TrustedDeviceStore(cableIn),
            (_, _) => Task.FromResult(false),
            cableId);
        cableReceiver.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peerConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotReverseGranted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotReverseActive = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cableReceiver.PeerConnected += _ => peerConnected.TrySetResult();
        cableReceiver.ReverseControlMessage += msg =>
        {
            if (msg.Type == "reverse-granted") gotReverseGranted.TrySetResult(msg.A);
            else if (msg.Type == "reverse-active") gotReverseActive.TrySetResult(msg.A);
        };
        using var cableClient = new System.Net.Sockets.TcpClient();
        await cableClient.ConnectAsync("127.0.0.1", Wire.Port, deadline.Token);
        using var cableSsl = new System.Net.Security.SslStream(
            cableClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, cert, _, _) => cert != null);
        await cableSsl.AuthenticateAsClientAsync("desktouch.local");
        await Wire.WriteAsync(cableSsl, new Packet("auth", Text: cableReceiver.Secret), deadline.Token);
        var readyPkt = await Wire.ReadAsync(cableSsl, deadline.Token);
        Check(readyPkt.Type == "ready", "Loopback Android cable client must authenticate cleanly");
        var layoutJson = System.Text.Json.JsonSerializer.Serialize(new[] {
            new MonitorRect("Android screen", 0, 0, 1080, 2400, true, RefreshHz: 144)
        });
        await Wire.WriteAsync(cableSsl, new Packet("hello", Text: System.Text.Json.JsonSerializer.Serialize(
            new PeerHello("Xiaomi-Cable-Test", layoutJson, true, null, "Android", false, "15", "0.3.46", 440, 144, 0))),
            deadline.Token);
        await peerConnected.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);

        Check(cableReceiver.TrySendReverse(new Packet("claim", 15)), "Cable receiver must send reverse claim");
        Packet? sawReverseMove = null;
        bool sentGrant = false;
        while (sawReverseMove == null)
        {
            var pkt = await Wire.ReadAsync(cableSsl, deadline.Token);
            if (pkt.Type == "reverse-claim")
            {
                await Wire.WriteAsync(cableSsl, new Packet("reverse-granted", pkt.A), deadline.Token);
                sentGrant = true;
                int grantedClaim = await gotReverseGranted.Task.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
                Check(grantedClaim == 15, "Hub must observe reverse-granted claim #15");
                Check(cableReceiver.TrySendReverse(new Packet("move", 321, 654)),
                    "Cable pacer must accept initial handoff move");
            }
            else if (pkt.Type == "reverse-move" && sentGrant)
            {
                sawReverseMove = pkt;
            }
        }
        Check(sawReverseMove.A == 321 && sawReverseMove.B == 654,
            "Cable FastMovePacer must flush the initial reverse-move coordinate to Android instead of clearing LatestReverseMove");
        await Wire.WriteAsync(cableSsl, new Packet("reverse-active", 15), deadline.Token);
        int activeClaim = await gotReverseActive.Task.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
        Check(activeClaim == 15, "Cable handoff must receive reverse-active acknowledgement after reverse-move delivery");

        // Verify Windows-initiated inbound trust offer saves deduplicated trust on both ends.
        var trustStore = new TrustedDeviceStore(cableIn);
        var offerTask = cableReceiver.OfferTrustToConnectedAsync(trustStore, deadline.Token);
        Packet offerPkt = await Wire.ReadAsync(cableSsl, deadline.Token);
        Check(offerPkt.Type == "trust-offer" && !string.IsNullOrEmpty(offerPkt.Text) && offerPkt.Text.Contains('\n'),
            "Inbound trust offer must carry receiver fingerprint and 256-bit secret");
        await Wire.WriteAsync(cableSsl, new Packet("trust-accepted", offerPkt.A), deadline.Token);
        bool offerSaved = await offerTask.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
        Check(offerSaved && trustStore.IsTrustedIncoming("Xiaomi-Cable-Test") && trustStore.Incoming.Count == 1,
            "Accepted inbound trust offer must deduplicate and save incoming trust cleanly");
        trustStore.AllowIncoming("Xiaomi-Cable-Test", TrustedDeviceStore.NewSecret());
        Check(trustStore.Incoming.Count == 1,
            "Repeated incoming trust grants for the same device must deduplicate instead of stacking duplicates");
    }
    finally
    {
        try { Directory.Delete(cableTestRoot, recursive: true); } catch { }
    }

    // Verify native Android dimensions are preserved without scaling down to Windows monitor height,
    // unrestricted delta scaling, and edge-push coalescing across FastMovePacer.
    var winPrimary = new MonitorTopology(new[] {
        new MonitorRect(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true)
    });
    var nativePhone = new MonitorTopology(new[] {
        new MonitorRect("Android screen", 0, 0, 1080, 2460, true, RefreshHz: 144)
    });
    var nativeInline = DisplayArrangementControl.ComposeInlineDevices(
        new System.Drawing.Rectangle(0, 0, 1920, 1080),
        new[] { new EnvironmentDisplayTile(1, "Xiaomi", "Android", 1, true, "FP_NATIVE", nativePhone) });
    Check(nativeInline[0].World.Width == 1080 && nativeInline[0].World.Height == 2460,
        "Android arrangement must keep original phone screen dimensions (1080x2460) instead of shrinking to Windows screen height");
    Check(SourceCapture.ScaleRawDeltaPrecise(900, 10, true, 440) >
          SourceCapture.ScaleRawDeltaPrecise(450, 10, true, 440),
        "Android pointer raw deltas must not be capped at 450 counts during full-screen swipes");
    using (var coalescePacer = new FastMovePacer(
        canSendUdp: () => false,
        sendUdp: _ => false,
        queueReliableTcp: _ => { },
        enableTrailingSync: false))
    {
        // Just ensure Push coalescing merges cleanly without dropping prior push delta.
        var merged = FastMovePacer.MergeCoalescedMove(
            new Packet("move", 1079, 2459, 1, FastMovePacer.PackEdgePush(20, 30)),
            new Packet("move", 1079, 2459, 2, FastMovePacer.PackEdgePush(15, 25)));
        var (mpx, mpy) = FastMovePacer.UnpackEdgePush(merged.D);
        Check(mpx == 35 && mpy == 55 && coalescePacer.TakePendingReliableSync() == null,
            "Coalesced moves at boundary must accumulate edge push without trailing sync when disabled");
    }
    Console.WriteLine("PASS: Cable adaptive pacing, inbound trust offer/dedup, native Android dimensions, and smooth UHID tracking verified.");

    // Verify v0.3.50 fixes: cable UI hides ping/jitter, UDP edge-push packs/unpacks,
    // reordered coordinates don't inflate loss, and 2.4 GHz Warm pacing backs off cleanly.
    var (cableClr, cableText) = DisplaySettingsForm.FormatLiveLinkDisplay(
        new ReceiverServer.ActiveLinkSnapshot(true, "Xiaomi", "cable", 0, 0, 0, 0, null), "English (US)");
    Check(cableText.Contains("Data cable", StringComparison.Ordinal) &&
          !cableText.Contains("ping", StringComparison.OrdinalIgnoreCase) &&
          !cableText.Contains("jitter", StringComparison.OrdinalIgnoreCase) &&
          cableClr.G > cableClr.B,
        "Data cable status display must hide ping and jitter completely");
    var (_, wifiText) = DisplaySettingsForm.FormatLiveLinkDisplay(
        new ReceiverServer.ActiveLinkSnapshot(true, "Xiaomi", "5GHz", 433, 11.4, 2.3, 30.0, null), "English (US)");
    Check(wifiText.Contains("11.4 ms ping", StringComparison.Ordinal) &&
          wifiText.Contains("jitter 2.3 ms", StringComparison.Ordinal) &&
          wifiText.Contains("loss 3.0%", StringComparison.Ordinal),
        "Wireless status display must report ping, jitter, and percent loss accurately");

    Check(ReceiverServer.ComputeWarmIntervalMs("2.4GHz", 0, 0) is >= 20 and <= 36 &&
          ReceiverServer.ComputeWarmIntervalMs("2.4GHz", 40, 400) <= 36 &&
          ReceiverServer.ComputeWarmIntervalMs("5GHz", 0, 0) == 24 &&
          ReceiverServer.SupportsUdpEdgePush("0.3.50") &&
          !ReceiverServer.SupportsUdpEdgePush("0.3.49"),
        "2.4 GHz idle keepalive must stay balanced (20-36 ms) without 100 Hz air collisions");

    var cableProbeTracker = new AdaptiveLinkTracker("cable");
    cableProbeTracker.OnLinkProfileChanged("cable", 480, 0);
    Check(cableProbeTracker.ComputeProbeIntervalMs(true) <= 45 &&
          cableProbeTracker.ComputeProbeIntervalMs(false) <= 140,
        "Data cable internal USB keepalive cadence must stay tight without exposing UI ping");

    byte[] reorderKey = Enumerable.Range(10, FastUdpCodec.KeySize).Select(x => (byte)x).ToArray();
    using var sCodec = new FastUdpCodec(reorderKey, peerId: 21, isServer: true);
    using var cCodec = new FastUdpCodec(reorderKey, peerId: 21, isServer: false);
    var udpReorderTracker = new AdaptiveLinkTracker("5GHz");
    byte[] u1 = new byte[FastUdpCodec.FrameSize];
    byte[] u2 = new byte[FastUdpCodec.FrameSize];
    sCodec.Seal(FastUdpOp.ReverseMove, 400, 900, 12, 1, u1);
    int packA = ((22 & 0xFFFF) << 16) | (1079 & 0xFFFF);
    int packB = ((-19 & 0xFFFF) << 16) | (2459 & 0xFFFF);
    sCodec.Seal(FastUdpOp.ReverseEdgePush, packA, packB, 12, 2, u2);
    Check(cCodec.TryOpen(u2, out var pushMsg, udpReorderTracker) &&
          pushMsg.Op == FastUdpOp.ReverseEdgePush &&
          unchecked((short)(pushMsg.A & 0xFFFF)) == 1079 &&
          unchecked((short)(pushMsg.A >> 16)) == 22 &&
          unchecked((short)(pushMsg.B & 0xFFFF)) == 2459 &&
          unchecked((short)(pushMsg.B >> 16)) == -19,
        "ReverseEdgePush UDP frame must preserve both screen coordinates and signed edge push");
    udpReorderTracker.RecordInboundPacket(pushMsg.Seq, false, 0, 2000);
    Check(!cCodec.TryOpen(u1, out _, udpReorderTracker),
        "Out-of-order coordinate packet must not move pointer backward");
    Check(!cCodec.TryOpen(u1, out _, udpReorderTracker),
        "Replay of already-seen reordered packet must still be blocked");
    for (ulong s = 3; s <= 15; s++)
    {
        byte[] nxt = new byte[FastUdpCodec.FrameSize];
        sCodec.Seal(FastUdpOp.ReverseMove, 500, 1000, 12, (int)s, nxt);
        Check(cCodec.TryOpen(nxt, out var nm, udpReorderTracker), "Subsequent ordered frames must open");
        udpReorderTracker.RecordInboundPacket(nm.Seq, false, 0, 2000 + (long)(s * 6));
    }
    Check(udpReorderTracker.LostPackets == 0 && udpReorderTracker.ReorderedPackets == 1,
        "Out-of-order wireless coordinate packet must count as reordered rather than packet loss");

    // Verify v0.3.51: axis-independent coalesced edge push + zero-skip 144Hz cable pacing.
    {
        var slideY = FastMovePacer.MergeCoalescedMove(
            new Packet("move", 1079, 500, 10, FastMovePacer.PackEdgePush(20, 0)),
            new Packet("move", 1079, 502, 11, FastMovePacer.PackEdgePush(15, 0)));
        var (spx, spy) = FastMovePacer.UnpackEdgePush(slideY.D);
        Check(spx == 35 && spy == 0,
            "Coalesced edge push along Y slide must preserve X edge-overflow delta");

        var slideX = FastMovePacer.MergeCoalescedMove(
            new Packet("move", 500, 2459, 12, FastMovePacer.PackEdgePush(0, 18)),
            new Packet("move", 505, 2459, 13, FastMovePacer.PackEdgePush(0, 14)));
        var (spx2, spy2) = FastMovePacer.UnpackEdgePush(slideX.D);
        Check(spx2 == 0 && spy2 == 32,
            "Coalesced edge push along X slide must preserve Y edge-overflow delta");

        double cable144Fast = ReceiverServer.ComputeAdaptiveCableIntervalMs(144, 2.0);
        Check(cable144Fast <= 3.0,
            "Data cable interval at 144Hz must be <=3.0ms so every 6.94ms display VSYNC receives fresh pointer updates");
    }
    // Verify v0.3.52: brief optical-mouse pauses must never falsely trigger touchpad hook fallback & black out RawInput,
    // and wireless AdaptiveLinkTracker recovers cleanly after quiet radio gaps without pinning high loss.
    Check(!SourceCapture.ShouldTriggerMouseHookFallback(1000, 1020, 1) &&
          !SourceCapture.ShouldTriggerMouseHookFallback(1000, 1035, 1) &&
          !SourceCapture.ShouldTriggerMouseHookFallback(1000, 1020, 4) &&
          SourceCapture.ShouldTriggerMouseHookFallback(1000, 1035, 2),
        "MouseHook fallback must require sustained >30ms RawInput absence across multiple hook moves");
    var wifiLossGap = new AdaptiveLinkTracker("2.4GHz");
    wifiLossGap.RecordInboundPacket(1, true, 1000, 1000);
    wifiLossGap.RecordInboundPacket(100, true, 1100, 1100);   // real 98-frame outage
    wifiLossGap.RecordInboundPacket(200, true, 1200, 1200);   // real 99-frame outage
    double preGapLoss = wifiLossGap.InboundLossPermille;
    wifiLossGap.RecordInboundPacket(201, true, 4200, 4200);   // 3 s quiet radio gap
    Check(preGapLoss > 50.0 && wifiLossGap.InboundLossPermille < preGapLoss * 0.6,
        "Wireless loss tracker must decay stale outage spikes after quiet recovery gaps");

    // Verify v0.3.53: single unified environment coordination across 2+ computers & local cable Android.
    {
        var sampleTopology = new MonitorTopology(new[] { new MonitorRect("DISPLAY1", 0, 0, 1920, 1080, true) });
        string fpHub = new string('F', 64);
        string fpLeaf = new string('1', 64);
        var winPeer = new ConnectedPeer("SURAJ", sampleTopology, true, fpHub, "Windows");
        var androidPeer = new ConnectedPeer("Xiaomi", sampleTopology, true, null, "Android");

        Check(DisplaySettingsForm.ShouldBlockAutoConnectBecauseReceiverActive(new[] { winPeer }, new string('2', 64)),
            "Windows hub already serving another PC must not split environment via auto-connect");
        Check(DisplaySettingsForm.ShouldBlockAutoConnectBecauseReceiverActive(new[] { androidPeer }, fpHub) == false,
            "PC holding only local Android peer may still join its distinct trusted Windows hub");
        var androidWithFp = new ConnectedPeer("Phone", sampleTopology, true, fpHub, "Android");
        Check(DisplaySettingsForm.ShouldBlockAutoConnectBecauseReceiverActive(new[] { androidWithFp }, fpHub),
            "Must never open a second reverse connection to an already-connected fingerprint");

        Check(DisplaySettingsForm.ShouldYieldOutboundToBecomeHub(fpHub, fpLeaf) &&
              !DisplaySettingsForm.ShouldYieldOutboundToBecomeHub(fpLeaf, fpHub) &&
              !DisplaySettingsForm.ShouldYieldOutboundToBecomeHub("short", fpLeaf),
            "Mutual simultaneous connect must deterministically resolve to one hub");

        var localOffsets = new Dictionary<string, System.Drawing.PointF>(StringComparer.Ordinal)
        {
            ["hub@Android|Xiaomi"] = new(5200f, 30f),
            ["hub@" + fpLeaf] = new(100f, 20f)
        };
        var remoteOffsets = new Dictionary<string, System.Drawing.PointF>(StringComparer.Ordinal)
        {
            ["hub@" + fpLeaf] = new(300f, 40f)
        };
        var merged = DisplaySettingsForm.MergeRemoteEnvironmentOffsets(
            remoteOffsets, localOffsets, new[] { androidPeer });
        Check(merged.ContainsKey("hub@Android|Xiaomi") && merged["hub@" + fpLeaf].X == 300f,
            "Hub layout sync must preserve locally connected receiver placements while applying hub coordinates");

        string trustStoreTmp = Path.Combine(Path.GetTempPath(), "desktouch-findany-" + Guid.NewGuid().ToString("N") + ".dpapi");
        try
        {
            var store = new TrustedDeviceStore(trustStoreTmp);
            string tok = TrustedDeviceStore.NewSecret();
            store.TrustOutgoing("SURAJ", fpHub, tok);
            store.SetAutoConnect(fpHub, false);
            Check(store.FindAutoConnect(fpHub) == null && store.FindAnyOutgoing(fpHub)?.Token == tok,
                "Manual Add devices must still reuse existing trust even when auto-connect is paused");
            store.ForgetOutgoing(fpHub);
            Check(store.FindAnyOutgoing(fpHub) == null,
                "Forgetting trusted computer must remove outgoing token");
        }
        finally { try { File.Delete(trustStoreTmp); } catch { } }
    }

    // Verify v0.3.54: headless tray display arrangement (no window/Paint), two-way mutual trust,
    // count-based multi-hub merging, and startup shortcut ownership check.
    {
        var mon1920 = new MonitorTopology(new[] { new MonitorRect("DISPLAY1", 0, 0, 1920, 1080, true) });
        var phoneMon = new MonitorTopology(new[] { new MonitorRect("PHONE", 0, 0, 1080, 2400, true) });
        using var hiddenCtrl = new DisplayArrangementControl();
        hiddenCtrl.Local = mon1920;
        hiddenCtrl.Remote = mon1920;
        hiddenCtrl.RemoteEdge = MonitorEdge.Right;
        hiddenCtrl.EnvironmentDevices = DisplayArrangementControl.ProjectEnvironment(
            new[]
            {
                new EnvironmentDisplayTile(10001, "Xiaomi", "Android", 1, true, "ANDROIDFP", phoneMon, "1080x2400")
            }, 1);
        Check(hiddenCtrl.AdjacentTarget(EnvironmentRouteOrder.Local, MonitorEdge.Right, 1, 0.5) == 1,
            "Tray/hidden DisplayArrangementControl must compute AdjacentTarget cleanly before any Paint event");
        Check(hiddenCtrl.AdjacentTarget(1, MonitorEdge.Left, 1, 0.5) == EnvironmentRouteOrder.Local &&
              hiddenCtrl.EdgeToward(EnvironmentRouteOrder.Local, 1, 1) == MonitorEdge.Right,
            "Headless EdgeToward and reverse cursor return edge must work in tray mode");

        string certTmp = Path.Combine(Path.GetTempPath(), "desktouch-certA-" + Guid.NewGuid().ToString("N") + ".pfx");
        string fpA;
        string fpB = new string('B', 64);
        string tok1 = TrustedDeviceStore.NewSecret();
        string tok2 = TrustedDeviceStore.NewSecret();
        string proposalJson;
        try
        {
            using var certA = PersistentReceiverIdentity.LoadOrCreate(certTmp);
            fpA = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certA.RawData)).ToUpperInvariant();
            proposalJson = MutualTrustCodec.SignProposal(certA, fpB, tok1, tok2);
        }
        finally { try { File.Delete(certTmp); } catch { } }
        Check(MutualTrustCodec.TryVerifyProposal(proposalJson, fpB, out var verified) &&
              verified != null && verified.PeerFingerprint == fpA &&
              verified.OutgoingToken == tok1 && verified.ReciprocalToken == tok2,
            "Signed MutualTrustProposal must verify cryptographically against sender certificate and target fingerprint");
        Check(!MutualTrustCodec.TryVerifyProposal(proposalJson, new string('C', 64), out _),
            "MutualTrustProposal bound to one target fingerprint must fail for a different target fingerprint");
        Check(!MutualTrustCodec.TryVerifyProposal(proposalJson.Replace(tok1, TrustedDeviceStore.NewSecret()), fpB, out _),
            "Tampered MutualTrustProposal must fail signature verification");

        string bidirTmp = Path.Combine(Path.GetTempPath(), "desktouch-bidir-" + Guid.NewGuid().ToString("N") + ".dpapi");
        try
        {
            var store = new TrustedDeviceStore(bidirTmp);
            store.TrustOutgoing("SURAJ", fpA, tok1);
            Check(!store.HasBidirectionalTrust("SURAJ", fpA),
                "One-way outgoing trust alone must not satisfy HasBidirectionalTrust");
            store.AllowIncoming("SURAJ", tok2, fpA);
            Check(store.HasBidirectionalTrust("SURAJ", fpA),
                "Matching outgoing + incoming trust entries must satisfy HasBidirectionalTrust");
        }
        finally { try { File.Delete(bidirTmp); } catch { } }

        string fpLow = new string('1', 64);
        string fpHigh = new string('E', 64);
        var candIdle = new NearbyComputer("SURAJ", "192.168.1.20", fpLow, false, true, false, 1);
        var candHub2 = new NearbyComputer("SURAJ", "192.168.1.20", fpLow, false, true, true, 2);
        var candHub3 = new NearbyComputer("SURAJ", "192.168.1.20", fpLow, false, true, true, 3);
        var androidConnected = new ConnectedPeer("Xiaomi", phoneMon, true, "PHONEFP", "Android");

        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                2, false, fpHigh, new[] { androidConnected }, candIdle, allowLegacyIdleSolicitation: false) ==
              DisplaySettingsForm.HubAutoConnectDecision.KeepLocalHubWaitForRemote,
            "2-device local hub must not subordinate to an idle 1-device computer");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                2, false, fpHigh, new[] { androidConnected }, candIdle, allowLegacyIdleSolicitation: true) ==
              DisplaySettingsForm.HubAutoConnectDecision.SolicitRemoteToYieldToLocalHub,
            "Larger local hub may solicit idle 1-way computer after initial wait");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                1, false, fpHigh, Array.Empty<ConnectedPeer>(), candHub2) ==
              DisplaySettingsForm.HubAutoConnectDecision.JoinRemoteHub,
            "Idle 1-device computer must join a 2-device environment hub");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                2, false, fpHigh, new[] { androidConnected }, candHub3) ==
              DisplaySettingsForm.HubAutoConnectDecision.JoinRemoteHub,
            "Smaller 2-device hub must merge into a larger 3-device hub");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                3, false, fpLow, new[] { androidConnected }, candHub2) ==
              DisplaySettingsForm.HubAutoConnectDecision.KeepLocalHubWaitForRemote,
            "Larger 3-device hub must stay hub regardless of fingerprint tie-breaker");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                2, false, fpHigh, new[] { androidConnected }, candHub2) ==
              DisplaySettingsForm.HubAutoConnectDecision.KeepLocalHubWaitForRemote,
            "Equal-size hub with higher fingerprint stays hub");
        Check(DisplaySettingsForm.EvaluateHubAutoConnect(
                2, false, fpLow, new[] { androidConnected },
                candHub2 with { Fingerprint = fpHigh }) ==
              DisplaySettingsForm.HubAutoConnectDecision.JoinRemoteHub,
            "Equal-size hub with lower fingerprint joins higher-fingerprint hub");

        string shareDir = Path.GetDirectoryName(WindowsStartup.ExpectedExecutable)!;
        string launcherExe = Path.GetFullPath(Path.Combine(shareDir, "..", "DeskTouchVision.exe"));
        Check(WindowsStartup.IsOwnedExecutableTarget(WindowsStartup.ExpectedExecutable) &&
              WindowsStartup.IsOwnedExecutableTarget(launcherExe) &&
              !WindowsStartup.IsOwnedExecutableTarget(@"C:\Windows\System32\notepad.exe"),
            "WindowsStartup must recognize both tray executable and launcher while rejecting external programs");

        var stamped = mon1920.WithPrimaryAppVersion(MonitorTopology.CurrentAppVersion());
        Check(stamped.Bounds == mon1920.Bounds && !string.IsNullOrWhiteSpace(stamped.Monitors[0].AppVersion),
            "MonitorTopology.WithPrimaryAppVersion must preserve bounds and stamp app version");

        // Verify v0.3.56 wireless ping/jitter outlier filtering and fast awake recovery.
        var wakeTracker = new AdaptiveLinkTracker("5GHz");
        wakeTracker.RecordRttSample(62.0, 1000); // cold initial beacon wake-up
        wakeTracker.RecordRttSample(4.2, 1100);  // awake wake-primed sample
        wakeTracker.RecordRttSample(4.5, 1200);
        wakeTracker.RecordRttSample(58.0, 1300); // isolated power-save / scan spike
        wakeTracker.RecordRttSample(4.0, 1400);
        Check(wakeTracker.SmoothedRttMs < 10.0 && wakeTracker.EffectiveJitterMs < 5.0,
            "v0.3.56 tracker must recover rapidly to <10ms RTT and <5ms jitter despite isolated wireless sleep spikes");
        double clean24Pace = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, 5.5, 4.0, 2.0, 0.0, "2.4GHz");
        Check(clean24Pace is >= 7.0 and <= 9.5,
            "Clean awake 2.4GHz link must unlock fast <9.5ms cursor pacing instead of locking at 16.5ms");

        // Verify v0.3.57 dual-coordinate packing / unpacking and waypoint recovery.
        int packedDual = FastMovePacer.PackDualCoord(100, 150);
        var (unpackedPrev, unpackedCurr) = FastMovePacer.UnpackDualCoord(packedDual);
        Check(unpackedPrev == 100 && unpackedCurr == 150, "Dual coord packing must preserve both prev (-) and curr (+)");
        var (singlePrev, singleCurr) = FastMovePacer.UnpackDualCoord(FastMovePacer.PackDualCoord(-1, 200));
        Check(singlePrev == 200 && singleCurr == 200, "Unspecified prev must decode as single coordinate");
        Check(ReceiverServer.SupportsDualMovePacket("Android", "0.3.57") && !ReceiverServer.SupportsDualMovePacket("Android", "0.3.56"),
            "Dual-position packets must only target Android peers running 0.3.57 or newer");
        Check(!ReceiverServer.SupportsDualMovePacket("Windows", "0.3.57"),
            "Windows peers must never receive dual-position packets");
        Check(ReceiverServer.SupportsUdpEdgePush("Android", "0.3.61") && !ReceiverServer.SupportsUdpEdgePush("Windows", "0.3.61"),
            "UDP ReverseEdgePush must only target Android peers and never Windows computers");

        // Verify v0.3.58 Ethernet interface detection and link band normalization.
        Check(AdaptiveLinkTracker.NormalizeBand("ethernet") == "ethernet",
            "NormalizeBand must recognize ethernet");
        Check(AdaptiveLinkTracker.NormalizeBand("Ethernet") == "ethernet",
            "NormalizeBand must be case-insensitive for ethernet");
        Check(ReceiverServer.DetectInterfaceForLocalAddress(null).Band == "wireless",
            "Null local address must default to wireless");
        Check(ReceiverServer.DetectInterfaceForLocalAddress(System.Net.IPAddress.Loopback).Band == "wireless",
            "Loopback local address must default to wireless");

        // Verify v0.3.64: Low-RTT clean-jitter RF drops, cellular pacing, DualMode unmapping, and IPv6 endpoints.
        double cleanDrop24 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, 6.0, 4.0, 2.5, 120.0, "2.4GHz");
        Check(cleanDrop24 is >= 7.5 and <= 10.5,
            "Clean-jitter low-RTT 2.4 GHz RF drops must not slam cursor pacing to 16.5ms");
        double bloated24 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, 48.0, 12.0, 15.0, 60.0, "2.4GHz");
        Check(bloated24 >= 12.5,
            "True bloated high-RTT 2.4 GHz link must still back off");
        Check(AdaptiveLinkTracker.NormalizeBand("cellular") == "cellular" &&
              AdaptiveLinkTracker.NormalizeBand("Mobile") == "cellular",
            "NormalizeBand must recognize cellular / mobile transport");
        double cellBase144 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, 0, 0, 0, 0, "cellular");
        Check(cellBase144 is >= 8.0 and <= 10.5,
            "Cellular 144 Hz initial pacing must stay responsive around 9.5ms");
        int cellWarm = ReceiverServer.ComputeWarmIntervalMs("cellular", 2.0, 10.0);
        Check(cellWarm is >= 20 and <= 38,
            "Cellular warm interval must keep radio awake cleanly");
        var mappedLoopback = System.Net.IPAddress.Loopback.MapToIPv6();
        Check(Equals(ReceiverServer.UnmapAddress(mappedLoopback), System.Net.IPAddress.Loopback),
            "DualMode IPv4-mapped loopback must unmap cleanly for USB cable detection");
        var v6List = ReceiverServer.CollectReachableGlobalIpv6Addresses();
        Check(v6List.All(s => System.Net.IPAddress.TryParse(s, out var parsed) &&
                              parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                              !parsed.IsIPv6LinkLocal &&
                              !System.Net.IPAddress.IsLoopback(parsed)),
            "Collected global IPv6 endpoints must only contain reachable unicast IPv6 addresses");
        var probeResp = new Packet("probe-echo", 1);
        Check(probeResp.Type == "probe-echo" && probeResp.A == 1,
            "Multi-sample cellular probe-echo packet contract must preserve sequence index");

        // Verify v0.3.66: Auxiliary mobile UDP peer alongside primary peer, strict global IPv6 filtering, and dual-end UDP confirmation.
        using (var auxHub = new FastUdpHub())
        {
            var primaryPeer = auxHub.AddPeer(1, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 12345), "2.4GHz");
            var auxPeer = auxHub.AddAuxiliaryPeer(1, System.Net.IPAddress.IPv6Loopback, "cellular");
            Check(primaryPeer != null && auxPeer != null &&
                  auxPeer.IsAuxiliary && !primaryPeer.IsAuxiliary &&
                  auxPeer.SessionId == 1 && auxPeer.WirePeerId != primaryPeer.WirePeerId,
                "Auxiliary mobile peer must share SessionId while using an isolated WirePeerId and key");

            var parsedCandidates = ReceiverServer.ParseMobileCandidateEndPoints(
                "[\"2401:4900:62fe:2095:bd0a:7f11:e821:39a\",\"fe80::1\",\"fd00::1\",\"127.0.0.1\"]", 41234);
            Check(parsedCandidates.Length == 1 &&
                  parsedCandidates[0].Address.ToString() == "2401:4900:62fe:2095:bd0a:7f11:e821:39a" &&
                  parsedCandidates[0].Port == 41234,
                "ParseMobileCandidateEndPoints must accept only global unicast candidates and reject link-local/ULA/loopback");

            using var mobClient = new FastUdpClient(
                System.Net.IPAddress.IPv6Loopback, auxHub.Port, auxPeer!.WirePeerId, auxPeer.Key, "cellular", 0);
            auxHub.ConfigurePeerEndPoints(auxPeer, new[] { new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, mobClient.LocalPort) });
            mobClient.SendProbe();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500 && (!mobClient.Confirmed || !auxPeer.Confirmed))
                System.Threading.Thread.Sleep(10);
            Check(mobClient.Confirmed && auxPeer.Confirmed,
                "Two-way loopback UDP probe must confirm auxiliary mobile stream without dropping primary Wi-Fi");
            auxHub.RemovePeer(auxPeer);
            auxHub.RemovePeer(primaryPeer);
        }

        // Verify v0.3.67: FastUdpOp.ReverseKey AEAD contract, non-colliding key seq field, and independent stream-prefer payload.
        Check(AdaptiveLinkTracker.IsInteractiveOp(FastUdpOp.ReverseKey),
            "FastUdpOp.ReverseKey must be treated as an interactive op for packet loss tracking");
        byte[] keyTestKey = Enumerable.Range(1, FastUdpCodec.KeySize).Select(i => (byte)i).ToArray();
        using (var srvKeyCodec = new FastUdpCodec(keyTestKey, 0x0042, true))
        using (var cliKeyCodec = new FastUdpCodec(keyTestKey, 0x0042, false))
        {
            byte[] keyFrame = new byte[FastUdpCodec.FrameSize];
            int sealedLen = srvKeyCodec.Seal(FastUdpOp.ReverseKey, 65, 1, 77, 19, keyFrame);
            Check(sealedLen == FastUdpCodec.FrameSize &&
                  cliKeyCodec.TryOpen(keyFrame, out var openedKey) &&
                  openedKey.Op == FastUdpOp.ReverseKey &&
                  openedKey.A == 65 &&
                  openedKey.B == 1 &&
                  openedKey.Lease == 77 &&
                  openedKey.TimestampMs == 19,
                "FastUdpOp.ReverseKey must seal and open vk, down flag, lease, and actionSeq cleanly");
        }
        var encodedReverseKey = ReverseInputProtocol.Encode(new Packet("key", 19, 0, 1, 65));
        Check(encodedReverseKey.Type == "reverse-key" &&
              encodedReverseKey.A == 19 &&
              encodedReverseKey.C == 1 &&
              encodedReverseKey.D == 65,
            "ReverseInputProtocol must preserve key actionSeq in A alongside C=down and D=vk");
        var splitPref = new Packet("stream-prefer", 1, 0, 0, 0, "cellular");
        Check(splitPref.Type == "stream-prefer" && splitPref.A == 1 && splitPref.B == 0,
            "stream-prefer packet must support independent Mouse (A) and Keyboard (B) path selection");

        // Verify v0.3.68: Wireless debugging mDNS endpoint parser + clean cellular 6.8ms pacing.
        string sampleMdns =
            "List of discovered mdns services\n" +
            "adb-UK9DYX4XF6KZSWGA-a1rxs3\t_adb-tls-connect._tcp\t192.168.1.2:40599\n" +
            "adb-pair-xyz\t_adb-tls-pairing._tcp\t192.168.1.2:37890\n" +
            "adb-UK9DYX4XF6KZSWGA-a1rxs3\t_adb-tls-connect._tcp\t192.168.1.2:40599\n";
        var parsedMdns = AdbCableBridge.ParseMdnsTlsConnectEndpoints(sampleMdns);
        Check(parsedMdns.Count == 1 && parsedMdns[0] == "192.168.1.2:40599",
            "ParseMdnsTlsConnectEndpoints must extract unique _adb-tls-connect._tcp endpoints only");
        double cleanCellLive144 = ReceiverServer.ComputeAdaptiveUdpIntervalMs("Android", 144, 21.0, 19.0, 1.8, 0.0, "cellular");
        Check(cleanCellLive144 is >= 6.4 and <= 7.2,
            "Clean 0%-loss cellular at 144 Hz must unlock crisp ~6.8ms cadence instead of holding at 9.7ms");

        // Verify v0.3.69: hardware ReverseKey forwarding, Numpad mapping, DualPath/KeepWarm stream-prefer, and split-path formatting.
        Check(SourceCapture.ResolveAndroidHardwareKeyVk(0x65, false, false, false, false) == 0x35 &&
              SourceCapture.ResolveAndroidHardwareKeyVk(0x65, true, false, false, false) == 0x65 &&
              SourceCapture.ResolveAndroidHardwareKeyVk(0x41, false, false, false, false) == 0x41,
            "ResolveAndroidHardwareKeyVk must map unshifted Numpad digits to standard digit VKs while preserving shifted/other keys");
        Check(ReceiverServer.SupportsFastReverseKey("Android", "0.3.67") &&
              ReceiverServer.SupportsFastReverseKey("Android", "0.3.69") &&
              !ReceiverServer.SupportsFastReverseKey("Android", "0.3.66") &&
              !ReceiverServer.SupportsFastReverseKey("Windows", "0.3.69") &&
              !ReceiverServer.SupportsFastReverseKey("Android", null),
            "SupportsFastReverseKey must require Android >= 0.3.67");
        var dualPref = new Packet("stream-prefer", 1, 0, 1, 2, "cellular");
        Check(dualPref.C == 1 && dualPref.D == 2,
            "stream-prefer packet must carry C=DualPathControls and D=KeepChannelsWarm");
        var splitSnap = new ReceiverServer.ActiveLinkSnapshot(
            true, "Xiaomi", "cellular", 0, 19.5, 1.4, 0.0, null, "Mouse: Mobile  •  Keys: Wireless + dual keys");
        var (_, splitFormatted) = DisplaySettingsForm.FormatLiveLinkDisplay(splitSnap, "English (US)");
        Check(splitFormatted.Contains("Mouse: Mobile  •  Keys: Wireless + dual keys"),
            "FormatLiveLinkDisplay must show SplitPathsSummary when active");
        var singleSnap = new ReceiverServer.ActiveLinkSnapshot(
            true, "Xiaomi", "5GHz", 866, 4.1, 0.6, 0.0, null);
        var (_, singleFormatted) = DisplaySettingsForm.FormatLiveLinkDisplay(singleSnap, "English (US)");
        Check(!singleFormatted.Contains("Mouse:") && singleFormatted.Contains("5 GHz wireless"),
            "FormatLiveLinkDisplay must stay concise on single-path connections");

        // Verify v0.3.71: multi-system Windows key relay must preserve hardware scanCode in A across 300+ keystrokes.
        var spaceKey = new Packet("key", 57, 0, 1, 0x20);
        var winSpace = ReceiverServer.PrepareReverseControlPacket("Windows", 320, spaceKey);
        Check(winSpace.A == 57 && winSpace.B == 0 && winSpace.C == 1 && winSpace.D == 0x20 && winSpace.Text == "320",
            "Windows multi-system key relay must preserve hardware Spacebar scanCode=57 and pass actionSeq in Text");
        var androidSpace = ReceiverServer.PrepareReverseControlPacket("Android", 320, spaceKey);
        Check(androidSpace.A == 320 && androidSpace.D == 0x20,
            "Android key relay must retain actionSeq in A and VK in D");
        Check(ReceiverServer.SupportsWindowsUdpReverseKey("Windows", "0.3.71") &&
              !ReceiverServer.SupportsWindowsUdpReverseKey("Windows", "0.3.70"),
            "SupportsWindowsUdpReverseKey must enable packed scan+vk UDP keys starting at v0.3.71");
    }

    // Verify v0.3.72: ForwardButton/ForwardWheel/ForwardKey AEAD framing, NotificationCount propagation,
    // EnvironmentDisplayTile NotificationCount, and DeskSpaceDeckBridge JSON/command dispatching.
    Check(AdaptiveLinkTracker.IsInteractiveOp(FastUdpOp.ForwardButton) &&
          AdaptiveLinkTracker.IsInteractiveOp(FastUdpOp.ForwardWheel) &&
          AdaptiveLinkTracker.IsInteractiveOp(FastUdpOp.ForwardKey),
          "ForwardButton/ForwardWheel/ForwardKey must be treated as interactive UDP ops");
    byte[] fwdKey = Enumerable.Range(21, FastUdpCodec.KeySize).Select(i => (byte)i).ToArray();
    using (var fwdCli = new FastUdpCodec(fwdKey, 0x0022, isServer: false))
    using (var fwdSrv = new FastUdpCodec(fwdKey, 0x0022, isServer: true))
    {
        byte[] fBtn = new byte[FastUdpCodec.FrameSize];
        byte[] fWhl = new byte[FastUdpCodec.FrameSize];
        byte[] fKey = new byte[FastUdpCodec.FrameSize];
        fwdCli.Seal(FastUdpOp.ForwardButton, 1, 1, 77, 10, fBtn);
        fwdCli.Seal(FastUdpOp.ForwardWheel, -120, 0, 77, 11, fWhl);
        fwdCli.Seal(FastUdpOp.ForwardKey, 32, 1, 77, 12, fKey);
        Check(fwdSrv.TryOpen(fBtn, out var mBtn) && mBtn.Op == FastUdpOp.ForwardButton && mBtn.A == 1 && mBtn.B == 1 && mBtn.Lease == 77 && mBtn.TimestampMs == 10 &&
              fwdSrv.TryOpen(fWhl, out var mWhl) && mWhl.Op == FastUdpOp.ForwardWheel && mWhl.A == -120 && mWhl.TimestampMs == 11 &&
              fwdSrv.TryOpen(fKey, out var mKey) && mKey.Op == FastUdpOp.ForwardKey && mKey.A == 32 && mKey.B == 1 && mKey.TimestampMs == 12,
              "FastUdpCodec must authenticate phone-to-system forward button/wheel/key frames cleanly");
    }

    var notifHelloJson = System.Text.Json.JsonSerializer.Serialize(new PeerHello(
        "Xiaomi 22041216I", testDisplay.ToWire(), ReverseInputSupported: true,
        Platform: "Android", ForwardInputSupported: true, NotificationCount: 7));
    var parsedNotifHello = ReceiverServer.ReadHello(notifHelloJson);
    Check(parsedNotifHello.NotificationCount == 7,
          "PeerHello must preserve numeric NotificationCount across JSON serialization");

    var memberDto = new EnvironmentMember(2, "SURAJ", "Windows", testDisplay.ToWire(), true, fpOne, 4);
    var memberRoundtrip = System.Text.Json.JsonSerializer.Deserialize<EnvironmentMember>(
        System.Text.Json.JsonSerializer.Serialize(memberDto));
    Check(memberRoundtrip != null && memberRoundtrip.NotificationCount == 4,
          "EnvironmentMember must preserve numeric NotificationCount cleanly");

    var badgeTile = new EnvironmentDisplayTile(1, "Xiaomi", "Android", 1, true, fpMobile, testDisplay, null, 9);
    Check(badgeTile.NotificationCount == 9 &&
          new EnvironmentDisplayTile(2, "SURAJ", "Windows", 1, true).NotificationCount == -1,
          "EnvironmentDisplayTile must store count-only notification badges with -1 unknown default");

    int bridgeSwitched = 0;
    bool bridgeReturned = false;
    int bridgePayPeer = -999;
    bool bridgeShown = false;
    using (var bridge = new DeskSpaceDeckBridge(
        id => bridgeSwitched = id,
        () => bridgeReturned = true,
        p => bridgePayPeer = p,
        () => bridgeShown = true))
    {
        var snap = new BridgeDeviceSnapshotDto(
            Version: 1,
            UpdatedUtcMs: 123456789,
            LocalName: "DESKTOP-IQ1KSJ9",
            LocalNotificationCount: 3,
            ActiveTargetId: 5,
            IsRemoteActive: true,
            ActiveDeviceName: "Xiaomi",
            ConnectedCount: 1,
            TotalNotificationCount: 10,
            HasAnyKnownNotificationCount: true,
            Devices: new[]
            {
                new BridgeDeviceItemDto(EnvironmentRouteOrder.Local, "DESKTOP-IQ1KSJ9", "Windows", true, false, false, true, 3, "Click to return"),
                new BridgeDeviceItemDto(5, "Xiaomi", "Android", false, true, true, true, 7, "Controlling")
            });
        string snapJson = DeskSpaceDeckBridge.SerializeSnapshot(snap);
        var back = System.Text.Json.JsonSerializer.Deserialize<BridgeDeviceSnapshotDto>(snapJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check(back != null && back.ConnectedCount == 1 && back.TotalNotificationCount == 10 &&
              back.Devices.Count == 2 && back.Devices[1].NotificationCount == 7,
              "DeskSpaceDeckBridge JSON snapshot must serialize and roundtrip cleanly");

        bridge.DispatchCommand("GET");
        bridge.DispatchCommand("SWITCH:-2");
        bridge.DispatchCommand("RETURN");
        bridge.DispatchCommand("TOGGLE_PAY:5");
        bridge.DispatchCommand("SHOW");
        Check(bridgeSwitched == -2 && bridgeReturned && bridgePayPeer == 5 && bridgeShown,
              "DeskSpaceDeckBridge must dispatch SWITCH, RETURN, TOGGLE_PAY, and SHOW commands accurately");

        var rawItems = new[]
        {
            new BridgeDeviceItemDto(EnvironmentRouteOrder.Local, "DESKTOP-IQ1KSJ9", "Windows", true, false, false, true, 1, "Click to return"),
            new BridgeDeviceItemDto(0, "SURAJ", "Windows", false, false, false, true, 3, "Connected"),
            new BridgeDeviceItemDto(3, "Xiaomi", "Android", false, true, true, true, -1, "Controlling")
        };
        var arrangedRects = new Dictionary<int, System.Drawing.RectangleF>
        {
            [0] = new System.Drawing.RectangleF(10, 40, 120, 68),                    // SURAJ on Left
            [EnvironmentRouteOrder.Local] = new System.Drawing.RectangleF(138, 40, 150, 68), // Hub in Center
            [3] = new System.Drawing.RectangleF(296, 28, 54, 92)                    // Xiaomi on Right
        };
        var normalized = DeskSpaceDeckBridge.NormalizeAndOrderDevices(rawItems, arrangedRects);
        Check(normalized.Count == 3 &&
              normalized[0].Id == 0 &&
              normalized[1].Id == EnvironmentRouteOrder.Local &&
              normalized[2].Id == 3 &&
              normalized[0].ArrangementOrder == 0 &&
              normalized[1].ArrangementOrder == 1 &&
              normalized[2].ArrangementOrder == 2 &&
              normalized[0].LayoutX < normalized[1].LayoutX &&
              normalized[1].LayoutX < normalized[2].LayoutX &&
              normalized[2].LayoutH > normalized[0].LayoutH,
              "DeskSpaceDeckBridge must sort and normalize spatial bounds left-to-right matching DisplayArrangementControl");

        var stackedRects = new Dictionary<int, System.Drawing.RectangleF>
        {
            [0] = new System.Drawing.RectangleF(140, 120, 120, 68),
            [EnvironmentRouteOrder.Local] = new System.Drawing.RectangleF(140, 40, 120, 68),
            [3] = new System.Drawing.RectangleF(268, 30, 54, 92)
        };
        var stacked = DeskSpaceDeckBridge.NormalizeAndOrderDevices(rawItems, stackedRects);
        Check(stacked[0].Id == EnvironmentRouteOrder.Local &&
              stacked[1].Id == 3 &&
              stacked[2].Id == 0,
              "DeskSpaceDeckBridge must order stacked rows top-to-bottom and left-to-right within rows");
    }

    // Custom editable pairing code validation, salted DT1-PAIR discovery, and spatial EnvironmentMember fields.
    Check(PairingCodeHelper.NormalizeCustomCode(" suraj-99 ") == "SURAJ-99" &&
          PairingCodeHelper.NormalizeCustomCode("ab") == "" &&
          PairingCodeHelper.NormalizeCustomCode("bad|code") == "",
          "PairingCodeHelper must normalize 4-32 alphanumeric codes and reject pipes or short codes");
    string pairTag = PairingCodeHelper.ComputeDiscoveryTag(fpOne, "suraj-99");
    Check(pairTag.Length == 24 && pairTag.All(Uri.IsHexDigit),
          "ComputeDiscoveryTag must produce a salted 24-hex discovery tag without exposing raw code");

    using (var pairDisc = new NearbyComputerDiscovery(fpOne, () => true, () => Array.Empty<string>(), () => true, () => 2, "suraj-99"))
    {
        var msgs = pairDisc.BuildPresenceMessages();
        Check(msgs[0].StartsWith("DT1-READY|", StringComparison.Ordinal) &&
              msgs[1] == "DT1-HUB|" + fpOne &&
              msgs.Any(m => m == $"DT1-PAIR|{fpOne}|{pairTag}"),
              "NearbyComputerDiscovery must preserve primary discovery messages and append salted DT1-PAIR tag");
    }

    var discoveredCustom = new[]
    {
        new NearbyComputer("SURAJ", "192.168.1.22", fpOne, false, true, false, 1, pairTag),
        new NearbyComputer("OTHER", "192.168.1.23", fpTwo, false, true, false, 1, null)
    };
    var customFound = PairComputerDialog.FindPeerByCode(discoveredCustom, "suraj-99", out bool matchedCustomTag);
    Check(customFound != null && matchedCustomTag && customFound.Fingerprint == fpOne,
          "PairComputerDialog.FindPeerByCode must locate Windows PC matching custom pairing tag");

    using (var pairReceiver = new ReceiverServer(_ => { }, new LocalInputPriority(), (_, _) => Task.FromResult(false)))
    {
        pairReceiver.SetCustomPairingCode("TEAM2026");
        Check(pairReceiver.TryVerifyCustomPairingCode("team2026"),
              "ReceiverServer must accept matching custom pairing code case-insensitively");
        for (int fail = 0; fail < 6; fail++)
            Check(!pairReceiver.TryVerifyCustomPairingCode("WRONG1"), "Wrong custom pairing code must be rejected");
        Check(!pairReceiver.TryVerifyCustomPairingCode("TEAM2026"),
              "ReceiverServer must temporarily rate-limit custom pairing code attempts after repeated failures");
    }

    var orderedMemberDto = new EnvironmentMember(2, "SURAJ", "Windows", testDisplay.ToWire(), true, fpOne, 4, 0, 0.0, 0.1, 0.3, 0.4, 87, true);
    var orderedRoundtrip = System.Text.Json.JsonSerializer.Deserialize<EnvironmentMember>(
        System.Text.Json.JsonSerializer.Serialize(orderedMemberDto));
    Check(orderedRoundtrip != null && orderedRoundtrip.ArrangementOrder == 0 && Math.Abs(orderedRoundtrip.LayoutW - 0.3) < 0.001
          && orderedRoundtrip.BatteryPercent == 87 && orderedRoundtrip.IsCharging,
          "EnvironmentMember must preserve optional spatial arrangement order, bounds, and battery telemetry");

    // v0.3.82 Ecosystem features: shared clipboard hash/packet, OTP extraction, file drop bounds & chunks, media/power allowlists, and rules.
    string clipJson = SharedClipboardHelper.SerializeClipboardTextPacket("ABCDEF0123456789", "Hello DeskTouch");
    Check(clipJson.Contains("Hello DeskTouch", StringComparison.Ordinal) &&
          SharedClipboardHelper.ComputeHash("text", System.Text.Encoding.UTF8.GetBytes("Hello DeskTouch")).Length == 16,
          "SharedClipboardHelper must serialize clipboard packets and compute 16-hex hashes");

    Check(DeviceTelemetryAndNotifModel.ExtractVerificationCode("Sign in", "Your OTP code is 492817 for login") == "492817" &&
          DeviceTelemetryAndNotifModel.ExtractVerificationCode("Balance update", "You spent Rs 450 today") == null,
          "ExtractVerificationCode must detect keyword-gated OTPs and ignore ordinary numbers");

    string sanitized = FileDropHelper.SanitizeFileName("..\\secret/report:2026?.pdf");
    Check(!sanitized.Contains("..", StringComparison.Ordinal) && !sanitized.Contains('/', StringComparison.Ordinal) &&
          sanitized.EndsWith(".pdf", StringComparison.Ordinal),
          "FileDropHelper.SanitizeFileName must strip traversal and invalid filename characters");

    var chunkAsm = new FileDropHelper.BufferedChunkAssembler();
    byte[] rawSample = System.Text.Encoding.UTF8.GetBytes("DeskTouchChunkPayload");
    string b64Part1 = Convert.ToBase64String(rawSample, 0, 9);
    string b64Part2 = Convert.ToBase64String(rawSample, 9, rawSample.Length - 9);
    var c1 = chunkAsm.AddChunk(55, 0, 2, b64Part1, "doc");
    var c2 = chunkAsm.AddChunk(55, 1, 2, b64Part2, "doc");
    Check(!c1.Complete && c2.Complete &&
          System.Text.Encoding.UTF8.GetString(c2.Data) == "DeskTouchChunkPayload",
          "BufferedChunkAssembler must reassemble multi-part payloads in order");

    Check(SystemMediaPowerHelper.IsValidMediaAction("play-pause") &&
          SystemMediaPowerHelper.IsValidMediaAction("switch-window") &&
          !SystemMediaPowerHelper.IsValidMediaAction("rm-rf") &&
          SystemMediaPowerHelper.IsValidPowerAction("lock") &&
          SystemMediaPowerHelper.IsValidPowerAction("wake") &&
          !SystemMediaPowerHelper.IsValidPowerAction("format-disk"),
          "SystemMediaPowerHelper must strictly validate media and power actions");

    var rulesCfg = AutomationAndProfilesStore.CreateDefault();
    var matchedVlc = AutomationAndProfilesStore.MatchRule(rulesCfg, "vlc.exe", "Movie.mkv");
    var matchedTally = AutomationAndProfilesStore.MatchRule(rulesCfg, "tallyprime.exe", "Tally");
    Check(matchedVlc?.ShortcutPreset == "Media" && matchedTally?.ShortcutPreset == "Accounting" &&
          AutomationAndProfilesStore.MatchRule(rulesCfg, "notepad.exe", "notes.txt") == null,
          "AutomationAndProfilesStore.MatchRule must map app process/title patterns to presets");

    var bridgeSnap = new BridgeDeviceSnapshotDto(
        1, 123456L, "DESKTOP", 2, EnvironmentRouteOrder.Local, false, "DESKTOP", 2, 2, true,
        new[] { new BridgeDeviceItemDto(EnvironmentRouteOrder.Local, "DESKTOP", "Windows", true, false, false, true, 2, "Hub", 0, 0, 0.4, 0.3, 0, 92, true, "AC") },
        ActiveProfile: "Work", ClipboardSyncEnabled: true, ActiveForegroundApp: "vlc.exe", ActiveShortcutPreset: "Media");
    string bridgeSnapJson = DeskSpaceDeckBridge.SerializeSnapshot(bridgeSnap);
    Check(bridgeSnapJson.Contains("\"activeProfile\":\"Work\"", StringComparison.Ordinal) &&
          bridgeSnapJson.Contains("\"batteryPercent\":92", StringComparison.Ordinal) &&
          bridgeSnapJson.Contains("\"activeForegroundApp\":\"vlc.exe\"", StringComparison.Ordinal),
          "DeskSpaceDeckBridge snapshot and ecosystem metadata must serialize cleanly");

    Console.WriteLine("PASS: Cable click urgency, low-RTT drop pacing, cellular/IPv6 dual-stack, and all DeviceSharing policy invariants verified.");
}

internal sealed class RecordingInputSession : IInputSession
{
    internal List<Packet> Packets { get; } = new();
    public MonitorTopology RemoteMonitors { get; }
    internal RecordingInputSession(MonitorTopology geometry) => RemoteMonitors=geometry;
    public bool TrySend(Packet packet){ Packets.Add(packet);return true; }
}
