package com.desktouch.vision;

import android.os.Build;
import android.content.Context;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.*;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import javax.net.ssl.*;

/**
 * Android endpoint of the existing DeskTouch TCP 37377 / TLS / framed JSON protocol.
 * Uses Windows approval on first pair, TLS fingerprint pinning, and the existing
 * claim -> granted -> first move -> active two-phase input authority contract.
 * No new Windows pairing protocol or replacement keyboard settings.
 */
final class DeskTouchSession implements AutoCloseable {
    interface Events { void status(String message); }
    static final class EnvironmentTarget {
        final int id;
        final String name,platform,layout;
        final boolean supportsInput;
        EnvironmentTarget(int id,String name,String platform,String layout,boolean enabled){
            this.id=id;this.name=name;this.platform=platform;
            this.layout=layout;this.supportsInput=enabled;
        }
        @Override public String toString(){
            return platform+" • "+name+(supportsInput?"":" (view only)");
        }
    }
    private final Events events;
    private final TrustedWindows trust;
    private final Context application;
    private volatile int reverseLease,reverseApplied;
    private final ExecutorService sender=Executors.newSingleThreadExecutor();
    private final Object writeLock=new Object();
    private final java.util.concurrent.atomic.AtomicReference<Packet> newestMove =
        new java.util.concurrent.atomic.AtomicReference<>();
    private final java.util.concurrent.atomic.AtomicBoolean moveEnqueued =
        new java.util.concurrent.atomic.AtomicBoolean();
    private volatile SSLSocket socket;
    private volatile DataInputStream input;
    private volatile DataOutputStream output;
    private volatile boolean running;
    private volatile boolean connecting;
    private volatile boolean active;
    private volatile int claim;
    private volatile String fingerprint;
    private volatile String requestedToken;
    private volatile String pendingTrust;
    private final List<int[]> monitors=new ArrayList<>();
    private int cursorX,cursorY;
    private int screenX,screenY,screenWidth,screenHeight;
    private volatile java.util.List<EnvironmentTarget> environmentTargets =
        java.util.Collections.emptyList();
    private volatile int environmentOwnId;
    private volatile int selectedEnvironmentId;
    private static final SecureRandom RANDOM=new SecureRandom();

    DeskTouchSession(Context context,TrustedWindows trust,Events events) {
        this.application=context.getApplicationContext();
        this.trust=trust; this.events=events;
    }
    static boolean allowedInputType(String type) {
        return "move".equals(type)||"button".equals(type)||"key".equals(type)||
            "wheel".equals(type)||"text".equals(type);
    }
    static boolean allowedWindowsControlType(String type) {
        return "reverse-claim".equals(type)||"reverse-release".equals(type)||
            "reverse-reset".equals(type)||"reverse-move".equals(type)||
            "reverse-button".equals(type)||"reverse-key".equals(type)||
            "reverse-wheel".equals(type)||"reverse-text".equals(type);
    }
    boolean isConnected(){return running;}
    boolean isConnecting(){return connecting;}
    boolean isActive(){return active;}
    boolean hasClaim(){return claim>0||active;}
    java.util.List<EnvironmentTarget> environmentTargets(){
        return environmentTargets;
    }
    int selectedEnvironmentTarget(){return selectedEnvironmentId;}
    int ownEnvironmentId(){return environmentOwnId;}

    void selectEnvironmentTarget(int id){
        if(!running) {status("Join a shared environment first.");return;}
        EnvironmentTarget selected=null;
        for(EnvironmentTarget target:environmentTargets)
            if(target.id==id){selected=target;break;}
        if(selected==null || !selected.supportsInput || id==environmentOwnId){
            status("That environment device is unavailable for remote control.");
            return;
        }
        if(selectedEnvironmentId==id)return;
        releaseControl(); // never carry a lease across destinations
        try {
            synchronized(monitors){
                JSONArray layout=new JSONArray(selected.layout);
                ArrayList<int[]> parsed=new ArrayList<>();
                int minX=Integer.MAX_VALUE,minY=Integer.MAX_VALUE;
                int maxX=Integer.MIN_VALUE,maxY=Integer.MIN_VALUE;
                for(int i=0;i<layout.length();i++){
                    JSONObject m=layout.getJSONObject(i);
                    int x=m.getInt("X"),y=m.getInt("Y");
                    int width=m.getInt("Width"),height=m.getInt("Height");
                    if(width<1||height<1||width>32768||height>32768)
                        throw new IOException("Invalid remote monitor dimensions");
                    parsed.add(new int[]{x,y,width,height});
                    minX=Math.min(minX,x);minY=Math.min(minY,y);
                    maxX=Math.max(maxX,x+width);maxY=Math.max(maxY,y+height);
                }
                if(parsed.isEmpty())throw new IOException("Missing remote screens");
                monitors.clear();monitors.addAll(parsed);
                screenX=minX;screenY=minY;screenWidth=maxX-minX;screenHeight=maxY-minY;
                cursorX=parsed.get(0)[0]+parsed.get(0)[2]/2;
                cursorY=parsed.get(0)[1]+parsed.get(0)[3]/2;
            }
        }catch(Exception invalid){
            status("Cannot select device: invalid screen geometry.");return;
        }
        selectedEnvironmentId=id;
        send(new Packet("environment-select",id));
        status("Selected "+selected.name+" in the same environment.");
    }

