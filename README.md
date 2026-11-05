# DeskTouch Vision — Windows-only keyboard and mouse sharing

DeskTouch Vision shares a Windows keyboard and mouse across multiple **Windows computers** on a trusted private Wi-Fi or Ethernet network. Android, BLE HID and Android Debug Bridge (ADB) support were removed in v0.3.24.

## How it works

Each Windows computer keeps its existing real monitors and native keyboard/mouse. DeskTouch adds other authenticated Windows computers to the Displays arrangement. Drag their groups beside your local screens and cross the outside edge to transfer input. Handoff works in both directions; your physical local input always takes priority. Press **Ctrl+Alt+Shift+F12** for emergency return.

A shared environment uses an encrypted TLS connection with explicit user approval. Trust a known Windows device by its persistent cryptographic fingerprint and token; reconnect finds its current IP dynamically and does not rely on a saved IP address. The app remains listening and sharing while hidden in the Windows tray.

## Install and pair

1. Download `DeskTouchVision-win-x64.zip` from https://github.com/surajTech-22/DeskTouch-Vision/releases/latest on each Windows PC.
2. Extract and run `INSTALL_DESKTOUCH.cmd`. Start DeskTouch on both PCs and keep it open or in the notification tray.
3. On the receiving PC click **Copy pairing code**. On the controlling PC select **Add devices**, enter the code and approve. You may also choose a discoverable Windows PC on the same LAN.
4. Drag its screen group next to an actual local monitor. Moving the mouse outward across that edge requests secure control.
5. To reconnect automatically choose **Trust device** and approve on the other PC. Manage or revoke these permissions through **Trusted devices**.
6. **Disconnect** ends the connection; **Exit DeskTouch** in the notification area fully shuts down sharing.

## Diagnostics

Use `DeskTouchVision/CHECK_DESKTOUCH.ps1` and `CHECK_CONNECTION.ps1` to inspect version, network connection, and updater status. Logs are written under `%LOCALAPPDATA%\DeskTouchVision\logs\connection.log`. Windows Firewall must allow DeskTouch on trusted private LANs.

## Windows development

The launcher is `DeskTouchVision/DeskTouchVision.csproj`; Windows sharing is `DeskTouchVision/DeviceSharing/DeviceSharing.csproj`.

```powershell
dotnet run --project DeskTouchVision/DeviceSharing.PolicyTests/DeviceSharing.PolicyTests.csproj -c Release
dotnet publish DeskTouchVision/DeviceSharing/DeviceSharing.csproj -c Release -r win-x64 --self-contained true
```

CI checks authenticated multi-PC TLS, physical-input priority, two-way leases, real Windows monitor edges, shared placement, tray survival and updater integrity. Hardware-level multi-PC testing remains necessary after installation.
