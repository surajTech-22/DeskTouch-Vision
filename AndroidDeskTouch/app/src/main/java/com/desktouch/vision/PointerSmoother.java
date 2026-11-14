package com.desktouch.vision;

/**
 * Adaptive, zero-overshoot cursor smoother and live frame-cadence estimator.
 *
 * <p>On clean low-jitter links (for example direct wired or quiet wireless links),
 * pointer coordinates are applied directly with zero artificial smoothing delay.
 * When live packet timing exhibits wireless jitter, clumping, or loss, frames
 * smoothly interpolate across display vsync ticks toward the newest target while
 * always snapping immediately before click, button, or scroll actions.
 */
public final class PointerSmoother {
    private static final int[] STANDARD_RATES_HZ = { 48, 50, 60, 75, 90, 120, 144, 165, 240 };
    private static final long FRAME_STALE_NS = 1_200_000_000L;

    private boolean initialized;
    private boolean settled = true;
    private float curX;
    private float curY;
    private float targetX;
    private float targetY;

    private int displayRefreshHz = 60;
    private int measuredFrameHz;
    private int frameSampleCount;
    private int consecutiveActiveFrames;
    private double emaFrameMs = 1000.0 / 60.0;
    private long lastFrameNs;
    private long lastActiveFrameNs;

    private long lastTargetNs;
    private double localArrivalJitterMs;
    private double networkJitterMs;
    private double lossPermille;

    public synchronized void setDisplayRefreshHz(int refreshHz) {
        if (refreshHz >= 24 && refreshHz <= 360) {
            this.displayRefreshHz = refreshHz;
        }
    }

    public synchronized void setLinkMetrics(double jitterMs, double lossPermille) {
        this.networkJitterMs = Math.max(0.0, Math.min(250.0, jitterMs));
        this.lossPermille = Math.max(0.0, Math.min(1000.0, lossPermille));
    }

    public synchronized void reset(float x, float y, long nowNs) {
        this.initialized = true;
        this.settled = true;
        this.curX = x;
        this.curY = y;
        this.targetX = x;
        this.targetY = y;
        this.lastTargetNs = nowNs;
        this.lastFrameNs = nowNs;
        this.consecutiveActiveFrames = 0;
    }

    public synchronized void clear() {
        this.initialized = false;
        this.settled = true;
        this.lastTargetNs = 0L;
        this.lastFrameNs = 0L;
        this.consecutiveActiveFrames = 0;
        this.localArrivalJitterMs = 0.0;
    }

    /**
     * Records a newly received remote pointer target.
     *
     * @return {@code true} if the display cursor needs rendering/interpolation toward target.
     */
    public synchronized boolean onTarget(float x, float y, long nowNs) {
        if (!initialized) {
            reset(x, y, nowNs);
            return false;
        }

        if (lastTargetNs > 0L && nowNs > lastTargetNs) {
            double gapMs = (nowNs - lastTargetNs) / 1_000_000.0;
            if (gapMs < 1.1) {
                // Compressed burst arrival indicates queued wireless packets catching up.
                localArrivalJitterMs = Math.max(localArrivalJitterMs, 4.5);
            } else if (gapMs <= 22.0) {
                localArrivalJitterMs *= 0.72;
            } else {
                // Idle pause or low-frequency tick: decay quickly so resume stays crisp.
                localArrivalJitterMs *= 0.35;
            }
        }
        lastTargetNs = nowNs;
        targetX = x;
        targetY = y;

        double dist = Math.hypot(targetX - curX, targetY - curY);
        if (dist <= 0.01) {
            curX = targetX;
            curY = targetY;
            settled = true;
            return false;
        }
        settled = false;
        return true;
    }

    /**
     * Advances cursor position for the current display frame timestamp.
     *
     * @return {@code true} if coordinates were updated into {@code outXY}.
     */
    public synchronized boolean stepFrame(long frameTimeNs, float[] outXY) {
        return stepFrame(frameTimeNs, outXY, true);
    }

    /**
     * Advances cursor position for the current display frame timestamp, optionally excluding
     * non-vsync UI wakeups from the live display cadence estimator.
     */
    public synchronized boolean stepFrame(long frameTimeNs, float[] outXY, boolean measureRefreshRate) {
        if (!initialized) {
            return false;
        }

        double nominalFrameMs = 1000.0 / getEffectiveRefreshHz();
        double dtMs = nominalFrameMs;
        if (measureRefreshRate && lastFrameNs > 0L && frameTimeNs > lastFrameNs) {
            double rawFrameMs = (frameTimeNs - lastFrameNs) / 1_000_000.0;
            if (rawFrameMs >= 3.5 && rawFrameMs <= 24.5) {
                recordLiveFrameMs(rawFrameMs, frameTimeNs);
                dtMs = rawFrameMs;
            } else {
                consecutiveActiveFrames = 0;
            }
        } else if (!measureRefreshRate) {
            consecutiveActiveFrames = 0;
        }
        lastFrameNs = frameTimeNs;

        double dx = targetX - curX;
        double dy = targetY - curY;
        double dist = Math.hypot(dx, dy);
        if (settled && dist <= 0.01) {
            return false;
        }

        double tauMs = computeTauMsLocked(nominalFrameMs);
        if (tauMs <= 0.01 || dist <= 0.45) {
            curX = targetX;
            curY = targetY;
            settled = true;
        } else {
            double clampedDt = Math.max(2.0, Math.min(35.0, dtMs));
            double alpha = 1.0 - Math.exp(-clampedDt / tauMs);
            if (alpha >= 0.95 || dist * (1.0 - alpha) <= 0.35) {
                curX = targetX;
                curY = targetY;
                settled = true;
            } else {
                curX += (float) (dx * alpha);
                curY += (float) (dy * alpha);
                settled = false;
            }
        }

        if (outXY != null && outXY.length >= 2) {
            outXY[0] = curX;
            outXY[1] = curY;
        }
        return true;
    }

