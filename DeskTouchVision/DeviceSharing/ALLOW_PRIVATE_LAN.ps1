#Requires -RunAsAdministrator
# Explicit administrator-approved repair. No Public/Domain network exposure.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision\DeviceSharing\DeskTouch.DeviceShare.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
  throw "DeskTouch device-sharing app was not found at $exe"
}
$rules = @(
  @{ Name='DeskTouchVision.Private.TCP.37377'; Port=37377; Protocol='TCP' },
  @{ Name='DeskTouchVision.Private.UDP.37378'; Port=37378; Protocol='UDP' }
)
foreach ($r in $rules) {
  $existing = Get-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue
  if ($existing) {
    # Only replace named rules owned by DeskTouch. Never modify unrelated rules.
    Remove-NetFirewallRule -Name $r.Name -ErrorAction Stop
  }
  New-NetFirewallRule -Name $r.Name -DisplayName ('DeskTouch Vision private LAN ' + $r.Protocol) `
    -Group 'DeskTouch Vision' -Enabled True -Direction Inbound -Action Allow `
    -Program $exe -Profile Private -Protocol $r.Protocol -LocalPort $r.Port `
    -RemoteAddress LocalSubnet -ErrorAction Stop | Out-Null
  Write-Host ("Allowed DeskTouch on Private LocalSubnet: {0} {1}" -f $r.Protocol,$r.Port) -ForegroundColor Green
}
$profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
if (-not ($profiles | Where-Object NetworkCategory -eq 'Private')) {
  Write-Warning 'Your active network is not Private. Set your trusted Wi-Fi/Ethernet profile to Private in Windows Settings; these rules intentionally do not open Public networks.'
}
$listener = @(Get-NetTCPConnection -State Listen -LocalPort 37377 -ErrorAction SilentlyContinue)
if ($listener.Count -gt 0) {
  Write-Host 'TCP 37377 is listening. Reopen the Android app and scan again.' -ForegroundColor Green
} else {
  Write-Warning 'Firewall rules are installed but no app listens on TCP 37377. Open DeskTouch Displays or its tray app on this PC.'
}
Write-Host 'Android/PC discovery is UDP 37378; remote control is TCP 37377. Rules allow LocalSubnet on Private networks only.'
Write-Host 'Press Enter to close.' -ForegroundColor Cyan
[void][Console]::ReadLine()
