# DeskTouch Vision v0.3.15 — simultaneous authority, Android keyboard and updater recovery

## One environment, all devices functional
- Fixed the root cause of Android selection disabling the other Windows system: a phone or PC claiming **input on the hub** was incorrectly changing the hub operator's **remote target**. Forward ownership and reverse destination selection are now independent; authenticated clients stay online and can simultaneously control different destinations.
- Repaired Windows selected-peer event handling. Peer-list updates and selection callbacks can no longer dispose the newly armed keyboard/mouse capture for a still-connected screen.
- Added automatic horizontal edge navigation based on the existing screen diagram (e.g., **SURAJ ← Windows → Android**, or **hub → second PC → phone**). Switching uses the already authenticated environment, never a new pairwise connection, and waits for each destination's fresh two-phase input grant.
- Screen positions are now stable regardless of which peer currently has keyboard/mouse authority; Android never replaces or rearranges the Windows display tiles just because it is selected. One physical keyboard controls a single destination at an instant; multiple destinations may be controlled concurrently by different computers/phones.

## Android keyboard, mouse responsiveness and diagnostics
- Android 13+ now prefers the **current live Accessibility InputConnection** for ordinary non-password text entry, including Chrome/WebView editors whose Accessibility root may not expose an editable focused node. Window focus fallback is supported when the editor is exposed by another interactive window. Existing editable-field ACTION_SET_TEXT remains the older-version fallback.
- Delete/backspace uses the supported Android Accessibility InputConnection where available. Password/numeric-password/Web-password fields remain protected.
- Windows Raw Input and touchpad mouse hook can both report the same physical movement. Immediate duplicate deltas are suppressed to reduce alternating fast/slow cursor movement.
- The Android remote cursor now uses a single non-touchable Accessibility overlay surface and redraws only its changed coordinates at display VSync; no per-motion WindowManager relayout. A privacy-safe `DeskTouchTiming` log reports only aggregate frame count, coalesced moves and worst local UI frame delay, never text, keys, screen coordinates or device identifiers.
- Real Xiaomi Wi-Fi jitter and target application focus cannot be certified by GitHub runners. Actual-device acceptance remains essential for latency claims.

## Android permissions survive in-place upgrades
- Persists the user's DeskTouch control approval synchronously and retains existing Android Keystore-encrypted trusted device data. Android package replacement preserves app storage when package ID and permanent signing identity match; an uninstall or data clear does not.
- After `MY_PACKAGE_REPLACED`, checks whether Android/Xiaomi has disabled the separately granted Accessibility service. Shows permission health and, if notifications are allowed, a one-tap reminder to revisit system Settings. **Never** silently turns Accessibility back on or bypasses user consent.
- Once the user restores Accessibility, sends an authenticated capability update to the current hub over the **existing TLS session**, without forcing a second pairing, and revokes active input leases immediately when permission is disabled.

## Second Windows PC updater recovery
- Manual **Checking GitHub releases** now has a 12-second deadline for an occupied update gate and a 25-second deadline for GitHub metadata. Instead of hanging behind a background download or blocked API, it reports a clear busy/timeout/error and writes diagnostics to `%LOCALAPPDATA%\\DeskTouchVision\\logs\\update-check.log`.
- Retains verified checksum, detached updater, safe process shutdown, tray restart, and release-event SSE; no unsafe forced process termination or old updater installation.

## Verification
- Windows shared-environment loopback TLS tests include simultaneous phone→hub plus hub→Windows leases, multi-device edge routing, independent release, eight-peer capacity, native input override, and updater checks.
- Android JUnit tests check input protocol, WebView/nonnative editor safety and password exclusions, and Accessibility system-vs-app permission decisions. The Android 35 emulator checks launch, service enablement, and in-place app-data persistence.
- Publish only with the original permanent Android certificate, the standard Windows SHA-256 package, and successful GitHub pre-release workflow.

---

# DeskTouch Vision v0.3.14 — inline Android display and lower-latency input

## Main display correction
- Connected Android screens are now placed **beside** the Windows monitor tiles in the primary **Rearrange your displays** row, not a separate "Other connected devices" strip. A Xiaomi phone appears as its own portrait screen, named from the authenticated Android endpoint.
- A phone tile is clickable to select the connected target over the existing shared-environment hub. Windows monitor numbering, physical layout and selected-PC drag/edge behavior are retained.
- Live online membership is authoritative: a remembered or offline Android cannot be shown as connected. Android accessibility permission and input authority remain separate from visibility.
- Inline screen-placement tests enforce shared vertical alignment, nonoverlapping hit targets, and Android portrait aspect ratios.

## Android responsiveness and native platform input
- Windows-to-Android pointer movement now coalesces to one **VSync-driven** overlay update per frame, instead of a WindowManager layout transaction for every incoming network packet. Unchanged cursor coordinates trigger no relayout.
- A click/scroll flushes the latest pointer coordinate first so input cannot target the previous visual frame.
- Where Android's Accessibility tree exposes a clickable/scrollable UI target, use its native **ACTION_CLICK** or **ACTION_SCROLL_FORWARD/BACKWARD** immediately. An accessibility gesture is a fallback for controls that lack actions.
- On Android 13+ (API 33), use the public Accessibility **InputMethod / InputConnection.commitText** path for ordinary text in approved non-password focused editors; older phones and custom editors retain the existing ACTION_SET_TEXT fallback. The Accessibility service XML declares the platform's official input-method flag.
- Serialize fallback gestures so a new click/scroll does not cancel one already being processed. Tap/scroll gesture durations were reduced from 90/120ms to 45/85ms.
- Original TLS identity, single logical environment, native Android touch priority, bounded text-entry permissions, and no-password-field policy remain in place.

