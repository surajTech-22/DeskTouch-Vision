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
    @Test public void unavailableGatewayDoesNotBecomeTrusted() {
        DesktopDiscovery.Device busy=DesktopDiscovery.parse(
            "DT1-BUSY|Busy|"+"D".repeat(64),"192.168.1.4");
        java.util.List<DesktopDiscovery.Device> members=
            java.util.Collections.singletonList(busy);
        assertFalse(DesktopDiscovery.chooseGateway(members,
            java.util.Collections.emptySet(),
            java.util.Collections.emptySet()).ready);
    }
}
