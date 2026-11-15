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
        // Space reports slightly denser than 1x display refresh so every VSYNC receives
        // a fresh position while USB/TCP clumps never outpace minFrameSec or evdev.
        long ns = Math.round(600_000_000.0 / clampedHz);
        return Math.max(4_000_000L, Math.min(12_000_000L, ns));
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
            return stepTo(targetX, targetY, 0, 0, nowNs);
        }

        MoveStep stepTo(int targetX, int targetY, int pushX, int pushY, long nowNs) {
            int tx = Math.max(0, Math.min(screenWidth - 1, targetX));
            int ty = Math.max(0, Math.min(screenHeight - 1, targetY));

            if (!anchored) {
                double minScale = Math.max(0.25, params.scale);
                double highGain = Math.max(minScale, minScale * Math.max(1.0, params.acceleration));
                double accelSpanGain = Math.min(highGain, minScale + (highGain - minScale) * 0.65);
                int pullXMag = (int) Math.min(12000L, Math.round(screenWidth * 1.15 / accelSpanGain));
                int pullYMag = (int) Math.min(14000L, Math.round(screenHeight * 1.15 / accelSpanGain));
                int cornerX = tx > (screenWidth - 1) * 0.5 ? (screenWidth - 1) : 0;
                int cornerY = ty > (screenHeight - 1) * 0.5 ? (screenHeight - 1) : 0;
                int pullX = cornerX == 0 ? -pullXMag : pullXMag;
                int pullY = cornerY == 0 ? -pullYMag : pullYMag;

                // Sign reversal from corner pull resets AOSP VelocityTracker on report 1
                // (at minScale); additional burst reports saturate at scale * acceleration.
                double jumpDx = tx - cornerX;
                double jumpDy = ty - cornerY;
                int rawX = computeEntryCounts(jumpDx, minScale, highGain);
                int rawY = computeEntryCounts(jumpDy, minScale, highGain);

                cursorX = tx;
                cursorY = ty;
                lastTargetX = tx;
                lastTargetY = ty;
                anchored = true;
                smoothedOutSpeed = 0.0;
                prevDx = 0.0;
                prevDy = 0.0;
                lastReportNs = nowNs;
                return new MoveStep(true, pullX, pullY, 0, 0, rawX, rawY);
            }

            double dx = tx - cursorX;
            double dy = ty - cursorY;
            boolean atLeft = tx == 0;
            boolean atRight = tx >= screenWidth - 1;
            boolean atTop = ty == 0;
            boolean atBottom = ty >= screenHeight - 1;

            boolean hitLeft = atLeft && lastTargetX != 0 && dx <= 0.0;
            boolean hitRight = atRight && lastTargetX < screenWidth - 1 && dx >= 0.0;
            boolean hitTop = atTop && lastTargetY != 0 && dy <= 0.0;
            boolean hitBottom = atBottom && lastTargetY < screenHeight - 1 && dy >= 0.0;

            boolean pushingLeft = atLeft && pushX < 0;
            boolean pushingRight = atRight && pushX > 0;
            boolean pushingTop = atTop && pushY < 0;
            boolean pushingBottom = atBottom && pushY > 0;

            lastTargetX = tx;
            lastTargetY = ty;

            boolean hasEdgeAction = hitLeft || hitRight || hitTop || hitBottom
                || pushingLeft || pushingRight || pushingTop || pushingBottom;
            if (Math.abs(dx) < 0.25 && Math.abs(dy) < 0.25 && !hasEdgeAction) {
                return new MoveStep(false, 0, 0, 0, 0, 0, 0);
            }

            double minFrameSec = Math.max(0.0025, 0.45 / Math.max(60, refreshHz));
            double dtSec;
            if (lastReportNs <= 0L || nowNs <= lastReportNs) {
                dtSec = minFrameSec;
                smoothedOutSpeed = 0.0;
            } else {
                dtSec = (nowNs - lastReportNs) / 1_000_000_000.0;
                if (dtSec > 0.090) {
                    smoothedOutSpeed = 0.0;
                    dtSec = 0.014;
                }
            }
            double effectiveDt = Math.max(minFrameSec, Math.min(0.045, dtSec));
            // AOSP VelocityControl clears velocity tracking when either axis flips sign.
            if (dx * prevDx < 0.0 || dy * prevDy < 0.0) {
                smoothedOutSpeed = 0.0;
            }

            double dist = Math.hypot(dx, dy);
            double instSpeed = dist / effectiveDt;
            double alpha = effectiveDt / (effectiveDt + 0.045);
            smoothedOutSpeed += alpha * (instSpeed - smoothedOutSpeed);
            double modelGain = gainForOutputSpeed(smoothedOutSpeed, params);
            double gain = params.scale + (modelGain - params.scale) * 0.70;

            int rawX = clampRelative8((int) Math.round(dx / gain));
            int rawY = clampRelative8((int) Math.round(dy / gain));
            if (hitLeft || pushingLeft) {
                int extraX = (hitLeft ? -edgeSnapCounts() : 0)
                    + (pushingLeft ? edgePushRaw(pushX, params.scale) : 0);
                rawX = clampRelative8(rawX + extraX);
                double nextX = cursorX + rawX * gain;
                cursorX = (nextX <= 0.0 || Math.abs(rawX) < 127) ? 0.0 : nextX;
            } else if (hitRight || pushingRight) {
                int extraX = (hitRight ? edgeSnapCounts() : 0)
                    + (pushingRight ? edgePushRaw(pushX, params.scale) : 0);
                rawX = clampRelative8(rawX + extraX);
                double nextX = cursorX + rawX * gain;
                cursorX = (nextX >= screenWidth - 1 || Math.abs(rawX) < 127) ? screenWidth - 1 : nextX;
            } else {
                cursorX = Math.max(0.0, Math.min(screenWidth - 1, cursorX + rawX * gain));
            }

            if (hitTop || pushingTop) {
                int extraY = (hitTop ? -edgeSnapCounts() : 0)
                    + (pushingTop ? edgePushRaw(pushY, params.scale) : 0);
                rawY = clampRelative8(rawY + extraY);
                double nextY = cursorY + rawY * gain;
                cursorY = (nextY <= 0.0 || Math.abs(rawY) < 127) ? 0.0 : nextY;
            } else if (hitBottom || pushingBottom) {
                int extraY = (hitBottom ? edgeSnapCounts() : 0)
                    + (pushingBottom ? edgePushRaw(pushY, params.scale) : 0);
                rawY = clampRelative8(rawY + extraY);
                double nextY = cursorY + rawY * gain;
                cursorY = (nextY >= screenHeight - 1 || Math.abs(rawY) < 127) ? screenHeight - 1 : nextY;
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

        private static int computeEntryCounts(double jumpDelta, double minScale, double highGain) {
            if (Math.abs(jumpDelta) <= 127.0 * minScale) {
                return (int) Math.round(jumpDelta / minScale);
            }
            double sign = jumpDelta >= 0.0 ? 1.0 : -1.0;
            double firstReportPx = sign * 127.0 * minScale;
            double remainingPx = jumpDelta - firstReportPx;
            int tailCounts = (int) Math.round(remainingPx / Math.max(0.25, highGain));
            return (int) Math.round(sign * 127.0) + tailCounts;
        }

        private static int clampRelative8(int value) {
            return Math.max(-127, Math.min(127, value));
        }

        private static int edgePushRaw(int pushDelta, double scale) {
            if (pushDelta == 0) return 0;
            int scaled = (int) Math.round(pushDelta / Math.max(0.25, scale));
            return scaled != 0 ? scaled : (pushDelta > 0 ? 1 : -1);
        }

        boolean hasRemainingMotion(int targetX, int targetY) {
            if (!anchored) return true;
            int tx = Math.max(0, Math.min(screenWidth - 1, targetX));
            int ty = Math.max(0, Math.min(screenHeight - 1, targetY));
            return Math.abs(tx - cursorX) >= 1.5 || Math.abs(ty - cursorY) >= 1.5;
        }

        private int edgeSnapCounts() {
            return (int) Math.max(10L, Math.round(16.0 / Math.max(0.25, params.scale)));
        }
    }
}
