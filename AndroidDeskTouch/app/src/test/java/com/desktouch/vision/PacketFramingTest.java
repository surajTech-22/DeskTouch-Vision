package com.desktouch.vision;
import org.junit.Test;
import java.io.*;
import static org.junit.Assert.*;
public class PacketFramingTest {
    @Test public void preservesWindowsWireFields() throws Exception {
        Packet sent=new Packet("key",41,1,1,65,"hello");
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();
        sent.write(new DataOutputStream(bytes));
        Packet read=Packet.read(new DataInputStream(new ByteArrayInputStream(bytes.toByteArray())));
        assertEquals("key",read.type);
        assertEquals(41,read.a);
        assertEquals(1,read.b);
        assertEquals(1,read.c);
        assertEquals(65,read.d);
        assertEquals("hello",read.text);
    }
    @Test(expected=IOException.class) public void rejectsOversizedWireFrame() throws Exception {
        ByteArrayOutputStream out=new ByteArrayOutputStream();
        new DataOutputStream(out).writeInt(8193);
        Packet.read(new DataInputStream(new ByteArrayInputStream(out.toByteArray())));
    }
    @Test(expected=IOException.class) public void rejectsTruncatedWireFrame() throws Exception {
        ByteArrayOutputStream out=new ByteArrayOutputStream();
        new DataOutputStream(out).writeInt(10);
        out.write(1);
        Packet.read(new DataInputStream(new ByteArrayInputStream(out.toByteArray())));
    }
}
