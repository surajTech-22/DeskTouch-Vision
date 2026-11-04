package com.desktouch.vision;

import android.content.ComponentName;
import android.content.Context;
import android.graphics.Color;
import android.inputmethodservice.InputMethodService;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * Optional, user-selected Android input method for DeskTouch remote typing.
 *
 * This is an EDITOR input connection, not a spoofed Bluetooth device, not UHID,
 * and not system-wide injection. Never claims focus or grants permissions.
 * Existing Accessibility actions remain the mouse/touch path.
 */
public final class DeskTouchInputMethod extends InputMethodService {
    private static volatile DeskTouchInputMethod current;
    private boolean editorStarted;

    @Override public void onCreate() {
        super.onCreate();
        current=this;
    }

    @Override public void onStartInput(EditorInfo attribute,boolean restarting) {
        super.onStartInput(attribute,restarting);
        editorStarted=true;
    }

    @Override public void onFinishInput() {
        editorStarted=false;
        super.onFinishInput();
    }

    @Override public void onDestroy() {
        editorStarted=false;
        if(current==this)current=null;
        super.onDestroy();
    }

    @Override public View onCreateInputView() {
        LinearLayout layout=new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(20,12,20,12);
        layout.setBackgroundColor(Color.rgb(36,39,46));
        TextView hint=new TextView(this);
        hint.setText("DeskTouch keyboard receiver — input from an approved Windows PC");
        hint.setTextColor(Color.WHITE);
        hint.setTextSize(14);
        layout.addView(hint);
        Button choose=new Button(this);
        choose.setText("Switch Android keyboard");
        choose.setAllCaps(false);
        choose.setOnClickListener(v->{
            InputMethodManager manager=
                (InputMethodManager)getSystemService(INPUT_METHOD_SERVICE);
            if(manager!=null)manager.showInputMethodPicker();
        });
        layout.addView(choose);
        return layout;
    }

    static boolean isSelected(Context context) {
        String selected=Settings.Secure.getString(context.getContentResolver(),
            Settings.Secure.DEFAULT_INPUT_METHOD);
        ComponentName identity=new ComponentName(context,DeskTouchInputMethod.class);
        return identity.flattenToString().equals(selected) ||
            identity.flattenToShortString().equals(selected);
    }

    private InputConnection approvedEditor() {
        if(!editorStarted || !DeskTouchAccessibilityService.ready(this))return null;
        EditorInfo info=getCurrentInputEditorInfo();
        if(info==null ||
            !DeskTouchAccessibilityService.safeEditorType(info.inputType))return null;
        return getCurrentInputConnection();
    }

    /** A real focused editor connection exists; used by emulator acceptance. */
    static boolean hasActiveEditor() {
        DeskTouchInputMethod service=current;
        return service!=null && service.approvedEditor()!=null;
    }

    /** Non-sensitive state for debug Android emulator probes; never reads text. */
    static String stateForTesting(Context context) {
        DeskTouchInputMethod method=current;
        if(method==null)return "service=not-bound selected="+isSelected(context);
        EditorInfo info=method.getCurrentInputEditorInfo();
        return "service=bound started="+method.editorStarted+
            " hasEditorInfo="+(info!=null)+
            " hasInputConnection="+(method.getCurrentInputConnection()!=null)+
            " selected="+isSelected(context)+
            " approved="+DeskTouchAccessibilityService.ready(context);
    }

    /** Called on the Android UI thread from the existing authorized input path. */
    static boolean commitRemoteText(String value) {
        DeskTouchInputMethod service=current;
        if(service==null || value==null || value.isEmpty() || value.length()>512)
            return false;
        InputConnection editor=service.approvedEditor();
        if(editor==null)return false;
        try {return editor.commitText(value,1);}
        catch(RuntimeException unavailable){return false;}
    }

    /** Special keys apply only to the currently focused, non-password editor. */
    static boolean sendRemoteKey(int windowsVk) {
        DeskTouchInputMethod service=current;
        if(service==null)return false;
        InputConnection editor=service.approvedEditor();
        int keyCode=RemoteImeKeys.androidKeyCode(windowsVk);
        if(editor==null || keyCode==KeyEvent.KEYCODE_UNKNOWN)return false;
        try {
            long now=SystemClock.uptimeMillis();
            KeyEvent down=new KeyEvent(now,now,KeyEvent.ACTION_DOWN,keyCode,0,0,
                KeyCharacterMap.VIRTUAL_KEYBOARD,0,KeyEvent.FLAG_SOFT_KEYBOARD,
                InputDevice.SOURCE_KEYBOARD);
            KeyEvent up=KeyEvent.changeAction(down,KeyEvent.ACTION_UP);
            return editor.sendKeyEvent(down) && editor.sendKeyEvent(up);
        }catch(RuntimeException unavailable){return false;}
    }
}