## Important platform limits
- Android normal third-party applications cannot freely inject full system-wide hardware HID mouse/keyboard events through the public SDK. DeskTouch uses opt-in Accessibility actions and authorized gestures; a visual pointer overlay is not an actual operating-system cursor.
- A GitHub emulator can validate builds and basic UI startup but not reproduce real phone Wi-Fi jitter or the Xiaomi display compositor. Physical input latency and drag behavior require testing on the device after installing the signed APK.
- Release artifacts must use the original permanent Android signing identity and existing SHA-256/update workflow.

---
# DeskTouch Vision v0.3.13 — connected Android devices visible in Displays

- The existing Windows **Displays** diagram now includes a compact row of all additional authenticated Windows/Android environment members, not just the single selected remote computer. Android devices are identified by their actual mobile name and **Android • Display 1**.
- Each phone already connected to the same hub appears automatically when the hub's live roster changes. A phone without Accessibility authorization still appears as connected, marked **View only • permission needed**, so visibility is separate from control authority.
- Clicking a member tile selects that device using the **existing** authenticated environment connection; it does not create a new pairing, disconnect another device, or change Windows monitor configuration.
- Native monitor names/numbering and the selected remote display's drag/edge behavior remain unchanged. The additional row shares the original diagram rather than replacing its design.
- Windows summary now reports the total number of online environment members and Android devices. Offline and separately connected phones are never fabricated as online. On the Android app, **Join environment** must point to the same Windows hub (for example SURAJ) to appear on the other Windows member.
- Regression tests verify two simultaneous Android tiles (including view-only), nonduplicated selected screens, target identities, and WinForms bitmap rendering at compact and standard window sizes.
- Uses the previously verified v0.3.12 shared-environment TLS hub, exclusive input authority, keyboard routing, and permanent Android signing identity.

---
# DeskTouch Vision v0.3.12 — single shared environment (development, not released)

## Real multi-device environment
- Replaces the one-to-one **controller-target connection model** with a Windows **environment hub**, to which multiple Windows PCs and Android phones can join and remain online together.
- A phone establishes **one authenticated TLS connection to the hub**, not one connection to each Windows PC. Each other device independently connects to the same hub; it is not necessary to pair every possible device-to-device combination.
- The hub publishes a live, authenticated membership roster of connected PCs/phones and screen geometry. The Windows and Android clients select the destination from this roster over their **existing** connection.
- Input for another environment member routes through the hub to that member's existing session. No new TCP connection is created when the user changes destination.
- The Android LAN discovery prefers a **hub** rather than an already joined PC, avoiding an accidental split environment. Windows **Add devices** no longer asks to disconnect an existing controller and replace it with another 1:1 connection.

## Keyboard, mouse and authority
- Routed mouse, scroll, clicks, Unicode text and key events use the same authenticated DeskTouch protocol and an explicit per-destination input claim.
- The hub relays the **actual destination's** grant and first-applied-pointer acknowledgement before the controller starts sending keyboard and clicks.
- Only one controller may own a destination at a time; other participants remain connected and may control different destinations simultaneously.
- The local physical mouse/keyboard always takes priority at the destination and revokes the remote lease.
- The hub tracks its **own** direct control leases alongside relayed leases to prevent two controllers from controlling the same phone or PC.
- If a target disconnects, its lease is removed, other clients remain online, and clients receive an updated member list.
- Existing coalesced mouse transport, Windows native sensitivity scaling and Android touchpad sensitivity are retained.

## Pairing / permission boundaries
- All participating devices must be explicitly approved by **their hub** once, with existing certificate pinning and persistent cryptographic trust. A mere LAN broadcast cannot authorize input, impersonate a member or grant a lease.
- The shared environment does not eliminate authenticated device-to-hub transport. It eliminates separate user-facing **device-pair** sessions and duplicate Android connections.
- Windows→Android remote gestures/typing still require Android Accessibility and in-app permission. Android's physical touch may revoke remote control. Android→Windows phone typing uses the on-screen keyboard input field or hardware keyboard and does not require Accessibility.
- The current Windows hub supports **up to eight simultaneously authenticated incoming peers**. This is a documented bound, not a claim of unlimited connections.
- The hub must be reachable on the same permitted private LAN. This revision is one hub with attached members, not a mesh of connected hubs across multiple networks. It does not stream screens or provide unrestricted hardware-level Android key injection.

## Verification and release policy
- Windows CI now includes a **three-peer live TLS integration test** for roster visibility, selecting a member through the hub, competing input claims, release, and destination physical-input revocation.
- Android CI covers hub-role discovery, packet allowlists and builds the debug APK.
- Automated tests cannot prove real-world latency, focus/elevated-app behavior, Accessibility text insertion or multi-monitor physical edge crossing. Test with two actual Windows computers and multiple Android phones before releasing.
- Keep the **existing permanent Android signing identity**; never publish debug-signed APKs as production updates.
- This branch is a development preview and must **not** deploy through release auto-updates until end-to-end acceptance passes.
