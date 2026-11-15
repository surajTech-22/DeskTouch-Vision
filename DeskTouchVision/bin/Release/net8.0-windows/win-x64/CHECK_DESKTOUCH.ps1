# Read-only local diagnostics. Does NOT capture typed text, pairing secrets or IP passwords.
$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
$exe = Join-Path $root 'DeskTouchVision.exe'
$sharing = Join-Path $root 'DeviceSharing\DeskTouch.DeviceShare.exe'

Write-Host 'DESKTOUCH — INSTALLED WINDOWS FILES' -ForegroundColor Cyan
foreach ($path in @($exe, $sharing, (Join-Path $root 'Update-DeskTouch.ps1'))) {
  if (Test-Path -LiteralPath $path) {
    $file = Get-Item -LiteralPath $path
    Write-Host ("{0} | version={1} | modified={2:u}" -f $path, $file.VersionInfo.FileVersion, $file.LastWriteTime)
  }
  else { Write-Host "MISSING: $path" -ForegroundColor Yellow }
}
Write-Host "`nDESKTOUCH — PROCESSES" -ForegroundColor Cyan
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe', 'powershell.exe') -and
    ($_.Name -ne 'powershell.exe' -or $_.CommandLine -match 'Update-DeskTouch.ps1') } |
  Select-Object ProcessId, Name, ExecutablePath, CreationDate |
  Format-Table -AutoSize

foreach ($name in @('update-check.log','update.log','install.log')) {
  $file = Join-Path $logs $name
  Write-Host "`nDESKTOUCH — $name" -ForegroundColor Cyan
  if (Test-Path -LiteralPath $file) {
    Get-Content -LiteralPath $file -Tail 35
  } else { Write-Host '(not created yet)' -ForegroundColor DarkGray }
}
Write-Host "`nOnly local executable paths, versions and updater logs were displayed." -ForegroundColor Gray
