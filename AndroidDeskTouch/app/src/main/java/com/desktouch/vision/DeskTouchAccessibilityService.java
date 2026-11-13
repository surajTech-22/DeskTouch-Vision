package com.desktouch.vision;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.accessibilityservice.InputMethod;
import android.text.InputType;
import android.view.inputmethod.EditorInfo;
import android.view.accessibility.AccessibilityWindowInfo;
import android.os.Build;
import android.content.Context;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.Choreographer;
import android.graphics.Rect;
import android.view.View;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.FrameLayout;
import java.util.ArrayDeque;
import java.util.List;

/**
 * Opt-in Windows -> Android control through Android's public Accessibility API.
 * A TLS connection alone NEVER enables remote gestures or reading editable text.
 * No root, hidden system input injection, or bypass of Android user approval.
 * Remote control is limited to gestures and non-password editable fields.
 */
public final class DeskTouchAccessibilityService extends AccessibilityService {
    private static volatile DeskTouchAccessibilityService instance;
    private static volatile Boolean cachedAllowed;
    private static final String PREFS="desktouch_android_control";
    private static final String ALLOWED="user_allowed_windows_control";
    private static final String PAUSED_FOR_BANKING="paused_for_banking";
    private final Handler ui=new Handler(Looper.getMainLooper());
    private WindowManager windows;
    private FrameLayout cursorRoot;
    private PointerDotView pointerDot;
    private int pointerDotSize;
    private float pointerDotHalf;
    private WindowManager.LayoutParams pointerParams;
    private WindowManager.LayoutParams compactPointerParams;
    private volatile int displayWidth=1,displayHeight=1;
    private volatile int x,y;
    private volatile int pendingX,pendingY;
    private volatile long pendingArrivalNanos;
    private final java.util.concurrent.atomic.AtomicInteger coalescedMoves=
        new java.util.concurrent.atomic.AtomicInteger();
    private long lastPerfReportNanos;
    private int renderFrames,maxLocalFrameDelayMs;
    private volatile Runnable pendingApplied;
    private final java.util.concurrent.atomic.AtomicBoolean moveScheduled =
        new java.util.concurrent.atomic.AtomicBoolean();
    private int lastOverlayX=Integer.MIN_VALUE,lastOverlayY=Integer.MIN_VALUE;
    private int downX,downY;
    private boolean held,shift,control,alt,remoteGesture;
    private boolean gestureRunning;
    private GestureDescription.StrokeDescription activeStroke;
    private int strokeX,strokeY;
    private boolean pendingLift;
    private int pendingLiftX,pendingLiftY;
    private boolean dragFallbackPending;
    private int gestureGeneration;
    private Choreographer choreographer;
    private final Choreographer.FrameCallback frameCallback = frameTimeNanos -> flushPointer();
    private final ArrayDeque<TouchAction> pendingActions=new ArrayDeque<>();
    private static final class TouchAction {
        static final int TYPE_GESTURE = 1;
        static final int TYPE_DOWN = 2;
        static final int TYPE_UP = 3;
        static final int TYPE_SCROLL = 4;
        final int kind;
        int sx,sy,ex,ey;
        long duration;
        boolean horizontal;
        TouchAction(int kind,int sx,int sy,int ex,int ey,long duration,boolean horizontal){
            this.kind=kind;this.sx=sx;this.sy=sy;this.ex=ex;this.ey=ey;
            this.duration=duration;this.horizontal=horizontal;
        }
    }
    private final class PointerDotView extends View {
        private final Paint fill=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint ring=new Paint(Paint.ANTI_ALIAS_FLAG);
        PointerDotView(Context context){
            super(context);
            setWillNotDraw(false);
            fill.setColor(0xff37b6f6);
            ring.setColor(0xffffffff);
            ring.setStyle(Paint.Style.STROKE);
            ring.setStrokeWidth(dp(2));
        }
        @Override protected void onDraw(Canvas canvas){
            super.onDraw(canvas);
            float cx=getWidth()/2f,cy=getHeight()/2f;
            float radius=dp(9);
            canvas.drawCircle(cx,cy,radius,fill);
            canvas.drawCircle(cx,cy,radius,ring);
        }
    }

    private volatile boolean remoteActive;

