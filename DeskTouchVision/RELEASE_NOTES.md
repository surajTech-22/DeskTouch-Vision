# DeskTouch Vision v0.3.70 — Modern 4-Tab Mobile App Redesign

Introduces the completely redesigned Android app interface while keeping every `v0.3.69` speed, channel, and security capability intact:

- **Sticky top header + instant Pay Mode switch (`MainActivity`, `PhoneUiKit`):** Keeps device connection status visible at all times alongside a single-tap `Pay Mode` / `Pay Safe: ON` Accessibility toggle right at the top.
- **Focused 4-tab layout (`Home`, `Trackpad`, `Speed`, `Setup`):** Replaces the long vertical scroll list with clean rounded Midnight Slate cards, live Wireless vs Mobile comparison badges, three-way segmented pickers (`Mouse Channel`, `Keyboard Channel`, `Cursor Feel`), material switch rows, interactive Trackpad surface + sensitivity card, and grouped `Setup` sections for Permissions, Xiaomi Battery, Trust, Updates, and Theme (`Midnight Slate`, `AMOLED Black`, `Crisp Light`).

---

# DeskTouch Vision v0.3.69 — Zero-Lag Hardware ReverseKey Keyboard, Dual-Path Key/Click Redundancy & Input Channel Controls

Eliminates intermittent Wi-Fi keyboard latency and gives direct Android UI control over Mouse/Keyboard channels, Cursor Feel, Dual-Path Boost, and Radio Wake:

- **Direct UDP hardware ReverseKey forwarding (`SourceCapture`, `NetworkSessions`, `DisplaySettingsForm`):** Sends unshifted and modified keystrokes as real hardware `"key"` packets over fast UDP `OP_REVERSE_KEY` for Android `>= 0.3.67` peers instead of queuing printable characters down TCP-only `"text"` frames, while cleanly normalizing unshifted Numpad digits (`0x60..0x69`) and keeping TCP backup compatibility for legacy peers.
- **Always-on Dual-Path protection & Wireless Radio Keep-Warm (`NetworkSessions`, `FastUdpHub`, `DeskTouchSession`):** Duplicates interactive `key`, `button`, and `wheel` UDP frames simultaneously over both confirmed Wi-Fi and Mobile UDP peers (with Android-side `actionSeq` deduplication) and pulses lightweight `OP_WARM` frames every `85 ms` when Wi-Fi is idle during an active lease so Xiaomi's 2.4 GHz radio never naps between keystrokes.
- **Interactive Input Channels & Speed Controls + Live Split Display (`MainActivity`, `DeskTouchSession`, `DeskTouchRuntime`, `PointerSmoother`, `DeskTouchAccessibilityService`):** Adds per-device toggles directly in the Android app for Mouse path (`Auto` / `Mobile` / `Wireless`), Keyboard path (`Fastest` / `Wireless` / `Mobile`), Cursor feel (`Auto` / `Raw 0ms` / `Smooth`), Dual-path keys & clicks (`OP_REVERSE_KEY` / button mirror), Wireless radio keep-warm, Saved-Path Reset, and live split Mouse/Keyboard latency telemetry across both Android and Windows status views.

---

# DeskTouch Vision v0.3.68 — Auto Wireless Debugging, Instant Mobile Stream Sync, Low-Delay Cellular Pacing & Faster Keyboard Path

Fixes the stream-preference confirmation race after cable/Wi-Fi handoffs, removes artificial mobile pacing delay, auto-connects Wireless Debugging, and puts Keyboard cleanly onto the fastest `0%`-loss path:

- **Zero-race stream preference sync & instant mobile startup (`NetworkSessions`, `DeskTouchSession`, `AutoConnectService`):** Stores desired `Mouse`/`Keyboard` stream preferences on Windows and applies them the instant `MobileUdpPeer.Confirmed` flips true (fixing the race where `stream-prefer` arrived over Wi-Fi before Mobile UDP confirmation completed), queues `AutoConnectService` cellular checks cleanly instead of dropping them during cable unplug transitions, starts Mobile UDP punching immediately upon credential receipt with `1.2–1.5s` fast retries if cellular is still waking, and evaluates initial stream memory/loss immediately on probe confirmation.
- **Crisp low-delay Mobile network mouse pacing (`NetworkSessions.ComputeAdaptiveUdpIntervalMs`):** Unlocks `6.8 ms` (`144 Hz`) pacing on clean low-jitter `0%`-loss cellular links instead of capping cellular at `9.5–10.0 ms`, and stops penalizing steady baseline cellular propagation latency when queue delay and loss are low.
- **Keyboard picks faster `0%`-loss path (`DeskTouchSession`, `CellularLinkHelper`):** Anchors post-offload Wi-Fi packet loss once Mouse moves onto Mobile so stale decaying mouse-burst EWMA doesn't pull Keyboard away from clean `~3–6 ms` Wi-Fi, and lets Keyboard automatically pick/remember whichever `0%`-loss channel has lower latency (`>= 8 ms` advantage) while keeping Mouse strictly on `0%`-loss Mobile until active loss reaches `>= 1.0%`.
- **Automatic paired Wireless Debugging discovery (`AdbCableBridge`):** Queries `adb mdns services` for already-paired `_adb-tls-connect._tcp` endpoints whenever no debugging session is active (with 60s backoff for unpaired devices), launching the Accessibility-free `144 Hz` Virtual Bridge automatically whenever Developer Options + Wireless Debugging are enabled.

---

# DeskTouch Vision v0.3.67 — Independent Mouse & Keyboard Fast Channels + Zero-Loss Stream Memory

Eliminates keyboard lag on Wi-Fi and stops unnecessary repeated switching between Wi-Fi and Mobile network:

