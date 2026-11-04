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
import android.view.Choreographer;
import android.graphics.Rect;
import android.view.View;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;
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
    private static final String PREFS="desktouch_android_control";
    private static final String ALLOWED="user_allowed_windows_control";
    private final Handler ui=new Handler(Looper.getMainLooper());
    private WindowManager windows;
    private View cursor;
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
    private final Choreographer.FrameCallback pointerFrame = ignored -> flushPointer();
    private int lastOverlayX=Integer.MIN_VALUE,lastOverlayY=Integer.MIN_VALUE;
    private int downX,downY;
    private boolean held,shift,control,alt,remoteGesture;
    private boolean gestureRunning;
    private final ArrayDeque<GestureCommand> pendingGestures=new ArrayDeque<>();
    private static final class GestureCommand {
        final int sx,sy,ex,ey;
        final long duration;
        GestureCommand(int sx,int sy,int ex,int ey,long duration){
            this.sx=sx;this.sy=sy;this.ex=ex;this.ey=ey;this.duration=duration;
        }
    }
    private final class PointerSurface extends View {
        private final Paint fill=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint ring=new Paint(Paint.ANTI_ALIAS_FLAG);
        PointerSurface(Context context){
            super(context);
            setWillNotDraw(false);
            fill.setColor(0xff37b6f6);
            ring.setColor(0xffffffff);
            ring.setStyle(Paint.Style.STROKE);
            ring.setStrokeWidth(dp(2));
        }
        @Override protected void onDraw(Canvas canvas){
            super.onDraw(canvas);
            int px=lastOverlayX,py=lastOverlayY;
            if(px==Integer.MIN_VALUE||py==Integer.MIN_VALUE)return;
            float radius=dp(9);
            canvas.drawCircle(px,py,radius,fill);
            canvas.drawCircle(px,py,radius,ring);
        }
    }

    private volatile boolean remoteActive;

    static boolean allowed(Context context){
        return context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
            .getBoolean(ALLOWED,false);
    }
    static void setAllowed(Context context,boolean allowed){
        // Persist the user's app-level approval synchronously BEFORE the
        // package installer can kill this process. In-place signed upgrades
        // retain these preferences; an uninstall/data-clear cannot.
        if(!context.getSharedPreferences(PREFS,Context.MODE_PRIVATE).edit()
            .putBoolean(ALLOWED,allowed).commit())
            throw new IllegalStateException("Cannot save Android control preference");
        if(!allowed){
            DeskTouchAccessibilityService service=instance;
            if(service!=null){
            service.remoteActive=false;
            service.ui.post(service::removePointer);
        }
            DeskTouchRuntime.remoteNativeTouch();
        }
    }
    static boolean ready(Context context){
        return instance!=null && allowed(context);
    }
    @Override public void onServiceConnected(){
        super.onServiceConnected();
        instance=this;
        windows=(WindowManager)getSystemService(WINDOW_SERVICE);
        DeskTouchRuntime.accessibilityPermissionChanged();
    }
    @Override public void onAccessibilityEvent(AccessibilityEvent event){
        // Touch from the phone user takes priority over remote input.
        // Events generated by our own approved gestures are ignored.
        if(event!=null&&event.getEventType()==
            AccessibilityEvent.TYPE_TOUCH_INTERACTION_START && !remoteGesture){
            DeskTouchRuntime.remoteNativeTouch();
            removePointer();
        }
    }
    @Override public void onInterrupt(){
        DeskTouchRuntime.remoteNativeTouch();
        removePointer();
    }
    @Override public void onDestroy(){
        if(instance==this)instance=null;
        DeskTouchRuntime.accessibilityPermissionChanged();
        DeskTouchRuntime.remoteNativeTouch();
        removePointer();
        super.onDestroy();
    }
    private int dp(float px){return Math.round(px*getResources().getDisplayMetrics().density);}
    private void showPointer(int requestedX,int requestedY){
        if(!ready(this))return;
        android.util.DisplayMetrics dm=getResources().getDisplayMetrics();
        x=Math.max(0,Math.min(Math.max(1,dm.widthPixels)-1,requestedX));
        y=Math.max(0,Math.min(Math.max(1,dm.heightPixels)-1,requestedY));
        if(cursor==null){
            // A transparent, NON-TOUCHABLE accessibility surface is added
            // only once. Its tiny dirty rectangle is redrawn at display
            // VSync, avoiding a WindowManager Binder relayout on every
            // network mouse movement (a major source of sporadic lag).
            PointerSurface pointer=new PointerSurface(this);
            WindowManager.LayoutParams params=new WindowManager.LayoutParams(
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE |
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE |
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT);
            params.gravity=Gravity.TOP|Gravity.LEFT;
            params.x=0;params.y=0;
            lastOverlayX=x;lastOverlayY=y;
            try{
                windows.addView(pointer,params);
                cursor=pointer;
            }catch(RuntimeException blocked){
                lastOverlayX=lastOverlayY=Integer.MIN_VALUE;
                return;
            }
        }
        if(cursor!=null && (x!=lastOverlayX || y!=lastOverlayY)){
            int oldX=lastOverlayX,oldY=lastOverlayY;
            lastOverlayX=x;lastOverlayY=y;
            int halo=dp(14);
            int left=Math.max(0,Math.min(oldX,x)-halo);
            int top=Math.max(0,Math.min(oldY,y)-halo);
            int right=Math.min(cursor.getWidth(),Math.max(oldX,x)+halo);
            int bottom=Math.min(cursor.getHeight(),Math.max(oldY,y)+halo);
            if(right>left&&bottom>top)
                cursor.invalidate(left,top,right,bottom);
            else cursor.invalidate();
        }
    }
    private void removePointer(){
        held=false;
        remoteActive=false;
        pendingApplied=null;
        Choreographer.getInstance().removeFrameCallback(pointerFrame);
        moveScheduled.set(false);
        lastOverlayX=lastOverlayY=Integer.MIN_VALUE;
        pendingGestures.clear();
        gestureRunning=false;
        if(cursor!=null&&windows!=null){
            try{windows.removeView(cursor);}catch(RuntimeException ignored){}
        }
        cursor=null;
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
        if(service.moveScheduled.compareAndSet(false,true))
            service.ui.post(()->Choreographer.getInstance()
                .postFrameCallback(service.pointerFrame));
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
        if(remoteActive&&cursor!=null&&ack!=null&&ready(this))ack.run();
        if(remoteActive&&ready(this)&&(px!=pendingX||py!=pendingY) &&
            moveScheduled.compareAndSet(false,true))
            Choreographer.getInstance().postFrameCallback(pointerFrame);
    }
    private void flushPointerBeforeAction(){
        // A click or scroll must operate at the most recently received
        // pointer position, never the previous animation frame.
        if(remoteActive && (pendingX!=x || pendingY!=y || cursor==null))
            showPointer(pendingX,pendingY);
        Runnable ack=pendingApplied;
        pendingApplied=null;
        if(cursor!=null && ack!=null && remoteActive)ack.run();
    }
    static void endRemote(){
        DeskTouchAccessibilityService service=instance;
        if(service!=null)service.ui.post(service::removePointer);
    }
    static void mouseButton(int button,boolean pressed){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service)||button!=1)return;
        service.ui.post(()->{
            if(!service.remoteActive || !ready(service))return;
            service.flushPointerBeforeAction();
            if(pressed){
                service.held=true;service.downX=service.x;service.downY=service.y;
            }else if(service.held){
                service.held=false;
                // A normal tap can use the application's native click
                // action immediately. Drag paths still use opt-in gestures.
                if(service.downX==service.x && service.downY==service.y &&
                    service.performNativeClick())return;
                service.enqueueGesture(service.downX,service.downY,
                    service.x,service.y,45);
            }
        });
    }
    static void wheel(int amount,boolean horizontal){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service)||amount==0)return;
        service.ui.post(()->{
            if(!service.remoteActive || !ready(service))return;
            service.flushPointerBeforeAction();
            // Native accessibility scroll actions are dispatched directly to
            // the scrollable UI node under the cursor. This avoids an
            // unnecessary 120ms gesture when the target exposes scrolling.
            if(!horizontal && service.performNativeScroll(amount))return;
            int distance=Math.max(55,Math.min(210,Math.abs(amount)));
            int sx=service.x,sy=service.y;
            int ex=horizontal?sx+(amount>0?-distance:distance):sx;
            int ey=horizontal?sy:sy+(amount>0?-distance:distance);
            service.enqueueGesture(sx,sy,ex,ey,85);
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
        return target!=null && target.performAction(AccessibilityNodeInfo.ACTION_CLICK);
    }

    private boolean performNativeScroll(int amount){
        AccessibilityNodeInfo root=getRootInActiveWindow();
        if(root==null)return false;
        // Bound traversal so a huge UI tree cannot stall the main thread.
        ArrayDeque<AccessibilityNodeInfo> nodes=new ArrayDeque<>();
        nodes.push(root);
        AccessibilityNodeInfo target=null;
        int visited=0;
        Rect bounds=new Rect();
        while(!nodes.isEmpty() && ++visited<=96){
            AccessibilityNodeInfo node=nodes.pop();
            node.getBoundsInScreen(bounds);
            if(!bounds.contains(x,y))continue;
            if(node.isScrollable())target=node;
            for(int i=node.getChildCount()-1;i>=0;i--){
                AccessibilityNodeInfo child=node.getChild(i);
                if(child!=null)nodes.push(child);
            }
        }
        if(target==null)return false;
        int action=amount>0?AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD:
            AccessibilityNodeInfo.ACTION_SCROLL_FORWARD;
        return target.performAction(action);
    }

    private void enqueueGesture(int sx,int sy,int ex,int ey,long duration){
        if(!remoteActive || !ready(this))return;
        // dispatchGesture cancels an already running gesture. Serialize
        // commands instead of repeatedly interrupting taps and swipes.
        if(pendingGestures.size()>=6)pendingGestures.pollFirst();
        pendingGestures.addLast(new GestureCommand(sx,sy,ex,ey,duration));
        pumpGestures();
    }
    private void pumpGestures(){
        if(gestureRunning || !remoteActive || !ready(this))return;
        GestureCommand command=pendingGestures.pollFirst();
        if(command==null){remoteGesture=false;return;}
        Path path=new Path();
        path.moveTo(command.sx,command.sy);
        if(command.sx!=command.ex||command.sy!=command.ey)
            path.lineTo(command.ex,command.ey);
        GestureDescription.StrokeDescription stroke=
            new GestureDescription.StrokeDescription(path,0,command.duration);
        GestureDescription gesture=new GestureDescription.Builder().addStroke(stroke).build();
        remoteGesture=gestureRunning=true;
        boolean started=dispatchGesture(gesture,new GestureResultCallback(){
            @Override public void onCompleted(GestureDescription completed){
                finishGesture();
            }
            @Override public void onCancelled(GestureDescription cancelled){
                finishGesture();
            }
        },ui);
        if(!started)finishGesture();
    }
    private void finishGesture(){
        gestureRunning=false;
        if(pendingGestures.isEmpty())remoteGesture=false;
        pumpGestures();
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
        // Chrome/WebView sometimes exposes a non-editable parent as the
        // Accessibility focus while Android still has a genuinely active,
        // typed, non-password InputConnection to its actual editor.
        if(Build.VERSION.SDK_INT<33 ||
            (focusedField!=null && focusedField.isPassword()))
            return null;
        try {
            InputMethod method=getInputMethod();
            if(method==null || !method.getCurrentInputStarted())return null;
            EditorInfo info=method.getCurrentInputEditorInfo();
            if(info==null || !safeEditorType(info.inputType))return null;
            // An active non-password Android InputConnection is itself an
            // authoritative focused editor, including WebView, even when
            // findFocus() cannot expose a standard AccessibilityNodeInfo.
            return method.getCurrentInputConnection();
        }catch(RuntimeException unavailable){return null;}
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

    static void text(String incoming){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service)||
            incoming==null||incoming.length()==0||incoming.length()>512)return;
        service.ui.post(()->{
            AccessibilityNodeInfo field=service.focusedEditor();
            if(service.commitNativeText(incoming,field))return;
            if(field==null||!field.isEditable()||field.isPassword())return;
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
            }
        });
    }

    static void key(int vk,boolean pressed){
        DeskTouchAccessibilityService service=instance;
        if(service==null||!ready(service))return;
        service.ui.post(()->service.applyKey(vk,pressed));
    }
    private void applyKey(int vk,boolean pressed){
        if(vk==16||vk==160||vk==161){shift=pressed;return;}
        if(vk==17||vk==162||vk==163){control=pressed;return;}
        if(vk==18||vk==164||vk==165){alt=pressed;return;}
        if(!pressed||control||alt)return;
        if(vk==27){performGlobalAction(GLOBAL_ACTION_BACK);return;}
        if(vk==0x24 && !isTextFieldFocused()){performGlobalAction(GLOBAL_ACTION_HOME);return;}
        AccessibilityNodeInfo field=focusedEditor();
        String printable=WindowsKeyText.printable(vk,shift);
        if(printable!=null && commitNativeText(printable,field))return;
        if(nativeDelete(vk,field))return;
        if(field==null || !field.isEditable() || field.isPassword())return;
        CharSequence text=field.getText();
        String original=text==null?"":text.toString();
        WindowsKeyText.Edit change=WindowsKeyText.apply(original,
            field.getTextSelectionStart()<0?original.length():field.getTextSelectionStart(),
            field.getTextSelectionEnd()<0?original.length():field.getTextSelectionEnd(),
            vk,shift);
        if(change==null)return;
        if(!change.text.equals(original)){
            Bundle value=new Bundle();
            value.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,
                change.text);
            if(!field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,value))return;
        }
        Bundle caret=new Bundle();
        caret.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT,change.cursor);
        caret.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT,change.cursor);
        field.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION,caret);
    }
    private boolean isTextFieldFocused(){
        AccessibilityNodeInfo root=getRootInActiveWindow();
        if(root==null)return false;
        AccessibilityNodeInfo focused=root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        return focused!=null&&focused.isEditable();
    }
}
