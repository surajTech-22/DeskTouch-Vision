package com.desktouch.vision;

import android.content.Context;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.net.InetAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.*;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.*;

/**
 * Android endpoint of the existing DeskTouch TCP 37377 / TLS / framed JSON protocol.
 * Uses Windows approval on first pair, TLS fingerprint pinning, and the existing
 * claim -> granted -> first move -> active two-phase input authority contract.
 * No new Windows pairing protocol or replacement keyboard settings.
 */
final class DeskTouchSession implements AutoCloseable {
    static final int INTERACTIVE_KEEPALIVE_MS = 22;

    static boolean shouldSendInteractiveKeepalive(
        boolean running, boolean sameChannel, boolean confirmed,
        boolean serviceReady, boolean screenInteractive, boolean activeSession)
    {
        return running && sameChannel && confirmed && serviceReady &&
            (activeSession || screenInteractive);
    }

    static final class ReverseControlDeduplicator {
        private int highestClaimDispatchSeq;
        private int highestControlActionSeq;
        private long seenControlActionMask;
        private int lastExecutedButtonSeq;
        private int lastExecutedKeySeq;
        private int highestReverseMoveSeq;

        synchronized void reset(){
            highestClaimDispatchSeq=0;
            highestControlActionSeq=0;
            seenControlActionMask=0L;
            lastExecutedButtonSeq=0;
            lastExecutedKeySeq=0;
            highestReverseMoveSeq=0;
        }

        synchronized boolean tryAcceptMove(int moveSeq){
            if(moveSeq<=0) return true;
            if(moveSeq<=highestReverseMoveSeq) return false;
            highestReverseMoveSeq=moveSeq;
            return true;
        }

        synchronized boolean tryAcceptClaimDispatch(int claim, int dispatchSeq, int currentLease){
            if(claim<=0) return false;
            if(dispatchSeq>0){
                if(dispatchSeq<=highestClaimDispatchSeq) return false;
                highestClaimDispatchSeq=dispatchSeq;
            }else if(currentLease==claim){
                return false;
            }
            highestControlActionSeq=0;
            seenControlActionMask=0L;
            lastExecutedButtonSeq=0;
            lastExecutedKeySeq=0;
            return true;
        }

        synchronized boolean tryAcceptControlAction(int seq, boolean isButton){
            if(seq<=0) return true;
            if(isButton && seq<=lastExecutedButtonSeq) return false;
            if(highestControlActionSeq==0){
                highestControlActionSeq=seq;
                seenControlActionMask=1L;
            }else{
                int diff=seq-highestControlActionSeq;
                if(diff>0){
                    seenControlActionMask=diff>=64 ? 1L : ((seenControlActionMask<<diff)|1L);
                    highestControlActionSeq=seq;
                }else{
                    int back=-diff;
                    if(back>=64) return false;
                    long bit=1L<<back;
                    if((seenControlActionMask & bit)!=0L) return false;
                    seenControlActionMask|=bit;
                }
            }
            if(isButton) lastExecutedButtonSeq=seq;
            return true;
        }

        synchronized boolean tryAcceptKeyAction(int seq){
            if(seq<=0) return true;
            if(seq<=lastExecutedKeySeq) return false;
            if(highestControlActionSeq==0){
                highestControlActionSeq=seq;
                seenControlActionMask=1L;
            }else{
                int diff=seq-highestControlActionSeq;
                if(diff>0){
                    seenControlActionMask=diff>=64 ? 1L : ((seenControlActionMask<<diff)|1L);
                    highestControlActionSeq=seq;
                }else{
                    int back=-diff;
                    if(back>=64) return false;
                    long bit=1L<<back;
                    if((seenControlActionMask & bit)!=0L) return false;
                    seenControlActionMask|=bit;
                }
            }
            lastExecutedKeySeq=seq;
            return true;
        }
    }

    private final ReverseControlDeduplicator reverseDedup = new ReverseControlDeduplicator();

