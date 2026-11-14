package com.desktouch.vision;

/**
 * Pure-Java bidirectional network speed, RTT, queue-delay, jitter, and loss tracker.
 *
 * <p>Uses a sliding sequence window to distinguish harmless minor wireless reordering
 * from real packet loss, and a dual-window minimum RTT baseline (20-second decay)
 * to detect queuing delay even when network conditions change mid-session.
 */
public final class AdaptiveLinkTracker {
    private static final int REORDER_SLACK = 8;
    private static final long MIN_RTT_WINDOW_MS = 10_000L;
    private static final int PING_RING_SIZE = 16;

    private final Object recvGate = new Object();
    private long maxSeq;
    private long settledSeq;
    private long seenMask;
    private double ewmaLossRatio;
    private long reorderedPackets;
    private long lostPackets;

    private long lastArriveMs;
    private double emaGapMs = 8.0;
    private double inboundJitterMs;

    private final Object rttGate = new Object();
    private final int[] pingTokens = new int[PING_RING_SIZE];
    private final long[] pingSentMs = new long[PING_RING_SIZE];
    private int pingCounter;

    private double smoothedRttMs;
    private double rttJitterMs;
    private double minRttWindowA = Double.MAX_VALUE;
    private double minRttWindowB = Double.MAX_VALUE;
    private long windowStartMs;

    private double peerLossPermille;
    private double peerJitterMs;
    private int peerRefreshHz;
    private int peerFrameTenthsMs;

    public int nextPingToken(long nowMs) {
        synchronized (rttGate) {
            pingCounter = (pingCounter + 1) & 0x3FFFFFFF;
            if (pingCounter == 0) {
                pingCounter = 1;
            }
            int token = pingCounter;
            int slot = token % PING_RING_SIZE;
            pingTokens[slot] = token;
            pingSentMs[slot] = nowMs;
            return token;
        }
    }

    public boolean onFeedbackReceived(int echoToken, int packedLossJitter, int packedDisplayRate, long nowMs) {
        boolean matchedPing = false;
        synchronized (rttGate) {
            if (echoToken > 0) {
                int slot = echoToken % PING_RING_SIZE;
                if (pingTokens[slot] == echoToken && pingSentMs[slot] > 0L && nowMs >= pingSentMs[slot]) {
                    pingTokens[slot] = 0;
                    double sampleRtt = Math.max(0.5, nowMs - pingSentMs[slot]);
                    recordRttSampleLocked(sampleRtt, nowMs);
                    matchedPing = true;
                }
            }
            if (packedLossJitter >= 0) {
                peerLossPermille = unpackLossPermille(packedLossJitter);
                peerJitterMs = unpackJitterMs(packedLossJitter);
            }
            if (packedDisplayRate > 0) {
                int hz = unpackRefreshHz(packedDisplayRate);
                int tenths = unpackFrameTenthsMs(packedDisplayRate);
                if (hz >= 24 && hz <= 360) {
                    peerRefreshHz = hz;
                }
                if (tenths >= 25 && tenths <= 420) {
                    peerFrameTenthsMs = tenths;
                }
            }
        }
        return matchedPing;
    }

    public void recordRttSample(double rttMs, long nowMs) {
        if (rttMs <= 0.0 || Double.isNaN(rttMs) || Double.isInfinite(rttMs)) {
            return;
        }
        synchronized (rttGate) {
            recordRttSampleLocked(Math.max(0.5, rttMs), nowMs);
        }
    }

    private void recordRttSampleLocked(double sampleRtt, long nowMs) {
        if (windowStartMs == 0L) {
            windowStartMs = nowMs;
        } else if (nowMs - windowStartMs >= MIN_RTT_WINDOW_MS) {
            minRttWindowB = minRttWindowA;
            minRttWindowA = Double.MAX_VALUE;
            windowStartMs = nowMs;
        }
        if (sampleRtt < minRttWindowA) {
            minRttWindowA = sampleRtt;
        }

        if (smoothedRttMs <= 0.0) {
            smoothedRttMs = sampleRtt;
            rttJitterMs = 0.0;
        } else {
            double dev = Math.abs(sampleRtt - smoothedRttMs);
            rttJitterMs += (dev > rttJitterMs ? 0.25 : 0.10) * (dev - rttJitterMs);
            double alpha = sampleRtt > smoothedRttMs ? 0.28 : 0.16;
            smoothedRttMs += alpha * (sampleRtt - smoothedRttMs);
        }
    }

