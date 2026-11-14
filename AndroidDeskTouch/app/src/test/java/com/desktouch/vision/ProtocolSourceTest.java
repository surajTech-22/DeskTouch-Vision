package com.desktouch.vision;
import org.junit.Test;
import static org.junit.Assert.*;
public final class ProtocolSourceTest {
    @Test public void packetConstructorsPreserveAuthorityFields() {
        Packet p = new Packet("claim",123);
        assertEquals("claim",p.type);
        assertEquals(123,p.a);
        assertEquals(0,p.b);
    }
    @Test public void packetRejectsUnknownTypesOnClientSide() {
        assertFalse(DeskTouchSession.allowedInputType("reverse-claim"));
        assertTrue(DeskTouchSession.allowedInputType("move"));
        assertTrue(DeskTouchSession.allowedInputType("key"));
        assertTrue(DeskTouchSession.allowedInputType("text")); // Android IME
        assertFalse(DeskTouchSession.allowedInputType("goodbye"));
    }
    @Test public void multipleDevicesAndBusyPeersAreRecognized() {
        String a="A".repeat(64);
        String b="B".repeat(64);
        String c="C".repeat(64);
        DesktopDiscovery.Device pc1=DesktopDiscovery.parse("DT1-READY|PC1|"+a,"192.168.1.11");
        DesktopDiscovery.Device pc2=DesktopDiscovery.parse("DT1-BUSY|PC2|"+b,"192.168.1.12");
        DesktopDiscovery.Device android=DesktopDiscovery.parse("DT1-MOBILE|Phone|"+c,"192.168.1.2");
        assertNotNull(pc1);
        assertNotNull(pc2);
        assertNotNull(android);
        assertTrue(pc1.ready);
        assertFalse(pc2.ready);
        assertTrue(android.android);
        assertFalse(android.ready);
        assertNull(DesktopDiscovery.parse("DT1-READY|Fake|BAD","192.168.1.9"));
    }
    @Test public void releaseUpdaterComparesSemanticVersions() {
        assertTrue(AndroidUpdater.compareVersions("0.3.6","0.3.5")>0);
        assertEquals(0,AndroidUpdater.compareVersions("0.3.6","0.3.6"));
        assertTrue(AndroidUpdater.compareVersions("0.4.0","0.3.99")>0);
        assertTrue(AndroidUpdater.compareVersions("0.3.5","0.3.6")<0);
    }
    @Test public void ownAndroidBeaconIsNotAnotherDevice(){
        DesktopDiscovery.Device self=DesktopDiscovery.parse(
            "DT1-MOBILE|My Phone|"+"F".repeat(64),"192.168.1.2");
        DesktopDiscovery.Device other=DesktopDiscovery.parse(
            "DT1-MOBILE|Other Phone|"+"E".repeat(64),"192.168.1.3");
        assertFalse(DesktopDiscovery.isRemoteDevice(self,"F".repeat(64)));
        assertTrue(DesktopDiscovery.isRemoteDevice(other,"F".repeat(64)));
    }
    @Test public void connectedWindowsPcGroupRetainsSeparateFingerprints(){
        DesktopDiscovery.Device pc1=DesktopDiscovery.parse(
            "DT1-READY|PC 1|"+"A".repeat(64),"192.168.1.11");
        DesktopDiscovery.Device pc2=DesktopDiscovery.parse(
            "DT1-READY|PC 2|"+"B".repeat(64),"192.168.1.12");
        DesktopDiscovery.Device pc3=DesktopDiscovery.parse(
            "DT1-READY|PC 3|"+"C".repeat(64),"192.168.1.13");
        assertNotNull(pc1);assertNotNull(pc2);assertNotNull(pc3);
        DesktopDiscovery.applyConnectedGroups(
            java.util.Arrays.asList(pc1,pc2,pc3),
            java.util.Collections.singletonList(
                new String[]{"A".repeat(64),"B".repeat(64)}));
        assertEquals(pc1.groupId,pc2.groupId);
        assertNotEquals(pc1.groupId,pc3.groupId);
        assertNotEquals(pc1.fingerprint,pc2.fingerprint);
    }
    @Test public void windowsToAndroidAuthorityIsExplicitlyWhitelisted() {
        assertTrue(DeskTouchSession.allowedWindowsControlType("reverse-claim"));
        assertTrue(DeskTouchSession.allowedWindowsControlType("reverse-move"));
        assertTrue(DeskTouchSession.allowedWindowsControlType("reverse-key"));
        assertTrue(DeskTouchSession.allowedWindowsControlType("reverse-text"));
        assertTrue(DeskTouchSession.allowedWindowsControlType("reverse-release"));
        assertFalse(DeskTouchSession.allowedWindowsControlType("auth-trusted"));
        assertFalse(DeskTouchSession.allowedWindowsControlType("reverse-auth-request"));
        assertFalse(DeskTouchSession.allowedWindowsControlType("claim"));
    }
    @Test public void windowsKeyboardTypesIntoAndroidTextFields() {
        WindowsKeyText.Edit a=WindowsKeyText.apply("",0,0,65,false);
        assertEquals("a",a.text);
        WindowsKeyText.Edit capital=WindowsKeyText.apply(a.text,1,1,66,true);
        assertEquals("aB",capital.text);
        assertEquals(2,capital.cursor);
        WindowsKeyText.Edit erase=WindowsKeyText.apply(capital.text,2,2,8,false);
        assertEquals("a",erase.text);
        WindowsKeyText.Edit space=WindowsKeyText.apply("hello",5,5,32,false);
        assertEquals("hello ",space.text);
        assertEquals("!",WindowsKeyText.printable(49,true));
        assertEquals("2",WindowsKeyText.printable(50,false));
        assertEquals("\\",WindowsKeyText.printable(220,false));
        assertNull(WindowsKeyText.apply("x",1,1,112,false)); // F1 is not text
    }
    @Test public void linkedPcsUseOneCardWithDistinctApprovedSessions() {
        DesktopDiscovery.Device one=DesktopDiscovery.parse(
            "DT1-READY|Desktop A|"+"A".repeat(64),"192.168.1.21");
        DesktopDiscovery.Device two=DesktopDiscovery.parse(
            "DT1-READY|Desktop B|"+"B".repeat(64),"192.168.1.22");
        DesktopDiscovery.Device separate=DesktopDiscovery.parse(
            "DT1-READY|Unlinked|"+"C".repeat(64),"192.168.1.23");
        DesktopDiscovery.applyConnectedGroups(
            java.util.Arrays.asList(one,two,separate),
            java.util.Collections.singletonList(new String[]{"A".repeat(64),
                "B".repeat(64)}));
        java.util.LinkedHashMap<String,java.util.List<DesktopDiscovery.Device>> groups =
            DesktopDiscovery.windowsGroups(java.util.Arrays.asList(one,two,separate));
        assertEquals(2,groups.size());
        java.util.List<DesktopDiscovery.Device> linked=groups.get(one.groupId);
        assertEquals(2,linked.size());
        DesktopDiscovery.Device existing=DesktopDiscovery.chooseGateway(linked,
            java.util.Collections.singleton(two.fingerprint),
            java.util.Collections.singleton(one.fingerprint));
        assertEquals(two.fingerprint,existing.fingerprint); // no duplicate session
        DesktopDiscovery.Device remembered=DesktopDiscovery.chooseGateway(linked,
            java.util.Collections.emptySet(),
            java.util.Collections.singleton(one.fingerprint));
        assertEquals(one.fingerprint,remembered.fingerprint);
        assertNotEquals(one.fingerprint,two.fingerprint); // separate TLS keys
    }
    @Test public void environmentDiscoveryPrefersActualHubNotJoinedMember() {
        DesktopDiscovery.Device hub=DesktopDiscovery.parse(
            "DT1-READY|DESKTOP-HUB|"+"A".repeat(64),"192.168.1.10");
        DesktopDiscovery.Device member=DesktopDiscovery.parse(
            "DT1-READY|DESKTOP-MEMBER|"+"B".repeat(64),"192.168.1.20");
        hub.isHub=true;
        java.util.List<DesktopDiscovery.Device> linked=java.util.Arrays.asList(hub,member);
        DesktopDiscovery.applyConnectedGroups(linked,
            java.util.Collections.singletonList(
                new String[]{hub.fingerprint,member.fingerprint}));
        assertEquals(hub.groupId,member.groupId);
        DesktopDiscovery.Device selected=DesktopDiscovery.chooseGateway(
            linked,java.util.Collections.emptySet(),
            java.util.Collections.singleton(member.fingerprint));
        assertEquals(hub.fingerprint,selected.fingerprint);
        assertNotEquals(member.fingerprint,selected.fingerprint);
    }
    @Test public void environmentMemberHasRoutedDisplayWithoutNewConnection() {
        DeskTouchSession.EnvironmentTarget member=
            new DeskTouchSession.EnvironmentTarget(
                3,"DESKTOP-SECOND","Windows","[]",true);
        assertEquals(3,member.id);
        assertTrue(member.supportsInput);
        assertEquals("Windows • DESKTOP-SECOND",member.toString());
    }
    @Test public void remoteAndroidTextNeverTargetsPasswordEditors(){
        assertTrue(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_TEXT));
        assertTrue(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_TEXT |
                android.text.InputType.TYPE_TEXT_VARIATION_WEB_EDIT_TEXT));
        assertFalse(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_TEXT |
                android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD));
        assertFalse(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_TEXT |
                android.text.InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD));
        assertFalse(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_TEXT |
                android.text.InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD));
        assertFalse(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_CLASS_NUMBER |
                android.text.InputType.TYPE_NUMBER_VARIATION_PASSWORD));
        assertFalse(DeskTouchAccessibilityService.safeEditorType(
            android.text.InputType.TYPE_NULL));
        assertTrue(DeskTouchAccessibilityService.allowsDirectInputConnection(
            false,android.text.InputType.TYPE_NULL));
        assertFalse(DeskTouchAccessibilityService.allowsDirectInputConnection(
            true,android.text.InputType.TYPE_NULL));
        assertFalse(DeskTouchAccessibilityService.allowsDirectInputConnection(
            false,android.text.InputType.TYPE_CLASS_TEXT |
                android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD));
        assertFalse(DeskTouchAccessibilityService.allowsDirectInputConnection(
            false,android.text.InputType.TYPE_CLASS_NUMBER |
                android.text.InputType.TYPE_NUMBER_VARIATION_PASSWORD));
    }
    @Test public void gesturesAndShortcutsScaleSafely(){
        assertEquals(25L,DeskTouchAccessibilityService.gestureDurationMs(0,0));
        assertEquals(25L,DeskTouchAccessibilityService.gestureDurationMs(4,3));
        assertTrue(DeskTouchAccessibilityService.gestureDurationMs(300,0)>=90L);
        assertEquals(420L,DeskTouchAccessibilityService.gestureDurationMs(2000,0));
        assertTrue(DeskTouchAccessibilityService.isSupportedShortcutKey(0x41,true,false,false));
        assertTrue(DeskTouchAccessibilityService.isSupportedShortcutKey(0x43,true,false,false));
        assertTrue(DeskTouchAccessibilityService.isSupportedShortcutKey(0x56,true,false,false));
        assertTrue(DeskTouchAccessibilityService.isSupportedShortcutKey(0x58,true,false,false));
        assertFalse(DeskTouchAccessibilityService.isSupportedShortcutKey(0x41,false,false,false));
        assertFalse(DeskTouchAccessibilityService.isSupportedShortcutKey(0x41,true,true,false));
    }
    @Test public void appApprovalSurvivesUpdateWithoutGrantingOsAccessibility(){
        assertTrue(PermissionContinuity.needsUserReapproval(true,false));
        assertFalse(PermissionContinuity.needsUserReapproval(true,true));
        assertFalse(PermissionContinuity.needsUserReapproval(false,false));
        assertFalse(PermissionContinuity.needsUserReapproval(false,true));
    }
    @Test public void unavailableGatewayDoesNotBecomeTrusted() {
        DesktopDiscovery.Device busy=DesktopDiscovery.parse(
            "DT1-BUSY|Busy|"+"D".repeat(64),"192.168.1.4");
        java.util.List<DesktopDiscovery.Device> members=
            java.util.Collections.singletonList(busy);
        assertFalse(DesktopDiscovery.chooseGateway(members,
            java.util.Collections.emptySet(),
            java.util.Collections.emptySet()).ready);
    }
    @Test public void fastUdpCodecAuthenticatesAndRejectsReplay() throws Exception {
        byte[] key=new byte[FastUdpChannel.KEY_SIZE];
        for(int i=0;i<key.length;i++) key[i]=(byte)(i+1);
        FastUdpChannel.Codec server=new FastUdpChannel.Codec(key,7,true);
        FastUdpChannel.Codec client=new FastUdpChannel.Codec(key,7,false);

        byte[] frame1=new byte[FastUdpChannel.FRAME_SIZE];
        assertEquals(FastUdpChannel.FRAME_SIZE,
            server.seal(FastUdpChannel.OP_REVERSE_MOVE,540,1200,42,100,frame1));
        FastUdpChannel.Message decoded1=client.tryOpen(frame1,frame1.length);
        assertNotNull(decoded1);
        assertEquals(FastUdpChannel.OP_REVERSE_MOVE,decoded1.op);
        assertEquals(540,decoded1.a);
        assertEquals(1200,decoded1.b);
        assertEquals(42,decoded1.lease);

        // Replay must be rejected cleanly.
        assertNull(client.tryOpen(frame1,frame1.length));

        byte[] frame2=new byte[FastUdpChannel.FRAME_SIZE];
        server.seal(FastUdpChannel.OP_REVERSE_MOVE,600,1300,42,104,frame2);
        byte[] tamperedCipher=frame2.clone();
        tamperedCipher[20]^=0x55;
        assertNull(client.tryOpen(tamperedCipher,tamperedCipher.length));

        byte[] tamperedHeader=frame2.clone();
        tamperedHeader[8]^=0x01; // AAD mismatch even if seq looks higher
        assertNull(client.tryOpen(tamperedHeader,tamperedHeader.length));

        FastUdpChannel.Message decoded2=client.tryOpen(frame2,frame2.length);
        assertNotNull(decoded2);
        assertEquals(600,decoded2.a);
        assertEquals(1300,decoded2.b);

        // Wrong direction / self-reflection cannot decrypt on sender side.
        assertNull(server.tryOpen(frame2,frame2.length));
        byte[] c2s=new byte[FastUdpChannel.FRAME_SIZE];
        client.seal(FastUdpChannel.OP_MOVE,320,240,99,200,c2s);
        FastUdpChannel.Message up=server.tryOpen(c2s,c2s.length);
        assertNotNull(up);
        assertEquals(FastUdpChannel.OP_MOVE,up.op);
        assertEquals(320,up.a);
        assertEquals(240,up.b);
        assertEquals(99,up.lease);

        // Stale out-of-order lower sequence must be rejected after higher accepted.
        byte[] olderSeq=new byte[FastUdpChannel.FRAME_SIZE];
        byte[] newerSeq=new byte[FastUdpChannel.FRAME_SIZE];
        server.seal(FastUdpChannel.OP_REVERSE_MOVE,100,100,42,110,olderSeq);
        server.seal(FastUdpChannel.OP_REVERSE_MOVE,200,200,42,114,newerSeq);
        FastUdpChannel.Message newer=client.tryOpen(newerSeq,newerSeq.length);
        assertNotNull(newer);
        assertEquals(200,newer.a);
        assertNull(client.tryOpen(olderSeq,olderSeq.length));

        byte[] warmFrame=new byte[FastUdpChannel.FRAME_SIZE];
        assertEquals(FastUdpChannel.FRAME_SIZE,
            server.seal(FastUdpChannel.OP_WARM,0,0,0,120,warmFrame));
        FastUdpChannel.Message warmMsg=client.tryOpen(warmFrame,warmFrame.length);
        assertNotNull(warmMsg);
        assertEquals(FastUdpChannel.OP_WARM,warmMsg.op);
    }
    @Test public void phoneGeometryAndOnlineBeaconHelpersWork(){
        assertEquals(0,PhoneScreenGeometry.rotationDegrees(0));
        assertEquals(90,PhoneScreenGeometry.rotationDegrees(1));
        assertEquals(180,PhoneScreenGeometry.rotationDegrees(2));
        assertEquals(270,PhoneScreenGeometry.rotationDegrees(3));
        assertEquals("Portrait",PhoneScreenGeometry.orientationName(1080,2400));
        assertEquals("Landscape",PhoneScreenGeometry.orientationName(2400,1080));
        assertEquals("Xiaomi 23090RA98I",
            PhoneScreenGeometry.cleanDeviceName("Xiaomi","Xiaomi 23090RA98I"));
        assertEquals("Google Pixel 8",
            PhoneScreenGeometry.cleanDeviceName("Google","Pixel 8"));
        PhoneScreenGeometry.Info info=new PhoneScreenGeometry.Info(
            1080,2400,440,120,0,"Xiaomi 14","Android 15","0.3.28");
        assertEquals("Portrait",info.orientation);
        assertEquals(440,info.densityDpi);
        assertEquals(120,info.refreshHz);

        DesktopDiscovery.Device online=DesktopDiscovery.parseOnline(
            "DT1-ONLINE|WORKSTATION|"+"E".repeat(64),"192.168.1.55");
        assertNotNull(online);
        assertEquals("WORKSTATION",online.name);
        assertTrue(online.ready);
        assertFalse(online.android);
        assertNull(DesktopDiscovery.parseOnline("DT1-READY|BAD|"+"E".repeat(64),"192.168.1.55"));
        assertNull(DesktopDiscovery.parseOnline("DT1-ONLINE|BAD|short","192.168.1.55"));
    }
    @Test public void fastUdpExtendedControlOpsAndDedupPreventStaleAndDuplicateActions() throws Exception {
        byte[] key=new byte[FastUdpChannel.KEY_SIZE];
        for(int i=0;i<key.length;i++) key[i]=(byte)(i+11);
        FastUdpChannel.Codec server=new FastUdpChannel.Codec(key,9,true);
        FastUdpChannel.Codec client=new FastUdpChannel.Codec(key,9,false);

        byte[] claimFrame=new byte[FastUdpChannel.FRAME_SIZE];
        server.seal(FastUdpChannel.OP_REVERSE_CLAIM,42,0,42,7,claimFrame);
        FastUdpChannel.Message claimMsg=client.tryOpen(claimFrame,claimFrame.length);
        assertNotNull(claimMsg);
        assertEquals(FastUdpChannel.OP_REVERSE_CLAIM,claimMsg.op);
        assertEquals(42,claimMsg.a);
        assertEquals(7,claimMsg.timestampMs);

        byte[] buttonFrame=new byte[FastUdpChannel.FRAME_SIZE];
        server.seal(FastUdpChannel.OP_REVERSE_BUTTON,1,1,42,101,buttonFrame);
        FastUdpChannel.Message buttonMsg=client.tryOpen(buttonFrame,buttonFrame.length);
        assertNotNull(buttonMsg);
        assertEquals(FastUdpChannel.OP_REVERSE_BUTTON,buttonMsg.op);
        assertEquals(101,buttonMsg.timestampMs);

        byte[] wheelFrame=new byte[FastUdpChannel.FRAME_SIZE];
        server.seal(FastUdpChannel.OP_REVERSE_WHEEL,-120,0,42,102,wheelFrame);
        FastUdpChannel.Message wheelMsg=client.tryOpen(wheelFrame,wheelFrame.length);
        assertNotNull(wheelMsg);
        assertEquals(FastUdpChannel.OP_REVERSE_WHEEL,wheelMsg.op);
        assertEquals(-120,wheelMsg.a);
        assertEquals(102,wheelMsg.timestampMs);

        byte[] grantedFrame=new byte[FastUdpChannel.FRAME_SIZE];
        client.seal(FastUdpChannel.OP_REVERSE_GRANTED,42,1,42,0,grantedFrame);
        FastUdpChannel.Message grantedMsg=server.tryOpen(grantedFrame,grantedFrame.length);
        assertNotNull(grantedMsg);
        assertEquals(FastUdpChannel.OP_REVERSE_GRANTED,grantedMsg.op);
        assertEquals(1,grantedMsg.b);

        byte[] activeFrame=new byte[FastUdpChannel.FRAME_SIZE];
        client.seal(FastUdpChannel.OP_REVERSE_ACTIVE,42,0,42,0,activeFrame);
        FastUdpChannel.Message activeMsg=server.tryOpen(activeFrame,activeFrame.length);
        assertNotNull(activeMsg);
        assertEquals(FastUdpChannel.OP_REVERSE_ACTIVE,activeMsg.op);

        DeskTouchSession.ReverseControlDeduplicator dedup=
            new DeskTouchSession.ReverseControlDeduplicator();
        assertTrue(dedup.tryAcceptClaimDispatch(42,7,0));
        assertFalse(dedup.tryAcceptClaimDispatch(42,7,0)); // duplicate after release must not re-acquire
        assertTrue(dedup.tryAcceptControlAction(1,true));   // UDP button down
        assertFalse(dedup.tryAcceptControlAction(1,true));  // delayed TCP duplicate ignored
        assertTrue(dedup.tryAcceptControlAction(2,true));   // UDP button up
        assertFalse(dedup.tryAcceptControlAction(1,true));  // stale TCP down after newer up ignored
        assertTrue(dedup.tryAcceptControlAction(4,true));   // newer button event
        assertTrue(dedup.tryAcceptControlAction(3,false));  // slightly out-of-order missing wheel still applies
        assertFalse(dedup.tryAcceptControlAction(3,false)); // wheel duplicate ignored
        assertTrue(dedup.tryAcceptMove(4000));

        // After disconnect/reconnect on a reused DeskTouchSession, reset() must
        // allow a fresh host Session's claim #1 and move #1 without silent drops.
        dedup.reset();
        assertTrue(dedup.tryAcceptClaimDispatch(1,1,0));
        assertTrue(dedup.tryAcceptMove(1));
    }

    @Test public void scrollWheelDirectionStaysInsideSafeScreenMargins(){
        DeskTouchAccessibilityService.WheelStroke up=
            DeskTouchAccessibilityService.computeWheelStroke(540,2100,120,false,1080,2400);
        assertTrue(up.ey<up.sy);
        assertEquals(45L,up.durationMs);
        assertTrue(up.ey>=Math.round(2400*0.14f) && up.sy<=Math.round(2400*0.80f));

        DeskTouchAccessibilityService.WheelStroke down=
            DeskTouchAccessibilityService.computeWheelStroke(540,120,-120,false,1080,2400);
        assertTrue(down.ey>down.sy);
        assertTrue(down.sy>=Math.round(2400*0.14f) && down.ey<=Math.round(2400*0.80f));

        DeskTouchAccessibilityService.WheelStroke right=
            DeskTouchAccessibilityService.computeWheelStroke(1060,1200,120,true,1080,2400);
        assertTrue(right.ex<right.sx);
        assertTrue(right.ex>=Math.round(1080*0.10f) && right.sx<=Math.round(1080*0.90f));

        // Scrolling directly on top of a tall app icon must escape well past the icon
        // bounds before lifting so Android does not fire performClick() and open the app.
        DeskTouchAccessibilityService.WheelStroke iconScrollUp=
            DeskTouchAccessibilityService.computeWheelStroke(
                220,1440,120,false,1080,2400,100,1200,340,1460);
        assertTrue(iconScrollUp.ey<=1200-84);

        DeskTouchAccessibilityService.WheelStroke iconScrollDown=
            DeskTouchAccessibilityService.computeWheelStroke(
                220,1220,-120,false,1080,2400,100,1200,340,1460);
        assertTrue(iconScrollDown.ey>=1460+84);

        // Even near the top margin where upward clearance is tight, the stroke must not
        // start and end inside the same app icon box.
        DeskTouchAccessibilityService.WheelStroke topIconScrollUp=
            DeskTouchAccessibilityService.computeWheelStroke(
                220,420,120,false,1080,2400,100,260,340,520);
        assertTrue(topIconScrollUp.sy>520 && topIconScrollUp.ey<topIconScrollUp.sy);
    }
    @Test public void interactiveKeepaliveIntervalStaysWithinWirelessWakeThreshold(){
        assertTrue(DeskTouchSession.INTERACTIVE_KEEPALIVE_MS>=18 &&
            DeskTouchSession.INTERACTIVE_KEEPALIVE_MS<=30);
        // Active phone control must keep upstream TX keepalive running so the phone
        // radio never enters power-save sleep while receiving downstream moves.
        assertTrue(DeskTouchSession.shouldSendInteractiveKeepalive(
            true,true,true,true,true,true));
        assertTrue(DeskTouchSession.shouldSendInteractiveKeepalive(
            true,true,true,true,false,true));
        assertTrue(DeskTouchSession.shouldSendInteractiveKeepalive(
            true,true,true,true,true,false));
        assertFalse(DeskTouchSession.shouldSendInteractiveKeepalive(
            true,true,true,true,false,false));
        assertFalse(DeskTouchSession.shouldSendInteractiveKeepalive(
            true,true,false,true,true,true));
    }
    @Test public void moveSequenceDedupAndPackedCoordsPreventStaleTcpAndTearing(){
        DeskTouchSession.ReverseControlDeduplicator dedup=
            new DeskTouchSession.ReverseControlDeduplicator();
        assertTrue(dedup.tryAcceptMove(100));
        assertTrue(dedup.tryAcceptMove(105));
        assertFalse(dedup.tryAcceptMove(104)); // late TCP packet after newer UDP move
        assertFalse(dedup.tryAcceptMove(105)); // duplicate sequence
        assertTrue(dedup.tryAcceptMove(106));  // newer settle update
        assertTrue(dedup.tryAcceptMove(0));    // unsequenced legacy hub fallback

        long packed=DeskTouchAccessibilityService.packCoords(1079,2399);
        assertEquals(1079,DeskTouchAccessibilityService.unpackX(packed));
        assertEquals(2399,DeskTouchAccessibilityService.unpackY(packed));

        long edge=DeskTouchAccessibilityService.packCoords(-12,0);
        assertEquals(-12,DeskTouchAccessibilityService.unpackX(edge));
        assertEquals(0,DeskTouchAccessibilityService.unpackY(edge));
    }

    @Test public void pointerSmootherTracksLiveRefreshRateAndAdaptsZeroDelayCleanVsJittery() {
        PointerSmoother smoother = new PointerSmoother();
        float[] out = new float[2];

        // 1. Clean low-jitter or typical quiet wireless link: zero smoothing lag.
        smoother.setDisplayRefreshHz(120);
        smoother.setLinkMetrics(4.0, 0.0);
        smoother.reset(100f, 200f, 1_000_000L);
        assertEquals(0.0, smoother.getCurrentTauMs(), 0.001);
        assertTrue(smoother.onTarget(260f, 360f, 9_300_000L));
        assertTrue(smoother.stepFrame(10_000_000L, out));
        assertEquals(260f, out[0], 0.01f);
        assertEquals(360f, out[1], 0.01f);
        assertTrue(smoother.isSettled());

        // 2. Live frame-rate estimator adapts generically across 50Hz / 90Hz / 120Hz / 144Hz
        // and ignores non-vsync UI posts / long pauses between cursor movements.
        long tNs = 20_000_000L;
        for (int i = 0; i < 10; i++) {
            tNs += 20_000_000L; // 50 Hz frames
            smoother.observeFrameTimestamp(tNs);
        }
        assertEquals(50, smoother.getMeasuredFrameHz());
        assertEquals(50, smoother.getEffectiveRefreshHz());

        for (int i = 0; i < 12; i++) {
            tNs += 6_944_444L; // 144 Hz frames
            smoother.observeFrameTimestamp(tNs);
        }
        assertEquals(144, smoother.getMeasuredFrameHz());
        assertEquals(144, smoother.getEffectiveRefreshHz());

        // Intermittent 30ms mouse pause + UI wakeup must NOT collapse 144 Hz cadence.
        tNs += 30_000_000L;
        smoother.observeFrameTimestamp(tNs, false);
        tNs += 6_944_444L;
        smoother.observeFrameTimestamp(tNs, true);
        assertEquals(144, smoother.getMeasuredFrameHz());

        // 3. Congested wireless link: interpolates monotonically without overshooting,
        // caps smoothing lag tightly even across 60Hz dips, and snaps before clicks/scrolls.
        smoother.setLinkMetrics(18.0, 25.0);
        double tau144 = smoother.getCurrentTauMs();
        assertTrue(tau144 > 5.0 && tau144 <= 8.0);
        smoother.setDisplayRefreshHz(60);
        double tau60 = smoother.getCurrentTauMs();
        assertTrue(tau60 > 5.0 && tau60 <= 10.0);
        smoother.setDisplayRefreshHz(144);
        smoother.reset(100f, 100f, tNs);
        assertTrue(smoother.onTarget(500f, 300f, tNs + 7_000_000L));
        assertTrue(smoother.stepFrame(tNs + 14_000_000L, out));
        assertTrue(out[0] > 100f && out[0] < 500f);
        assertTrue(out[1] > 100f && out[1] < 300f);
        assertFalse(smoother.isSettled());
        assertTrue(smoother.snapNow(out));
        assertEquals(500f, out[0], 0.01f);
        assertEquals(300f, out[1], 0.01f);
        assertTrue(smoother.isSettled());
    }

    @Test public void adaptiveLinkTrackerCapsPacingAndRecoversLossQuickly() {
        AdaptiveLinkTracker tracker = new AdaptiveLinkTracker();
        tracker.recordInboundPacket(1L, true, 1000, 1000);
        for (long s = 6L; s <= 15L; s++) {
            tracker.recordInboundPacket(s, true, (int) (1000L + s * 7L), 1000L + s * 7L);
        }
        double spikeLoss = tracker.getInboundLossPermille();
        for (long s = 16L; s <= 65L; s++) {
            tracker.recordInboundPacket(s, true, (int) (1000L + s * 7L), 1000L + s * 7L);
        }
        double recovered = tracker.getInboundLossPermille();
        assertTrue(spikeLoss > 40.0 && recovered < spikeLoss * 0.25 && recovered < 15.0);

        tracker.recordRttSample(48.0, 2000L);
        tracker.onFeedbackReceived(0, AdaptiveLinkTracker.packLossJitter(30.0, 22.0), -1, 2000L);
        double p144 = tracker.computeAdaptiveSendIntervalMs(144);
        double p90 = tracker.computeAdaptiveSendIntervalMs(90);
        double p60 = tracker.computeAdaptiveSendIntervalMs(60);
        assertTrue(p144 <= 14.0 && p90 <= 16.0 && p60 <= 18.5);
    }

    @Test public void loopbackCableDiscoveryParsesReadyHubOverSocket() throws Exception {
        String fp = "ABCDEF0123456789".repeat(4);
        try (java.net.ServerSocket server = new java.net.ServerSocket(0, 1,
                java.net.InetAddress.getLoopbackAddress())) {
            Thread responder = new Thread(() -> {
                try (java.net.Socket s = server.accept()) {
                    String payload = "DT1-READY|DESKTOP-TEST|" + fp + "\nDT1-HUB|" + fp + "\n";
                    s.getOutputStream().write(payload.getBytes(java.nio.charset.StandardCharsets.US_ASCII));
                    s.getOutputStream().flush();
                } catch (Exception ignored) {}
            });
            responder.setDaemon(true);
            responder.start();

            DesktopDiscovery.Device cable = DesktopDiscovery.probeLoopbackCable(
                    java.net.InetAddress.getLoopbackAddress(), server.getLocalPort());
            assertNotNull(cable);
            assertTrue(cable.cable);
            assertTrue(cable.ready);
            assertTrue(cable.isHub);
            assertEquals("127.0.0.1", cable.address);
            assertEquals("DESKTOP-TEST", cable.name);
            assertTrue(cable.toString().contains("data cable"));
        }
    }

    @Test public void adaptiveLinkTrackerMeasuresLossRttBloatAndReordering() {
        AdaptiveLinkTracker tracker = new AdaptiveLinkTracker();
        int packedLj = AdaptiveLinkTracker.packLossJitter(12.0, 7.4);
        assertEquals(12.0, AdaptiveLinkTracker.unpackLossPermille(packedLj), 0.1);
        assertEquals(7.4, AdaptiveLinkTracker.unpackJitterMs(packedLj), 0.11);

        int packedDr = AdaptiveLinkTracker.packDisplayRate(144, 69);
        assertEquals(144, AdaptiveLinkTracker.unpackRefreshHz(packedDr));
        assertEquals(69, AdaptiveLinkTracker.unpackFrameTenthsMs(packedDr));

        // Harmless wireless reordering (seq 7 before seq 6) must NOT count as real packet loss.
        long[] seqOrder = { 1, 2, 3, 4, 5, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        long ms = 1000L;
        for (long s : seqOrder) {
            ms += 8L;
            tracker.recordInboundPacket(s, true, ms);
        }
        assertTrue(tracker.getReorderedPackets() >= 1);
        assertEquals(0L, tracker.getLostPackets());

        // Skipping seq 18 completely past REORDER_SLACK must detect genuine loss.
        for (long s = 17; s <= 35; s++) {
            if (s == 18) continue;
            ms += 8L;
            tracker.recordInboundPacket(s, true, ms);
        }
        assertEquals(1L, tracker.getLostPackets());

        // Clean vs congested RTT pacing scales up cleanly under wireless bloat.
        AdaptiveLinkTracker cleanTracker = new AdaptiveLinkTracker();
        int t1 = cleanTracker.nextPingToken(ms);
        assertTrue(cleanTracker.onFeedbackReceived(t1, AdaptiveLinkTracker.packLossJitter(0.0, 1.0),
            AdaptiveLinkTracker.packDisplayRate(120, 83), ms + 3L));
        double cleanPace = cleanTracker.computeAdaptiveSendIntervalMs(120);
        assertTrue(cleanPace <= 6.5);

        int t2 = cleanTracker.nextPingToken(ms + 200L);
        assertTrue(cleanTracker.onFeedbackReceived(t2, AdaptiveLinkTracker.packLossJitter(35.0, 20.0),
            AdaptiveLinkTracker.packDisplayRate(60, 167), ms + 255L));
        double busyPace = cleanTracker.computeAdaptiveSendIntervalMs(60);
        assertTrue(busyPace > cleanPace + 5.0 && busyPace <= 32.0);

        // Verify RFC-3550 transit jitter ignores mouse pauses when network latency is steady,
        // yet immediately detects real wireless queuing + burst catch-up.
        AdaptiveLinkTracker transitTracker = new AdaptiveLinkTracker();
        long[] pauseStepsMs = { 7L, 7L, 140L, 7L, 95L, 7L, 120L, 7L };
        long rxMs = 2000L;
        int txMs = 9000;
        for (int i = 0; i < pauseStepsMs.length; i++) {
            rxMs += pauseStepsMs[i];
            txMs += (int) pauseStepsMs[i];
            transitTracker.recordInboundPacket(i + 1L, true, txMs, rxMs);
        }
        assertTrue(transitTracker.getInboundJitterMs() < 1.5);
        rxMs += 42L;
        txMs += 7; // +35ms network stall
        transitTracker.recordInboundPacket(9L, true, txMs, rxMs);
        rxMs += 2L;
        txMs += 28; // queued backlog catch-up
        transitTracker.recordInboundPacket(10L, true, txMs, rxMs);
        assertTrue(transitTracker.getInboundJitterMs() > 8.0);
    }

    @Test public void liveRefreshRateResolutionAndUdpSlidingReplayWindow() throws Exception {
        assertEquals(120, DeskTouchSession.resolveLiveRefreshHz(120, 60));
        assertEquals(50, DeskTouchSession.resolveLiveRefreshHz(50, 120));
        assertEquals(90, DeskTouchSession.resolveLiveRefreshHz(0, 90));
        assertEquals(60, DeskTouchSession.resolveLiveRefreshHz(0, 0));

        byte[] key = new byte[FastUdpChannel.KEY_SIZE];
        for (int i = 0; i < key.length; i++) key[i] = (byte) (i + 1);
        FastUdpChannel.Codec srv = new FastUdpChannel.Codec(key, 5, true);
        FastUdpChannel.Codec cli = new FastUdpChannel.Codec(key, 5, false);
        byte[] f1 = new byte[FastUdpChannel.FRAME_SIZE];
        byte[] f2 = new byte[FastUdpChannel.FRAME_SIZE];
        byte[] f3 = new byte[FastUdpChannel.FRAME_SIZE];
        byte[] f4 = new byte[FastUdpChannel.FRAME_SIZE];
        srv.seal(FastUdpChannel.OP_LINK_PING, 11, 25, 0, 100, f1);
        srv.seal(FastUdpChannel.OP_REVERSE_BUTTON, 1, 1, 9, 10, f2);
        srv.seal(FastUdpChannel.OP_REVERSE_WHEEL, -120, 0, 9, 11, f3);
        srv.seal(FastUdpChannel.OP_LINK_FEEDBACK, 11, 0, 0, (120 << 16) | 83, f4);

        // Out-of-order delivery within 64-packet window succeeds without duplicate replay.
        FastUdpChannel.Message m1 = cli.tryOpen(f1, f1.length);
        FastUdpChannel.Message m3 = cli.tryOpen(f3, f3.length);
        FastUdpChannel.Message m2 = cli.tryOpen(f2, f2.length);
        FastUdpChannel.Message m4 = cli.tryOpen(f4, f4.length);
        assertNotNull(m1);
        assertNotNull(m2);
        assertNotNull(m3);
        assertNotNull(m4);
        assertEquals(FastUdpChannel.OP_LINK_PING, m1.op);
        assertEquals(FastUdpChannel.OP_REVERSE_BUTTON, m2.op);
        assertEquals(FastUdpChannel.OP_REVERSE_WHEEL, m3.op);
        assertEquals(FastUdpChannel.OP_LINK_FEEDBACK, m4.op);
        assertNull(cli.tryOpen(f2, f2.length)); // replayed seq blocked
    }
}
