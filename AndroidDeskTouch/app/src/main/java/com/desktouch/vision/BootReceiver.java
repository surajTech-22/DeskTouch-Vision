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
        // Android owns Accessibility authorization. If Xiaomi/HyperOS
        // disables the service after an in-place update, notify the user
        // rather than silently pretending permission has survived.
        if(Intent.ACTION_MY_PACKAGE_REPLACED.equals(action))
            try{PermissionContinuity.onPackageReplaced(context);}
            catch(RuntimeException restricted){
                // User may have denied notification permission or Xiaomi may
                // block this background notification. Never break trust
                // continuity/reconnection because a reminder was blocked.
                Log.w("DeskTouch","Post-update permission reminder restricted",restricted);
            }
        if(!AutoConnectService.enabled(context))return;
        try{AutoConnectService.start(context);}
        catch(RuntimeException e){
            // OEM battery management or OS foreground restrictions may deny
            // background launch; next app opening resumes service normally.
            Log.w("DeskTouch","OS postponed boot reconnect",e);
        }
    }
}
