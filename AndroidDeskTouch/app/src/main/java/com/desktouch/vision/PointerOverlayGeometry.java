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
    /**
     * Do not recenter the compact accessibility overlay on EVERY mouse frame:
     * each WindowManager relayout is a slow cross-process Binder operation.
     * Hold the overlay steady until the cursor approaches its edge, then
     * move only as far as needed to keep the pointer visible.
     */
    static int followOrigin(int pointer,int oldOrigin,int screenExtent,
                            int overlayExtent,int margin) {
        if(screenExtent<=0||overlayExtent<=0)return 0;
        int screen=Math.max(1,screenExtent);
        int view=Math.min(screen,overlayExtent);
        int maxOrigin=Math.max(0,screen-view);
        int current=Math.max(0,Math.min(maxOrigin,oldOrigin));
        int target=Math.max(0,Math.min(screen-1,pointer));
        int safe=Math.max(1,Math.min(Math.max(0,margin),Math.max(1,view/4)));
        if(target<current+safe)
            return Math.max(0,Math.min(maxOrigin,target-safe));
        if(target>current+view-1-safe)
            return Math.max(0,Math.min(maxOrigin,target-(view-1-safe)));
        return current;
    }

}
