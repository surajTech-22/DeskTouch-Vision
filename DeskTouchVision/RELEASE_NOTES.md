# DeskTouch Vision v0.3.24 — Windows-only stability

DeskTouch Vision now supports **Windows-to-Windows computer sharing only**.

- Remove the Android app project, Android update/signing workflow, Bluetooth HID and ADB/UHID experiments, and mobile controls.
- Accept only Windows clients in the authenticated TLS hello and Windows members in the shared-environment roster. Old Android clients cannot claim input authority.
- Keep the existing multi-PC encrypted TLS hub, approved pairing and permanent per-device trust identity; never identify a PC by remembered IP address.
- Preserve both directions of mouse/keyboard authority, physical local-input priority, key/button release before handoff, emergency Ctrl+Alt+Shift+F12 and monitor boundary safeguards.
- Check trusted Windows devices every 5 seconds instead of 8.5 seconds, with 15-second retry backoff. No duplicate network connection on edge handoff.
- Keep the listener, hooks and release-event updater running when hidden in tray. Verified updates close cleanly before installation.
- Simplify the interface to Windows device management and retain the existing draggable monitor arrangement.
- Publish only the Windows ZIP and SHA-256, with a Windows-only update notification after assets are verified.
- Expand Windows-only tests for legacy client rejection, secure discovery, connected-peer deduplication, multi-PC screen arrangement, tray lifetime and native authority.

For this Windows-only release, install the same version on every PC. CI verifies the Windows code and protocol; physical two-PC handoff must still be confirmed on your devices.
