# DeskTouch Vision v0.3.23 — Native Android HID over Wi-Fi, no Bluetooth

- Replace Bluetooth-only native HID startup with an authorized Android UHID connection over ADB/Wi-Fi. Windows captures the same physical keyboard and mouse via the existing SourceCapture, then Android's Linux /dev/uhid registers a virtual physical keyboard and mouse with the native Android input stack.
- No Bluetooth radio, USB HID accessory, root, Accessibility pointer overlay, Android Activity, separate Android app or fake USB device. The installed DeskTouch APK contains the shell-only UHID bridge, launched as Android's authorized ADB shell user (ordinary application UID cannot access /dev/uhid).
- Reuse the existing screen-edge handoff and emergency return Ctrl+Alt+Shift+F12. Input authority activates only after Android successfully opens both kernel UHID devices and acknowledges its first mouse report.
- Native keyboard HID reports, relative mouse movement, buttons and vertical/horizontal scrolling are sent over the same ADB stream. Input goes through the Android kernel rather than Accessibility tap or IME text injection.
- Opt-in persists in the Windows tray; reconnects after transient ADB link loss; local input is restored if Android disconnects. The existing Android app can remain closed because the shell bridge runs under adb.
- **Initial setup:** on Android enable Developer options > Wireless debugging, pair the PC once using official Android SDK platform-tools adb.exe (`adb pair <phone-address>:<pairing-port>`, then `adb connect <phone-address>:<connection-port>`). Install the matching permanently signed DeskTouch Android v0.3.23 APK. DeskTouch locates adb under Android Studio SDK or PATH and uses a single authorized device only. Wireless debugging may need enabling again after phone restarts.
- Bluetooth v0.3.22 remains in source for hardware-capable systems but is no longer a required or selected path for this no-Bluetooth mode. No layout redesign.
- Android UHID permission and wireless debugging policies vary by device/OEM and still need physical Android testing; GitHub CI verifies code, protocol structures and emulator input regressions but does not prove a specific phone permits /dev/uhid.
- UHID implementation informed by Apache-2.0 open-source scrcpy: https://github.com/Genymobile/scrcpy. No scrcpy dependency or separate executable is installed.

