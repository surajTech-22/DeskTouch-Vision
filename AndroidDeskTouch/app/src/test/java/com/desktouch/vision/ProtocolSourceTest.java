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
}
