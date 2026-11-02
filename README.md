# DeskTouch Vision — Offline Visual Keyboard & Mouse (Preview)

This is the **official standalone repository** for the experimental DeskTouch Vision Windows application. It is separate from DeskSpace.

## Verified build status

GitHub Actions compiled and packaged the app on a Windows runner:
https://github.com/surajTech-22/Desk-space/actions/runs/41448407511

The ZIP is a Windows x64 **preview installer**, not proof that webcam vision can detect physical key presses or mouse clicks. No live camera-on-hardware functional testing has been performed.

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

1. Open the successful GitHub Actions run linked above.
2. Download artifact `DeskTouchVision-Installer-win-x64`.
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
- [ ] First signed/trusted release published and updater tested end-to-end.
- [ ] Camera-view geometry feasibility test on real laptop.
- [ ] Offline model for keyboard and keycap layout recognition.
- [ ] Accurate finger-contact inference, including two-handed typing and false-positive suppression.
- [ ] Mouse movement, click-button and wheel motion recognition.
- [ ] Explicit opt-in OS input emulation with keyboard emergency stop.
- [ ] Benchmarked accuracy, latency, CPU use, darkness and occlusion handling.

The older **absolute physical touchpad mapping** experiment remains ON HOLD, separate from this project.