    private void acceptEnvironmentRoster(Packet packet){
        try {
            JSONArray roster=new JSONArray(packet.text);
            if(roster.length()<1||roster.length()>9)return;
            ArrayList<EnvironmentTarget> members=new ArrayList<>();
            HashSet<Integer> ids=new HashSet<>();
            for(int i=0;i<roster.length();i++){
                JSONObject m=roster.getJSONObject(i);
                int id=m.getInt("Id");
                String name=m.getString("Name"),platform=m.getString("Platform");
                String layout=m.getString("Layout");
                if(id<0 || name.isEmpty() || name.length()>100 ||
                    !(platform.equals("Windows")||platform.equals("Android")) ||
                    !ids.add(id))return;
                // Parsing member monitor geometry is a validation step only.
                if(new JSONArray(layout).length()==0)return;
                members.add(new EnvironmentTarget(id,name,platform,layout,
                    m.optBoolean("SupportsReverseInput",false)));
            }
            if(!ids.contains(0))return;
            environmentOwnId=packet.a;
            environmentTargets=java.util.Collections.unmodifiableList(members);
            boolean stillAvailable=selectedEnvironmentId==0;
            for(EnvironmentTarget m:members)
                if(m.id==selectedEnvironmentId&&m.supportsInput)
                    stillAvailable=true;
            if(!stillAvailable) {
                // A departed destination cannot keep remote authority.
                selectedEnvironmentId=-1;
                selectEnvironmentTarget(0);
            }
            status("Shared DeskTouch environment: "+members.size()+" devices online.");
        }catch(Exception invalid){status("Invalid environment membership update.");}
    }

