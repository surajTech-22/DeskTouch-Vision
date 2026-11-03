package com.desktouch.vision;

import android.content.*;
import android.os.Build;
import android.util.Log;

/** Rejoin known peers after device boot; never open the Android UI from boot. */
public final class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent) {
        String action=intent==null?null:intent.getAction();
        if(!Intent.ACTION_BOOT_COMPLETED.equals(action) &&
            !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action))return;
        if(!AutoConnectService.enabled(context))return;
        try{AutoConnectService.start(context);}
        catch(RuntimeException e){
            // OEM battery management or OS foreground restrictions may deny
            // background launch; next app opening resumes service normally.
            Log.w("DeskTouch","OS postponed boot reconnect",e);
        }
    }
}
