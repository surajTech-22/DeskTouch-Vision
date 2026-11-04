package com.desktouch.vision;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.PowerManager;
import android.util.Log;

/**
 * Power/network resources are held ONLY for an explicitly approved Windows
 * -> Android input lease, never merely for an idle paired TCP connection.
 * They are released on native touch, lease revocation, or disconnect.
 */
final class RemoteInputPower {
    private static PowerManager.WakeLock cpu;
    private static WifiManager.WifiLock lowLatencyWifi;
    private RemoteInputPower() {}

    static synchronized void begin(Context app) {
        Context context=app.getApplicationContext();
        if(cpu==null) {
            try {
                PowerManager power=(PowerManager)context.getSystemService(Context.POWER_SERVICE);
                if(power!=null) {
                    cpu=power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,
                        "DeskTouchVision:RemoteInputLease");
                    cpu.setReferenceCounted(false);
                }
            }catch(RuntimeException e){Log.w("DeskTouchPower","CPU wake lock unavailable",e);}
        }
        try {if(cpu!=null&&!cpu.isHeld())cpu.acquire();}
        catch(RuntimeException e){Log.w("DeskTouchPower","CPU wake lock denied",e);}
        if(lowLatencyWifi==null) {
            try {
                WifiManager wifi=(WifiManager)context.getApplicationContext()
                    .getSystemService(Context.WIFI_SERVICE);
                if(wifi!=null) {
                    int mode=Build.VERSION.SDK_INT>=29
                        ?WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                        :WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                    lowLatencyWifi=wifi.createWifiLock(mode,
                        "DeskTouchVision:LowLatencyRemoteInput");
                    lowLatencyWifi.setReferenceCounted(false);
                }
            }catch(RuntimeException e){Log.w("DeskTouchPower","Wi-Fi lock unavailable",e);}
        }
        try {if(lowLatencyWifi!=null&&!lowLatencyWifi.isHeld())lowLatencyWifi.acquire();}
        catch(RuntimeException e){Log.w("DeskTouchPower","Wi-Fi low-latency mode denied",e);}
    }

    static synchronized void end() {
        try {if(lowLatencyWifi!=null&&lowLatencyWifi.isHeld())lowLatencyWifi.release();}
        catch(RuntimeException e){Log.w("DeskTouchPower","Wi-Fi release failed",e);}
        try {if(cpu!=null&&cpu.isHeld())cpu.release();}
        catch(RuntimeException e){Log.w("DeskTouchPower","CPU release failed",e);}
    }
}
