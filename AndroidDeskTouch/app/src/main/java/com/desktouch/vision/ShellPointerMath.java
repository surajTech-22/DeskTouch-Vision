package com.desktouch.vision;

import java.io.BufferedReader;
import java.io.IOException;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Pure-JVM cursor physics, Android VelocityControl inverse math, and command-loop
 * coalescing helpers shared by {@link ShellInputDaemon} and unit tests.
 */
final class ShellPointerMath {
    private static final Pattern VELOCITY_PARAMS_PATTERN = Pattern.compile(
        "PointerVelocityControlParameters:\\s*" +
            "scale=([0-9]+(?:\\.[0-9]+)?),\\s*" +
            "lowThreshold=([0-9]+(?:\\.[0-9]+)?),\\s*" +
            "highThreshold=([0-9]+(?:\\.[0-9]+)?),\\s*" +
            "acceleration=([0-9]+(?:\\.[0-9]+)?)"
    );

    private ShellPointerMath() {}

    static final class VelocityParams {
        final double scale;
        final double lowThreshold;
        final double highThreshold;
        final double acceleration;

        VelocityParams(double scale, double lowThreshold, double highThreshold, double acceleration) {
            this.scale = Math.max(0.10, Math.min(8.0, scale));
            this.lowThreshold = Math.max(10.0, lowThreshold);
            this.highThreshold = Math.max(this.lowThreshold + 1.0, highThreshold);
            this.acceleration = Math.max(1.0, Math.min(10.0, acceleration));
        }

        @Override
        public String toString() {
            return String.format(Locale.ROOT,
                "VelocityParams(scale=%.3f, low=%.1f, high=%.1f, accel=%.2f)",
                scale, lowThreshold, highThreshold, acceleration);
        }
    }

    static VelocityParams defaultVelocityParams(int pointerSpeed) {
        int clamped = Math.max(-7, Math.min(7, pointerSpeed));
        // Android / HyperOS base cursor sensitivity at speed -4 is 0.500 and 1.000 at 0.
        double scale = Math.max(0.25, Math.min(4.0, Math.pow(2.0, clamped / 4.0)));
        return new VelocityParams(scale, 500.0, 3000.0, 3.0);
    }

    static VelocityParams parseDumpsysVelocityParams(String dumpsysText) {
        if (dumpsysText == null || dumpsysText.isEmpty()) return null;
        Matcher m = VELOCITY_PARAMS_PATTERN.matcher(dumpsysText);
        VelocityParams last = null;
        while (m.find()) {
            try {
                double scale = Double.parseDouble(m.group(1));
                double low = Double.parseDouble(m.group(2));
                double high = Double.parseDouble(m.group(3));
                double accel = Double.parseDouble(m.group(4));
                if (Double.isFinite(scale) && Double.isFinite(low) &&
                    Double.isFinite(high) && Double.isFinite(accel) &&
                    scale > 0.05 && high > low && accel >= 1.0) {
                    last = new VelocityParams(scale, low, high, accel);
                }
            } catch (NumberFormatException ignored) {}
        }
        return last;
    }

    static double gainAtRawSpeed(double rawSpeedCountsPerSec, VelocityParams p) {
        if (rawSpeedCountsPerSec <= p.lowThreshold || p.highThreshold <= p.lowThreshold || p.acceleration <= 1.0) {
            return p.scale;
        }
        if (rawSpeedCountsPerSec >= p.highThreshold) {
            return p.scale * p.acceleration;
        }
        double frac = (rawSpeedCountsPerSec - p.lowThreshold) / (p.highThreshold - p.lowThreshold);
        return p.scale * (1.0 + (p.acceleration - 1.0) * frac);
    }

    static double solveRawSpeedForOutputSpeed(double outSpeedPxPerSec, VelocityParams p) {
        if (outSpeedPxPerSec <= 0.0) return 0.0;
        double outLow = p.lowThreshold * p.scale;
        if (outSpeedPxPerSec <= outLow || p.highThreshold <= p.lowThreshold || p.acceleration <= 1.0) {
            return outSpeedPxPerSec / Math.max(0.05, p.scale);
        }
        double highGain = p.scale * p.acceleration;
        double outHigh = p.highThreshold * highGain;
        if (outSpeedPxPerSec >= outHigh) {
            return outSpeedPxPerSec / Math.max(0.05, highGain);
        }
        double slope = (p.acceleration - 1.0) / (p.highThreshold - p.lowThreshold);
        double a = p.scale * slope;
        double b = p.scale * (1.0 - slope * p.lowThreshold);
        double disc = b * b + 4.0 * a * outSpeedPxPerSec;
        return (-b + Math.sqrt(Math.max(0.0, disc))) / (2.0 * a);
    }

