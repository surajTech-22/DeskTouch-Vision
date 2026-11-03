package com.desktouch.vision;

import java.util.*;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Consumer;

/**
 * Multiple simultaneous trusted Windows TLS sessions, one selected Android
 * keyboard/touchpad target at a time. Switching targets releases the old
 * authority lease before any events are sent to the new device.
 */
final class MobileSessionHub implements AutoCloseable {
    static final int MAX_DEVICES = 8;
    private final TrustedWindows trust;
    private final Consumer<String> events;
    private final Map<String,DeskTouchSession> peers = new ConcurrentHashMap<>();
    private final Map<String,String> labels = new ConcurrentHashMap<>();
    private volatile String selected;

    MobileSessionHub(TrustedWindows trust,Consumer<String> events) {
        this.trust=trust;this.events=events;
    }
    int connectedCount() {
        int count=0;
        for(DeskTouchSession peer:peers.values())if(peer.isConnected())count++;
        return count;
    }
    int sessionCount(){return peers.size();}
    String selectedId(){return selected;}
    boolean isConnected(){return connectedCount()>0;}
    boolean isConnecting(){
        for(DeskTouchSession p:peers.values()) if(p.isConnecting())return true;
        return false;
    }
    boolean isConnectedTo(String fingerprint){
        DeskTouchSession p=peers.get(fingerprint);
        return p!=null&&p.isConnected();
    }
    boolean isConnectingTo(String fingerprint){
        DeskTouchSession p=peers.get(fingerprint);
        return p!=null&&p.isConnecting();
    }
    List<String> connectedNames(){
        ArrayList<String> names=new ArrayList<>();
        for(String id:peers.keySet())
            if(isConnectedTo(id))names.add(labels.getOrDefault(id,id.substring(0,8)));
        Collections.sort(names);
        return names;
    }
    void select(String fingerprint) {
        if(fingerprint==null||!peers.containsKey(fingerprint))return;
        String previous=selected;
        if(previous!=null&&!previous.equals(fingerprint)) {
            DeskTouchSession old=peers.get(previous);
            if(old!=null)old.releaseControl();
        }
        selected=fingerprint;
        events.accept("Selected "+labels.getOrDefault(fingerprint,"Windows")+
            " • "+connectedCount()+" Windows device(s) connected");
    }
    void connect(DesktopDiscovery.Device device,int width,int height){
        if(device==null||device.android||!device.ready)return;
        String id=device.fingerprint;
        DeskTouchSession existing=peers.get(id);
        if(existing==null){
            if(peers.size()>=MAX_DEVICES) {
                events.accept("Eight paired connections are already configured.");
                return;
            }
            DeskTouchSession created=new DeskTouchSession(trust,message ->
                events.accept(device.name+": "+message+" • "+connectedCount()+" online"));
            DeskTouchSession earlier=peers.putIfAbsent(id,created);
            existing=earlier==null?created:earlier;
            if(earlier!=null)created.close();
        }
        labels.put(id,device.name);
        if(selected==null)selected=id;
        if(existing.isConnected()||existing.isConnecting()){
            events.accept(device.name+" is already connected or connecting. "+
                connectedCount()+" device(s) online.");
            return;
        }
        existing.connect(device,width,height);
    }
    boolean selectAndConnect(DesktopDiscovery.Device device,int width,int height){
        connect(device,width,height);
        select(device.fingerprint);
        return true;
    }
    private DeskTouchSession active() {
        String id=selected;
        return id==null?null:peers.get(id);
    }
    boolean isActive(){DeskTouchSession p=active();return p!=null&&p.isActive();}
    void claimControl(){
        DeskTouchSession p=active();
        if(p!=null)p.claimControl();
        else events.accept("Choose a connected Windows computer first.");
    }
    void releaseControl(){DeskTouchSession p=active();if(p!=null)p.releaseControl();}
    void requestTrust(){DeskTouchSession p=active();if(p!=null)p.requestTrust();}
    void moveBy(float dx,float dy){DeskTouchSession p=active();if(p!=null)p.moveBy(dx,dy);}
    void click(int button){DeskTouchSession p=active();if(p!=null)p.click(button);}
    void button(int button,boolean down){DeskTouchSession p=active();if(p!=null)p.button(button,down);}
    void wheel(int amount,boolean horizontal){DeskTouchSession p=active();if(p!=null)p.wheel(amount,horizontal);}
    void key(int vk,boolean down){DeskTouchSession p=active();if(p!=null)p.key(vk,down);}
    void disconnect(){
        String id=selected;
        DeskTouchSession p=active();
        if(p!=null){p.releaseControl();p.disconnect();}
        events.accept("Disconnected selected device; "+connectedCount()+" other connection(s) remain.");
    }
    void disconnectAll(){
        for(DeskTouchSession p:peers.values()){p.releaseControl();p.close();}
        peers.clear();labels.clear();selected=null;
        events.accept("Disconnected all devices.");
    }
    @Override public void close(){disconnectAll();}
}
