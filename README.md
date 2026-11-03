# DeskTouch Vision — Windows display-style keyboard and mouse sharing

DeskTouch Vision is a standalone Windows application for sharing one keyboard and mouse between Windows computers over a trusted local network. The **primary screen follows the Windows Display Settings arrangement style**, without separate "PC 1" and "PC 2" technical setup columns.

**Version:** 0.2.5 experimental preview, not yet verified on two real Windows machines. The existing v0.2.0 UI was replaced following feedback that the split PC1/PC2 window was difficult to use.

## v0.2.5 — stable paired Windows computers and mirrored monitor arrangements

This release addresses the situation where the **controlling computer shows the connected PC, but the receiving computer does not**, along with unnecessary connection loss while using native displays.

- Both computers now exchange their **actual Windows extended-monitor layout and computer name after authenticated TLS pairing**. The receiver's *Displays* UI shows its peer, not merely the `Ready to pair` label.
- Move the connected screen group on either computer; the matching **left/right/top/bottom placement is mirrored to the other side**. Only the computer that initiated sharing forwards keyboard/mouse events in this two-PC preview; showing the peer on both computers does not imply full bidirectional input routing yet.
- Windows `WM_DISPLAYCHANGE` events **no longer destroy TLS pairing**. A spurious notification is ignored; a genuine monitor layout change updates the peer without dropping the connection. Active remote input is returned to the local PC safely if the coordinates change.
- While paired, the receiver keeps the connection alive when its own physical keyboard, touchpad or mouse is used; local physical input **revokes remote input only**, never the peer relationship.
- Mouse movement packets are **coalesced** to the latest coordinates instead of filling the send queue and triggering overload disconnections. Heartbeats run every 1.5 seconds and receiver's silence watchdog is increased to 18 seconds. Actual disconnection reasons are written to `%LOCALAPPDATA%\\DeskTouchVision\\logs\\connection.log`.
- An actual network failure still releases remotely held keys/buttons and may require re-pairing. The application does not bypass Windows Firewall or silently grant a new remote-control connection.

**Install this release on BOTH computers** before testing. An older receiver does not understand the new two-way monitor-layout messages.

The Windows CI integration test now verifies **receiver-side peer visibility**, topology changes in both directions, placement synchronization, high-frequency mouse events without overload, secure TLS/manual-code pairing, and native input priority. These are automated protocol tests on one Windows runner, not proof of reliability over the user's two physical computers.

## v0.2.4 — fix installation-folder lock and stop repeat download loops

**Confirmed on the user’s machine, 2026-11-03:** v0.2.2 repeatedly downloaded and verified the v0.2.3 release but the independent PowerShell updater failed with `Cannot move item because ...\\Programs\\DeskTouchVision is in use`; it then restarted v0.2.2, which downloaded the same release again. This is **not** a connection/discovery or checksum problem.

**Root cause:** the update PowerShell process could inherit a working directory *within* `DeskTouchVision\\DeviceSharing`, preventing Windows from renaming the parent installation directory while the updater itself was running. The v0.2.4 launcher now copies the updater into a unique temporary folder and explicitly sets its working directory **outside** the installation. The installer also resets its current directory to TEMP, serializes install attempts with a Windows named mutex, and retries transient folder locks rather than terminating unrelated processes.

**One-time recovery for older installations:** Versions 0.2.2 and 0.2.3 cannot inherit v0.2.4’s corrected updater until they install successfully. Run the standalone `RECOVER_DESKTOUCH.ps1` on the affected Windows PC from **Downloads**, not from the installed application folder. It downloads the official v0.2.4 ZIP and matching SHA-256 checksum, validates the archive, closes only the installed DeskTouch windows gracefully, waits for old updater hosts to exit, invokes the atomic installer from TEMP and checks the final sharing executable reports **0.2.4.0**. The script never force-kills native input processes, changes Windows keyboard/mouse drivers, or alters display settings.

PowerShell 5.1 commands to get and execute the recovery script:

```powershell
$script = "$env:USERPROFILE\\Downloads\\RECOVER_DESKTOUCH.ps1"
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/surajTech-22/DeskTouch-Vision/main/DeskTouchVision/RECOVER_DESKTOUCH.ps1" -OutFile $script
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script
```

The output is recorded under `%LOCALAPPDATA%\\DeskTouchVision\\logs\\recovery.log`; automatic updater logs are `update-check.log` and `update.log`. No keyboard content, pairing secrets or screen data are collected. If the repair encounters another active updater or a persistent lock, it stops without force-terminating another process, and the installed application is preserved.

The standalone release installer ZIP and manual `INSTALL_DESKTOUCH.cmd` remain valid alternatives. Hardware validation of two-PC input sharing and the first post-repair unattended version upgrade is still pending.

## v0.2.3 — Windows connection fix

**Confirmed defect:** The original Windows TLS receiver used an ephemeral RSA private key. Windows Schannel rejected it with `Authentication failed because the platform does not support ephemeral keys`, so discovery worked but keyboard/mouse pairing could fail. The receiver now imports a temporary certificate into the Windows current-user key store (not machine-wide). Our Windows build test exercises a genuine encrypted TLS connection with the receiver approval flow and manual-code authentication.

**To connect:** Install the **same latest DeskTouch version on both Windows PCs**, keep the app open on both, select **Add a computer**, then approve the prompt on the receiving PC. The keyboard and mouse physically attached to that computer always have priority when used.

If discovery works but secure input still cannot connect, Windows Firewall may allow UDP discovery port **37378** while blocking TCP **37377**. On the receiving PC, allow `DeskTouch.DeviceShare.exe` to accept incoming connections **only for Private networks**, or use the included read-only `CHECK_CONNECTION.ps1` script:

```powershell
# On receiving PC: verify TCP listener and network profile.
powershell -NoProfile -ExecutionPolicy Bypass -File .\CHECK_CONNECTION.ps1

# On controlling PC: test the receiver IP found in the Add dialog.
powershell -NoProfile -ExecutionPolicy Bypass -File .\CHECK_CONNECTION.ps1 -ReceiverIp 192.168.1.20
```

Replace the example IP with the actual receiving PC's IPv4 address. This command does not modify the firewall, and does not read or store keyboard content or pairing secrets. On a failed connection, DeskTouch now distinguishes blocked TCP, TLS, local approval, and monitor data failures and logs sanitized connection stages under `%LOCALAPPDATA%\DeskTouchVision\logs\connection.log`.

**Security:** No automatic firewall exception is silently added; first-time pairing always requires receiver consent or a full locally copied pairing code. The program never bypasses Windows secure desktop/UAC restrictions.

**Status:** CI can test the encrypted protocol on a Windows runner; real two-computer input switching, Windows Firewall configurations and unattended updates still need hardware checks.

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
