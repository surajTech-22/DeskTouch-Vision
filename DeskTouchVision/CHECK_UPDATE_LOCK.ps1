# Read-only update-lock preflight. No process termination, network,
# keyboard inspection, credential access or Windows setting changes.
$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
Write-Host 'DESKTOUCH INSTALLED VERSION' -ForegroundColor Cyan
$exe = Join-Path $root 'DeviceSharing\DeskTouch.DeviceShare.exe'
if (Test-Path -LiteralPath $exe) {
  Write-Host (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
} else { Write-Warning 'Sharing executable missing.' }
Write-Host ''
Write-Host 'ACTIVE UPDATE LOCK' -ForegroundColor Cyan
$mutex = [System.Threading.Mutex]::new($false, 'Local\DeskTouchVision.Installer')
$ownsLock = $false
try {
  try { $ownsLock = $mutex.WaitOne(0) }
  catch [System.Threading.AbandonedMutexException] {
    $ownsLock = $true
    Write-Warning 'An earlier installer exited without releasing its lock.'
  }
  if ($ownsLock) {
    Write-Host 'FREE -- no updater currently owns the install mutex.' -ForegroundColor Green
  } else {
    Write-Warning 'BUSY -- another updater thread currently owns the installation mutex.'
  }
}
finally {
  if ($ownsLock) { try { $mutex.ReleaseMutex() } catch { } }
  $mutex.Dispose()
}
Write-Host ''
Write-Host 'POTENTIAL UPDATER HOST PROCESSES' -ForegroundColor Cyan
$owners = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -match '(?i)Update-DeskTouch[.]ps1' } |
  Select-Object ProcessId, CreationDate, ExecutablePath)
if ($owners.Count -gt 0) {
  $owners | Format-Table -AutoSize
} else { Write-Host 'No active Update-DeskTouch.ps1 process detected.' }
foreach ($name in @('update-check.log','update.log')) {
  Write-Host ("\n" + $name) -ForegroundColor Cyan
  $path = Join-Path $logs $name
  if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Tail 16 }
  else { Write-Host '(not present)' }
}
Write-Host ''
Write-Host 'Diagnostic only. No processes or devices were modified.' -ForegroundColor Gray
