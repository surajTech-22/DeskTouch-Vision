# DeskTouch Vision v0.3.27 — Ultra-fast wireless cursor, instant clicks/drags & zero queue lag

DeskTouch Vision now delivers near-wired cursor, drag, scroll, and click responsiveness between **Windows PCs and Android phones** over your private Wi-Fi network without Developer Options, cables, or Bluetooth dongles.

- **Authenticated fast coordinate channel (AES-256-GCM UDP + QoS):** Rapid and sudden mouse movements now bypass single-lane TCP head-of-line stalling, recover cleanly from any dropped Wi-Fi frames with zero coordinate drift, pre-warm the phone radio on edge approach, and sync final landing/pre-click positions reliably.
- **Instant Android clicks, live drags, & smooth scrolling:** Removed synchronous accessibility tree scans from every tap and wheel tick, replaced post-release drag replay with live real-time touch continuation (`down -> move -> up`), coalesced rapid wheel scrolling, and switched the phone cursor dot to hardware-accelerated translation with zero per-frame redraws.
- **Smoother Windows input capture:** Pre-allocated reusable Raw Input buffers, eliminated redundant mouse hook allocations, enabled 60ms touchpad fallback, offloaded diagnostic log writes off the UI thread, and raised capture priority while active.
- **Includes everything from v0.3.26:** Screen-edge glide + `Ctrl + Alt + M`, Android 13–15 keyboard/search fixes, 1-Tap Pause for Banking, automatic Wi-Fi reconnect, and QR code phone installer.
