# DeskTouch Vision — Windows display-style keyboard and mouse sharing

DeskTouch Vision is a standalone Windows application for sharing one keyboard and mouse between Windows computers over a trusted local network. The **primary screen follows the Windows Display Settings arrangement style**, without separate "PC 1" and "PC 2" technical setup columns.

**Version:** 0.2.1 experimental preview, not yet verified on two real Windows machines. The existing v0.2.0 UI was replaced following feedback that the split PC1/PC2 window was difficult to use.

## v0.2.2 — repair old camera screen and misleading update results

If the window title still says **"DeskTouch Vision 0.2.0 — offline Windows sharing preview"**, the older camera-calibration application is still running: it is **not** the updated Windows Displays interface. A dialog saying "No newer published release" in v0.2.0 can be inaccurate: that older code returns the same result when an automatic update is already downloading or has a hidden error.

**Reliable one-time repair:**

1. Get the latest official `DeskTouchVision-win-x64.zip` from [GitHub Releases](https://github.com/surajTech-22/DeskTouch-Vision/releases/latest).
2. Download the matching `.zip.sha256` and verify the ZIP checksum if possible.
3. Extract the ZIP into a new temporary directory, close the old "DeskTouch updates" message, and run `INSTALL_DESKTOUCH.cmd`.
4. The script stages the complete application under `%LOCALAPPDATA%\\Programs\\DeskTouchVision`, closes only matching installed DeskTouch processes gracefully, retains settings and can roll back a failed replacement. It never disables local input hardware.
5. Launch the existing **DeskTouch Vision** desktop shortcut again. The title should be **"DeskTouch Vision — Displays"**, with a Windows-style monitor layout rather than a camera preview.

**Read-only diagnostic:** `CHECK_DESKTOUCH.ps1` is included beside the installer. It prints installed file versions, process paths and the last updater log messages without accessing pairing secrets or typed content. Application logs: `%LOCALAPPDATA%\\DeskTouchVision\\logs\\update-check.log`, `update.log` and `install.log`.

**Automatic updates:** installed v0.2.2 uses its compiled assembly version to compare releases, waits for any startup download before presenting a manual status, shows failures rather than claiming no release, downloads to a unique temporary folder, verifies the official release SHA-256 file, launches an independent updater, closes both app processes gracefully and restarts. Older v0.2.0/v0.2.1 clients are not retroactively fixed; use the manual repair once if they cannot upgrade.

## How it should work

1. Install DeskTouch Vision on both authorized Windows computers. Open the app on both computers. Each shows its own numbered Windows monitors automatically. The native Windows **Extend displays** arrangement remains authoritative and untouched.
2. On the computer with the keyboard/mouse you want to share, select **Add a computer**. Nearby DeskTouch PCs are discovered over the LAN (when private-network broadcast is permitted).
3. Choose the target computer. The receiving Windows computer asks **Allow connection?**. Approving pairs a certificate-pinned, encrypted channel. If LAN discovery fails, use **Copy pairing code** on the receiver and paste it into the Add dialog instead.
4. After connecting, the remote computer appears as another set of numbered displays in the same arrangement. Drag the remote group left, right, up or down. This changes **DeskTouch's cross-computer switching boundary**, not the positions of monitors configured in Windows.
5. Move the mouse across the configured outside edge. DeskTouch requests keyboard/mouse control only after the receiver's physical devices have been idle for two seconds. Internal native Windows monitor seams continue to work normally.
6. The receiver's **own built-in or attached keyboard, touchpad and mouse always override** shared input immediately. A disconnected but idle device does not block sharing. Remote access requires a new deliberate edge crossing after local activity ceases.
7. Move across the opposite external edge, press **Ctrl+Alt+Shift+F12**, disconnect, or close DeskTouch to restore control to the source computer.

**Important distinction:** Windows' native Display Settings controls monitors connected to a *single* PC. DeskTouch presents other *computers* as additional draggable groups in its own interface; it does not create fake Windows monitors, change registry display settings, disable native keyboard/mouse hardware, stream desktops, or alter the Windows driver stack.

## Install on Windows

Use the latest release: https://github.com/surajTech-22/DeskTouch-Vision/releases/latest

- Download `DeskTouchVision-win-x64.zip` and the accompanying `.sha256` file.
- Extract the ZIP and run `INSTALL_DESKTOUCH.cmd`. The installation is per-user and does not require .NET on the computer.
- Desktop shortcut `DeskTouch Vision` now opens the **Displays** interface directly. The camera window will no longer open behind it.
- If Windows Firewall prompts, permit the program only on **Private** networks. Discovery uses UDP 37378, and the encrypted pairing/input connection uses TCP 37377.
- **Windows display settings** opens Microsoft's native display configuration screen for local monitor reordering and resolution changes.
- All runtime gesture/input processing is local. Only optional update checks contact GitHub/ntfy.
- Release-event auto-update checks GitHub's newest release, verifies a SHA-256 checksum, gracefully exits the input sharing app, replaces the package with rollback protection and restarts. Its end-to-end unattended operation on the user's PC still needs field testing.

### Manual pairing fallback

On the receiving PC, click **Copy pairing code**. Paste this complete code in the lower section of **Add a computer** on the controlling PC. The 32-digit secret is never advertised during automatic LAN discovery. Treat the code like a temporary access credential.

## Safety and limitations

- **Preview:** builds and automated input-priority logic tests do not establish stable real hardware operation. Verify with a mouse and keyboard physically attached to each PC before relying on cross-computer input.
- Initial release supports **two Windows PCs per active connection**. Multiple-PC mesh/topology, Android input sharing, shared clipboard and persistent remembered trust are later milestones.
- Native Windows multi-monitor configurations are recognized but not changed. The receiver must remain in a logged-in desktop session; UAC secure desktop, Windows lock screens and elevated apps may refuse synthetic input.
- LAN discovery can be blocked by Windows firewalls, client isolation, VPNs or different subnets. Manual code pairing remains available.
- No keyboard content is logged. The shared source computer's own keyboard is intentionally forwarded while remote control is active; use a return edge or emergency shortcut to reclaim it.
- The offline camera-disconnected-keyboard feature and the touchpad-to-touchscreen experiment are **on hold**. The prior camera calibration preview can be opened explicitly with `DeskTouchVision.exe --camera-experiment`; it is not the main experience.

## Developer notes

The repository contains the main `DeskTouchVision` launcher, separate `DeskTouch.DeviceShare` Windows module, release-event updater, Windows installer scripts, monitor mapping and LAN authentication, and deterministic priority/topology regression tests.

Build both applications on a Windows x64 build machine:

```powershell
dotnet run --project DeskTouchVision/DeviceSharing.PolicyTests/DeviceSharing.PolicyTests.csproj -c Release
dotnet publish DeskTouchVision/DeskTouchVision.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish DeskTouchVision/DeviceSharing/DeviceSharing.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Do not claim actual two-PC functional acceptance without testing local native preemption, USB/Bluetooth devices, split-monitor seams, all four edge directions, wired/Wi-Fi disconnect, recovery from app exit and delayed updater restarts on real hardware.
