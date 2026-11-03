param(
  [Parameter(Mandatory=$true)][string]$Package,
  [Parameter(Mandatory=$true)][string]$Target,
  [Parameter(Mandatory=$true)][int]$CurrentPid,
  [Parameter(Mandatory=$true)][string]$ExpectedHash
)
$ErrorActionPreference = 'Stop'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs 'update.log'
function Log([string]$text) { ("{0:u} {1}" -f (Get-Date), $text) | Out-File $log -Append -Encoding utf8 }

function Stop-InstalledApplications([string]$installDir) {
  foreach ($exeName in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe')) {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction Stop)
    foreach ($info in $processes) {
      $expected = if ($exeName -eq 'DeskTouchVision.exe') {
        Join-Path $installDir $exeName
      } else {
        Join-Path $installDir "DeviceSharing\$exeName"
      }
      if (-not [string]::Equals($info.ExecutablePath, $expected, [StringComparison]::OrdinalIgnoreCase)) { continue }
      $p = Get-Process -Id $info.ProcessId -ErrorAction SilentlyContinue
      if (-not $p) { continue }
      Log "Requesting graceful close: $exeName PID=$($p.Id)"
      [void]$p.CloseMainWindow()
      if (-not $p.WaitForExit(12000)) {
        throw "Active $exeName did not close safely; update deferred to avoid leaving input hooks/held keys behind."
      }
    }
  }
}

$stage = $null
$backup = $null
$movedOriginal = $false
$activated = $false
try {
  $targetFull = [IO.Path]::GetFullPath($Target).TrimEnd('\')
  $expectedTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision')).TrimEnd('\')
  if (-not [string]::Equals($targetFull, $expectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected install destination: $targetFull"
  }
  $pkg = [IO.Path]::GetFullPath($Package)
  if (-not (Test-Path -LiteralPath $pkg -PathType Leaf)) { throw 'Downloaded archive missing' }
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pkg).Hash -ine $ExpectedHash) {
    throw 'SHA-256 checksum mismatch'
  }
  $stage = Join-Path $env:TEMP ('DeskTouchVision-Extract-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $stage | Out-Null
  Expand-Archive -LiteralPath $pkg -DestinationPath $stage -Force
  $payload = Join-Path $stage 'app'
  if (-not (Test-Path (Join-Path $payload 'DeskTouchVision.exe') -PathType Leaf)) {
    throw 'Invalid package: missing DeskTouchVision.exe'
  }
  if (-not (Test-Path (Join-Path $payload 'DeviceSharing\DeskTouch.DeviceShare.exe') -PathType Leaf)) {
    throw 'Invalid package: missing Windows sharing module'
  }

  $parent = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
  if ($parent -and -not $parent.WaitForExit(15000)) {
    throw 'DeskTouch shell did not close. Update deferred rather than forcibly terminating.'
  }
  Stop-InstalledApplications $targetFull

  $parentDir = Split-Path $targetFull -Parent
  New-Item -ItemType Directory -Force -Path $parentDir | Out-Null
  $backup = Join-Path $parentDir ('DeskTouchVision.backup-' + [guid]::NewGuid().ToString('N'))
  if (Test-Path -LiteralPath $targetFull) {
    Move-Item -LiteralPath $targetFull -Destination $backup
    $movedOriginal = $true
  }
  Move-Item -LiteralPath $payload -Destination $targetFull
  if (-not (Test-Path (Join-Path $targetFull 'DeskTouchVision.exe'))) {
    throw 'Updated application not found after install'
  }
  Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe')
  $activated = $true
  Log "Update installed and restarted. Settings preserved in %LOCALAPPDATA%\DeskTouchVision."
  if ($movedOriginal) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
  # Downloaded release archive and extracted payload are transient after success.
  Remove-Item -LiteralPath $pkg -Force -ErrorAction SilentlyContinue
  $downloadFolder = Split-Path -Path $pkg -Parent
  if ($downloadFolder -like (Join-Path $env:TEMP 'DeskTouchVision-Update*')) {
    Remove-Item -LiteralPath $downloadFolder -Force -ErrorAction SilentlyContinue
  }
}
catch {
  Log "FAILED: $($_.Exception.Message)"
  if ($movedOriginal -and -not $activated) {
    try {
      if (Test-Path -LiteralPath $targetFull) {
        Remove-Item -LiteralPath $targetFull -Recurse -Force -ErrorAction Stop
      }
      Move-Item -LiteralPath $backup -Destination $targetFull -ErrorAction Stop
      Log 'Previous installed version restored after update failure.'
    } catch { Log "Rollback FAILED: $($_.Exception.Message)" }
  }
  # If a network/download/staging/close check failed *before* replacement,
  # do not leave the user with NO DeskTouch window after the updater exited.
  if ($targetFull -and (Test-Path -LiteralPath (Join-Path $targetFull 'DeskTouchVision.exe'))) {
    try {
      $running = @(Get-CimInstance Win32_Process -Filter "Name='DeskTouch.DeviceShare.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($targetFull,
          [StringComparison]::OrdinalIgnoreCase) })
      if ($running.Count -eq 0) {
        Log 'Restarting previous DeskTouch installation after failed update.'
        Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe') -ErrorAction Stop
      }
    } catch { Log "Restart after failure unavailable: $($_.Exception.Message)" }
  }
  throw
}
finally {
  if ($stage -and (Test-Path -LiteralPath $stage)) {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
  }
}
