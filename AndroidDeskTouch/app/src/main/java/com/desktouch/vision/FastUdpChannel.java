package com.desktouch.vision;

import android.os.Process;
import android.os.SystemClock;
import java.io.Closeable;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * Authenticated, replay-protected 48-byte AES-256-GCM UDP codec and socket
 * ("DTU1") for zero-head-of-line-blocking coordinate streaming.
 */
final class FastUdpChannel implements Closeable {
    static final int KEY_SIZE = 32;
    static final int FRAME_SIZE = 48;

    static final byte DIR_SERVER_TO_CLIENT = 1;
    static final byte DIR_CLIENT_TO_SERVER = 2;

    static final byte OP_REVERSE_MOVE = 1;
    static final byte OP_MOVE = 2;
    static final byte OP_PROBE = 3;
    static final byte OP_PROBE_ACK = 4;
    static final byte OP_WARM = 5;

    static final class Message {
        final byte op;
        final int peerId;
        final long seq;
        final int a;
        final int b;
        final int lease;
        final int timestampMs;

        Message(byte op, int peerId, long seq, int a, int b, int lease, int timestampMs) {
            this.op = op;
            this.peerId = peerId;
            this.seq = seq;
            this.a = a;
            this.b = b;
            this.lease = lease;
            this.timestampMs = timestampMs;
        }
    }

    static final class Codec {
        private static final byte[] MAGIC = {'D', 'T', 'U', '1'};
        private static final byte[] SRV_PREFIX = "SRV1".getBytes(StandardCharsets.US_ASCII);
        private static final byte[] CLI_PREFIX = "CLI1".getBytes(StandardCharsets.US_ASCII);

        private final byte outboundDir;
        private final byte inboundDir;
        private final int peerId;
        private final SecretKeySpec keySpec;
        private final Object sendLock = new Object();
        private final Object recvLock = new Object();
        private final Cipher sendCipher;
        private final Cipher recvCipher;
        private final byte[] sendPlain = new byte[16];
        private final byte[] sendNonce = new byte[12];
        private final byte[] recvPlain = new byte[16];
        private final byte[] recvNonce = new byte[12];
        private long sendSeq;
        private long lastRecvSeq;
        private volatile boolean closed;

        Codec(byte[] key, int peerId, boolean isServer) throws GeneralSecurityException {
            if (key == null || key.length != KEY_SIZE) {
                throw new IllegalArgumentException("Invalid UDP session key length");
            }
            this.peerId = peerId & 0xFFFF;
            this.outboundDir = isServer ? DIR_SERVER_TO_CLIENT : DIR_CLIENT_TO_SERVER;
            this.inboundDir = isServer ? DIR_CLIENT_TO_SERVER : DIR_SERVER_TO_CLIENT;
            this.keySpec = new SecretKeySpec(key, "AES");
            this.sendCipher = Cipher.getInstance("AES/GCM/NoPadding");
            this.recvCipher = Cipher.getInstance("AES/GCM/NoPadding");
            System.arraycopy(outboundDir == DIR_SERVER_TO_CLIENT ? SRV_PREFIX : CLI_PREFIX, 0, sendNonce, 0, 4);
            System.arraycopy(inboundDir == DIR_SERVER_TO_CLIENT ? SRV_PREFIX : CLI_PREFIX, 0, recvNonce, 0, 4);
        }

        int seal(byte op, int a, int b, int lease, int timestampMs, byte[] destination)
                throws GeneralSecurityException {
            if (destination == null || destination.length < FRAME_SIZE) {
                throw new IllegalArgumentException("Destination buffer too small");
            }
            synchronized (sendLock) {
                if (closed) return 0;
                long seq = ++sendSeq;
                destination[0] = MAGIC[0];
                destination[1] = MAGIC[1];
                destination[2] = MAGIC[2];
                destination[3] = MAGIC[3];
                destination[4] = outboundDir;
                destination[5] = op;
                destination[6] = (byte) ((peerId >>> 8) & 0xFF);
                destination[7] = (byte) (peerId & 0xFF);
                writeLongBE(destination, 8, seq);

                writeLongBE(sendNonce, 4, seq);
                writeIntBE(sendPlain, 0, a);
                writeIntBE(sendPlain, 4, b);
                writeIntBE(sendPlain, 8, lease);
                writeIntBE(sendPlain, 12, timestampMs);

                sendCipher.init(Cipher.ENCRYPT_MODE, keySpec, new GCMParameterSpec(128, sendNonce));
                sendCipher.updateAAD(destination, 0, 16);
                sendCipher.doFinal(sendPlain, 0, 16, destination, 16);
                return FRAME_SIZE;
            }
        }

        Message tryOpen(byte[] frame, int length) {
            if (closed || frame == null || length != FRAME_SIZE) return null;
            if (frame[0] != MAGIC[0] || frame[1] != MAGIC[1]
                    || frame[2] != MAGIC[2] || frame[3] != MAGIC[3]) {
                return null;
            }
            if (frame[4] != inboundDir) return null;
            int id = ((frame[6] & 0xFF) << 8) | (frame[7] & 0xFF);
            if (id != peerId) return null;
            byte op = frame[5];
            if (op < OP_REVERSE_MOVE || op > OP_WARM) return null;
            long seq = readLongBE(frame, 8);
            if (seq == 0) return null;

            synchronized (recvLock) {
                if (closed || Long.compareUnsigned(seq, lastRecvSeq) <= 0) return null;
                writeLongBE(recvNonce, 4, seq);
                try {
                    recvCipher.init(Cipher.DECRYPT_MODE, keySpec, new GCMParameterSpec(128, recvNonce));
                    recvCipher.updateAAD(frame, 0, 16);
                    recvCipher.doFinal(frame, 16, 32, recvPlain, 0);
                } catch (GeneralSecurityException badAuth) {
                    return null;
                }
                lastRecvSeq = seq;
                return new Message(
                    op,
                    id,
                    seq,
                    readIntBE(recvPlain, 0),
                    readIntBE(recvPlain, 4),
                    readIntBE(recvPlain, 8),
                    readIntBE(recvPlain, 12)
                );
            }
        }

