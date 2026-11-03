param(
  [string]$ReceiverIp
)
$ErrorActionPreference = 'Continue'
Write-Host 'DESKTOUCH VISION -- LOCAL CONNECTION DIAGNOSTIC' -ForegroundColor Cyan
$exe = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision\DeviceSharing\DeskTouch.DeviceShare.exe'
if (Test-Path -LiteralPath $exe) {
  $file = Get-Item -LiteralPath $exe
  Write-Host ("Installed sharing app: {0}, version: {1}" -f $exe, $file.VersionInfo.FileVersion)
} else {
  Write-Warning 'DeskTouch sharing application is missing or not installed in its expected location.'
}
Write-Host "`nNetwork profiles:" -ForegroundColor Cyan
Get-NetConnectionProfile | Select-Object Name, InterfaceAlias, NetworkCategory |
  Format-Table -AutoSize

Write-Host "`nLocal TCP receiver port 37377:" -ForegroundColor Cyan
$listeners = @(Get-NetTCPConnection -LocalPort 37377 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -eq 0) {
  Write-Warning 'No TCP listener at port 37377. Keep DeskTouch Displays open on the receiving PC.'
} else {
  $listeners | Select-Object LocalAddress, LocalPort, State, OwningProcess |
    Format-Table -AutoSize
}
Write-Host "`nLocal UDP discovery port 37378:" -ForegroundColor Cyan
Get-NetUDPEndpoint -LocalPort 37378 -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize

if (-not [string]::IsNullOrWhiteSpace($ReceiverIp)) {
  $address = $null
  if (-not ([System.Net.IPAddress]::TryParse($ReceiverIp, [ref]$address)) -or
      $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    Write-Warning "Invalid receiver IPv4: $ReceiverIp"
  } else {
    Write-Host "`nTesting secure input port on $ReceiverIp ..." -ForegroundColor Cyan
    $result = Test-NetConnection -ComputerName $ReceiverIp -Port 37377 -InformationLevel Detailed -WarningAction SilentlyContinue
    Write-Host ("TCP port 37377 reachable: {0}" -f $result.TcpTestSucceeded)
    if (-not $result.TcpTestSucceeded) {
      Write-Warning 'Discovery (UDP) can succeed while Windows Firewall blocks TCP 37377. Allow DeskTouch.DeviceShare.exe inbound on PRIVATE networks on the receiving PC only.'
    }
  }
}
Write-Host "`nNo keyboard content, pairing code, or certificate private keys were read." -ForegroundColor Gray
