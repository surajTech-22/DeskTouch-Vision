# DeskTouch Vision for Android (same product and repository)

**Android release 0.3.8** reuses the Windows DeskTouch LAN discovery (UDP 37378),
TLS endpoint (TCP 37377), existing approval prompt, monitor geometry, Wire
framing and two-phase authority handoff. There is **no separate remote-desktop
protocol, login system, or replacement keyboard/mouse settings**.

## Build

```bash
cd AndroidDeskTouch
gradle :app:testDebugUnitTest :app:assembleDebug
```

GitHub Actions runs Android unit tests and development-only debug builds on
branches. After a successful Windows release on **main**, the Android release
workflow **requires the same permanent signing certificate** and attaches the
verified, signed APK to that release. If the private signing secrets are absent
or incorrect, the official Android release **fails closed**. The existing
Android APK asset filename is unchanged to preserve Windows QR and Android
updater compatibility.

**First signed release:** v0.3.8 uses the persistent DeskTouch Android signing
identity. An Android installation of the earlier debug-signed v0.3.7 may need
one manual uninstall/reinstall, which resets app-local trusted devices. Then
all later releases signed with this same identity can update in place with
Android package installer approval. See
`ANDROID_UPDATES_AND_SIGNING.md` for the pinned public certificate and
one-time secret configuration. Do **not** commit the private keystore.

## Use

1. Run DeskTouch Vision v0.3.8 or newer on Windows and keep it visible or in tray.
2. Install the APK via Android's own package installer after scanning Windows QR.
3. Open the Android app and **Find devices** on the same private LAN.
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
9. **Startup and reconnect** are enabled by default after the first launch.
   The foreground connection notification persists when the app is backgrounded.
   After Windows has approved a trusted device token, the Android background
   service scans the local LAN every 10 seconds and securely reconnects to that
   exact TLS identity (no saved IP). Reconnection does **not** steal input
   authority; take control deliberately after bringing the app to the front.
   The startup checkbox can disable boot launch; the Disconnect button pauses
   trusted reconnection until manually re-enabled.
10. **Two or more PCs** on the LAN appear in Add devices even if one is busy.
   Android phones also advertise their presence. Windows receiver sessions are
   currently limited to one active remote-control peer at a time; switching
   to a different PC requires explicit disconnect/confirmation. Presence does
   not imply two simultaneous input-control connections.
11. On Windows, **Launch in tray when Windows starts** is enabled for the
   current Windows user; the tray menu can disable it. To fix a refused TCP
   connection, open Windows Add devices → Fix LAN access (UAC consent required).
   This adds only application-scoped TCP 37377/UDP 37378 inbound rules on
   Private, LocalSubnet networks; it never opens a Public network rule.


## Known limitations / fail-safe behavior

- Android does **not** expose unrestricted system-wide cursor injection to an
  ordinary third-party app. Windows-to-Android reverse claims are explicitly
  rejected rather than falsely acknowledged; this is not yet full two-way
  control. Android accessibility gestures alone cannot guarantee hardware-cursor
  movement ACK or 1:1 mouse event behavior.
- Pointer capture and physical keyboard forwarding are available **while the
  Android activity has focus**. Leaving the app returns Windows control.
  An Android connectedDevice foreground service now continues LAN
  discovery and trusted-device reconnection while backgrounded, where the OS
  allows. It starts after boot without opening the Android UI. Some OEM power
  managers can defer or stop this service; app/notification permissions may
  need adjustment through Android's own Settings.
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