        void close() {
            closed = true;
            Arrays.fill(sendPlain, (byte) 0);
            Arrays.fill(recvPlain, (byte) 0);
        }
    }

    interface Listener {
        void onProbeConfirmed();
        void onReverseMove(int x, int y, int lease);
    }

    private final DatagramSocket socket;
    private final InetAddress serverAddress;
    private final int serverPort;
    private final Codec codec;
    private final Listener listener;
    private final byte[] sendBuffer = new byte[FRAME_SIZE];
    private final DatagramPacket sendPacket;
    private final Thread recvThread;
    private volatile boolean closed;
    volatile boolean confirmed;

    FastUdpChannel(InetAddress serverAddress, int serverPort, int peerId, byte[] key, Listener listener)
            throws Exception {
        this.serverAddress = serverAddress;
        this.serverPort = serverPort;
        this.listener = listener;
        this.codec = new Codec(key, peerId, false);
        this.socket = new DatagramSocket(0);
        try {
            this.socket.setTrafficClass(0x10);
        } catch (Exception ignored) {
        }
        try {
            this.socket.setReceiveBufferSize(262144);
            this.socket.setSendBufferSize(262144);
        } catch (Exception ignored) {
        }
        this.sendPacket = new DatagramPacket(sendBuffer, FRAME_SIZE, serverAddress, serverPort);
        this.recvThread = new Thread(this::receiveLoop, "DeskTouch FastUdpRecv");
        this.recvThread.setDaemon(true);
        this.recvThread.start();
    }

    int localPort() {
        return socket.getLocalPort();
    }

    void sendProbe() {
        int port = localPort();
        send(OP_PROBE, port, 0, 0);
        send(OP_PROBE, port, 0, 0);
    }

    void sendWarm() {
        send(OP_WARM, 0, 0, 0);
    }

    boolean send(byte op, int a, int b, int lease) {
        if (closed) return false;
        synchronized (sendBuffer) {
            if (closed) return false;
            try {
                int len = codec.seal(op, a, b, lease, (int) SystemClock.uptimeMillis(), sendBuffer);
                if (len <= 0) return false;
                sendPacket.setLength(len);
                socket.send(sendPacket);
                return true;
            } catch (Exception ignored) {
                return false;
            }
        }
    }

    private void receiveLoop() {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY);
        } catch (Throwable ignored) {
        }
        byte[] recvBuf = new byte[256];
        DatagramPacket packet = new DatagramPacket(recvBuf, recvBuf.length);
        while (!closed) {
            try {
                packet.setLength(recvBuf.length);
                socket.receive(packet);
                if (packet.getLength() != FRAME_SIZE) continue;
                if (!serverAddress.equals(packet.getAddress()) || packet.getPort() != serverPort) {
                    continue;
                }
                Message msg = codec.tryOpen(recvBuf, packet.getLength());
                if (msg == null) continue;
                if (msg.op == OP_PROBE) {
                    send(OP_PROBE_ACK, localPort(), 0, 0);
                    markConfirmed();
                } else if (msg.op == OP_PROBE_ACK) {
                    markConfirmed();
                } else if (msg.op == OP_WARM) {
                    markConfirmed();
                } else if (msg.op == OP_REVERSE_MOVE) {
                    markConfirmed();
                    listener.onReverseMove(msg.a, msg.b, msg.lease);
                }
            } catch (Exception e) {
                if (closed) break;
            }
        }
    }

    private void markConfirmed() {
        if (!confirmed) {
            confirmed = true;
            listener.onProbeConfirmed();
        }
    }

    @Override
    public void close() {
        if (closed) return;
        closed = true;
        codec.close();
        try {
            socket.close();
        } catch (Exception ignored) {
        }
    }

    private static void writeIntBE(byte[] buf, int off, int v) {
        buf[off] = (byte) (v >>> 24);
        buf[off + 1] = (byte) (v >>> 16);
        buf[off + 2] = (byte) (v >>> 8);
        buf[off + 3] = (byte) v;
    }

    private static int readIntBE(byte[] buf, int off) {
        return ((buf[off] & 0xFF) << 24)
            | ((buf[off + 1] & 0xFF) << 16)
            | ((buf[off + 2] & 0xFF) << 8)
            | (buf[off + 3] & 0xFF);
    }

    private static void writeLongBE(byte[] buf, int off, long v) {
        buf[off] = (byte) (v >>> 56);
        buf[off + 1] = (byte) (v >>> 48);
        buf[off + 2] = (byte) (v >>> 40);
        buf[off + 3] = (byte) (v >>> 32);
        buf[off + 4] = (byte) (v >>> 24);
        buf[off + 5] = (byte) (v >>> 16);
        buf[off + 6] = (byte) (v >>> 8);
        buf[off + 7] = (byte) v;
    }

    private static long readLongBE(byte[] buf, int off) {
        return ((buf[off] & 0xFFL) << 56)
            | ((buf[off + 1] & 0xFFL) << 48)
            | ((buf[off + 2] & 0xFFL) << 40)
            | ((buf[off + 3] & 0xFFL) << 32)
            | ((buf[off + 4] & 0xFFL) << 24)
            | ((buf[off + 5] & 0xFFL) << 16)
            | ((buf[off + 6] & 0xFFL) << 8)
            | (buf[off + 7] & 0xFFL);
    }
}