- **Independent fast UDP channels for Keyboard & Mouse (`FastUdpChannel`, `FastUdpHub`, `NetworkSessions`, `DeskTouchSession`):** Adds dedicated `FastUdpOp.ReverseKey` (`14`) with reliable ordered TCP backup (`p.a` sequence preservation, non-blocking `isImmediateControl` flush), separate `preferMobileMouse` and `preferMobileKeyboard` stream selection (`stream-prefer` `A`/`B`), and isolated mouse-button vs keystroke deduplication (`lastExecutedKeySeq`), allowing e.g. `4 ms` Wi-Fi keyboard strokes simultaneously alongside `0%`-loss Mobile `144 Hz` mouse streaming.
- **Persistent 0%-loss stream memory & instantaneous loss-triggered switching (`DeskTouchSession`, `CellularLinkHelper`, `AutoConnectService`):** Saves cleanly working (`0%` loss) stream choices per band and device role in local app preferences (`desktouch_stream_memory`, strictly without saving IPs or network names), restores remembered channels immediately upon auxiliary Mobile UDP confirmation, eliminates periodic 1-second/75-second bounce-backs onto congested Wi-Fi, and only evaluates switching when the active stream itself crosses `>= 1.0%` packet loss and the alternate stream shows strictly lower loss.

---

# DeskTouch Vision v0.3.66 — Always-On Wi-Fi Link + Simultaneous Local Mobile Receiver & Stronger-Path Switching

Replaces cellular-over-Wi-Fi session replacement with a purely local dual-stream architecture that keeps the PC and phone connected Wi-Fi-to-Wi-Fi continuously:

- **Wi-Fi connection stays open (`AutoConnectService`, `DeskTouchSession`, `DeskTouchRuntime`):** Removes session replacement when `"sreevatsa_5GHz"` or local Wi-Fi is connected, keeping pairing, control, and system Wi-Fi completely untouched while evaluating mobile network strength alongside it.
- **Auxiliary local Mobile UDP stream with simultaneous two-way firewall opening (`FastUdpChannel`, `FastUdpHub`, `NetworkSessions`, `CellularLinkHelper`):** Windows allocates a dedicated auxiliary encrypted UDP peer (`MobileUdpPeer`) alongside the primary Wi-Fi peer; Android binds a secondary `FastUdpChannel` onto `TRANSPORT_CELLULAR` and exchanges global cellular IPv6 candidates over the existing Wi-Fi TLS channel (`"mobile-udp-punch"` / `"mobile-udp-channel"`), causing both sides to probe outward simultaneously across multiple candidate endpoints.
- **Stronger-path receive switching (`DeskTouchSession`, `NetworkSessions`, `FastMovePacer`):** Android continuously benchmarks real packet loss, jitter, and RTT across both active receivers (`Wi-Fi` vs `Mobile network`), signals `"stream-prefer"` over Wi-Fi to select whichever receive stream is stronger, mirrors a 250 ms transition overlap so zero cursor frames drop during handoff, and automatically falls back to Wi-Fi if mobile delivery stalls or degrades.

---

# DeskTouch Vision v0.3.65 — Non-Blocking Mobile Network Switcher, Multi-Sample Probe & Burst-Loss Memory

Fixes the Android monitor deadlock that prevented cellular path probing on dual-path networks, adds recent burst-loss retention, and upgrades path benchmarking:

- **Non-blocking `CellularLinkHelper` acquisition & live probe diagnostics:** Removes the `synchronized` monitor lock held across `CountDownLatch.await(...)` inside `acquireCellularNetwork` (which blocked `ConnectivityManager.NetworkCallback.onAvailable` until timeout even when `ccmni` mobile data came up in `<100 ms`), adds explicit `onUnavailable` wakeup and stale-callback guards, and leaves the Android Wi-Fi connection completely untouched while routing only DeskTouch traffic over whichever path is faster.
- **Multi-sample TLS+echo benchmark (`CellularLinkHelper`, `NetworkSessions`):** Upgrades `"auth-trusted-probe"` (`request.A == 2`) with two fast `"probe-echo"` round trips on the same TLS socket to measure genuine median RTT and cellular jitter rather than relying on a single packet or hardcoded jitter constant, while remaining backward compatible with `v0.3.64` peers and logging trusted probes on Windows.
- **Recent peak burst-loss memory & anti-ping-pong hold (`AdaptiveLinkTracker`, `DeskTouchSession`, `AutoConnectService`):** Retains 45-second peak packet-loss and jitter windows across idle heartbeat pauses so `AutoConnectService` remembers RF bursts experienced during active mouse control, schedules fast re-evaluation `~900 ms` after returning control from a degraded wireless lease, and avoids bouncing right back onto a recently demoted noisy band while returning smoothly when a clean band returns.

---

# DeskTouch Vision v0.3.64 — Instant Cable Clicks, Smooth Low-RTT Wireless & Direct Mobile Network Auto-Switching

Eliminates USB cable mouse-click stalls, smooths local 2.4 GHz cursor feel without false pace throttling, and adds automatic dual-stack IPv6 + mobile cellular path switching:

- **Zero-lag mouse clicks over USB cable & Virtual Bridge (`ShellInputClient`, `ShellInputDaemon`, `NetworkSessions`):** Unblocks urgent `BTN` / `WHEEL` / `KEY` commands from starving behind continuous `144 Hz` `drainPendingMove()` loops, eliminates 12-step pre-click cursor animation loops and `DeskTouch-UhidPacer` lock stalls (`LockSupport.parkNanos`) on click down/up, dispatches button/wheel clicks on Windows with `SendUrgentAsync` ahead of queued background moves/cable pings, and skips redundant pre-click wire move flushes when the cursor is already at the target coordinate.
- **Smooth local 2.4 GHz cursor responsiveness (`NetworkSessions`, `FastUdpChannel`, `AdaptiveLinkTracker`, `PointerSmoother`):** Prevents isolated RF packet drops on low-RTT clean-jitter wireless links (`<= 18 ms` RTT, `<= 5.5 ms` jitter) from slamming cursor interval down to `16.5 ms` or injecting excessive smoothing drag (`tau <= 3.5 ms`), and aligns Windows `TrailingTcpSyncDelayMs` to `45 ms`.
- **Direct Mobile Network + Wi-Fi best-path switcher (`CellularLinkHelper`, `AutoConnectService`, `DeskTouchRuntime`, `DeskTouchSession`, `NetworkSessions`, `FastUdpChannel`):** Upgrades Windows TCP (`37377`) and UDP (`FastUdpHub` / `FastUdpClient`) to dual-stack IPv6 + IPv4 (`UnmapAddress` keeps USB cable detection exact), advertises global IPv6 endpoints in volatile memory only (never persisted to disk), adds stateless `"auth-trusted-probe"` benchmarking, acquires `TRANSPORT_CELLULAR` on Android via `ConnectivityManager.requestNetwork`, and automatically promotes whichever path (Wi-Fi or Mobile Network) is cleaner/faster with hysteresis & cooldown while keeping USB data cable highest priority.

