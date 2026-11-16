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

    static VelocityParams parseDumpsysVelocityParamsForDevice(String dumpsysText, String deviceName) {
        if (dumpsysText == null || dumpsysText.isEmpty()) return null;
        if (deviceName != null && !deviceName.isEmpty()) {
            String[] lines = dumpsysText.split("\n");
            for (int i = 0; i < lines.length; i++) {
                if (!lines[i].contains(deviceName)) continue;
                int limit = Math.min(lines.length, i + 50);
                for (int j = i + 1; j < limit; j++) {
                    String row = lines[j];
                    if (row.contains("PointerVelocityControlParameters:")) {
                        VelocityParams matched = parseDumpsysVelocityParams(row);
                        if (matched != null) return matched;
                    }
                    String trimmed = row.trim();
                    if ((trimmed.startsWith("Device ") || trimmed.matches("\\d+:\\s+.*"))
                        && !trimmed.contains(deviceName)) {
                        break;
                    }
                }
            }
        }
        return parseDumpsysVelocityParams(dumpsysText);
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
        double scaledSpeed = rawSpeedCountsPerSec * p.scale;
        if (scaledSpeed <= p.lowThreshold || p.highThreshold <= p.lowThreshold || p.acceleration <= 1.0) {
            return p.scale;
        }
        if (scaledSpeed >= p.highThreshold) {
            return p.scale * p.acceleration;
        }
        double frac = (scaledSpeed - p.lowThreshold) / (p.highThreshold - p.lowThreshold);
        return p.scale * (1.0 + (p.acceleration - 1.0) * frac);
    }

    static double solveRawSpeedForOutputSpeed(double outSpeedPxPerSec, VelocityParams p) {
        if (outSpeedPxPerSec <= 0.0) return 0.0;
        double safeScale = Math.max(0.05, p.scale);
        if (outSpeedPxPerSec <= p.lowThreshold || p.highThreshold <= p.lowThreshold || p.acceleration <= 1.0) {
            return outSpeedPxPerSec / safeScale;
        }
        double outHigh = p.highThreshold * p.acceleration;
        if (outSpeedPxPerSec >= outHigh) {
            return outSpeedPxPerSec / Math.max(0.05, p.scale * p.acceleration);
        }
        double slope = (p.acceleration - 1.0) / (p.highThreshold - p.lowThreshold);
        double a = slope;
        double b = 1.0 - slope * p.lowThreshold;
        double disc = b * b + 4.0 * a * outSpeedPxPerSec;
        double scaledSpeed = (-b + Math.sqrt(Math.max(0.0, disc))) / (2.0 * a);
        return scaledSpeed / safeScale;
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

    static long computeSubFrameIntervalNs(int refreshHz) {
        int clampedHz = Math.max(60, Math.min(240, refreshHz > 0 ? refreshHz : 60));
        // Double the display refresh rate (capped at 288 Hz) so 125 Hz mice, 60 Hz touchpads,
        // and jittery transports place fresh cursor updates inside every VSYNC frame.
        long targetTickHz = Math.min(288L, clampedHz * 2L);
        long ns = Math.round(1_000_000_000.0 / targetTickHz);
        return Math.max(3_200_000L, Math.min(8_500_000L, ns));
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
     * Zero-allocation per-axis mirror of AOSP's 100 ms Least-Squares VelocityTracker,
     * matching Android 14 / HyperOS CursorInputMapper without wiping cross-axis speed on curve bends.
     */
    static final class AospAxisTracker {
        private static final int CAP = 20;
        private static final long HORIZON_NS = 100_000_000L;
        private static final long IDLE_RESET_NS = 200_000_000L;
        private final long[] tNs = new long[CAP];
        private final double[] pos = new double[CAP];
        private final double[] fitX = new double[CAP + 1];
        private final double[] fitY = new double[CAP + 1];
        private double cum;
        private int head;
        private int count;

        void reset() {
            head = 0;
            count = 0;
            cum = 0.0;
        }

        void add(long nowNs, double delta) {
            if (count > 0) {
                long lastT = tNs[(head + count - 1) % CAP];
                if (nowNs - lastT > IDLE_RESET_NS) {
                    reset();
                } else if (nowNs < lastT) {
                    nowNs = lastT;
                }
            }
            cum += delta;
            int slot = (head + count) % CAP;
            if (count == CAP) {
                head = (head + 1) % CAP;
                count--;
                slot = (head + count) % CAP;
            }
            tNs[slot] = nowNs;
            pos[slot] = cum;
            count++;
        }

        double velocity() {
            return computeVelocity(false, 0L, 0.0);
        }

        double previewVelocityWith(long nowNs, double candDelta) {
            if (count > 0 && nowNs - tNs[(head + count - 1) % CAP] > IDLE_RESET_NS) {
                return 0.0;
            }
            return computeVelocity(true, nowNs, candDelta);
        }

        private double computeVelocity(boolean includeCandidate, long candNs, double candDelta) {
            int total = count + (includeCandidate ? 1 : 0);
            if (total < 2) return 0.0;

            long newestT = includeCandidate
                ? Math.max(candNs, count > 0 ? tNs[(head + count - 1) % CAP] : candNs)
                : tNs[(head + count - 1) % CAP];
            int m = 0;
            long oldestT = newestT;
            if (includeCandidate) {
                fitX[0] = 0.0;
                fitY[0] = cum + candDelta;
                m = 1;
            }
            for (int i = count - 1; i >= 0; i--) {
                int s = (head + i) % CAP;
                long age = newestT - tNs[s];
                if (age > HORIZON_NS) break;
                fitX[m] = -age / 1_000_000_000.0;
                fitY[m] = pos[s];
                oldestT = tNs[s];
                m++;
            }
            if (m < 2) return 0.0;
            if (m == 2 || newestT - oldestT < 12_000_000L) {
                double sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
                for (int i = 0; i < m; i++) {
                    double xi = fitX[i], yi = fitY[i];
                    sx += xi;
                    sy += yi;
                    sxx += xi * xi;
                    sxy += xi * yi;
                }
                double denom = m * sxx - sx * sx;
                return Math.abs(denom) > 1e-12 ? (m * sxy - sx * sy) / denom : 0.0;
            }
            double s0 = m, s1 = 0.0, s2 = 0.0, s3 = 0.0, s4 = 0.0;
            double sy0 = 0.0, sy1 = 0.0, sy2 = 0.0;
            for (int i = 0; i < m; i++) {
                double xi = fitX[i], x2 = xi * xi, yi = fitY[i];
                s1 += xi;
                s2 += x2;
                s3 += x2 * xi;
                s4 += x2 * x2;
                sy0 += yi;
                sy1 += xi * yi;
                sy2 += x2 * yi;
            }
            double[][] a = {
                { s0, s1, s2, sy0 },
                { s1, s2, s3, sy1 },
                { s2, s3, s4, sy2 }
            };
            for (int col = 0; col < 3; col++) {
                int piv = col;
                for (int r = col + 1; r < 3; r++) {
                    if (Math.abs(a[r][col]) > Math.abs(a[piv][col])) piv = r;
                }
                double[] tmp = a[col];
                a[col] = a[piv];
                a[piv] = tmp;
                if (Math.abs(a[col][col]) < 1e-18) return 0.0;
                for (int r = col + 1; r < 3; r++) {
                    double f = a[r][col] / a[col][col];
                    for (int c = col; c < 4; c++) a[r][c] -= f * a[col][c];
                }
            }
            double c2 = a[2][3] / a[2][2];
            return (a[1][3] - a[1][2] * c2) / a[1][1];
        }
    }

    /**
     * Sub-frame cadence smoother that splits slower or jittery mouse/touchpad updates
     * (such as 125 Hz USB mice, 60 Hz touchpads, or wireless bursts) smoothly across
     * 2x-refresh sub-VSYNC ticks while letting fast >=180 Hz mice and clicks pass immediately.
     */
    static final class SubFrameGlide {
        private volatile long subFrameNs = 3_472_222L;
        private boolean hasTarget;
        private boolean active;
        private boolean urgentSnap;
        private double curX;
        private double curY;
        private int targetX;
        private int targetY;
        private int pushX;
        private int pushY;
        private long lastPacketNs;
        private long emaInputGapNs;
        private long glideDeadlineNs;

        void configure(int refreshHz) {
            this.subFrameNs = computeSubFrameIntervalNs(refreshHz);
        }

        long subFrameIntervalNs() {
            return subFrameNs;
        }

        void reset() {
            hasTarget = false;
            active = false;
            urgentSnap = false;
            pushX = 0;
            pushY = 0;
            lastPacketNs = 0L;
            emaInputGapNs = 0L;
            glideDeadlineNs = 0L;
        }

        void onTarget(int tx, int ty, int px, int py, long nowNs) {
            if (lastPacketNs > 0L && nowNs > lastPacketNs) {
                long gap = nowNs - lastPacketNs;
                if (gap <= 35_000_000L) {
                    long clamped = Math.max(2_000_000L, Math.min(28_000_000L, gap));
                    emaInputGapNs = emaInputGapNs == 0L ? clamped : (emaInputGapNs * 3L + clamped) / 4L;
                } else {
                    emaInputGapNs = 0L;
                }
            }
            lastPacketNs = nowNs;

            if (!hasTarget) {
                curX = tx;
                curY = ty;
                hasTarget = true;
                glideDeadlineNs = nowNs;
            } else {
                double dist = Math.hypot(tx - curX, ty - curY);
                if (px != 0 || py != 0 || dist <= 2.2 || emaInputGapNs == 0L ||
                        emaInputGapNs <= (long) (subFrameNs * 1.65)) {
                    glideDeadlineNs = nowNs;
                } else {
                    long horizonNs = Math.min(16_000_000L,
                        Math.max(subFrameNs, Math.round(emaInputGapNs * 0.90)));
                    glideDeadlineNs = nowNs + horizonNs;
                }
            }
            targetX = tx;
            targetY = ty;
            pushX = px;
            pushY = py;
            urgentSnap = false;
            active = true;
        }

        void snapToTarget() {
            urgentSnap = true;
            glideDeadlineNs = 0L;
        }

        boolean isActive() {
            return active && hasTarget;
        }

        int currentPushX() {
            return pushX;
        }

        int currentPushY() {
            return pushY;
        }

        boolean sampleNext(long nowNs, int[] outCoords) {
            if (!active || !hasTarget) return false;
            if (urgentSnap || nowNs >= glideDeadlineNs || (glideDeadlineNs - nowNs) <= (subFrameNs / 2L)) {
                curX = targetX;
                curY = targetY;
                active = false;
                urgentSnap = false;
                pushX = 0;
                pushY = 0;
            } else {
                int ticksLeft = Math.max(1, (int) Math.round((glideDeadlineNs - nowNs) / (double) subFrameNs) + 1);
                curX += (targetX - curX) / ticksLeft;
                curY += (targetY - curY) / ticksLeft;
                if (Math.abs(targetX - curX) < 0.5 && Math.abs(targetY - curY) < 0.5) {
                    curX = targetX;
                    curY = targetY;
                    active = false;
                }
            }
            outCoords[0] = (int) Math.round(curX);
            outCoords[1] = (int) Math.round(curY);
            return true;
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
        private final AospAxisTracker vxTracker = new AospAxisTracker();
        private final AospAxisTracker vyTracker = new AospAxisTracker();

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
            vxTracker.reset();
            vyTracker.reset();
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
                int resetSignX = (rawX == 0 && pullX != 0) ? -Integer.signum(pullX) : 0;
                int resetSignY = (rawY == 0 && pullY != 0) ? -Integer.signum(pullY) : 0;

                cursorX = Math.max(0.0, Math.min(screenWidth - 1, tx + resetSignX * minScale));
                cursorY = Math.max(0.0, Math.min(screenHeight - 1, ty + resetSignY * minScale));
                lastTargetX = tx;
                lastTargetY = ty;
                anchored = true;
                vxTracker.reset();
                vyTracker.reset();
                return new MoveStep(true, pullX, pullY, resetSignX, resetSignY, rawX, rawY);
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

            double gain = gainAtRawSpeed(Math.hypot(vxTracker.velocity(), vyTracker.velocity()), params);
            for (int it = 0; it < 3; it++) {
                double candRawX = clampRelative8Continuous(dx / gain);
                double candRawY = clampRelative8Continuous(dy / gain);
                double vx = vxTracker.previewVelocityWith(nowNs, candRawX);
                double vy = vyTracker.previewVelocityWith(nowNs, candRawY);
                gain = gainAtRawSpeed(Math.hypot(vx, vy), params);
            }

            int rawX = clampRelative8((int) Math.round(dx / gain));
            int rawY = clampRelative8((int) Math.round(dy / gain));
            if (rawX != 0 || rawY != 0) {
                vxTracker.add(nowNs, rawX);
                vyTracker.add(nowNs, rawY);
                gain = gainAtRawSpeed(Math.hypot(vxTracker.velocity(), vyTracker.velocity()), params);
            }

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

            return new MoveStep(false, 0, 0, 0, 0, rawX, rawY);
        }

        private static double clampRelative8Continuous(double value) {
            return Math.max(-127.0, Math.min(127.0, value));
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
            double tol = Math.max(0.55, Math.min(1.5, params.scale * 0.75));
            return Math.abs(tx - cursorX) >= tol || Math.abs(ty - cursorY) >= tol;
        }

        private int edgeSnapCounts() {
            return (int) Math.max(10L, Math.round(16.0 / Math.max(0.25, params.scale)));
        }
    }
}
