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
}