---

# DeskTouch Vision v0.3.63 — Clean Mobile Wireless Streaming, Zero Keepalive Echo Storms & Synced Android Build

Fixes the wireless packet-loss/jitter inflation and restores a complete matching Android and Windows release:

- **Zero keepalive collision & echo storms (`DeskTouchSession`, `FastUdpChannel`, `NetworkSessions`):** Restores Android's test-compliant 22 ms keepalive ceiling, suppresses upstream phone keepalives while live cursor movements are arriving, removes routine `OP_WARM(0) <-> OP_WARM(2)` ping-pong echoes, removes the redundant Windows background keepalive loop, and fixes `FastMovePacer.UpdateTimerLocked` so idle keepalive pulses (`22–36 ms` on 2.4 GHz) are no longer blocked for 130 ms behind trailing TCP sync.
- **Preserves `(-)` / `(+)` waypoint continuity (`NetworkSessions`, `FastUdpChannel`):** Removes the double-send cursor duplication workaround that mutated `TakeWirePrevious` on the first send, destroyed `(-)` recovery waypoints on the second copy, and doubled wireless contention.
- **Honest jitter & idle-exempt loss accounting (`AdaptiveLinkTracker` on Windows & Android, `FastUdpChannel`):** Excludes background `OP_WARM` / `OP_LINK_PING` control timers (and coarse Windows `Environment.TickCount` quantization) from movement transit-jitter measurements while preserving sub-millisecond `LinkPing` RTT jitter, synchronizes Android's `REORDER_SLACK = 63` + `latePackets` reclassification with Windows, and exempts dropped idle keepalive/probe pulses during stationary pauses from fabricating cursor packet loss.
- **Synced Windows & Android v0.3.63 release:** Bumps `DeskTouchVision`, `DeviceSharing`, Android (`versionCode 64`), and `RECOVER_DESKTOUCH.ps1` together and validates all version/protocol tests across both platforms.

---

# DeskTouch Vision v0.3.62 - 2.4 GHz Wireless Low-Latency Workarounds & Honest Loss Accounting

Makes phone control over 2.4 GHz faster and steadier without forcing the phone onto another band, and stops late-but-delivered frames from being reported as packet loss:

- **Late-vs-lost accounting (`FastUdpChannel`):** Widens the reorder settle window from 8 to 63 frames (~470 ms at 7.5 ms pacing, ~1.0 s at the 16.5 ms 2.4 GHz cap), reclassifies frames that still arrive after settlement as late instead of lost, and stops counting slots outside the 64-bit sliding window as loss. The old boundary logic turned a single 3-8 frame reorder burst into 19-39% displayed loss even with a 5 ms RTT; the live line now reports only frames that never arrive.
- **~100 Hz 2.4 GHz keepalive (`NetworkSessions`, `FastUdpChannel`, `DeskTouchSession`):** Drives the Warm/CAM pulse at 10-20 ms on 2.4 GHz (was 55-95 ms) and at 12 ms from the phone during an active lease (was 22 ms), keeping the radio out of Wi-Fi power-save between mouse frames. Measured basis: 100 Hz ping = 1% loss / 6.5 ms average / 8 ms p90, versus 1 Hz = 2.5% loss / 115 ms average and 10 Hz = 6% loss / 441 ms maximum.
- **Duplicate 2.4 GHz move frames (`NetworkSessions`, `FastUdpChannel`):** Sends each movement twice with the same move sequence; the Android receiver's move dedup drops the copy when the first already landed. Independent air loss can no longer remove both copies, at a cost of at most ~50 kbps payload on a 144 Mbps link.
- **Power-lock verification (`DeskTouchSession`, `RemoteInputPower`):** Confirms the CPU and dual Wi-Fi low-latency/high-performance locks are reacquired on every granted Windows-to-Android input lease, not only at TCP connect.
- **Diagnostics (`NetworkSessions`):** Telemetry now includes `late_total`, `reordered_total` and `duplicated` so a 2.4 GHz stall can be separated from real air loss without a packet capture.

---

# DeskTouch Vision v0.3.61 — Wireless Accessibility Dual-Position Recovery & System-to-System Edge/Status Fixes

Resolves recent update regressions across computer-to-computer and mobile wireless connections:

- **Screen-edge movement continuity on Windows peers (`NetworkSessions`, `FastUdpChannel`):** Restricts mobile `ReverseEdgePush` packets exclusively to Android targets (`platform == "Android"`) so cursor movements touching or sliding along the screen edges of a destination Windows computer are never converted into mobile edge-push frames or dropped.
- **Active-lease status bar computer prioritization (`NetworkSessions`, `DisplaySettingsForm`):** Updates `GetActiveLinkSummary` and client status bar selection to check whether a non-Windows target holds an active control lease, ensuring the computer-to-computer wired Ethernet link is always prioritized in the status bar whenever the cursor is local or controlling the other computer.
- **Movement timestamp tracking, unstarved ping probes & sub-millisecond wired jitter (`FastUdpChannel`, `NetworkSessions`):** Records `LastMoveSentTicks` on outbound fast-channel movements, separates the mobile Continuous Active Mode keepalive timer from the wake-primed ping timer so live ping probes are never starved, and prevents coarse 15.6 ms system tick quantization from reporting false jitter on wired Ethernet connections.
- **Wireless Accessibility mode dual-position (`-` / `+`) waypoint recovery (`DeskTouchSession`, `DeskTouchAccessibilityService`, `PointerSmoother`, `ShellPointerMath`, `ShellInputDaemon`):** Threads unpacked previous (`-`) and current (`+`) mouse positions into Android Accessibility pointer rendering (`PointerSmoother`) so dropped wireless packets are smoothly traversed in both Accessibility and Virtual Bridge modes, and removes the destabilizing network-selection override command on Android.

