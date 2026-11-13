package com.desktouch.vision;

import org.json.JSONException;
import org.json.JSONObject;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

final class Packet {
    final String type;
    final int a,b,c,d;
    final String text;
    Packet(String type) { this(type,0,0,0,0,null); }
    Packet(String type, int a) { this(type,a,0,0,0,null); }
    Packet(String type, int a, int b) { this(type,a,b,0,0,null); }
    Packet(String type, int a, int b, int c, int d) { this(type,a,b,c,d,null); }
    Packet(String type, int a, int b, int c, int d, String text) {
        this.type=type; this.a=a; this.b=b; this.c=c; this.d=d; this.text=text;
    }
    String json() throws JSONException {
        JSONObject o = new JSONObject().put("Type",type).put("A",a).put("B",b).put("C",c).put("D",d);
        if(text != null) o.put("Text",text);
        return o.toString();
    }
    static Packet parse(String s) throws JSONException {
        JSONObject o=new JSONObject(s);
        return new Packet(o.getString("Type"),o.optInt("A"),o.optInt("B"),o.optInt("C"),
                          o.optInt("D"),o.optString("Text",null));
    }
    static Packet read(DataInputStream in) throws IOException, JSONException {
        int size=in.readInt();
        if(size<2 || size>8192) throw new IOException("Invalid DeskTouch frame");
        byte[] data=new byte[size];
        in.readFully(data);
        return parse(new String(data,StandardCharsets.UTF_8));
    }
    void write(DataOutputStream out) throws IOException,JSONException {
        byte[] data=json().getBytes(StandardCharsets.UTF_8);
        if(data.length<2 || data.length>8192) throw new IOException("Invalid packet size");
        // Write header + payload in one call so SSLSocket emits one TLS record
        // instead of separate 4-byte and payload records on every packet.
        byte[] frame=new byte[4+data.length];
        frame[0]=(byte)(data.length>>>24);
        frame[1]=(byte)(data.length>>>16);
        frame[2]=(byte)(data.length>>>8);
        frame[3]=(byte)data.length;
        System.arraycopy(data,0,frame,4,data.length);
        out.write(frame);
        out.flush();
    }
}
