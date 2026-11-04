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
        }
        Log.i("DeskTouchExternalEditor","IME_REQUEST command="+type+
            " accepted="+accepted+" "+
            DeskTouchInputMethod.stateForTesting(context));
    }
}
