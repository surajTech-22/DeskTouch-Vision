package com.desktouch.vision;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.TextWatcher;
import android.util.Log;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;

/** Debug-only external-process target, mimicking a real third-party text editor. */
public final class ExternalEditorProbeActivity extends Activity {
    static final String TAG="DeskTouchExternalEditor";
    private EditText editor;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root=new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(15,15,15,15);
        root.setBackgroundColor(Color.WHITE);
        editor=new EditText(this);
        editor.setTextColor(Color.BLACK);
        editor.setSingleLine(true);
        editor.setFocusableInTouchMode(true);
        editor.setHint("External-process keyboard test");
        editor.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s,int start,int count,int after){}
            @Override public void onTextChanged(CharSequence s,int start,int before,int count) {
                // Fixed, generated test text only. This class is absent from release APK.
                Log.i(TAG,"EDITOR_VALUE="+s.toString()+" CARET="+editor.getSelectionStart());
            }
            @Override public void afterTextChanged(Editable editable){}
        });
        root.addView(editor,new LinearLayout.LayoutParams(-1,160));
        setContentView(root);
        new Handler(Looper.getMainLooper()).postDelayed(()->{
            editor.requestFocusFromTouch();
            InputMethodManager input=
                (InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
            if(input!=null){
                input.restartInput(editor);
                input.showSoftInput(editor,InputMethodManager.SHOW_IMPLICIT);
            }
            Log.i(TAG,"EDITOR_READY focused="+editor.hasFocus());
        },400);
    }
}
