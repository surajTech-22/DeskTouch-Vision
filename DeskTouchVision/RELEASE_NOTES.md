# DeskTouch Vision v0.3.11 — development preview (not released)

## Linked Windows group and mobile connection
- A linked network appears as **one group card** in Android, rather than separate Connect buttons.
- **Connect all** initiates individually authenticated TLS pairing to every available Windows PC in that group. First-time approval is still required on **each** Windows PC. Subsequent trusted reconnects retain every approved member, subject to the 8-peer receiver limit. No IP is stored as a trusted identity.
- All PCs and multiple Android phones may remain connected simultaneously. Only **one input source may control each destination** at a time; keystrokes are never broadcast to every PC.
- Mobile offers an active Windows-screen selector. Swiping past the left/right edge of an active PC screen can switch to an already connected adjacent group member with a new input claim and applied-cursor acknowledgement.
- This is **one logical network** in the UI backed by independently authenticated direct TLS connections, not a transparent Windows-to-Windows gateway relay. Android-to-Android remote control is not implemented.

## Keyboard and mouse
- Android to Windows input is restored behind an explicit **Take control** button and the existing physical-input-priority two-phase lease. The Android touchpad, physical USB/Bluetooth keyboard/mouse, and on-screen keyboard typing field can send input to the selected Windows PC.
- Committed Android IME text is sent as a bounded, authenticated Unicode text packet. Windows injects it only for the currently granted input lease. No text contents are retained in diagnostic logs. Text entry requires an ordinary editable program with focus; elevated Windows apps may reject input from a lower-privileged process.
- Mouse movement coalesces to the newest position instead of queuing stale moves; Android's TLS socket disables Nagle packet delays. Mobile touchpad sensitivity is adjustable.
- Windows raw mouse capture now scales remote movement according to the PC's existing native pointer-speed setting. The slowest physical deltas remain observable, and extreme raw deltas are bounded; no new pointer-settings panel is introduced.
- Windows reverse-input capture is **bound to the original peer session**. On selected-target changes, the prior reverse lease is reset on that original connection, preventing stale key/release packets from targeting the new screen.
- Windows → Android typing remains an opt-in Android Accessibility feature limited to accessible non-password editable fields. It does not provide an unrestricted Android system keyboard driver.

## Test and release boundary
- Android APK unit/build checks and Windows .NET input, discovery, priority and packaging CI run on the development branch.
- CI cannot reproduce two actual PCs' display edges, Android IME behavior, Bluetooth peripherals or Windows elevated-app focus. Real three-device acceptance is required before calling v0.3.11 a verified release.
- The private Android signing identity from v0.3.8 remains unchanged; never replace or commit the signing keystore.