---

# DeskTouch Vision v0.3.60 — Windows Direct Pixel Coordinates & Wired Ethernet Link Preservation

Resolves computer-to-computer cursor lag, drops, and corrects status bar link classification:

- **Windows direct pixel coordinates (`NetworkSessions`, `InputInjector`, `WinNative`):** Dual-position waypoint packing is restricted exclusively to Android mobile devices (`platform == "Android"`). Computer-to-computer movement streams clean, 1:1 raw screen coordinates with zero midpoint packing overhead. Additionally, the Windows input injector safely unmarshals dual coordinates and clamps coordinates within the active virtual screen bounds, eliminating out-of-bounds coordinate rejections.
- **Wired Ethernet media preservation (`FastUdpChannel`, `NetworkSessions`):** Passes detected physical Ethernet adapter information into UDP hub peer registration and client connection initialization. Prevents the default wireless tracker state from overriding confirmed wired Ethernet links in the status display across both host and client computers.
- **Bidirectional wired link exchange (`NetworkSessions`):** Exchanges network media profile packets during session setup and handshake loops so both connected computers consistently identify and report high-speed wired Ethernet connections.

---

# DeskTouch Vision v0.3.59 — Computer Peer Link Prioritization & Client-Side Wired Network Display

Enhances multi-device status reporting and client-side link transparency across simultaneous connections:

- **Computer peer link prioritization (`ReceiverServer`, `NetworkSessions`):** When both a computer and a mobile phone are connected simultaneously to the sharing hub, the live link status summary prioritizes the computer peer. This ensures high-speed wired network connections remain prominently displayed with real-time ping and throughput statistics rather than being superseded by auxiliary wireless devices.
- **Client-side active link status display (`HostSession`, `DisplaySettingsForm`):** Enables client-side destination machines to inspect and display active outbound link telemetry (connection type, adapter speed, and round-trip ping) directly in the settings window rather than showing an idle waiting state while actively connected.

---

# DeskTouch Vision v0.3.58 — Wired Ethernet Media Classification & Instant-Glide Pointer Restoration

Corrects physical network adapter classification for computer-to-computer connections and restores instant, responsive cursor glide:

- **Wired Ethernet adapter detection & UI classification (`NetworkSessions`, `FastUdpChannel`, `DisplaySettingsForm`):** Inspects the local network interface bound to the peer connection to identify physical Ethernet adapters (`802.3`). When two computers connect over a wired cable, the link is correctly classified and displayed as `Wired network (Ethernet) (1 Gbps)` instead of defaulting to `"Wireless"`.
- **Dropout-only waypoint glide (`ShellPointerMath`):** Restores immediate, sub-frame cursor response during normal continuous mouse streaming with zero midpoint lag or rubber-banding. Dual-position (- / +) waypoint traversal is engaged only when a genuine transmission gap or packet drop occurs (`>= 14 ms`), recovering dropped trajectories without delaying normal movement.
- **Continuous 16–18 ms radio keepalive & warm echo (`DeskTouchSession`, `FastUdpChannel`, `NetworkSessions`):** Restores continuous background keepalive pulses and uplink echo responses during mouse motion, keeping the mobile Wi-Fi radio in Continuous Active Mode (CAM) and preventing sleep spikes and bufferbloat.

---

# DeskTouch Vision v0.3.57 — Dual-Position (- / +) Waypoint Recovery & Zero-Collision Mouse Streaming

Eliminates cursor skips, packet-loss teleportation, and wireless air collisions across both wireless and data cable connections:

- **Dual-position (- / +) coordinate recovery (`FastMovePacer`, `FastUdpChannel`, `DeskTouchSession`, `NetworkSessions`):** Every mouse motion packet sent over wireless and data cable packs both the previous (-) waypoint and current (+) destination into each coordinate. When intermediate wireless or coalesced cable frames are dropped, the receiver unpacks the intermediate waypoint and smoothly traverses it rather than skipping across the screen.
- **Waypoint glide and extended dropout tolerance (`ShellPointerMath`, `ShellInputDaemon`, `ShellInputClient`):** Teaches the sub-frame glide engine to progress through recovered intermediate waypoints during packet-loss bursts, and extends the jitter reset gap from 35 ms to 120 ms so transient wireless dropouts do not snap or reset cursor smoothing.
- **Zero-collision wireless mouse streaming (`NetworkSessions`, `FastUdpChannel`, `DeskTouchSession`):** Suppresses bidirectional keepalive pulses and wake-up pings while mouse motion packets are actively streaming across the 802.11 half-duplex air interface, preventing upstream packets from colliding with downstream cursor frames.
- **Tuned 2.4 GHz pacing on high-refresh screens (`NetworkSessions`):** Unlocks 8.5 ms / 120 Hz cursor pacing on clean 2.4 GHz links when the phone display is configured for 90 Hz or 120 Hz.

---

# DeskTouch Vision v0.3.56 — Wireless Low-Latency Wake-Primed Probing, Persistent Radio Locks & Jitter Filtering

Reduces wireless ping and jitter between Windows computers and Android phones down to single-digit / low-teen milliseconds:

- **Persistent dual-mode Android wireless locks (`RemoteInputPower`, `DeskTouchSession`):** Holds both `WIFI_MODE_FULL_LOW_LATENCY` and `WIFI_MODE_FULL_HIGH_PERF` simultaneously so background wireless power-save remains suppressed when DeskTouch is minimized on Android 10+, and prevents closing secondary or make-before-break sessions from releasing the active session's wireless and CPU wake locks.
- **System-level background roam-scan suppression (`ShellInputDaemon`):** Applies shell-privileged wireless network-selection and background scan overrides (`set-network-selection-config` / `set-scan-always-available`) when the virtual input bridge is active to prevent periodic 40–150 ms off-channel roaming scans.
- **Bidirectional wake-primed probing & 20 ms active keepalive (`NetworkSessions`, `FastUdpChannel`, `DeskTouchSession`):** Keeps upstream and downstream packet spacing at 20 ms during active control (below the 30 ms 802.11 power-save sleep threshold) and precedes idle link probes with a two-way `Warm` wake-up handshake so `LinkPing` measures the awake wireless link rather than router beacon sleep wait.
- **Baseline-anchored outlier & stream-only jitter filtering (`FastUdpChannel`, `AdaptiveLinkTracker`, `NetworkSessions`):** Prevents isolated wireless beacon/scan spikes and fast recovery drops from inflating smoothed RTT or jitter, filters inbound transit jitter to continuous stream intervals (`<= 45 ms`), and unlocks smooth 125 Hz+ cursor pacing on clean 2.4 GHz and 5 GHz wireless links.

