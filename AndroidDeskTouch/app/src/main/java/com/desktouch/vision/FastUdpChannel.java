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
    static final byte OP_REVERSE_CLAIM = 6;
    static final byte OP_REVERSE_GRANTED = 7;
    static final byte OP_REVERSE_ACTIVE = 8;
    static final byte OP_REVERSE_BUTTON = 9;
    static final byte OP_REVERSE_WHEEL = 10;
    static final byte OP_LINK_PING = 11;
    static final byte OP_LINK_FEEDBACK = 12;
    static final byte OP_REVERSE_EDGE_PUSH = 13;

    static int unpackCurr(int packed) {
        int hi = (packed >>> 16) & 0xFFFF;
        if (hi == 0 || hi > 16384) return packed;
        return packed & 0xFFFF;
    }

    static int unpackPrev(int packed) {
        int hi = (packed >>> 16) & 0xFFFF;
        if (hi == 0 || hi > 16384) return packed;
        return hi - 1;
    }

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
        private long recvWindowMask;
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
            return tryOpen(frame, length, null);
        }

        Message tryOpen(byte[] frame, int length, AdaptiveLinkTracker tracker) {
            if (closed || frame == null || length != FRAME_SIZE) return null;
            if (frame[0] != MAGIC[0] || frame[1] != MAGIC[1]
                    || frame[2] != MAGIC[2] || frame[3] != MAGIC[3]) {
                return null;
            }
            if (frame[4] != inboundDir) return null;
            int id = ((frame[6] & 0xFF) << 8) | (frame[7] & 0xFF);
            if (id != peerId) return null;
            byte op = frame[5];
            if (op < OP_REVERSE_MOVE || op > OP_REVERSE_EDGE_PUSH) return null;
            long seq = readLongBE(frame, 8);
            if (seq == 0) return null;

            synchronized (recvLock) {
                if (closed) return null;
                boolean isCoordinateOp = op == OP_MOVE || op == OP_REVERSE_MOVE || op == OP_REVERSE_EDGE_PUSH;
                boolean isNewMax = lastRecvSeq == 0L || Long.compareUnsigned(seq, lastRecvSeq) > 0;
                long back = 0L;
                long backBit = 0L;
                if (!isNewMax) {
                    back = lastRecvSeq - seq;
                    if (Long.compareUnsigned(back, 64L) >= 0) return null;
                    backBit = 1L << (int) back;
                    if ((recvWindowMask & backBit) != 0L) return null;
                }
                writeLongBE(recvNonce, 4, seq);
                try {
                    recvCipher.init(Cipher.DECRYPT_MODE, keySpec, new GCMParameterSpec(128, recvNonce));
                    recvCipher.updateAAD(frame, 0, 16);
                    recvCipher.doFinal(frame, 16, 32, recvPlain, 0);
                } catch (GeneralSecurityException badAuth) {
                    return null;
                }
                if (lastRecvSeq == 0L) {
                    lastRecvSeq = seq;
                    recvWindowMask = 1L;
                } else if (isNewMax) {
                    long diff = seq - lastRecvSeq;
                    recvWindowMask = Long.compareUnsigned(diff, 64L) >= 0
                        ? 1L
                        : ((recvWindowMask << (int) diff) | 1L);
                    lastRecvSeq = seq;
                } else {
                    recvWindowMask |= backBit;
                    if (isCoordinateOp) {
                        if (tracker != null) tracker.recordAuthenticatedSeqOnly(seq);
                        return null;
                    }
                }
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
        void onReverseMove(int x, int y, int lease, int moveSeq);
        default void onReverseMove(int x, int y, int pushX, int pushY, int lease, int moveSeq) {
            onReverseMove(x, y, lease, moveSeq);
        }
        default void onReverseDualMove(int prevX, int prevY, int x, int y, int pushX, int pushY, int lease, int moveSeq) {
            onReverseMove(x, y, pushX, pushY, lease, moveSeq);
        }
        default void onReverseClaim(int claim, int dispatchSeq) {}
        default void onReverseButton(int button, int down, int lease, int actionSeq) {}
        default void onReverseWheel(int amount, int horizontal, int lease, int actionSeq) {}
        default void onLinkPing(int pingToken, int senderPaceTenths, int lease, int ts) {}
        default void onLinkFeedback(int echoToken, int packedLossJitter, int packedDisplayRate, int lease) {}
    }

    private final DatagramSocket socket;
    private volatile InetAddress serverAddress;
    private volatile int serverPort;
    private volatile java.util.List<InetAddress> extraCandidates = java.util.Collections.emptyList();
    private final Codec codec;
    private final Listener listener;
    final AdaptiveLinkTracker linkTracker = new AdaptiveLinkTracker();
    private final byte[] sendBuffer = new byte[FRAME_SIZE];
    private final DatagramPacket sendPacket;
    private final Thread recvThread;
    private volatile boolean closed;
    volatile boolean confirmed;
    private long highestReverseMoveSeq;
    private volatile long lastSentMs;
    private volatile boolean pendingWakePing;
    private volatile int pendingPingPaceTenths;
    private volatile int pendingPingLease;
    private volatile long lastWakePrimerMs;

    FastUdpChannel(InetAddress serverAddress, int serverPort, int peerId, byte[] key, Listener listener)
            throws Exception {
        this(serverAddress, serverPort, peerId, key, listener, null, null);
    }

    FastUdpChannel(InetAddress serverAddress, int serverPort, int peerId, byte[] key, Listener listener,
            android.net.Network boundNetwork) throws Exception {
        this(serverAddress, serverPort, peerId, key, listener, boundNetwork, null);
    }

    FastUdpChannel(InetAddress serverAddress, int serverPort, int peerId, byte[] key, Listener listener,
            android.net.Network boundNetwork, java.util.List<InetAddress> candidateAddresses) throws Exception {
        this.serverAddress = serverAddress;
        this.serverPort = serverPort;
        this.listener = listener;
        this.codec = new Codec(key, peerId, false);
        if (candidateAddresses != null && !candidateAddresses.isEmpty()) {
            java.util.ArrayList<InetAddress> extras = new java.util.ArrayList<>();
            for (InetAddress c : candidateAddresses) {
                if (c != null && !c.equals(serverAddress)) extras.add(c);
            }
            if (!extras.isEmpty()) {
                this.extraCandidates = java.util.Collections.unmodifiableList(extras);
            }
        }
        if (boundNetwork != null) {
            this.socket = new DatagramSocket(null);
            boundNetwork.bindSocket(this.socket);
            this.socket.bind(new java.net.InetSocketAddress(0));
        } else {
            this.socket = new DatagramSocket(0);
        }
        try {
            this.socket.setTrafficClass(0xB8);
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

    long getLastSentMs() {
        return lastSentMs;
    }

    void sendProbe() {
        int port = localPort();
        send(OP_PROBE, port, 0, 0);
        send(OP_PROBE, port, 0, 0);
        java.util.List<InetAddress> extras = extraCandidates;
        if (!confirmed && extras != null && !extras.isEmpty()) {
            for (InetAddress cand : extras) {
                sendToAddress(cand, serverPort, OP_PROBE, port, 0, 0);
            }
        }
    }

    private void sendToAddress(InetAddress target, int port, byte op, int a, int b, int lease) {
        if (closed || target == null) return;
        synchronized (sendBuffer) {
            if (closed) return;
            try {
                int len = codec.seal(op, a, b, lease, (int) SystemClock.uptimeMillis(), sendBuffer);
                if (len <= 0) return;
                DatagramPacket pkt = new DatagramPacket(sendBuffer, len, target, port);
                socket.send(pkt);
                lastSentMs = SystemClock.uptimeMillis();
            } catch (Exception ignored) {
            }
        }
    }

    void sendWarm() {
        send(OP_WARM, 0, 0, 0);
    }

    void requestWakePrimedPing(long nowMs, int paceTenths, int lease) {
        if (!pendingWakePing) {
            pendingWakePing = true;
            pendingPingPaceTenths = paceTenths;
            pendingPingLease = lease;
            lastWakePrimerMs = nowMs;
            send(OP_WARM, 1, 0, 0);
        } else if (nowMs - lastWakePrimerMs >= 140L) {
            pendingWakePing = false;
            sendLinkPing(linkTracker.nextPingToken(nowMs), paceTenths, lease);
        }
    }

    void sendReverseGranted(int lease, boolean granted) {
        send(OP_REVERSE_GRANTED, lease, granted ? 1 : 0, lease);
    }

    void sendReverseActive(int lease) {
        send(OP_REVERSE_ACTIVE, lease, 0, lease);
    }

    boolean sendLinkPing(int pingToken, int paceTenths, int lease) {
        pendingWakePing = false;
        return send(OP_LINK_PING, pingToken, paceTenths, lease);
    }

    boolean sendLinkFeedback(int echoToken, int packedLossJitter, int packedDisplayRate, int lease) {
        return sendWithTag(OP_LINK_FEEDBACK, echoToken, packedLossJitter, lease, packedDisplayRate);
    }

    boolean send(byte op, int a, int b, int lease) {
        return sendWithTag(op, a, b, lease, (int) SystemClock.uptimeMillis());
    }

    boolean sendWithTag(byte op, int a, int b, int lease, int timestampTag) {
        if (closed) return false;
        synchronized (sendBuffer) {
            if (closed) return false;
            try {
                int len = codec.seal(op, a, b, lease, timestampTag, sendBuffer);
                if (len <= 0) return false;
                sendPacket.setLength(len);
                socket.send(sendPacket);
                lastSentMs = SystemClock.uptimeMillis();
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
                Message msg = codec.tryOpen(recvBuf, packet.getLength(), linkTracker);
                if (msg == null) continue;
                InetAddress srcAddr = packet.getAddress();
                int srcPort = packet.getPort();
                if (srcAddr != null && (!srcAddr.equals(serverAddress) || srcPort != serverPort)) {
                    synchronized (sendBuffer) {
                        serverAddress = srcAddr;
                        serverPort = srcPort;
                        sendPacket.setAddress(srcAddr);
                        sendPacket.setPort(srcPort);
                        extraCandidates = java.util.Collections.emptyList();
                    }
                } else if (!extraCandidates.isEmpty()) {
                    extraCandidates = java.util.Collections.emptyList();
                }
                long nowMs = SystemClock.uptimeMillis();
                boolean hasSenderTimestamp = msg.op == OP_MOVE;
                boolean interactiveOp = AdaptiveLinkTracker.isInteractiveOp(msg.op);
                linkTracker.recordInboundPacket(msg.seq, hasSenderTimestamp, msg.timestampMs, nowMs, interactiveOp);
                if (msg.op == OP_PROBE) {
                    send(OP_PROBE_ACK, localPort(), 0, 0);
                    markConfirmed();
                } else if (msg.op == OP_PROBE_ACK) {
                    markConfirmed();
                } else if (msg.op == OP_WARM) {
                    markConfirmed();
                    if (msg.a == 1) {
                        send(OP_WARM, 2, 0, 0);
                    }
                    if (pendingWakePing) {
                        pendingWakePing = false;
                        sendLinkPing(linkTracker.nextPingToken(nowMs), pendingPingPaceTenths, pendingPingLease);
                    }
                } else if (msg.op == OP_LINK_PING) {
                    markConfirmed();
                    listener.onLinkPing(msg.a, msg.b, msg.lease, msg.timestampMs);
                } else if (msg.op == OP_LINK_FEEDBACK) {
                    markConfirmed();
                    linkTracker.onFeedbackReceived(msg.a, msg.b, msg.timestampMs, nowMs);
                    listener.onLinkFeedback(msg.a, msg.b, msg.timestampMs, msg.lease);
                } else if (msg.op == OP_REVERSE_MOVE || msg.op == OP_REVERSE_EDGE_PUSH) {
                    markConfirmed();
                    if (highestReverseMoveSeq == 0L || Long.compareUnsigned(msg.seq, highestReverseMoveSeq) > 0) {
                        highestReverseMoveSeq = msg.seq;
                        if (msg.op == OP_REVERSE_EDGE_PUSH) {
                            int x = (short) (msg.a & 0xFFFF);
                            int pushX = (short) (msg.a >> 16);
                            int y = (short) (msg.b & 0xFFFF);
                            int pushY = (short) (msg.b >> 16);
                            listener.onReverseDualMove(x, y, x, y, pushX, pushY, msg.lease, msg.timestampMs);
                        } else {
                            int currX = unpackCurr(msg.a);
                            int prevX = unpackPrev(msg.a);
                            int currY = unpackCurr(msg.b);
                            int prevY = unpackPrev(msg.b);
                            listener.onReverseDualMove(prevX, prevY, currX, currY, 0, 0, msg.lease, msg.timestampMs);
                        }
                    }
                } else if (msg.op == OP_REVERSE_CLAIM) {
                    markConfirmed();
                    listener.onReverseClaim(msg.a, msg.timestampMs);
                } else if (msg.op == OP_REVERSE_BUTTON) {
                    markConfirmed();
                    if (msg.lease > 0) listener.onReverseButton(msg.a, msg.b, msg.lease, msg.timestampMs);
                } else if (msg.op == OP_REVERSE_WHEEL) {
                    markConfirmed();
                    if (msg.lease > 0) listener.onReverseWheel(msg.a, msg.b, msg.lease, msg.timestampMs);
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
