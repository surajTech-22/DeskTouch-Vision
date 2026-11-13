package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.os.*;
import android.provider.Settings;
import android.util.Log;
import java.util.Set;
import java.util.HashSet;
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
    static final String ACTION_PAUSE_BANKING="com.desktouch.vision.ACTION_PAUSE_BANKING";
    static final String ACTION_RESUME_CONTROL="com.desktouch.vision.ACTION_RESUME_CONTROL";
    private static final String CHANNEL="desktouch_connected_devices";
    private static final int NOTIFICATION_ID=37377;
    private static volatile AutoConnectService activeInstance;
    private ScheduledExecutorService scheduler;
    private MobileDiscoveryResponder responder;
    private TrustedWindows trust;
    private ConnectivityManager connectivityManager;
    private ConnectivityManager.NetworkCallback wifiCallback;
    private volatile boolean scanning;

    static boolean enabled(Context context) {
        return context.getSharedPreferences(PREFS, MODE_PRIVATE)
            .getBoolean(ENABLED,true);
    }
    static boolean reconnectEnabled(Context context) {
        return context.getSharedPreferences(PREFS, MODE_PRIVATE)
            .getBoolean(RECONNECT,true);
    }
    static void pausePeer(Context context,String fingerprint) {
        if(fingerprint==null || !fingerprint.matches("[A-Fa-f0-9]{64}"))return;
        android.content.SharedPreferences prefs=context.getSharedPreferences(PREFS,MODE_PRIVATE);
        Set<String> paused=new HashSet<>(prefs.getStringSet("pausedPeers",
            java.util.Collections.emptySet()));
        paused.add(fingerprint.toUpperCase(java.util.Locale.ROOT));
        prefs.edit().putStringSet("pausedPeers",paused).apply();
    }
    static void resumePeer(Context context,String fingerprint) {
        if(fingerprint==null || !fingerprint.matches("[A-Fa-f0-9]{64}"))return;
        android.content.SharedPreferences prefs=context.getSharedPreferences(PREFS,MODE_PRIVATE);
        Set<String> paused=new HashSet<>(prefs.getStringSet("pausedPeers",
            java.util.Collections.emptySet()));
        paused.remove(fingerprint.toUpperCase(java.util.Locale.ROOT));
        prefs.edit().putStringSet("pausedPeers",paused).apply();
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

    static void refreshNotification() {
        AutoConnectService service=activeInstance;
        if(service==null)return;
        NotificationManager manager=service.getSystemService(NotificationManager.class);
        if(manager!=null)manager.notify(NOTIFICATION_ID,service.buildNotification());
    }

    private Notification buildNotification() {
        Intent ui=new Intent(this,MainActivity.class);
        PendingIntent open=PendingIntent.getActivity(this,0,ui,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        boolean active=DeskTouchAccessibilityService.ready(this);
        boolean paused=DeskTouchAccessibilityService.isPausedForBanking(this);
        String subtitle=active
            ? "Connected & ready • Tap Pause before opening bank apps"
            : paused
                ? "Paused for banking • Wi-Fi stays ready"
                : "Discovering and reconnecting trusted devices on your LAN";
        Notification.Builder builder=new Notification.Builder(this,CHANNEL)
            .setSmallIcon(com.desktouch.vision.R.drawable.ic_desktouch)
            .setContentTitle("DeskTouch Vision")
            .setContentText(subtitle)
            .setContentIntent(open)
            .setOngoing(true);
        if(active) {
            Intent pause=new Intent(this,AutoConnectService.class)
                .setAction(ACTION_PAUSE_BANKING);
            PendingIntent pausePending=PendingIntent.getService(this,1,pause,
                PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
            builder.addAction(new Notification.Action.Builder(
                null,"Pause for Banking",pausePending).build());
        } else if(paused || !DeskTouchAccessibilityService.allowed(this)) {
            Intent resume=new Intent(this,AutoConnectService.class)
                .setAction(ACTION_RESUME_CONTROL);
            PendingIntent resumePending=PendingIntent.getService(this,2,resume,
                PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
            builder.addAction(new Notification.Action.Builder(
                null,"Resume Control",resumePending).build());
        }
        return builder.build();
    }

    @Override public void onCreate() {
        super.onCreate();
        activeInstance=this;
        trust=new TrustedWindows(this);
        NotificationManager manager=getSystemService(NotificationManager.class);
        NotificationChannel channel=new NotificationChannel(CHANNEL,
            "DeskTouch device sharing", NotificationManager.IMPORTANCE_LOW);
        manager.createNotificationChannel(channel);
        Notification notification=buildNotification();
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
        registerWifiReconnectCallback();
        // Updates are downloaded only from official GitHub and SHA-256 checked.
        // Android's own installer must still be approved by the user.
        scheduler.scheduleWithFixedDelay(
            ()->AndroidUpdater.checkBackground(getApplicationContext()),
            30,2*60*60,TimeUnit.SECONDS);
    }

    private void registerWifiReconnectCallback() {
        connectivityManager=getSystemService(ConnectivityManager.class);
        if(connectivityManager==null)return;
        try {
            NetworkRequest request=new NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .build();
            wifiCallback=new ConnectivityManager.NetworkCallback() {
                @Override public void onAvailable(Network network) {
                    triggerFastReconnect();
                }
            };
            connectivityManager.registerNetworkCallback(request,wifiCallback);
        } catch(Exception error) {
            Log.w("DeskTouch","Wi-Fi change callback unavailable",error);
        }
    }

    private void triggerFastReconnect() {
        ScheduledExecutorService pool=scheduler;
        if(pool!=null && !pool.isShutdown())
            pool.execute(this::autoConnect);
    }
    private void autoConnect() {
        if(scanning || !reconnectEnabled(this) || !enabled(this)) return;
        scanning=true;
        try {
            Set<String> verified=trust.fingerprints();
            Set<String> paused=getSharedPreferences(PREFS,MODE_PRIVATE)
                .getStringSet("pausedPeers",java.util.Collections.emptySet());
            if(verified.isEmpty()) return; // no silent first-time pairing
            android.graphics.Point display=PhoneScreenGeometry.read(this);
            // One logical environment => ONE TLS gateway session from this
            // Android device. Other Windows/phone members are authenticated
            // separately TO THAT HUB and routed by its environment roster.
            if(DeskTouchRuntime.connectedCount()>0)return;
            java.util.LinkedHashMap<String,java.util.List<DesktopDiscovery.Device>> groups=
                DesktopDiscovery.windowsGroups(DesktopDiscovery.scan());
            for(java.util.List<DesktopDiscovery.Device> members:groups.values()){
                java.util.Set<String> eligible=new java.util.HashSet<>(verified);
                eligible.removeAll(paused);
                DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                    members,java.util.Collections.emptySet(),eligible);
                if(gateway==null||!gateway.ready||
                    !eligible.contains(gateway.fingerprint))continue;
                if(DeskTouchRuntime.isConnecting(gateway.fingerprint))return;
                DeskTouchSession selected=DeskTouchRuntime.select(this,gateway.fingerprint);
                selected.connect(gateway,display.x,display.y);
                return; // never auto-open pairwise connections to group members
            }
        }catch(Exception error){
            Log.w("DeskTouch","Trusted LAN reconnect deferred",error);
        }finally{scanning=false;}
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        String action=intent!=null ? intent.getAction() : null;
        if(ACTION_PAUSE_BANKING.equals(action)) {
            DeskTouchAccessibilityService.pauseForBanking(this);
            refreshNotification();
        } else if(ACTION_RESUME_CONTROL.equals(action)) {
            DeskTouchAccessibilityService.setAllowed(this,true);
            refreshNotification();
            Intent settings=new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            try { startActivity(settings); } catch(RuntimeException ignored) {}
        }
        if(!enabled(this)) { stopSelf();return START_NOT_STICKY; }
        return START_STICKY;
    }
    @Override public android.os.IBinder onBind(Intent intent) {return null;}
    @Override public void onDestroy() {
        if(activeInstance==this)activeInstance=null;
        if(connectivityManager!=null && wifiCallback!=null) {
            try { connectivityManager.unregisterNetworkCallback(wifiCallback); }
            catch(Exception ignored) {}
        }
        if(scheduler!=null)scheduler.shutdownNow();
        if(responder!=null)responder.close();
        // An active Android UI may still hold this same session when the user
        // disables startup. Stop background work without closing their UI session.
        super.onDestroy();
    }
}