---

# DeskTouch Vision v0.3.55 — Antivirus-Clean Updater Extraction & v0.3.54 Unified Shared Environment

Includes everything from v0.3.54 plus clean package extraction during updates:

- **Antivirus-friendly package extraction (`Update-DeskTouch`, `RECOVER_DESKTOUCH`):** Uses Windows' built-in signed `tar.exe` archiver to unpack verified release ZIP packages before integrity verification instead of PowerShell/in-process ZIP extraction that triggered false-positive behavior heuristics on Quick Heal after its Nov 16 signature update.

---

# DeskTouch Vision v0.3.54 — Two-Way Mutual Trust, Size-Based Multi-Hub Merge & Seamless Tray Operation

Completes bidirectional trust, deterministic multi-hub merging by device count, universal 3-device shared environments across any transport, and reliable unattended tray operation:

- **Two-way mutual trust pairing & auto-upgrade (`NetworkSessions`, `TrustedDeviceStore`, `DisplaySettingsForm`):** Approving `Trust device` in either direction now exchanges cryptographically signed mutual proposals so both Windows computers save reciprocal reconnect keys simultaneously, and existing one-way trusted installations automatically upgrade to two-way trust over an authenticated trusted session.
- **Multi-hub merging by device count (`NearbyComputerDiscovery`, `DisplaySettingsForm`, `PairComputerDialog`, `DesktopDiscovery`, `AutoConnectService`):** Discovery now advertises active environment member counts (`DT1-HUBCOUNT`) only when 2+ devices are online; standalone 1-device computers never override active hubs, smaller hubs automatically merge into larger-count hubs (with deterministic fingerprint tie-breaking when sizes match), and Android phones automatically migrate to the largest reachable trusted hub.
- **Unified shared environment across any connection path (`NetworkSessions`, `DisplaySettingsForm`):** A computer joined to a hub bridges any locally connected phone (including via data cable) up into the hub roster (`sub-peers` / `sub-route`) while retaining zero-hop direct cable input locally, so all connected computers and phones always appear and operate together inside `"One environment"`.
- **Unattended Windows startup, tray icon & headless tray control (`DisplayArrangementControl`, `DisplaySettingsForm`, `WindowsStartup`, `Install-DeskTouch`, `Update-DeskTouch`):** Computes display arrangement geometry eagerly without requiring `OnPaint` or an open window so cursor edge-crossing and keyboard handoff work immediately while minimized to the tray at startup; restores the tray icon cleanly across Explorer restarts (`TaskbarCreated`); starts trusted scanning within 450 ms and across unlock events; repairs legacy Startup links cleanly without error; and replaces slow archive extraction in `Update-DeskTouch.ps1` with native .NET extraction.

---

# DeskTouch Vision v0.3.53 — Unified Single Multi-PC & Phone Environment

Ensures two Windows computers and your phone always rejoin and remain in the same shared environment instead of creating split, duplicate, or re-prompted environments:

- **No split or opposing PC-to-PC environments (`DisplaySettingsForm`, `NetworkSessions`):** Prevents trusted auto-reconnect from opening a conflicting outgoing session when a Windows computer is already connected in the local hub, deterministically collapses simultaneous mutual connections into one shared environment hub by certificate identity, evicts stale half-open reconnect sessions for the same trusted PC without dropping active routing, and lets manual `Add devices` reconnects reuse saved cryptographic trust silently without nagging for a fresh approval prompt.
- **Keep locally connected phone & hub computers unified (`DisplaySettingsForm`, `TrustedDeviceStore`):** Preserves locally connected Android devices (including via data cable) inside the same `"One environment"` layout and shortcut navigation (`Ctrl+Alt+M`) alongside a joined Windows hub without hiding them or wiping their custom saved display placement on hub sync.
- **Android automatic hub convergence (`DesktopDiscovery`, `DeskTouchRuntime`, `AutoConnectService`):** Fixes fast cached IP probes to wait briefly for `DT1-HUB` so a leaf computer never bypasses the true Windows environment hub, and automatically migrates an idle wireless phone onto the active trusted hub whenever both computers are online so all three devices share one environment cleanly.

---

# DeskTouch Vision v0.3.52 — Smooth Zero-Skip 144Hz Cursor Motion & Wireless Stability

Eliminates cursor frame-skipping, beat-frequency judder, velocity drift, high-refresh throttling, and wireless metric/cursor fallbacks:

