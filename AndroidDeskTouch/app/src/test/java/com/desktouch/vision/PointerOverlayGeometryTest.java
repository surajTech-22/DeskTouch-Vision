package com.desktouch.vision;

import org.junit.Test;
import static org.junit.Assert.*;

public final class PointerOverlayGeometryTest {
    @Test public void centerOfPhoneScreen(){
        assertEquals(514,PointerOverlayGeometry.origin(540,1080,52));
        assertEquals(1174,PointerOverlayGeometry.origin(1200,2400,52));
    }
    @Test public void phoneEdgesNeverMoveOverlayOutsideScreen(){
        assertEquals(0,PointerOverlayGeometry.origin(0,1080,52));
        assertEquals(0,PointerOverlayGeometry.origin(-200,1080,52));
        assertEquals(1028,PointerOverlayGeometry.origin(1079,1080,52));
        assertEquals(1028,PointerOverlayGeometry.origin(5000,1080,52));
    }
    @Test public void compactFallbackWorksOnSmallDisplays(){
        assertEquals(0,PointerOverlayGeometry.origin(2,38,52));
        assertEquals(0,PointerOverlayGeometry.origin(20,38,38));
        assertEquals(0,PointerOverlayGeometry.origin(0,0,52));
        assertEquals(0,PointerOverlayGeometry.origin(10,50,0));
    }
}
