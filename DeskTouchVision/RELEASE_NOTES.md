# DeskTouch Vision v0.3.21 — Android smoother pointer, accurate taps and connection resilience

## Changes
- Fix phone touch-coordinate offsets by using the same full physical display size in the Android foreground pairing dialog, automatic background reconnect, pointer overlay, and Accessibility gesture coordinates; do not confuse content-area DisplayMetrics with full screen pixels.
- Publish corrected Android display topology over the existing authenticated TLS session when the phone rotates; maintain the existing user-authorized connection and shared group routing.
- Make the compact fallback pointer overlay follow an invisible viewport. It repositions the Android WindowManager surface only when the pointer nears its boundary, instead of making an expensive cross-process window relayout on virtually every mouse move.
- Use an exact screen-coordinate Accessibility tap first; fall back to accessibility node-click if an OEM rejects gesture dispatch. Native Android touch still takes priority.
- Keep CPU and low-latency Wi-Fi resources active only during an explicitly authorized Windows-to-Android control lease; release them on physical phone touch, return-local, disconnect or permission revocation.
- Request Android keep-screen-on for the non-touchable pointer overlay while Windows owns the input lease; OEM Android power policy may still override it.
- Enable TCP keepalives and increase Android socket read grace from 22 to 60 seconds and Windows per-peer idle heartbeat grace from 18 to 50 seconds to reduce false timeouts during screen/Wi-Fi scheduling transitions. Heartbeats and trusted reconnect remain enabled.
- Keep the existing Android package name, permanent release signing identity, keyboard/IME, phone display placement and bidirectional Windows native device controls. No new app, Bluetooth spoofing or accessibility permission bypass.

## Validation
- Android JVM overlay follow-window boundary/no-relayout regression tests, signed-APK production manifest checks and Android emulator gesture/keyboard acceptance.
- Windows multi-device encrypted TLS/authority tests, heartbeat grace assertion, installer/tray/update tests, plus coordinated Windows and Android package CI.
- Physical Xiaomi/Android testing is still required for actual Wi-Fi jitter, display overlay restrictions, touch accuracy in third-party apps and screen sleep behavior. Keeping the screen on consumes battery; the app releases low-latency resources when remote control ends.
