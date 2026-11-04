# DeskTouch Vision v0.3.18 — Bluetooth HID eligibility inside the connected app

## What changed
- Open **Bluetooth check** in the Windows Displays window or tray menu to inspect all connected Windows PCs together. No separate PowerShell script is needed.
- Every authenticated Windows peer checks its own default Bluetooth adapter using Windows' official API and sends only capability data over the existing DeskTouch TLS environment connection.
- Results indicate Bluetooth LE support, peripheral/HID-advertising capability, and whether Bluetooth is enabled. The check identifies a preferred eligible candidate; an existing eligible candidate remains preferred to avoid needless changes.
- A diagnostic request is triggered after the Windows app adopts its secure hub connection and whenever **Recheck connected PCs** is pressed. The scan runs off the input/heartbeat loop.
- No keyboard content, Bluetooth MAC addresses, pairing secrets, device IDs, or raw input events are included in the transmitted report.

## Scope and limitations
- This is a **read-only compatibility diagnostic**, not Bluetooth HID keyboard/mouse emulation.
- HID advertising, Android Bluetooth pairing, Android-observed RSSI and automatic selection of the strongest *actual* Bluetooth connection still require testing on the real PCs and Android phone.
- Existing Windows keyboard/mouse handoff, Android accessibility input, device positions, and trust/auto-update protocols are unchanged.

## Validation
- Windows KVM preview build and input priority policy checks.
- Authenticated multi-peer TLS report collection plus existing two-way Windows input authority and update safety tests.
- Android build/tests and Android emulator shared-environment smoke tests (no Android APK source changes).
