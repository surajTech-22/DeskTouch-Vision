# DeskTouch Vision — Windows & Android keyboard and mouse sharing

DeskTouch Vision shares your **keyboard and mouse across Windows PCs and Android phones** on a trusted private Wi-Fi network — with no Developer Options, USB cables, or Bluetooth dongles required.

## How it works

Each Windows PC keeps its existing monitors and local input priority, while connected PCs and portrait Android phone screens appear side-by-side in **Displays**.
- **Edge Glide:** Drag a PC or Android phone tile next to any monitor edge and slide your cursor across to control it.
- **Quick Mobile Toggle:** Press **`Ctrl + Alt + M`** to jump straight between PC and your connected Android phone. Press **`Ctrl + Alt + Shift + F12`** anytime for emergency return to Windows.
- **Two-Way Control:** Control Android with your Windows mouse/keyboard (Right Click = Back, Middle Click = Home, full typing & search Enter/Arrows/`Ctrl+A/C/X/V`), or control Windows from the Android touchpad & keyboard.
- **Pause for Banking:** On Android, tap **Pause for Banking** (Quick Settings tile, ongoing notification, or in-app) before opening banking/UPI apps, and tap Resume later.
- **Trusted Wi-Fi Reconnect:** Devices recognize each other by their persistent encrypted TLS fingerprint and reconnect automatically when back on the same Wi-Fi (no remembered IP addresses).

## Install and pair

1. Download `DeskTouchVision-win-x64.zip` from https://github.com/surajTech-22/DeskTouch-Vision/releases/latest on Windows, extract, and run `INSTALL_DESKTOUCH.cmd`.
2. Click **Get Android app** on Windows and scan the QR code with your phone's camera to install `DeskTouchVision-android-preview.apk`.
3. On Android, open DeskTouch Vision, follow the short Accessibility / Restricted Settings setup (Battery: No restrictions for Xiaomi/Redmi), tap **Find devices**, join your PC, and approve on Windows.
4. Click **Trust connected PC** on Android so both devices reconnect automatically over Wi-Fi.

## Diagnostics

Use `DeskTouchVision/CHECK_DESKTOUCH.ps1` and `CHECK_CONNECTION.ps1` to inspect version, network connection, and updater status. Logs are written under `%LOCALAPPDATA%\DeskTouchVision\logs\connection.log`. Windows Firewall must allow DeskTouch on trusted private LANs.

## Windows development

The launcher is `DeskTouchVision/DeskTouchVision.csproj`; Windows sharing is `DeskTouchVision/DeviceSharing/DeviceSharing.csproj`.

```powershell
dotnet run --project DeskTouchVision/DeviceSharing.PolicyTests/DeviceSharing.PolicyTests.csproj -c Release
dotnet publish DeskTouchVision/DeviceSharing/DeviceSharing.csproj -c Release -r win-x64 --self-contained true
```

CI checks authenticated multi-PC TLS, physical-input priority, two-way leases, real Windows monitor edges, shared placement, tray survival and updater integrity. Hardware-level multi-PC testing remains necessary after installation.