    public void recordInboundPacket(long seq, boolean streamTimingOp, long nowMs) {
        synchronized (recvGate) {
            recordSeqLocked(seq);
            if (streamTimingOp && nowMs > 0L) {
                if (lastArriveMs > 0L && nowMs >= lastArriveMs) {
                    long dt = nowMs - lastArriveMs;
                    if (dt >= 2L && dt <= 160L) {
                        double dev = Math.abs(dt - emaGapMs);
                        inboundJitterMs += (dev > inboundJitterMs ? 0.25 : 0.06) * (dev - inboundJitterMs);
                        if (dt <= 45L) {
                            emaGapMs += 0.12 * (dt - emaGapMs);
                        }
                    } else if (dt < 2L) {
                        inboundJitterMs = Math.max(inboundJitterMs, 4.5);
                    } else if (dt > 300L) {
                        inboundJitterMs *= 0.85;
                    }
                }
                lastArriveMs = nowMs;
            }
        }
    }

    private void recordSeqLocked(long seq) {
        if (seq == 0L) {
            return;
        }
        if (maxSeq == 0L) {
            maxSeq = seq;
            settledSeq = seq;
            seenMask = 1L;
            return;
        }
        if (Long.compareUnsigned(seq, maxSeq) > 0) {
            long diff = seq - maxSeq;
            if (Long.compareUnsigned(diff, 64L) >= 0) {
                seenMask = 1L;
                long skipped = Long.compareUnsigned(diff, 65L) >= 0 ? 64L : (diff - 1L);
                for (long i = 0; i < skipped; i++) {
                    ewmaLossRatio = ewmaLossRatio * 0.975 + 0.025;
                    lostPackets++;
                }
                settledSeq = seq;
            } else {
                seenMask = (seenMask << (int) diff) | 1L;
            }
            maxSeq = seq;
            settleOldSlotsLocked();
        } else {
            long back = maxSeq - seq;
            if (Long.compareUnsigned(back, 64L) < 0) {
                long bit = 1L << (int) back;
                if ((seenMask & bit) == 0L) {
                    seenMask |= bit;
                    if (back >= 1L) {
                        reorderedPackets++;
                    }
                }
            }
        }
    }

    private void settleOldSlotsLocked() {
        if (Long.compareUnsigned(maxSeq, REORDER_SLACK) <= 0) {
            return;
        }
        long targetSettle = maxSeq - REORDER_SLACK;
        if (Long.compareUnsigned(targetSettle, settledSeq) <= 0) {
            return;
        }
        long step = Math.min(64L, targetSettle - settledSeq);
        long start = settledSeq + 1L;
        for (long i = 0; i < step; i++) {
            long s = start + i;
            long back = maxSeq - s;
            boolean seen = back >= 0L && back < 64L && ((seenMask & (1L << (int) back)) != 0L);
            if (seen) {
                ewmaLossRatio *= 0.975;
            } else {
                ewmaLossRatio = ewmaLossRatio * 0.975 + 0.025;
                lostPackets++;
            }
        }
        settledSeq = targetSettle;
    }

    public int getInboundPackedLossJitter() {
        synchronized (recvGate) {
            return packLossJitter(getInboundLossPermilleLocked(), inboundJitterMs);
        }
    }

    public double getInboundLossPermille() {
        synchronized (recvGate) {
            return getInboundLossPermilleLocked();
        }
    }

    private double getInboundLossPermilleLocked() {
        return Math.max(0.0, Math.min(1000.0, ewmaLossRatio * 1000.0));
    }

    public double getInboundJitterMs() {
        synchronized (recvGate) {
            return inboundJitterMs;
        }
    }

    public long getReorderedPackets() {
        synchronized (recvGate) {
            return reorderedPackets;
        }
    }

