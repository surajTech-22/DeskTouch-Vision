# v0.3.11 development preview — unified Windows network
Android can show one linked Windows network card and initiate secure connections to its individual PCs in one action. Each Windows member has its own separately approved TLS certificate: a network group is **not** an authentication identity. Once approved, those Windows PCs and other mobile clients remain connected concurrently; Android's active target and Windows' input leases still select a single controlled destination at a time.

To control Windows **from the phone**, connect all members, select a Windows screen in the group, choose **Take control of selected Windows screen**, and wait for the cursor-applied authority acknowledgement. Use the touchpad or the Android keyboard typing field; the on-screen keyboard commits Unicode text through an authenticated, exclusive lease. The sensitivity slider adjusts phone touchpad speed. Select another connected PC or deliberately move outward across a linked group edge to request a new handoff. The Windows machine's local keyboard/mouse takes priority.

To control Android **from Windows**, enable DeskTouch's Windows-control checkbox and its Accessibility service in Android Settings. A remote Windows keyboard can type only into supported, focused non-password fields; Accessibility is not an unrestricted hardware keyboard driver. No special Android permission is required for the phone's ordinary touchpad/IME to send input to a previously approved Windows PC.

This is a **development** branch; production APK updates must still be signed with the existing permanent Android certificate, and physical multi-device acceptance is required before release.

---

# DeskTouch Vision for Android (same product and repository)

**Android release 0.3.9** reuses the Windows DeskTouch LAN discovery (UDP 37378),
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
