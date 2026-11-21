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
        AdaptiveLinkTracker tracker = new AdaptiveLinkTracker("2.4GHz");
        long burstMs = 1000L;
        for (long s = 1L; s <= 30L; s++) {
            burstMs += 16L;
            tracker.recordInboundPacket(s, true, burstMs);
        }
        for (long s = 40L; s >= 31L; s--) {
            burstMs += 1L;
            tracker.recordInboundPacket(s, true, burstMs);
        }
        assertEquals(0L, tracker.getLostPackets());
        assertTrue(tracker.getReorderedPackets() >= 9L && tracker.getInboundLossPermille() < 1.0);

        AdaptiveLinkTracker lateTracker = new AdaptiveLinkTracker("2.4GHz");
        long lateMs = 1000L;
        for (long s = 1L; s <= 69L; s++) {
            lateMs += 8L;
            lateTracker.recordInboundPacket(s, true, lateMs);
        }
        for (long s = 71L; s <= 133L; s++) {
            lateMs += 8L;
            lateTracker.recordInboundPacket(s, true, lateMs);
        }
        assertEquals(1L, lateTracker.getLostPackets());
        assertEquals(0L, lateTracker.getLatePackets());
        lateMs += 8L;
        lateTracker.recordInboundPacket(70L, true, lateMs);
        assertEquals(0L, lateTracker.getLostPackets());
        assertEquals(1L, lateTracker.getLatePackets());
        double spikeLoss = lateTracker.getInboundLossPermille();
        for (long s = 134L; s <= 200L; s++) {
            lateMs += 8L;
            lateTracker.recordInboundPacket(s, true, lateMs);
        }
        double recovered = lateTracker.getInboundLossPermille();
        assertTrue(spikeLoss > 0.0 && recovered < spikeLoss * 0.25 && spikeLoss < 100.0);

        // Idle keepalive/probe drops during mouse pause must not fabricate mouse packet loss.
        AdaptiveLinkTracker idleTracker = new AdaptiveLinkTracker("2.4GHz");
        long idleMs = 5000L;
        for (long s = 1L; s <= 10L; s++) {
            idleMs += 8L;
            idleTracker.recordInboundPacket(s, false, 0, idleMs, true);
        }
        for (long s = 11L; s <= 120L; s++) {
            idleMs += 24L;
            if (s % 5L == 0L) continue;
            idleTracker.recordInboundPacket(s, false, 0, idleMs, false);
        }
        assertEquals(0L, idleTracker.getLostPackets());
        assertTrue(idleTracker.getInboundLossPermille() < 0.01);

        AdaptiveLinkTracker pacingTracker = new AdaptiveLinkTracker();
        pacingTracker.recordRttSample(48.0, 2000L);
        pacingTracker.onFeedbackReceived(0, AdaptiveLinkTracker.packLossJitter(30.0, 22.0), -1, 2000L);
        double p144 = pacingTracker.computeAdaptiveSendIntervalMs(144);
        double p90 = pacingTracker.computeAdaptiveSendIntervalMs(90);
        double p60 = pacingTracker.computeAdaptiveSendIntervalMs(60);
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

        // Skipping seq 18 completely past REORDER_SLACK (63) must detect genuine loss.
        for (long s = 17; s <= 100; s++) {
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

    @Test public void paymentAppDetectorMatchesPaymentAndBankingPackagesSafely() {
        String[] paymentApps = {
            "com.google.android.apps.nbu.paisa.user",
            "com.phonepe.app",
            "net.one97.paytm",
            "in.org.npci.upiapp",
            "com.dreamplug.androidapp",
            "com.mobikwik_new",
            "com.sbi.lotusintouch",
            "com.snapwork.hdfc",
            "com.csam.icici.bank.imobile",
            "com.axis.mobile",
            "com.msf.kbank.mobile",
            "com.kotak811mobilebankingapp",
            "com.nextbillion.groww",
            "com.zerodha.kite3",
            "in.example.custom.upi",
            "com.example.bank.mobilebanking"
        };
        for (String pkg : paymentApps) {
            assertTrue("Expected payment/banking match for " + pkg,
                PaymentAppDetector.isPaymentOrBankingPackage(pkg));
        }

        String[] regularApps = {
            "com.desktouch.vision",
            "com.android.chrome",
            "com.android.settings",
            "com.android.systemui",
            "com.miui.securitycenter",
            "com.xiaomi.market",
            "com.whatsapp",
            "com.google.android.youtube",
            "com.instagram.android",
            "com.google.android.gms",
            "",
            null,
            "invalidpackage"
        };
        for (String pkg : regularApps) {
            assertFalse("Expected non-payment result for " + pkg,
                PaymentAppDetector.isPaymentOrBankingPackage(pkg));
        }
    }

    @Test
    public void shellBridgeInputAndHidKeyTableWorkWithoutAccessibility() {
        assertEquals(0x04, HidKeyTable.forAsciiChar('a').usage);
        assertEquals(0, HidKeyTable.forAsciiChar('a').modifier);
        assertEquals(0x04, HidKeyTable.forAsciiChar('A').usage);
        assertEquals(HidKeyTable.MOD_LEFT_SHIFT, HidKeyTable.forAsciiChar('A').modifier);
        assertEquals(0x27, HidKeyTable.forAsciiChar('0').usage);
        assertEquals(0x1E, HidKeyTable.forAsciiChar('!').usage);
        assertEquals(HidKeyTable.MOD_LEFT_SHIFT, HidKeyTable.forAsciiChar('!').modifier);
        assertEquals(0x28, HidKeyTable.forAsciiChar('\n').usage);
        assertEquals(0x2B, HidKeyTable.forAsciiChar('\t').usage);
        assertNull(HidKeyTable.forAsciiChar('\u20B9')); // INR symbol not pure ASCII HID

        assertEquals(HidKeyTable.MOD_LEFT_CTRL, HidKeyTable.modifierBitForVk(0x11));
        assertEquals(HidKeyTable.MOD_RIGHT_SHIFT, HidKeyTable.modifierBitForVk(0xA1));
        assertEquals(HidKeyTable.MOD_LEFT_ALT, HidKeyTable.modifierBitForVk(0xA4));
        assertEquals(0x28, HidKeyTable.usageForVk(0x0D));
        assertEquals(0x4F, HidKeyTable.usageForVk(0x27));
        assertEquals(0x3A, HidKeyTable.usageForVk(0x70));

        assertTrue(DeskTouchSession.shouldUseShellBridge(true, false));
        assertTrue(DeskTouchSession.shouldUseShellBridge(true, true));
        assertFalse(DeskTouchSession.shouldUseShellBridge(false, true));
        assertFalse(DeskTouchSession.shouldUseShellBridge(false, false));
    }

    @Test
    public void bandShiftAdaptiveTrackerClearsStaleBaselineAndScalesProbes() {
        AdaptiveLinkTracker tracker = new AdaptiveLinkTracker("5GHz");
        tracker.recordRttSample(280.0, 1000L);
        assertTrue(tracker.getSmoothedRttMs() > 200.0);
        int active5 = tracker.computeProbeIntervalMs(true);
        double pace5 = tracker.computeAdaptiveSendIntervalMs(90, "5GHz");

        assertTrue(tracker.onLinkProfileChanged("2.4GHz", 144, 1));
        assertEquals(0.0, tracker.getSmoothedRttMs(), 0.001);
        assertEquals("2.4GHz", tracker.getBand());
        int active24 = tracker.computeProbeIntervalMs(true);
        assertTrue("2.4 GHz probe gap should be wider than 5 GHz", active24 > active5);

        tracker.recordRttSample(12.0, 2000L);
        assertTrue("Fresh RTT should populate immediately after band shift",
            tracker.getSmoothedRttMs() < 30.0);
        double pace24 = tracker.computeAdaptiveSendIntervalMs(90, "2.4GHz");
        assertTrue("Clearing stale 280ms congestion should recover from max 5GHz slowdown",
            pace24 <= pace5);
        double pace5Clean = new AdaptiveLinkTracker("5GHz").computeAdaptiveSendIntervalMs(90, "5GHz");
        assertTrue("Equal-load 2.4 GHz send interval should be gentler than 5 GHz",
            pace24 > pace5Clean);
    }

    @Test
    public void wifiBandClassificationMatchesRealFrequencies() {
        assertEquals("2.4GHz", WifiLinkMonitor.classifyBand(2437));
        assertEquals("5GHz", WifiLinkMonitor.classifyBand(5805));
        assertEquals("6GHz", WifiLinkMonitor.classifyBand(6175));
        assertEquals("wireless", WifiLinkMonitor.classifyBand(0));
    }

    @Test
    public void keyboardBindingsAndExtendedKeysMatchSupportedModes() {
        java.util.List<KeyboardBindingInspector.ShortcutBinding> usbBindings =
            KeyboardBindingInspector.buildShortcutTable(
                true, false, true, true, true, true, true, true, true, true, true, true, true);
        assertTrue(usbBindings.size() >= 6);
        for (KeyboardBindingInspector.ShortcutBinding b : usbBindings) {
            assertTrue("USB full keyboard binding should be ready: " + b.action, b.activeNow);
        }

        java.util.List<KeyboardBindingInspector.ShortcutBinding> accBindings =
            KeyboardBindingInspector.buildShortcutTable(
                false, true, false, false, false, false, false, false, false, false, true, false, false);
        assertFalse(accBindings.isEmpty());
        boolean foundNeedCable = false;
        for (KeyboardBindingInspector.ShortcutBinding b : accBindings) {
            if (!b.activeNow && b.status.contains("data cable")) foundNeedCable = true;
        }
        assertTrue("Non-USB mode should explain cable requirement rather than faking bindings", foundNeedCable);

        assertEquals(0x53, HidKeyTable.usageForVk(0x90)); // NumLock
        assertEquals(0x64, HidKeyTable.usageForVk(0xE2)); // OEM 102
        assertEquals(0x68, HidKeyTable.usageForVk(0x7C)); // F13
        assertEquals(0x5B, MainActivity.toWindowsKey(android.view.KeyEvent.KEYCODE_META_LEFT));
        assertEquals(0x2E, MainActivity.toWindowsKey(android.view.KeyEvent.KEYCODE_FORWARD_DEL));
        assertEquals(0x90, MainActivity.toWindowsKey(android.view.KeyEvent.KEYCODE_NUM_LOCK));
        assertEquals(0xBA, MainActivity.toWindowsKey(android.view.KeyEvent.KEYCODE_SEMICOLON));
    }

    @Test
    public void heartbeatFastFailAndIntervalScaleByTransport() {
        long cableGap = DeskTouchSession.computeHeartbeatIntervalMs(true, true, "cable");
        long wifi5Gap = DeskTouchSession.computeHeartbeatIntervalMs(false, true, "5GHz");
        long wifi24Gap = DeskTouchSession.computeHeartbeatIntervalMs(false, true, "2.4GHz");
        assertTrue(cableGap < wifi5Gap);
        assertTrue(wifi5Gap < wifi24Gap);

        assertFalse(DeskTouchSession.shouldFastFailDeadSession(5000L, 1000L, 500L, 2, true));
        assertTrue(DeskTouchSession.shouldFastFailDeadSession(5000L, 1000L, 500L, 3, true));
        assertFalse(DeskTouchSession.shouldFastFailDeadSession(5000L, 1000L, 500L, 3, false));
        assertTrue(DeskTouchSession.shouldFastFailDeadSession(7000L, 1000L, 500L, 3, false));
    }

    @Test
    public void shellPointerMathCompensatesVelocityAndNeverDropsBufferedCommands() throws Exception {
        ShellPointerMath.VelocityParams parsed = ShellPointerMath.parseDumpsysVelocityParams(
            "  Configuration:\n" +
            "    PointerVelocityControlParameters: scale=0.500, lowThreshold=500.000, highThreshold=3000.000, acceleration=3.000\n");
        assertNotNull(parsed);
        assertEquals(0.500, parsed.scale, 0.001);
        assertEquals(500.0, parsed.lowThreshold, 0.1);
        assertEquals(3000.0, parsed.highThreshold, 0.1);
        assertEquals(3.0, parsed.acceleration, 0.01);

        ShellPointerMath.VelocityParams fallback = ShellPointerMath.defaultVelocityParams(-4);
        assertEquals(0.500, fallback.scale, 0.001);

        for (double targetSpeed : new double[] { 120.0, 250.0, 750.0, 1500.0, 2800.0, 5200.0 }) {
            double raw = ShellPointerMath.solveRawSpeedForOutputSpeed(targetSpeed, parsed);
            double reconstructed = raw * ShellPointerMath.gainAtRawSpeed(raw, parsed);
            assertEquals("VelocityControl inverse should reconstruct speed " + targetSpeed,
                targetSpeed, reconstructed, 0.05);
        }

        // Verify MOVETO coalescing preserves buffered END/BEGIN/PING commands instead of swallowing them.
        String wireStream =
            "MOVETO 10 20\n" +
            "MOVETO 30 40\n" +
            "END\n" +
            "BEGIN 1080 2460 -4 144\n" +
            "MOVETO 55 65\n" +
            "PING\n";
        java.io.BufferedReader br = new java.io.BufferedReader(new java.io.StringReader(wireStream));
        String[] pendingSlot = new String[1];
        assertEquals("MOVETO 30 40", ShellPointerMath.readNextCommand(br, pendingSlot));
        assertEquals("END", ShellPointerMath.readNextCommand(br, pendingSlot));
        assertEquals("BEGIN 1080 2460 -4 144", ShellPointerMath.readNextCommand(br, pendingSlot));
        assertEquals("MOVETO 55 65", ShellPointerMath.readNextCommand(br, pendingSlot));
        assertEquals("PING", ShellPointerMath.readNextCommand(br, pendingSlot));
        assertNull(ShellPointerMath.readNextCommand(br, pendingSlot));

        // Verify PointerCompensator only nudges edge once on boundary arrival, never continuously.
        ShellPointerMath.PointerCompensator comp = new ShellPointerMath.PointerCompensator();
        comp.configure(1080, 2460, parsed, 144);
        long t = 1_000_000_000L;
        ShellPointerMath.MoveStep entry = comp.stepTo(20, 100, t);
        assertTrue(entry.needsAnchorReset);
        ShellPointerMath.MoveStep hitEdge = comp.stepTo(0, 100, t + 4_000_000L);
        assertTrue("Arrival at x=0 must push negative edge align delta", hitEdge.rawX < 0);
        ShellPointerMath.MoveStep slideAlongEdge = comp.stepTo(0, 110, t + 8_000_000L);
        assertEquals("Sliding along left edge must not repeatedly inject -X edge bumps", 0, slideAlongEdge.rawX);
        assertTrue("Sliding along left edge must continue tracking Y movement", slideAlongEdge.rawY > 0);

        // Verify explicit edge push recovers even when target X is clamped at screen border.
        ShellPointerMath.MoveStep pushLeft = comp.stepTo(0, 110, -18, 0, t + 16_000_000L);
        assertTrue("Pushing past left boundary must emit negative recovery counts", pushLeft.rawX < 0);
        ShellPointerMath.MoveStep pushRightAtLeftZero = comp.stepTo(0, 110, 18, 0, t + 24_000_000L);
        assertEquals("Inward push at left edge must not emit edge push", 0, pushRightAtLeftZero.rawX);

        // Verify nearest-corner entry avoids cross-screen error spikes.
        ShellPointerMath.PointerCompensator farEntryComp = new ShellPointerMath.PointerCompensator();
        farEntryComp.configure(1080, 2460, parsed, 144);
        ShellPointerMath.MoveStep farEntry = farEntryComp.stepTo(1060, 2400, t);
        assertTrue(farEntry.needsAnchorReset);
        assertTrue("Far-side entry should anchor to bottom-right corner", farEntry.anchorPullX > 0 && farEntry.anchorPullY > 0);
        assertEquals("Nearest-corner jump should use exact minScale counts", -38, farEntry.rawX);
        assertEquals("Nearest-corner jump should use exact minScale counts on Y", -118, farEntry.rawY);

        // Verify full-screen sweep reaches right-bottom corner and pushes through if clamped.
        ShellPointerMath.MoveStep toRight = farEntryComp.stepTo(1079, 2459, 24, 20, t + 8_000_000L);
        assertTrue("Sweep to right-bottom corner must push both axes positive", toRight.rawX > 0 && toRight.rawY > 0);
        ShellPointerMath.MoveStep clampedPush = farEntryComp.stepTo(1079, 2459, 25, 0, t + 16_000_000L);
        assertTrue("Continuous right push at clamped edge must keep moving pointer right", clampedPush.rawX > 0);
        assertEquals("No Y push should emit zero Y counts at bottom edge", 0, clampedPush.rawY);
    }

    @Test
    public void trustResponseCorrelationMatchesIdOrLegacyZero() {
        assertTrue(DeskTouchSession.acceptPendingTrustResponse(7, 7));
        assertTrue(DeskTouchSession.acceptPendingTrustResponse(7, 0));
        assertFalse(DeskTouchSession.acceptPendingTrustResponse(7, 6));
        assertFalse(DeskTouchSession.buildPendingTrustNotice().isEmpty());
    }

    @Test
    public void middleScreenEntryAccountsForVelocitySaturation() {
        ShellPointerMath.VelocityParams parsed = ShellPointerMath.parseDumpsysVelocityParams(
            "    PointerVelocityControlParameters: scale=0.500, lowThreshold=500.000, highThreshold=3000.000, acceleration=3.000");
        ShellPointerMath.PointerCompensator midComp = new ShellPointerMath.PointerCompensator();
        midComp.configure(1080, 2460, parsed, 144);
        long t = 2_000_000_000L;
        ShellPointerMath.MoveStep entry = midComp.stepTo(1079, 1230, t);
        assertTrue(entry.needsAnchorReset);
        // -1229px from bottom-right corner: first -127 counts at 0.5 (-63.5px), remaining -1165.5px at 1.5 (-777 counts) = -904
        assertEquals("Middle-screen entry must not overshoot 3x using scale-only division", -904, entry.rawY);
    }

    @Test
    public void continuousStepsCapToHidLimitAndDaemonPacingAlignsWithMinFrame() {
        long gap144 = ShellPointerMath.computeDaemonPacingIntervalNs(144);
        assertTrue("144 Hz UHID pacing must exceed minFrameSec and stay under 1x frame",
            gap144 >= 4_000_000L && gap144 <= 6_000_000L);

        ShellPointerMath.VelocityParams parsed = ShellPointerMath.parseDumpsysVelocityParams(
            "    PointerVelocityControlParameters: scale=0.500, lowThreshold=500.000, highThreshold=3000.000, acceleration=3.000");
        ShellPointerMath.PointerCompensator comp = new ShellPointerMath.PointerCompensator();
        comp.configure(1080, 2460, parsed, 144);
        long t = 3_000_000_000L;
        ShellPointerMath.MoveStep entry = comp.stepTo(540, 200, t);
        assertTrue(entry.needsAnchorReset);

        // Fast continuous swipe must stay within single 8-bit HID report [-127, 127] and settle cleanly.
        t += gap144;
        ShellPointerMath.MoveStep fast = comp.stepTo(540, 620, t);
        assertFalse(fast.needsAnchorReset);
        assertTrue("Continuous UHID step must never split into microsecond multi-report bursts",
            Math.abs(fast.rawX) <= 127 && Math.abs(fast.rawY) <= 127);
        assertTrue("Continuous swipe must advance positive Y", fast.rawY > 0);
        int settleSteps = 0;
        for (int i = 0; i < 8; i++) {
            t += gap144;
            ShellPointerMath.MoveStep next = comp.stepTo(540, 620, t);
            assertTrue(Math.abs(next.rawX) <= 127 && Math.abs(next.rawY) <= 127);
            if (!next.hasMotion()) break;
            settleSteps++;
        }
        assertTrue("Continuous swipe excess must settle quickly across paced frames", settleSteps >= 1 && settleSteps <= 5);
    }

    @Test
    public void cableLiveLinkHidesPingAndWirelessUsesPercentLoss() {
        DeskTouchSession.LiveStatusSnapshot cableSnap = new DeskTouchSession.LiveStatusSnapshot(
            true, "DESKTOP-IQ1KSJ9", "FP1", true, WifiLinkMonitor.BAND_CABLE, 0,
            4.5, 1.2, 5.0, "", null);
        String cableText = MainActivity.formatLiveLinkText(cableSnap, "Wi-Fi");
        assertTrue(cableText.contains("via Data cable"));
        assertFalse("Data cable status must never show ping", cableText.contains("ping"));
        assertFalse("Data cable status must never show jitter", cableText.contains("jitter"));

        DeskTouchSession.LiveStatusSnapshot wifiSnap = new DeskTouchSession.LiveStatusSnapshot(
            true, "DESKTOP-IQ1KSJ9", "FP1", false, WifiLinkMonitor.BAND_5_GHZ, 433,
            12.4, 2.1, 30.0, "", null);
        String wifiText = MainActivity.formatLiveLinkText(wifiSnap, "Wi-Fi");
        assertTrue(wifiText.contains("ping 12.4 ms"));
        assertTrue(wifiText.contains("loss 3.0%"));
        assertFalse("Wireless UI must show percentage rather than per-mille", wifiText.contains("‰"));
    }

    @Test
    public void reorderedUdpCoordinatesTrackLossZeroAndEdgePushUnpacksCleanly() throws Exception {
        byte[] key = new byte[FastUdpChannel.KEY_SIZE];
        for (int i = 0; i < key.length; i++) key[i] = (byte) (i + 3);
        FastUdpChannel.Codec srv = new FastUdpChannel.Codec(key, 12, true);
        FastUdpChannel.Codec cli = new FastUdpChannel.Codec(key, 12, false);
        AdaptiveLinkTracker tracker = new AdaptiveLinkTracker("5GHz");

        byte[] seq1Move = new byte[FastUdpChannel.FRAME_SIZE];
        byte[] seq2Push = new byte[FastUdpChannel.FRAME_SIZE];
        srv.seal(FastUdpChannel.OP_REVERSE_MOVE, 900, 1800, 44, 1, seq1Move);
        int packedA = ((24 & 0xFFFF) << 16) | (1079 & 0xFFFF);
        int packedB = ((18 & 0xFFFF) << 16) | (2459 & 0xFFFF);
        srv.seal(FastUdpChannel.OP_REVERSE_EDGE_PUSH, packedA, packedB, 44, 2, seq2Push);

        FastUdpChannel.Message opened2 = cli.tryOpen(seq2Push, seq2Push.length, tracker);
        assertNotNull(opened2);
        assertEquals(FastUdpChannel.OP_REVERSE_EDGE_PUSH, opened2.op);
        assertEquals(1079, (short) (opened2.a & 0xFFFF));
        assertEquals(24, (short) (opened2.a >> 16));
        assertEquals(2459, (short) (opened2.b & 0xFFFF));
        assertEquals(18, (short) (opened2.b >> 16));
        tracker.recordInboundPacket(opened2.seq, false, 0, 1000L);

        // Stale coordinate still rejected for pointer motion, yet credited to prevent false loss.
        assertNull(cli.tryOpen(seq1Move, seq1Move.length, tracker));
        for (long s = 3; s <= 15; s++) {
            byte[] next = new byte[FastUdpChannel.FRAME_SIZE];
            srv.seal(FastUdpChannel.OP_REVERSE_MOVE, 1000, 2000, 44, (int) s, next);
            FastUdpChannel.Message m = cli.tryOpen(next, next.length, tracker);
            assertNotNull(m);
            tracker.recordInboundPacket(m.seq, false, 0, 1000L + s * 6L);
        }
        assertEquals("Reordered frame must not count as lost", 0L, tracker.getLostPackets());
        assertEquals("Reordered frame must be tracked as reordered", 1L, tracker.getReorderedPackets());
    }

    @Test
    public void pointerCompensatorFullScreenCoastReachesAllCorners() {
        ShellPointerMath.VelocityParams parsed = ShellPointerMath.parseDumpsysVelocityParams(
            "    PointerVelocityControlParameters: scale=0.500, lowThreshold=500.000, highThreshold=3000.000, acceleration=3.000");
        ShellPointerMath.PointerCompensator comp = new ShellPointerMath.PointerCompensator();
        comp.configure(1080, 2460, parsed, 144);
        long t = 5_000_000_000L;
        long gap = ShellPointerMath.computeDaemonPacingIntervalNs(144);

        comp.stepTo(100, 100, t);
        int[][] corners = { {1079, 2459}, {0, 2459}, {0, 0}, {1079, 0} };
        for (int[] corner : corners) {
            int steps = 0;
            while (steps < 40) {
                t += gap;
                ShellPointerMath.MoveStep s = comp.stepTo(corner[0], corner[1], t);
                assertTrue(Math.abs(s.rawX) <= 127 && Math.abs(s.rawY) <= 127);
                steps++;
                if (!comp.hasRemainingMotion(corner[0], corner[1])) break;
            }
            assertFalse("Pointer must converge cleanly to corner " + corner[0] + "," + corner[1],
                comp.hasRemainingMotion(corner[0], corner[1]));
        }
    }

    @Test
    public void deviceSpecificVelocityParamsExactBorderResetAndPrecisionStepsWork() {
        String dumpsys =
            "Event Hub State:\n" +
            "  3: DeskTouch USB Mouse\n" +
            "    Classes: CURSOR\n" +
            "Input Reader State:\n" +
            "  Device 2: Other Touchpad\n" +
            "    PointerVelocityControlParameters: scale=2.000, lowThreshold=200.000, highThreshold=1000.000, acceleration=4.000\n" +
            "  Device 3: DeskTouch USB Mouse\n" +
            "    Cursor Input Mapper:\n" +
            "      PointerVelocityControlParameters: scale=0.625, lowThreshold=450.000, highThreshold=2600.000, acceleration=2.500\n";
        ShellPointerMath.VelocityParams matched =
            ShellPointerMath.parseDumpsysVelocityParamsForDevice(dumpsys, "DeskTouch USB Mouse");
        assertNotNull(matched);
        assertEquals(0.625, matched.scale, 0.001);
        assertEquals(2.500, matched.acceleration, 0.001);

        ShellPointerMath.PointerCompensator borderComp = new ShellPointerMath.PointerCompensator();
        borderComp.configure(1080, 2460, matched, 144);
        long t = 6_000_000_000L;
        ShellPointerMath.MoveStep exactCorner = borderComp.stepTo(1079, 2459, t);
        assertTrue(exactCorner.needsAnchorReset);
        assertTrue("Exact corner entry must emit 1-count opposite resetSign on X and Y",
            exactCorner.resetSignX < 0 && exactCorner.resetSignY < 0);

        // Slow 1px adjustment right after fast swipe must stay pending until settled cleanly.
        t += 4_000_000L;
        borderComp.stepTo(500, 1200, t);
        t += 4_000_000L;
        borderComp.stepTo(501, 1200, t);
        assertTrue("Small 1px delta must remain pending whenever not within sub-count tolerance",
            borderComp.hasRemainingMotion(503, 1200));
    }

    @Test
    public void scaledVelocityDomainAospLsqTrackerAndSubFrameGlidePreventFrameSkips() {
        ShellPointerMath.VelocityParams xiaomi = new ShellPointerMath.VelocityParams(0.500, 500.0, 3000.0, 3.0);
        // Velocity thresholds apply to scaled speed (raw * scale): at scale=0.5, 864 raw/s -> 432 px/s <= 500.
        assertEquals(0.500, ShellPointerMath.gainAtRawSpeed(864.0, xiaomi), 1e-6);
        assertEquals(0.6456, ShellPointerMath.gainAtRawSpeed(1728.0, xiaomi), 1e-4);

        // Curve test: sinusoidal Y reversal must NOT wipe independent X LSQ2 velocity.
        ShellPointerMath.AospAxisTracker tx = new ShellPointerMath.AospAxisTracker();
        ShellPointerMath.AospAxisTracker ty = new ShellPointerMath.AospAxisTracker();
        long t = 1_000_000_000L;
        long dt144 = 6_944_444L;
        for (int i = 0; i < 16; i++) {
            int rawY = i < 8 ? 10 : -10;
            tx.add(t, 24);
            ty.add(t, rawY);
            t += dt144;
        }
        assertTrue("X velocity must stay fast when Y reverses along a curve", tx.velocity() > 2800.0);

        // Sub-frame pacing: 144Hz -> ~3.47ms (288Hz), splitting 125Hz (8ms) updates across 2 ticks without tail lag.
        long sub144 = ShellPointerMath.computeSubFrameIntervalNs(144);
        assertTrue(sub144 >= 3_200_000L && sub144 <= 3_800_000L);
        ShellPointerMath.SubFrameGlide glide = new ShellPointerMath.SubFrameGlide();
        glide.configure(144);
        int[] out = new int[2];
        long clk = 10_000_000_000L;
        glide.onTarget(100, 100, 0, 0, clk);
        assertTrue(glide.sampleNext(clk, out));
        assertEquals(100, out[0]);
        for (int i = 1; i <= 4; i++) {
            clk += 8_000_000L; // 125 Hz input mouse updates
            glide.onTarget(100 + i * 20, 100, 0, 0, clk);
            if (i < 4) {
                assertTrue(glide.sampleNext(clk, out));
                assertTrue(glide.sampleNext(clk + sub144, out));
                assertTrue(glide.sampleNext(clk + sub144 * 2L, out));
                assertEquals(100 + i * 20, out[0]);
            }
        }
        assertTrue(glide.sampleNext(clk, out));
        assertTrue("125Hz input on 144Hz display should glide smoothly rather than jumping full 20px on tick 1",
            out[0] > 160 && out[0] < 180);
        glide.snapToTarget();
        assertTrue(glide.sampleNext(clk + sub144, out));
        assertEquals("Click/flush snap must hit exact newest target", 180, out[0]);

        // High-rate >=250Hz input passes immediately with zero artificial glide lag.
        glide.reset();
        glide.onTarget(200, 200, 0, 0, clk);
        assertTrue(glide.sampleNext(clk, out));
        for (int i = 1; i <= 4; i++) {
            clk += 4_000_000L;
            glide.onTarget(200 + i * 10, 200, 0, 0, clk);
        }
        assertTrue(glide.sampleNext(clk, out));
        assertEquals(240, out[0]);

        // Verify v0.3.52 wireless loss recovery after brief radio idle gap without masking streaming loss.
        AdaptiveLinkTracker wifiRecovery = new AdaptiveLinkTracker("2.4GHz");
        wifiRecovery.recordInboundPacket(1L, true, 1000, 1000L);
        wifiRecovery.recordInboundPacket(100L, true, 1100, 1100L);
        wifiRecovery.recordInboundPacket(200L, true, 1200, 1200L);
        double burstLoss = wifiRecovery.getInboundLossPermille();
        assertTrue("Burst skip should register non-zero loss", burstLoss > 50.0);
        wifiRecovery.recordInboundPacket(201L, true, 4200, 4200L);
        assertTrue("Quiet radio gap must decay stale loss rather than freezing UI above 30%",
            wifiRecovery.getInboundLossPermille() < burstLoss * 0.6);
    }

    @Test
    public void cachedLeafDoesNotBypassHubAndWirelessMigratesToRealHub() throws Exception {
        String hubFp = "B".repeat(64);
        String leafFp = "A".repeat(64);
        assertEquals(hubFp, DesktopDiscovery.parseHubHintFingerprint("DT1-HUB|" + hubFp.toLowerCase()));
        assertNull(DesktopDiscovery.parseHubHintFingerprint("DT1-READY|PC|" + hubFp));

        DesktopDiscovery.Device leaf = new DesktopDiscovery.Device("DESKTOP", "192.168.1.10", leafFp, true, false);
        leaf.isHub = false;
        DesktopDiscovery.Device hub = new DesktopDiscovery.Device("SURAJ", "192.168.1.11", hubFp, true, false);
        hub.isHub = true;

        assertFalse("Cached leaf PC must not bypass multi-PC LAN hub scan",
            AutoConnectService.shouldAcceptCachedDirectPeer(leaf, 2));
        assertTrue("Single trusted PC remains directly reachable",
            AutoConnectService.shouldAcceptCachedDirectPeer(leaf, 1));
        assertTrue("True hub bypasses LAN broadcast via cached probe",
            AutoConnectService.shouldAcceptCachedDirectPeer(hub, 2));

        assertTrue("Idle wireless peer on leaf PC must migrate to real trusted hub",
            AutoConnectService.shouldSwitchWirelessPeerToHub(false, false, 2, leaf, hub));
        assertFalse("Data cable session must never migrate off cable to LAN hub",
            AutoConnectService.shouldSwitchWirelessPeerToHub(true, false, 2, leaf, hub));
        assertFalse("Active input lease must never be interrupted mid-control",
            AutoConnectService.shouldSwitchWirelessPeerToHub(false, true, 2, leaf, hub));
        assertFalse("Already connected to the hub must not re-migrate",
            AutoConnectService.shouldSwitchWirelessPeerToHub(false, false, 2, hub, leaf));

        // Verify v0.3.54: DT1-HUBCOUNT parsing & merging smaller hub into larger hub.
        String bigHubFp = "C".repeat(64);
        String[] parsedHint =
            DesktopDiscovery.parseHubCountHint("DT1-HUBCOUNT|" + bigHubFp.toLowerCase() + "|3");
        assertNotNull(parsedHint);
        assertEquals(bigHubFp, parsedHint[0]);
        assertEquals("3", parsedHint[1]);
        assertNull(DesktopDiscovery.parseHubCountHint("DT1-HUBCOUNT|" + bigHubFp + "|0"));

        DesktopDiscovery.Device smallHub = new DesktopDiscovery.Device("SURAJ", "192.168.1.11", hubFp, true, false);
        smallHub.isHub = true;
        smallHub.hubMemberCount = 2;
        DesktopDiscovery.Device largeHub = new DesktopDiscovery.Device("DESKTOP", "192.168.1.10", bigHubFp, true, false);
        largeHub.isHub = true;
        largeHub.hubMemberCount = 3;

        assertTrue("Smaller 2-device hub must migrate to larger 3-device hub",
            AutoConnectService.shouldSwitchWirelessPeerToHub(false, false, 3, smallHub, largeHub));
        assertFalse("Must never downgrade from 3-device hub to smaller 2-device hub",
            AutoConnectService.shouldSwitchWirelessPeerToHub(false, false, 3, largeHub, smallHub));

        // Verify v0.3.56 wireless outlier filtering & fast awake recovery.
        AdaptiveLinkTracker wakeTracker = new AdaptiveLinkTracker("5GHz");
        wakeTracker.recordRttSample(62.0, 1000L);
        wakeTracker.recordRttSample(4.2, 1100L);
        wakeTracker.recordRttSample(4.5, 1200L);
        wakeTracker.recordRttSample(58.0, 1300L);
        wakeTracker.recordRttSample(4.0, 1400L);
        assertTrue("Awake wireless link must stay <10ms RTT despite isolated sleep spike",
            wakeTracker.getSmoothedRttMs() < 10.0);
        assertTrue("Awake wireless link must stay <5ms jitter despite isolated sleep spike",
            wakeTracker.getEffectiveJitterMs() < 5.0);

        // Verify v0.3.57 dual-coordinate packing / unpacking and waypoint recovery.
        int packedDual = ((100 + 1) << 16) | 150;
        assertEquals(150, FastUdpChannel.unpackCurr(packedDual));
        assertEquals(100, FastUdpChannel.unpackPrev(packedDual));

        // Legacy / single coordinate unpacking returns identity.
        assertEquals(300, FastUdpChannel.unpackCurr(300));
        assertEquals(300, FastUdpChannel.unpackPrev(300));

        // SubFrameGlide waypoint progression.
        ShellPointerMath.SubFrameGlide glide = new ShellPointerMath.SubFrameGlide();
        glide.configure(120);
        int[] out = new int[2];
        long t = 100_000_000L;
        glide.onTarget(10, 10, 10, 10, 0, 0, t);
        assertTrue(glide.sampleNext(t, out));
        assertEquals(10, out[0]);
        assertEquals(10, out[1]);

        // Simulating packet drop: cursor is at 10,10, but next packet carries waypoint prev=(25,25) and target=(40,40).
        t += 16_000_000L;
        glide.onTarget(25, 25, 40, 40, 0, 0, t);
        assertTrue(glide.isActive());
        // First sub-frame tick must move towards waypoint (25, 25), NOT snap to 40, 40.
        glide.sampleNext(t + 4_000_000L, out);
        assertTrue("Subframe coordinate must advance through intermediate waypoint", out[0] > 10 && out[0] <= 25);

        // PointerSmoother (wireless Accessibility mode) must also traverse dropped (-) waypoint.
        PointerSmoother smoother = new PointerSmoother();
        smoother.setDisplayRefreshHz(120);
        smoother.reset(10f, 10f, t);
        float[] sOut = new float[2];
        assertTrue(smoother.onTarget(25f, 25f, 40f, 40f, t + 16_000_000L));
        assertTrue(smoother.stepFrame(t + 24_000_000L, sOut));
        assertEquals(25, Math.round(sOut[0]));
        assertEquals(25, Math.round(sOut[1]));
        assertFalse("PointerSmoother must remain unsettled until reaching (+) target after (-) waypoint", smoother.isSettled());
        assertTrue(smoother.stepFrame(t + 32_000_000L, sOut));
        assertEquals(40, Math.round(sOut[0]));
        assertEquals(40, Math.round(sOut[1]));

        // Verify v0.3.64: Clean-jitter RF drops stay crisp; cellular scoring & cooldowns work cleanly.
        PointerSmoother rfSmoother = new PointerSmoother();
        rfSmoother.setDisplayRefreshHz(144);
        rfSmoother.setLinkMetrics(2.0, 150.0);
        assertTrue("Clean-jitter RF drops must stay crisp (<= 3.5 ms tau)",
            rfSmoother.getCurrentTauMs() <= 3.5);

        assertEquals("cellular", AdaptiveLinkTracker.normalizeBand("cellular"));
        assertEquals("cellular", AdaptiveLinkTracker.normalizeBand("Mobile"));
        AdaptiveLinkTracker dropTracker = new AdaptiveLinkTracker("2.4GHz");
        dropTracker.recordRttSample(4.5, 1000L);
        dropTracker.recordRttSample(6.0, 1020L);
        assertTrue("Clean-jitter low-RTT RF drops on 2.4GHz must stay <= 10.5ms pace",
            dropTracker.computeAdaptiveSendIntervalMs(144, "2.4GHz") <= 10.5);

        assertTrue("Degraded 2.4 GHz Wi-Fi (8% loss) must prefer clean 30ms Mobile network",
            CellularLinkHelper.shouldPreferCellular(5.0, 3.0, 80.0, 30.0, 4.0, 0.0, 60_000L, 0L));
        assertFalse("Clean 5 GHz Wi-Fi must never switch to 30ms Mobile network",
            CellularLinkHelper.shouldPreferCellular(4.0, 1.5, 0.0, 30.0, 4.0, 0.0, 60_000L, 0L));
        assertFalse("Cooldown window must prevent rapid transport flapping",
            CellularLinkHelper.shouldPreferCellular(5.0, 3.0, 80.0, 30.0, 4.0, 0.0, 65_000L, 55_000L));
        assertTrue("Healthy low-latency Wi-Fi returning must beat 35ms Mobile network after cooldown",
            CellularLinkHelper.shouldReturnToWifi(35.0, 4.0, 0.0, 4.5, 1.5, 0.0, 90_000L, 50_000L));

        // Verify v0.3.65: recent peak loss memory persists across idle gaps for cellular evaluation & clears on profile change.
        AdaptiveLinkTracker peakTracker = new AdaptiveLinkTracker("2.4GHz");
        peakTracker.recordInboundPacket(1L, true, 1000, 1000L);
        peakTracker.recordInboundPacket(95L, true, 1200, 1200L);
        double livePeak = peakTracker.getInboundLossPermille();
        assertTrue("Burst drop must populate peak loss window", livePeak > 50.0);
        peakTracker.recordInboundPacket(96L, true, 6200, 6200L);
        assertTrue("Recent peak loss window must retain active burst after idle gap",
            peakTracker.getRecentPeakLossPermille(8000L) >= livePeak * 0.99);
        assertEquals("Recent peak loss window must expire cleanly after 45s",
            0.0, peakTracker.getRecentPeakLossPermille(60_000L), 0.001);
        peakTracker.onLinkProfileChanged("5GHz", 866, 2);
        assertEquals("Link profile change must clear prior band peak loss",
            0.0, peakTracker.getRecentPeakLossPermille(8000L), 0.001);

        DeskTouchSession.LiveStatusSnapshot peakSnap = new DeskTouchSession.LiveStatusSnapshot(
            true, "DESKTOP", "A".repeat(64), false, "2.4GHz", 144, 5.0, 2.0, 2.0, 85.0, 5.5, "", null);
        assertTrue("Moderate recent peak Wi-Fi loss (8.5%) must still promote fast 15ms Mobile network",
            CellularLinkHelper.shouldPreferCellular(
                peakSnap.rttMs, peakSnap.recentPeakJitterMs, peakSnap.recentPeakLossPermille,
                15.0, 2.0, 0.0, 70_000L, 0L));

        // Verify v0.3.66: Auxiliary WirePeerId AEAD probe/ack UDP frame exchange over real DatagramSockets.
        try (java.net.DatagramSocket echoServer = new java.net.DatagramSocket(0, java.net.InetAddress.getLoopbackAddress());
             java.net.DatagramSocket clientSock = new java.net.DatagramSocket(0, java.net.InetAddress.getLoopbackAddress())) {
            int srvPort = echoServer.getLocalPort();
            byte[] key = new byte[FastUdpChannel.KEY_SIZE];
            for (int i = 0; i < key.length; i++) key[i] = (byte) (i + 1);
            int auxPeerId = 0x8001;
            FastUdpChannel.Codec serverCodec = new FastUdpChannel.Codec(key, auxPeerId, true);
            FastUdpChannel.Codec clientCodec = new FastUdpChannel.Codec(key, auxPeerId, false);
            byte[] txCli = new byte[FastUdpChannel.FRAME_SIZE];
            int nCli = clientCodec.seal(FastUdpChannel.OP_PROBE, clientSock.getLocalPort(), 0, 0, 1, txCli);
            clientSock.send(new java.net.DatagramPacket(txCli, nCli, java.net.InetAddress.getLoopbackAddress(), srvPort));

            byte[] rxSrv = new byte[128];
            java.net.DatagramPacket pSrv = new java.net.DatagramPacket(rxSrv, rxSrv.length);
            echoServer.setSoTimeout(1500);
            echoServer.receive(pSrv);
            FastUdpChannel.Message srvMsg = serverCodec.tryOpen(rxSrv, pSrv.getLength(), null);
            assertNotNull(srvMsg);
            assertEquals(FastUdpChannel.OP_PROBE, srvMsg.op);

            byte[] txSrv = new byte[FastUdpChannel.FRAME_SIZE];
            int nSrv = serverCodec.seal(FastUdpChannel.OP_PROBE_ACK, srvPort, 0, 0, 2, txSrv);
            echoServer.send(new java.net.DatagramPacket(txSrv, nSrv, pSrv.getAddress(), pSrv.getPort()));

            byte[] rxCli = new byte[128];
            java.net.DatagramPacket pCli = new java.net.DatagramPacket(rxCli, rxCli.length);
            clientSock.setSoTimeout(1500);
            clientSock.receive(pCli);
            FastUdpChannel.Message cliMsg = clientCodec.tryOpen(rxCli, pCli.getLength(), null);
            assertNotNull(cliMsg);
            assertEquals(FastUdpChannel.OP_PROBE_ACK, cliMsg.op);
            assertEquals(srvPort, cliMsg.a);
            clientCodec.close();
            serverCodec.close();
        }

        // Verify v0.3.67: Independent FastUdpChannel.OP_REVERSE_KEY, AdaptiveLinkTracker interactive classification,
        // and loss-triggered stream switching without flip-flopping on 0% streams.
        assertTrue("OP_REVERSE_KEY must be classified as interactive",
            AdaptiveLinkTracker.isInteractiveOp(FastUdpChannel.OP_REVERSE_KEY));
        byte[] kbKey = new byte[FastUdpChannel.KEY_SIZE];
        for (int i = 0; i < kbKey.length; i++) kbKey[i] = (byte) (i + 7);
        FastUdpChannel.Codec srvKb = new FastUdpChannel.Codec(kbKey, 42, true);
        FastUdpChannel.Codec cliKb = new FastUdpChannel.Codec(kbKey, 42, false);
        byte[] kbFrame = new byte[FastUdpChannel.FRAME_SIZE];
        int kbLen = srvKb.seal(FastUdpChannel.OP_REVERSE_KEY, 65, 1, 77, 19, kbFrame);
        assertEquals(FastUdpChannel.FRAME_SIZE, kbLen);
        FastUdpChannel.Message openedKb = cliKb.tryOpen(kbFrame, kbLen, null);
        assertNotNull(openedKb);
        assertEquals(FastUdpChannel.OP_REVERSE_KEY, openedKb.op);
        assertEquals(65, openedKb.a);
        assertEquals(1, openedKb.b);
        assertEquals(77, openedKb.lease);
        assertEquals(19, openedKb.timestampMs);
        srvKb.close();
        cliKb.close();

        assertFalse("Clean 0% active stream must NEVER switch even if alternate is 0%",
            CellularLinkHelper.shouldSwitchStreamOnLoss(0.0, 0.0, 50_000L, 10_000L, 8_000L));
        assertFalse("Sub-1% loss (0.8% = 8 permille) must stay on current remembered stream",
            CellularLinkHelper.shouldSwitchStreamOnLoss(8.0, 0.0, 50_000L, 10_000L, 8_000L));
        assertFalse("High active loss (2.0%) must continue on current path if alternate is no cleaner",
            CellularLinkHelper.shouldSwitchStreamOnLoss(20.0, 20.0, 50_000L, 10_000L, 8_000L));
        assertFalse("Cooldown window must prevent rapid repeated stream switching",
            CellularLinkHelper.shouldSwitchStreamOnLoss(25.0, 0.0, 14_000L, 10_000L, 8_000L));
        assertTrue("Active >= 1.0% loss (12 permille) must switch when alternate stream is cleaner",
            CellularLinkHelper.shouldSwitchStreamOnLoss(12.0, 0.0, 50_000L, 10_000L, 8_000L));

        // Verify v0.3.68: Keyboard automatically selects faster 0%-loss path (e.g. 4ms Wi-Fi vs 20ms Mobile) without oscillating back.
        assertTrue("Keyboard on 20.5ms 0%-loss Mobile must switch to faster 4.2ms 0%-loss Wi-Fi",
            CellularLinkHelper.shouldSwitchKeyboardToFasterZeroLoss(20.5, 0.0, 4.2, 0.0, 50_000L, 10_000L, 15_000L));
        assertFalse("Keyboard already on faster 4.2ms 0%-loss Wi-Fi must NEVER bounce back to 20.5ms Mobile",
            CellularLinkHelper.shouldSwitchKeyboardToFasterZeroLoss(4.2, 0.0, 20.5, 0.0, 50_000L, 10_000L, 15_000L));
        assertFalse("Keyboard must NEVER switch to a lossy alternate path even if lower RTT",
            CellularLinkHelper.shouldSwitchKeyboardToFasterZeroLoss(20.5, 0.0, 4.2, 5.0, 50_000L, 10_000L, 15_000L));

        // Verify v0.3.69: PointerSmoother feel modes, DeskTouchSession null-safe defaults, and split live link display.
        PointerSmoother feelSmoother = new PointerSmoother();
        feelSmoother.setDisplayRefreshHz(120);
        feelSmoother.setLinkMetrics(28.0, 45.0);
        feelSmoother.setFeelMode(PointerSmoother.FEEL_RAW_DIRECT);
        assertEquals("Raw 0ms feel mode must keep tau at 0.0 even under jitter/loss",
            0.0, feelSmoother.getCurrentTauMs(), 0.001);
        feelSmoother.setLinkMetrics(2.0, 0.0);
        feelSmoother.setFeelMode(PointerSmoother.FEEL_SMOOTH);
        assertTrue("Smooth feel mode must apply positive smoothing even on quiet link",
            feelSmoother.getCurrentTauMs() >= 8.0);
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, DeskTouchSession.readMouseMode(null));
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, DeskTouchSession.readKeyboardMode(null));
        assertTrue(DeskTouchSession.readDualPathControls(null));
        assertTrue(DeskTouchSession.readKeepChannelsWarm(null));
        assertEquals(PointerSmoother.FEEL_AUTO, DeskTouchSession.readPointerFeelMode(null));

        DeskTouchSession.LiveStatusSnapshot splitUiSnap = new DeskTouchSession.LiveStatusSnapshot(
            true, "DESKTOP-IQ1KSJ9", "FP1", false, WifiLinkMonitor.BAND_CELLULAR, 0,
            19.8, 1.5, 0.0, 0.0, 1.5, "", null,
            true, false, 4.2, 0.0, 19.8, 0.0, true, true);
        String splitUiText = MainActivity.formatLiveLinkText(splitUiSnap, "Wireless");
        assertTrue(splitUiText.contains("Mouse: Mobile (19.8 ms)"));
        assertTrue(splitUiText.contains("Keys: Wireless (4.2 ms)"));
        assertTrue(splitUiText.contains("[Dual-path]"));
    }

    @Test
    public void phoneUiKitThemeAndSegmentMappingWorkSafely() {
        assertEquals(PhoneUiKit.THEME_SLATE, PhoneUiKit.clampTheme(-1));
        assertEquals(PhoneUiKit.THEME_SLATE, PhoneUiKit.clampTheme(99));
        assertEquals(PhoneUiKit.THEME_AMOLED, PhoneUiKit.clampTheme(PhoneUiKit.THEME_AMOLED));
        assertEquals(PhoneUiKit.THEME_LIGHT, PhoneUiKit.clampTheme(PhoneUiKit.THEME_LIGHT));
        assertEquals(PhoneUiKit.THEME_SLATE, PhoneUiKit.readTheme(null));
        PhoneUiKit.writeTheme(null, PhoneUiKit.THEME_LIGHT);

        PhoneUiKit.Palette slate = PhoneUiKit.palette(PhoneUiKit.THEME_SLATE);
        PhoneUiKit.Palette amoled = PhoneUiKit.palette(PhoneUiKit.THEME_AMOLED);
        PhoneUiKit.Palette light = PhoneUiKit.palette(PhoneUiKit.THEME_LIGHT);
        assertEquals(0xFF12161F, slate.canvasBg);
        assertEquals(0xFF171D29, slate.chromeBg);
        assertEquals(0xFF1B2230, slate.cardBg);
        assertEquals(0xFF0EA5E9, slate.accent);
        assertFalse(slate.lightBars);
        assertEquals(0xFF000000, amoled.canvasBg);
        assertFalse(amoled.lightBars);
        assertEquals(0xFFF1F5F9, light.canvasBg);
        assertTrue(light.lightBars);
        assertEquals("Midnight Slate", PhoneUiKit.themeName(PhoneUiKit.THEME_SLATE));
        assertEquals("AMOLED Black", PhoneUiKit.themeName(PhoneUiKit.THEME_AMOLED));
        assertEquals("Crisp Light", PhoneUiKit.themeName(PhoneUiKit.THEME_LIGHT));

        assertEquals(0, PhoneUiKit.mouseModeToSegment(DeskTouchSession.STREAM_MODE_AUTO));
        assertEquals(1, PhoneUiKit.mouseModeToSegment(DeskTouchSession.STREAM_MODE_FORCE_MOBILE));
        assertEquals(2, PhoneUiKit.mouseModeToSegment(DeskTouchSession.STREAM_MODE_FORCE_WIFI));
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, PhoneUiKit.segmentToMouseMode(0));
        assertEquals(DeskTouchSession.STREAM_MODE_FORCE_MOBILE, PhoneUiKit.segmentToMouseMode(1));
        assertEquals(DeskTouchSession.STREAM_MODE_FORCE_WIFI, PhoneUiKit.segmentToMouseMode(2));
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, PhoneUiKit.segmentToMouseMode(99));

        assertEquals(0, PhoneUiKit.keyboardModeToSegment(DeskTouchSession.STREAM_MODE_AUTO));
        assertEquals(1, PhoneUiKit.keyboardModeToSegment(DeskTouchSession.STREAM_MODE_FORCE_WIFI));
        assertEquals(2, PhoneUiKit.keyboardModeToSegment(DeskTouchSession.STREAM_MODE_FORCE_MOBILE));
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, PhoneUiKit.segmentToKeyboardMode(0));
        assertEquals(DeskTouchSession.STREAM_MODE_FORCE_WIFI, PhoneUiKit.segmentToKeyboardMode(1));
        assertEquals(DeskTouchSession.STREAM_MODE_FORCE_MOBILE, PhoneUiKit.segmentToKeyboardMode(2));
        assertEquals(DeskTouchSession.STREAM_MODE_AUTO, PhoneUiKit.segmentToKeyboardMode(-5));

        assertEquals(0, PhoneUiKit.pointerFeelToSegment(PointerSmoother.FEEL_AUTO));
        assertEquals(1, PhoneUiKit.pointerFeelToSegment(PointerSmoother.FEEL_RAW_DIRECT));
        assertEquals(2, PhoneUiKit.pointerFeelToSegment(PointerSmoother.FEEL_SMOOTH));
        assertEquals(PointerSmoother.FEEL_AUTO, PhoneUiKit.segmentToPointerFeel(0));
        assertEquals(PointerSmoother.FEEL_RAW_DIRECT, PhoneUiKit.segmentToPointerFeel(1));
        assertEquals(PointerSmoother.FEEL_SMOOTH, PhoneUiKit.segmentToPointerFeel(2));
        assertEquals(PointerSmoother.FEEL_AUTO, PhoneUiKit.segmentToPointerFeel(7));
    }

    @Test
    public void forwardFastUdpAndNotificationCountsWorkSafely() {
        assertEquals(15, FastUdpChannel.OP_FORWARD_BUTTON);
        assertEquals(16, FastUdpChannel.OP_FORWARD_WHEEL);
        assertEquals(17, FastUdpChannel.OP_FORWARD_KEY);
        assertTrue(AdaptiveLinkTracker.isInteractiveOp(FastUdpChannel.OP_FORWARD_BUTTON));
        assertTrue(AdaptiveLinkTracker.isInteractiveOp(FastUdpChannel.OP_FORWARD_WHEEL));
        assertTrue(AdaptiveLinkTracker.isInteractiveOp(FastUdpChannel.OP_FORWARD_KEY));

        DeskTouchSession.EnvironmentTarget targetWithNotifs = new DeskTouchSession.EnvironmentTarget(
            2, "DESKTOP-IQ1KSJ9", "Windows", "1920x1080", true, 5);
        DeskTouchSession.EnvironmentTarget targetDefault = new DeskTouchSession.EnvironmentTarget(
            3, "SURAJ", "Windows", "1920x1080", true);
        assertEquals(5, targetWithNotifs.notificationCount);
        assertEquals(-1, targetDefault.notificationCount);

        assertFalse(DeskTouchNotificationListener.isPermissionGranted(null));
        assertEquals(-1, DeskTouchNotificationListener.readActiveCount(null));
        DeskTouchNotificationListener.openAppInfo(null);
        DeskTouchNotificationListener.openSettings(null);
        assertNotNull(DeskTouchRuntime.connectedNotificationSummary(null));
    }

    @Test
    public void navTabsLinkedDisplayFormatAndPairingCodeHelpersWorkSafely() {
        assertEquals(0, PhoneUiKit.TAB_HOME);
        assertEquals(1, PhoneUiKit.TAB_FIND);
        assertEquals(2, PhoneUiKit.TAB_TRACKPAD);
        assertEquals(3, PhoneUiKit.TAB_SPEED);
        assertEquals(4, PhoneUiKit.TAB_SETUP);

        assertEquals("23", LinkedDisplayHelper.formatNotificationCount(23));
        assertEquals("0", LinkedDisplayHelper.formatNotificationCount(0));
        assertEquals("—", LinkedDisplayHelper.formatNotificationCount(-1));

        String hubLayout = "[{\"Name\":\"\\\\\\\\.\\\\DISPLAY1\",\"X\":0,\"Y\":0,\"Width\":1920,\"Height\":1080,\"Primary\":true}]";
        DeskTouchSession.EnvironmentTarget hub = new DeskTouchSession.EnvironmentTarget(
            0, "DESKTOP-IQ1KSJ9", "Windows", hubLayout, true, 23);
        LinkedDisplayHelper.DisplayTileModel hubTile = LinkedDisplayHelper.parseTileModel(hub, 0, 1);
        assertTrue(hubTile.hub);
        assertFalse(hubTile.self);
        assertFalse(hubTile.portrait);
        assertTrue(hubTile.selected);
        assertTrue(hubTile.selectable);
        assertEquals("Display 1", hubTile.displayLabel);
        assertEquals("1920 × 1080", hubTile.resolutionSummary);
        assertEquals(23, hubTile.notificationCount);

        String multiLayout = "[{\"Name\":\"\\\\\\\\.\\\\DISPLAY1\",\"Width\":1920,\"Height\":1080,\"Primary\":true},"
            + "{\"Name\":\"\\\\\\\\.\\\\DISPLAY2\",\"Width\":1280,\"Height\":1024,\"Primary\":false}]";
        DeskTouchSession.EnvironmentTarget peer = new DeskTouchSession.EnvironmentTarget(
            2, "SURAJ-WORKSTATION", "Windows", multiLayout, true, 3);
        LinkedDisplayHelper.DisplayTileModel peerTile = LinkedDisplayHelper.parseTileModel(peer, 2, 1);
        assertFalse(peerTile.hub);
        assertEquals(2, peerTile.monitorCount);
        assertEquals("2 Displays", peerTile.displayLabel);
        assertEquals("2 screens • 1920 × 1080", peerTile.resolutionSummary);

        String phoneLayout = "[{\"Name\":\"Android screen\",\"Width\":1080,\"Height\":2400,\"Primary\":true}]";
        DeskTouchSession.EnvironmentTarget phone = new DeskTouchSession.EnvironmentTarget(
            1, "Xiaomi 22041216I", "Android", phoneLayout, true, -1);
        LinkedDisplayHelper.DisplayTileModel phoneTile = LinkedDisplayHelper.parseTileModel(phone, 0, 1);
        assertTrue(phoneTile.self);
        assertTrue(phoneTile.portrait);
        assertFalse(phoneTile.selectable);
        assertEquals("Android · 1", phoneTile.displayLabel);
        assertEquals("1080 × 2400", phoneTile.resolutionSummary);

        assertEquals("AABBCCDDEE11", LinkedDisplayHelper.normalizePairingCode("aa:bb cc-dd.ee 11"));
        assertEquals("", LinkedDisplayHelper.normalizePairingCode("abc"));
        assertEquals("", LinkedDisplayHelper.normalizePairingCode("ZZZZZZZZ"));

        String fpHub = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        String fpMember = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        DesktopDiscovery.Device dHub = new DesktopDiscovery.Device("DESKTOP-IQ1KSJ9", "192.168.1.10", fpHub, true, false, false);
        dHub.isHub = true;
        dHub.hubMemberCount = 2;
        DesktopDiscovery.Device dMember = new DesktopDiscovery.Device("SURAJ", "192.168.1.11", fpMember, true, false, false);
        java.util.List<DesktopDiscovery.Device> found = java.util.Arrays.asList(dHub, dMember);
        DesktopDiscovery.applyConnectedGroups(found, java.util.Collections.singletonList(new String[]{fpHub, fpMember}));

        DesktopDiscovery.Device matched = LinkedDisplayHelper.findDeviceByPairingCode(found, "bbbbbbbb");
        assertNotNull(matched);
        assertEquals(fpMember, matched.fingerprint);
        DesktopDiscovery.Device gateway = LinkedDisplayHelper.resolveGatewayForDevice(
            found, matched, java.util.Collections.emptySet(), java.util.Collections.emptySet());
        assertNotNull(gateway);
        assertEquals(fpHub, gateway.fingerprint);

        // Verify v0.3.76 spatial Linked Displays sorting, Trackpad PC taskbar filtering, and custom pairing tags.
        DeskTouchSession.EnvironmentTarget hubTarget = new DeskTouchSession.EnvironmentTarget(
            0, "DESKTOP-IQ1KSJ9", "Windows", hubLayout, true, 3, 1, 0.32, 0.10, 0.45, 0.40);
        DeskTouchSession.EnvironmentTarget leftTarget = new DeskTouchSession.EnvironmentTarget(
            2, "SURAJ", "Windows", multiLayout, true, 3, 0, 0.0, 0.10, 0.30, 0.40);
        DeskTouchSession.EnvironmentTarget phoneTarget = new DeskTouchSession.EnvironmentTarget(
            3, "Xiaomi 22041216I", "Android", phoneLayout, true, -1, 2, 0.80, 0.0, 0.18, 0.85);
        java.util.List<DeskTouchSession.EnvironmentTarget> unsorted =
            java.util.Arrays.asList(hubTarget, leftTarget, phoneTarget);
        java.util.List<DeskTouchSession.EnvironmentTarget> sorted =
            LinkedDisplayHelper.sortTargetsSpatially(unsorted);
        assertEquals(2, sorted.get(0).id);
        assertEquals(0, sorted.get(1).id);
        assertEquals(3, sorted.get(2).id);

        java.util.List<java.util.List<DeskTouchSession.EnvironmentTarget>> rows =
            LinkedDisplayHelper.groupTargetsIntoRows(sorted);
        assertEquals(1, rows.size());
        assertEquals(3, rows.get(0).size());

        DeskTouchSession.EnvironmentTarget topPc = new DeskTouchSession.EnvironmentTarget(
            2, "SURAJ", "Windows", multiLayout, true, 1, 0, 0.0, 0.0, 0.45, 0.35);
        DeskTouchSession.EnvironmentTarget bottomHub = new DeskTouchSession.EnvironmentTarget(
            0, "DESKTOP-IQ1KSJ9", "Windows", hubLayout, true, 2, 1, 0.0, 0.55, 0.45, 0.35);
        java.util.List<java.util.List<DeskTouchSession.EnvironmentTarget>> stackedRows =
            LinkedDisplayHelper.groupTargetsIntoRows(
                LinkedDisplayHelper.sortTargetsSpatially(java.util.Arrays.asList(bottomHub, topPc)));
        assertEquals(2, stackedRows.size());
        assertEquals(2, stackedRows.get(0).get(0).id);
        assertEquals(0, stackedRows.get(1).get(0).id);

        java.util.List<LinkedDisplayHelper.DisplayTileModel> trackpadPcs =
            LinkedDisplayHelper.buildTrackpadPcTiles(sorted, 0, 3);
        assertEquals(2, trackpadPcs.size());
        assertEquals(2, trackpadPcs.get(0).id);
        assertEquals(0, trackpadPcs.get(1).id);

        assertEquals("SURAJ-99", LinkedDisplayHelper.normalizeCustomPairingCode(" suraj-99 "));
        assertEquals("", LinkedDisplayHelper.normalizeCustomPairingCode("bad|code"));
        String expectedTag = LinkedDisplayHelper.computePairingTag(fpMember, "SURAJ-99");
        assertEquals(24, expectedTag.length());
        String[] parsedHint = DesktopDiscovery.parsePairTagHint("DT1-PAIR|" + fpMember + "|" + expectedTag);
        assertNotNull(parsedHint);
        dMember.pairingTag = parsedHint[1];
        DesktopDiscovery.Device customMatched = LinkedDisplayHelper.findDeviceByPairingCode(found, "suraj-99");
        assertNotNull(customMatched);
        assertEquals(fpMember, customMatched.fingerprint);

        // Verify v0.3.80 Trackpad Windows Taskbar + PC/Screen selector & in-app onResume update check.
        String mainSource = readProjectSource("src/main/java/com/desktouch/vision/MainActivity.java");
        assertTrue(mainSource.contains("trackpadScreenScroll.setVisibility("));
        assertTrue(mainSource.replace("\r\n", "\n").contains("if(index==PhoneUiKit.TAB_TRACKPAD){\n            refreshTrackpadPcTaskbar();"));
        assertTrue(mainSource.contains("AndroidUpdater.checkInAppOnResume(this);"));
        assertTrue(mainSource.contains("private void refreshWindowsTaskbar(){"));
        assertTrue(mainSource.contains("createWindowsTaskbarAppButton("));
        assertTrue(mainSource.contains("active.sendTaskbarAction(targetId,\"__start__\");"));
        assertTrue(mainSource.contains("active.sendTaskbarAction(targetId,\"__search__\");"));

        String sampleTaskbarJson = "{\"targetId\":0,\"items\":["
            + "{\"k\":\"w101\",\"n\":\"Google Chrome\",\"p\":\"Chrome\",\"a\":true,\"m\":false,\"r\":true,\"h\":\"A1B2C3D4\"},"
            + "{\"k\":\"p2\",\"n\":\"WhatsApp\",\"p\":\"WhatsApp\",\"a\":false,\"m\":false,\"r\":false,\"h\":\"E5F60708\"}"
            + "]}";
        java.util.List<DeskTouchSession.TaskbarItem> parsedTaskbar =
            DeskTouchSession.parseTaskbarItems(sampleTaskbarJson);
        assertEquals(2, parsedTaskbar.size());
        assertEquals("w101", parsedTaskbar.get(0).key);
        assertEquals("Chrome", parsedTaskbar.get(0).app);
        assertTrue(parsedTaskbar.get(0).active);
        assertTrue(parsedTaskbar.get(0).running);
        assertEquals("p2", parsedTaskbar.get(1).key);
        assertFalse(parsedTaskbar.get(1).running);

        String updaterSource = readProjectSource("src/main/java/com/desktouch/vision/AndroidUpdater.java");
        assertTrue(updaterSource.contains("static void checkInAppOnResume(Activity activity)"));
        assertTrue(updaterSource.contains("promptInAppUpdate("));

        String sessionSource = readProjectSource("src/main/java/com/desktouch/vision/DeskTouchSession.java");
        assertTrue(sessionSource.contains("void switchTargetAndClaim(int id, int entryDirection){"));
        assertTrue(sessionSource.contains("TASKBAR_ICON_CACHE"));
        assertTrue(sessionSource.contains("new Packet(\"taskbar-need-icons\",reqTarget)"));
        assertTrue(mainSource.contains("active.switchTargetAndClaim(pc.id,0);"));

        // Verify v0.3.82 Ecosystem helpers: OTP extraction, notification serialization, chunk assembly,
        // battery telemetry propagation, live preview watch, 3-finger & pinch gestures, and media/profile controls.
        assertEquals("819204", EcosystemFeatures.extractVerificationCode("Login", "Use OTP 819204 to verify"));
        assertEquals("", EcosystemFeatures.extractVerificationCode("Summary", "Order total 1250 items"));
        assertEquals("report_2026_.pdf", EcosystemFeatures.sanitizeFileName("../dir/report:2026?.pdf"));

        byte[] previewBytes = "DeskTouchPreviewFrameBytes".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        String p1 = java.util.Base64.getEncoder().encodeToString(java.util.Arrays.copyOfRange(previewBytes, 0, 10));
        String p2 = java.util.Base64.getEncoder().encodeToString(java.util.Arrays.copyOfRange(previewBytes, 10, previewBytes.length));
        EcosystemFeatures.ChunkAssembler assembler = new EcosystemFeatures.ChunkAssembler();
        assertNull(assembler.addChunk(7, 0, 2, 320, 180, p1, "tag"));
        EcosystemFeatures.ChunkAssembler.Result assembled = assembler.addChunk(7, 1, 2, 320, 180, p2, "tag");
        assertNotNull(assembled);
        assertArrayEquals(previewBytes, assembled.data);
        assertEquals(320, assembled.width);

        java.util.List<EcosystemFeatures.NotificationItem> sampleNotifs = java.util.Collections.singletonList(
            new EcosystemFeatures.NotificationItem("k1", "Messages", "OTP", "Code 442211", 1000L, true, "442211"));
        String notifsJson = EcosystemFeatures.serializeNotificationList(sampleNotifs);
        java.util.List<EcosystemFeatures.NotificationItem> parsedNotifs = EcosystemFeatures.parseNotificationList(notifsJson);
        assertEquals(1, parsedNotifs.size());
        assertEquals("k1", parsedNotifs.get(0).key);
        assertTrue(parsedNotifs.get(0).canReply);
        assertEquals("442211", parsedNotifs.get(0).otpCode);

        EcosystemFeatures.ProfileSummary parsedProf = EcosystemFeatures.parseProfileState(
            "{\"activeProfile\":\"Presentation\",\"clipboardSync\":false,\"notifPreviews\":false,\"otpHelper\":true,\"shortcutPreset\":\"Presentation\",\"profiles\":[\"Work\",\"Presentation\",\"Away\"]}");
        assertEquals("Presentation", parsedProf.activeProfile);
        assertFalse(parsedProf.clipboardSync);
        assertEquals(3, parsedProf.profiles.size());
        assertFalse(EcosystemFeatures.shortcutsForPresetOrCustom(null, "Presentation").isEmpty());

        DeskTouchSession.EnvironmentTarget battTarget = new DeskTouchSession.EnvironmentTarget(
            4, "Phone", "Android", "[]", false, 2, 0, 0.8, 0.2, 0.1, 0.3, 84, true);
        LinkedDisplayHelper.DisplayTileModel battTile = LinkedDisplayHelper.parseTileModel(battTarget, 0, 4);
        assertEquals(84, battTile.batteryPercent);
        assertTrue(battTile.charging);

        assertTrue(mainSource.contains("setScreenPreviewWatch("));
        assertTrue(mainSource.contains("showNotificationsDialog("));
        assertTrue(mainSource.contains("handleShareIntent("));
        assertTrue(mainSource.contains("switch-window"));
        assertTrue(mainSource.contains("task-view"));
        assertTrue(mainSource.contains("REQ_SEND_FILE"));
        assertTrue(sessionSource.contains("screen-preview-chunk"));
        assertTrue(sessionSource.contains("sendFileUriAsync"));

        // Verify v0.3.83 FIT_CENTER letterbox touch mapping & Full Screen Preview support.
        float[] centerMapped = EcosystemFeatures.mapTouchToBitmapNormalized(500f, 500f, 1000, 1000, 1920, 1080);
        assertEquals(0.5f, centerMapped[0], 0.01f);
        assertEquals(0.5f, centerMapped[1], 0.01f);
        float[] topBarClamped = EcosystemFeatures.mapTouchToBitmapNormalized(500f, 10f, 1000, 1000, 1920, 1080);
        assertEquals(0.5f, topBarClamped[0], 0.01f);
        assertEquals(0.0f, topBarClamped[1], 0.001f);
        float[] bottomBarClamped = EcosystemFeatures.mapTouchToBitmapNormalized(500f, 990f, 1000, 1000, 1920, 1080);
        assertEquals(1.0f, bottomBarClamped[1], 0.001f);
        assertTrue(mainSource.contains("openFullscreenScreenPreview("));
        assertTrue(mainSource.contains("SCREEN_ORIENTATION_SENSOR_LANDSCAPE"));
        assertTrue(sessionSource.contains("void setScreenPreviewWatch(int targetId, int monitorIndex, boolean highRes)"));

        // Verify v0.3.85: QR direct payload parsing, Phone Screen/Camera streaming, Shelf, & Repair controls.
        String sampleFp = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        String sampleSecret = "A1B2C3D4E5F60718293A4B5C6D7E8F90";
        QrScannerDialog.DirectQrTarget qr = QrScannerDialog.parseDirectQrPayload(
            "DT1|192.168.1.42|" + sampleFp.toLowerCase(java.util.Locale.ROOT) + "|" + sampleSecret);
        assertNotNull(qr);
        assertEquals("192.168.1.42", qr.address);
        assertEquals(sampleFp, qr.fingerprint);
        assertEquals(sampleSecret, qr.secret);
        assertNull(QrScannerDialog.parseDirectQrPayload("DT1|not-an-ip|" + sampleFp + "|" + sampleSecret));
        assertNull(QrScannerDialog.parseDirectQrPayload("DT1|192.168.1.42|short|" + sampleSecret));
        assertNull(QrScannerDialog.parseDirectQrPayload("RANDOM"));
        assertTrue(mainSource.contains("launchQrScanner("));
        assertTrue(mainSource.contains("showSharedShelfDialog("));
        assertTrue(mainSource.contains("runFixConnectionAndBackground("));
        assertTrue(sessionSource.contains("connectWithDirectSecret("));
        assertTrue(sessionSource.contains("phone-screen-watch"));
        assertTrue(sessionSource.contains("phone-camera-watch"));
        assertTrue(sessionSource.contains("phone-mic-audio"));
    }

    @Test
    public void virtualBridgeAutoRecognitionAndPairingWorkSafely() throws Exception {
        String mainSource = readProjectSource("src/main/java/com/desktouch/vision/MainActivity.java");
        String sessionSource = readProjectSource("src/main/java/com/desktouch/vision/DeskTouchSession.java");
        String shellClientSource = readProjectSource("src/main/java/com/desktouch/vision/ShellInputClient.java");

        String hintJson = VirtualBridgeHelper.encodeHintJson(true, true, 41234, 37890, true, false);
        org.json.JSONObject bridgeHintObj = new org.json.JSONObject(hintJson);
        assertTrue(bridgeHintObj.getBoolean("usb"));
        assertTrue(bridgeHintObj.getBoolean("wifi"));
        assertEquals(41234, bridgeHintObj.getInt("connectPort"));
        assertEquals(37890, bridgeHintObj.getInt("pairPort"));
        assertTrue(bridgeHintObj.getBoolean("allowed"));
        assertFalse(bridgeHintObj.getBoolean("ready"));

        VirtualBridgeHelper.clearRemoteStatus();
        VirtualBridgeHelper.updateRemoteStatus(37891, 41235, "needs_wireless_pair|Pair once");
        assertEquals("needs_wireless_pair", VirtualBridgeHelper.latestStatus().state);
        assertEquals("Pair once", VirtualBridgeHelper.latestStatus().detail);
        assertEquals(37891, VirtualBridgeHelper.bestPairPortHint());
        assertEquals(41235, VirtualBridgeHelper.bestConnectPortHint());
        VirtualBridgeHelper.clearRemoteStatus();
        assertEquals("debug_off", VirtualBridgeHelper.latestStatus().state);

        assertTrue(shellClientSource.contains("scheduleRetryIfValid("));
        assertTrue(shellClientSource.contains("DeskTouchRuntime.requestShellBridgeSync(app)"));
        assertTrue(shellClientSource.contains("if (\"ERR\".equals(ack))"));
        assertTrue(sessionSource.contains("bridge-status"));
        assertTrue(sessionSource.contains("adb-pair-result"));
        assertTrue(sessionSource.contains("sendAdbBridgeHint("));
        assertTrue(mainSource.contains("checkVirtualBridgeFromUi("));
        assertTrue(mainSource.contains("showPairWirelessBridgeDialog("));
        assertTrue(mainSource.contains("setLiveStatusListener") && mainSource.contains("refreshPermissionState()"));
    }

    @Test
    public void universalKeyboardAndNativeImeWorkSafely() throws Exception {
        String daemonSource = readProjectSource("src/main/java/com/desktouch/vision/ShellInputDaemon.java");
        String shellClientSource = readProjectSource("src/main/java/com/desktouch/vision/ShellInputClient.java");
        String imeSource = readProjectSource("src/main/java/com/desktouch/vision/DeskTouchInputMethod.java");
        String sessionSource = readProjectSource("src/main/java/com/desktouch/vision/DeskTouchSession.java");
        String mainSource = readProjectSource("src/main/java/com/desktouch/vision/MainActivity.java");

        assertEquals(android.view.KeyEvent.KEYCODE_A, RemoteImeKeys.fullAndroidKeyCode('A'));
        assertEquals(android.view.KeyEvent.KEYCODE_5, RemoteImeKeys.fullAndroidKeyCode('5'));
        assertEquals(android.view.KeyEvent.KEYCODE_SPACE, RemoteImeKeys.fullAndroidKeyCode(0x20));
        assertEquals(android.view.KeyEvent.KEYCODE_ENTER, RemoteImeKeys.fullAndroidKeyCode(13));
        assertEquals(android.view.KeyEvent.KEYCODE_SEMICOLON, RemoteImeKeys.fullAndroidKeyCode(0xBA));
        assertEquals(android.view.KeyEvent.KEYCODE_UNKNOWN, RemoteImeKeys.androidKeyCode('A'));

        int expectedCtrlShift = android.view.KeyEvent.META_CTRL_ON
            | android.view.KeyEvent.META_CTRL_LEFT_ON
            | android.view.KeyEvent.META_SHIFT_ON
            | android.view.KeyEvent.META_SHIFT_RIGHT_ON;
        assertEquals(expectedCtrlShift, RemoteImeKeys.metaStateFromHidModifiers(
            HidKeyTable.MOD_LEFT_CTRL | HidKeyTable.MOD_RIGHT_SHIFT));
        assertEquals(0, RemoteImeKeys.metaStateFromHidModifiers(0));

        assertTrue(daemonSource.contains("(byte) 0x2A, (byte) 0xE7, (byte) 0x00"));
        assertTrue(daemonSource.contains("(byte) 0x2A, (byte) 0xFF, (byte) 0x00"));
        assertTrue(daemonSource.contains("\"desktouch-mouse\"") && daemonSource.contains("\"desktouch-keyboard\""));
        assertTrue(daemonSource.contains("scheduleDelayedVelocityProbe(160L)"));
        assertTrue(daemonSource.contains("uhidKeyboardHealthy = false"));
        assertTrue(daemonSource.contains("injectInputEventDirect("));
        assertTrue(daemonSource.contains("ensureSoftImeVisibleWhileConnectedAsync()"));
        assertTrue(daemonSource.contains("restoreSoftImeVisibilityIfApplied()"));

        assertTrue(imeSource.contains("ShellInputClient.ready(this) || DeskTouchAccessibilityService.ready(this)"));
        assertTrue(imeSource.contains("getEnabledInputMethodList()"));
        assertTrue(imeSource.contains("preferredNativeImeLabel("));
        assertTrue(shellClientSource.contains("void destroyDevices()"));
        assertTrue(shellClientSource.contains("sendAndReadLine(\"DESTROY\")"));
        assertTrue(sessionSource.contains("ShellInputClient.destroyDevices()"));
        assertTrue(sessionSource.contains("DeskTouchInputMethod.commitRemoteText("));
        assertTrue(mainSource.contains("openSoftKeyboardForField("));
        assertTrue(mainSource.contains("InputMethodManager.SHOW_FORCED"));
        assertTrue(mainSource.contains("Native phone keyboard active ("));
    }

    @Test
    public void fastUdpMediaAndStandaloneMicProtocolWorkSafely() throws Exception {
        byte[] key = new byte[FastUdpChannel.KEY_SIZE];
        new java.security.SecureRandom().nextBytes(key);
        FastUdpChannel.Codec server = new FastUdpChannel.Codec(key, 9, true);
        FastUdpChannel.Codec client = new FastUdpChannel.Codec(key, 9, false);

        byte[] pcm = new byte[640];
        for (int i = 0; i < pcm.length; i++) pcm[i] = (byte) ((i * 19 + 5) & 0xFF);
        byte[] wire = new byte[FastUdpChannel.MAX_MEDIA_FRAME_SIZE];
        int micLen = client.sealMedia(FastUdpChannel.MEDIA_KIND_MIC_AUDIO, 21, 16000, 1, 0, pcm, 0, pcm.length, wire);
        assertEquals(FastUdpChannel.MEDIA_MIN_FRAME_SIZE + pcm.length, micLen);

        FastUdpChannel.MediaDatagram openedMic = server.tryOpenMedia(wire, micLen);
        assertNotNull(openedMic);
        assertEquals(FastUdpChannel.MEDIA_KIND_MIC_AUDIO, openedMic.kind);
        assertEquals(21, openedMic.frameOrAudioId);
        assertEquals(16000, openedMic.indexOrSampleRate);
        assertArrayEquals(pcm, openedMic.payload);

        // Replay & tamper rejection
        assertNull(server.tryOpenMedia(wire, micLen));
        byte[] tampered = java.util.Arrays.copyOf(wire, micLen);
        tampered[micLen - 1] ^= 0x3C;
        assertNull(server.tryOpenMedia(tampered, tampered.length));

        // Server -> client PC Screen Preview + independent DTU1 input sequence
        byte[] jpegPart = new byte[] { (byte) 0xFF, (byte) 0xD8, 0x12, 0x34, 0x56, 0x78 };
        int prevLen = server.sealMedia(FastUdpChannel.MEDIA_KIND_PC_SCREEN_PREVIEW_CHUNK, 77, 0, 1, (560 << 16) | 315, jpegPart, 0, jpegPart.length, wire);
        FastUdpChannel.MediaDatagram openedPrev = client.tryOpenMedia(wire, prevLen);
        assertNotNull(openedPrev);
        assertEquals(FastUdpChannel.MEDIA_KIND_PC_SCREEN_PREVIEW_CHUNK, openedPrev.kind);
        assertEquals((560 << 16) | 315, openedPrev.metaOrRouteId);
        assertArrayEquals(jpegPart, openedPrev.payload);

        byte[] dtuFrame = new byte[FastUdpChannel.FRAME_SIZE];
        client.seal(FastUdpChannel.OP_MOVE, 321, 654, 1, 55, dtuFrame);
        FastUdpChannel.Message dtuMsg = server.tryOpen(dtuFrame, dtuFrame.length);
        assertNotNull(dtuMsg);
        assertEquals(1L, dtuMsg.seq);
        assertEquals(321, dtuMsg.a);

        EcosystemFeatures.ChunkAssembler assembler = new EcosystemFeatures.ChunkAssembler();
        assertNull(assembler.addRawChunk(88, 2, 3, 560, 315, new byte[] { 6, 7 }));
        assertNull(assembler.addRawChunk(88, 0, 3, 560, 315, new byte[] { 1, 2, 3 }));
        EcosystemFeatures.ChunkAssembler.Result joined = assembler.addRawChunk(88, 1, 3, 560, 315, new byte[] { 4, 5 });
        assertNotNull(joined);
        assertArrayEquals(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, joined.data);

        String mediaServiceSource = readProjectSource("src/main/java/com/desktouch/vision/PhoneMediaService.java");
        String sessionSource = readProjectSource("src/main/java/com/desktouch/vision/DeskTouchSession.java");
        String mainSource = readProjectSource("src/main/java/com/desktouch/vision/MainActivity.java");
        assertTrue(mediaServiceSource.contains("ACTION_START_MIC"));
        assertTrue(mediaServiceSource.contains("VOICE_COMMUNICATION"));
        assertTrue(mediaServiceSource.contains("AcousticEchoCanceler"));
        assertTrue(mediaServiceSource.contains("SecurityException"));
        assertTrue(sessionSource.contains("sendMedia("));
        assertTrue(sessionSource.contains("\"phone-mic-watch\""));
        assertTrue(mainSource.contains("Share Mic Only"));
        assertTrue(mainSource.contains("onMicShareRequested()"));
    }

    private static String readProjectSource(String relFromAppModule) {
        try {
            String[] prefixes = {"", "app/", "AndroidDeskTouch/app/"};
            for (String prefix : prefixes) {
                java.nio.file.Path candidate = java.nio.file.Paths.get(prefix + relFromAppModule);
                if (java.nio.file.Files.isRegularFile(candidate)) {
                    return new String(java.nio.file.Files.readAllBytes(candidate), java.nio.charset.StandardCharsets.UTF_8);
                }
            }
            throw new java.io.FileNotFoundException("Cannot locate " + relFromAppModule + " from " + java.nio.file.Paths.get("").toAbsolutePath());
        } catch (java.io.IOException e) {
            throw new RuntimeException(e);
        }
    }
}
