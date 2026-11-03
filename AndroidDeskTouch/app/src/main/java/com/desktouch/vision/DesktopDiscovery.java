package com.desktouch.vision;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class DesktopDiscovery {
    static final class Device {
        final String name,address,fingerprint;
        Device(String n,String a,String f) { name=n;address=a;fingerprint=f; }
        @Override public String toString() {return name+" ("+address+")";}
    }
    static List<Device> scan() throws Exception {
        // The address exists only for this scan/session. The trust record uses the TLS
        // certificate SHA-256 fingerprint, never DHCP address or hostname.
        Map<String,Device> devices=new LinkedHashMap<>();
        byte[] probe="DT1-DISCOVER".getBytes(StandardCharsets.US_ASCII);
        try(DatagramSocket socket=new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(350);
            Set<InetAddress> destinations=new HashSet<>();
            destinations.add(InetAddress.getByName("255.255.255.255"));
            Enumeration<NetworkInterface> interfaces=NetworkInterface.getNetworkInterfaces();
            while(interfaces.hasMoreElements()) {
                NetworkInterface nic=interfaces.nextElement();
                if(!nic.isUp() || nic.isLoopback()) continue;
                for(InterfaceAddress addr:nic.getInterfaceAddresses())
                    if(addr.getBroadcast()!=null) destinations.add(addr.getBroadcast());
            }
            for(InetAddress dest:destinations) {
                try {socket.send(new DatagramPacket(probe,probe.length,dest,37378));}
                catch(Exception ignored) { /* network adapter may block broadcast */ }
            }
            long deadline=System.nanoTime()+2_200_000_000L;
            while(System.nanoTime()<deadline) {
                byte[] buffer=new byte[512];
                DatagramPacket result=new DatagramPacket(buffer,buffer.length);
                try {socket.receive(result);}
                catch(SocketTimeoutException ex) {continue;}
                String message=new String(result.getData(),0,result.getLength(),StandardCharsets.US_ASCII);
                String[] parts=message.split("\\|",-1);
                if(parts.length!=3 || !parts[0].equals("DT1-READY") ||
                   parts[1].isBlank() || !parts[2].matches("[A-Fa-f0-9]{64}")) continue;
                String ip=result.getAddress().getHostAddress();
                devices.put(parts[2].toUpperCase(Locale.ROOT),
                    new Device(parts[1],ip,parts[2].toUpperCase(Locale.ROOT)));
            }
        }
        return new ArrayList<>(devices.values());
    }
}
