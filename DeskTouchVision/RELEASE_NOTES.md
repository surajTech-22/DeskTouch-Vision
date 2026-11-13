# DeskTouch Vision v0.3.31 — Instant Phone Handoff, Scroll & Trusted-Device Fixes, and Pointer Sensitivity

DeskTouch Vision eliminates intermittent wireless phone handoff delays, fixes upward scroll gestures opening the app drawer on Android, repairs Android trusted-device reconnection and management, and adds customizable mouse pointer sensitivity on both Windows and Android.

- **Consistent instant Android cursor handoff:** Kept the Android pointer overlay attached and warm between switches instead of rebuilding it on every screen crossing, skipped redundant MIUI full-screen overlay attempts once rejected, flushed initial handoff acknowledgments immediately, replied to reverse claims before starting power locks, kept interactive Android radios awake over UDP between switches, and widened Windows pre-edge wake hints plus hub-wide warm forwarding.
- **Scroll direction & safe-zone fix on Android:** Corrected the inverted vertical scroll swipe direction so scrolling up moves pages up smoothly (`110 ms`) instead of flinging upward into the Android app drawer, and clamped scroll strokes safely away from system status and bottom navigation gesture bars.
- **Reliable Android trusted devices:** Fixed stale session references on Trust/Leave actions, added case-normalized resilient trust storage, automatically cleared expired tokens with one-tap retry on manual join (without weakening background reconnect protections), surfaced trust status and forget-trusted controls in the Android UI, and brought Windows trust approval dialogs reliably to the foreground.
- **Custom mouse pointer sensitivity & accurate telemetry:** Added a persistent Pointer Sensitivity slider on both Windows (`25%–250%`) and Android, sped up Android network discovery and retry intervals, and excluded normal end-of-stroke settle packets from TCP fallback telemetry counts.

---

# DeskTouch Vision v0.3.30 — Fast-Lane Input Confirmation, Smooth Relay Pacing & Pre-Edge Wake

DeskTouch Vision resolves the outgoing fast-lane handshake stall, removes Windows 15.6 ms timer quantization and two-hop relay beat jitter, pre-wakes relayed Android phones before crossing edges, and adds live fast-lane and millisecond handoff telemetry.

- **Fast-lane input confirmation fix:** Fixed a startup race where the outgoing controller's initial probes arrived before the hub opened its firewall pinhole and were never retried, leaving all pointer movement stuck on reliable TCP fallback. Bidirectional confirmation now retries cleanly until both directions are verified.
- **Smooth 1 ms timer precision & unblocked relay forwarding:** Enabled 1 ms system timer resolution while sharing sessions are connected, switched throttled flush delays to exact remaining intervals, separated hub relay pacing from source pacing so wireless jitter never double-stalls movements, and skipped redundant zero-pixel raw mouse dispatches.
- **Faster phone handoff & single-record Android packets:** Forwarded pre-edge and claim wake-up hints through the hub to wake Android radios earlier before crossing onto the phone, and combined Android control frames into single writes instead of two separate TLS records.
- **Live telemetry diagnostics:** Added fast-lane confirmation, connected peer app versions, handoff grant/apply timing, fast-vs-fallback move counts, and round-trip latency measurements to `connection.log`.

---

# DeskTouch Vision v0.3.29 — Side Placement Shifting & Smoother Low-Latency Android Cursor

DeskTouch Vision fixes cursor shifting across Left/Right side phone placements and removes Android overlay/input bottlenecks for responsive control across computers and Android phones.

- **Side screen shifting fixed (Left & Right):** Replaced the oversized horizontal placement gap on auto-arranged devices with a compact scale-safe seam, made portrait phone dragging use a zoom-invariant seam, and widened side-edge band tolerance slightly so cursor handoff works cleanly when the phone is placed Left, Right, Top, or Bottom on multi-monitor setups.
- **Uneven multi-monitor layout snapping & auto-healing:** Resolved per-monitor physical edge alignment so phones placed above/below elevated displays (such as 1080p + 768p setups) snap flush without floating gaps, auto-heal stale detached layout offsets on reconnect, align direct phone hotkey routing with the phone's actual placed edge, and keep solid phone border sliding responsive.
- **Smoother, lower-latency phone cursor & clicks:** Enabled hardware acceleration and cached GPU layer rendering on the Android pointer overlay, rendered the initial entry cursor immediately on handoff, removed per-move permission lock lookups and per-packet allocation overhead, shortened touch-stroke handoff steps, skipped redundant pre-click slow-channel move flushes, and widened pre-edge Wi-Fi wake-up distance on Windows.
- **Clearer connection & low-latency telemetry:** Fixed a probe confirmation logging race so low-latency input confirmation and connected app versions appear reliably in the connection log.
