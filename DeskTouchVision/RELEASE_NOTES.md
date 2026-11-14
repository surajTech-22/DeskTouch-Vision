# DeskTouch Vision v0.3.37 — Instant Cursor Handoff Recovery, True Transit Jitter & Crisp High-Refresh Tracking

DeskTouch Vision eliminates "Handoff acknowledgement timed out" failures across reconnects and single-packet wireless drops, separates genuine network transit jitter from normal cursor pauses, and preserves crisp native refresh-rate responsiveness across phones and Windows hosts.

- **Instant reconnect & single-drop handoff reliability:** Cleared per-connection reverse handoff and pointer deduplication counters across Android session reconnects so resumed sessions never drop fresh Windows edge claims or initial cursor moves, moved Android fast-lane activation and grant replies off the UI main thread onto the background network worker, atomically handed off pointer timing/activation callbacks, and backed up initial post-handoff move delivery with reliable TCP until reverse activation is confirmed.
- **True transit jitter & pause immunity on both endpoints:** Replaced raw inter-packet arrival-gap variance with RFC-3550 sender-timestamp transit-time jitter on both Windows and Android so brief hand pauses or pace adjustments are no longer misread as severe wireless jitter.
- **Crisp high-refresh pointer responsiveness & accurate frame tracking:** Separated live display `Choreographer` ticks from asynchronous wakeups so pauses no longer depress reported live refresh rates, kept smoothing disabled (`tau = 0`) on clean normal home wireless links, tightened active probe cadence (`100 ms`), and reset Android UI frame latency measurement strictly per rendered frame.

---

# DeskTouch Vision v0.3.36 — Live Display Refresh-Rate Sync, Adaptive Speed/Ping Pacing & Smooth Reordered Motion

DeskTouch Vision dynamically tracks each Android device's live screen refresh rate and real-time network speed, latency, jitter, and packet loss so cross-device mouse control stays smooth across everyday wireless and wired connections without device-specific workarounds.

- **Live cross-device display refresh-rate sensing and streaming:** Android now monitors real-time `DisplayManager` mode shifts and active `Choreographer` frame cadence (while requesting smoother overlay refresh only during active pointer display), streaming live display rate updates continuously to Windows (`OP_LINK_FEEDBACK` and `"display-metrics"`) instead of relying on startup estimates.
- **Dynamic network speed, ping, jitter & packet-loss pacing:** Continuous encrypted probes (`OP_LINK_PING` / `OP_LINK_FEEDBACK`) track round-trip ping, rolling min-RTT queuing delay, inter-arrival jitter, and real packet loss on both Windows and Android, scaling update cadence automatically down when wireless queues spike and back up to full speed on clean or wired links.
- **Smooth jitter-compensated motion & UI-stall elimination:** Added zero-overshoot frame-paced cursor interpolation (zero added smoothing lag on clean links; click/scroll actions always snap immediately), widened the fast-lane codec with a 64-packet sliding anti-replay window for reordered non-coordinate control packets, bounded Accessibility node scans to prevent UI-thread hangs, and eliminated high-rate idle keepalive floods.

---

# DeskTouch Vision v0.3.35 — Continuous Phone Wireless Wake, Ordered Zero-Jump Pointer Motion & Clean 5 GHz Pacing

DeskTouch Vision eliminates intermittent multi-hundred-millisecond wireless stalls and backward/diagonal cursor jumps while keeping phone battery sleep protection, scroll direction, app-icon scroll protection, trusted reconnection, and direct in-app Android updates intact.

- **Continuous phone wireless radio wake during active control:** Fixed the root cause of 200–500+ ms stalls during active phone control where suppressing upstream keepalive signals (`reverseLease > 0` or recent move arrival) allowed Android phones to drop into 802.11 power-save buffering between upstream packets. Restored a lightweight 22 ms interactive keepalive whenever the phone screen is awake (still stopping immediately with the screen off while idle, without triggering Windows echo floods).
- **Ordered zero-jump pointer sequence & tear-free UI coordinates:** Serialized multi-thread UDP pointer dispatches on Windows, attached monotonic move sequence numbers across both fast UDP and reliable TCP backup streams so delayed or retransmitted TCP packets can never overwrite newer cursor coordinates, and packed pending Android pointer X/Y coordinates atomically so UI vs receive threads never tear frames diagonally.
- **Display-matched 5 GHz airtime pacing & responsive touchpad fallback:** Tuned Android pointer pacing to match the phone's refresh rate (`6.5 ms` for 120 Hz, `8 ms` for 90 Hz, `10 ms` for 60 Hz) instead of flooding 5 GHz airtime with redundant 3.5 ms frames, eliminated premature 2 ms pacing timer wakes, and tightened Windows touchpad entry delay (`28 ms` / 2 events).