    static double gainForOutputSpeed(double outSpeedPxPerSec, VelocityParams p) {
        if (outSpeedPxPerSec <= 1e-3) return p.scale;
        double raw = solveRawSpeedForOutputSpeed(outSpeedPxPerSec, p);
        return gainAtRawSpeed(raw, p);
    }

    static long computeDaemonPacingIntervalNs(int refreshHz) {
        int clampedHz = Math.max(60, Math.min(240, refreshHz > 0 ? refreshHz : 60));
        // Space reports slightly denser than half-frame (and below PC cable pacer) so
        // healthy paced input emits with 0ms wait while USB bulk clumps are separated evenly.
        long ns = Math.round(425_000_000.0 / clampedHz);
        return Math.max(2_300_000L, Math.min(5_000_000L, ns));
    }

    /**
     * Reads the next logical daemon command, coalescing back-to-back buffered
     * {@code MOVETO} lines without ever swallowing a trailing non-move command.
     */
    static String readNextCommand(BufferedReader reader, String[] pendingSlot) throws IOException {
        while (true) {
            String line;
            if (pendingSlot != null && pendingSlot[0] != null) {
                line = pendingSlot[0];
                pendingSlot[0] = null;
            } else {
                line = reader.readLine();
            }
            if (line == null) return null;
            if (line.isEmpty()) continue;
            if (!line.startsWith("MOVETO ")) return line;

            String latestMove = line;
            while (reader.ready()) {
                String next = reader.readLine();
                if (next == null) break;
                if (next.startsWith("MOVETO ")) {
                    latestMove = next;
                } else if (!next.isEmpty()) {
                    if (pendingSlot != null) pendingSlot[0] = next;
                    break;
                }
            }
            return latestMove;
        }
    }

    static final class MoveStep {
        final boolean needsAnchorReset;
        final int anchorPullX;
        final int anchorPullY;
        final int resetSignX;
        final int resetSignY;
        final int rawX;
        final int rawY;

        MoveStep(boolean needsAnchorReset, int anchorPullX, int anchorPullY,
                 int resetSignX, int resetSignY, int rawX, int rawY) {
            this.needsAnchorReset = needsAnchorReset;
            this.anchorPullX = anchorPullX;
            this.anchorPullY = anchorPullY;
            this.resetSignX = resetSignX;
            this.resetSignY = resetSignY;
            this.rawX = rawX;
            this.rawY = rawY;
        }

        boolean hasMotion() {
            return needsAnchorReset || rawX != 0 || rawY != 0;
        }
    }

    /**
     * Stateful inverse-VelocityControl mapper with sub-pixel error feedback and
     * single-shot boundary snaps (preventing continuous edge-bump velocity spikes).
     */
    static final class PointerCompensator {
        private int screenWidth = 1080;
        private int screenHeight = 2400;
        private int refreshHz = 60;
        private VelocityParams params = defaultVelocityParams(0);

        private boolean anchored;
        private double cursorX;
        private double cursorY;
        private int lastTargetX = -1;
        private int lastTargetY = -1;
        private double smoothedOutSpeed;
        private double prevDx;
        private double prevDy;
        private long lastReportNs;

        void configure(int width, int height, VelocityParams params, int refreshHz) {
            int nextW = Math.max(100, width);
            int nextH = Math.max(100, height);
            if (nextW != screenWidth || nextH != screenHeight) {
                anchored = false;
            }
            this.screenWidth = nextW;
            this.screenHeight = nextH;
            if (params != null) this.params = params;
            if (refreshHz >= 24 && refreshHz <= 360) this.refreshHz = refreshHz;
        }

        void updateVelocityParams(VelocityParams params) {
            if (params != null) this.params = params;
        }

        void releaseAnchor() {
            anchored = false;
            lastTargetX = -1;
            lastTargetY = -1;
            smoothedOutSpeed = 0.0;
            prevDx = 0.0;
            prevDy = 0.0;
            lastReportNs = 0L;
        }

        long minPacingIntervalNs() {
            return computeDaemonPacingIntervalNs(refreshHz);
        }

