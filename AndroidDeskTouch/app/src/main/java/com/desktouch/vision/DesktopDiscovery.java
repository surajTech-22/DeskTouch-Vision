package com.desktouch.vision;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Repeated adapter-wide discovery; busy Windows peers remain visible. */
final class DesktopDiscovery {
    static final class Device {
        final String name,address,fingerprint;
        final boolean ready,android;
        Device(String n,String a,String f,boolean ready,boolean android) {
            name=n;address=a;fingerprint=f;this.ready=ready;this.android=android;
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

    static List<Device> scan() throws Exception {
        // These IPs exist only during the current discovery and TCP connection;
        // saved identities always use cryptographic fingerprints, never IP.
        Map<String,Device> devices=new LinkedHashMap<>();
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
                Device device=parse(message,ip);
                if(device==null)continue;
                String key=(device.android?"android:":"windows:")+device.fingerprint;
                Device old=devices.get(key);
                if(old==null||(!old.ready&&device.ready))devices.put(key,device);
            }
        }
        ArrayList<Device> found=new ArrayList<>(devices.values());
        found.sort(Comparator.comparing((Device d)->d.android)
            .thenComparing(d->d.name,String.CASE_INSENSITIVE_ORDER));
        return found;
    }
}
