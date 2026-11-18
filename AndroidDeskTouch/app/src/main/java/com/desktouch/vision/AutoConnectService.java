package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.wifi.WifiManager;
import android.os.*;
import android.provider.Settings;
import android.util.Log;
import android.widget.Toast;
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
    private static final String PAYMENT_NOTICE_CHANNEL="desktouch_payment_mode";
    private static final int NOTIFICATION_ID=37377;
    private static final int PAYMENT_NOTICE_ID=37378;
    private static final long DISCONNECT_AUTO_PAUSE_DELAY_MS=15_000L;
    private static final long HUB_RECHECK_INTERVAL_MS=8_000L;
    private static volatile AutoConnectService activeInstance;
    private ScheduledExecutorService scheduler;
    private MobileDiscoveryResponder responder;
    private TrustedWindows trust;
    private ConnectivityManager connectivityManager;
    private ConnectivityManager.NetworkCallback wifiCallback;
    private BroadcastReceiver wakeReceiver;
    private volatile boolean scanning;
    private volatile boolean pendingWake;
    private volatile boolean retryScheduled;
    private volatile boolean hadConnectedWithAccessibility;
    private volatile String lastAutoConnectSkipReason="";
    private volatile long lastHubCheckAtMs;
    private volatile WifiLinkMonitor.LinkSnapshot lastLinkSnapshot;
    private CellularLinkHelper cellularHelper;
    private ExecutorService cellularExecutor;
    private final java.util.concurrent.atomic.AtomicBoolean cellularProbeInFlight =
        new java.util.concurrent.atomic.AtomicBoolean();
    private volatile boolean pendingCellularEval;
    private volatile long lastCellularProbeAtMs;
    private final java.util.concurrent.atomic.AtomicInteger networkEpoch=
        new java.util.concurrent.atomic.AtomicInteger();
    private ScheduledFuture<?> disconnectAutoPauseFuture;

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

    static void nudgeReconnect(Context context) {
        AutoConnectService service=activeInstance;
        if(service!=null) {
            service.triggerFastReconnect();
            return;
        }
        if(context!=null && enabled(context) && reconnectEnabled(context)) {
            start(context.getApplicationContext());
        }
    }

    static void onConnectionCountChanged(Context context) {
        AutoConnectService service=activeInstance;
        if(service!=null) service.evaluateDisconnectAutoPause();
        refreshNotification();
    }

    static void onMobileUdpCredentialsReady(Context context) {
        AutoConnectService service = activeInstance;
        if (service != null) service.scheduleCellularEvaluation();
    }

    static void scheduleMobileRetry(Context context, long delayMs) {
        AutoConnectService service = activeInstance;
        if (service == null) return;
        ScheduledExecutorService pool = service.scheduler;
        if (pool == null || pool.isShutdown()) {
            service.scheduleCellularEvaluation();
            return;
        }
        pool.schedule(service::scheduleCellularEvaluation, Math.max(250L, delayMs), TimeUnit.MILLISECONDS);
    }

    static void onLeaseEndedDegraded(Context context) {
        AutoConnectService service=activeInstance;
        if(service==null) return;
        ScheduledExecutorService pool=service.scheduler;
        if(pool==null || pool.isShutdown()) {
            service.scheduleCellularEvaluation();
            return;
        }
        pool.schedule(service::scheduleCellularEvaluation,900,TimeUnit.MILLISECONDS);
    }

    static void onAccessibilityStateChanged(Context context) {
        AutoConnectService service=activeInstance;
        if(service!=null) service.evaluateDisconnectAutoPause();
    }

    static void cancelPaymentNotice(Context context) {
        if(context==null) return;
        NotificationManager manager=context.getSystemService(NotificationManager.class);
        if(manager!=null) {
            try { manager.cancel(PAYMENT_NOTICE_ID); }
            catch(RuntimeException ignored) {}
        }
    }

    static void notifyAutoPausedForPayment(Context context, String appName) {
        if(context==null) return;
        Context app=context.getApplicationContext();
        String safeName=(appName!=null && !appName.isEmpty()) ? appName : "payment app";
        AutoConnectService service=activeInstance;
        if(service!=null) {
            service.postPaymentModeNotice(
                "DeskTouch Accessibility turned off",
                "Stepped aside for " + safeName + " • Tap Resume when back at Windows");
        } else {
            new Handler(Looper.getMainLooper()).post(()->
                Toast.makeText(app,
                    "DeskTouch Accessibility turned off for " + safeName,
                    Toast.LENGTH_SHORT).show());
        }
    }

    private synchronized void cancelDisconnectAutoPauseLocked() {
        if(disconnectAutoPauseFuture!=null) {
            disconnectAutoPauseFuture.cancel(false);
            disconnectAutoPauseFuture=null;
        }
    }

    private synchronized void evaluateDisconnectAutoPause() {
        int connected=DeskTouchRuntime.connectedCount();
        boolean sysEnabled=PermissionContinuity.systemAccessibilityEnabled(this);
        if(!sysEnabled) {
            cancelDisconnectAutoPauseLocked();
            hadConnectedWithAccessibility=false;
            return;
        }
        if(connected>0) {
            hadConnectedWithAccessibility=true;
            cancelDisconnectAutoPauseLocked();
            return;
        }
        if(!hadConnectedWithAccessibility ||
            !DeskTouchAccessibilityService.autoPauseOnDisconnectEnabled(this)) {
            cancelDisconnectAutoPauseLocked();
            return;
        }
        ScheduledExecutorService pool=scheduler;
        if(pool==null || pool.isShutdown()) return;
        if(disconnectAutoPauseFuture!=null && !disconnectAutoPauseFuture.isDone()) return;
        disconnectAutoPauseFuture=pool.schedule(()->{
            synchronized(AutoConnectService.this) {
                disconnectAutoPauseFuture=null;
            }
            if(DeskTouchRuntime.connectedCount()>0) return;
            if(!DeskTouchAccessibilityService.autoPauseOnDisconnectEnabled(this)) return;
            if(!PermissionContinuity.systemAccessibilityEnabled(this)) {
                hadConnectedWithAccessibility=false;
                return;
            }
            hadConnectedWithAccessibility=false;
            Log.i("DeskTouch","Auto-turning off Accessibility 15s after Windows disconnected");
            DeskTouchAccessibilityService.pauseForBanking(
                this,"Auto-turned off after Windows disconnected");
            postPaymentModeNotice(
                "DeskTouch Accessibility turned off",
                "Windows disconnected • Payment apps work normally");
        },DISCONNECT_AUTO_PAUSE_DELAY_MS,TimeUnit.MILLISECONDS);
    }

    private void postPaymentModeNotice(String title,String body) {
        if(Build.VERSION.SDK_INT>=33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                !=android.content.pm.PackageManager.PERMISSION_GRANTED) {
            new Handler(Looper.getMainLooper()).post(()->
                Toast.makeText(getApplicationContext(),title + " • " + body,Toast.LENGTH_SHORT).show());
            return;
        }
        NotificationManager manager=getSystemService(NotificationManager.class);
        if(manager==null) return;
        Intent resume=new Intent(this,AutoConnectService.class)
            .setAction(ACTION_RESUME_CONTROL);
        PendingIntent resumePending=PendingIntent.getService(this,3,resume,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Intent openUi=new Intent(this,MainActivity.class);
        PendingIntent openPending=PendingIntent.getActivity(this,4,openUi,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification notice=new Notification.Builder(this,PAYMENT_NOTICE_CHANNEL)
            .setSmallIcon(com.desktouch.vision.R.drawable.ic_desktouch)
            .setContentTitle(title)
            .setContentText(body)
            .setContentIntent(openPending)
            .setAutoCancel(true)
            .addAction(new Notification.Action.Builder(
                null,"Resume Control",resumePending).build())
            .build();
        try { manager.notify(PAYMENT_NOTICE_ID,notice); }
        catch(RuntimeException ignored) {}
    }

    private Notification buildNotification() {
        Intent ui=new Intent(this,MainActivity.class);
        PendingIntent open=PendingIntent.getActivity(this,0,ui,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        boolean sysEnabled=PermissionContinuity.systemAccessibilityEnabled(this);
        boolean shellReady=ShellInputClient.ready(this);
        boolean active=shellReady || DeskTouchAccessibilityService.ready(this);
        boolean paused=DeskTouchAccessibilityService.isPausedForBanking(this);
        String subtitle=shellReady
            ? (sysEnabled
                ? "Virtual Bridge ACTIVE • Accessibility still ON (tap Turn Off for Payments)"
                : "Accessibility OFF • Virtual Bridge ACTIVE (mouse & keyboard ready)")
            : sysEnabled
            ? (active
                ? "Accessibility ON • Auto-turns off for payment apps & on disconnect"
                : "Accessibility ON in Android Settings • Tap Turn Off for Payments")
            : paused
                ? "Payment-safe mode (Accessibility OFF) • Connections stay ready"
                : "Accessibility OFF (payment apps work) • Connections stay ready";
        Notification.Builder builder=new Notification.Builder(this,CHANNEL)
            .setSmallIcon(com.desktouch.vision.R.drawable.ic_desktouch)
            .setContentTitle("DeskTouch Vision")
            .setContentText(subtitle)
            .setContentIntent(open)
            .setOngoing(true);
        if(sysEnabled) {
            Intent pause=new Intent(this,AutoConnectService.class)
                .setAction(ACTION_PAUSE_BANKING);
            PendingIntent pausePending=PendingIntent.getService(this,1,pause,
                PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
            builder.addAction(new Notification.Action.Builder(
                null,"Turn Off for Payments",pausePending).build());
        } else if(!shellReady) {
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
        cellularHelper=new CellularLinkHelper(this);
        cellularExecutor=Executors.newSingleThreadExecutor(r->{
            Thread t=new Thread(r,"DeskTouch CellularProbe");
            t.setDaemon(true);
            return t;
        });
        hadConnectedWithAccessibility=
            DeskTouchRuntime.connectedCount()>0 &&
            PermissionContinuity.systemAccessibilityEnabled(this);
        NotificationManager manager=getSystemService(NotificationManager.class);
        NotificationChannel channel=new NotificationChannel(CHANNEL,
            "DeskTouch device sharing", NotificationManager.IMPORTANCE_LOW);
        manager.createNotificationChannel(channel);
        NotificationChannel paymentChannel=new NotificationChannel(PAYMENT_NOTICE_CHANNEL,
            "DeskTouch payment-safe mode", NotificationManager.IMPORTANCE_LOW);
        paymentChannel.setShowBadge(false);
        manager.createNotificationChannel(paymentChannel);
        Notification notification=buildNotification();
        if(Build.VERSION.SDK_INT>=29)
            startForeground(NOTIFICATION_ID,notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        else startForeground(NOTIFICATION_ID,notification);
        responder=new MobileDiscoveryResponder();
        responder.setOnlineListener(device->{
            if(device==null || trust==null) return;
            Set<String> paused=getSharedPreferences(PREFS,MODE_PRIVATE)
                .getStringSet("pausedPeers",java.util.Collections.emptySet());
            if(trust.fingerprints().contains(device.fingerprint) &&
                !paused.contains(device.fingerprint))
                triggerFastReconnect();
        });
        try { responder.start(this); }
        catch(Exception error) {
            Log.w("DeskTouch","Android discovery responder unavailable",error);
        }
        scheduler=Executors.newSingleThreadScheduledExecutor();
        scheduler.scheduleWithFixedDelay(this::autoConnect,0,10,TimeUnit.SECONDS);
        registerWifiReconnectCallback();
        registerWakeReceivers();
        // Updates are downloaded only from official GitHub and SHA-256 checked.
        // Android's own installer must still be approved by the user.
        scheduler.scheduleWithFixedDelay(
            ()->AndroidUpdater.checkBackground(getApplicationContext()),
            30,2*60*60,TimeUnit.SECONDS);
    }

    static void onCableBridgeReadyHint(Context context, String fingerprint) {
        AutoConnectService service=activeInstance;
        if(service!=null)service.scheduleCableAttachSweep();
    }

    private void handleNetworkTransition(String reason) {
        applyObservedNetworkChange(false);
    }

    private void handleNetworkCapabilitiesChanged(Network network, NetworkCapabilities caps) {
        applyObservedNetworkChange(true);
    }

    private void applyObservedNetworkChange(boolean capabilitiesOnly) {
        WifiLinkMonitor.LinkSnapshot prev=lastLinkSnapshot;
        WifiLinkMonitor.LinkSnapshot snap=WifiLinkMonitor.inspect(
            this,DeskTouchRuntime.anyCableConnected(),networkEpoch.get());
        boolean radioPathChanged = prev==null || !snap.sameRadioPath(prev);
        if(!radioPathChanged && !snap.significantlyDifferentForReporting(prev)) {
            if(!capabilitiesOnly && DeskTouchRuntime.connectedCount()==0) {
                triggerFastReconnect();
            }
            return;
        }
        if(radioPathChanged) {
            snap=WifiLinkMonitor.inspect(
                this,DeskTouchRuntime.anyCableConnected(),networkEpoch.incrementAndGet());
        }
        lastLinkSnapshot=snap;
        DeskTouchRuntime.notifyNetworkProfile(this,snap);
        if(!capabilitiesOnly && (radioPathChanged || DeskTouchRuntime.connectedCount()==0)) {
            triggerFastReconnect();
        }
    }

    private void handleNetworkLost() {
        WifiLinkMonitor.LinkSnapshot snap=WifiLinkMonitor.inspect(
            this,DeskTouchRuntime.anyCableConnected(),networkEpoch.incrementAndGet());
        lastLinkSnapshot=snap;
        DeskTouchRuntime.notifyNetworkProfile(this,snap);
        triggerFastReconnect();
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
                    handleNetworkTransition("available");
                }
                @Override public void onCapabilitiesChanged(Network network, NetworkCapabilities caps) {
                    handleNetworkCapabilitiesChanged(network, caps);
                }
                @Override public void onLinkPropertiesChanged(Network network, android.net.LinkProperties props) {
                    handleNetworkTransition("link-properties");
                }
                @Override public void onLost(Network network) {
                    handleNetworkLost();
                }
            };
            connectivityManager.registerNetworkCallback(request,wifiCallback);
        } catch(Exception error) {
            Log.w("DeskTouch","Wi-Fi change callback unavailable",error);
        }
        lastLinkSnapshot=WifiLinkMonitor.inspect(this,DeskTouchRuntime.anyCableConnected(),networkEpoch.get());
    }

    private void registerWakeReceivers() {
        IntentFilter filter=new IntentFilter();
        filter.addAction(Intent.ACTION_SCREEN_ON);
        filter.addAction(Intent.ACTION_USER_PRESENT);
        filter.addAction(Intent.ACTION_POWER_CONNECTED);
        filter.addAction(Intent.ACTION_POWER_DISCONNECTED);
        filter.addAction("android.hardware.usb.action.USB_STATE");
        filter.addAction(WifiManager.NETWORK_STATE_CHANGED_ACTION);
        wakeReceiver=new BroadcastReceiver() {
            @Override public void onReceive(Context context,Intent intent) {
                String action=intent!=null ? intent.getAction() : null;
                if("android.hardware.usb.action.USB_STATE".equals(action)) {
                    boolean connected=intent.getBooleanExtra("connected",false);
                    boolean configured=intent.getBooleanExtra("configured",false);
                    if(!connected && !configured) {
                        if(DeskTouchRuntime.closeCableSessions()) {
                            triggerCableDetachRecovery();
                            return;
                        }
                    } else {
                        scheduleCableAttachSweep();
                    }
                } else if(Intent.ACTION_POWER_DISCONNECTED.equals(action)) {
                    if(DeskTouchRuntime.anyCableConnected()) {
                        verifyCableOrFailover();
                        return;
                    }
                } else if(Intent.ACTION_POWER_CONNECTED.equals(action)) {
                    scheduleCableAttachSweep();
                } else if(WifiManager.NETWORK_STATE_CHANGED_ACTION.equals(action)) {
                    handleNetworkTransition("wifi-state");
                    return;
                }
                triggerFastReconnect();
            }
        };
        try {
            if(Build.VERSION.SDK_INT>=33)
                registerReceiver(wakeReceiver,filter,Context.RECEIVER_NOT_EXPORTED);
            else
                registerReceiver(wakeReceiver,filter);
        } catch(Exception error) {
            wakeReceiver=null;
            Log.w("DeskTouch","Screen/Wi-Fi wake receiver unavailable",error);
        }
    }

    private volatile long lastUsbFollowUpMs;

    private void scheduleCableAttachSweep() {
        ScheduledExecutorService pool=scheduler;
        if(pool==null || pool.isShutdown()) return;
        long now=android.os.SystemClock.uptimeMillis();
        if(now-lastUsbFollowUpMs<400L) return;
        lastUsbFollowUpMs=now;
        int[] delaysMs={150,600,1300,2400};
        for(int delay:delaysMs) {
            pool.schedule(()->{
                if(!DeskTouchRuntime.anyCableConnected()) autoConnect();
            },delay,TimeUnit.MILLISECONDS);
        }
    }

    private void verifyCableOrFailover() {
        ScheduledExecutorService pool=scheduler;
        if(pool==null || pool.isShutdown()) return;
        pool.schedule(()->{
            if(!DeskTouchRuntime.anyCableConnected()) return;
            DesktopDiscovery.Device loopback=DesktopDiscovery.probeLoopbackCable();
            if(loopback==null && DeskTouchRuntime.closeCableSessions()) {
                triggerCableDetachRecovery();
            }
        },180,TimeUnit.MILLISECONDS);
    }

    private void triggerCableDetachRecovery() {
        ScheduledExecutorService pool=scheduler;
        if(pool==null || pool.isShutdown()) return;
        pool.execute(this::autoConnect);
    }

    private void triggerFastReconnect() {
        if(scanning) pendingWake=true;
        ScheduledExecutorService pool=scheduler;
        if(pool!=null && !pool.isShutdown())
            pool.execute(this::autoConnect);
    }

    private void scheduleInteractiveFollowUp() {
        ScheduledExecutorService pool=scheduler;
        if(pool==null || pool.isShutdown() || retryScheduled) return;
        PowerManager power=getSystemService(PowerManager.class);
        if(power!=null && !power.isInteractive()) return;
        retryScheduled=true;
        pool.schedule(()->{
            retryScheduled=false;
            autoConnect();
        },900,TimeUnit.MILLISECONDS);
    }

    private void logSkipReasonOnce(String reason) {
        if(reason==null || reason.equals(lastAutoConnectSkipReason)) return;
        lastAutoConnectSkipReason=reason;
        Log.i("DeskTouch","Auto-connect idle: "+reason);
    }

    static boolean shouldAcceptCachedDirectPeer(DesktopDiscovery.Device direct, int eligibleCount) {
        return direct != null && direct.ready && !direct.android && (direct.isHub || eligibleCount <= 1);
    }

    static boolean shouldSwitchWirelessPeerToHub(
            boolean cableConnected, boolean leaseActive, int eligibleCount,
            DesktopDiscovery.Device currentDirect, DesktopDiscovery.Device gateway) {
        if (cableConnected || leaseActive || eligibleCount <= 1 || currentDirect == null || gateway == null || !gateway.ready)
            return false;
        if (gateway.fingerprint.equalsIgnoreCase(currentDirect.fingerprint))
            return false;
        if (!currentDirect.isHub && gateway.isHub)
            return true;
        return gateway.isHub && gateway.hubMemberCount > currentDirect.hubMemberCount;
    }

    private DesktopDiscovery.Device findCachedDirectHub(Set<String> eligible) {
        java.util.LinkedHashSet<String> ordered = new java.util.LinkedHashSet<>();
        String preferred = DeskTouchRuntime.selectedIdentity();
        if (preferred != null && eligible.contains(preferred)) ordered.add(preferred);
        ordered.addAll(eligible);
        for (String fp : ordered) {
            String cachedIp = DeskTouchRuntime.getLastKnownPeerHost(fp);
            DesktopDiscovery.Device direct = DesktopDiscovery.probeDirectHost(cachedIp, eligible);
            if (shouldAcceptCachedDirectPeer(direct, eligible.size()))
                return direct;
        }
        return null;
    }

    private void autoConnect() {
        if(scanning) return;
        if(!enabled(this)) { logSkipReasonOnce("service-disabled"); return; }
        if(!reconnectEnabled(this)) { logSkipReasonOnce("reconnect-disabled"); return; }
        scanning=true;
        boolean connectedNow=false;
        try {
            Set<String> verified=trust.fingerprints();
            Set<String> paused=getSharedPreferences(PREFS,MODE_PRIVATE)
                .getStringSet("pausedPeers",java.util.Collections.emptySet());
            Set<String> eligible=new HashSet<>(verified);
            eligible.removeAll(paused);
            if(eligible.isEmpty()) {
                logSkipReasonOnce("no-eligible-peers verified="+verified.size()+" paused="+paused.size());
                return; // no silent first-time pairing
            }
            lastAutoConnectSkipReason="";
            android.graphics.Point display=PhoneScreenGeometry.read(this);
            // One logical environment => ONE TLS gateway session from this
            // Android device. Other Windows/phone members are authenticated
            // separately TO THAT HUB and routed by its environment roster.
            if(DeskTouchRuntime.connectedCount()>0) {
                if(!DeskTouchRuntime.anyCableConnected() && !DeskTouchRuntime.anyInputLeaseActive()) {
                    DesktopDiscovery.Device cable=DesktopDiscovery.probeLoopbackCable();
                    if(cable!=null && cable.ready && eligible.contains(cable.fingerprint)) {
                        DeskTouchRuntime.upgradeToCableMakeBeforeBreak(
                            this,cable,display.x,display.y,()->{
                                if(reconnectEnabled(this) && enabled(this))
                                    scheduleInteractiveFollowUp();
                            });
                    } else if(eligible.size()>1) {
                        long nowUp=android.os.SystemClock.uptimeMillis();
                        if(nowUp-lastHubCheckAtMs>=HUB_RECHECK_INTERVAL_MS) {
                            lastHubCheckAtMs=nowUp;
                            String currentFp=DeskTouchRuntime.connectedNonCableFingerprint();
                            String currentIp=currentFp==null ? null : DeskTouchRuntime.getLastKnownPeerHost(currentFp);
                            DesktopDiscovery.Device currentDirect=DesktopDiscovery.probeDirectHost(currentIp,eligible);
                            if(currentDirect!=null) {
                                java.util.LinkedHashMap<String,java.util.List<DesktopDiscovery.Device>> groups=
                                    DesktopDiscovery.windowsGroups(DesktopDiscovery.scanFast(eligible));
                                for(java.util.List<DesktopDiscovery.Device> members:groups.values()) {
                                    DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                                        members,java.util.Collections.emptySet(),eligible);
                                    if(shouldSwitchWirelessPeerToHub(false,false,eligible.size(),currentDirect,gateway)
                                        && eligible.contains(gateway.fingerprint)
                                        && !DeskTouchRuntime.isConnecting(gateway.fingerprint)) {
                                        DeskTouchRuntime.switchToHubMakeBeforeBreak(
                                            this,gateway,display.x,display.y,()->{
                                                if(reconnectEnabled(this) && enabled(this))
                                                    scheduleInteractiveFollowUp();
                                            });
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                connectedNow=true;
                return;
            }
            DesktopDiscovery.Device cableDirect=DesktopDiscovery.probeLoopbackCable();
            if(cableDirect!=null && cableDirect.ready && eligible.contains(cableDirect.fingerprint)
                && !DeskTouchRuntime.isConnecting(cableDirect.fingerprint)) {
                DeskTouchSession selected=DeskTouchRuntime.select(this,cableDirect.fingerprint);
                selected.connect(cableDirect,display.x,display.y,false,()->{
                    if(reconnectEnabled(this) && enabled(this))
                        scheduleInteractiveFollowUp();
                });
                connectedNow=true;
                return;
            }
            DesktopDiscovery.Device cachedHub=findCachedDirectHub(eligible);
            if(cachedHub!=null && !DeskTouchRuntime.isConnecting(cachedHub.fingerprint)) {
                DeskTouchSession selected=DeskTouchRuntime.select(this,cachedHub.fingerprint);
                selected.connect(cachedHub,display.x,display.y,false,()->{
                    if(reconnectEnabled(this) && enabled(this))
                        scheduleInteractiveFollowUp();
                });
                connectedNow=true;
                return;
            }
            java.util.LinkedHashMap<String,java.util.List<DesktopDiscovery.Device>> groups=
                DesktopDiscovery.windowsGroups(DesktopDiscovery.scanFast(eligible));
            for(java.util.List<DesktopDiscovery.Device> members:groups.values()){
                DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                    members,java.util.Collections.emptySet(),eligible);
                if(gateway==null||!gateway.ready||
                    !eligible.contains(gateway.fingerprint))continue;
                if(DeskTouchRuntime.isConnecting(gateway.fingerprint)) {
                    connectedNow=true;
                    return;
                }
                DeskTouchSession selected=DeskTouchRuntime.select(this,gateway.fingerprint);
                selected.connect(gateway,display.x,display.y,false,()->{
                    if(reconnectEnabled(this) && enabled(this))
                        scheduleInteractiveFollowUp();
                });
                connectedNow=true;
                return; // never auto-open pairwise connections to group members
            }
        }catch(Exception error){
            Log.w("DeskTouch","Trusted LAN reconnect deferred",error);
        }finally{
            scanning=false;
            scheduleCellularEvaluation();
            if(!connectedNow) {
                if(pendingWake) {
                    pendingWake=false;
                    triggerFastReconnect();
                } else if(reconnectEnabled(this) && enabled(this)) {
                    scheduleInteractiveFollowUp();
                }
            } else {
                pendingWake=false;
            }
        }
    }

    private void scheduleCellularEvaluation() {
        ExecutorService exec=cellularExecutor;
        if(exec==null || exec.isShutdown() || !enabled(this) || !reconnectEnabled(this)) return;
        if(DeskTouchRuntime.anyCableConnected()) return;
        boolean needPunchForConnectedWifi=DeskTouchRuntime.connectedNonCableFingerprint()!=null
            && !DeskTouchRuntime.hasActiveMobileUdpStream();
        if(!needPunchForConnectedWifi && DeskTouchRuntime.anyInputLeaseActive()) return;
        if(!needPunchForConnectedWifi && DeskTouchRuntime.hasActiveMobileUdpStream()) return;
        if(!cellularProbeInFlight.compareAndSet(false,true)) {
            pendingCellularEval=true;
            return;
        }
        pendingCellularEval=false;
        exec.execute(()->{
            try {
                evaluateCellularFailoverOrReturn();
            } catch(Throwable err) {
                Log.w("DeskTouch","Mobile network check skipped",err);
            } finally {
                cellularProbeInFlight.set(false);
                if(pendingCellularEval) {
                    pendingCellularEval=false;
                    scheduleCellularEvaluation();
                }
            }
        });
    }

    private void evaluateCellularFailoverOrReturn() {
        if(cellularHelper==null) return;
        if(!DeskTouchRuntime.anyCellularConnected() && !DeskTouchRuntime.hasActiveMobileUdpStream()
            && cellularHelper.getActiveNetwork()!=null) {
            cellularHelper.releaseCellularNetwork();
        }
        if(trust==null || !enabled(this) || !reconnectEnabled(this)) return;
        if(DeskTouchRuntime.anyCableConnected()) return;
        Set<String> allowed=trust.fingerprints();
        if(allowed.isEmpty()) return;
        Set<String> paused=getSharedPreferences(PREFS,MODE_PRIVATE)
            .getStringSet("pausedPeers",java.util.Collections.emptySet());
        long now=SystemClock.uptimeMillis();

        // Keep Wi-Fi <-> Wi-Fi session intact and run the local auxiliary Mobile UDP receiver
        // alongside it; switching runs event-driven on loss feedback, not periodic timers.
        String nonCableFp=DeskTouchRuntime.connectedNonCableFingerprint();
        if(nonCableFp!=null) {
            if(paused.contains(nonCableFp)) return;
            DeskTouchRuntime.evaluateLocalMobileReceiver(this,cellularHelper);
            return;
        }

        // Fallback only when Wi-Fi is completely disconnected/unavailable (Case 3).
        if(DeskTouchRuntime.anyInputLeaseActive() || DeskTouchRuntime.connectedCount()>0) return;
        if(lastCellularProbeAtMs>0L && now-lastCellularProbeAtMs<60_000L) return;
        PhoneScreenGeometry.Info phone=PhoneScreenGeometry.readInfo(this);
        for(String fp:DeskTouchRuntime.endpointFingerprints()) {
            if(!allowed.contains(fp) || paused.contains(fp)) continue;
            java.util.List<String> ipv6Endpoints=DeskTouchRuntime.peerEndpoints(fp);
            if(ipv6Endpoints.isEmpty()) continue;
            lastCellularProbeAtMs=now;
            android.net.Network cellNet=cellularHelper.acquireCellularNetwork(4500L);
            if(cellNet==null) return;
            CellularLinkHelper.ProbeResult cellProbe=CellularLinkHelper.probeDirectEndpoints(
                cellNet,fp,trust,ipv6Endpoints,2400);
            if(!cellProbe.reachable) {
                Log.i("DeskTouch","Offline mobile reconnect failed ("+cellProbe.errorMessage+")");
                cellularHelper.releaseCellularNetwork();
                continue;
            }
            DesktopDiscovery.Device cellDev=new DesktopDiscovery.Device(
                "DeskTouch PC",cellProbe.address,fp,true,false);
            if(!DeskTouchRuntime.switchTransportMakeBeforeBreak(
                this,cellDev,phone.width,phone.height,cellNet,true,
                ()->Log.i("DeskTouch","Connected directly via Mobile network ("+cellProbe.address+")"),
                ()->cellularHelper.releaseCellularNetwork())) {
                cellularHelper.releaseCellularNetwork();
            }
            return;
        }
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        String action=intent!=null ? intent.getAction() : null;
        if(ACTION_PAUSE_BANKING.equals(action)) {
            boolean selfDisabled=DeskTouchAccessibilityService.pauseForBanking(
                this,"Turned off from notification");
            refreshNotification();
            if(!selfDisabled) {
                DeskTouchAccessibilityService.openAccessibilitySettings(this,false);
            }
        } else if(ACTION_RESUME_CONTROL.equals(action)) {
            cancelPaymentNotice(this);
            DeskTouchAccessibilityService.setAllowed(this,true);
            DeskTouchRuntime.accessibilityPermissionChanged(this);
            DeskTouchAccessibilityService.openAccessibilitySettings(this,true);
        }
        if(!enabled(this)) { stopSelf();return START_NOT_STICKY; }
        return START_STICKY;
    }
    @Override public android.os.IBinder onBind(Intent intent) {return null;}
    @Override public void onDestroy() {
        if(activeInstance==this)activeInstance=null;
        cancelDisconnectAutoPauseLocked();
        if(cellularExecutor!=null)cellularExecutor.shutdownNow();
        if(cellularHelper!=null)cellularHelper.releaseCellularNetwork();
        if(connectivityManager!=null && wifiCallback!=null) {
            try { connectivityManager.unregisterNetworkCallback(wifiCallback); }
            catch(Exception ignored) {}
        }
        if(wakeReceiver!=null) {
            try { unregisterReceiver(wakeReceiver); }
            catch(Exception ignored) {}
            wakeReceiver=null;
        }
        if(scheduler!=null)scheduler.shutdownNow();
        if(responder!=null)responder.close();
        // An active Android UI may still hold this same session when the user
        // disables startup. Stop background work without closing their UI session.
        super.onDestroy();
    }
}
