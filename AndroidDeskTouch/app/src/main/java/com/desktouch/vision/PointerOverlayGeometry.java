package com.desktouch.vision;

/**
 * Screen-to-accessibility-overlay coordinate mapping. Pure arithmetic so
 * corner cases can be tested without asking Android for extra permissions.
 */
final class PointerOverlayGeometry {
    private PointerOverlayGeometry(){}
    static int origin(int requested,int screenExtent,int overlayExtent){
        if(screenExtent<=0||overlayExtent<=0)return 0;
        int screen=Math.max(1,screenExtent);
        int view=Math.min(screen,overlayExtent);
        int center=Math.max(0,Math.min(screen-1,requested));
        return Math.max(0,Math.min(screen-view,center-view/2));
    }
}
