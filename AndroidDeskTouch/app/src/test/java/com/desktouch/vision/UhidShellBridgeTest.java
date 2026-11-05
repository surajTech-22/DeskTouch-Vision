package com.desktouch.vision;

import org.junit.Test;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import static org.junit.Assert.*;

/** Validates Linux UHID report framing without shell permissions/device hardware. */
public class UhidShellBridgeTest {
    @Test public void uhidCreateIsNativeVirtualInputNotBluetooth() {
        byte[] request=UhidShellBridge.createRequest("DeskTouch Test Keyboard",
            new byte[] { 5, 1, 9, 6 });
        ByteBuffer b=ByteBuffer.wrap(request).order(ByteOrder.nativeOrder());
        assertEquals(284,request.length);
        assertEquals(11,b.getInt(0)); // UHID_CREATE2
        assertEquals(4,b.getShort(260));
        assertEquals(6,b.getShort(262)); // Linux BUS_VIRTUAL
        assertEquals(0,b.getInt(264)); // no falsified physical vendor
        assertEquals(5,request[280]);
        assertEquals(1,request[281]);
    }
    @Test public void nativeKeyboardAndMouseInputUseUhidInput2() {
        byte[] keys=UhidShellBridge.inputRequest(
            new byte[] { 0x02,0,4,0,0,0,0,0 });
        byte[] mouse=UhidShellBridge.inputRequest(
            new byte[] { 1,12,(byte)-5,0,0 });
        ByteBuffer kb=ByteBuffer.wrap(keys).order(ByteOrder.nativeOrder());
        ByteBuffer mm=ByteBuffer.wrap(mouse).order(ByteOrder.nativeOrder());
        assertEquals(14,keys.length);
        assertEquals(11,mouse.length);
        assertEquals(12,kb.getInt(0));
        assertEquals(8,kb.getShort(4));
        assertEquals(12,mm.getInt(0));
        assertEquals(5,mm.getShort(4));
        assertEquals(-5,mouse[8]);
    }
}
