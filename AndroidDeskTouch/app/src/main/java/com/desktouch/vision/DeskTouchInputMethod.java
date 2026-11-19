package com.desktouch.vision;

import android.content.ComponentName;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
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
import android.view.inputmethod.InputMethodInfo;
import android.view.inputmethod.InputMethodManager;
import android.view.inputmethod.InputMethodSubtype;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.List;

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
        String nativeLabel=preferredNativeImeLabel(this);
        TextView hint=new TextView(this);
        hint.setText("DeskTouch keyboard receiver — remote typing works with "+nativeLabel+" too");
        hint.setTextColor(Color.WHITE);
        hint.setTextSize(14);
        layout.addView(hint);
        Button choose=new Button(this);
        choose.setText("Switch back to "+nativeLabel);
        choose.setAllCaps(false);
        choose.setOnClickListener(v->openKeyboardPicker(this));
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

    static boolean openKeyboardPicker(Context context) {
        if(context==null)return false;
        InputMethodManager manager=
            (InputMethodManager)context.getSystemService(Context.INPUT_METHOD_SERVICE);
        if(manager==null)return false;
        manager.showInputMethodPicker();
        return true;
    }

    static String preferredNativeImeLabel(Context context) {
        if(context==null)return "phone's normal keyboard";
        InputMethodManager manager=
            (InputMethodManager)context.getSystemService(Context.INPUT_METHOD_SERVICE);
        if(manager==null)return "phone's normal keyboard";
        List<InputMethodInfo> enabled=manager.getEnabledInputMethodList();
        if(enabled==null || enabled.isEmpty())return "phone's normal keyboard";

        ComponentName self=new ComponentName(context,DeskTouchInputMethod.class);
        String selfFull=self.flattenToString();
        String selfShort=self.flattenToShortString();
        String currentDefault=Settings.Secure.getString(context.getContentResolver(),
            Settings.Secure.DEFAULT_INPUT_METHOD);
        PackageManager pm=context.getPackageManager();

        if(currentDefault!=null && !currentDefault.isEmpty() &&
            !selfFull.equals(currentDefault) && !selfShort.equals(currentDefault)) {
            for(InputMethodInfo info : enabled) {
                if(info==null)continue;
                String id=info.getId();
                if(currentDefault.equals(id) || currentDefault.equals(info.getComponent().flattenToShortString())) {
                    CharSequence label=info.loadLabel(pm);
                    if(label!=null && label.length()>0)return label.toString();
                }
            }
        }

        InputMethodInfo fallbackSystem=null;
        InputMethodInfo fallbackAny=null;
        for(InputMethodInfo info : enabled) {
            if(info==null)continue;
            String id=info.getId();
            String shortId=info.getComponent().flattenToShortString();
            if(selfFull.equals(id) || selfShort.equals(shortId) ||
                context.getPackageName().equals(info.getPackageName())) {
                continue;
            }
            if(fallbackAny==null)fallbackAny=info;
            boolean isSystem=info.getServiceInfo()!=null &&
                info.getServiceInfo().applicationInfo!=null &&
                (info.getServiceInfo().applicationInfo.flags & ApplicationInfo.FLAG_SYSTEM)!=0;
            boolean hasKeyboardSubtype=hasKeyboardMode(info);
            if(isSystem && hasKeyboardSubtype) {
                CharSequence label=info.loadLabel(pm);
                if(label!=null && label.length()>0)return label.toString();
            }
            if(isSystem && fallbackSystem==null)fallbackSystem=info;
            if(hasKeyboardSubtype && fallbackAny==info)fallbackAny=info;
        }

        InputMethodInfo chosen=fallbackSystem!=null ? fallbackSystem : fallbackAny;
        if(chosen!=null) {
            CharSequence label=chosen.loadLabel(pm);
            if(label!=null && label.length()>0)return label.toString();
        }
        return "phone's normal keyboard";
    }

    private static boolean hasKeyboardMode(InputMethodInfo info) {
        int count=info.getSubtypeCount();
        if(count<=0)return true;
        for(int i=0;i<count;i++) {
            InputMethodSubtype subtype=info.getSubtypeAt(i);
            if(subtype!=null && "keyboard".equalsIgnoreCase(subtype.getMode())) {
                return true;
            }
        }
        return false;
    }

    private InputConnection approvedEditor() {
        boolean authorized=ShellInputClient.ready(this) || DeskTouchAccessibilityService.ready(this);
        if(!editorStarted || !authorized)return null;
        EditorInfo info=getCurrentInputEditorInfo();
        if(info==null ||
            !DeskTouchAccessibilityService.allowsDirectInputConnection(false,info.inputType))
            return null;
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
            " approved="+(ShellInputClient.ready(context) || DeskTouchAccessibilityService.ready(context));
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
        int keyCode=RemoteImeKeys.fullAndroidKeyCode(windowsVk);
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
