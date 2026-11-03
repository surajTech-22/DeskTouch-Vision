# DeskTouch Vision for Android (same product and repository)

**Android preview 0.3.1** reuses the Windows DeskTouch LAN discovery (UDP 37378),
TLS endpoint (TCP 37377), existing approval prompt, monitor geometry, Wire
framing and two-phase authority handoff. There is **no separate remote-desktop
protocol, login system, or replacement keyboard/mouse settings**.

## Build

```bash
cd AndroidDeskTouch
gradle :app:testDebugUnitTest :app:assembleDebug
```

GitHub Actions builds and uploads an installable debug-preview APK. After a
matching Windows release completes, its workflow attaches
`DeskTouchVision-android-preview.apk` to the same release. The Windows app's
**Get Android app** button or tray menu displays a QR only if the official APK
is already present in the latest release.

**Signing limitation:** this CI debug-preview uses ephemeral debug signatures.
An upgrade from a different CI run may need uninstall/reinstall and may lose
local trusted-device state. Production Android auto-updates require a stable,
privately held signing key, supplied as a CI secret. Do **not** commit it.

## Use

1. Run DeskTouch Vision v0.3.1 or newer on Windows and keep it visible or in tray.
2. Install the APK via Android's own package installer after scanning Windows QR.
3. Open the Android app and **Find Windows computers** on the same private LAN.
4. Select Windows PC and approve the pairing prompt on **Windows**.
5. Tap **Take control of Windows** when the physical Windows keyboard/mouse is idle.
   Only after a matching `granted` and cursor-applied `active` ACK will Android
   send movement, clicks, keyboard and scroll.
6. Use the touchpad area or connected Android hardware keyboard/mouse. For the
   latter, select **Capture connected physical mouse**; Android's
   `requestPointerCapture` requires the app to retain focus.
7. To remember the paired PC independently of IP, select **Trust connected PC**
   and approve the second Windows prompt. A per-peer token is encrypted with
   Android Keystore and tied to the Windows certificate SHA-256 fingerprint.
8. Use **Android keyboard settings** / **Android connected device settings**
   to open Android's built-in system settings. The app does not implement
   duplicate keyboard/mouse configuration.

## Known limitations / fail-safe behavior

- Android does **not** expose unrestricted system-wide cursor injection to an
  ordinary third-party app. Windows-to-Android reverse claims are explicitly
  rejected rather than falsely acknowledged; this is not yet full two-way
  control. Android accessibility gestures alone cannot guarantee hardware-cursor
  movement ACK or 1:1 mouse event behavior.
- Pointer capture and physical keyboard forwarding are available **while the
  Android activity has focus**. Leaving the app returns Windows control.
  Android background service and automatic trusted reconnection are not yet
  available, notwithstanding stored trust tokens.
- No screen streaming, file transfer or Android screen takeover is implemented.
- Network discovery may be blocked by router isolation; same-routed-LAN
  connectivity and Windows firewall TCP/UDP rules are required.
- Tests in Actions validate compilation/unit tests; physical Android+Windows
  input, Android brand/driver quirks and Windows two-PC authority handoff
  require on-device validation.
- First pairing pins the discovered TLS fingerprint; the Windows owner must
  also explicitly approve pairing. The authenticated trust token is never
  identified by the phone's hostname or IP address.

These are explicit preview limits, not hidden workarounds or promises of
unrestricted Android OS control.
