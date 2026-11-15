package com.desktouch.vision;

import android.content.Context;
import java.util.*;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Consumer;

/**
 * Android maintains one authenticated connection to its selected Windows
 * environment hub. That hub routes input to its approved online members.
 * Each destination still requires an exclusive input authority lease.
 */
final class DeskTouchRuntime {
    private static final Map<String,DeskTouchSession> sessions=new ConcurrentHashMap<>();
    private static final Map<String,DeskTouchSession> pendingCableUpgrades=new ConcurrentHashMap<>();
    private static final Map<String,String> lastKnownPeerHosts=new ConcurrentHashMap<>();
    private static volatile Consumer<String> listener;
    private static volatile Runnable liveStatusListener;
    private static volatile String lastStatus="DeskTouch ready to find trusted devices.";
    private static volatile String selectedFingerprint;
    private static volatile Context appContext;
    private static DeskTouchSession androidController;
    private DeskTouchRuntime(){}

    static void rememberPeerHost(String fingerprint, String host){
        if(fingerprint==null || host==null || host.isBlank() || "127.0.0.1".equals(host))return;
        lastKnownPeerHosts.put(fingerprint.toUpperCase(Locale.ROOT),host);
    }
    static String getLastKnownPeerHost(String fingerprint){
        if(fingerprint==null)return null;
        return lastKnownPeerHosts.get(fingerprint.toUpperCase(Locale.ROOT));
    }
    static void setLiveStatusListener(Runnable observer){
        liveStatusListener=observer;
    }
    static void notifyLiveStatusChanged(){
        Runnable current=liveStatusListener;
        if(current!=null)current.run();
    }

    private static DeskTouchSession newSession(Context context,String id) {
        appContext=context.getApplicationContext();
        return new DeskTouchSession(appContext,
            new TrustedWindows(appContext),message->{
            lastStatus=message;
            Consumer<String> current=listener;
            if(current!=null)current.accept(message);
        });
    }