    /**
     * Observes a display vsync timestamp even when the cursor just became settled.
     */
    public synchronized void observeFrameTimestamp(long frameTimeNs) {
        observeFrameTimestamp(frameTimeNs, true);
    }

    public synchronized void observeFrameTimestamp(long frameTimeNs, boolean measureRefreshRate) {
        if (measureRefreshRate && lastFrameNs > 0L && frameTimeNs > lastFrameNs) {
            double rawFrameMs = (frameTimeNs - lastFrameNs) / 1_000_000.0;
            if (rawFrameMs >= 3.5 && rawFrameMs <= 24.5) {
                recordLiveFrameMs(rawFrameMs, frameTimeNs);
            } else {
                consecutiveActiveFrames = 0;
            }
        } else if (!measureRefreshRate) {
            consecutiveActiveFrames = 0;
        }
        lastFrameNs = frameTimeNs;
    }

    /**
     * Immediately snaps the visual cursor to the exact newest target (for clicks/scrolls).
     */
    public synchronized boolean snapNow(float[] outXY) {
        if (!initialized) {
            return false;
        }
        curX = targetX;
        curY = targetY;
        settled = true;
        if (outXY != null && outXY.length >= 2) {
            outXY[0] = curX;
            outXY[1] = curY;
        }
        return true;
    }

    public synchronized boolean isSettled() {
        return settled;
    }

    public synchronized int getDisplayRefreshHz() {
        return displayRefreshHz;
    }

    public synchronized int getMeasuredFrameHz() {
        return measuredFrameHz;
    }

    public synchronized int getEffectiveRefreshHz() {
        if (measuredFrameHz >= 24 && frameSampleCount >= 4 &&
                lastActiveFrameNs > 0L && lastFrameNs > 0L &&
                (lastFrameNs - lastActiveFrameNs) <= FRAME_STALE_NS) {
            return measuredFrameHz;
        }
        return displayRefreshHz;
    }

    public synchronized int getEffectiveFrameTenthsMs() {
        int hz = Math.max(24, getEffectiveRefreshHz());
        return (int) Math.round(10000.0 / hz);
    }

    public synchronized double getCurrentTauMs() {
        double nominalFrameMs = 1000.0 / getEffectiveRefreshHz();
        return computeTauMsLocked(nominalFrameMs);
    }

    private double computeTauMsLocked(double nominalFrameMs) {
        double jitter = Math.max(networkJitterMs, localArrivalJitterMs);
        if (jitter <= 6.5 && lossPermille <= 4.0) {
            return 0.0;
        }
        double rawTau = Math.max(2.0,
            Math.max(0.0, jitter - 6.0) * 0.35
                + Math.max(0.0, lossPermille - 4.0) * 0.08
                + nominalFrameMs * 0.14);
        return Math.min(rawTau, Math.min(10.0, nominalFrameMs * 1.15));
    }

    private void recordLiveFrameMs(double rawFrameMs, long frameTimeNs) {
        consecutiveActiveFrames++;
        if (consecutiveActiveFrames < 2) {
            return;
        }
        if (frameSampleCount == 0) {
            emaFrameMs = rawFrameMs;
        } else {
            emaFrameMs = emaFrameMs + 0.35 * (rawFrameMs - emaFrameMs);
        }
        frameSampleCount++;
        lastActiveFrameNs = frameTimeNs;
        int rawHz = (int) Math.round(1000.0 / Math.max(3.5, emaFrameMs));
        measuredFrameHz = snapNearestStandardHz(rawHz);
    }

    public static int snapNearestStandardHz(int rawHz) {
        if (rawHz <= 0) {
            return 60;
        }
        int best = rawHz;
        double bestRelDiff = Double.MAX_VALUE;
        for (int candidate : STANDARD_RATES_HZ) {
            double relDiff = Math.abs(rawHz - candidate) / (double) candidate;
            if (relDiff <= 0.05 && relDiff < bestRelDiff) {
                bestRelDiff = relDiff;
                best = candidate;
            }
        }
        return Math.max(24, Math.min(360, best));
    }
}
