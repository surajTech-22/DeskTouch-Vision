package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.os.*;
import android.util.Log;
import java.util.Set;
import java.util.concurrent.*;

/**
 * Android-compliant auto-start: a connectedDevice foreground service rather
 * than launching an Activity from BOOT_COMPLETED. It only connects to
 * previously authorized persistent TLS identities. No IP is saved and no
 * input authority is taken automatically.
 */
public final class AutoConnectService extends Service {
    static final String PREFS="desktouch_startup";
    static final String ENABLED="enabled";
    static final String RECONNECT="reconnect";
    private static final String CHANNEL="desktouch_connected_devices";
    private static final int NOTIFICATION_ID=37377;
    private ScheduledExecutorService scheduler;
    private MobileDiscoveryResponder responder;
    private TrustedWindows trust;
    private volatile boolean scanning;

    static boolean enabled(Context context) {
        return context.getSharedPreferences(PREFS, MODE_PRIVATE)
            .getBoolean(ENABLED,true);
    }
    static boolean reconnectEnabled(Context context) {
        return context.getSharedPreferences(PREFS, MODE_PRIVATE)
            .getBoolean(RECONNECT,true);
    }
    static void setReconnect(Context context,boolean value) {
        context.getSharedPreferences(PREFS, MODE_PRIVATE).edit()
            .putBoolean(RECONNECT,value).apply();
    }
    static void setEnabled(Context context, boolean enabled) {
        context.getSharedPreferences(PREFS, MODE_PRIVATE).edit()
            .putBoolean(ENABLED,enabled).apply();
        if(enabled) start(context);
        else context.stopService(new Intent(context,AutoConnectService.class));
    }
    static void start(Context context) {
        if(!enabled(context)) return;
        Intent intent=new Intent(context,AutoConnectService.class);
        if(Build.VERSION.SDK_INT>=26) context.startForegroundService(intent);
        else context.startService(intent);
    }

    @Override public void onCreate() {
        super.onCreate();
        trust=new TrustedWindows(this);
        DeskTouchRuntime.session(this);
        NotificationManager manager=getSystemService(NotificationManager.class);
        NotificationChannel channel=new NotificationChannel(CHANNEL,
            "DeskTouch device sharing", NotificationManager.IMPORTANCE_LOW);
        manager.createNotificationChannel(channel);
        Intent ui=new Intent(this,MainActivity.class);
        PendingIntent open=PendingIntent.getActivity(this,0,ui,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification notification=new Notification.Builder(this,CHANNEL)
            .setSmallIcon(com.desktouch.vision.R.drawable.ic_desktouch)
            .setContentTitle("DeskTouch Vision")
            .setContentText("Discovering and reconnecting trusted devices on your LAN")
            .setContentIntent(open)
            .setOngoing(true).build();
        if(Build.VERSION.SDK_INT>=29)
            startForeground(NOTIFICATION_ID,notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        else startForeground(NOTIFICATION_ID,notification);
        responder=new MobileDiscoveryResponder();
        try { responder.start(this); }
        catch(Exception error) {
            Log.w("DeskTouch","Android discovery responder unavailable",error);
        }
        scheduler=Executors.newSingleThreadScheduledExecutor();
        scheduler.scheduleWithFixedDelay(this::autoConnect,0,10,TimeUnit.SECONDS);
    }
    private void autoConnect() {
        if(scanning || !reconnectEnabled(this) || !enabled(this)) return;
        DeskTouchSession session=DeskTouchRuntime.session(this);
        if(session.isConnected() || session.isConnecting())return;
        scanning=true;
        try {
            Set<String> verified=trust.fingerprints();
            if(verified.isEmpty()) return; // no silent first-time pairing
            for(DesktopDiscovery.Device device:DesktopDiscovery.scan()) {
                if(!verified.contains(device.fingerprint))continue;
                if(session.isConnected()||session.isConnecting())break;
                android.util.DisplayMetrics display=getResources().getDisplayMetrics();
                session.connect(device,Math.max(200,display.widthPixels),
                    Math.max(100,display.heightPixels));
                break;
            }
        }catch(Exception error){
            Log.w("DeskTouch","Trusted LAN reconnect deferred",error);
        }finally{scanning=false;}
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(!enabled(this)) { stopSelf();return START_NOT_STICKY; }
        return START_STICKY;
    }
    @Override public android.os.IBinder onBind(Intent intent) {return null;}
    @Override public void onDestroy() {
        if(scheduler!=null)scheduler.shutdownNow();
        if(responder!=null)responder.close();
        DeskTouchRuntime.clear();
        super.onDestroy();
    }
}