    static synchronized DeskTouchSession session(Context context) {
        appContext=context.getApplicationContext();
        // A reconnect service can establish the hub before the UI opens.
        DeskTouchSession current=selectedFingerprint!=null
            ? sessions.get(selectedFingerprint) : null;
        boolean needSelect=selectedFingerprint==null
            || "__unselected__".equals(selectedFingerprint)
            || current==null
            || (!current.isConnected() && !current.isConnecting());
        if(needSelect && !sessions.isEmpty()){
            String nextId=null;
            for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()){
                if(!"__unselected__".equals(entry.getKey()) && entry.getValue().isConnected()){
                    nextId=entry.getKey();break;
                }
            }
            if(nextId==null){
                for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()){
                    if(!"__unselected__".equals(entry.getKey()) && entry.getValue().isConnecting()){
                        nextId=entry.getKey();break;
                    }
                }
            }
            if(nextId==null && (current==null || "__unselected__".equals(selectedFingerprint))){
                for(String key:sessions.keySet()){
                    if(!"__unselected__".equals(key)){
                        nextId=key;break;
                    }
                }
            }
            if(nextId!=null){
                selectedFingerprint=nextId;
                DeskTouchSession stale=sessions.remove("__unselected__");
                if(stale!=null && !stale.isConnected()) stale.close();
            } else if(selectedFingerprint==null){
                selectedFingerprint="__unselected__";
            }
        } else if(selectedFingerprint==null){
            selectedFingerprint="__unselected__";
        }
        return sessions.computeIfAbsent(selectedFingerprint,
            id->newSession(context,id));
    }

    static synchronized DeskTouchSession forPeer(Context context,String fingerprint) {
        if(!fingerprint.matches("[A-Fa-f0-9]{64}"))
            throw new IllegalArgumentException("Invalid cryptographic device identity");
        String id=fingerprint.toUpperCase(Locale.ROOT);
        if(!sessions.containsKey(id) && sessions.size()>=9) {
            // Recycle an old disconnected session rather than permanently
            // exhausting the device list after ordinary network changes.
            for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()) {
                if(entry.getKey().equals("__unselected__"))continue;
                DeskTouchSession old=entry.getValue();
                if(!old.isConnected()&&!old.isConnecting() &&
                   !entry.getKey().equals(selectedFingerprint)) {
                    sessions.remove(entry.getKey(),old);
                    old.close();
                    break;
                }
            }
            if(sessions.size()>=9)
                throw new IllegalStateException("Maximum eight devices connected");
        }
        return sessions.computeIfAbsent(id,k->newSession(context,k));
    }

    static synchronized DeskTouchSession select(Context context,String fingerprint) {
        DeskTouchSession next=forPeer(context,fingerprint);
        if(selectedFingerprint!=null&&!selectedFingerprint.equals(fingerprint)){
            DeskTouchSession old=sessions.get(selectedFingerprint);
            if(old!=null)old.releaseControl(); // NEVER have two active input sources.
        }
        selectedFingerprint=fingerprint.toUpperCase(Locale.ROOT);
        return next;
    }

    static String selectedIdentity(){return selectedFingerprint;}
    static synchronized boolean isConnected(String fingerprint){
        DeskTouchSession s=sessions.get(fingerprint.toUpperCase(Locale.ROOT));
        return s!=null&&s.isConnected();
    }
    static synchronized boolean isConnecting(String fingerprint){
        DeskTouchSession s=sessions.get(fingerprint.toUpperCase(Locale.ROOT));
        return s!=null&&s.isConnecting();
    }
    static synchronized int connectedCount(){
        int count=0;
        for(DeskTouchSession s:sessions.values())if(s.isConnected())count++;
        return count;
    }
    static synchronized boolean anyCableConnected(){
        for(DeskTouchSession s:sessions.values())if(s.isCableSession())return true;
        return false;
    }
    static synchronized boolean anyInputLeaseActive(){
        for(DeskTouchSession s:sessions.values())if(s.isBusyInLease())return true;
        return false;
    }
    static boolean upgradeToCableMakeBeforeBreak(
            Context context, DesktopDiscovery.Device cableDevice,
            int width, int height, Runnable onFailure){
        if(context==null || cableDevice==null || cableDevice.fingerprint==null)return false;
        String id=cableDevice.fingerprint.toUpperCase(Locale.ROOT);
        DeskTouchSession candidate;
        synchronized(DeskTouchRuntime.class){
            if(anyInputLeaseActive())return false;
            DeskTouchSession current=sessions.get(id);
            if(current!=null && current.isConnected() && current.isCableSession())return false;
            DeskTouchSession pending=pendingCableUpgrades.get(id);
            if(pending!=null && pending.isConnecting())return false;
            candidate=newSession(context,id);
            pendingCableUpgrades.put(id,candidate);
        }
        candidate.connect(cableDevice,width,height,false,()->{
            synchronized(DeskTouchRuntime.class){
                pendingCableUpgrades.remove(id,candidate);
                DeskTouchSession previous=sessions.get(id);
                if(previous!=null && previous!=candidate){
                    if(previous.isBusyInLease()){
                        // Lease became active mid-handshake; defer drop so active input is not torn mid-stroke.
                        sessions.put(id,candidate);
                        selectedFingerprint=id;
                        previous.releaseControl();
                        previous.close();
                    }else{
                        sessions.put(id,candidate);
                        selectedFingerprint=id;
                        previous.close();
                    }
                }else{
                    sessions.put(id,candidate);
                    selectedFingerprint=id;
                }
            }
            notifyLiveStatusChanged();
        },()->{
            synchronized(DeskTouchRuntime.class){
                pendingCableUpgrades.remove(id,candidate);
            }
            candidate.close();
            if(onFailure!=null)onFailure.run();
        });
        return true;
    }
    static synchronized boolean closeCableSessions(){
        boolean closedAny=false;
        for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()){
            DeskTouchSession s=entry.getValue();
            if(s.isCableSession()){
                s.close();
                sessions.remove(entry.getKey(),s);
                closedAny=true;
            }
        }
        if(closedAny)notifyLiveStatusChanged();
        return closedAny;
    }
    static synchronized boolean closeWirelessIfIdle(String fingerprint){
        if(anyInputLeaseActive())return false;
        String id=fingerprint==null?null:fingerprint.toUpperCase(Locale.ROOT);
        boolean closedAny=false;
        for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()){
            DeskTouchSession s=entry.getValue();
            if(!s.isConnected())continue;
            if(s.isCableSession())return false;
            if(id==null || id.equals(s.connectedFingerprint()) || sessions.size()==1){
                s.close();
                sessions.remove(entry.getKey(),s);
                closedAny=true;
            }
        }
        return closedAny;
    }
    static void notifyNetworkProfile(Context context, WifiLinkMonitor.LinkSnapshot snapshot){
        if(snapshot==null)return;
        for(DeskTouchSession session:sessions.values()){
            if(session.isConnected() && !session.isCableSession()){
                session.publishLinkProfile(snapshot);
            }
        }
        notifyLiveStatusChanged();
    }
    static void refreshKeyboardBindings(Context context){
        if(context!=null)appContext=context.getApplicationContext();
        for(DeskTouchSession session:sessions.values()){
            if(session.isConnected())session.publishKeyboardBindings();
        }
        notifyLiveStatusChanged();
    }
    static synchronized DeskTouchSession.LiveStatusSnapshot liveSnapshot(Context context){
        if(context!=null)appContext=context.getApplicationContext();
        DeskTouchSession chosen=null;
        String sel=selectedFingerprint;
        if(sel!=null){
            DeskTouchSession s=sessions.get(sel);
            if(s!=null && s.isConnected())chosen=s;
        }
        if(chosen==null){
            for(DeskTouchSession s:sessions.values()){
                if(s.isConnected()){ chosen=s; break; }
            }
        }
        if(chosen!=null)return chosen.getLiveSnapshot();
        Context ctx=appContext;
        if(ctx==null)return null;
        WifiLinkMonitor.LinkSnapshot link=WifiLinkMonitor.inspect(ctx,false,0);
        KeyboardBindingInspector.KeyboardReport kb=KeyboardBindingInspector.inspect(
            ctx,ShellInputClient.ready(ctx),DeskTouchAccessibilityService.ready(ctx),"");
        return new DeskTouchSession.LiveStatusSnapshot(
            false,"",null,false,link.band,link.linkSpeedMbps,0.0,0.0,0.0,"",kb);
    }
    // Multiple trusted PCs remain connected, but only one may remotely
    // control Android at any moment. Switching needs a fresh lease handshake.
    static synchronized boolean claimAndroidControl(DeskTouchSession requester){
        if(androidController!=null && androidController!=requester)return false;
        androidController=requester;
        return true;
    }
    static synchronized void releaseAndroidControl(DeskTouchSession requester){
        if(androidController==requester)androidController=null;
    }
    static void publishPhoneGeometry(){
        for(DeskTouchSession session:sessions.values())session.publishPhoneGeometry();
    }
    static void liveRefreshRateChanged(int refreshHz, int frameTenthsMs){
        for(DeskTouchSession session:sessions.values())
            session.onLiveRefreshRateChanged(refreshHz, frameTenthsMs);
    }
    static void accessibilityPermissionChanged(){
        accessibilityPermissionChanged(appContext);
    }
    static void accessibilityPermissionChanged(Context context){
        if(context!=null)appContext=context.getApplicationContext();
        Context ctx=appContext;
        for(DeskTouchSession session:sessions.values()){
            session.publishAccessibilityCapability();
            session.publishKeyboardBindings();
        }
        AutoConnectService.refreshNotification();
        AutoConnectService.onAccessibilityStateChanged(ctx);
        notifyLiveStatusChanged();
        if(ctx!=null){
            PaymentPauseWidgetProvider.updateAll(ctx);
            BankingPauseTileService.requestRefresh(ctx);
        }
    }
    static void pointerOverlayUnavailable(){
        // Report an actionable status without leaking pointer coordinates,
        // typed characters, IP addresses, or paired-device identity.
        lastStatus="Android refused the DeskTouch pointer overlay. " +
            "Check Xiaomi Accessibility and display-over-other-apps restrictions, " +
            "then enable DeskTouch Windows control again.";
        Consumer<String> current=listener;
        if(current!=null)current.accept(lastStatus);
        remoteNativeTouch();
    }
    static void remoteNativeTouch(){
        for(DeskTouchSession s:sessions.values())s.revokeRemoteFromNative();
    }
    static void releaseAll() {
        for(DeskTouchSession s:sessions.values())if(s.hasClaim())s.releaseControl();
    }
    static void setListener(Consumer<String> observer){
        listener=observer;
        if(observer!=null)observer.accept(lastStatus);
    }
    static synchronized void clear(){
        for(DeskTouchSession s:pendingCableUpgrades.values())s.close();
        pendingCableUpgrades.clear();
        for(DeskTouchSession s:sessions.values())s.close();
        sessions.clear();
        selectedFingerprint=null;androidController=null;listener=null;
    }
}