---

# DeskTouch Vision v0.3.34 — Fast-Lane Clicks, Scrolls & Handoffs, Smooth Entry, and Congestion-Free 5 GHz Wireless

DeskTouch Vision eliminates intermittent click, scroll, and edge-switch stalls on 5 GHz wireless connections, removes Android display-mode entry spikes, and stops idle keepalive congestion while keeping screen keep-awake, scroll direction, app-icon scroll protection, and in-app Android updates intact.

- **Zero TCP head-of-line stalls for clicks, scrolls, and edge switches:** Clicks, scrolls, edge claims, grants, and activation acknowledgements now travel over the authenticated AES-256-GCM fast UDP lane in parallel with reliable TCP backup using a 64-bit sliding deduplication window and stale-press guard, plus immediate pre-click UDP move flushes and UDP trailing settle sync so rare 5 GHz packet drops never freeze TCP for 200–450 ms.
- **No more Android entry apply spikes or scroll Binder stalls:** Removed dynamic display `preferredRefreshRate` switching on accessibility overlay activation (keeping `FLAG_KEEP_SCREEN_ON` sleep prevention intact) and added short-range UI-thread scroll bounds caching so rapid wheel ticks skip redundant cross-window accessibility tree walks while preserving accidental app-launch protection.
- **Congestion-free 5 GHz keepalive & faster edge pre-wake:** Stopped continuous 20 ms bidirectional keepalive flooding (moving idle Android keepalive to 55 ms only when inactive, rate-limiting Windows idle echo to 240 ms while inactive, tuning active warm interval to 35 ms with a 4-second cooldown, and widening Windows pre-edge wake radius to 320 px at 25 ms).

---

# DeskTouch Vision v0.3.33 — Bidirectional Phone Radio Keepalive, High-Refresh Overlay & Instant Cursor Pacing

DeskTouch Vision eliminates the remaining mobile pointer lag on phones with aggressive wireless power saving, boosts active pointer smoothness on high-refresh displays, and refines in-app Android update feedback.

- **Bidirectional 20 ms interactive wireless keepalive:** While the phone screen is awake and DeskTouch is ready, the Android endpoint sends tiny interactive warm signals every 20 ms (stopping immediately when the screen sleeps or DeskTouch pauses) and the Windows hub immediately echoes a low-delay UDP keepalive back, preventing mobile radios from dropping into beacon sleep between movements or across long pauses on Windows.
- **Unbuffered Android pointer pacing & full display refresh rate:** Lowered Android UDP move pacing from 7.5 ms back to 3.5 ms (while retaining low-delay queue scheduling that achieved zero TCP retransmissions), lowered Windows warm cadence from 35 ms to 20 ms, and requested the display's maximum supported refresh rate (`preferredRefreshRate`) while the remote cursor is active on screen (resetting to adaptive default when hidden).
- **Clearer in-app Android update flow:** Added an in-app "Checking for DeskTouch update" progress dialog when tapping "Get updates" and safe installer launch handling so verified Android updates install cleanly inside DeskTouch without opening Chrome.

---

# DeskTouch Vision v0.3.32 — Smooth Switch-Back, Screen Keep-Awake, Safe App-Icon Scroll, and In-App Android Updates

DeskTouch Vision eliminates switch-back stalls and wireless queue drops, keeps the phone screen awake while the remote pointer is active, restores the previous scroll direction while preventing scrolls over app icons from opening apps, and downloads/installs Android updates directly inside the app.

- **No more switch-back lag or wireless stalls:** Switched fast-lane packets to low-delay traffic scheduling instead of starving normal confirmations in the voice queue, paced Android pointer traffic smoothly for phone refresh rates, kept interactive phone radios warm at a faster 50 ms cadence while screen-on plus a 15-second Windows warm cooldown between edge crossings, raised idle settle sync delay to avoid redundant mid-pause packets, and kept the pointer surface warm cleanly without slow cold restarts.
- **Screen stays awake while controlling the phone:** Fixed a regression where keep-screen-on stopped updating the overlay window flags while the pointer stayed active; the phone now stays awake whenever the remote cursor is active on screen and resumes normal sleep as soon as the cursor leaves.
- **Restored scroll direction & fixed accidental app launches when scrolling:** Restored the previous scroll direction, scaled swipe distance beyond Android touch slop with crisp 45 ms strokes, detected clickable icons under the cursor so scroll strokes exit well past the icon bounds before lifting, and dropped canceled zero-distance scroll bursts.
- **Direct in-app Android update downloads:** Restored verified SHA-256 APK download and native package-installer launch directly inside DeskTouch instead of opening Chrome, with signing verification and in-app progress feedback.

---

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
