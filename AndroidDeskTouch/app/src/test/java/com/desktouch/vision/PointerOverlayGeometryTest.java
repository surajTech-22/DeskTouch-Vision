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
    @Test public void compactPointerMovesWithoutRepositioningWindowEachFrame(){
        // A 240-pixel fallback overlay can draw dozens of cursor movements
        // without forcing a costly WindowManager.updateViewLayout Binder call.
        int origin=150;
        for(int px=190;px<345;px+=2)
            assertEquals(150,PointerOverlayGeometry.followOrigin(px,origin,1080,240,40));
        assertEquals(166,PointerOverlayGeometry.followOrigin(365,origin,1080,240,40));
        assertEquals(150,PointerOverlayGeometry.followOrigin(190,origin,1080,240,40));
    }
    @Test public void pointerOverlayClampsToRealScreenAndSurvivesRotation(){
        assertEquals(0,PointerOverlayGeometry.followOrigin(-500,100,1080,240,40));
        assertEquals(840,PointerOverlayGeometry.followOrigin(5000,100,1080,240,40));
        assertEquals(0,PointerOverlayGeometry.followOrigin(10,0,40,240,40));
        // Landscape dimensions are physical pixels, not reduced app metrics.
        assertEquals(1620,PointerOverlayGeometry.followOrigin(1920,400,1920,300,50));
    }
}

