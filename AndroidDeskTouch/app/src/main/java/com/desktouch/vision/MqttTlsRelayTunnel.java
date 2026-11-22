package com.desktouch.vision;

import java.io.ByteArrayOutputStream;
import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.Arrays;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;

/**
 * Outbound-only end-to-end encrypted relay bridge over MQTT 3.1.1 TLS (port 8883).
 * Used ONLY as an automatic fallback when direct LAN / Global IPv6 / STUN TCP is blocked
 * by router NAT or stateful firewalls. Only DeskTouch's pinned X.509 TLS stream bytes
 * flow through the session topic, so the broker sees opaque double-TLS ciphertext.
 */
final class MqttTlsRelayTunnel implements Closeable {
    static final String DEFAULT_BROKER_HOST = "broker.emqx.io";
    static final int DEFAULT_BROKER_PORT = 8883;
    private static final int MAX_CHUNK_BYTES = 12288;
    private static final int KEEP_ALIVE_SECONDS = 25;

    private static final byte[] CTRL_SYN = "SYN1".getBytes(StandardCharsets.US_ASCII);
    private static final byte[] CTRL_RDY = "RDY1".getBytes(StandardCharsets.US_ASCII);
    private static final byte[] CTRL_EOF = "EOF1".getBytes(StandardCharsets.US_ASCII);

    final int localPort;
    private final SSLSocket brokerSsl;
    private final ServerSocket localServer;
    private final Object writeLock = new Object();
    private final String ctrlTopic;
    private volatile boolean closed;
    private volatile Socket localSocket;

    private MqttTlsRelayTunnel(
            SSLSocket brokerSsl,
            ServerSocket localServer,
            int localPort,
            String ctrlTopic) {
        this.brokerSsl = brokerSsl;
        this.localServer = localServer;
        this.localPort = localPort;
        this.ctrlTopic = ctrlTopic;
    }

    static MqttTlsRelayTunnel openClientBridge(
            String sessionId, String brokerHost, int brokerPort, int timeoutMs) throws IOException {
        String host = (brokerHost == null || brokerHost.trim().isEmpty())
            ? DEFAULT_BROKER_HOST : brokerHost.trim();
        int port = (brokerPort > 0 && brokerPort <= 65535) ? brokerPort : DEFAULT_BROKER_PORT;
        String sid = normalizeSessionId(sessionId);
        if (sid.length() < 16) {
            throw new IOException("Invalid relay session id");
        }

        final String c2sTopic = "dtv/r/" + sid + "/c2s";
        final String s2cTopic = "dtv/r/" + sid + "/s2c";
        final String ctrlTopic = "dtv/r/" + sid + "/ctrl";

        SSLSocketFactory sf = (SSLSocketFactory) SSLSocketFactory.getDefault();
        SSLSocket ssl = null;
        ServerSocket server = null;
        try {
            ssl = (SSLSocket) sf.createSocket(host, port);
            ssl.setTcpNoDelay(true);
            ssl.setSoTimeout(Math.max(4000, Math.min(timeoutMs, 10000)));
            ssl.startHandshake();

            OutputStream bOut = ssl.getOutputStream();
            InputStream bIn = ssl.getInputStream();
            Object initLock = new Object();
            String cid = "dtv-a-" + sid.substring(0, 10) + "-" + (1000 + new SecureRandom().nextInt(9000));
            sendConnect(bOut, initLock, cid);
            int[] pktType = new int[1];
            byte[] connAck = readMqttPacket(bIn, pktType);
            if (pktType[0] != 2 || connAck.length < 2 || connAck[1] != 0) {
                throw new IOException("Relay broker rejected MQTT CONNECT");
            }
            sendSubscribe(bOut, initLock, 1, s2cTopic);
            sendSubscribe(bOut, initLock, 2, ctrlTopic);

            final SSLSocket synSsl = ssl;
            final boolean[] ready = { false };
            Thread synThread = new Thread(() -> {
                for (int i = 0; i < 14 && !ready[0]; i++) {
                    try {
                        sendPublish(synSsl.getOutputStream(), initLock, ctrlTopic, CTRL_SYN, 0, CTRL_SYN.length);
                        Thread.sleep(400);
                    } catch (Throwable ignored) {
                        return;
                    }
                }
            }, "DeskTouch-RelaySyn");
            synThread.setDaemon(true);
            synThread.start();

            ArrayDeque<byte[]> bufferedS2c = new ArrayDeque<>();
            long deadline = System.currentTimeMillis() + Math.max(4500, timeoutMs);
            while (!ready[0] && System.currentTimeMillis() < deadline) {
                byte[] body = readMqttPacket(bIn, pktType);
                if (pktType[0] == 3) {
                    ParsedPublish pub = tryParsePublish(body);
                    if (pub != null) {
                        if (ctrlTopic.equals(pub.topic) && Arrays.equals(pub.payload, CTRL_RDY)) {
                            ready[0] = true;
                            break;
                        }
                        if (s2cTopic.equals(pub.topic) && pub.payload.length > 0) {
                            bufferedS2c.addLast(pub.payload);
                            ready[0] = true;
                            break;
                        }
                    }
                }
            }
            ready[0] = true;
            synThread.interrupt();
            if (bufferedS2c.isEmpty() && System.currentTimeMillis() >= deadline) {
                throw new IOException("Remote PC did not answer encrypted relay handshake");
            }
            // Also verify RDY was actually seen (if loop exited early on deadline vs RDY).
            if (bufferedS2c.isEmpty() && pktType[0] != 3) {
                throw new IOException("Remote PC did not answer encrypted relay handshake");
            }

            ssl.setSoTimeout(45000);
            server = new ServerSocket(0, 1, InetAddress.getLoopbackAddress());
            int boundPort = server.getLocalPort();
            MqttTlsRelayTunnel tunnel = new MqttTlsRelayTunnel(ssl, server, boundPort, ctrlTopic);
            tunnel.startPumpThreads(c2sTopic, s2cTopic, bufferedS2c);
            return tunnel;
        } catch (IOException ex) {
            if (server != null) try { server.close(); } catch (Exception ignored) {}
            if (ssl != null) try { ssl.close(); } catch (Exception ignored) {}
            throw ex;
        }
    }