- **Exact Android 14 / HyperOS VelocityControl inverse tracking (`ShellPointerMath`, `ShellInputDaemon`):** Fixes pointer speed scaling (`scaledSpeed = rawSpeed * scale` threshold domain matching verified hardware measurements), replaces exponential single-axis speed guesswork with a zero-allocation per-axis 100 ms least-squares trajectory predictor (`AospAxisTracker`), and removes premature X/Y speed wipeouts on diagonal curve bends (`Err < 1 px` across slow, fast, and curved sweeps instead of up to 33% shortfalls).
- **Sub-frame 2× VSYNC micro-glide & jitter-free deadline pacing (`ShellPointerMath`, `ShellInputDaemon`):** Splits slower or jittery mouse/touchpad streams (such as 125 Hz optical mice, 60 Hz touchpads, or wireless bursts) evenly across lock-free sub-frame ticks (3.47 ms / 288 Hz on 144 Hz panels) while passing >=180 Hz mice and pre-click snaps immediately with zero added lag, and replaces regex move parsing and sloppy `Object.wait` sleeps with alloc-free ASCII parsing and sub-millisecond `LockSupport` deadline pacing.
- **High-refresh display lock during kernel UHID control (`DeskTouchAccessibilityService`, `PhoneScreenGeometry`, `DeskTouchSession`):** Retains an active peak display refresh-rate/mode vote while Windows controls the phone via kernel UHID so the panel does not drop to 50/60 Hz idle modes during cursor movement.
- **Kernel cursor persistence & wireless mode improvements (`ShellInputClient`, `AdbCableBridge`, `DeskTouchSession`, `AdaptiveLinkTracker`, `FastUdpChannel`):** Persists active virtual cursor bridge credentials safely across app restarts and USB cable unplugs so wireless sessions reuse the running kernel cursor instead of falling back to the Accessibility blue-dot overlay; stops slow TCP heartbeat jitter from polluting fast UDP wireless ping/jitter metrics; removes redundant upstream keepalive collisions on 2.4 GHz Wi-Fi; and prevents stale burst-loss values from sticking on quiet wireless links.
- **Windows RawInput blackout guard (`SourceCapture`):** Requires sustained (>30 ms, multiple missed hooks) RawInput absence before triggering touchpad hook fallback so brief optical-mouse pauses never black out 24 ms of native raw packets.

---

# DeskTouch Vision v0.3.51 — Elimination of Cursor Sticking, Edge Trapping & 144Hz Frame Skipping

Solves cursor stalls/sticking and frame skipping end-to-end across Windows capture, data-cable/wireless pacing, and Android kernel `/dev/uhid` mouse injection:

- **Eliminate cursor sticking & partial-screen stalls (`ShellInputDaemon`, `ShellPointerMath`, `ShellInputClient`, `FastMovePacer`, `SourceCapture`):** Probes Android `dumpsys input` specifically for `"DeskTouch USB Mouse"` *after* `/dev/uhid` device creation (with a delayed retry if InputReader is still initializing) instead of reading before device creation; removes the 30% velocity-gain damper and tunes AOSP velocity tracking; emits exact 1-count opposite-sign resets on exact-border entries; preserves slow precision micro-steps without 0-count rounding drops; preserves X and Y edge-push deltas independently during diagonal boundary slides; fixes un-bridged outer boundary edge-push delivery; and raises accidental Android return-edge push hysteresis cleanly so laggy flicks reach 100% of the physical screen.
- **Eliminate 144Hz frame skipping & double-pacer jitter (`NetworkSessions`, `FastUdpChannel`, `ShellInputDaemon`):** Removes redundant 4.17ms Android sleep pacing in front of fresh incoming pointer packets (keeping pacing strictly for internal multi-step coasting); removes blocking 12ms prime stalls on first cursor movement by priming during device registration; stops up to 33ms socket-reader sleeps before clicks/scrolls; upgrades wired data-cable cadence (`<=2.4ms` at 144Hz) so every 6.94ms display frame receives fresh motion; eliminates lock contention from background cable keepalives during active swipes; and cuts touchpad fallback transition delay in half.

# DeskTouch Vision v0.3.50 — Full-Screen Cursor Completion, Clean Data Cable UI & Accurate Low-Loss Wireless Link

Fixes incomplete full-screen traversal and mid-screen stalls during fast swipes by completing remaining multi-frame UHID steps cleanly and forwarding boundary edge-push deltas over wireless; removes misleading data-cable ping polling and displays clean Data cable status with ping, jitter, and percentage loss shown strictly on wireless connections; eliminates false wireless packet-loss inflation from reordered frames, prevents same-AP epoch resets, prioritizes wireless QoS (`0xB8`), and backs off redundant 2.4 GHz keepalive traffic.

# DeskTouch Vision v0.3.49 — Reliable Trust Persistence, Smooth 144Hz Cursor Motion & Low-Latency Cable Ping

Fixes "Trusted computers saved: 0" persistence failures and UI refresh delays on Android, eliminates initial UHID buffer overruns and microsecond cursor burst stalls for smooth 144Hz tracking without frame skipping, and prioritizes ping/pong heartbeats over pointer traffic to keep wired ping well under 10ms.

# DeskTouch Vision v0.3.48 — Native Screen Dimensions, Smooth Un-broken Movement & Reliable Trusted Device Linking

Fixes the v0.3.47 cursor stalling/breaking regression, preserves the connected Android phone's real original screen dimensions without comparing or shrinking against Windows monitors, and ensures trusted device linking works reliably from either device without losing saved keys or creating duplicate records.

- **Native Android screen dimensions & unrestricted movement (`DisplayArrangementControl`, `SourceCapture`, `FastMovePacer`):** Displays and maps Android devices at their actual reported resolution (such as 1080×2460) instead of scaling/clamping them relative to the Windows screen height, ensures edge-adjacent docked tall screens can always cross smoothly back across all corners, removes the restrictive 450-count Android mouse delta cap, raises Android return-edge hysteresis cleanly, and merges coalesced boundary push deltas without dropping motion.
- **Smooth jitter-free UHID pointer tracking (`ShellInputDaemon`, `ShellPointerMath`, `ShellInputClient`):** Restores the non-blocking `DeskTouch-UhidPacer` emission loop with boundary-push accumulation and anti-clump framing so TCP/USB packet bursts never stall `/dev/uhid` or spike velocity calculations, fixes 3× initial entry overshoot on saturated burst reports, and eliminates trailing 130 ms sync wakes on cable sessions.
- **Reliable mutual trusted-device saving & persistence (`TrustedDevices`, `DisplaySettingsForm`, `NetworkSessions`, `DeskTouchSession`):** Enables trusting an already-connected inbound device directly from Windows (`trust-offer` / `trust-accepted`), deduplicates incoming trust records by device identity, replies cleanly on busy trust requests instead of leaving Android waiting, updates trust status immediately across UI screens, and preserves Android's saved key across transient disconnects instead of erasing it on interrupted handshakes.

---

# DeskTouch Vision v0.3.47 — Full-Screen Cursor Tracking & Sub-10ms Cable Ping

