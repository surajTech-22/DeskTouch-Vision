param(
  [Parameter(Mandatory=$true)][string]$Package,
  [Parameter(Mandatory=$true)][string]$Target,
  [Parameter(Mandatory=$true)][int]$CurrentPid,
  [Parameter(Mandatory=$true)][string]$ExpectedHash
)
$ErrorActionPreference = 'Stop'
# Set current directory OUTSIDE the application before touching install files.
# Windows refuses to rename folders that are another process's current dir.
$oldWorkingDirectory = (Get-Location).ProviderPath
$independentWorkingDirectory = [IO.Path]::GetFullPath($env:TEMP)
Set-Location -LiteralPath $independentWorkingDirectory
$installMutex = [System.Threading.Mutex]::new($false, 'Local\DeskTouchVision.Installer')
$hasInstallMutex = $false
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs 'update.log'
function Log([string]$text) { ("{0:u} {1}" -f (Get-Date), $text) | Out-File $log -Append -Encoding utf8 }

function Assert-InstallDirectoryAvailable([string]$directory) {
  # The application folder may have a transient handle during shutdown.
  # Retry the RENAME several times; do not force-kill input-sharing apps.
  if (-not (Test-Path -LiteralPath $directory)) { return }
  $targetBackup = Join-Path (Split-Path $directory -Parent) (
    'DeskTouchVision.backup-' + [guid]::NewGuid().ToString('N'))
  for ($attempt = 1; $attempt -le 8; $attempt++) {
    try {
      Move-Item -LiteralPath $directory -Destination $targetBackup -ErrorAction Stop
      return $targetBackup
    } catch {
      Log "Install directory still held (attempt $attempt/8). Waiting for graceful release."
      if ($attempt -eq 8) {
        $owners = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
          Where-Object { $_.ExecutablePath -and
            $_.ExecutablePath.StartsWith($directory + '\',
              [StringComparison]::OrdinalIgnoreCase) } |
          Select-Object Name, ProcessId)
        foreach ($owner in $owners) {
          Log ("Potential lock owner {0} PID={1}" -f $owner.Name, $owner.ProcessId)
        }
        throw "Installation folder remains in use. Please close any open Explorer windows inside DeskTouch and retry. Updater working directory: $independentWorkingDirectory"
      }
      Start-Sleep -Milliseconds 750
    }
  }
}

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
  # Wait briefly for another authorized updater to finish the critical
  # install phase. Old versions used to keep this lock through slow backup
  # deletion, leaving an "already running" loop for many minutes.
  try {
    $hasInstallMutex = $installMutex.WaitOne(90000)
  } catch [System.Threading.AbandonedMutexException] {
    $hasInstallMutex = $true
    Log 'Recovered an abandoned installer mutex after its owner exited.'
  }
  if (-not $hasInstallMutex) {
    $active = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.ProcessId -ne $PID -and
        $_.CommandLine -match '(?i)Update-DeskTouch[.]ps1' } |
      Select-Object ProcessId, CreationDate)
    foreach ($owner in $active) {
      Log ("Other updater process PID={0} created={1}" -f $owner.ProcessId, $owner.CreationDate)
    }
    throw 'Another installer still holds the install lock after 90 seconds. Existing installation was not modified by this attempt.'
  }
  Log "INSTALL LOCK ACQUIRED PID=$PID; working directory outside app: $independentWorkingDirectory"
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
  if (Test-Path -LiteralPath $targetFull) {
    $backup = Assert-InstallDirectoryAvailable $targetFull
    $movedOriginal = $true
    Log "Existing install safely renamed to $backup"
  }
  Move-Item -LiteralPath $payload -Destination $targetFull
  if (-not (Test-Path (Join-Path $targetFull 'DeskTouchVision.exe'))) {
    throw 'Updated application not found after install'
  }
  Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe')
  $activated = $true
  Log "INSTALL COMMITTED PID=$PID. Settings preserved; releasing mutex before cleanup."
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
  # Windows PowerShell can spend many minutes removing a 176-MB .NET
  # single-file backup or extracted directory. Never hold the global
  # installation mutex while deleting ANY of those old files. This was
  # the cause of subsequent release attempts getting skipped.
  if ($hasInstallMutex) {
    try {
      $installMutex.ReleaseMutex()
      Log "INSTALL LOCK RELEASED PID=$PID before package/backup cleanup."
    } catch { Log "Installer mutex release error: $($_.Exception.Message)" }
  }
  $installMutex.Dispose()

  if ($activated) {
    # Unique backup and TEMP folders do not overlap a newer release's
    # staging paths, so cleanup is safe after the critical lock is free.
    if ($movedOriginal -and $backup -and (Test-Path -LiteralPath $backup)) {
      Log "Cleanup started for previous application backup."
      Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($pkg -and (Test-Path -LiteralPath $pkg)) {
      Remove-Item -LiteralPath $pkg -Force -ErrorAction SilentlyContinue
      $downloadFolder = Split-Path -Path $pkg -Parent
      if ($downloadFolder -like (Join-Path $env:TEMP 'DeskTouchVision-Update*')) {
        Remove-Item -LiteralPath $downloadFolder -Force -ErrorAction SilentlyContinue
      }
    }
  }
  if ($stage -and (Test-Path -LiteralPath $stage)) {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
  }
  if ($activated) { Log "POST-INSTALL CLEANUP FINISHED PID=$PID." }
}
