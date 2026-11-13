package com.desktouch.vision;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * Available exclusively in debug builds. ADB test harness submits a synthetic
 * packet to the same IME endpoint as DeskTouch's approved remote-input path.
 * Never included in release packages; never exposed in the production UI.
 */
public final class DebugRemoteInputReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent message) {
        if(!"com.desktouch.vision.DEBUG_SEND_INPUT".equals(message.getAction()))
            return;
        String type=message.getStringExtra("command");
        boolean accepted=false;
        if("text".equals(type)) {
            String value=message.getStringExtra("value");
            if(value!=null && value.length()>0 && value.length()<=64)
                accepted=DeskTouchInputMethod.commitRemoteText(value);
        } else if("key".equals(type)) {
            accepted=DeskTouchInputMethod.sendRemoteKey(
                message.getIntExtra("vk",0));
        } else if("reverse-text".equals(type) || "reverse-key".equals(type)) {
            // Debug test uses the exact Android-side input service handlers
            // that authenticated, authority-checked DeskTouchSession calls.
            // This doesn't bypass the production session authentication path.
            if(DeskTouchAccessibilityService.ready(context)) {
                accepted=DeskTouchAccessibilityService.pointer(25,25,null);
                if(accepted && "reverse-text".equals(type)) {
                    String value=message.getStringExtra("value");
                    if(value==null || value.isEmpty() || value.length()>64)
                        accepted=false;
                    else DeskTouchAccessibilityService.text(value,()->
                        Log.w("DeskTouchExternalEditor","REVERSE_INPUT_REJECTED type=text"));
                } else if(accepted) {
                    int vk=message.getIntExtra("vk",0);
                    DeskTouchAccessibilityService.key(vk,true,()->
                        Log.w("DeskTouchExternalEditor","REVERSE_INPUT_REJECTED type=key"));
                    DeskTouchAccessibilityService.key(vk,false,null);
                }
            }
        }
        Log.i("DeskTouchExternalEditor","IME_REQUEST command="+type+
            " accepted="+accepted+" "+
            DeskTouchInputMethod.stateForTesting(context));
    }
}
