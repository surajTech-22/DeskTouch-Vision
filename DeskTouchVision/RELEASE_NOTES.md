# DeskTouch Vision v0.3.20 — Android pointer handoff resilience

## Changes
- Fix a Windows display-arrangement mismatch where the default gap between PC and portrait Android previews could exceed the actual edge-routing tolerance, making the phone visible but unreachable by cursor handoff. The linked phone now sits at a reachable seam across different Displays window sizes.
- Keep Android devices with enabled input permission next to the Windows monitors; a view-only phone can no longer block the path to a controllable phone.
- Add Windows regression tests for entering and leaving the Android screen at multiple diagram scales and for mixed view-only/controllable Android devices.
- Preserve the existing Windows↔Windows and Windows→Android authenticated TLS handoff and exclusive authority leases.
- Android normally renders its cursor through the existing single non-touchable full-screen Accessibility overlay at display refresh rate, without doing a window relayout on every mouse movement.
- If an Android OEM/MIUI/Xiaomi system rejects that full-screen overlay, DeskTouch retries a compact non-touchable `TYPE_ACCESSIBILITY_OVERLAY` pointer inside the **same existing app**, without requiring root, fake Bluetooth, a separate installed app, or bypassing Android permissions.
- If the Android operating system rejects both overlay types or blocks a subsequent overlay layout update, DeskTouch safely revokes the phone's remote input lease and gives Windows back its native keyboard and mouse instead of waiting on a pointer-applied acknowledgement that cannot arrive.
- An actionable Android pointer overlay status and non-sensitive diagnostic log event explain what failed; no typed text, coordinates, device identity, or LAN details are logged.
- Includes unit tests for position clamping and compact overlay boundary behavior. Existing Android input receiver / IME, Windows multi-peer authority and device placement are reused unchanged.

## Validation and limits
- GitHub tests: Android JVM tests, Android emulator click/drag/scroll/IME and reverse text/key acceptance, Windows shared-environment TLS and installer/update safety.
- A GitHub emulator cannot establish Xiaomi's actual OEM window-overlay policy or guarantee the user's real phone grants accessibility/display permissions. On the phone, leave the existing app installed; install the same-cert update, enable Windows control and the DeskTouch Accessibility service. Check the phone's overlay restrictions if control is revoked.
- A native Bluetooth HID keyboard/mouse is not emulated by this update.