    boolean tryAcceptControlAction(int seq, boolean isButton){
        return reverseDedup.tryAcceptControlAction(seq,isButton);
    }
    boolean tryAcceptKeyAction(int seq){
        return reverseDedup.tryAcceptKeyAction(seq);
    }
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
    private volatile int reverseLease,reverseApplied,reportedMissingKeyboard;
    private volatile boolean reverseUsingShell;
    private final ExecutorService sender=Executors.newSingleThreadExecutor(r->{
        Thread t=new Thread(()->{
            try { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY); }
            catch (Throwable ignored) {}
            r.run();
        },"DeskTouch Sender");
        t.setDaemon(true);
        return t;
    });
    private final ExecutorService controlSender=Executors.newSingleThreadExecutor(r->{
        Thread t=new Thread(()->{
            try { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY); }
            catch (Throwable ignored) {}
            r.run();
        },"DeskTouch CtrlWriter");
        t.setDaemon(true);
        return t;
    });
    private final ScheduledExecutorService udpPacer=Executors.newSingleThreadScheduledExecutor(r->{
        Thread t=new Thread(()->{
            try { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY); }
            catch (Throwable ignored) {}
            r.run();
        },"DeskTouch UdpSend");
        t.setDaemon(true);
        return t;
    });
    private final Object writeLock=new Object();
    private final Object pingTrackLock=new Object();
    private final Object udpPacerLock=new Object();
    private final java.util.concurrent.atomic.AtomicReference<Packet> newestMove =
        new java.util.concurrent.atomic.AtomicReference<>();
    private final java.util.concurrent.atomic.AtomicBoolean moveEnqueued =
        new java.util.concurrent.atomic.AtomicBoolean();
    private Packet throttledUdpMove;
    private Packet pendingTcpSyncMove;
    private long lastUdpMoveMs;
    private long lastAnyMoveMs;
    private ScheduledFuture<?> scheduledThrottleFlush;
    private ScheduledFuture<?> scheduledTrailingSync;
    static final class LiveStatusSnapshot {
        final boolean connected;
        final String peerName;
        final String fingerprint;
        final boolean cable;
        final String band;
        final int speedMbps;
        final double rttMs;
        final double jitterMs;
        final double lossPermille;
        final double recentPeakLossPermille;
        final double recentPeakJitterMs;
        final String pcLayout;
        final KeyboardBindingInspector.KeyboardReport keyboardReport;
        final boolean preferMobileMouse;
        final boolean preferMobileKeyboard;
        final double wifiRttMs;
        final double wifiLossPermille;
        final double mobRttMs;
        final double mobLossPermille;
        final boolean mobConfirmed;
        final boolean dualPathControls;
        LiveStatusSnapshot(boolean connected, String peerName, String fingerprint, boolean cable,
                String band, int speedMbps, double rttMs, double jitterMs, double lossPermille,
                String pcLayout, KeyboardBindingInspector.KeyboardReport keyboardReport) {
            this(connected, peerName, fingerprint, cable, band, speedMbps, rttMs, jitterMs,
                lossPermille, lossPermille, jitterMs, pcLayout, keyboardReport);
        }
        LiveStatusSnapshot(boolean connected, String peerName, String fingerprint, boolean cable,
                String band, int speedMbps, double rttMs, double jitterMs, double lossPermille,
                double recentPeakLossPermille, double recentPeakJitterMs,
                String pcLayout, KeyboardBindingInspector.KeyboardReport keyboardReport) {
            this(connected, peerName, fingerprint, cable, band, speedMbps, rttMs, jitterMs,
                lossPermille, recentPeakLossPermille, recentPeakJitterMs, pcLayout, keyboardReport,
                false, false, rttMs, lossPermille, 0.0, 0.0, false, true);
        }
        LiveStatusSnapshot(boolean connected, String peerName, String fingerprint, boolean cable,
                String band, int speedMbps, double rttMs, double jitterMs, double lossPermille,
                double recentPeakLossPermille, double recentPeakJitterMs,
                String pcLayout, KeyboardBindingInspector.KeyboardReport keyboardReport,
                boolean preferMobileMouse, boolean preferMobileKeyboard,
                double wifiRttMs, double wifiLossPermille,
                double mobRttMs, double mobLossPermille,
                boolean mobConfirmed, boolean dualPathControls) {
            this.connected = connected;
            this.peerName = peerName != null ? peerName : "";
            this.fingerprint = fingerprint;
            this.cable = cable;
            this.band = band != null ? band : WifiLinkMonitor.BAND_WIRELESS;
            this.speedMbps = speedMbps;
            this.rttMs = rttMs;
            this.jitterMs = jitterMs;
            this.lossPermille = lossPermille;
            this.recentPeakLossPermille = recentPeakLossPermille;
            this.recentPeakJitterMs = recentPeakJitterMs;
            this.pcLayout = pcLayout != null ? pcLayout : "";
            this.keyboardReport = keyboardReport;
            this.preferMobileMouse = preferMobileMouse;
            this.preferMobileKeyboard = preferMobileKeyboard;
            this.wifiRttMs = wifiRttMs;
            this.wifiLossPermille = wifiLossPermille;
            this.mobRttMs = mobRttMs;
            this.mobLossPermille = mobLossPermille;
            this.mobConfirmed = mobConfirmed;
            this.dualPathControls = dualPathControls;
        }
    }
    static final int STREAM_MODE_AUTO = 0;
    static final int STREAM_MODE_FORCE_WIFI = 1;
    static final int STREAM_MODE_FORCE_MOBILE = 2;
    private static final String STREAM_MEMORY_PREFS = "desktouch_stream_memory";
    private static final String PREF_MOUSE_MODE = "opt_mouse_mode";
    private static final String PREF_KB_MODE = "opt_kb_mode";
    private static final String PREF_DUAL_PATH_CONTROLS = "opt_dual_path_controls";
    private static final String PREF_KEEP_CHANNELS_WARM = "opt_keep_channels_warm";
    private static final String PREF_POINTER_FEEL = "opt_pointer_feel";
    private static final long STREAM_SWITCH_COOLDOWN_MS = 8_000L;
    private static final long STREAM_SAVE_CLEAN_DELAY_MS = 3_000L;
    private static final long MOUSE_UNLOAD_GRACE_MS = 6_000L;
    private static final long MOUSE_UNLOAD_ANCHOR_DELAY_MS = 2_000L;
    private static final long KEYBOARD_FAST_SWITCH_COOLDOWN_MS = 15_000L;
    private volatile FastUdpChannel udpChannel;
    private volatile FastUdpChannel mobileUdpChannel;
    private volatile CellularLinkHelper lastCellularHelper;
    private final Object streamPrefLock = new Object();
    private volatile int mobileUdpPort;
    private volatile int mobileUdpPeerId;
    private volatile byte[] mobileUdpKey;
    private volatile boolean preferMobileMouse;
    private volatile boolean preferMobileKeyboard;
    private volatile long lastMouseSwitchAtMs;
    private volatile long lastKeyboardSwitchAtMs;
    private volatile long wifiPostMouseOffloadAtMs;
    private volatile long wifiPostOffloadLostAnchor = -1L;
    private volatile long connectedStreamSinceMs;
    private volatile String lastSavedMouseStream = "";
    private volatile String lastSavedKeyboardStream = "";
    private volatile long mobilePunchBackoffUntilMs;
    private volatile boolean mobilePunchAttemptedOnce;
    private volatile long lastMobileUdpLinkPingMs;
    private ScheduledFuture<?> mobileUdpProbeRetryFuture;
    private ScheduledFuture<?> mobileUdpKeepAliveFuture;
    private ScheduledFuture<?> mobileUdpTimeoutFuture;
    private volatile InetAddress peerInetAddress;
    private volatile String peerName;
    private volatile String peerHostAddress;
    private volatile boolean cableSession;
    private volatile boolean cellularSession;
    private volatile android.net.Network boundNetwork;
    private volatile SSLSocket socket;
    private volatile DataInputStream input;
    private volatile DataOutputStream output;
    private volatile boolean running;
    private volatile boolean connecting;
    private volatile boolean active;
    private volatile int claim;
    private volatile String fingerprint;
    private volatile String requestedToken;
    private final Object trustLock=new Object();
    private volatile String pendingTrust;
    private int pendingTrustId;
    private long pendingTrustAtMs;
    private int nextTrustId=1;
    private final List<int[]> monitors=new ArrayList<>();
    private int cursorX,cursorY;
    private int screenX,screenY,screenWidth,screenHeight;
    private volatile int announcedPhoneWidth,announcedPhoneHeight,announcedPhoneRotation;
    private volatile int announcedPhoneRefreshHz;
    private volatile int livePhoneRefreshHz,livePhoneFrameTenthsMs;
    private volatile int peerTargetRefreshHz;
    private volatile double lastAdaptiveMoveIntervalMs = 2.5;
    private volatile long lastPingSentMs;
    private volatile long lastPongReceivedMs;
    private volatile long connectedAtMs;
    private volatile int missedPings;
    private final AdaptiveLinkTracker cableTracker = new AdaptiveLinkTracker(WifiLinkMonitor.BAND_CABLE);
    private final int[] tcpPingTokens = new int[16];
    private final long[] tcpPingSentNanos = new long[16];
    private int tcpPingSeq;
    private volatile String pcKeyboardLayout = "";
    private volatile String liveLinkBand = WifiLinkMonitor.BAND_WIRELESS;
    private volatile int liveLinkSpeedMbps;
    private volatile double liveRttMs;
    private volatile KeyboardBindingInspector.KeyboardReport lastKeyboardReport;
    private volatile long lastTcpMetricsSentMs;
    private volatile long lastUdpLinkPingMs;
    private volatile long lastUdpFeedbackPushMs;
    private volatile long lastWarmSentMs;
    private volatile long lastUdpReverseMoveAtMs;
    private ScheduledFuture<?> udpProbeRetryFuture;
    private ScheduledFuture<?> udpKeepAliveFuture;
    private DisplayManager.DisplayListener displayListener;
    private volatile java.util.List<EnvironmentTarget> environmentTargets =
        java.util.Collections.emptyList();
    private volatile int environmentOwnId;
    private volatile int selectedEnvironmentId;
    private static final SecureRandom RANDOM=new SecureRandom();

    DeskTouchSession(Context context,TrustedWindows trust,Events events) {
        this.application=context.getApplicationContext();
        this.trust=trust; this.events=events;
    }

    String peerHostAddress() { return peerHostAddress; }
    static long computeHeartbeatIntervalMs(boolean cable, boolean activeSession, String band) {
        if (cable) return activeSession ? 450L : 850L;
        if (WifiLinkMonitor.BAND_24_GHZ.equals(band)) return activeSession ? 1100L : 1700L;
        if (WifiLinkMonitor.BAND_CELLULAR.equals(band)) return activeSession ? 900L : 1500L;
        return activeSession ? 750L : 1200L;
    }
    static boolean shouldFastFailDeadSession(long nowMs, long lastPongMs, long connectedAtMs,
            int missedPings, boolean cable) {
        if (missedPings < 3) return false;
        long baseline = lastPongMs > 0L ? lastPongMs : connectedAtMs;
        if (baseline <= 0L) return false;
        long limitMs = cable ? 3600L : 5200L;
        return (nowMs - baseline) >= limitMs;
    }
    LiveStatusSnapshot getLiveSnapshot() {
        FastUdpChannel ch = udpChannel;
        FastUdpChannel mobCh = mobileUdpChannel;
        String band = cableSession ? WifiLinkMonitor.BAND_CABLE : liveLinkBand;
        double jitter = 0.0;
        double loss = 0.0;
        double peakLoss = 0.0;
        double peakJitter = 0.0;
        double rtt = cableSession ? 0.0 : liveRttMs;
        boolean mobOk = !cableSession && mobCh != null && mobCh.confirmed;
        double wifiRtt = cableSession ? 0.0 : liveRttMs;
        double wifiLoss = 0.0;
        if (!cableSession && ch != null) {
            if (ch.linkTracker.getSmoothedRttMs() > 0) wifiRtt = ch.linkTracker.getSmoothedRttMs();
            wifiLoss = ch.linkTracker.getInboundLossPermille();
        }
        double mobRtt = (mobOk && mobCh.linkTracker.getSmoothedRttMs() > 0)
            ? mobCh.linkTracker.getSmoothedRttMs() : 0.0;
        double mobLoss = mobOk ? mobCh.linkTracker.getInboundLossPermille() : 0.0;
        if (preferMobileMouse && mobOk) {
            long nowMs = SystemClock.uptimeMillis();
            if (mobRtt > 0) rtt = mobRtt;
            jitter = mobCh.linkTracker.getEffectiveJitterMs();
            loss = mobLoss;
            peakLoss = Math.max(loss, mobCh.linkTracker.getRecentPeakLossPermille(nowMs));
            peakJitter = Math.max(jitter, mobCh.linkTracker.getRecentPeakJitterMs(nowMs));
            band = WifiLinkMonitor.BAND_CELLULAR;
        } else if (!cableSession && ch != null) {
            long nowMs = SystemClock.uptimeMillis();
            rtt = wifiRtt;
            jitter = ch.linkTracker.getEffectiveJitterMs();
            loss = wifiLoss;
            peakLoss = Math.max(loss, ch.linkTracker.getRecentPeakLossPermille(nowMs));
            peakJitter = Math.max(jitter, ch.linkTracker.getRecentPeakJitterMs(nowMs));
            String trackerBand = ch.linkTracker.getBand();
            if (trackerBand != null && !trackerBand.isEmpty()) band = trackerBand;
        }
        boolean dualPath = readDualPathControls(application);
        return new LiveStatusSnapshot(running, peerName, fingerprint, cableSession,
            band, (preferMobileMouse && mobOk) ? 0 : (cableSession ? 0 : liveLinkSpeedMbps),
            rtt, jitter, loss, peakLoss, peakJitter,
            pcKeyboardLayout, lastKeyboardReport,
            preferMobileMouse && mobOk, preferMobileKeyboard && mobOk,
            wifiRtt, wifiLoss, mobRtt, mobLoss, mobOk, dualPath);
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
    boolean isCableSession(){return running && cableSession;}
    boolean isCellularSession(){return running && cellularSession;}
    boolean hasActiveMobileUdpStream(){return running && mobileUdpChannel != null;}
    boolean isBusyInLease(){return running && (claim>0 || active || reverseLease>0);}

    static javax.net.ssl.SSLSocketFactory createPinnedSslFactory(String pinnedFingerprint) throws Exception {
        final byte[] expected = hex(pinnedFingerprint.toUpperCase(Locale.ROOT));
        javax.net.ssl.TrustManager[] managers = new javax.net.ssl.TrustManager[] {
            new javax.net.ssl.X509TrustManager() {
                @Override public java.security.cert.X509Certificate[] getAcceptedIssuers() {
                    return new java.security.cert.X509Certificate[0];
                }
                @Override public void checkClientTrusted(java.security.cert.X509Certificate[] c, String t) throws CertificateException {
                    throw new CertificateException("Client certificates not used");
                }
                @Override public void checkServerTrusted(java.security.cert.X509Certificate[] c, String t)
                        throws java.security.cert.CertificateException {
                    try {
                        if (c == null || c.length == 0) throw new java.security.cert.CertificateException("Missing TLS certificate");
                        byte[] actual = java.security.MessageDigest.getInstance("SHA-256").digest(c[0].getEncoded());
                        if (!java.security.MessageDigest.isEqual(actual, expected))
                            throw new java.security.cert.CertificateException("Fingerprint mismatch");
                    } catch (java.security.cert.CertificateException ce) {
                        throw ce;
                    } catch (Exception ex) {
                        throw new java.security.cert.CertificateException(ex);
                    }
                }
            }
        };
        javax.net.ssl.SSLContext ctx = javax.net.ssl.SSLContext.getInstance("TLS");
        ctx.init(null, managers, new java.security.SecureRandom());
        return ctx.getSocketFactory();
    }
    String connectedFingerprint(){return running ? fingerprint : null;}
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
        connect(device,androidWidth,androidHeight,false,null,null);
    }
    void connect(DesktopDiscovery.Device device,int androidWidth,int androidHeight,
            boolean interactiveUser,Runnable onFailure) {
        connect(device,androidWidth,androidHeight,interactiveUser,null,onFailure);
    }
    void connect(DesktopDiscovery.Device device,int androidWidth,int androidHeight,
            boolean interactiveUser,Runnable onConnected,Runnable onFailure) {
        connect(device,androidWidth,androidHeight,interactiveUser,null,false,onConnected,onFailure);
    }
    void connect(DesktopDiscovery.Device device,int androidWidth,int androidHeight,
            boolean interactiveUser,android.net.Network networkOverride,boolean cellular,
            Runnable onConnected,Runnable onFailure) {
        sender.execute(()->{
            if(running || connecting) {status("Already connected or connecting");return;}
            connecting=true;
            boundNetwork = networkOverride;
            cellularSession = cellular && !device.cable;
            try {
                String pinned=device.fingerprint.toUpperCase(Locale.ROOT);
                if(!pinned.matches("[A-F0-9]{64}")) throw new SecurityException("Invalid TLS fingerprint");
                String saved=trust.get(pinned);
                SSLSocketFactory pinnedFactory = createPinnedSslFactory(pinned);
                SSLSocket ssl=null;
                DataInputStream in=null;
                DataOutputStream out=null;
                Packet ready=null;
                for(int attempt=0;attempt<2;attempt++){
                    boolean tryTrusted=attempt==0 && saved!=null;
                    if (networkOverride != null) {
                        java.net.Socket raw = new java.net.Socket();
                        networkOverride.bindSocket(raw);
                        raw.setTcpNoDelay(true);
                        raw.setKeepAlive(true);
                        raw.connect(new java.net.InetSocketAddress(
                            java.net.InetAddress.getByName(device.address), 37377),
                            cellular ? 3500 : 5000);
                        ssl = (SSLSocket) pinnedFactory.createSocket(raw, device.address, 37377, true);
                    } else {
                        ssl = (SSLSocket) pinnedFactory.createSocket();
                        ssl.connect(new java.net.InetSocketAddress(device.address, 37377), 5000);
                    }
                    ssl.setTcpNoDelay(true);
                    ssl.setKeepAlive(true);
                    try { ssl.setTrafficClass(0x10); } catch (Exception ignored) {}
                    ssl.setSoTimeout(50000);
                    ssl.startHandshake();
                    in=new DataInputStream(new BufferedInputStream(ssl.getInputStream()));
                    out=new DataOutputStream(ssl.getOutputStream());
                    new Packet(tryTrusted?"auth-trusted":"auth-request",0,0,0,0,
                        tryTrusted?saved:PhoneScreenGeometry.cleanDeviceName(Build.MANUFACTURER,Build.MODEL)).write(out);
                    ready=Packet.read(in);
                    if(ready!=null && "ready".equals(ready.type)) break;
                    try { ssl.close(); } catch (Exception ignored) {}
                    ssl=null;
                    boolean explicitlyRejected = ready!=null && (
                        "denied".equals(ready.type) ||
                        "trust-denied".equals(ready.type) ||
                        "untrusted".equals(ready.type));
                    if(tryTrusted){
                        if(explicitlyRejected){
                            trust.forget(pinned);
                            if(interactiveUser){
                                status("Saved Windows trust revoked or not matched; requesting fresh approval...");
                                continue;
                            }
                        }else if(ready==null){
                            throw new IOException("Windows connection closed before confirming trust");
                        }
                    }
                    throw new SecurityException("Windows did not approve pairing");
                }
                if(ssl==null || in==null || out==null || ready==null || !"ready".equals(ready.type))
                    throw new SecurityException("Windows did not approve pairing");
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
                // Reconnect must use the same physical display dimensions as
                // the Accessibility pointer and its gesture coordinates.
                PhoneScreenGeometry.Info info=PhoneScreenGeometry.readInfo(application);
                int width=Math.max(200,info.width>0?info.width:androidWidth);
                int height=Math.max(100,info.height>0?info.height:androidHeight);
                if(width!=info.width || height!=info.height)
                    info=new PhoneScreenGeometry.Info(width,height,info.densityDpi,info.refreshHz,
                        info.rotationDegrees,info.deviceName,info.osVersion,info.appVersion);
                JSONArray myLayout=buildLayoutArray(info);
                JSONObject hello=new JSONObject().put("Name",info.deviceName)
                    .put("Layout",myLayout.toString())
                    .put("ReverseInputSupported",canAcceptRemoteInput())
                    .put("ForwardInputSupported",true)
                    .put("Platform","Android")
                    .put("OsVersion",info.osVersion)
                    .put("DensityDpi",info.densityDpi)
                    .put("RefreshHz",info.refreshHz)
                    .put("RotationDegrees",info.rotationDegrees);
                if(!info.appVersion.isEmpty())hello.put("AppVersion",info.appVersion);
                new Packet("hello",0,0,0,0,hello.toString()).write(out);
                synchronized(monitors) {monitors.clear();monitors.addAll(parsed);}
                screenX=minX;screenY=minY;screenWidth=ready.c;screenHeight=ready.d;
                cursorX=parsed.get(0)[0]+parsed.get(0)[2]/2;
                cursorY=parsed.get(0)[1]+parsed.get(0)[3]/2;
                fingerprint=pinned;
                announcedPhoneWidth=info.width;announcedPhoneHeight=info.height;
                announcedPhoneRotation=info.rotationDegrees;
                announcedPhoneRefreshHz=info.refreshHz;
                if(info.refreshHz>0){
                    livePhoneRefreshHz=info.refreshHz;
                    livePhoneFrameTenthsMs=(int)Math.round(10000.0/info.refreshHz);
                    DeskTouchAccessibilityService.setDisplayRefreshHz(info.refreshHz);
                }
                peerInetAddress=ssl.getInetAddress();
                cableSession=device.cable || (peerInetAddress!=null && peerInetAddress.isLoopbackAddress());
                peerName=device.name;
                peerHostAddress=peerInetAddress!=null ? peerInetAddress.getHostAddress() : device.address;
                if(!cableSession && peerHostAddress!=null && !peerHostAddress.isEmpty()
                    && !"127.0.0.1".equals(peerHostAddress)) {
                    DeskTouchRuntime.rememberPeerHost(pinned,peerHostAddress);
                }
                liveLinkBand=cableSession
                    ? WifiLinkMonitor.BAND_CABLE
                    : (cellularSession ? WifiLinkMonitor.BAND_CELLULAR : WifiLinkMonitor.BAND_WIRELESS);
                liveLinkSpeedMbps=0;
                liveRttMs=0.0;
                missedPings=0;
                cableTracker.onLinkProfileChanged(WifiLinkMonitor.BAND_CABLE,0,0);
                java.util.Arrays.fill(tcpPingTokens,0);
                socket=ssl;input=in;output=out;running=true;active=false;claim=0;
                long nowUp=SystemClock.uptimeMillis();
                connectedAtMs=nowUp;
                lastPongReceivedMs=nowUp;
                reverseDedup.reset();
                AutoConnectService.onConnectionCountChanged(application);
                registerDisplayListener();
                if(canAcceptRemoteInput() ||
                    !DeskTouchAccessibilityService.isPausedForBanking(application))
                    RemoteInputPower.begin(application);
                // Keep TLS timeout moderate (~15s); heartbeat fast-fail catches dead cable/Wi-Fi in ~4s.
                ssl.setSoTimeout(15000);
                if(cableSession) DeskTouchAccessibilityService.setLinkMetrics(0.0,0.0);
                String transportLabel = cableSession
                    ? " via data cable"
                    : (cellularSession ? " via mobile network" : "");
                status("Connected to "+device.name+transportLabel+". Windows approved pairing.");
                if(cellularSession) {
                    liveLinkBand=WifiLinkMonitor.BAND_CELLULAR;
                    send(new Packet("link-info",0,0,0,0,WifiLinkMonitor.BAND_CELLULAR));
                } else if(!cableSession) {
                    WifiLinkMonitor.LinkSnapshot snap=WifiLinkMonitor.inspect(application,false,0);
                    publishLinkProfile(snap);
                }
                ShellInputClient.warmConnectIfConfigured(application);
                publishKeyboardBindings();
                if(onConnected!=null) onConnected.run();
                DeskTouchRuntime.notifyLiveStatusChanged();
                Thread heartbeat=new Thread(()->{
                    while(running){
                        try {
                            long sleepMs=computeHeartbeatIntervalMs(cableSession,reverseLease>0||hasClaim(),liveLinkBand);
                            Thread.sleep(sleepMs);
                            if(!running) return;
                            long nowMs=SystemClock.uptimeMillis();
                            if(shouldFastFailDeadSession(nowMs,lastPongReceivedMs,connectedAtMs,missedPings,cableSession)){
                                status("Connection timed out ("+(cableSession?"data cable":"wireless")+")");
                                closeConnection();
                                return;
                            }
                            sendTrackedPing(nowMs);
                        }
                        catch(InterruptedException e){Thread.currentThread().interrupt();return;}
                    }
                },"DeskTouch heartbeat");
                heartbeat.setDaemon(true); heartbeat.start();
                Thread reader=new Thread(this::readLoop,"DeskTouch Android input authority");
                reader.setDaemon(true);reader.start();
            } catch(Exception e) {
                status("Connection failed: "+e.getMessage());
                closeConnection();
                if(onFailure!=null)onFailure.run();
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
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY);
        } catch (Throwable ignored) {}
        try {
            while(running){
                Packet p=Packet.read(input);
                lastPongReceivedMs=SystemClock.uptimeMillis();
                missedPings=0;
                if("endpoints".equals(p.type)){
            if(p.text!=null && !p.text.isEmpty() && fingerprint!=null){
                try {
                    JSONObject obj=new JSONObject(p.text);
                    JSONArray arr=obj.optJSONArray("v6");
                    if(arr!=null){
                        ArrayList<String> list=new ArrayList<>();
                        for(int i=0;i<arr.length();i++){
                            String ip=arr.optString(i,"");
                            if(!ip.isEmpty()) list.add(ip);
                        }
                        if(!list.isEmpty()) DeskTouchRuntime.rememberPeerEndpoints(fingerprint,list);
                    }
                } catch(Exception ignored) {}
            }
        }else if("udp-channel".equals(p.type)){
                    if(!cableSession) setupUdpChannel(p);
                } else if("mobile-udp-channel".equals(p.type)){
                    if(!cableSession) acceptMobileUdpCredentials(p);
                } else if("ping".equals(p.type)){
                    if(!controlSender.isShutdown()){
                        final Packet pongPacket=new Packet("pong",p.a,p.b);
                        controlSender.execute(()->writeNow(pongPacket));
                    }
                } else if("environment-roster".equals(p.type)){
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
                    String token;
                    synchronized(trustLock){
                        if(pendingTrust==null || !acceptPendingTrustResponse(pendingTrustId,p.a)){
                            token=null;
                        }else{
                            token=pendingTrust;
                            pendingTrust=null;
                            pendingTrustId=0;
                            pendingTrustAtMs=0L;
                        }
                    }
                    if(token!=null){
                        try{
                            trust.save(fingerprint,token);
                            AutoConnectService.resumePeer(application,fingerprint);
                            AutoConnectService.setEnabled(application,true);
                            AutoConnectService.setReconnect(application,true);
                            status("Trusted Windows PC saved securely, without storing its IP address");
                            DeskTouchRuntime.notifyLiveStatusChanged();
                        }catch(Exception ex){
                            String msg=ex.getMessage();
                            status("Could not save trusted PC key on Android"
                                +(msg!=null && !msg.isEmpty() ? ": "+msg : ""));
                        }
                    }
                } else if("trust-offer".equals(p.type)){
                    boolean savedOffer=false;
                    String offerErr=null;
                    String raw=p.text;
                    if(raw!=null && fingerprint!=null && !fingerprint.isEmpty()){
                        int nl=raw.indexOf('\n');
                        String offeredFp=nl>0 ? raw.substring(0,nl).trim() : fingerprint;
                        String offeredToken=nl>0 ? raw.substring(nl+1).trim() : raw.trim();
                        if(offeredFp.equalsIgnoreCase(fingerprint) &&
                            offeredToken.matches("[A-Fa-f0-9]{64}")){
                            try{
                                trust.save(fingerprint,offeredToken);
                                AutoConnectService.resumePeer(application,fingerprint);
                                AutoConnectService.setEnabled(application,true);
                                AutoConnectService.setReconnect(application,true);
                                savedOffer=true;
                                status("Trusted Windows PC saved securely, without storing its IP address");
                                DeskTouchRuntime.notifyLiveStatusChanged();
                            }catch(Exception ex){
                                offerErr=ex.getMessage();
                            }
                        }
                    }
                    send(new Packet(savedOffer?"trust-accepted":"trust-denied",p.a));
                    if(!savedOffer && offerErr!=null && !offerErr.isEmpty()){
                        status("Could not save trusted PC key on Android: "+offerErr);
                    }
                } else if("trust-denied".equals(p.type)){
                    boolean matched;
                    synchronized(trustLock){
                        matched=pendingTrust!=null && acceptPendingTrustResponse(pendingTrustId,p.a);
                        if(matched){
                            pendingTrust=null;
                            pendingTrustId=0;
                            pendingTrustAtMs=0L;
                        }
                    }
                    if(matched){
                        status("Windows declined trusted-device permission");
                        DeskTouchRuntime.notifyLiveStatusChanged();
                    }
                } else if("pong".equals(p.type)){
                    long nowMs=SystemClock.uptimeMillis();
                    double rttMs=-1.0;
                    int token=p.a;
                    if(token>0){
                        int slot=token%tcpPingTokens.length;
                        synchronized(pingTrackLock){
                            if(tcpPingTokens[slot]==token){
                                tcpPingTokens[slot]=0;
                                long elapsedNs=System.nanoTime()-tcpPingSentNanos[slot];
                                if(elapsedNs>0L && elapsedNs<10_000_000_000L){
                                    rttMs=Math.max(0.2,elapsedNs/1_000_000.0);
                                }
                            }
                        }
                    }
                    if(rttMs<0.0 && lastPingSentMs>0L){
                        long diffMs=nowMs-lastPingSentMs;
                        if(diffMs>=0L && diffMs<=5000L) rttMs=Math.max(0.5,diffMs);
                    }
                    if(rttMs>0.0 && rttMs<=5000.0){
                        if(cableSession){
                            cableTracker.recordRttSample(rttMs,nowMs);
                            liveRttMs=0.0;
                        } else {
                            FastUdpChannel ch=udpChannel;
                            if(ch!=null && ch.confirmed){
                                // Do not pollute fast UDP RTT & jitter metrics with slow TCP TLS heartbeat spikes.
                                liveRttMs=0.0;
                            } else if(ch!=null){
                                ch.linkTracker.recordRttSample(rttMs,nowMs);
                                syncAdaptiveMetrics(ch);
                            } else {
                                liveRttMs=rttMs;
                            }
                        }
                        DeskTouchRuntime.notifyLiveStatusChanged();
                    }
                } else if("display-metrics".equals(p.type)){
                    if(p.a>=24 && p.a<=360){
                        peerTargetRefreshHz=p.a;
                        FastUdpChannel ch=udpChannel;
                        if(ch!=null) syncAdaptiveMetrics(ch);
                    }
                } else if(allowedWindowsControlType(p.type)){
                    handleWindowsControl(p);
                } else if("shell-input-bridge".equals(p.type)){
                    ShellInputClient.configure(application,p.a,p.text);
                    publishKeyboardBindings();
                    DeskTouchRuntime.notifyLiveStatusChanged();
                } else if("cable-bridge-ready".equals(p.type)){
                    AutoConnectService.onCableBridgeReadyHint(application,fingerprint);
                } else if("keyboard-layout".equals(p.type)){
                    pcKeyboardLayout=p.text!=null ? p.text : "";
                    publishKeyboardBindings();
                    DeskTouchRuntime.notifyLiveStatusChanged();
                } else if("pause-accessibility".equals(p.type)){
                    handlePauseAccessibilityFromWindows();
                } else if("goodbye".equals(p.type)) break;
            }
        } catch(Exception e){
            if(running){
                String msg=e.getMessage();
                status(msg!=null && !msg.isEmpty()
                    ? "Windows disconnected: "+msg
                    : "Windows connection closed");
            }
        }
        finally{closeConnection();}
    }
    // Windows forwards approved local or environment-relayed input through
    // the SAME authenticated session. Native phone touch retains priority.
    private static JSONArray buildLayoutArray(PhoneScreenGeometry.Info info)
            throws org.json.JSONException {
        JSONArray array=new JSONArray();
        JSONObject primary=new JSONObject()
            .put("Name","Android screen")
            .put("X",0)
            .put("Y",0)
            .put("Width",info.width)
            .put("Height",info.height)
            .put("Primary",true)
            .put("DensityDpi",info.densityDpi)
            .put("RefreshHz",info.refreshHz)
            .put("RotationDegrees",info.rotationDegrees)
            .put("Orientation",info.orientation)
            .put("DeviceName",info.deviceName)
            .put("OsVersion",info.osVersion);
        if(!info.appVersion.isEmpty()) primary.put("AppVersion",info.appVersion);
        array.put(primary);
        return array;
    }
    private void registerDisplayListener(){
        DisplayManager dm=(DisplayManager)application.getSystemService(Context.DISPLAY_SERVICE);
        if(dm==null || displayListener!=null) return;
        DisplayManager.DisplayListener listener=new DisplayManager.DisplayListener(){
            @Override public void onDisplayAdded(int displayId){}
            @Override public void onDisplayRemoved(int displayId){}
            @Override public void onDisplayChanged(int displayId){
                if(displayId==android.view.Display.DEFAULT_DISPLAY)
                    publishPhoneGeometry();
            }
        };
        displayListener=listener;
        try{dm.registerDisplayListener(listener,new Handler(Looper.getMainLooper()));}
        catch(RuntimeException ignored){displayListener=null;}
    }
    private void unregisterDisplayListener(){
        DisplayManager.DisplayListener listener=displayListener;
        displayListener=null;
        if(listener==null)return;
        DisplayManager dm=(DisplayManager)application.getSystemService(Context.DISPLAY_SERVICE);
        if(dm!=null)try{dm.unregisterDisplayListener(listener);}catch(RuntimeException ignored){}
    }
    private void warmOrProbeUdp(){
        FastUdpChannel ch=udpChannel;
        if(ch==null || udpPacer.isShutdown())return;
        udpPacer.execute(()->{
            if(ch.confirmed)ch.sendWarm();
            else ch.sendProbe();
        });
    }
    private void emitReverseActive(int lease){
        if(reverseLease==lease && reverseApplied!=lease){
            reverseApplied=lease;
            FastUdpChannel ch=udpChannel;
            if(ch!=null && ch.confirmed && !udpPacer.isShutdown()){
                udpPacer.execute(()->{
                    FastUdpChannel c=udpChannel;
                    if(c!=null && c.confirmed && reverseLease==lease) c.sendReverseActive(lease);
                });
            }
            send(new Packet("reverse-active",lease));
        }
    }

    private int readAndroidPointerSpeed(){
        try{
            return android.provider.Settings.System.getInt(
                application.getContentResolver(),"pointer_speed",0);
        }catch(Exception ignored){
            return 0;
        }
    }

    private int currentRefreshHz(){
        int hz=livePhoneRefreshHz;
        if(hz<24){
            hz=PhoneScreenGeometry.readInfo(application).refreshHz;
        }
        int peak=PhoneScreenGeometry.readPeakRefreshHz(application);
        if(peak>hz && peak<=360){
            hz=peak;
        }
        return hz>=24 && hz<=360 ? hz : 60;
    }

    private void handleReverseClaim(int claim, int dispatchSeq, boolean fromUdp){
        if(claim<=0){
            send(new Packet("reverse-busy",claim));
            return;
        }
        int ptrSpeed=readAndroidPointerSpeed();
        int hz=currentRefreshHz();
        boolean preparedShell=false;
        if(reverseLease!=claim && ShellInputClient.hasBridgeConfig(application)){
            preparedShell=ShellInputClient.prepareReverseClaim(application,ptrSpeed,hz);
        }
        boolean granted;
        boolean chosenShell=false;
        synchronized(reverseDedup){
            if(!reverseDedup.tryAcceptClaimDispatch(claim,dispatchSeq,reverseLease)) return;
            boolean shellReady=preparedShell || ShellInputClient.ready(application);
            boolean accReady=DeskTouchAccessibilityService.ready(application);
            chosenShell=shouldUseShellBridge(shellReady,accReady);
            if(hasClaim() ||
                (!shellReady && !accReady) ||
                (reverseLease>0 && reverseLease!=claim) ||
                !DeskTouchRuntime.claimAndroidControl(this)){
                granted=false;
            }else{
                reverseLease=claim;
                reverseUsingShell=chosenShell;
                reverseApplied=0;
                reportedMissingKeyboard=0;
                lastUdpReverseMoveAtMs=0;
                granted=true;
            }
        }
        FastUdpChannel ch=udpChannel;
        if(ch!=null && ch.confirmed){
            if(Looper.myLooper()==Looper.getMainLooper() && !udpPacer.isShutdown()){
                udpPacer.execute(()->{
                    FastUdpChannel c=udpChannel;
                    if(c!=null && c.confirmed) c.sendReverseGranted(claim,granted);
                });
            }else{
                ch.sendReverseGranted(claim,granted);
            }
        }
        if(!granted){
            send(new Packet("reverse-busy",claim));
            return;
        }
        if(chosenShell){
            if(!preparedShell) ShellInputClient.beginRemote(application,ptrSpeed,hz);
            DeskTouchAccessibilityService.setNativeCursorRefreshVote(true);
        }
        DeskTouchAccessibilityService.setPointerFeelMode(readPointerFeelMode(application));
        send(new Packet("reverse-granted",claim));
        if(!fromUdp) warmOrProbeUdp();
        RemoteInputPower.begin(application);
        status("Windows is allowed to control this Android screen.");
    }

    private void handleWindowsControl(Packet p){
        if("reverse-claim".equals(p.type)){
            handleReverseClaim(p.a,p.c,false);
            return;
        }
        if("reverse-release".equals(p.type)||"reverse-reset".equals(p.type)){
            if("reverse-reset".equals(p.type) || p.a==reverseLease)
                clearReverseControl(false);
            return;
        }
        if(reverseLease<=0 || !isActiveBackendReady()){
            clearReverseControl(false);
            return;
        }
        if("reverse-move".equals(p.type)){
            FastUdpChannel ch=udpChannel;
            if(p.c<=0 && ch!=null && ch.confirmed && reverseApplied==reverseLease &&
                SystemClock.uptimeMillis()-lastUdpReverseMoveAtMs<40){
                return;
            }
            int currX = FastUdpChannel.unpackCurr(p.a);
            int prevX = FastUdpChannel.unpackPrev(p.a);
            int currY = FastUdpChannel.unpackCurr(p.b);
            int prevY = FastUdpChannel.unpackPrev(p.b);
            applyReversePointerMove(prevX,prevY,currX,currY,(short)(p.d>>>16),(short)(p.d&0xFFFF),reverseLease,p.c,false);
        }else if("reverse-button".equals(p.type)){
            dispatchReverseButton(p.a,p.b!=0,p.c,false);
        }else if("reverse-wheel".equals(p.type)){
            dispatchReverseWheel(p.a,p.b!=0,p.c,false);
        }else if("reverse-key".equals(p.type)){
            dispatchReverseKey(p.d,p.c!=0,p.a>0 ? p.a : 0,false);
        }else if("reverse-text".equals(p.type) && p.text!=null &&
            p.text.length()>0 && p.text.length()<=512){
            final int activeLease=reverseLease;
            Runnable accepted=onReverseInputAccepted(activeLease);
            Runnable rejected=onReverseInputRejected(activeLease);
            if(reverseUsingShell) ShellInputClient.text(p.text,accepted,rejected);
            else DeskTouchAccessibilityService.text(p.text,accepted,rejected);
        }
    }
    static boolean shouldUseShellBridge(boolean shellReady, boolean accessibilityReady){
        return shellReady;
    }
    private boolean canAcceptRemoteInput(){
        return ShellInputClient.ready(application) || DeskTouchAccessibilityService.ready(application);
    }
    private boolean isActiveBackendReady(){
        return reverseUsingShell
            ? (ShellInputClient.ready(application) || ShellInputClient.hasBridgeConfig(application))
            : DeskTouchAccessibilityService.ready(application);
    }
    private void dispatchReverseButton(int button,boolean down,int actionSeq,boolean fromUdp){
        if(fromUdp) lastUdpReverseMoveAtMs=SystemClock.uptimeMillis();
        if(!tryAcceptControlAction(actionSeq,true)) return;
        if(reverseUsingShell) ShellInputClient.mouseButton(button,down);
        else DeskTouchAccessibilityService.mouseButton(button,down);
    }
    private void dispatchReverseWheel(int amount,boolean horizontal,int actionSeq,boolean fromUdp){
        if(fromUdp) lastUdpReverseMoveAtMs=SystemClock.uptimeMillis();
        if(!tryAcceptControlAction(actionSeq,false)) return;
        if(reverseUsingShell) ShellInputClient.wheel(amount,horizontal);
        else DeskTouchAccessibilityService.wheel(amount,horizontal);
    }
    private void dispatchReverseKey(int vk,boolean down,int actionSeq,boolean fromUdp){
        if(fromUdp) lastUdpReverseMoveAtMs=SystemClock.uptimeMillis();
        if(!tryAcceptKeyAction(actionSeq)) return;
        final int activeLease=reverseLease;
        Runnable accepted=onReverseInputAccepted(activeLease);
        Runnable rejected=onReverseInputRejected(activeLease);
        if(reverseUsingShell) ShellInputClient.key(vk,down,accepted,rejected);
        else DeskTouchAccessibilityService.key(vk,down,accepted,rejected);
    }
    private Runnable onReverseInputAccepted(int activeLease){
        return ()->{ if(reverseLease==activeLease)reportedMissingKeyboard=0; };
    }
    private Runnable onReverseInputRejected(int activeLease){
        return ()->{
            // Never send text, character values or typed data back to
            // the hub; report only the failed authority lease number.
            if(reverseLease==activeLease && reportedMissingKeyboard!=activeLease){
                reportedMissingKeyboard=activeLease;
                send(new Packet("reverse-key-unavailable",activeLease));
            }
        };
    }
    private void applyReversePointerMove(int x,int y,int lease,int moveSeq,boolean fromUdp){
        applyReversePointerMove(x,y,x,y,0,0,lease,moveSeq,fromUdp);
    }
    private void applyReversePointerMove(int x,int y,int pushX,int pushY,int lease,int moveSeq,boolean fromUdp){
        applyReversePointerMove(x,y,x,y,pushX,pushY,lease,moveSeq,fromUdp);
    }
    private void applyReversePointerMove(int prevX,int prevY,int x,int y,int pushX,int pushY,int lease,int moveSeq,boolean fromUdp){
        final boolean posted;
        synchronized(reverseDedup){
            if(!running || lease<=0 || lease!=reverseLease
                || !isActiveBackendReady()) return;
            if(!reverseDedup.tryAcceptMove(moveSeq)) return;
            if(fromUdp) lastUdpReverseMoveAtMs=SystemClock.uptimeMillis();
            Runnable ack=reverseApplied==lease ? null : ()->emitReverseActive(lease);
            posted=reverseUsingShell
                ? ShellInputClient.movePointer(prevX,prevY,x,y,pushX,pushY,ack)
                : DeskTouchAccessibilityService.pointer(prevX,prevY,x,y,ack);
        }
        if(!posted)revokeRemoteFromNative();
    }
    private void clearReverseControl(boolean notifyWindows){
        int lease;
        synchronized(reverseDedup){
            lease=reverseLease;reverseLease=0;reverseApplied=0;reverseUsingShell=false;
        }
        DeskTouchRuntime.releaseAndroidControl(this);
        if(!running && lease>0)RemoteInputPower.end();
        ShellInputClient.endRemote();
        DeskTouchAccessibilityService.setNativeCursorRefreshVote(false);
        DeskTouchAccessibilityService.endRemote();
        if(notifyWindows && lease>0 && running)
            send(new Packet("reverse-local-active",lease));
        if(lease>0 && running && !cableSession && !cellularSession && mobileUdpChannel==null) {
            LiveStatusSnapshot snap=getLiveSnapshot();
            if(snap.recentPeakLossPermille>=10.0) {
                AutoConnectService.onLeaseEndedDegraded(application);
            }
        }
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
        warmOrProbeUdp();
        send(new Packet("claim",claim));
        status("Requesting Windows mouse and keyboard authority");
    }
    static int resolveLiveRefreshHz(int accessibilityHz, int displayHz){
        if(accessibilityHz>=24 && accessibilityHz<=360) return accessibilityHz;
        if(displayHz>=24 && displayHz<=360) return displayHz;
        return 60;
    }

    private int packCurrentDisplayRate(){
        int accHz=DeskTouchAccessibilityService.liveEffectiveRefreshHz();
        int hz=reverseUsingShell && reverseLease>0
            ? currentRefreshHz()
            : resolveLiveRefreshHz(accHz, livePhoneRefreshHz>0 ? livePhoneRefreshHz : announcedPhoneRefreshHz);
        int tenths=(reverseUsingShell && reverseLease>0)
            ? (int)Math.round(10000.0/hz)
            : (accHz>=24
                ? DeskTouchAccessibilityService.liveEffectiveFrameTenthsMs()
                : (livePhoneFrameTenthsMs>0 ? livePhoneFrameTenthsMs : (int)Math.round(10000.0/hz)));
        return AdaptiveLinkTracker.packDisplayRate(hz,tenths);
    }

    private synchronized void sendTrackedPing(long nowMs){
        if(!running || controlSender.isShutdown()) return;
        int token=(++tcpPingSeq)&0x7FFF;
        if(token==0) token=(++tcpPingSeq)&0x7FFF;
        final int pingToken=token;
        final int slot=pingToken%tcpPingTokens.length;
        lastPingSentMs=nowMs;
        missedPings++;
        controlSender.execute(()->{
            if(!running) return;
            DataOutputStream out=output;
            if(out==null) return;
            try {
                synchronized(writeLock){
                    if(!running || output==null) return;
                    synchronized(pingTrackLock){
                        tcpPingTokens[slot]=pingToken;
                        tcpPingSentNanos[slot]=System.nanoTime();
                    }
                    new Packet("ping",pingToken).write(output);
                }
            } catch(Exception e){
                status("Connection lost: "+e.getMessage());
                closeConnection();
            }
        });
    }

    private void syncAdaptiveMetrics(FastUdpChannel ch){
        if(ch==null) return;
        if(ch.linkTracker.getPeerRefreshHz()>0){
            peerTargetRefreshHz=ch.linkTracker.getPeerRefreshHz();
        }
        double rtt=ch.linkTracker.getSmoothedRttMs();
        if(rtt>0.0) liveRttMs=rtt;
        double jitter=ch.linkTracker.getEffectiveJitterMs();
        double loss=ch.linkTracker.getInboundLossPermille();
        DeskTouchAccessibilityService.setLinkMetrics(jitter,loss);
        lastAdaptiveMoveIntervalMs=ch.linkTracker.computeAdaptiveSendIntervalMs(
            peerTargetRefreshHz,liveLinkBand);
    }

    void publishLinkProfile(WifiLinkMonitor.LinkSnapshot snap){
        if(!running || snap==null) return;
        if(cableSession){
            liveLinkBand=WifiLinkMonitor.BAND_CABLE;
            liveLinkSpeedMbps=0;
            DeskTouchRuntime.notifyLiveStatusChanged();
            return;
        }
        if(cellularSession){
            liveLinkBand=WifiLinkMonitor.BAND_CELLULAR;
            liveLinkSpeedMbps=0;
            DeskTouchRuntime.notifyLiveStatusChanged();
            return;
        }
        String newBand=AdaptiveLinkTracker.normalizeBand(snap.band);
        boolean wasDifferent=!newBand.equals(liveLinkBand);
        liveLinkBand=newBand;
        liveLinkSpeedMbps=Math.max(0,snap.linkSpeedMbps);
        FastUdpChannel ch=udpChannel;
        boolean shifted=false;
        if(ch!=null){
            shifted=ch.linkTracker.onLinkProfileChanged(newBand,liveLinkSpeedMbps,snap.epoch);
            if(shifted || wasDifferent){
                liveRttMs=0.0;
                syncAdaptiveMetrics(ch);
                long now=SystemClock.uptimeMillis();
                if(ch.confirmed && !udpPacer.isShutdown()){
                    int pace=(int)Math.round(lastAdaptiveMoveIntervalMs*10.0);
                    int lease=claim>0 ? claim : reverseLease;
                    udpPacer.execute(()->ch.sendLinkPing(
                        ch.linkTracker.nextPingToken(SystemClock.uptimeMillis()),pace,lease));
                    lastUdpLinkPingMs=now;
                }
            }
        } else if(wasDifferent){
            liveRttMs=0.0;
        }
        send(new Packet("link-info",liveLinkSpeedMbps,snap.rssiDbm,snap.epoch,snap.frequencyMhz,newBand));
        if(shifted || wasDifferent){
            sendTrackedPing(SystemClock.uptimeMillis());
        }
        DeskTouchRuntime.notifyLiveStatusChanged();
    }

    void publishKeyboardBindings(){
        boolean shellReady=ShellInputClient.ready(application);
        boolean accReady=DeskTouchAccessibilityService.ready(application);
        KeyboardBindingInspector.KeyboardReport report=KeyboardBindingInspector.inspect(
            application,shellReady,accReady,pcKeyboardLayout);
        lastKeyboardReport=report;
        if(running){
            send(new Packet("keyboard-info",0,0,0,0,report.toWireJson().toString()));
        }
    }

    void onLiveRefreshRateChanged(int refreshHz, int frameTenthsMs){
        if(!running || refreshHz<24 || refreshHz>360) return;
        int tenths=frameTenthsMs>0 ? frameTenthsMs : (int)Math.round(10000.0/refreshHz);
        boolean changed=refreshHz!=livePhoneRefreshHz || Math.abs(tenths-livePhoneFrameTenthsMs)>=3;
        livePhoneRefreshHz=refreshHz;
        livePhoneFrameTenthsMs=tenths;
        DeskTouchAccessibilityService.setDisplayRefreshHz(refreshHz);
        if(changed && reverseLease>0 && reverseUsingShell){
            ShellInputClient.beginRemote(application,readAndroidPointerSpeed(),currentRefreshHz());
        }

        long now=SystemClock.uptimeMillis();
        FastUdpChannel ch=udpChannel;
        if(ch!=null && ch.confirmed && (changed || now-lastUdpFeedbackPushMs>=120L)){
            lastUdpFeedbackPushMs=now;
            if(!udpPacer.isShutdown()){
                int packedLj=ch.linkTracker.getInboundPackedLossJitter();
                int packedDr=AdaptiveLinkTracker.packDisplayRate(refreshHz,tenths);
                int leaseSnapshot=reverseLease;
                udpPacer.execute(()->ch.sendLinkFeedback(0,packedLj,packedDr,leaseSnapshot));
            }
        }
        if(changed || now-lastTcpMetricsSentMs>=400L){
            lastTcpMetricsSentMs=now;
            send(new Packet("display-metrics",refreshHz,tenths));
        }
    }

    void publishPhoneGeometry(){
        if(!running)return;
        PhoneScreenGeometry.Info info=PhoneScreenGeometry.readInfo(application);
        if(info.refreshHz>0 && info.refreshHz!=announcedPhoneRefreshHz){
            announcedPhoneRefreshHz=info.refreshHz;
            onLiveRefreshRateChanged(info.refreshHz,(int)Math.round(10000.0/info.refreshHz));
        }
        if(info.width==announcedPhoneWidth && info.height==announcedPhoneHeight &&
            info.rotationDegrees==announcedPhoneRotation)return;
        if(info.width<200||info.height<100)return;
        try {
            JSONArray layout=buildLayoutArray(info);
            announcedPhoneWidth=info.width;announcedPhoneHeight=info.height;
            announcedPhoneRotation=info.rotationDegrees;
            // Publish rotations on the existing authenticated TLS link.
            send(new Packet("topology",0,0,0,0,layout.toString()));
            if(reverseLease>0 && reverseUsingShell)
                ShellInputClient.beginRemote(application,readAndroidPointerSpeed(),currentRefreshHz());
        }catch(org.json.JSONException ignored){}
    }
    void publishAccessibilityCapability(){
        // A permission restored after an Android package update must become
        // visible on the SAME authenticated environment connection, without
        // asking the user to disconnect or pair the phone again.
        if(running)send(new Packet("android-capability",
            canAcceptRemoteInput()?1:0));
    }

    private void handlePauseAccessibilityFromWindows(){
        boolean disabled=DeskTouchAccessibilityService.pauseForBanking(
            application,"Turned off from Windows");
        status(disabled
            ? "Windows turned off Android Accessibility for payment apps. Connection stays open."
            : "Pause requested from Windows; finish turning off DeskTouch in Accessibility settings.");
    }

    void releaseControl(){
        int lease=claim;
        claim=0;active=false;
        if(lease>0 && running)send(new Packet("release",lease));
        clearReverseControl(true);
        status("Input control released; connections remain available.");
    }
    static boolean acceptPendingTrustResponse(int expectedId, int responseId){
        return responseId==0 || responseId==expectedId;
    }

    static String buildPendingTrustNotice(){
        return "Trust approval is already waiting on Windows — click Yes on the PC dialog";
    }

    void requestTrust(){
        if(!running){status("Pair with Windows first");return;}
        int reqId;
        String proposal;
        synchronized(trustLock){
            long now=SystemClock.uptimeMillis();
            if(pendingTrust!=null && now-pendingTrustAtMs<55_000L){
                status(buildPendingTrustNotice());
                return;
            }
            byte[] secret=new byte[32];RANDOM.nextBytes(secret);
            StringBuilder s=new StringBuilder();
            for(byte b:secret)s.append(String.format(Locale.ROOT,"%02X",b&255));
            proposal=s.toString();
            pendingTrust=proposal;
            reqId=nextTrustId++;
            if(nextTrustId<=0)nextTrustId=1;
            pendingTrustId=reqId;
            pendingTrustAtMs=now;
        }
        send(new Packet("trust-request",reqId,0,0,0,proposal));
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
    private FastUdpChannel.Listener createUdpListener(final boolean isMobile) {
        return new FastUdpChannel.Listener() {
            @Override public void onProbeConfirmed() {
                if (isMobile) {
                    ScheduledFuture<?> retry = mobileUdpProbeRetryFuture;
                    if (retry != null) retry.cancel(false);
                    ScheduledFuture<?> to = mobileUdpTimeoutFuture;
                    if (to != null) to.cancel(false);
                    long now = SystemClock.uptimeMillis();
                    boolean changed = false;
                    synchronized (streamPrefLock) {
                        int mouseMode = readMouseMode(application);
                        int kbMode = readKeyboardMode(application);
                        String savedMouse = readRememberedStream("mouse");
                        String savedKb = readRememberedStream("keyboard");
                        boolean wantMobMouse = mouseMode == STREAM_MODE_FORCE_MOBILE
                            || (mouseMode == STREAM_MODE_AUTO && "cellular".equals(savedMouse));
                        boolean wantMobKb = kbMode == STREAM_MODE_FORCE_MOBILE
                            || (kbMode == STREAM_MODE_AUTO && "cellular".equals(savedKb));
                        if (wantMobMouse != preferMobileMouse) {
                            preferMobileMouse = wantMobMouse;
                            lastMouseSwitchAtMs = now;
                            wifiPostMouseOffloadAtMs = preferMobileMouse ? now : 0L;
                            wifiPostOffloadLostAnchor = -1L;
                            changed = true;
                        }
                        if (preferMobileMouse && "cellular".equals(savedMouse)) lastSavedMouseStream = "cellular";
                        else if (!preferMobileMouse && "wifi".equals(savedMouse)) lastSavedMouseStream = "wifi";
                        if (wantMobKb != preferMobileKeyboard) {
                            preferMobileKeyboard = wantMobKb;
                            lastKeyboardSwitchAtMs = now;
                            changed = true;
                        }
                        if (preferMobileKeyboard && "cellular".equals(savedKb)) lastSavedKeyboardStream = "cellular";
                        else if (!preferMobileKeyboard && "wifi".equals(savedKb)) lastSavedKeyboardStream = "wifi";
                        if (connectedStreamSinceMs <= 0L) connectedStreamSinceMs = now;
                    }
                    sendStreamPreference();
                    FastUdpChannel ch = mobileUdpChannel;
                    if (running && ch != null && !udpPacer.isShutdown()) {
                        udpPacer.execute(() -> {
                            FastUdpChannel c = mobileUdpChannel;
                            if (running && c == ch && c.confirmed) {
                                int pace = (int) Math.round(lastAdaptiveMoveIntervalMs * 10.0);
                                c.sendLinkPing(c.linkTracker.nextPingToken(SystemClock.uptimeMillis()), pace, reverseLease);
                            }
                        });
                    }
                    android.util.Log.i("DeskTouch", String.format(java.util.Locale.US,
                        "Mobile UDP receiver confirmed alongside Wi-Fi (Mouse:%s, Keyboard:%s)",
                        preferMobileMouse ? "Mobile" : "Wi-Fi",
                        preferMobileKeyboard ? "Mobile" : "Wi-Fi"));
                    evaluateLossTriggeredStreams();
                    DeskTouchRuntime.notifyLiveStatusChanged();
                } else {
                    ScheduledFuture<?> retry = udpProbeRetryFuture;
                    if (retry != null) retry.cancel(false);
                    if (running) {
                        send(new Packet("udp-ack"));
                        sendStreamPreference();
                    }
                }
            }
            @Override public void onReverseMove(int x, int y, int lease, int moveSeq) {
                applyReversePointerMove(x, y, x, y, 0, 0, lease, moveSeq, true);
            }
            @Override public void onReverseMove(int x, int y, int pushX, int pushY, int lease, int moveSeq) {
                applyReversePointerMove(x, y, x, y, pushX, pushY, lease, moveSeq, true);
            }
            @Override public void onReverseDualMove(int prevX, int prevY, int x, int y, int pushX, int pushY, int lease, int moveSeq) {
                applyReversePointerMove(prevX, prevY, x, y, pushX, pushY, lease, moveSeq, true);
            }
            @Override public void onReverseClaim(int claim, int dispatchSeq) {
                if (!running) return;
                handleReverseClaim(claim, dispatchSeq, true);
            }
            @Override public void onReverseButton(int button, int down, int lease, int actionSeq) {
                if (!running || lease <= 0 || lease != reverseLease
                    || !isActiveBackendReady()) return;
                dispatchReverseButton(button, down != 0, actionSeq, true);
            }
            @Override public void onReverseWheel(int amount, int horizontal, int lease, int actionSeq) {
                if (!running || lease <= 0 || lease != reverseLease
                    || !isActiveBackendReady()) return;
                dispatchReverseWheel(amount, horizontal != 0, actionSeq, true);
            }
            @Override public void onReverseKey(int vk, boolean down, int lease, int actionSeq) {
                if (!running || lease <= 0 || lease != reverseLease
                    || !isActiveBackendReady()) return;
                dispatchReverseKey(vk, down, actionSeq, true);
            }
            @Override public void onLinkPing(int pingToken, int senderPaceTenths, int lease, int ts) {
                if (!running) return;
                FastUdpChannel activeCh = isMobile ? mobileUdpChannel : udpChannel;
                if (activeCh == null) return;
                activeCh.sendLinkFeedback(
                    pingToken,
                    activeCh.linkTracker.getInboundPackedLossJitter(),
                    packCurrentDisplayRate(),
                    reverseLease);
                if (!isMobile || preferMobileMouse) {
                    syncAdaptiveMetrics(activeCh);
                }
                evaluateLossTriggeredStreams();
                DeskTouchRuntime.notifyLiveStatusChanged();
            }
            @Override public void onLinkFeedback(int echoToken, int packedLossJitter, int packedDisplayRate, int lease) {
                if (!running) return;
                FastUdpChannel activeCh = isMobile ? mobileUdpChannel : udpChannel;
                if (activeCh != null) {
                    if (!isMobile || preferMobileMouse) {
                        syncAdaptiveMetrics(activeCh);
                    }
                    evaluateLossTriggeredStreams();
                    DeskTouchRuntime.notifyLiveStatusChanged();
                }
            }
        };
    }

    private String streamMemoryKey(String role) {
        String fp = fingerprint;
        if (fp == null || fp.isEmpty()) return null;
        String band = AdaptiveLinkTracker.normalizeBand(liveLinkBand);
        if (band.isEmpty()) band = WifiLinkMonitor.BAND_WIRELESS;
        return fp + "|" + role + "|" + band;
    }

    private String readRememberedStream(String role) {
        String key = streamMemoryKey(role);
        if (key == null) return "";
        try {
            return application.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
                .getString(key, "");
        } catch (Exception ignored) {
            return "";
        }
    }

    private void rememberCleanStreamIfNeeded(String role, String stream) {
        String key = streamMemoryKey(role);
        if (key == null || stream == null || stream.isEmpty()) return;
        if ("mouse".equals(role)) {
            if (stream.equals(lastSavedMouseStream)) return;
            lastSavedMouseStream = stream;
        } else {
            if (stream.equals(lastSavedKeyboardStream)) return;
            lastSavedKeyboardStream = stream;
        }
        try {
            application.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
                .edit().putString(key, stream).apply();
        } catch (Exception ignored) {
        }
    }

    static int readMouseMode(Context context) {
        if (context == null) return STREAM_MODE_AUTO;
        int v = context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .getInt(PREF_MOUSE_MODE, STREAM_MODE_AUTO);
        return (v == STREAM_MODE_FORCE_WIFI || v == STREAM_MODE_FORCE_MOBILE) ? v : STREAM_MODE_AUTO;
    }

    static void writeMouseMode(Context context, int mode) {
        if (context == null) return;
        int clamped = (mode == STREAM_MODE_FORCE_WIFI || mode == STREAM_MODE_FORCE_MOBILE) ? mode : STREAM_MODE_AUTO;
        context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .edit().putInt(PREF_MOUSE_MODE, clamped).apply();
    }

    static int readKeyboardMode(Context context) {
        if (context == null) return STREAM_MODE_AUTO;
        int v = context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .getInt(PREF_KB_MODE, STREAM_MODE_AUTO);
        return (v == STREAM_MODE_FORCE_WIFI || v == STREAM_MODE_FORCE_MOBILE) ? v : STREAM_MODE_AUTO;
    }

    static void writeKeyboardMode(Context context, int mode) {
        if (context == null) return;
        int clamped = (mode == STREAM_MODE_FORCE_WIFI || mode == STREAM_MODE_FORCE_MOBILE) ? mode : STREAM_MODE_AUTO;
        context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .edit().putInt(PREF_KB_MODE, clamped).apply();
    }

    static boolean readDualPathControls(Context context) {
        return context == null || context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .getBoolean(PREF_DUAL_PATH_CONTROLS, true);
    }

    static void writeDualPathControls(Context context, boolean enabled) {
        if (context == null) return;
        context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(PREF_DUAL_PATH_CONTROLS, enabled).apply();
    }

    static boolean readKeepChannelsWarm(Context context) {
        return context == null || context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .getBoolean(PREF_KEEP_CHANNELS_WARM, true);
    }

    static void writeKeepChannelsWarm(Context context, boolean enabled) {
        if (context == null) return;
        context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(PREF_KEEP_CHANNELS_WARM, enabled).apply();
    }

    static int readPointerFeelMode(Context context) {
        if (context == null) return PointerSmoother.FEEL_AUTO;
        int v = context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .getInt(PREF_POINTER_FEEL, PointerSmoother.FEEL_AUTO);
        return (v == PointerSmoother.FEEL_RAW_DIRECT || v == PointerSmoother.FEEL_SMOOTH)
            ? v : PointerSmoother.FEEL_AUTO;
    }

    static void writePointerFeelMode(Context context, int mode) {
        if (context == null) return;
        int clamped = (mode == PointerSmoother.FEEL_RAW_DIRECT || mode == PointerSmoother.FEEL_SMOOTH)
            ? mode : PointerSmoother.FEEL_AUTO;
        context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE)
            .edit().putInt(PREF_POINTER_FEEL, clamped).apply();
    }

    static void clearStreamMemory(Context context) {
        if (context == null) return;
        try {
            android.content.SharedPreferences prefs = context.getSharedPreferences(STREAM_MEMORY_PREFS, Context.MODE_PRIVATE);
            android.content.SharedPreferences.Editor ed = prefs.edit();
            for (String k : prefs.getAll().keySet()) {
                if (k != null && (k.contains("|mouse|") || k.contains("|keyboard|"))) {
                    ed.remove(k);
                }
            }
            ed.apply();
        } catch (Exception ignored) {
        }
    }

    void resetStreamStateAndReapply() {
        synchronized (streamPrefLock) {
            lastSavedMouseStream = "";
            lastSavedKeyboardStream = "";
            preferMobileMouse = false;
            preferMobileKeyboard = false;
            wifiPostMouseOffloadAtMs = 0L;
            wifiPostOffloadLostAnchor = -1L;
            lastMouseSwitchAtMs = 0L;
            lastKeyboardSwitchAtMs = 0L;
        }
        applyStreamOptions();
    }

    void applyStreamOptions() {
        DeskTouchAccessibilityService.setPointerFeelMode(readPointerFeelMode(application));
        if (!running || cableSession || cellularSession) return;
        int mouseMode = readMouseMode(application);
        int kbMode = readKeyboardMode(application);
        boolean wantPunch = false;
        synchronized (streamPrefLock) {
            FastUdpChannel mobCh = mobileUdpChannel;
            boolean mobOk = mobCh != null && mobCh.confirmed;
            if (mouseMode == STREAM_MODE_FORCE_WIFI) {
                preferMobileMouse = false;
                wifiPostMouseOffloadAtMs = 0L;
                wifiPostOffloadLostAnchor = -1L;
            } else if (mouseMode == STREAM_MODE_FORCE_MOBILE) {
                if (mobOk) {
                    preferMobileMouse = true;
                    wifiPostMouseOffloadAtMs = SystemClock.uptimeMillis();
                    wifiPostOffloadLostAnchor = -1L;
                } else {
                    wantPunch = true;
                }
            }
            if (kbMode == STREAM_MODE_FORCE_WIFI) {
                preferMobileKeyboard = false;
            } else if (kbMode == STREAM_MODE_FORCE_MOBILE) {
                if (mobOk) preferMobileKeyboard = true;
                else wantPunch = true;
            }
        }
        sendStreamPreference();
        if (wantPunch) {
            mobilePunchAttemptedOnce = false;
            mobilePunchBackoffUntilMs = 0L;
            AutoConnectService.onMobileUdpCredentialsReady(application);
        }
        evaluateLossTriggeredStreams();
        DeskTouchRuntime.notifyLiveStatusChanged();
    }

    private void sendStreamPreference() {
        if (!running) return;
        boolean mobMouse = preferMobileMouse;
        boolean mobKb = preferMobileKeyboard;
        boolean dualPath = readDualPathControls(application);
        boolean keepWarm = readKeepChannelsWarm(application);
        String label = (mobMouse || mobKb) ? "cellular" : "wifi";
        send(new Packet("stream-prefer", mobMouse ? 1 : 0, mobKb ? 1 : 0,
            dualPath ? 1 : 2, keepWarm ? 1 : 2, label));
    }

    private void evaluateLossTriggeredStreams() {
        if (!running || cableSession || cellularSession) return;
        long now = SystemClock.uptimeMillis();
        synchronized (streamPrefLock) {
            if (!running || cableSession || cellularSession) return;
            FastUdpChannel wifiCh = udpChannel;
            FastUdpChannel mobCh = mobileUdpChannel;
            if (wifiCh == null) return;
            double wifiLoss = Math.max(wifiCh.linkTracker.getInboundLossPermille(),
                wifiCh.linkTracker.getPeerLossPermille());
            if (mobCh == null || !mobCh.confirmed) {
                if (connectedStreamSinceMs > 0L
                    && now - connectedStreamSinceMs >= STREAM_SAVE_CLEAN_DELAY_MS
                    && wifiLoss == 0.0) {
                    if (!preferMobileMouse) rememberCleanStreamIfNeeded("mouse", "wifi");
                    if (!preferMobileKeyboard) rememberCleanStreamIfNeeded("keyboard", "wifi");
                }
                return;
            }
            long lastMobArrive = mobCh.linkTracker.getLastArriveMs();
            long latestSwitch = Math.max(lastMouseSwitchAtMs, lastKeyboardSwitchAtMs);
            boolean silentMobileDrop = (preferMobileMouse || preferMobileKeyboard)
                && lastMobArrive > 0 && now - lastMobArrive > 3500L && now - latestSwitch > 4000L;
            if (silentMobileDrop) {
                preferMobileMouse = false;
                preferMobileKeyboard = false;
                lastMouseSwitchAtMs = now;
                lastKeyboardSwitchAtMs = now;
                lastSavedMouseStream = "";
                lastSavedKeyboardStream = "";
                sendStreamPreference();
                DeskTouchRuntime.notifyLiveStatusChanged();
                closeMobileUdpChannel(true, lastCellularHelper);
                mobilePunchBackoffUntilMs = now + 20_000L;
                return;
            }

            double mobLoss = Math.max(mobCh.linkTracker.getInboundLossPermille(),
                mobCh.linkTracker.getPeerLossPermille());
            boolean changed = false;
            int mouseMode = readMouseMode(application);
            int kbMode = readKeyboardMode(application);

            double activeMouseLoss = preferMobileMouse ? mobLoss : wifiLoss;
            double candMouseLoss = preferMobileMouse ? wifiLoss : mobLoss;
            if (mouseMode == STREAM_MODE_FORCE_WIFI) {
                if (preferMobileMouse) {
                    preferMobileMouse = false;
                    lastMouseSwitchAtMs = now;
                    wifiPostMouseOffloadAtMs = 0L;
                    wifiPostOffloadLostAnchor = -1L;
                    changed = true;
                }
            } else if (mouseMode == STREAM_MODE_FORCE_MOBILE) {
                if (!preferMobileMouse) {
                    preferMobileMouse = true;
                    lastMouseSwitchAtMs = now;
                    wifiPostMouseOffloadAtMs = now;
                    wifiPostOffloadLostAnchor = -1L;
                    changed = true;
                }
            } else if (CellularLinkHelper.shouldSwitchStreamOnLoss(
                activeMouseLoss, candMouseLoss, now, lastMouseSwitchAtMs, STREAM_SWITCH_COOLDOWN_MS)) {
                preferMobileMouse = !preferMobileMouse;
                lastMouseSwitchAtMs = now;
                lastSavedMouseStream = "";
                if (preferMobileMouse) {
                    wifiPostMouseOffloadAtMs = now;
                    wifiPostOffloadLostAnchor = -1L;
                } else {
                    wifiPostMouseOffloadAtMs = 0L;
                    wifiPostOffloadLostAnchor = -1L;
                }
                changed = true;
            } else {
                long mouseStableSince = lastMouseSwitchAtMs > 0L ? lastMouseSwitchAtMs : connectedStreamSinceMs;
                if (mouseStableSince > 0L && now - mouseStableSince >= STREAM_SAVE_CLEAN_DELAY_MS
                    && activeMouseLoss == 0.0) {
                    rememberCleanStreamIfNeeded("mouse", preferMobileMouse ? "cellular" : "wifi");
                }
            }

            // Track clean post-offload Wi-Fi baseline after Mouse leaves Wi-Fi so decaying mouse-burst
            // EWMA does not drag Keyboard onto slower Mobile network or block fast Keyboard return.
            if (preferMobileMouse && wifiPostMouseOffloadAtMs > 0L) {
                long lostNow = wifiCh.linkTracker.getLostPackets();
                if (wifiPostOffloadLostAnchor < 0L) {
                    if (now - wifiPostMouseOffloadAtMs >= MOUSE_UNLOAD_ANCHOR_DELAY_MS) {
                        wifiPostOffloadLostAnchor = lostNow;
                    }
                } else if (lostNow > wifiPostOffloadLostAnchor) {
                    wifiPostMouseOffloadAtMs = 0L;
                    wifiPostOffloadLostAnchor = -1L;
                }
            } else if (!preferMobileMouse && wifiPostMouseOffloadAtMs > 0L) {
                wifiPostMouseOffloadAtMs = 0L;
                wifiPostOffloadLostAnchor = -1L;
            }

            boolean kbHoldForMouseUnload = preferMobileMouse && wifiPostMouseOffloadAtMs > 0L
                && now - wifiPostMouseOffloadAtMs < MOUSE_UNLOAD_GRACE_MS;
            double effectiveWifiKbLoss = (preferMobileMouse && wifiPostMouseOffloadAtMs > 0L
                && wifiPostOffloadLostAnchor >= 0L) ? 0.0 : wifiLoss;
            double activeKbLoss = preferMobileKeyboard ? mobLoss : effectiveWifiKbLoss;
            double candKbLoss = preferMobileKeyboard ? effectiveWifiKbLoss : mobLoss;
            double wifiRtt = wifiCh.linkTracker.getSmoothedRttMs();
            double mobRtt = mobCh.linkTracker.getSmoothedRttMs();
            double activeKbRtt = preferMobileKeyboard ? mobRtt : wifiRtt;
            double candKbRtt = preferMobileKeyboard ? wifiRtt : mobRtt;

            if (kbMode == STREAM_MODE_FORCE_WIFI) {
                if (preferMobileKeyboard) {
                    preferMobileKeyboard = false;
                    lastKeyboardSwitchAtMs = now;
                    changed = true;
                }
            } else if (kbMode == STREAM_MODE_FORCE_MOBILE) {
                if (!preferMobileKeyboard) {
                    preferMobileKeyboard = true;
                    lastKeyboardSwitchAtMs = now;
                    changed = true;
                }
            } else {
                boolean kbLossSwitch = !kbHoldForMouseUnload && CellularLinkHelper.shouldSwitchStreamOnLoss(
                    activeKbLoss, candKbLoss, now, lastKeyboardSwitchAtMs, STREAM_SWITCH_COOLDOWN_MS);
                boolean kbFasterZeroLossSwitch = !kbHoldForMouseUnload && !kbLossSwitch
                    && CellularLinkHelper.shouldSwitchKeyboardToFasterZeroLoss(
                        activeKbRtt, activeKbLoss, candKbRtt, candKbLoss,
                        now, lastKeyboardSwitchAtMs, KEYBOARD_FAST_SWITCH_COOLDOWN_MS);
                if (kbLossSwitch || kbFasterZeroLossSwitch) {
                    preferMobileKeyboard = !preferMobileKeyboard;
                    lastKeyboardSwitchAtMs = now;
                    lastSavedKeyboardStream = "";
                    changed = true;
                } else {
                    long kbStableSince = lastKeyboardSwitchAtMs > 0L ? lastKeyboardSwitchAtMs : connectedStreamSinceMs;
                    if (kbStableSince > 0L && now - kbStableSince >= STREAM_SAVE_CLEAN_DELAY_MS
                        && activeKbLoss == 0.0) {
                        rememberCleanStreamIfNeeded("keyboard", preferMobileKeyboard ? "cellular" : "wifi");
                    }
                }
            }

            if (changed) {
                sendStreamPreference();
                DeskTouchRuntime.notifyLiveStatusChanged();
                android.util.Log.i("DeskTouch", String.format(java.util.Locale.US,
                    "Updated receive streams -> Mouse:%s Keyboard:%s (Wi-Fi %.1fms/%.1f%%, Mobile %.1fms/%.1f%%)",
                    preferMobileMouse ? "Mobile" : "Wi-Fi",
                    preferMobileKeyboard ? "Mobile" : "Wi-Fi",
                    wifiRtt, wifiLoss / 10.0, mobRtt, mobLoss / 10.0));
            }
        }
    }

    private void acceptMobileUdpCredentials(Packet p) {
        if (p.a <= 0 || p.a > 65535 || p.text == null || p.text.isEmpty()) return;
        try {
            byte[] key = Base64.getDecoder().decode(p.text);
            if (key.length != FastUdpChannel.KEY_SIZE) return;
            mobileUdpPort = p.a;
            mobileUdpPeerId = p.b;
            mobileUdpKey = key;
            AutoConnectService.onMobileUdpCredentialsReady(application);
        } catch (Exception ignored) {
        }
    }

    void evaluateLocalMobileReceiver(Context context, CellularLinkHelper helper) {
        if (!running || cableSession || cellularSession || helper == null) return;
        this.lastCellularHelper = helper;
        long now = SystemClock.uptimeMillis();
        FastUdpChannel mobCh = mobileUdpChannel;
        if (mobCh != null && !mobCh.confirmed) {
            return; // Punch still in progress
        }
        if (mobCh != null) {
            // Both streams are active; switching runs event-driven on loss/speed feedback, not periodic polling.
            return;
        }
        if (mobileUdpPort <= 0 || mobileUdpKey == null) return;
        java.util.List<String> pcEndpoints = DeskTouchRuntime.peerEndpoints(fingerprint);
        if (pcEndpoints.isEmpty()) {
            AutoConnectService.scheduleMobileRetry(application, 600L);
            return;
        }
        FastUdpChannel wifiCh = udpChannel;
        double wifiLoss = 0.0;
        if (wifiCh != null) {
            wifiLoss = Math.max(wifiCh.linkTracker.getInboundLossPermille(),
                wifiCh.linkTracker.getPeerLossPermille());
        }
        boolean forceMobile = readMouseMode(application) == STREAM_MODE_FORCE_MOBILE
            || readKeyboardMode(application) == STREAM_MODE_FORCE_MOBILE;
        boolean rememberCell = "cellular".equals(readRememberedStream("mouse"))
            || "cellular".equals(readRememberedStream("keyboard"));
        boolean wifiLossTriggered = wifiLoss >= 10.0;
        if (mobilePunchAttemptedOnce && !wifiLossTriggered && !rememberCell && !forceMobile) return;
        if (now < mobilePunchBackoffUntilMs) return;

        android.net.Network cellNet = helper.acquireCellularNetwork(4500L);
        if (cellNet == null) {
            mobilePunchBackoffUntilMs = now + 1500L;
            AutoConnectService.scheduleMobileRetry(application, 1500L);
            return;
        }
        java.util.List<String> myCellV6 = CellularLinkHelper.getGlobalCellularIpv6Addresses(
            helper.connectivityManager(), cellNet);
        if (myCellV6.isEmpty()) {
            android.util.Log.i("DeskTouch", "Mobile UDP receiver skipped: no global IPv6 address on cellular link");
            helper.releaseCellularNetwork();
            mobilePunchBackoffUntilMs = now + 30_000L;
            return;
        }
        mobilePunchAttemptedOnce = true;
        try {
            java.util.ArrayList<InetAddress> targets = new java.util.ArrayList<>();
            for (String s : pcEndpoints) {
                try { targets.add(InetAddress.getByName(s)); }
                catch (Exception ignored) {}
            }
            if (targets.isEmpty()) {
                helper.releaseCellularNetwork();
                return;
            }
            FastUdpChannel ch = new FastUdpChannel(
                targets.get(0), mobileUdpPort, mobileUdpPeerId, mobileUdpKey,
                createUdpListener(true), cellNet, targets);
            ch.linkTracker.onLinkProfileChanged(WifiLinkMonitor.BAND_CELLULAR, 0, 0);
            mobileUdpChannel = ch;
            JSONArray cellArr = new JSONArray(myCellV6);
            send(new Packet("mobile-udp-punch", ch.localPort(), 0, 0, 0, cellArr.toString()));
            ch.sendProbe();
            android.util.Log.i("DeskTouch", "Started two-way local mobile UDP punch from "
                + myCellV6.get(0) + ":" + ch.localPort() + " while keeping Wi-Fi");
            final int[] attempts = {0};
            if (!udpPacer.isShutdown()) {
                mobileUdpProbeRetryFuture = udpPacer.scheduleWithFixedDelay(() -> {
                    if (!running || mobileUdpChannel != ch || ch.confirmed || ++attempts[0] > 14) {
                        ScheduledFuture<?> f = mobileUdpProbeRetryFuture;
                        if (f != null) f.cancel(false);
                        return;
                    }
                    ch.sendProbe();
                }, 120, 180, TimeUnit.MILLISECONDS);
                mobileUdpTimeoutFuture = udpPacer.schedule(() -> {
                    if (running && mobileUdpChannel == ch && !ch.confirmed) {
                        android.util.Log.i("DeskTouch",
                            "Mobile UDP receiver punch timed out (router or carrier blocked two-way UDP); keeping Wi-Fi");
                        mobilePunchBackoffUntilMs = SystemClock.uptimeMillis() + 60_000L;
                        closeMobileUdpChannel(true, helper);
                    }
                }, 3800, TimeUnit.MILLISECONDS);
                mobileUdpKeepAliveFuture = udpPacer.scheduleWithFixedDelay(() -> {
                    if (!running || mobileUdpChannel != ch || !ch.confirmed) return;
                    long t = SystemClock.uptimeMillis();
                    long gap = ch.linkTracker.computeProbeIntervalMs(
                        (preferMobileMouse || preferMobileKeyboard) && isBusyInLease());
                    if (t - lastMobileUdpLinkPingMs >= gap) {
                        lastMobileUdpLinkPingMs = t;
                        int pace = (int) Math.round(lastAdaptiveMoveIntervalMs * 10.0);
                        ch.sendLinkPing(ch.linkTracker.nextPingToken(t), pace, reverseLease);
                    }
                }, 350, 350, TimeUnit.MILLISECONDS);
            }
        } catch (Exception ex) {
            android.util.Log.w("DeskTouch", "Mobile UDP setup skipped: " + ex.getMessage());
            closeMobileUdpChannel(true, helper);
            mobilePunchBackoffUntilMs = now + 45_000L;
        }
    }

    private void setupUdpChannel(Packet p){
        InetAddress addr=peerInetAddress;
        if(addr==null || p.a<=0 || p.a>65535 || p.text==null || p.text.isEmpty()) return;
        try {
            byte[] key=Base64.getDecoder().decode(p.text);
            if(key.length!=FastUdpChannel.KEY_SIZE) return;
            closeUdpChannel();
            FastUdpChannel ch=new FastUdpChannel(addr,p.a,p.b,key,createUdpListener(false), boundNetwork);
            ch.linkTracker.onLinkProfileChanged(liveLinkBand,liveLinkSpeedMbps,0);
            udpChannel=ch;
            connectedStreamSinceMs=SystemClock.uptimeMillis();
            lastSavedMouseStream="";
            lastSavedKeyboardStream="";
            send(new Packet("udp-ready",ch.localPort()));
            ch.sendProbe();
            final int[] attempts={0};
            if(!udpPacer.isShutdown()) {
                udpProbeRetryFuture=udpPacer.scheduleWithFixedDelay(()->{
                    if(!running || udpChannel!=ch || ch.confirmed || ++attempts[0]>12){
                        ScheduledFuture<?> f=udpProbeRetryFuture;
                        if(f!=null)f.cancel(false);
                        return;
                    }
                    ch.sendProbe();
                },180,180,TimeUnit.MILLISECONDS);
                final PowerManager keepAlivePm=(PowerManager)application.getSystemService(Context.POWER_SERVICE);
                udpKeepAliveFuture=udpPacer.scheduleWithFixedDelay(()->{
                    if(!running || udpChannel!=ch || !ch.confirmed) return;
                    long now=SystemClock.uptimeMillis();
                    boolean screenInteractive=keepAlivePm==null || keepAlivePm.isInteractive();
                    boolean activeSession=reverseLease>0 || hasClaim();
                    boolean serviceReady=canAcceptRemoteInput();
                    if(activeSession && shouldSendInteractiveKeepalive(
                        running,true,true,serviceReady,screenInteractive,true)){
                        if(readKeepChannelsWarm(application)
                            && now-lastWarmSentMs>=INTERACTIVE_KEEPALIVE_MS
                            && now-lastUdpReverseMoveAtMs>=30L
                            && now-ch.getLastSentMs()>=INTERACTIVE_KEEPALIVE_MS){
                            lastWarmSentMs=now;
                            ch.sendWarm();
                        }
                        long probeGapMs=ch.linkTracker.computeProbeIntervalMs(true);
                        if(now-lastUdpLinkPingMs>=probeGapMs){
                            int paceTenths=(int)Math.round(lastAdaptiveMoveIntervalMs*10.0);
                            int pingLease=claim>0?claim:reverseLease;
                            long lastArr=ch.linkTracker.getLastArriveMs();
                            if(lastArr>0 && now-lastArr<=18L){
                                lastUdpLinkPingMs=now;
                                ch.sendLinkPing(ch.linkTracker.nextPingToken(now),paceTenths,pingLease);
                            }else{
                                lastUdpLinkPingMs=now;
                                ch.requestWakePrimedPing(now,paceTenths,pingLease);
                            }
                        }
                    }else{
                        long idlePingGapMs=screenInteractive
                            ? ch.linkTracker.computeProbeIntervalMs(false)
                            : Math.max(2200L,ch.linkTracker.computeProbeIntervalMs(false));
                        if(now-lastUdpLinkPingMs>=idlePingGapMs){
                            long lastArr=ch.linkTracker.getLastArriveMs();
                            if(lastArr>0 && now-lastArr<=18L){
                                lastUdpLinkPingMs=now;
                                ch.sendLinkPing(ch.linkTracker.nextPingToken(now),0,0);
                            }else{
                                lastUdpLinkPingMs=now;
                                ch.requestWakePrimedPing(now,0,0);
                            }
                        }
                    }
                },INTERACTIVE_KEEPALIVE_MS,INTERACTIVE_KEEPALIVE_MS,TimeUnit.MILLISECONDS);
            }
        } catch(Exception ignored) {
        }
    }
    private void closeUdpChannel(){
        ScheduledFuture<?> keepAlive=udpKeepAliveFuture;
        udpKeepAliveFuture=null;
        if(keepAlive!=null) keepAlive.cancel(false);
        ScheduledFuture<?> retry=udpProbeRetryFuture;
        udpProbeRetryFuture=null;
        if(retry!=null) retry.cancel(false);
        FastUdpChannel old=udpChannel;
        udpChannel=null;
        if(old!=null) old.close();
        clearPendingUdpMoves();
    }

    private void closeMobileUdpChannel(boolean notifyWindows, CellularLinkHelper helper) {
        boolean wasPreferring = preferMobileMouse || preferMobileKeyboard;
        synchronized (streamPrefLock) {
            preferMobileMouse = false;
            preferMobileKeyboard = false;
            lastSavedMouseStream = "";
            lastSavedKeyboardStream = "";
            wifiPostMouseOffloadAtMs = 0L;
            wifiPostOffloadLostAnchor = -1L;
        }
        ScheduledFuture<?> keepAlive = mobileUdpKeepAliveFuture;
        mobileUdpKeepAliveFuture = null;
        if (keepAlive != null) keepAlive.cancel(false);
        ScheduledFuture<?> retry = mobileUdpProbeRetryFuture;
        mobileUdpProbeRetryFuture = null;
        if (retry != null) retry.cancel(false);
        ScheduledFuture<?> to = mobileUdpTimeoutFuture;
        mobileUdpTimeoutFuture = null;
        if (to != null) to.cancel(false);
        FastUdpChannel old = mobileUdpChannel;
        mobileUdpChannel = null;
        if (old != null) old.close();
        if (notifyWindows && running && (old != null || wasPreferring)) {
            send(new Packet("mobile-udp-close"));
        }
        if (helper != null) helper.releaseCellularNetwork();
        if (wasPreferring) DeskTouchRuntime.notifyLiveStatusChanged();
    }
    private Packet clearPendingUdpMoves(){
        synchronized(udpPacerLock){
            throttledUdpMove=null;
            pendingTcpSyncMove=null;
            if(scheduledThrottleFlush!=null){scheduledThrottleFlush.cancel(false);scheduledThrottleFlush=null;}
            if(scheduledTrailingSync!=null){scheduledTrailingSync.cancel(false);scheduledTrailingSync=null;}
            return null;
        }
    }
    private Packet takePendingUdpMoveSync(){
        synchronized(udpPacerLock){
            FastUdpChannel ch=udpChannel;
            Packet latest=throttledUdpMove!=null ? throttledUdpMove
                : (ch!=null && ch.confirmed ? null : pendingTcpSyncMove);
            throttledUdpMove=null;
            pendingTcpSyncMove=null;
            if(scheduledThrottleFlush!=null){scheduledThrottleFlush.cancel(false);scheduledThrottleFlush=null;}
            if(scheduledTrailingSync!=null){scheduledTrailingSync.cancel(false);scheduledTrailingSync=null;}
            return latest;
        }
    }
    private void scheduleUdpTrailingSyncLocked(){
        if(scheduledTrailingSync!=null) scheduledTrailingSync.cancel(false);
        scheduledTrailingSync=udpPacer.schedule(()->{
            Packet sync=null;
            synchronized(udpPacerLock){
                if(throttledUdpMove==null && pendingTcpSyncMove!=null &&
                    SystemClock.uptimeMillis()-lastAnyMoveMs>=42){
                    sync=pendingTcpSyncMove;
                    pendingTcpSyncMove=null;
                }
            }
            if(sync!=null && running && active){
                final Packet toSend=sync;
                sender.execute(()->writeNow(toSend));
            }
        },45,TimeUnit.MILLISECONDS);
    }
    private void send(Packet p){
        if(sender.isShutdown())return;
        if("move".equals(p.type)){
            FastUdpChannel ch=udpChannel;
            int activeClaim=claim;
            if(ch!=null && ch.confirmed && active && activeClaim>0){
                boolean sendNow=false;
                synchronized(udpPacerLock){
                    long now=SystemClock.uptimeMillis();
                    lastAnyMoveMs=now;
                    pendingTcpSyncMove=p;
                    long minMoveMs=Math.max(2L,Math.round(lastAdaptiveMoveIntervalMs));
                    if(now-lastUdpMoveMs>=minMoveMs){
                        lastUdpMoveMs=now;
                        throttledUdpMove=null;
                        if(scheduledThrottleFlush!=null){
                            scheduledThrottleFlush.cancel(false);
                            scheduledThrottleFlush=null;
                        }
                        sendNow=true;
                    } else {
                        throttledUdpMove=p;
                        if(scheduledThrottleFlush==null || scheduledThrottleFlush.isDone()){
                            scheduledThrottleFlush=udpPacer.schedule(()->{
                                Packet deferred=null;
                                int curClaim=claim;
                                synchronized(udpPacerLock){
                                    if(throttledUdpMove!=null){
                                        deferred=throttledUdpMove;
                                        throttledUdpMove=null;
                                        lastUdpMoveMs=SystemClock.uptimeMillis();
                                    }
                                    scheduledThrottleFlush=null;
                                }
                                FastUdpChannel c=udpChannel;
                                if(deferred!=null && c!=null && c.confirmed && active && curClaim>0)
                                    c.send(FastUdpChannel.OP_MOVE,deferred.a,deferred.b,curClaim);
                            },minMoveMs,TimeUnit.MILLISECONDS);
                        }
                    }
                    scheduleUdpTrailingSyncLocked();
                }
                newestMove.set(null);
                if(sendNow){
                    udpPacer.execute(()->{
                        FastUdpChannel c=udpChannel;
                        if(c!=null && c.confirmed && active && activeClaim>0)
                            c.send(FastUdpChannel.OP_MOVE,p.a,p.b,activeClaim);
                    });
                }
                return;
            }
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
        }else {
            final boolean isRelease="release".equals(p.type)||"reset".equals(p.type);
            sender.execute(()->{
                Packet udpSync=isRelease ? clearPendingUdpMoves() : takePendingUdpMoveSync();
                Packet tcpPrior=newestMove.getAndSet(null);
                Packet preceding=isRelease ? tcpPrior : (udpSync!=null ? udpSync : tcpPrior);
                if(preceding!=null)writeNow(preceding);
                writeNow(p);
            });
        }
    }
    private synchronized void closeConnection(){
        unregisterDisplayListener();
        closeUdpChannel();
        closeMobileUdpChannel(false, null);
        mobileUdpPort=0;mobileUdpPeerId=0;mobileUdpKey=null;mobilePunchAttemptedOnce=false;
        clearReverseControl(false);
        reverseDedup.reset();
        synchronized(trustLock){
            pendingTrust=null;pendingTrustId=0;pendingTrustAtMs=0L;
        }
        running=false;active=false;claim=0;peerInetAddress=null;cableSession=false;
        cellularSession=false;boundNetwork=null;
        peerName=null;peerHostAddress=null;liveRttMs=0.0;liveLinkSpeedMbps=0;missedPings=0;
        lastPongReceivedMs=0L;connectedAtMs=0L;
        environmentTargets=java.util.Collections.emptyList();
        selectedEnvironmentId=0;environmentOwnId=0;
        if(DeskTouchRuntime.connectedCount()==0) RemoteInputPower.end();
        else RemoteInputPower.begin(application);
        AutoConnectService.onConnectionCountChanged(application);
        DeskTouchRuntime.notifyLiveStatusChanged();
        SSLSocket sock=socket;socket=null;
        if(sock!=null)try{sock.close();}catch(Exception ignored){}
    }
    void disconnect(){
        closeConnection();
        status("Disconnected. Trusted auto-reconnect paused until enabled.");
    }
    @Override public void close(){
        closeConnection();
        udpPacer.shutdownNow();
        controlSender.shutdownNow();
        sender.shutdownNow();
    }
}
