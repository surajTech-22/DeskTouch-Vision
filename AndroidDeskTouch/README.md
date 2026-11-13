# v0.3.12 development preview — one shared DeskTouch environment

**One environment, multiple connected devices.** Select **Join environment** on Android. The phone connects to one Windows hub using its pinned TLS certificate; other PCs and phones join the **same** hub separately. Once connected, the hub sends an authenticated list of all its members. Select any supported member from this list; the hub routes control there without requiring another pairing or separate Android connection.

To add another Windows PC, open DeskTouch on that PC and join the current hub; to add another mobile, choose that same hub in the mobile app. Each device must be approved by the hub. One destination accepts one remote input controller at a time, while all other devices stay connected and can continue operating independently. Hardware input on the destination takes priority.

Android's on-screen typing field / physical keyboard can control selected Windows programs when a remote lease is active. Windows can control Android using its opt-in Accessibility service; normal password-field protections remain. Hub role discovery avoids mistakenly joining a connected Windows leaf.

The current hub accepts **up to 8 simultaneous incoming devices**. Devices must share a reachable private LAN; no external server or IP-address based trust is used. This is a single hub-based environment, not a transparent cross-network mesh. Real multi-device and permission testing is required before official release; use only the existing permanent Android signing identity.

---



# DeskTouch Vision for Android (same product and repository)

**Earlier Android release 0.3.9** reuses the Windows DeskTouch LAN discovery (UDP 37378),
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

## Use (v0.3.9 one-way mode: Windows controls Android)

1. Install **v0.3.9 on both Windows PCs and the Android phone**.
2. On the phone, open DeskTouch Vision and select **Allow approved Windows PCs to
   control this Android screen**. Open Android **Accessibility settings**, find
   **DeskTouch Windows control**, and manually enable it. Android always owns
   this permission. Some Android 13+ devices require the app's own **Allow
   restricted settings** option in App info before sideloaded services appear.
   The app does not bypass that restriction.
3. Use **Find devices** and connect Android to a Windows PC using the existing
   TLS fingerprint verification and Windows pairing approval. Enable **Trust**
   if you want it to reconnect automatically after future launches.
4. Reconnect the phone if it was paired before enabling Accessibility; the
   advertised `ReverseInputSupported` capability is negotiated in TLS hello.
5. In **Windows DeskTouch → Displays**, choose the phone in the new
   **Connected device target** selector. The arrangement diagram shows the
   actual Windows computer name and Android manufacturer/model **at the top of
   each screen**, and independent numbering for each system. Drag the Android
   screen to the outer left/right/top/bottom edge.
6. Move the Windows physical cursor to that outside edge. The first
   authenticated claim is acknowledged by Android **only after** its virtual
   pointer is shown. Windows then forwards mouse movement, taps, swipes,
   scrolling and text into ordinary editable Android fields over the **same
   existing connection**. The Windows keyboard is not forwarded to Android
   until this claim/first-frame ACK succeeds.
7. Android physical touch, revoking Accessibility permission, or selecting
   **Stop Windows control on this phone** immediately cancels remote authority.
   Windows retains its physical keyboard and cursor. The other trusted devices
   remain connected and do not acquire control automatically.
8. The stable Android signing certificate introduced in v0.3.8 is unchanged.
   Use **Get updates / Update existing application** to update v0.3.8→v0.3.9
   without uninstalling or clearing trust records.

The original Android touchpad-to-Windows control buttons are removed for now;
this release intentionally uses **Windows → Android** only. Windows-to-Windows
behavior remains on the existing shared listener with one input lease per
controlled desktop. Multiple secure identities may be visible in the same
linked network; a visual group **is not** a transparent network relay.

## Known limitations / fail-safe behavior

- Android Accessibility can perform approved tap/swipe gestures and
  `ACTION_SET_TEXT` edits in accessible, non-password fields. It does **not**
  provide an unrestricted native Android hardware cursor/keyboard driver,
  arbitrary key injection, screen streaming or root permission. Some
  apps/web pages may not support Accessibility text editing, and this is not
  guaranteed to replicate every Windows key shortcut.
- The visible Android pointer is an **Accessibility overlay**, not the OS's
  hardware pointer. It receives remote movement without taking touch focus.
  Phone-local touch has authority priority and can end a remote lease.
- A linked pair of Windows PCs remains **two cryptographically distinct
  devices**. The selection list reuses authenticated connections without
  creating a new socket when changing the target, but this build does not
  relay Android control transparently through an unrelated Windows PC's TLS
  link. Doing so would require a separately approved, identity-bound relay.
- The foreground service may be restricted by OEM Android battery rules.
  Network discovery requires the same routed LAN and Windows firewall
  Private LocalSubnet permissions. No antivirus exclusion is required.
- GitHub CI verifies protocol, input policy and package compilation. A real
  test with the Android Accessibility service and both physical Windows PCs
  is required to confirm device-specific behavior.

**Never commit the private Android signing keystore or its password.**