    private void startPumpThreads(String c2sTopic, String s2cTopic, ArrayDeque<byte[]> bufferedS2c) {
        Thread coordinator = new Thread(() -> {
            try {
                localServer.setSoTimeout(10000);
                Socket accepted = localServer.accept();
                localSocket = accepted;
                accepted.setTcpNoDelay(true);
                try { localServer.close(); } catch (Exception ignored) {}

                final InputStream localIn = accepted.getInputStream();
                final OutputStream localOut = accepted.getOutputStream();
                while (!bufferedS2c.isEmpty()) {
                    localOut.write(bufferedS2c.removeFirst());
                }
                localOut.flush();

                Thread keepAlive = new Thread(() -> {
                    while (!closed) {
                        try {
                            Thread.sleep(15000);
                            if (closed) break;
                            synchronized (writeLock) {
                                OutputStream out = brokerSsl.getOutputStream();
                                out.write(0xC0);
                                out.write(0x00);
                                out.flush();
                            }
                        } catch (Throwable ignored) {
                            break;
                        }
                    }
                }, "DeskTouch-RelayPing");
                keepAlive.setDaemon(true);
                keepAlive.start();

                Thread up = new Thread(() -> {
                    byte[] buf = new byte[MAX_CHUNK_BYTES];
                    try {
                        OutputStream bOut = brokerSsl.getOutputStream();
                        while (!closed) {
                            int n = localIn.read(buf);
                            if (n <= 0) break;
                            sendPublish(bOut, writeLock, c2sTopic, buf, 0, n);
                        }
                    } catch (Throwable ignored) {
                    } finally {
                        close();
                    }
                }, "DeskTouch-RelayUp");
                up.setDaemon(true);
                up.start();

                InputStream bIn = brokerSsl.getInputStream();
                int[] pktType = new int[1];
                while (!closed) {
                    byte[] body = readMqttPacket(bIn, pktType);
                    if (pktType[0] == 3) {
                        ParsedPublish pub = tryParsePublish(body);
                        if (pub != null) {
                            if (s2cTopic.equals(pub.topic) && pub.payload.length > 0) {
                                localOut.write(pub.payload);
                                localOut.flush();
                            } else if (ctrlTopic.equals(pub.topic) && Arrays.equals(pub.payload, CTRL_EOF)) {
                                break;
                            }
                        }
                    }
                }
            } catch (Throwable ignored) {
            } finally {
                close();
            }
        }, "DeskTouch-RelayDown");
        coordinator.setDaemon(true);
        coordinator.start();
    }