Fixes the v0.3.45/v0.3.46 regression where cursor movement stalled or remained trapped inside only part of the Android screen, eliminates double-pacer cable jitter, and reduces wired USB data-cable RTT spikes below 10 ms.

- **Full-screen reachability & edge-overflow pass-through (`ShellPointerMath`, `SourceCapture`, `ShellInputClient`):** Anchors initial pointer entry from the nearest display corner using exact baseline velocity scale (`params.scale`), removes positive-feedback velocity drift on USB bursts, and packs Windows boundary push deltas (`Packet.D`) so continuous movement past clamped screen borders always reaches 100% of the Android screen without stalling or corner bounce.
- **Single-stage jitter-free UHID emission (`ShellInputDaemon`):** Removes the duplicate Android secondary sleep pacer so coalesced `MOVETO` frames execute directly on an `URGENT_DISPLAY` thread aligned with Windows `FastMovePacer` framing.
- **Sub-10ms wired data-cable latency & ADB scheduling (`NetworkSessions`, `FastUdpChannel`, `AdbCableBridge`, `DeskTouchSession`):** Aligns wired `FastMovePacer` interval to 1x live refresh rate to prevent ADB `WRTE/OKAY` congestion, tightens wired keepalive probe cadence (38 ms active / 120 ms idle) to stop USB idle-wakeup spikes, elevates running `adb` processes to `AboveNormal` priority during wired sync, removes double socket buffering on Android, and replies to heartbeats immediately on the urgent read loop.

---

# DeskTouch Vision v0.3.46 — Instant Data-Cable Screen Crossing Fix

Fixes the v0.3.45 regression where moving the cursor across the screen edge over a USB data cable stalled on "waiting for cursor/authority application acknowledgement" and timed out instead of handing control to Android.

- **Preserve queued cable pointer frames in the pacer:** Fixes `FastMovePacer` cable dispatch immediately clearing `LatestReverseMove` / `LatestMove` after queueing the initial `reverse-move` frame, restoring immediate cursor handoff (`reverse-move` -> `reverse-active`) on both direct and relayed cable sessions.
- **End-to-end cable handoff regression test:** Verifies a loopback Android cable session receives `reverse-claim`, replies `reverse-granted`, receives the initial `reverse-move` coordinate without stalling, and completes `reverse-active`.

---

# DeskTouch Vision v0.3.45 — Smooth Native Data-Cable Cursor, Persistent Native Pointer & Reliable Trusted-Device Save

Eliminates USB data-cable pointer jitter and speed wobble, prevents the Android pointer from flipping into the fallback software dot after repeated screen crossings, and fixes trusted-device saves being rejected when approving from Windows.

- **Smooth frame-aligned cable cursor & speed-curve compensation:** Paces Windows-to-phone cable pointer frames cleanly at half-frame intervals (~3.5 ms at 144 Hz) and adds a dedicated nanosecond-paced Android virtual mouse emitter that matches Android's system pointer velocity and acceleration curve with sub-pixel carryover while removing repeated edge-kick velocity spikes.
- **Persistent native cursor across repeated screen crossings:** Fixes buffered `MOVETO` command coalescing swallowing trailing `BEGIN`/`END`/`PING` acknowledgments, preserves virtual mouse/keyboard registration across brief loopback reconnects, and self-heals the loopback bridge automatically on screen-entry claims instead of falling back to the Accessibility dot.
- **Non-blocking trusted-device approval (`trust-request`):** Runs the Windows trust confirmation prompt asynchronously off the TLS read loop so heartbeats continue uninterrupted while deciding, correlates Android trust proposals by request ID, and prevents duplicate button taps from invalidating the pending key.
- **Automatic virtual-bridge daemon refresh on app upgrade:** Tracks the installed APK path alongside the daemon PID so upgrading the Android app immediately reloads the updated daemon over cable without requiring a manual restart.

---

# DeskTouch Vision v0.3.44 — Bulletproof Windows Reopen & Recovery While Background Process Is Running

Fixes the issue where launching DeskTouch Vision while `DeskTouch.DeviceShare` was already running in Task Manager (tray, minimized, background/other desktop, or mid-shutdown) could exit without opening a visible window on screen.

- **Verified desktop window wakeup & automatic recovery (`SingleInstanceActivation`):** Launching DeskTouch Vision (`DeskTouchVision.exe` or `DeskTouch.DeviceShare.exe` directly) serializes rapid double-clicks, checks the caller's active desktop to confirm the Displays window actually becomes visible, un-minimized, and responsive, and automatically shuts down and replaces orphaned, stuck, or non-visible background instances so the Windows app always opens cleanly.
- **Reliable WinForms tray/minimized restoration (`DisplaySettingsForm`):** Closes the startup `--tray` vs wakeup signal race, restores minimized windows cleanly before clamping off-screen coordinates, and brings the existing window to the foreground cleanly without breaking input hooks.

---

# DeskTouch Vision v0.3.43 — Zero-Delay Cable, Band-Adaptive Wireless Ping, Dynamic Cable↔Wireless Shifting & Keyboard Binding Live Inspector

Delivers instant USB data-cable input and seamless switching between wireless bands (5 GHz ↔ 2.4 GHz ↔ 6 GHz) and the USB data cable, instant keyboard handoff across edges without waiting for Windows key recovery, real Android shortcut transparency backed by the connected keyboard layout, and live ping metrics across both Windows and Android.

- **Instant data-cable Virtual Bridge (`BUS_USB` + persistent virtual hardware + move coalescing):** Keeps the virtual mouse and keyboard devices mounted across screen crossings while releasing held inputs on handoff, coalesces queued pointer bursts cleanly, eliminates periodic 15-second package queries while healthy, and tracks live sub-millisecond cable RTT.
- **Immediate keyboard handoff across screen edges:** Proactively refreshes keyboard capture on handoff arrival, recovers on the very first missed raw keystroke, and passes supported modifier and Windows-key combinations without stalling until manual key recovery.
- **Online-game-style band & network-shift adaptive ping:** Automatically detects wireless band changes (2.4 GHz / 5 GHz / 6 GHz) and network transitions, clears stale congestion baselines (e.g. 280 ms carryover), adapts probe/pacing cadences to the active band, authenticates UDP roaming endpoints securely, and fails dead links in ~4 seconds.
- **Dynamic make-before-break Wireless ↔ Data Cable shifting:** Promotes USB data-cable sessions only after the encrypted connection completes (never dropping wireless prematurely), triggers fast multi-probe attach sweeps, and fails back immediately over cached-host wireless when the data cable is unplugged.
- **Connected keyboard & supported Android shortcuts view:** Inspects your Windows keyboard layout and connected Android/Virtual keyboard capabilities directly, surfacing supported bindings live in both Windows (`Shortcuts`) and Android without fabricating unsupported OS shortcuts.