    private void status(String message) { events.status(message); }
    void connect(DesktopDiscovery.Device device,int androidWidth,int androidHeight) {
        sender.execute(()->{
            if(running || connecting) {status("Already connected or connecting");return;}
            connecting=true;
            try {
                String pinned=device.fingerprint.toUpperCase(Locale.ROOT);
                if(!pinned.matches("[A-F0-9]{64}")) throw new SecurityException("Invalid TLS fingerprint");
                String saved=trust.get(pinned);
                TrustManager manager=new X509TrustManager(){
                    public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
                    public void checkClientTrusted(X509Certificate[] chain,String auth) throws CertificateException {
                        throw new CertificateException("Client certificates not used");
                    }
                    public void checkServerTrusted(X509Certificate[] chain,String auth) throws CertificateException {
                        if(chain==null||chain.length==0) throw new CertificateException("Missing TLS certificate");
                        try {
                            byte[] sha=MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                            if(!MessageDigest.isEqual(sha,hex(pinned)))
                                throw new CertificateException("Fingerprint mismatch");
                        } catch(CertificateException e){throw e;}
                        catch(Exception e){throw new CertificateException(e);}
                    }
                };
                SSLContext context=SSLContext.getInstance("TLS");
                context.init(null,new TrustManager[]{manager},new SecureRandom());
                SSLSocket ssl=(SSLSocket)context.getSocketFactory().createSocket();
                ssl.connect(new java.net.InetSocketAddress(device.address,37377),12000);
                ssl.setTcpNoDelay(true);
                ssl.setSoTimeout(50000);
                ssl.startHandshake();
                DataInputStream in=new DataInputStream(new BufferedInputStream(ssl.getInputStream()));
                DataOutputStream out=new DataOutputStream(new BufferedOutputStream(ssl.getOutputStream()));
                new Packet(saved==null?"auth-request":"auth-trusted",0,0,0,0,
                    saved==null?Build.MANUFACTURER+" "+Build.MODEL:saved).write(out);
                Packet ready=Packet.read(in);
                if(!"ready".equals(ready.type)) throw new SecurityException("Windows did not approve pairing");
                ArrayList<int[]> parsed=new ArrayList<>();
                JSONArray layout=new JSONArray(ready.text);
                if(layout.length()<1||layout.length()>20)throw new IOException("Invalid Windows display geometry");
                int minX=Integer.MAX_VALUE,minY=Integer.MAX_VALUE,maxX=Integer.MIN_VALUE,maxY=Integer.MIN_VALUE;
                for(int i=0;i<layout.length();i++){
                    JSONObject m=layout.getJSONObject(i);
                    int x=m.getInt("X"),y=m.getInt("Y"),w=m.getInt("Width"),h=m.getInt("Height");
                    if(w<1||h<1||w>32768||h>32768)throw new IOException("Invalid monitor size");
                    parsed.add(new int[]{x,y,w,h});
                    minX=Math.min(minX,x);minY=Math.min(minY,y);
                    maxX=Math.max(maxX,x+w);maxY=Math.max(maxY,y+h);
                }
                if(minX!=ready.a||minY!=ready.b||maxX-minX!=ready.c||maxY-minY!=ready.d)
                    throw new SecurityException("Windows display geometry mismatch");
                int width=Math.max(200,androidWidth),height=Math.max(100,androidHeight);
                JSONArray myLayout=new JSONArray();
                myLayout.put(new JSONObject().put("Name","Android screen").put("X",0).put("Y",0)
                    .put("Width",width).put("Height",height).put("Primary",true));
                JSONObject hello=new JSONObject().put("Name",Build.MANUFACTURER+" "+Build.MODEL)
                    .put("Layout",myLayout.toString())
                    .put("ReverseInputSupported",
                        DeskTouchAccessibilityService.ready(application))
                    .put("ForwardInputSupported",true)
                    .put("Platform","Android");
                new Packet("hello",0,0,0,0,hello.toString()).write(out);
                synchronized(monitors) {monitors.clear();monitors.addAll(parsed);}
                screenX=minX;screenY=minY;screenWidth=ready.c;screenHeight=ready.d;
                cursorX=parsed.get(0)[0]+parsed.get(0)[2]/2;
                cursorY=parsed.get(0)[1]+parsed.get(0)[3]/2;
                fingerprint=pinned;
                socket=ssl;input=in;output=out;running=true;active=false;claim=0;
                ssl.setSoTimeout(22000);
                status("Connected to "+device.name+". Windows approved pairing.");
                Thread heartbeat=new Thread(()->{
                    while(running){
                        try { Thread.sleep(1500);if(running)send(new Packet("ping")); }
                        catch(InterruptedException e){Thread.currentThread().interrupt();return;}
                    }
                },"DeskTouch heartbeat");
                heartbeat.setDaemon(true); heartbeat.start();
                Thread reader=new Thread(this::readLoop,"DeskTouch Android input authority");
                reader.setDaemon(true);reader.start();
            } catch(Exception e) {
                status("Connection failed: "+e.getMessage());
                closeConnection();
            } finally {connecting=false;}
        });
    }
    private static byte[] hex(String value) {
        byte[] data=new byte[value.length()/2];
        for(int i=0;i<data.length;i++)data[i]=(byte)Integer.parseInt(value.substring(i*2,i*2+2),16);
        return data;
    }
    private void readLoop(){
        try {
            while(running){
                Packet p=Packet.read(input);
                if("environment-roster".equals(p.type)){
                    acceptEnvironmentRoster(p);
                } else if("environment-unavailable".equals(p.type)){
                    status("Environment member went offline. Select another device.");
                    if(selectedEnvironmentId==p.a){
                        selectedEnvironmentId=-1;selectEnvironmentTarget(0);
                    }
                } else if("granted".equals(p.type) && p.a==claim && claim>0){
                    // The remote Windows endpoint must successfully move its cursor
                    // and acknowledge "active" before we send any keyboard/buttons.
                    send(new Packet("move",cursorX,cursorY));
                    status("Windows granted control; confirming cursor");
                } else if("active".equals(p.type) && p.a==claim && claim>0){
                    active=true;status("Controlling Windows with Android keyboard and mouse");
                } else if("busy".equals(p.type) && p.a==claim){
                    active=false;claim=0;status("Windows keyboard/mouse is active; retry after idle");
                } else if("local-active".equals(p.type)){
                    active=false;claim=0;status("Windows physical keyboard/mouse reclaimed control");
                } else if("trust-granted".equals(p.type)){
                    String token=pendingTrust;pendingTrust=null;
                    if(token!=null){
                        trust.save(fingerprint,token);
                        status("Trusted Windows PC saved securely, without storing its IP address");
                    }
                } else if("trust-denied".equals(p.type)){
                    pendingTrust=null;status("Windows declined trusted-device permission");
                } else if(allowedWindowsControlType(p.type)){
                    handleWindowsControl(p);
                } else if("goodbye".equals(p.type)) break;
            }
        } catch(Exception e){if(running)status("Windows disconnected: "+e.getMessage());}
        finally{closeConnection();}
    }
    // Windows forwards approved local or environment-relayed input through
    // the SAME authenticated session. Native phone touch retains priority.
    private void handleWindowsControl(Packet p){
        if("reverse-claim".equals(p.type)){
            if(p.a<=0 || hasClaim() ||
                !DeskTouchAccessibilityService.ready(application) ||
                (reverseLease>0 && reverseLease!=p.a) ||
                !DeskTouchRuntime.claimAndroidControl(this)){
                send(new Packet("reverse-busy",p.a));
                return;
            }
            reverseLease=p.a;reverseApplied=0;
            send(new Packet("reverse-granted",p.a));
            status("Windows is allowed to control this Android screen.");
            return;
        }
        if("reverse-release".equals(p.type)||"reverse-reset".equals(p.type)){
            if("reverse-reset".equals(p.type) || p.a==reverseLease)
                clearReverseControl(false);
            return;
        }
        if(reverseLease<=0 || !DeskTouchAccessibilityService.ready(application)){
            clearReverseControl(false);
            return;
        }
        if("reverse-move".equals(p.type)){
            final int lease=reverseLease;
            boolean posted=DeskTouchAccessibilityService.pointer(p.a,p.b,()->{
                if(reverseLease==lease && reverseApplied!=lease){
                    reverseApplied=lease;
                    send(new Packet("reverse-active",lease));
                }
            });
            if(!posted)revokeRemoteFromNative();
        }else if("reverse-button".equals(p.type)){
            DeskTouchAccessibilityService.mouseButton(p.a,p.b!=0);
        }else if("reverse-wheel".equals(p.type)){
            DeskTouchAccessibilityService.wheel(p.a,p.b!=0);
        }else if("reverse-key".equals(p.type)){
            DeskTouchAccessibilityService.key(p.d,p.c!=0);
        }else if("reverse-text".equals(p.type) && p.text!=null &&
            p.text.length()>0 && p.text.length()<=512){
            DeskTouchAccessibilityService.text(p.text);
        }
    }
    private void clearReverseControl(boolean notifyWindows){
        int lease=reverseLease;reverseLease=0;reverseApplied=0;
        DeskTouchRuntime.releaseAndroidControl(this);
        DeskTouchAccessibilityService.endRemote();
        if(notifyWindows && lease>0 && running)
            send(new Packet("reverse-local-active",lease));
    }
    void revokeRemoteFromNative(){
        if(reverseLease>0)clearReverseControl(true);
    }