    @Override
    public void close() {
        if (closed) return;
        closed = true;
        try {
            synchronized (writeLock) {
                sendPublish(brokerSsl.getOutputStream(), writeLock, ctrlTopic, CTRL_EOF, 0, CTRL_EOF.length);
            }
        } catch (Throwable ignored) {}
        try { localServer.close(); } catch (Exception ignored) {}
        Socket ls = localSocket;
        localSocket = null;
        if (ls != null) try { ls.close(); } catch (Exception ignored) {}
        try { brokerSsl.close(); } catch (Exception ignored) {}
    }

    private static String normalizeSessionId(String raw) {
        if (raw == null) return "";
        StringBuilder sb = new StringBuilder(raw.length());
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
                sb.append(Character.toLowerCase(c));
                if (sb.length() >= 48) break;
            }
        }
        return sb.toString();
    }

    private static void writeVarInt(OutputStream out, int v) throws IOException {
        do {
            int b = v & 0x7F;
            v >>>= 7;
            if (v > 0) b |= 0x80;
            out.write(b);
        } while (v > 0);
    }

    private static int readVarInt(InputStream in) throws IOException {
        int mult = 1, val = 0;
        for (int i = 0; i < 4; i++) {
            int b = in.read();
            if (b < 0) throw new EOFException();
            val += (b & 0x7F) * mult;
            if ((b & 0x80) == 0) return val;
            mult *= 128;
        }
        throw new IOException("Malformed MQTT length");
    }

    private static void sendConnect(OutputStream out, Object lock, String clientId) throws IOException {
        byte[] cb = clientId.getBytes(StandardCharsets.UTF_8);
        ByteArrayOutputStream body = new ByteArrayOutputStream(12 + cb.length);
        body.write(new byte[]{ 0, 4, 'M', 'Q', 'T', 'T', 4, 2, (byte) (KEEP_ALIVE_SECONDS >> 8), (byte) KEEP_ALIVE_SECONDS });
        body.write((cb.length >> 8) & 0xFF);
        body.write(cb.length & 0xFF);
        body.write(cb);
        byte[] b = body.toByteArray();
        synchronized (lock) {
            out.write(0x10);
            writeVarInt(out, b.length);
            out.write(b);
            out.flush();
        }
    }

    private static void sendSubscribe(OutputStream out, Object lock, int packetId, String topic) throws IOException {
        byte[] tb = topic.getBytes(StandardCharsets.UTF_8);
        ByteArrayOutputStream body = new ByteArrayOutputStream(5 + tb.length);
        body.write((packetId >> 8) & 0xFF);
        body.write(packetId & 0xFF);
        body.write((tb.length >> 8) & 0xFF);
        body.write(tb.length & 0xFF);
        body.write(tb);
        body.write(0);
        byte[] b = body.toByteArray();
        synchronized (lock) {
            out.write(0x82);
            writeVarInt(out, b.length);
            out.write(b);
            out.flush();
        }
    }

    private static void sendPublish(
            OutputStream out, Object lock, String topic, byte[] data, int off, int len) throws IOException {
        byte[] tb = topic.getBytes(StandardCharsets.UTF_8);
        int remLen = 2 + tb.length + len;
        synchronized (lock) {
            out.write(0x30);
            writeVarInt(out, remLen);
            out.write((tb.length >> 8) & 0xFF);
            out.write(tb.length & 0xFF);
            out.write(tb);
            if (len > 0) out.write(data, off, len);
            out.flush();
        }
    }

    private static byte[] readMqttPacket(InputStream in, int[] outType) throws IOException {
        int hdr = in.read();
        if (hdr < 0) throw new EOFException();
        outType[0] = (hdr >>> 4) & 0x0F;
        int len = readVarInt(in);
        if (len < 0 || len > 262144) throw new IOException("Invalid MQTT packet size: " + len);
        byte[] buf = new byte[len];
        int off = 0;
        while (off < len) {
            int n = in.read(buf, off, len - off);
            if (n < 0) throw new EOFException();
            off += n;
        }
        return buf;
    }

    static final class ParsedPublish {
        final String topic;
        final byte[] payload;

        ParsedPublish(String topic, byte[] payload) {
            this.topic = topic;
            this.payload = payload;
        }
    }

    static ParsedPublish tryParsePublish(byte[] body) {
        if (body == null || body.length < 2) return null;
        int tLen = ((body[0] & 0xFF) << 8) | (body[1] & 0xFF);
        if (2 + tLen > body.length) return null;
        String topic = new String(body, 2, tLen, StandardCharsets.UTF_8);
        byte[] payload = Arrays.copyOfRange(body, 2 + tLen, body.length);
        return new ParsedPublish(topic, payload);
    }
}