---

# DeskTouch Vision v0.3.42 — Reliable Window Activation, Foreground Reopen & Tray Upgrades

Fixes cases where launching DeskTouch Vision again while running in the system tray failed to pop the Displays window into view, adds a Start Menu app shortcut alongside Desktop launch, and ensures installer upgrades stop tray instances gracefully.

- **Foreground single-instance wakeup:** Passes Windows foreground permission from the launcher (`DeskTouchVision.exe`) to the active Displays window, adds a per-user wakeup signal alongside window broadcast messaging, restores minimized or off-screen bounds safely inside the active display, and brings the window cleanly to the front.
- **Start Menu launch & reliable tray shutdown for installers:** Installs a normal Start Menu shortcut (`DeskTouch Vision`) so searching Windows Start launches the visible app rather than only the background sign-in entry, and adds a graceful shutdown signal so installer and updater scripts close tray instances cleanly while preserving input-hook safety.

---

# DeskTouch Vision v0.3.41 — Reliable Virtual Bridge Startup & Non-Locking Folder Updates

Fixes a shell self-termination race when launching the Accessibility-free Virtual Bridge daemon on Android and ensures background cable monitoring no longer locks the Windows installation folder during updates.

- **PID-file Virtual Bridge lifecycle (`--pid-file` + `kill -0`):** Tracks the running virtual mouse and keyboard daemon by exact PID file instead of process-name pattern matching so the launch shell never terminates itself mid-startup.
- **Update-safe Windows bridge process launch:** Starts background cable tooling from its own SDK working directory so in-place Windows updates swap folders cleanly without directory handle locks.

---

# DeskTouch Vision v0.3.40 — Accessibility-Free Virtual Bridge & Payment-Safe Mode

DeskTouch Vision now controls your Android phone's mouse cursor, clicks, scroll wheel, and keyboard without enabling Android Accessibility Settings whenever activated from your connected Windows PC, while keeping 1-tap and automatic Accessibility pause controls ready when Accessibility mode is used.

- **Accessibility-free Virtual Mouse & Keyboard Bridge:** When an authorized phone is connected over its data cable, DeskTouch launches a local virtual pointer and keyboard daemon directly via Android's shell privilege (`app_process` `/dev/uhid`), receives a fresh loopback token over the mutual-TLS connection, and tears down virtual hardware cleanly between handoffs so on-screen typing stays unaffected.
- **Payment-safe automatic Accessibility pause & Home widget:** When using Android Accessibility fallback, DeskTouch automatically turns Accessibility off upon opening common payment or banking apps and 15 seconds after disconnect, adds a 1-tap "Pause phone for payments" control on Windows plus a Home screen Pay Mode button on Android, and keeps remote control status visible in notifications and Quick Settings.

---

# DeskTouch Vision v0.3.39 — Responsive Pacing Caps, Automatic USB Data-Cable Routing & Low-Latency Tracking

DeskTouch Vision cuts wireless frame-pacing caps in half on high-refresh phones, removes Android display-mode ceiling clamping above 144 Hz, doubles background probe responsiveness, and automatically upgrades plugged-in Android phones onto a direct USB data-cable tunnel with zero artificial pacing delay.

- **Tighter wireless pacing caps & faster recovery:** Lowers maximum adaptive wireless intervals from 32 ms down to 14 ms on 120–144 Hz phones (16 ms at 90 Hz, 18.5 ms at 60 Hz) while softening jitter, queue-bloat, RTT, and packet-loss slopes and speeding EWMA loss recovery so brief wireless drops bounce back briskly.
- **2x faster background health probes & lighter Android smoothing:** Cuts Windows active health/telemetry loop intervals from 200 ms to 100 ms (still logging every 3 s), trims Android cursor smoothing delay (under 10 ms at 60 Hz and under 8 ms at 144 Hz), and raises supported active refresh-rate detection up to 240 Hz.
- **Automatic USB data-cable discovery & failover:** Automatically registers `adb reverse` loopback forwarding on wired Android devices (safely tolerating cold `adb` server startup), discovers and connects directly over localhost USB with zero UDP pacing delay and TCP ping tracking, auto-upgrades idle wireless sessions when plugged in, and seamlessly falls back to Wi-Fi if unplugged.

---

# DeskTouch Vision v0.3.38 — Keyboard Hook Self-Healing, Modifier Resync & Protected-Window Diagnostics

DeskTouch Vision automatically recovers Windows keyboard capture if another utility preempts global input hooks, prevents stuck pre-handoff modifier keys from blocking phone typing, eliminates false unavailable warnings when pressing the Windows key while controlling an Android phone, and reports when Windows or security keyboard filters withhold keystrokes for an active protected window.

- **Automatic keyboard hook preemption recovery:** Detects when physical keyboard activity arrives in remote mode while DeskTouch's low-level keyboard hook receives zero events and automatically re-registers the hook to reclaim top-of-chain delivery without requiring a focus toggle.
- **Pre-handoff modifier resync & clean Android meta handling:** Resynchronizes local `Ctrl`/`Alt`/`Shift` state from Windows when remote authority confirms so missed pre-handoff key-up events no longer block Android printable text routing, and suppresses unneeded Windows-key round trips that triggered false `Android keyboard input unavailable` warnings on Android targets.
- **Clear protected-window and antivirus-shield diagnostics:** Inspects active foreground protection read-only on handoff and when key delivery remains blocked after hook refresh, surfacing actionable status and log notices naming the active protected window when Windows UIPI or a security keyboard filter withholds keyboard capture.

---

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