    void claimControl(){
        if(!running){status("Connect to a Windows PC first");return;}
        // Never start keyboard events until the target has confirmed that
        // the first remote cursor movement was actually applied.
        if(reverseLease>0)clearReverseControl(true);
        active=false;
        claim=RANDOM.nextInt(Integer.MAX_VALUE-1)+1;
        send(new Packet("claim",claim));
        status("Requesting Windows mouse and keyboard authority");
    }
    void publishAccessibilityCapability(){
        // A permission restored after an Android package update must become
        // visible on the SAME authenticated environment connection, without
        // asking the user to disconnect or pair the phone again.
        if(running)send(new Packet("android-capability",
            DeskTouchAccessibilityService.ready(application)?1:0));
    }

    void releaseControl(){
        int lease=claim;
        claim=0;active=false;
        if(lease>0 && running)send(new Packet("release",lease));
        clearReverseControl(true);
        status("Input control released; connections remain available.");
    }
    void requestTrust(){
        if(!running){status("Pair with Windows first");return;}
        byte[] secret=new byte[32];RANDOM.nextBytes(secret);
        StringBuilder s=new StringBuilder();
        for(byte b:secret)s.append(String.format(Locale.ROOT,"%02X",b&255));
        pendingTrust=s.toString();
        send(new Packet("trust-request",0,0,0,0,pendingTrust));
        status("Approve 'Trust this device' on Windows");
    }
    private void clampToMonitor(){
        synchronized(monitors){
            if(monitors.isEmpty())return;
            int bestDistance=Integer.MAX_VALUE;
            int bx=cursorX,by=cursorY;
            for(int[] m:monitors){
                int x=Math.max(m[0],Math.min(m[0]+m[2]-1,cursorX));
                int y=Math.max(m[1],Math.min(m[1]+m[3]-1,cursorY));
                long d=(long)(x-cursorX)*(x-cursorX)+(long)(y-cursorY)*(y-cursorY);
                if(d<bestDistance){bestDistance=(int)Math.min(d,Integer.MAX_VALUE);bx=x;by=y;}
            }
            cursorX=bx;cursorY=by;
        }
    }
    void prepareGroupEntry(int direction){
        synchronized(monitors){
            cursorX=direction>0 ? screenX : screenX+screenWidth-1;
            cursorY=screenY+screenHeight/2;
            clampToMonitor();
        }
    }
    private boolean crossEnvironmentEdge(int direction){
        if(!active)return false;
        List<EnvironmentTarget> targets=environmentTargets;
        ArrayList<EnvironmentTarget> available=new ArrayList<>();
        for(EnvironmentTarget member:targets)
            if(member.supportsInput&&member.id!=environmentOwnId)
                available.add(member);
        int index=-1;
        for(int i=0;i<available.size();i++)
            if(available.get(i).id==selectedEnvironmentId)index=i;
        int next=index+direction;
        if(index<0||next<0||next>=available.size())return false;
        int before=selectedEnvironmentId;
        selectEnvironmentTarget(available.get(next).id);
        if(selectedEnvironmentId==before)return false;
        synchronized(monitors){
            cursorX=direction>0?screenX:screenX+screenWidth-1;
            cursorY=screenY+screenHeight/2;
            clampToMonitor();
        }
        claimControl(); // fresh two-phase ACK from the newly selected device
        return true;
    }
    void moveBy(float dx,float dy){
        if(!active)return;
        synchronized(monitors){
            int nextX=cursorX+Math.round(dx);
            if(nextX<screenX && dx<0 &&
                crossEnvironmentEdge(-1))return;
            if(nextX>=screenX+screenWidth && dx>0 &&
                crossEnvironmentEdge(1))return;
            cursorX=nextX;cursorY+=Math.round(dy);
            clampToMonitor();
            send(new Packet("move",cursorX,cursorY));
        }
    }
    void click(int button){
        if(!active)return;
        button(button,true);button(button,false);
    }
    void button(int button,boolean down){
        if(active && button>=1 && button<=3)
            send(new Packet("button",button,down?1:0));
    }
    void wheel(int amount,boolean horizontal){
        if(active&&amount!=0)send(new Packet("wheel",amount,horizontal?1:0));
    }
    void key(int vk,boolean down){
        if(active&&vk>0&&vk<=255)
            send(new Packet("key",0,0,down?1:0,vk));
    }
    void text(String value){
        if(!active || value==null || value.isEmpty())return;
        // Never include typed characters in diagnostics.
        for(int at=0;at<value.length();at+=Math.min(512,value.length()-at))
            send(new Packet("text",0,0,0,0,
                value.substring(at,Math.min(value.length(),at+512))));
    }
    private void writeNow(Packet p){
        if(!running || output==null)return;
        try {synchronized(writeLock){p.write(output);}}
        catch(Exception e){status("Connection lost: "+e.getMessage());closeConnection();}
    }
    private void send(Packet p){
        if(sender.isShutdown())return;
        if("move".equals(p.type)){
            newestMove.set(p);
            if(moveEnqueued.compareAndSet(false,true))
                sender.execute(()->{
                    do {
                        Packet latest=newestMove.getAndSet(null);
                        if(latest!=null)writeNow(latest);
                        moveEnqueued.set(false);
                    } while(newestMove.get()!=null &&
                        moveEnqueued.compareAndSet(false,true));
                });
        }else sender.execute(()->{
            Packet preceding=newestMove.getAndSet(null);
            if(preceding!=null)writeNow(preceding);
            writeNow(p);
        });
    }
    private synchronized void closeConnection(){
        clearReverseControl(false);
        running=false;active=false;claim=0;pendingTrust=null;
        environmentTargets=java.util.Collections.emptyList();
        selectedEnvironmentId=0;environmentOwnId=0;
        SSLSocket sock=socket;socket=null;
        if(sock!=null)try{sock.close();}catch(Exception ignored){}
    }
    void disconnect(){
        closeConnection();
        status("Disconnected. Trusted auto-reconnect paused until enabled.");
    }
    @Override public void close(){
        closeConnection();
        sender.shutdownNow();
    }
}
