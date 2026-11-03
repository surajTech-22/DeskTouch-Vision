package com.desktouch.vision;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Repeated adapter-wide discovery; busy Windows peers remain visible. */
final class DesktopDiscovery {
    static final class Device {
        final String name,address,fingerprint;
        final boolean ready,android;
        String groupId;
        Device(String n,String a,String f,boolean ready,boolean android) {
            name=n;address=a;fingerprint=f;this.ready=ready;this.android=android;
            groupId=f;
        }
        @Override public String toString() {
            return name+" ("+address+")"+(ready?"":" • "+(android?"Android phone":"Busy"));
        }
    }

    static Device parse(String reply,String source) {
        boolean ready=reply.startsWith("DT1-READY|");
        boolean busy=reply.startsWith("DT1-BUSY|");
        boolean mobile=reply.startsWith("DT1-MOBILE|");
        if(!ready&&!busy&&!mobile)return null;
        String[] parts=reply.substring(reply.indexOf('|')+1).split("\\|",-1);
        if(parts.length!=2||parts[0].isBlank()||
            parts[0].length()>100||!parts[1].matches("[A-Fa-f0-9]{64}"))
            return null;
        return new Device(parts[0],source,parts[1].toUpperCase(Locale.ROOT),
            ready,!ready&&!busy);
    }

    static boolean isRemoteDevice(Device device,String localFingerprint){
        return device!=null && !(device.android &&
            device.fingerprint.equalsIgnoreCase(localFingerprint));
    }
    static void applyConnectedGroups(List<Device> peers,List<String[]> links){
        // Display grouping only; the individual TLS fingerprints are NEVER
        // merged, replaced, or used to authorize another device.
        Map<String,String> parent=new HashMap<>();
        for(Device d:peers)if(!d.android)parent.put(d.fingerprint,d.fingerprint);
        for(String[] edge:links){
            if(edge.length!=2||!parent.containsKey(edge[0])||
               !parent.containsKey(edge[1]))continue;
            String a=root(parent,edge[0]),b=root(parent,edge[1]);
            if(!a.equals(b)){
                if(a.compareTo(b)<0)parent.put(b,a);
                else parent.put(a,b);
            }
        }
        for(Device d:peers)if(!d.android)d.groupId=root(parent,d.fingerprint);
    }
    private static String root(Map<String,String> parent,String id){
        String next=parent.get(id);
        if(next==null)return id;
        while(!next.equals(parent.get(next)))next=parent.get(next);
        return next;
    }
    static List<Device> scan() throws Exception {
        // These IPs exist only during the current discovery and TCP connection;
        // saved identities always use cryptographic fingerprints, never IP.
        Map<String,Device> devices=new LinkedHashMap<>();
        Map<String,String> sourceIpByFingerprint=new HashMap<>();
        List<String[]> pendingLinks=new ArrayList<>();
        // The local phone's beacon is for Windows, not a remote Android peer.
        String ownAndroidIdentity=null;
        try {ownAndroidIdentity=MobileDiscoveryResponder.mobileFingerprint();}
        catch(Exception ignored) { /* keep scanning other devices */ }

        byte[] probe="DT1-DISCOVER".getBytes(StandardCharsets.US_ASCII);
        try(DatagramSocket socket=new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(280);
            Set<InetAddress> destinations=new HashSet<>();
            destinations.add(InetAddress.getByName("255.255.255.255"));
            Enumeration<NetworkInterface> interfaces=NetworkInterface.getNetworkInterfaces();
            while(interfaces.hasMoreElements()) {
                NetworkInterface nic=interfaces.nextElement();
                if(!nic.isUp() || nic.isLoopback()) continue;
                for(InterfaceAddress addr:nic.getInterfaceAddresses())
                    if(addr.getBroadcast()!=null) destinations.add(addr.getBroadcast());
            }
            long deadline=System.nanoTime()+3_800_000_000L;
            int sends=0;
            long nextSend=0;
            while(System.nanoTime()<deadline){
                long now=System.nanoTime();
                if(sends<3 && now>=nextSend){
                    sends++;
                    nextSend=now+900_000_000L;
                    for(InetAddress dest:destinations) {
                        try {socket.send(new DatagramPacket(probe,probe.length,dest,37378));}
                        catch(Exception ignored) { /* some adapters block broadcast */ }
                    }
                }
                byte[] buffer=new byte[512];
                DatagramPacket result=new DatagramPacket(buffer,buffer.length);
                try {socket.receive(result);}
                catch(SocketTimeoutException ex) {continue;}
                String message=new String(result.getData(),0,result.getLength(),
                    StandardCharsets.US_ASCII);
                String ip=result.getAddress().getHostAddress();
                if(message.startsWith("DT1-LINK|")){
                    String[] link=message.split("\\|",-1);
                    if(link.length==3&&link[1].matches("[A-Fa-f0-9]{64}")&&
                        link[2].matches("[A-Fa-f0-9]{64}"))
                        pendingLinks.add(new String[]{link[1].toUpperCase(Locale.ROOT),
                            link[2].toUpperCase(Locale.ROOT),ip});
                    continue;
                }
                Device device=parse(message,ip);
                if(device==null)continue;
                if(!isRemoteDevice(device,ownAndroidIdentity))continue;
                if(result.getAddress().isLoopbackAddress()||
                   result.getAddress().isAnyLocalAddress())continue;
                String key=(device.android?"android:":"windows:")+device.fingerprint;
                Device old=devices.get(key);
                if(old==null||(!old.ready&&device.ready))devices.put(key,device);
                if(!device.android)sourceIpByFingerprint.put(device.fingerprint,ip);
            }
        }
        ArrayList<Device> found=new ArrayList<>(devices.values());
        List<String[]> verifiedLinks=new ArrayList<>();
        for(String[] edge:pendingLinks)
            if(edge.length==3 && edge[2].equals(sourceIpByFingerprint.get(edge[0])))
                verifiedLinks.add(new String[]{edge[0],edge[1]});
        applyConnectedGroups(found,verifiedLinks);
        found.sort(Comparator.comparing((Device d)->d.android)
            .thenComparing(d->d.name,String.CASE_INSENSITIVE_ORDER));
        return found;
    }
}
