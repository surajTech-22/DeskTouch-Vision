# DeskTouch Vision — Offline Visual Keyboard & Mouse (Preview)

This is the **official standalone repository** for the experimental DeskTouch Vision Windows application. It is separate from DeskSpace.

## Verified build and installation

- The **first independent Windows build and release succeeded**: https://github.com/surajTech-22/DeskTouch-Vision/actions/runs/41449397062
- Official preview release: https://github.com/surajTech-22/DeskTouch-Vision/releases/tag/v0.1.0
- Installer: https://github.com/surajTech-22/DeskTouch-Vision/releases/download/v0.1.0/DeskTouchVision-win-x64.zip
- Matching checksum: https://github.com/surajTech-22/DeskTouch-Vision/releases/download/v0.1.0/DeskTouchVision-win-x64.zip.sha256
- Release ZIP SHA-256: `055ebbdc99cee88d9a4e24d4af2e4c46e9e68ca913f8fd1571f20ae55f490036`

The ZIP is a Windows x64 preview installer. CI verified compilation, packaging and release publishing, **not real-world recognition accuracy or automatic update installation on a laptop**.

## Windows keyboard and mouse sharing — v0.2.0 experimental preview

**The Windows-to-Windows feature is opt-in and not yet validated on two physical PCs.** It does not require camera recognition. Install the same DeskTouch Vision app on two authorized Windows computers connected to a trusted local network.

1. On PC 2, open **Windows device sharing** in DeskTouch, then click **Start receiver on this PC**. Allow inbound access on trusted **Private** networks if Windows Firewall asks. The UI displays the receiver's local IPv4 address, certificate fingerprint and temporary pairing secret.
2. On PC 1, open **Windows device sharing**; enter the IPv4 address, full certificate fingerprint and pairing secret from PC 2. Check the authorization box, then click **Connect to receiver** and **Arm right-edge switching**.
3. Move the cursor to the right **external edge** of PC 1's Windows extended desktop. The receiver explicitly grants control only when its physical input devices have been idle for at least 2 seconds. Native monitor seams remain normal Windows cursor movement.
4. Type or move the connected input devices on PC 1 to control PC 2. Move past the left edge on PC 2 or press **Ctrl+Alt+Shift+F12** on PC 1 to regain local control.
5. If anyone uses PC 2's own built-in or attached keyboard, touchpad, or mouse, its native input **takes priority immediately** and the sender returns to local control. A disconnected network link also releases remotely held buttons and keys after the heartbeat timeout.
6. To stop sharing, click **Disconnect / return local** or close the Windows Device Sharing window on either computer. DeskTouch does not disable native drivers.

**Limitations:** Only two Windows computers and a right-edge handoff are implemented in this preview; configurable multi-PC layouts and Android are future work. Windows secure desktop/UAC and elevated apps may reject injected input. Keyboard-layout differences and high-DPI multi-monitor behavior require real-hardware validation. Do not rely on experimental remote control as the only way to access a computer. The old camera-only disconnected keyboard/mouse feature and touchpad conversion remain on hold.

**Standalone package:** `DeskTouchVision-win-x64.zip` includes `app/DeskTouchVision.exe` and `app/DeviceSharing/DeskTouch.DeviceShare.exe`. The installer is `INSTALL_DESKTOUCH.cmd`. After installing, the optional release-event updater obtains subsequent tagged GitHub releases, verifies the SHA-256 checksum, closes both app processes gracefully, and restarts. It never disables native keyboard or mouse drivers. Unattended updates and input handoff must still be verified on two real PCs.

## Critical feasibility requirements

The laptop's built-in webcam **must be able to see** the physical keyboard, mouse and fingers during use. A standard front-facing laptop webcam usually looks toward the user, not the desk, and cannot see behind/below itself. Software cannot infer precise actions outside the camera view. Full disconnected-device keyboard/mouse emulation is a research goal, not a currently working capability.

## Currently implemented

- Offline built-in camera preview (no cloud inference and no video recording).
- Manual calibration of keyboard and mouse rectangles in camera view.
- Simple approximate 3-row QWERTY visual guide, and basic change-from-idle movement visualization, NOT true automatic key identification or keystroke detection.
- Local calibration file in `%LOCALAPPDATA%\DeskTouchVision\camera-calibration.json`.
- Privacy Stop button; no simulated keypresses or mouse events.
- Per-user Windows installation via `INSTALL_DESKTOUCH.cmd` and a self-contained x64 .NET 8 packaged executable.
- Optional auto-update scaffold: check the official separate GitHub repository's latest release on app launch, listen for release events (ntfy SSE), download asset + SHA-256, close the app, install via independent PowerShell updater, restart. **This is staged, not active until the separate repo and releases are configured and published.**
- No hourly polling; SSE reconnects after disconnection. Network only for optional updates.

## Install preview (Windows x64)

1. Open the official v0.1.0 release linked above.
2. Download asset `DeskTouchVision-win-x64.zip`.
3. Extract the ZIP.
4. Double-click `INSTALL_DESKTOUCH.cmd`.
5. It installs under `%LOCALAPPDATA%\Programs\DeskTouchVision` and creates a desktop shortcut.
6. In the app, press **Start camera**, allow Windows webcam access, then **Set keyboard area** / **Set mouse area**.
7. Camera preview and motion overlays will work only if the camera sees those objects.

You can also build locally:
```powershell
dotnet publish DeskTouchVision/DeskTouchVision.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Official release and updates

Windows packages are built by the repository's own GitHub Actions workflow. The first preview release is generated when the app is built on main, with version `v0.1.0` and SHA-256 checksum; later releases need a version bump and successful Windows build. Installed apps look for releases from this repository and accept release event notifications; end-to-end update operation remains unverified until a subsequent release.

## Implementation milestones

- [x] Standalone build + installer packaging CI.
- [x] Preview camera and manual calibration.
- [x] Offline motion visualization.
- [x] Optional event-based update infrastructure, staged.
- [x] Dedicated repository created and project migrated.
- [ ] End-to-end v0.1.0→v0.2.0 updater validation on a physical Windows installation; preview artifacts are not code signed.
- [ ] Camera-view geometry feasibility test on real laptop.
- [ ] Offline model for keyboard and keycap layout recognition.
- [ ] Accurate finger-contact inference, including two-handed typing and false-positive suppression.
- [ ] Mouse movement, click-button and wheel motion recognition.
- [ ] Explicit opt-in OS input emulation with keyboard emergency stop.
- [ ] Benchmarked accuracy, latency, CPU use, darkness and occlusion handling.

The older **absolute physical touchpad mapping** experiment remains ON HOLD, separate from this project.