        MoveStep stepTo(int targetX, int targetY, long nowNs) {
            int tx = Math.max(0, Math.min(screenWidth - 1, targetX));
            int ty = Math.max(0, Math.min(screenHeight - 1, targetY));

            if (!anchored) {
                double minScale = Math.max(0.25, params.scale);
                int pull = (int) Math.min(20000L,
                    Math.round(Math.max(screenWidth, screenHeight) * 2.5 / minScale));
                int edgePad = edgeSnapCounts();

                int resetX = tx > 0 ? 1 : 0;
                int resetY = ty > 0 ? 1 : 0;
                double startX = resetX > 0 ? params.scale : 0.0;
                double startY = resetY > 0 ? params.scale : 0.0;

                double jumpDx = Math.max(0.0, tx - startX);
                double jumpDy = Math.max(0.0, ty - startY);
                double dist = Math.hypot(jumpDx, jumpDy);
                double entryGain = dist > 160.0 * params.scale
                    ? params.scale * params.acceleration
                    : params.scale;

                int rawX = tx == 0 ? -edgePad : (int) Math.round(jumpDx / entryGain);
                int rawY = ty == 0 ? -edgePad : (int) Math.round(jumpDy / entryGain);
                if (tx >= screenWidth - 1) rawX += edgePad;
                if (ty >= screenHeight - 1) rawY += edgePad;

                cursorX = tx;
                cursorY = ty;
                lastTargetX = tx;
                lastTargetY = ty;
                anchored = true;
                smoothedOutSpeed = 0.0;
                prevDx = 0.0;
                prevDy = 0.0;
                lastReportNs = nowNs;
                return new MoveStep(true, -pull, -pull, resetX, resetY, rawX, rawY);
            }

            double dx = tx - cursorX;
            double dy = ty - cursorY;
            boolean hitLeft = tx == 0 && lastTargetX != 0 && dx <= 0.0;
            boolean hitRight = tx >= screenWidth - 1 && lastTargetX < screenWidth - 1 && dx >= 0.0;
            boolean hitTop = ty == 0 && lastTargetY != 0 && dy <= 0.0;
            boolean hitBottom = ty >= screenHeight - 1 && lastTargetY < screenHeight - 1 && dy >= 0.0;
            lastTargetX = tx;
            lastTargetY = ty;

            double dist = Math.hypot(dx, dy);
            if (dist < 0.35 && !hitLeft && !hitRight && !hitTop && !hitBottom) {
                return new MoveStep(false, 0, 0, 0, 0, 0, 0);
            }

            double dtSec;
            if (lastReportNs <= 0L || nowNs <= lastReportNs) {
                dtSec = Math.max(0.0025, 1.0 / Math.max(60, refreshHz * 2.0));
                smoothedOutSpeed = 0.0;
            } else {
                dtSec = (nowNs - lastReportNs) / 1_000_000_000.0;
                if (dtSec > 0.120) {
                    smoothedOutSpeed = 0.0;
                    dtSec = 0.012;
                }
            }
            double effectiveDt = Math.max(0.0023, Math.min(0.045, dtSec));
            if (dx * prevDx + dy * prevDy < 0.0) {
                smoothedOutSpeed *= 0.25;
            }

            double instSpeed = dist / effectiveDt;
            double alpha = effectiveDt / (effectiveDt + 0.024);
            smoothedOutSpeed += alpha * (instSpeed - smoothedOutSpeed);
            double gain = gainForOutputSpeed(smoothedOutSpeed, params);

            int rawX = (int) Math.round(dx / gain);
            int rawY = (int) Math.round(dy / gain);
            if (hitLeft) {
                rawX -= edgeSnapCounts();
                cursorX = 0.0;
            } else if (hitRight) {
                rawX += edgeSnapCounts();
                cursorX = screenWidth - 1;
            } else {
                cursorX = Math.max(0.0, Math.min(screenWidth - 1, cursorX + rawX * gain));
            }

            if (hitTop) {
                rawY -= edgeSnapCounts();
                cursorY = 0.0;
            } else if (hitBottom) {
                rawY += edgeSnapCounts();
                cursorY = screenHeight - 1;
            } else {
                cursorY = Math.max(0.0, Math.min(screenHeight - 1, cursorY + rawY * gain));
            }

            prevDx = dx;
            prevDy = dy;
            if (rawX != 0 || rawY != 0) {
                lastReportNs = nowNs;
            }
            return new MoveStep(false, 0, 0, 0, 0, rawX, rawY);
        }

        private int edgeSnapCounts() {
            return (int) Math.max(4L, Math.round(6.0 / Math.max(0.25, params.scale)));
        }
    }
}
