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
    private static volatile Consumer<String> listener;
    private static volatile String lastStatus="DeskTouch ready to find trusted devices.";
    private static volatile String selectedFingerprint;
    private static DeskTouchSession androidController;
    private DeskTouchRuntime(){}

    private static DeskTouchSession newSession(Context context,String id) {
        return new DeskTouchSession(context.getApplicationContext(),
            new TrustedWindows(context.getApplicationContext()),message->{
            lastStatus=message;
            Consumer<String> current=listener;
            if(current!=null)current.accept(message);
        });
    }

    static synchronized DeskTouchSession session(Context context) {
        // A reconnect service can establish the hub before the UI opens.
        if(selectedFingerprint==null ||
           (!sessions.containsKey(selectedFingerprint) &&
            !sessions.isEmpty())){
            selectedFingerprint=null;
            for(Map.Entry<String,DeskTouchSession> entry:sessions.entrySet()){
                if(entry.getValue().isConnected()){
                    selectedFingerprint=entry.getKey();break;
                }
            }
            if(selectedFingerprint==null)selectedFingerprint="__unselected__";
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
        for(DeskTouchSession s:sessions.values())s.close();
        sessions.clear();
        selectedFingerprint=null;androidController=null;listener=null;
    }
}
