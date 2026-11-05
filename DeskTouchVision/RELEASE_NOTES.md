# DeskTouch Vision v0.3.22 — Native Android Bluetooth HID

- Windows now advertises a standard encrypted Bluetooth LE HID-over-GATT keyboard and mouse (GATT service 0x1812, keyboard/mouse report descriptors). Android pairs it through native Bluetooth settings; native keyboard/mouse input requires no Accessibility injection or open Android app.
- Existing Windows physical mouse/keyboard hooks, native priority, edge handoff, and emergency Ctrl+Alt+Shift+F12 remain in place. HID is opt-in via the existing Bluetooth action; after the user pairs one Android phone, native input routing arms automatically.
- Native HID advertising continues with the Windows application hidden in the tray and restarts after Windows sign-in only when the user previously opted in. Stop HID in the existing action to revoke it.
- Use a single paired subscribed HID host only, fail closed without peripheral support, report connection loss, and restore Windows input rather than forwarding to another peer.
- Keyboard and mouse HID reports are native and relative; no fake Bluetooth software adapter, new Android app, root, network HID impersonation, or second input-hook owner.
- Requires Windows Bluetooth LE peripheral role and Android HID-over-GATT support. Actual pairing, system cursor and screen wake behavior depend on the radio, driver and Android device; GitHub CI cannot test real Bluetooth hardware.
- MIT BLE HID core incorporated from https://github.com/abhishek-raj/windows-ble-hid with copyright and license retained in DeviceSharing/NativeHid/LICENSE.

