# DeskTouch Vision v0.3.19 — Android keyboard input and shared-device verification

## What's new
- DeskTouch Vision for Android now includes an **optional, user-selected Android keyboard service** inside the existing application. It translates the approved Windows text, editing and navigation commands into an Android input connection for the currently focused editable field.
- Enable and select **DeskTouch keyboard receiver** from the existing Android app. Android's system settings must authorize a third-party keyboard; DeskTouch never silently grants that permission.
- Existing accessibility mouse cursor, clicks, dragging and scrolling remain in place. Existing Windows-to-Windows handoff, authenticated multi-device TLS environment, local physical-input priority, Android device placement, and trusted identity behavior are preserved.
- After an in-place Android update, DeskTouch continues to report Accessibility permission health and prompts for Android's own reapproval if Xiaomi/Android disables it.

## GitHub verification before release
- Android 35 emulator: real UI click, drag, scroll, selection of DeskTouch keyboard, and cross-process text entry including Backspace, caret movement and insertion.
- Android 35 emulator: DeskTouch's existing reverse-text and reverse-key input handlers successfully update a separate-process editor.
- Android JVM protocol/input tests, APK compilation, Windows 3-client authority and 8-peer load tests, Windows runtime build, tray/update regression tests and installer integrity checks.
- Official Android APK must be signed by the *existing permanent DeskTouch signing key*; its signature is verified against the pinned certificate, never replaced by a new signing identity.
- Windows release ZIP includes installer and SHA-256. Update notifications are sent after the permanently signed Android APK and Windows release assets are both available.

## Important limits
- Android keyboard service is **not a Bluetooth peripheral, Bluetooth loopback, or true kernel HID device**. It operates through Android's allowed input-method and Accessibility APIs, with the permissions the user grants.
- GitHub emulator tests use simulated approved input commands, not a real Xiaomi phone, live Windows-to-phone Wi-Fi handoff, physical Bluetooth pairing, or every third-party app. Confirm real-device keyboard focus, Chrome typing, pointer latency, two-PC placement and reconnect behavior on your own devices.
- Do not uninstall the Android app to update: install the official matching-cert v0.3.19 APK over v0.3.18 so trusted pairings/app data can be preserved. Android may require reapproval of Accessibility or keyboard selection.
