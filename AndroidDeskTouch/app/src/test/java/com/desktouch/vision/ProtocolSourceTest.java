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
    public void cachedLeafDoesNotBypassHubAndWirelessMigratesToRealHub() {
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
    }
}
