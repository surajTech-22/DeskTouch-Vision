package com.desktouch.vision;

import android.app.Activity;
import android.os.Bundle;
import android.content.Context;
import android.graphics.Color;
import android.view.MotionEvent;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.concurrent.atomic.AtomicInteger;

/** Debug-only UI under test: verifies actual Android IME and Accessibility actions. */
public final class InputProbeActivity extends Activity {
    EditText editor;
    Button clickTarget;
    TextView dragTarget;
    ScrollView scrollTarget;
    final AtomicInteger clicks=new AtomicInteger();
    final AtomicInteger drags=new AtomicInteger();
    private float downX,downY;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
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
    }
    private int dp(int v) {
        return Math.round(v*getResources().getDisplayMetrics().density);
    }
}