    public long getLostPackets() {
        synchronized (recvGate) {
            return lostPackets;
        }
    }

    public double getSmoothedRttMs() {
        synchronized (rttGate) {
            return smoothedRttMs;
        }
    }

    public double getMinRttMs() {
        synchronized (rttGate) {
            double best = Math.min(minRttWindowA, minRttWindowB);
            return best == Double.MAX_VALUE ? 0.0 : best;
        }
    }

    public double getQueueDelayMs() {
        synchronized (rttGate) {
            double min = Math.min(minRttWindowA, minRttWindowB);
            if (smoothedRttMs <= 0.0 || min == Double.MAX_VALUE || smoothedRttMs <= min) {
                return 0.0;
            }
            return smoothedRttMs - min;
        }
    }

    public double getEffectiveJitterMs() {
        double inJ = getInboundJitterMs();
        synchronized (rttGate) {
            return Math.max(inJ, Math.max(rttJitterMs, peerJitterMs));
        }
    }

    public double getPeerLossPermille() {
        synchronized (rttGate) {
            return peerLossPermille;
        }
    }

    public int getPeerRefreshHz() {
        synchronized (rttGate) {
            return peerRefreshHz;
        }
    }

    public int getPeerFrameTenthsMs() {
        synchronized (rttGate) {
            return peerFrameTenthsMs;
        }
    }

    /**
     * Computes adaptive cursor update pacing (ms between packets) from live RTT,
     * queue delay, jitter, loss, and target display cadence.
     */
    public double computeAdaptiveSendIntervalMs(int targetRefreshHz) {
        double rtt;
        double minRtt;
        double lossP;
        double jitter;
        synchronized (rttGate) {
            rtt = smoothedRttMs;
            double bestMin = Math.min(minRttWindowA, minRttWindowB);
            minRtt = bestMin == Double.MAX_VALUE ? 0.0 : bestMin;
            lossP = Math.max(peerLossPermille, ewmaLossRatio * 1000.0);
            jitter = Math.max(inboundJitterMs, Math.max(rttJitterMs, peerJitterMs));
        }

        double baseMs = 2.5;
        if (targetRefreshHz > 0) {
            baseMs = Math.max(2.0, Math.min(8.5, 1000.0 / (Math.min(240, Math.max(30, targetRefreshHz)) * 1.4)));
        }
        if (rtt <= 0.0) {
            return baseMs;
        }
        double queueDelay = Math.max(0.0, rtt - minRtt);
        if (rtt <= 5.0 && queueDelay <= 2.0 && jitter <= 2.5 && lossP < 2.0) {
            return Math.max(1.8, baseMs * 0.85);
        }
        double penalty = Math.max(0.0, rtt - 14.0) * 0.14
            + Math.max(0.0, queueDelay - 6.0) * 0.30
            + Math.max(0.0, jitter - 4.0) * 0.25
            + Math.max(0.0, lossP - 5.0) * 0.18;
        return Math.max(baseMs, Math.min(35.0, baseMs + penalty));
    }

    public static int packLossJitter(double lossPermille, double jitterMs) {
        int loss = (int) Math.max(0, Math.min(65535, Math.round(lossPermille)));
        int jitterTenths = (int) Math.max(0, Math.min(65535, Math.round(jitterMs * 10.0)));
        return (loss << 16) | jitterTenths;
    }

    public static double unpackLossPermille(int packed) {
        return (packed >>> 16) & 0xFFFF;
    }

    public static double unpackJitterMs(int packed) {
        return (packed & 0xFFFF) / 10.0;
    }

    public static int packDisplayRate(int refreshHz, int frameTenthsMs) {
        int hz = Math.max(0, Math.min(65535, refreshHz));
        int ft = Math.max(0, Math.min(65535, frameTenthsMs));
        return (hz << 16) | ft;
    }

    public static int unpackRefreshHz(int packed) {
        return (packed >>> 16) & 0xFFFF;
    }

    public static int unpackFrameTenthsMs(int packed) {
        return packed & 0xFFFF;
    }
}
