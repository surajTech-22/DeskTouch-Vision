package com.desktouch.vision;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.Build;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.spec.ECGenParameterSpec;
import java.util.Locale;

/**
 * Makes the same DeskTouch Android app appear in Windows "Add devices".
 * Android currently initiates the secure session with Windows, so discovery
 * advertises DT1-MOBILE (NOT a falsely ready Windows TCP receiver).
 * Its Keystore-backed public key is a stable identity hint, not authentication.
 */
final class MobileDiscoveryResponder implements AutoCloseable {
    interface OnlineListener {
        void onDesktopOnline(DesktopDiscovery.Device device);
    }

    private static final String KEY_ALIAS="DeskTouchVision.MobileIdentity.v1";
    private DatagramSocket listener;
    private Thread thread;
    private WifiManager.MulticastLock multicastLock;
    private volatile boolean running;
    private volatile OnlineListener onlineListener;

    void setOnlineListener(OnlineListener listener) {
        this.onlineListener=listener;
    }

    static String mobileFingerprint() throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (!store.containsAlias(KEY_ALIAS)) {
            KeyPairGenerator generator=KeyPairGenerator.getInstance("EC","AndroidKeyStore");
            android.security.keystore.KeyGenParameterSpec spec=
                new android.security.keystore.KeyGenParameterSpec.Builder(
                    KEY_ALIAS, android.security.keystore.KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                    .setDigests(android.security.keystore.KeyProperties.DIGEST_SHA256)
                    .build();
            generator.initialize(spec);
            generator.generateKeyPair();
        }
        byte[] digest=MessageDigest.getInstance("SHA-256").digest(
            store.getCertificate(KEY_ALIAS).getPublicKey().getEncoded());
        StringBuilder hex=new StringBuilder(64);
        for(byte b:digest)hex.append(String.format(Locale.ROOT,"%02X",b&0xff));
        return hex.toString();
    }
    synchronized void start(Context context) throws Exception {
        if(running)return;
        String device=PhoneScreenGeometry.cleanDeviceName(Build.MANUFACTURER,Build.MODEL)
            .replace("|","-").replace("\n"," ").replace("\r"," ").trim();
        if(device.length()>85)device=device.substring(0,85);
        String response="DT1-MOBILE|"+device+"|"+mobileFingerprint();
        byte[] payload=response.getBytes(StandardCharsets.US_ASCII);
        WifiManager wifi=(WifiManager)context.getApplicationContext()
            .getSystemService(Context.WIFI_SERVICE);
        if(wifi!=null){
            multicastLock=wifi.createMulticastLock("DeskTouchVision.mobile-discovery");
            multicastLock.setReferenceCounted(false);
            multicastLock.acquire();
        }
        try{
            listener=new DatagramSocket(null);
            listener.setReuseAddress(true);
            listener.bind(new InetSocketAddress("0.0.0.0",37378));
            listener.setSoTimeout(750);
            running=true;
            thread=new Thread(()->{
                while(running){
                    try{
                        byte[] buffer=new byte[256];
                        DatagramPacket request=new DatagramPacket(buffer,buffer.length);
                        listener.receive(request);
                        String probe=new String(request.getData(),0,request.getLength(),
                            StandardCharsets.US_ASCII);
                        InetAddress remote=request.getAddress();
                        if(remote==null || remote.isLoopbackAddress() ||
                            remote.isAnyLocalAddress())continue;
                        byte[] bytes=remote.getAddress();
                        if(bytes.length!=4)continue;
                        int a=bytes[0]&255,b=bytes[1]&255;
                        if(!(a==10||(a==172&&b>=16&&b<=31)||
                            (a==192&&b==168)||(a==169&&b==254)))continue;
                        if("DT1-DISCOVER".equals(probe)) {
                            listener.send(new DatagramPacket(payload,payload.length,
                                remote,request.getPort()));
                            continue;
                        }
                        DesktopDiscovery.Device online=
                            DesktopDiscovery.parseOnline(probe,remote.getHostAddress());
                        OnlineListener notify=onlineListener;
                        if(online!=null && notify!=null) notify.onDesktopOnline(online);
                    }catch(SocketTimeoutException ignored){}
                    catch(Exception ex){if(running)android.util.Log.w("DeskTouch","Discovery reply failed",ex);}
                }
            },"DeskTouch Android device discovery");
            thread.setDaemon(true);
            thread.start();
        }catch(Exception e){
            close();
            throw e;
        }
    }
    @Override public synchronized void close(){
        running=false;
        if(listener!=null)listener.close();
        listener=null;
        if(multicastLock!=null&&multicastLock.isHeld())multicastLock.release();
        multicastLock=null;
    }
}
