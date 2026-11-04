package com.desktouch.vision;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.util.Log;
import android.view.MotionEvent;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BooleanSupplier;

/**
 * DEBUG ONLY real Android input probes executed in the application's normal
 * process, avoiding instrumentation's accessibility service suppression.
 * Never included in the permanently signed production APK.
 */
public final class InputProbeActivity extends Activity {
    private static final String TAG="DeskTouchInputProbe";
    private final Handler ui=new Handler(Looper.getMainLooper());
    private EditText editor;
    private Button clickTarget;
    private TextView dragTarget;
    private ScrollView scrollTarget;
    private final AtomicInteger clicks=new AtomicInteger();
    private final AtomicInteger drags=new AtomicInteger();
    private float downX,downY;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        // Android's system Accessibility service must already be enabled by
        // the emulator shell. This is only the in-app user's control toggle.
        DeskTouchAccessibilityService.setAllowed(this,true);
        LinearLayout root=new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(12,12,12,12);
        root.setBackgroundColor(Color.WHITE);
        editor=new EditText(this);
        editor.setSingleLine(true);
        editor.setHint("Remote keyboard probe");
        editor.setTextColor(Color.BLACK);
        root.addView(editor,new LinearLayout.LayoutParams(-1,dp(64)));
        clickTarget=new Button(this);
        clickTarget.setText("REMOTE CLICK TARGET");
        clickTarget.setOnClickListener(v->clicks.incrementAndGet());
        root.addView(clickTarget,new LinearLayout.LayoutParams(-1,dp(70)));
        dragTarget=new TextView(this);
        dragTarget.setText("REMOTE DRAG TARGET");
        dragTarget.setTextColor(Color.WHITE);
        dragTarget.setBackgroundColor(Color.rgb(55,95,150));
        dragTarget.setOnTouchListener((v,event)->{
            switch(event.getActionMasked()){
                case MotionEvent.ACTION_DOWN:
                    downX=event.getX();downY=event.getY();return true;
                case MotionEvent.ACTION_MOVE:return true;
                case MotionEvent.ACTION_UP:
                    if(Math.abs(event.getX()-downX)>12 ||
                       Math.abs(event.getY()-downY)>12)
                        drags.incrementAndGet();
                    return true;
                default:return false;
            }
        });
        root.addView(dragTarget,new LinearLayout.LayoutParams(-1,dp(180)));
        scrollTarget=new ScrollView(this);
        LinearLayout content=new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        for(int i=0;i<18;i++){
            TextView row=new TextView(this);
            row.setText("SCROLL PROBE ROW "+i);
            row.setTextSize(16);
            row.setTextColor(Color.BLACK);
            content.addView(row,new LinearLayout.LayoutParams(-1,dp(70)));
        }
        scrollTarget.addView(content);
        root.addView(scrollTarget,new LinearLayout.LayoutParams(-1,dp(260)));
        setContentView(root);
        ui.postDelayed(()->probe(getIntent()),450);
    }

    @Override protected void onNewIntent(Intent incoming) {
        super.onNewIntent(incoming);
        setIntent(incoming);
        ui.postDelayed(()->probe(incoming),350);
    }

    private int dp(int v) {
        return Math.round(v*getResources().getDisplayMetrics().density);
    }
    private void result(String name,boolean success,String detail) {
        Log.println(success?Log.INFO:Log.ERROR,TAG,
            "RESULT_"+name+"_"+(success?"PASS":"FAIL")+" "+detail);
    }
    private void checkUntil(String name,BooleanSupplier condition,int attempts,
        Runnable success) {
        if(condition.getAsBoolean()){success.run();return;}
        if(attempts==0){result(name,false,"Android UI did not reach expected state");return;}
        ui.postDelayed(()->checkUntil(name,condition,attempts-1,success),150);
    }
    private void dismissIme() {
        InputMethodManager manager=
            (InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
        if(manager!=null)manager.hideSoftInputFromWindow(editor.getWindowToken(),0);
        editor.clearFocus();
    }

    private void probe(Intent intent) {
        if(intent==null)return;
        String name=intent.getStringExtra("probe");
        if(name==null)return;
        if(!DeskTouchAccessibilityService.ready(this)){
            result(name.toUpperCase(),false,
                "Android Accessibility is not bound (system permission or app approval)");
            return;
        }
        if("keyboard".equals(name)) {
            editor.setText("");
            editor.requestFocus();
            InputMethodManager manager=
                (InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
            if(manager!=null)manager.showSoftInput(editor,InputMethodManager.SHOW_IMPLICIT);
            checkUntil("KEYBOARD",DeskTouchInputMethod::hasActiveEditor,28,()->{
                boolean accepted=DeskTouchInputMethod.commitRemoteText("hello") &&
                    DeskTouchInputMethod.sendRemoteKey(8) &&
                    DeskTouchInputMethod.commitRemoteText("o") &&
                    DeskTouchInputMethod.sendRemoteKey(37) &&
                    DeskTouchInputMethod.commitRemoteText("!");
                if(!accepted){result("KEYBOARD",false,"IME rejected an input operation");return;}
                checkUntil("KEYBOARD",()->"hell!o".equals(
                    editor.getText().toString()),25,()->
                    result("KEYBOARD",true,"text, deletion and caret movement applied"));
            });
        } else if("click".equals(name)) {
            dismissIme();
            int[] at=new int[2];clickTarget.getLocationOnScreen(at);
            int x=at[0]+clickTarget.getWidth()/2,y=at[1]+clickTarget.getHeight()/2;
            if(!DeskTouchAccessibilityService.pointer(x,y,null)){
                result("CLICK",false,"Accessibility pointer unavailable");return;
            }
            DeskTouchAccessibilityService.mouseButton(1,true);
            DeskTouchAccessibilityService.mouseButton(1,false);
            checkUntil("CLICK",()->clicks.get()>0,25,()->
                result("CLICK",true,"native Android button was clicked"));
        } else if("drag".equals(name)) {
            dismissIme();
            int[] at=new int[2];dragTarget.getLocationOnScreen(at);
            int sx=at[0]+dragTarget.getWidth()/3;
            int sy=at[1]+dragTarget.getHeight()/2;
            int ex=sx+dragTarget.getWidth()/3;
            if(!DeskTouchAccessibilityService.pointer(sx,sy,null)){
                result("DRAG",false,"Accessibility pointer unavailable");return;
            }
            DeskTouchAccessibilityService.mouseButton(1,true);
            ui.postDelayed(()->{
                DeskTouchAccessibilityService.pointer(ex,sy,null);
                DeskTouchAccessibilityService.mouseButton(1,false);
                checkUntil("DRAG",()->drags.get()>0,30,()->
                    result("DRAG",true,"Android touch view received drag"));
            },130);
        } else if("scroll".equals(name)) {
            dismissIme();
            int[] at=new int[2];scrollTarget.getLocationOnScreen(at);
            int x=at[0]+scrollTarget.getWidth()/2;
            int y=at[1]+scrollTarget.getHeight()/2;
            if(!DeskTouchAccessibilityService.pointer(x,y,null)){
                result("SCROLL",false,"Accessibility pointer unavailable");return;
            }
            DeskTouchAccessibilityService.wheel(-120,false);
            checkUntil("SCROLL",()->scrollTarget.getScrollY()>0,25,()->
                result("SCROLL",true,"Android ScrollView moved"));
        } else if("status".equals(name)) {
            result("STATUS",true,"Accessibility bound, IME selected="+
                DeskTouchInputMethod.isSelected(this));
        }
    }
}