    static boolean allowed(Context context){
        Boolean cached=cachedAllowed;
        if(cached!=null)return cached;
        boolean value=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
            .getBoolean(ALLOWED,false);
        cachedAllowed=value;
        return value;
    }
    static boolean isPausedForBanking(Context context){
        return context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
            .getBoolean(PAUSED_FOR_BANKING,false);
    }
    static void setAllowed(Context context,boolean allowed){
        // Persist the user's app-level approval synchronously BEFORE the
        // package installer can kill this process. In-place signed upgrades
        // retain these preferences; an uninstall/data-clear cannot.
        android.content.SharedPreferences.Editor editor=
            context.getSharedPreferences(PREFS,Context.MODE_PRIVATE).edit()
                .putBoolean(ALLOWED,allowed);
        if(allowed)editor.putBoolean(PAUSED_FOR_BANKING,false);
        if(!editor.commit())
            throw new IllegalStateException("Cannot save Android control preference");
        cachedAllowed=allowed;
        if(!allowed){
            DeskTouchAccessibilityService service=instance;
            if(service!=null){
                service.remoteActive=false;
                service.ui.post(service::removePointer);
            }
            DeskTouchRuntime.remoteNativeTouch();
        }
    }
    static void pauseForBanking(Context context){
        if(!context.getSharedPreferences(PREFS,Context.MODE_PRIVATE).edit()
            .putBoolean(ALLOWED,false)
            .putBoolean(PAUSED_FOR_BANKING,true)
            .commit())
            throw new IllegalStateException("Cannot save banking pause preference");
        cachedAllowed=false;
        RemoteInputPower.end();
        DeskTouchAccessibilityService service=instance;
        if(service!=null){
            service.remoteActive=false;
            service.ui.post(()->{
                service.removePointer();
                service.disableSelf();
            });
        }
        DeskTouchRuntime.remoteNativeTouch();
        DeskTouchRuntime.accessibilityPermissionChanged();
    }
    static boolean ready(Context context){
        return instance!=null && allowed(context);
    }
    @Override public void onServiceConnected(){
        super.onServiceConnected();
        instance=this;
        cachedAllowed=null;
        fullOverlayRejected=false;
        try { choreographer=Choreographer.getInstance(); }
        catch(RuntimeException ignored) { choreographer=null; }
        if(isPausedForBanking(this))
            getSharedPreferences(PREFS,Context.MODE_PRIVATE).edit()
                .putBoolean(PAUSED_FOR_BANKING,false).apply();
        windows=(WindowManager)getSystemService(WINDOW_SERVICE);
        refreshDisplayGeometry();
        DeskTouchRuntime.accessibilityPermissionChanged();
    }
    private void refreshDisplayGeometry(){
        android.graphics.Point size=PhoneScreenGeometry.read(this);
        displayWidth=Math.max(1,size.x);displayHeight=Math.max(1,size.y);
    }
    @Override public void onConfigurationChanged(android.content.res.Configuration config){
        super.onConfigurationChanged(config);
        fullOverlayRejected=false;
        removePointer(); // Old overlay size is wrong after rotation.
        refreshDisplayGeometry();
        DeskTouchRuntime.publishPhoneGeometry();
    }
    @Override public void onAccessibilityEvent(AccessibilityEvent event){
        // Touch from the phone user takes priority over remote input.
        // Events generated by our own approved gestures are ignored.
        if(event!=null&&event.getEventType()==
            AccessibilityEvent.TYPE_TOUCH_INTERACTION_START && !remoteGesture){
            DeskTouchRuntime.remoteNativeTouch();
            hidePointer();
        }
    }
    @Override public void onInterrupt(){
        DeskTouchRuntime.remoteNativeTouch();
        removePointer();
    }
    @Override public void onDestroy(){
        if(instance==this)instance=null;
        if(choreographer!=null){
            try{choreographer.removeFrameCallback(frameCallback);}
            catch(Throwable ignored){}
            choreographer=null;
        }
        DeskTouchRuntime.accessibilityPermissionChanged();
        DeskTouchRuntime.remoteNativeTouch();
        removePointer();
        super.onDestroy();
    }
    private int dp(float px){return Math.round(px*getResources().getDisplayMetrics().density);}
    private volatile boolean fullOverlayRejected;
    private float maxRefreshRateHz;
    private float preferredActiveRefreshRate(){
        if(Build.VERSION.SDK_INT<23)return 0f;
        try{
            android.hardware.display.DisplayManager dm=
                (android.hardware.display.DisplayManager)getSystemService(DISPLAY_SERVICE);
            android.view.Display display=dm!=null
                ? dm.getDisplay(android.view.Display.DEFAULT_DISPLAY):null;
            if(display==null)return 0f;
            float best=0f;
            for(android.view.Display.Mode mode:display.getSupportedModes()){
                float hz=mode.getRefreshRate();
                if(hz>best)best=hz;
            }
            return best>=85f?best:0f;
        }catch(RuntimeException ignored){return 0f;}
    }
    private boolean tryAttachOverlay(FrameLayout root,WindowManager.LayoutParams params){
        try{
            windows.addView(root,params);
            cursorRoot=root;
            return true;
        }catch(RuntimeException rejected){
            return false;
        }
    }
    private WindowManager.LayoutParams overlayLayoutParams(int width,int height){
        WindowManager.LayoutParams params=new WindowManager.LayoutParams(
            width,height,WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE |
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE |
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN |
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS |
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED |
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
            PixelFormat.TRANSLUCENT);
        params.gravity=Gravity.TOP|Gravity.LEFT;
        if(Build.VERSION.SDK_INT>=28)
            params.layoutInDisplayCutoutMode=
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
        if(Build.VERSION.SDK_INT>=30)params.setFitInsetsTypes(0);
        return params;
    }
    private void showPointer(int requestedX,int requestedY){
        if(!ready(this))return;
        // Read the physical-display geometry once per rotation, not on every
        // mouse frame. This matches the topology advertised by TCP hello.
        int screenWidth=displayWidth,screenHeight=displayHeight;
        x=Math.max(0,Math.min(screenWidth-1,requestedX));
        y=Math.max(0,Math.min(screenHeight-1,requestedY));
        if(cursorRoot==null){
            FrameLayout root=new FrameLayout(this);
            root.setKeepScreenOn(true); // Active approved remote pointer window only.
            PointerDotView dot=new PointerDotView(this);
            dot.setLayerType(View.LAYER_TYPE_HARDWARE,null);
            pointerDotSize=dp(24);
            pointerDotHalf=pointerDotSize/2f;
            root.addView(dot,new FrameLayout.LayoutParams(
                pointerDotSize,pointerDotSize,Gravity.TOP|Gravity.LEFT));

            boolean attached=false;
            if(!fullOverlayRejected){
                WindowManager.LayoutParams full=overlayLayoutParams(screenWidth,screenHeight);
                if(tryAttachOverlay(root,full)){
                    pointerDot=dot;
                    pointerParams=full;
                    compactPointerParams=null;
                    attached=true;
                }else{
                    fullOverlayRejected=true;
                }
            }
            if(!attached){
                // Xiaomi/OEM policies may reject a full-screen overlay; fall back to
                // a medium or compact non-touchable accessibility overlay.
                WindowManager.LayoutParams compact=overlayLayoutParams(
                    Math.min(screenWidth,dp(240)),Math.min(screenHeight,dp(240)));
                compact.x=PointerOverlayGeometry.origin(x,screenWidth,compact.width);
                compact.y=PointerOverlayGeometry.origin(y,screenHeight,compact.height);
                if(!tryAttachOverlay(root,compact)){
                    compact=overlayLayoutParams(
                        Math.min(screenWidth,dp(96)),Math.min(screenHeight,dp(96)));
                    compact.x=PointerOverlayGeometry.origin(x,screenWidth,compact.width);
                    compact.y=PointerOverlayGeometry.origin(y,screenHeight,compact.height);
                    if(!tryAttachOverlay(root,compact)){
                        cursorRoot=null;
                        pointerDot=null;
                        pointerParams=null;
                        compactPointerParams=null;
                        lastOverlayX=lastOverlayY=Integer.MIN_VALUE;
                        Log.e("DeskTouchPointer",
                            "Android rejected pointer overlays; remote control revoked");
                        DeskTouchRuntime.pointerOverlayUnavailable();
                        return;
                    }
                }
                pointerDot=dot;
                pointerParams=compact;
                compactPointerParams=compact;
                Log.w("DeskTouchPointer","Full-screen pointer overlay unavailable; compact accessibility overlay active");
            }
        }
        if(cursorRoot==null||pointerDot==null)return;
        if(cursorRoot.getVisibility()!=View.VISIBLE)cursorRoot.setVisibility(View.VISIBLE);
        if(pointerDot.getVisibility()!=View.VISIBLE)pointerDot.setVisibility(View.VISIBLE);
        int localX=x,localY=y;
        boolean compactMoved=false;
        if(compactPointerParams!=null){
            WindowManager.LayoutParams compact=compactPointerParams;
            int left=PointerOverlayGeometry.followOrigin(x,compact.x,
                screenWidth,compact.width,compact.width/6);
            int top=PointerOverlayGeometry.followOrigin(y,compact.y,
                screenHeight,compact.height,compact.height/6);
            if(compact.x!=left || compact.y!=top){
                compact.x=left;compact.y=top;
                compactMoved=true;
            }
            localX=x-left;localY=y-top;
        }
        if(!applyKeepScreenOn(true,compactMoved)){
            Log.e("DeskTouchPointer",
                "Pointer overlay update rejected; remote control revoked");
            DeskTouchRuntime.pointerOverlayUnavailable();
            removePointer();
            return;
        }
        if(localX!=lastOverlayX || localY!=lastOverlayY){
            lastOverlayX=localX;lastOverlayY=localY;
            pointerDot.setTranslationX(localX-pointerDotHalf);
            pointerDot.setTranslationY(localY-pointerDotHalf);
        }
    }
    private void resetGestureState(){
        gestureGeneration++;
        GestureDescription.StrokeDescription dangling=activeStroke;
        activeStroke=null;
        held=false;
        shift=false;
        control=false;
        alt=false;
        pendingLift=false;
        dragFallbackPending=false;
        if(dangling!=null && windows!=null){
            try{
                Path lift=new Path();
                lift.moveTo(strokeX,strokeY);
                dispatchGesture(new GestureDescription.Builder()
                    .addStroke(dangling.continueStroke(lift,0,8L,false)).build(),
                    null,null);
            }catch(RuntimeException ignored){}
        }
        remoteActive=false;
        pendingApplied=null;
        moveScheduled.set(false);
        if(choreographer!=null){
            try{choreographer.removeFrameCallback(frameCallback);}
            catch(Throwable ignored){}
        }
        pendingActions.clear();
        gestureRunning=false;
        remoteGesture=false;
    }
    private boolean applyKeepScreenOn(boolean keepAwake,boolean forceLayout){
        if(cursorRoot==null)return false;
        if(cursorRoot.getKeepScreenOn()!=keepAwake)cursorRoot.setKeepScreenOn(keepAwake);
        WindowManager.LayoutParams lp=pointerParams;
        if(lp==null || windows==null)return true;
        int nextFlags=keepAwake
            ? (lp.flags | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            : (lp.flags & ~WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        boolean changed=nextFlags!=lp.flags;
        lp.flags=nextFlags;
        if(changed || forceLayout){
            try{windows.updateViewLayout(cursorRoot,lp);}
            catch(RuntimeException rejected){return false;}
        }
        return true;
    }
    private boolean pointerVisible(){
        return cursorRoot!=null && pointerDot!=null &&
            cursorRoot.getVisibility()==View.VISIBLE &&
            pointerDot.getVisibility()==View.VISIBLE;
    }
    private void hidePointer(){
        resetGestureState();
        if(pointerDot!=null && pointerDot.getVisibility()!=View.INVISIBLE)
            pointerDot.setVisibility(View.INVISIBLE);
        if(cursorRoot!=null && !applyKeepScreenOn(false,false))removePointer();
    }
    private void removePointer(){
        resetGestureState();
        lastOverlayX=lastOverlayY=Integer.MIN_VALUE;
        if(cursorRoot!=null&&windows!=null){
            try{windows.removeView(cursorRoot);}catch(RuntimeException ignored){}
        }
        cursorRoot=null;
        pointerDot=null;
        pointerParams=null;
        compactPointerParams=null;
    }
    private void schedulePointerFrame(){
        Choreographer vsync=choreographer;
        if(vsync!=null){
            try{
                vsync.postFrameCallback(frameCallback);
                return;
            }catch(RuntimeException ignored){}
        }
        ui.post(this::flushPointer);
    }

    static boolean pointer(int px,int py,Runnable applied){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service))return false;
        service.remoteActive=true;
        // Retain the newest position on fast mice/touchpads instead of
        // filling Android's UI queue with hundreds of obsolete moves.
        service.pendingX=px;
        service.pendingY=py;
        service.pendingArrivalNanos=SystemClock.elapsedRealtimeNanos();
        if(applied!=null)service.pendingApplied=applied;
        if(service.moveScheduled.compareAndSet(false,true)){
            if(!service.pointerVisible() || applied!=null)
                service.ui.post(service::flushPointer);
            else service.schedulePointerFrame();
        }
        else service.coalescedMoves.incrementAndGet();
        return true;
    }
    private void flushPointer(){
        int px=pendingX,py=pendingY;
        long arrival=pendingArrivalNanos;
        long now=SystemClock.elapsedRealtimeNanos();
        if(remoteActive&&ready(this))showPointer(px,py);
        if(remoteActive && arrival>0 && now>=arrival){
            int ageMs=(int)Math.min(5000,(now-arrival)/1_000_000L);
            maxLocalFrameDelayMs=Math.max(maxLocalFrameDelayMs,ageMs);
            renderFrames++;
            if(lastPerfReportNanos==0)lastPerfReportNanos=now;
            if(now-lastPerfReportNanos>=5_000_000_000L){
                // No coordinates, text, keys, device identities or IPs.
                Log.i("DeskTouchTiming","frames="+renderFrames+
                    " newest_moves_coalesced="+coalescedMoves.getAndSet(0)+
                    " worst_local_ui_delay_ms="+maxLocalFrameDelayMs);
                lastPerfReportNanos=now;
                maxLocalFrameDelayMs=renderFrames=0;
            }
        }
        Runnable ack=pendingApplied;
        pendingApplied=null;
        moveScheduled.set(false);
        if(remoteActive&&cursorRoot!=null&&ack!=null&&ready(this))ack.run();
        if(remoteActive&&ready(this)&&held&&activeStroke!=null&&!gestureRunning&&!pendingLift)
            continueDragIfReady();
        if(remoteActive&&ready(this)&&(px!=pendingX||py!=pendingY) &&
            moveScheduled.compareAndSet(false,true))
            schedulePointerFrame();
    }
    private void flushPointerBeforeAction(){
        // A click or scroll must operate at the most recently received
        // pointer position, never the previous animation frame.
        if(remoteActive && (pendingX!=x || pendingY!=y || !pointerVisible()))
            showPointer(pendingX,pendingY);
        Runnable ack=pendingApplied;
        pendingApplied=null;
        if(cursorRoot!=null && ack!=null && remoteActive)ack.run();
    }
    static void endRemote(){
        DeskTouchAccessibilityService service=instance;
        if(service!=null)service.ui.post(service::hidePointer);
    }
    static long gestureDurationMs(int dx,int dy){
        long distance=Math.round(Math.hypot(dx,dy));
        if(distance<=12)return 25L;
        return Math.min(420L,Math.max(90L,distance/2L));
    }
    static void mouseButton(int button,boolean pressed){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service))return;
        if(button!=1 && button!=2 && button!=3)return;
        service.ui.post(()->{
            if(!service.remoteActive || !ready(service))return;
            service.flushPointerBeforeAction();
            if(button==2){
                if(pressed)service.performGlobalAction(GLOBAL_ACTION_BACK);
                return;
            }
            if(button==3){
                if(!pressed)service.performGlobalAction(GLOBAL_ACTION_HOME);
                return;
            }
            if(pressed)service.handleLeftDown();
            else service.handleLeftUp();
        });
    }
    static final class WheelStroke {
        final int sx,sy,ex,ey;
        final long durationMs;
        WheelStroke(int sx,int sy,int ex,int ey,long durationMs){
            this.sx=sx;this.sy=sy;this.ex=ex;this.ey=ey;
            this.durationMs=durationMs;
        }
    }
    private Rect findClickableBoundsAt(int px,int py){
        int w=Math.max(100,displayWidth),h=Math.max(100,displayHeight);
        Rect match=findClickableInTree(getRootInActiveWindow(),px,py,w,h);
        if(match!=null)return match;
        List<AccessibilityWindowInfo> windowsList=getWindows();
        if(windowsList==null)return null;
        for(AccessibilityWindowInfo win:windowsList){
            if(win==null)continue;
            match=findClickableInTree(win.getRoot(),px,py,w,h);
            if(match!=null)return match;
        }
        return null;
    }
    private static Rect findClickableInTree(
        AccessibilityNodeInfo root,int px,int py,int screenWidth,int screenHeight)
    {
        if(root==null)return null;
        ArrayDeque<AccessibilityNodeInfo> nodes=new ArrayDeque<>();
        nodes.push(root);
        Rect best=null;
        long bestArea=Long.MAX_VALUE;
        int visited=0;
        Rect bounds=new Rect();
        while(!nodes.isEmpty() && ++visited<=120){
            AccessibilityNodeInfo node=nodes.pop();
            node.getBoundsInScreen(bounds);
            if(!bounds.contains(px,py))continue;
            if(node.isEnabled() && node.isClickable() && !node.isScrollable()){
                int bw=bounds.width(),bh=bounds.height();
                if(bw>0 && bh>0 && bw<screenWidth*0.55f && bh<screenHeight*0.45f){
                    long area=(long)bw*bh;
                    if(area<=bestArea){
                        bestArea=area;
                        best=new Rect(bounds);
                    }
                }
            }
            for(int i=node.getChildCount()-1;i>=0;i--){
                AccessibilityNodeInfo child=node.getChild(i);
                if(child!=null)nodes.push(child);
            }
        }
        return best;
    }
    static WheelStroke computeWheelStroke(int px,int py,int amount,boolean horizontal,
            int screenWidth,int screenHeight){
        return computeWheelStroke(px,py,amount,horizontal,screenWidth,screenHeight,0,0,0,0);
    }
    static WheelStroke computeWheelStroke(int px,int py,int amount,boolean horizontal,
            int screenWidth,int screenHeight,
            int iconLeft,int iconTop,int iconRight,int iconBottom){
        int w=Math.max(100,screenWidth);
        int h=Math.max(100,screenHeight);
        int axisSize=horizontal ? w : h;
        int minStep=Math.max(120,Math.round(axisSize*0.075f));
        int maxStep=Math.max(minStep,Math.round(axisSize*(horizontal?0.24f:0.18f)));
        int scaled=Math.round(Math.abs(amount)*(axisSize/1600f));
        int step=Math.max(minStep,Math.min(maxStep,scaled));
        // Restore 0.3.30 wheel direction; keep strokes crisp (<100ms) and long enough
        // to exit clickable icons beyond Android touch slop without triggering a tap.
        int signedDelta=amount>0 ? -step : step;
        int minX=Math.round(w*0.10f);
        int maxX=Math.round(w*0.90f);
        int minY=Math.round(h*0.14f);
        int maxY=Math.round(h*0.80f);
        if(maxX-minX<60){minX=0;maxX=w-1;}
        if(maxY-minY<60){minY=0;maxY=h-1;}
        int minBound=horizontal ? minX : minY;
        int maxBound=horizontal ? maxX : maxY;
        int start=Math.max(minBound,Math.min(maxBound,horizontal ? px : py));
        int end=start+signedDelta;
        if(end>maxBound){
            start=Math.max(minBound,maxBound-Math.abs(signedDelta));
            end=Math.min(maxBound,start+Math.abs(signedDelta));
        }else if(end<minBound){
            start=Math.min(maxBound,minBound+Math.abs(signedDelta));
            end=Math.max(minBound,start-Math.abs(signedDelta));
        }
        if(iconRight>iconLeft && iconBottom>iconTop){
            int escapeMargin=Math.max(84,Math.round(axisSize*0.035f));
            int maxEscape=Math.max(step,Math.round(axisSize*0.34f));
            int edgeLow=horizontal ? iconLeft : iconTop;
            int edgeHigh=horizontal ? iconRight : iconBottom;
            if(signedDelta<0){
                int clearEnd=edgeLow-escapeMargin;
                if(end>clearEnd){
                    if(clearEnd>=minBound && start-clearEnd<=maxEscape){
                        end=clearEnd;
                    }else if(start>=edgeLow && start<=edgeHigh && edgeHigh+escapeMargin<=maxBound){
                        start=edgeHigh+escapeMargin;
                        end=Math.max(minBound,start-step);
                    }else{
                        end=Math.max(minBound,Math.max(clearEnd,start-maxEscape));
                    }
                }
            }else{
                int clearEnd=edgeHigh+escapeMargin;
                if(end<clearEnd){
                    if(clearEnd<=maxBound && clearEnd-start<=maxEscape){
                        end=clearEnd;
                    }else if(start>=edgeLow && start<=edgeHigh && edgeLow-escapeMargin>=minBound){
                        start=edgeLow-escapeMargin;
                        end=Math.min(maxBound,start+step);
                    }else{
                        end=Math.min(maxBound,Math.min(clearEnd,start+maxEscape));
                    }
                }
            }
        }
        int sx=horizontal ? start : Math.max(minX,Math.min(maxX,px));
        int sy=horizontal ? Math.max(minY,Math.min(maxY,py)) : start;
        int ex=horizontal ? end : sx;
        int ey=horizontal ? sy : end;
        return new WheelStroke(sx,sy,ex,ey,45L);
    }
    private Rect lastWheelBounds;
    private long lastWheelBoundsAtMs;
    private int lastWheelBoundsX;
    private int lastWheelBoundsY;

    private Rect cachedWheelClickableBounds(int px,int py){
        long now=android.os.SystemClock.uptimeMillis();
        TouchAction tail=pendingActions.peekLast();
        boolean sameSpot=lastWheelBoundsAtMs!=0L &&
            Math.hypot(px-lastWheelBoundsX,py-lastWheelBoundsY)<=24.0;
        if(sameSpot && ((tail!=null && tail.kind==TouchAction.TYPE_SCROLL) ||
            now-lastWheelBoundsAtMs<=180L)){
            return lastWheelBounds!=null ? new Rect(lastWheelBounds) : null;
        }
        Rect found=findClickableBoundsAt(px,py);
        lastWheelBounds=found!=null ? new Rect(found) : null;
        lastWheelBoundsAtMs=now;
        lastWheelBoundsX=px;
        lastWheelBoundsY=py;
        return found;
    }

    static void wheel(int amount,boolean horizontal){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service)||amount==0)return;
        service.ui.post(()->{
            if(!service.remoteActive || !ready(service))return;
            service.flushPointerBeforeAction();
            Rect clickableBounds=service.cachedWheelClickableBounds(service.x,service.y);
            WheelStroke stroke=clickableBounds==null
                ? computeWheelStroke(service.x,service.y,amount,horizontal,
                    service.displayWidth,service.displayHeight)
                : computeWheelStroke(service.x,service.y,amount,horizontal,
                    service.displayWidth,service.displayHeight,
                    clickableBounds.left,clickableBounds.top,
                    clickableBounds.right,clickableBounds.bottom);
            int minTravel=Math.max(48,Math.round(
                Math.max(100,horizontal?service.displayWidth:service.displayHeight)*0.025f));
            if(Math.hypot(stroke.ex-stroke.sx,stroke.ey-stroke.sy)<minTravel)return;
            TouchAction tail=service.pendingActions.peekLast();
            if(tail!=null && tail.kind==TouchAction.TYPE_SCROLL && tail.horizontal==horizontal){
                if(horizontal){
                    int deltaX=stroke.ex-stroke.sx;
                    int w=Math.max(100,service.displayWidth);
                    int minX=Math.round(w*0.10f),maxX=Math.round(w*0.90f);
                    if(maxX-minX<60){minX=0;maxX=w-1;}
                    int combined=Math.max(-520,Math.min(520,(tail.ex-tail.sx)+deltaX));
                    tail.ex=Math.max(minX,Math.min(maxX,tail.sx+combined));
                    if(Math.abs(tail.ex-tail.sx)<minTravel)service.pendingActions.removeLast();
                }else{
                    int deltaY=stroke.ey-stroke.sy;
                    int h=Math.max(100,service.displayHeight);
                    int minY=Math.round(h*0.14f),maxY=Math.round(h*0.80f);
                    if(maxY-minY<60){minY=0;maxY=h-1;}
                    int combined=Math.max(-560,Math.min(560,(tail.ey-tail.sy)+deltaY));
                    tail.ey=Math.max(minY,Math.min(maxY,tail.sy+combined));
                    if(Math.abs(tail.ey-tail.sy)<minTravel)service.pendingActions.removeLast();
                }
                return;
            }
            int scrollCount=0;
            for(TouchAction a:service.pendingActions)
                if(a.kind==TouchAction.TYPE_SCROLL)scrollCount++;
            if(scrollCount>=2){
                java.util.Iterator<TouchAction> it=service.pendingActions.iterator();
                while(it.hasNext()){
                    if(it.next().kind==TouchAction.TYPE_SCROLL){it.remove();break;}
                }
            }
            service.enqueueAction(new TouchAction(
                TouchAction.TYPE_SCROLL,stroke.sx,stroke.sy,stroke.ex,stroke.ey,
                stroke.durationMs,horizontal));
        });
    }

    private boolean performNativeClick(){
        AccessibilityNodeInfo root=getRootInActiveWindow();
        if(root==null)return false;
        ArrayDeque<AccessibilityNodeInfo> nodes=new ArrayDeque<>();
        nodes.push(root);
        AccessibilityNodeInfo target=null,editor=null;
        int visited=0;
        Rect bounds=new Rect();
        while(!nodes.isEmpty() && ++visited<=96){
            AccessibilityNodeInfo node=nodes.pop();
            node.getBoundsInScreen(bounds);
            if(!bounds.contains(x,y))continue;
            if(node.isEditable() && node.isEnabled())editor=node;
            if(node.isClickable() && node.isEnabled())target=node;
            for(int i=node.getChildCount()-1;i>=0;i--){
                AccessibilityNodeInfo child=node.getChild(i);
                if(child!=null)nodes.push(child);
            }
        }
        if(editor!=null){
            boolean clicked=editor.performAction(AccessibilityNodeInfo.ACTION_CLICK);
            boolean focused=editor.isFocused() ||
                editor.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
            if(clicked||focused)return true;
        }
        if(target==null)return false;
        // Some WebView editors receive a click but do not start the input
        // connection until they are also marked as focused.
        if(target.isEditable())
            target.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
        return target.performAction(AccessibilityNodeInfo.ACTION_CLICK);
    }

    private void handleLeftDown(){
        if(held)return;
        held=true;downX=x;downY=y;
        dragFallbackPending=false;pendingLift=false;
        if(!gestureRunning && activeStroke==null && pendingActions.isEmpty()){
            startLiveStrokeDown(x,y);
            return;
        }
        enqueueAction(new TouchAction(TouchAction.TYPE_DOWN,x,y,x,y,8L,false));
    }
    private void handleLeftUp(){
        if(!held && !pendingLift && activeStroke==null && pendingActions.isEmpty())return;
        held=false;
        if(activeStroke!=null){
            if(!pendingLift && pendingActions.isEmpty()){
                if(!gestureRunning)dispatchLiveStrokeUp(x,y);
                else{pendingLift=true;pendingLiftX=x;pendingLiftY=y;}
            }else{
                enqueueAction(new TouchAction(TouchAction.TYPE_UP,x,y,x,y,8L,false));
            }
            return;
        }
        if(dragFallbackPending){
            dragFallbackPending=false;
            enqueueAction(new TouchAction(TouchAction.TYPE_GESTURE,
                downX,downY,x,y,gestureDurationMs(x-downX,y-downY),false));
            return;
        }
        if(!pendingActions.isEmpty())
            enqueueAction(new TouchAction(TouchAction.TYPE_UP,x,y,x,y,8L,false));
    }
    private void enqueueAction(TouchAction action){
        if(!remoteActive || !ready(this))return;
        if(pendingActions.size()>=8)pendingActions.pollFirst();
        pendingActions.addLast(action);
        pumpActions();
    }
    private void startLiveStrokeDown(int px,int py){
        Path path=new Path();
        path.moveTo(px,py);
        GestureDescription.StrokeDescription stroke=
            new GestureDescription.StrokeDescription(path,0,8L,true);
        final int gen=++gestureGeneration;
        activeStroke=stroke;
        strokeX=px;strokeY=py;
        gestureRunning=true;remoteGesture=true;
        boolean started=dispatchGesture(
            new GestureDescription.Builder().addStroke(stroke).build(),
            new GestureResultCallback(){
                @Override public void onCompleted(GestureDescription completed){
                    if(gen!=gestureGeneration)return;
                    onStrokeStepDone(false);
                }
                @Override public void onCancelled(GestureDescription cancelled){
                    if(gen!=gestureGeneration)return;
                    onStrokeStepDone(true);
                }
            },ui);
        if(!started){
            activeStroke=null;
            gestureRunning=false;
            if(pendingLift){
                pendingLift=false;
                enqueueAction(new TouchAction(TouchAction.TYPE_GESTURE,
                    downX,downY,pendingLiftX,pendingLiftY,
                    gestureDurationMs(pendingLiftX-downX,pendingLiftY-downY),false));
            }else if(held){
                dragFallbackPending=true;
            }
            pumpActions();
        }
    }
    private void continueDragIfReady(){
        if(!remoteActive || !ready(this) || !held || activeStroke==null
            || gestureRunning || pendingLift)return;
        if(Math.hypot(x-strokeX,y-strokeY)<2.0)return;
        Path path=new Path();
        path.moveTo(strokeX,strokeY);
        path.lineTo(x,y);
        GestureDescription.StrokeDescription next;
        try{
            next=activeStroke.continueStroke(path,0,8L,true);
        }catch(RuntimeException badContinuation){
            activeStroke=null;
            dragFallbackPending=true;
            return;
        }
        final int gen=++gestureGeneration;
        activeStroke=next;
        strokeX=x;strokeY=y;
        gestureRunning=true;remoteGesture=true;
        boolean started=dispatchGesture(
            new GestureDescription.Builder().addStroke(next).build(),
            new GestureResultCallback(){
                @Override public void onCompleted(GestureDescription completed){
                    if(gen!=gestureGeneration)return;
                    onStrokeStepDone(false);
                }
                @Override public void onCancelled(GestureDescription cancelled){
                    if(gen!=gestureGeneration)return;
                    onStrokeStepDone(true);
                }
            },ui);
        if(!started){
            activeStroke=null;
            gestureRunning=false;
            dragFallbackPending=held;
            pumpActions();
        }
    }
    private void dispatchLiveStrokeUp(int px,int py){
        GestureDescription.StrokeDescription cur=activeStroke;
        activeStroke=null;
        pendingLift=false;
        if(cur==null)return;
        Path path=new Path();
        path.moveTo(strokeX,strokeY);
        if(px!=strokeX||py!=strokeY)path.lineTo(px,py);
        GestureDescription.StrokeDescription up;
        try{
            up=cur.continueStroke(path,0,8L,false);
        }catch(RuntimeException badUp){
            gestureRunning=false;
            enqueueAction(new TouchAction(TouchAction.TYPE_GESTURE,
                downX,downY,px,py,gestureDurationMs(px-downX,py-downY),false));
            return;
        }
        final int gen=++gestureGeneration;
        gestureRunning=true;remoteGesture=true;
        boolean started=dispatchGesture(
            new GestureDescription.Builder().addStroke(up).build(),
            new GestureResultCallback(){
                @Override public void onCompleted(GestureDescription completed){
                    if(gen!=gestureGeneration)return;
                    onStrokeLiftDone();
                }
                @Override public void onCancelled(GestureDescription cancelled){
                    if(gen!=gestureGeneration)return;
                    onStrokeLiftDone();
                }
            },ui);
        if(!started){
            gestureRunning=false;
            pumpActions();
        }
    }
    private void onStrokeStepDone(boolean cancelled){
        gestureRunning=false;
        if(!remoteActive || !ready(this)){
            activeStroke=null;pendingLift=false;remoteGesture=false;
            pendingActions.clear();
            return;
        }
        if(cancelled){
            activeStroke=null;
            if(pendingLift){
                pendingLift=false;
                enqueueAction(new TouchAction(TouchAction.TYPE_GESTURE,
                    downX,downY,pendingLiftX,pendingLiftY,
                    gestureDurationMs(pendingLiftX-downX,pendingLiftY-downY),false));
            }else if(held){
                dragFallbackPending=true;
            }
            pumpActions();
            return;
        }
        if(pendingLift && activeStroke!=null){
            dispatchLiveStrokeUp(pendingLiftX,pendingLiftY);
            return;
        }
        if(held && activeStroke!=null)continueDragIfReady();
    }
    private void onStrokeLiftDone(){
        gestureRunning=false;
        if(!pendingActions.isEmpty())pumpActions();
        else if(activeStroke==null)remoteGesture=false;
    }
    private void pumpActions(){
        while(!gestureRunning && activeStroke==null && remoteActive && ready(this)){
            TouchAction command=pendingActions.pollFirst();
            if(command==null){remoteGesture=false;return;}
            if(command.kind==TouchAction.TYPE_DOWN){
                TouchAction next=pendingActions.peekFirst();
                if(next!=null && next.kind==TouchAction.TYPE_UP){
                    pendingActions.pollFirst();
                    dispatchSingleGesture(command.sx,command.sy,next.ex,next.ey,
                        gestureDurationMs(next.ex-command.sx,next.ey-command.sy));
                    return;
                }
                if(held){
                    startLiveStrokeDown(x,y);
                    return;
                }
                continue;
            }
            if(command.kind==TouchAction.TYPE_UP)continue;
            if(command.kind==TouchAction.TYPE_SCROLL &&
                Math.hypot(command.ex-command.sx,command.ey-command.sy)<36.0)
                continue;
            dispatchSingleGesture(command.sx,command.sy,command.ex,command.ey,command.duration);
            return;
        }
    }
    private void dispatchSingleGesture(int sx,int sy,int ex,int ey,long duration){
        Path path=new Path();
        path.moveTo(sx,sy);
        if(sx!=ex||sy!=ey)path.lineTo(ex,ey);
        GestureDescription.StrokeDescription stroke=
            new GestureDescription.StrokeDescription(path,0,duration);
        GestureDescription gesture=new GestureDescription.Builder().addStroke(stroke).build();
        final int gen=++gestureGeneration;
        remoteGesture=gestureRunning=true;
        boolean started=dispatchGesture(gesture,new GestureResultCallback(){
            @Override public void onCompleted(GestureDescription completed){
                if(gen!=gestureGeneration)return;
                finishGesture();
            }
            @Override public void onCancelled(GestureDescription cancelled){
                if(gen!=gestureGeneration)return;
                finishGesture();
            }
        },ui);
        if(!started){
            // Some OEMs reject gestures; fall back to node-click only then.
            if(sx==ex && sy==ey)performNativeClick();
            finishGesture();
        }
    }
    private void finishGesture(){
        gestureRunning=false;
        if(pendingActions.isEmpty() && activeStroke==null)remoteGesture=false;
        pumpActions();
    }
    static boolean safeEditorType(int inputType){
        int kind=inputType & InputType.TYPE_MASK_CLASS;
        int variant=inputType & InputType.TYPE_MASK_VARIATION;
        if(kind==InputType.TYPE_CLASS_TEXT){
            return variant!=InputType.TYPE_TEXT_VARIATION_PASSWORD &&
                variant!=InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD &&
                variant!=InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD;
        }
        return kind!=InputType.TYPE_NULL &&
            !(kind==InputType.TYPE_CLASS_NUMBER &&
                variant==InputType.TYPE_NUMBER_VARIATION_PASSWORD);
    }

    /**
     * Chrome, WebView, and SearchView often report TYPE_NULL on their active
     * AccessibilityInputConnection even when they are ordinary non-password boxes.
     * Keep safeEditorType strict while allowing non-password TYPE_NULL connections.
     */
    static boolean allowsDirectInputConnection(boolean nodeIsPassword,int inputType){
        if(nodeIsPassword)return false;
        int kind=inputType & InputType.TYPE_MASK_CLASS;
        if(kind==InputType.TYPE_NULL)return true;
        return safeEditorType(inputType);
    }

    static boolean isSupportedShortcutKey(int vk,boolean ctrl,boolean alt,boolean shift){
        return ctrl && !alt && !shift &&
            (vk==0x41 || vk==0x43 || vk==0x56 || vk==0x58);
    }

    private AccessibilityNodeInfo focusEditableAtPoint(int px,int py){
        AccessibilityNodeInfo root=getRootInActiveWindow();
        AccessibilityNodeInfo match=findEditableAtPoint(root,px,py);
        if(match==null){
            List<AccessibilityWindowInfo> list=getWindows();
            if(list!=null){
                for(AccessibilityWindowInfo w:list){
                    if(w==null)continue;
                    match=findEditableAtPoint(w.getRoot(),px,py);
                    if(match!=null)break;
                }
            }
        }
        if(match!=null && !match.isPassword()){
            match.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
            if(!match.isFocused())
                match.performAction(AccessibilityNodeInfo.ACTION_CLICK);
            return match;
        }
        return null;
    }

    private static AccessibilityNodeInfo findEditableAtPoint(
        AccessibilityNodeInfo root,int px,int py)
    {
        if(root==null)return null;
        ArrayDeque<AccessibilityNodeInfo> nodes=new ArrayDeque<>();
        nodes.push(root);
        AccessibilityNodeInfo editor=null;
        long bestArea=Long.MAX_VALUE;
        int visited=0;
        Rect bounds=new Rect();
        while(!nodes.isEmpty() && ++visited<=240){
            AccessibilityNodeInfo node=nodes.pop();
            node.getBoundsInScreen(bounds);
            if(!bounds.contains(px,py))continue;
            if(node.isEditable() && node.isEnabled()){
                long area=(long)Math.max(1,bounds.width())*Math.max(1,bounds.height());
                if(area<=bestArea){
                    bestArea=area;
                    editor=node;
                }
            }
            for(int i=node.getChildCount()-1;i>=0;i--){
                AccessibilityNodeInfo child=node.getChild(i);
                if(child!=null)nodes.push(child);
            }
        }
        return editor;
    }

    private AccessibilityNodeInfo focusedEditor(){
        AccessibilityNodeInfo root=getRootInActiveWindow();
        if(root!=null){
            AccessibilityNodeInfo node=root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if(node!=null)return node;
        }
        // WebView/editors may expose focused input under a separate
        // interactive window, not under getRootInActiveWindow().
        List<AccessibilityWindowInfo> windows=getWindows();
        if(windows==null)return null;
        for(AccessibilityWindowInfo window:windows){
            if(window==null)continue;
            AccessibilityNodeInfo tree=window.getRoot();
            if(tree==null)continue;
            AccessibilityNodeInfo node=tree.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if(node!=null)return node;
        }
        return null;
    }

    private InputMethod.AccessibilityInputConnection nativeEditor(
        AccessibilityNodeInfo focusedField)
    {
        if(Build.VERSION.SDK_INT<33 ||
            (focusedField!=null && focusedField.isPassword()))
            return null;
        try {
            InputMethod method=getInputMethod();
            if(method==null)return null;
            InputMethod.AccessibilityInputConnection connection=
                method.getCurrentInputConnection();
            if(connection==null)return null;
            EditorInfo info=method.getCurrentInputEditorInfo();
            boolean nodePassword=focusedField!=null && focusedField.isPassword();
            if(info!=null)
                return allowsDirectInputConnection(nodePassword,info.inputType)
                    ? connection : null;
            return focusedField!=null && focusedField.isEditable() && !nodePassword
                ? connection : null;
        }catch(RuntimeException unavailable){return null;}
    }

    private boolean sendNativeKey(int vk,AccessibilityNodeInfo field){
        if(Build.VERSION.SDK_INT<33)return false;
        InputMethod.AccessibilityInputConnection editor=nativeEditor(field);
        int keyCode=RemoteImeKeys.androidKeyCode(vk);
        if(editor==null || keyCode==KeyEvent.KEYCODE_UNKNOWN)return false;
        try{
            long now=SystemClock.uptimeMillis();
            KeyEvent down=new KeyEvent(now,now,KeyEvent.ACTION_DOWN,keyCode,0,0,
                KeyCharacterMap.VIRTUAL_KEYBOARD,0,KeyEvent.FLAG_SOFT_KEYBOARD,
                InputDevice.SOURCE_KEYBOARD);
            KeyEvent up=KeyEvent.changeAction(down,KeyEvent.ACTION_UP);
            editor.sendKeyEvent(down);
            editor.sendKeyEvent(up);
            return true;
        }catch(RuntimeException unavailable){return false;}
    }

    private boolean performShortcutAction(int vk,AccessibilityNodeInfo field){
        if(!isSupportedShortcutKey(vk,control,alt,shift))return false;
        if(field!=null && field.isPassword())return false;
        int menuId=vk==0x41?android.R.id.selectAll:
            vk==0x43?android.R.id.copy:
            vk==0x58?android.R.id.cut:android.R.id.paste;
        if(Build.VERSION.SDK_INT>=33){
            InputMethod.AccessibilityInputConnection editor=nativeEditor(field);
            if(editor!=null){
                try{
                    editor.performContextMenuAction(menuId);
                    return true;
                }catch(RuntimeException ignored){}
            }
        }
        if(field==null)return false;
        if(vk==0x41){
            CharSequence text=field.getText();
            int len=text==null?0:text.length();
            Bundle sel=new Bundle();
            sel.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT,0);
            sel.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT,len);
            return field.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION,sel);
        }
        int action=vk==0x43?AccessibilityNodeInfo.ACTION_COPY:
            vk==0x58?AccessibilityNodeInfo.ACTION_CUT:
            AccessibilityNodeInfo.ACTION_PASTE;
        return field.performAction(action);
    }

    private boolean commitNativeText(String incoming,
        AccessibilityNodeInfo focusedField)
    {
        InputMethod.AccessibilityInputConnection editor=nativeEditor(focusedField);
        if(editor==null)return false;
        try{
            editor.commitText(incoming,1,null);
            return true;
        }catch(RuntimeException unavailable){
            return false; // fallback to ACTION_SET_TEXT where available
        }
    }

    private boolean nativeDelete(int vk,AccessibilityNodeInfo field){
        if(vk!=8 && vk!=46)return false;
        InputMethod.AccessibilityInputConnection editor=nativeEditor(field);
        if(editor==null)return false;
        try{
            editor.deleteSurroundingText(vk==8?1:0,vk==46?1:0);
            return true;
        }catch(RuntimeException unavailable){return false;}
    }

    static void text(String incoming,Runnable rejected){
        text(incoming,null,rejected);
    }

    static void text(String incoming,Runnable accepted,Runnable rejected){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service)||
            incoming==null||incoming.length()==0||incoming.length()>512){
            if(rejected!=null)rejected.run();
            return;
        }
        service.ui.post(()->{
            if(!service.remoteActive || !ready(service)){
                if(rejected!=null)rejected.run();
                return;
            }
            AccessibilityNodeInfo field=service.focusedEditor();
            if(field==null || !field.isEditable())
                field=service.focusEditableAtPoint(service.x,service.y);
            if(service.commitNativeText(incoming,field) ||
                DeskTouchInputMethod.commitRemoteText(incoming)){
                if(accepted!=null)accepted.run();
                return;
            }
            if(field==null||!field.isEditable()||field.isPassword()){
                if(rejected!=null)rejected.run();
                return;
            }
            CharSequence original=field.getText();
            String content=original==null?"":original.toString();
            int start=field.getTextSelectionStart();
            int end=field.getTextSelectionEnd();
            if(start<0)start=content.length();
            if(end<0)end=start;
            start=Math.max(0,Math.min(content.length(),start));
            end=Math.max(start,Math.min(content.length(),end));
            String value=content.substring(0,start)+incoming+content.substring(end);
            Bundle args=new Bundle();
            args.putCharSequence(
                AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,value);
            if(field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,args)){
                Bundle selection=new Bundle();
                int caret=start+incoming.length();
                selection.putInt(
                    AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT,caret);
                selection.putInt(
                    AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT,caret);
                field.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION,selection);
                if(accepted!=null)accepted.run();
            }else if(rejected!=null)rejected.run();
        });
    }

    static void key(int vk,boolean pressed,Runnable rejected){
        key(vk,pressed,null,rejected);
    }

    static void key(int vk,boolean pressed,Runnable accepted,Runnable rejected){
        DeskTouchAccessibilityService service=instance;
        if(service==null || !ready(service)){
            if(pressed && rejected!=null)rejected.run();
            return;
        }
        service.ui.post(()->{
            if(!ready(service)||!service.remoteActive ||
                !service.applyKey(vk,pressed)){
                if(pressed && rejected!=null)rejected.run();
            }else if(pressed && accepted!=null){
                accepted.run();
            }
        });
    }
    private boolean applyKey(int vk,boolean pressed){
        if(vk==16||vk==160||vk==161){shift=pressed;return true;}
        if(vk==17||vk==162||vk==163){control=pressed;return true;}
        if(vk==18||vk==164||vk==165){alt=pressed;return true;}
        if(!pressed)return true;
        AccessibilityNodeInfo field=focusedEditor();
        if(field==null || !field.isEditable())
            field=focusEditableAtPoint(x,y);
        if(control||alt){
            return performShortcutAction(vk,field);
        }
        if(vk==27)return performGlobalAction(GLOBAL_ACTION_BACK);
        if(vk==0x24 && !isTextFieldFocused())
            return performGlobalAction(GLOBAL_ACTION_HOME);
        // Send Enter and Tab as real key events first so search bars, address bars,
        // and dialogs submit or advance focus instead of inserting raw newlines.
        if(vk==13 && (sendNativeKey(vk,field) ||
            (Build.VERSION.SDK_INT>=30 && field!=null && !field.isPassword() &&
                field.performAction(
                    AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.getId())) ||
            DeskTouchInputMethod.sendRemoteKey(vk)))
            return true;
        if(vk==9 && (sendNativeKey(vk,field) ||
            DeskTouchInputMethod.sendRemoteKey(vk)))
            return true;
        String printable=WindowsKeyText.printable(vk,shift);
        if(printable!=null && (commitNativeText(printable,field) ||
            DeskTouchInputMethod.commitRemoteText(printable)))return true;
        if(sendNativeKey(vk,field) || nativeDelete(vk,field) ||
            DeskTouchInputMethod.sendRemoteKey(vk))
            return true;
        if(field==null || !field.isEditable() || field.isPassword())
            return false;
        CharSequence value=field.getText();
        String original=value==null?"":value.toString();
        WindowsKeyText.Edit change=WindowsKeyText.apply(original,
            field.getTextSelectionStart()<0?original.length():field.getTextSelectionStart(),
            field.getTextSelectionEnd()<0?original.length():field.getTextSelectionEnd(),
            vk,shift);
        if(change==null)return false;
        if(!change.text.equals(original)){
            Bundle update=new Bundle();
            update.putCharSequence(
                AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,change.text);
            if(!field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,update))
                return false;
        }
        Bundle caret=new Bundle();
        caret.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT,change.cursor);
        caret.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT,change.cursor);
        field.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION,caret);
        return true;
    }
    private boolean isTextFieldFocused(){
        AccessibilityNodeInfo focused=focusedEditor();
        return focused!=null && focused.isEditable();
    }
}
