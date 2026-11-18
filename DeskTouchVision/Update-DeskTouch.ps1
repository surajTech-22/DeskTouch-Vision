param(
  [Parameter(Mandatory=$true)][string]$Package,
  [Parameter(Mandatory=$true)][string]$Target,
  [Parameter(Mandatory=$true)][int]$CurrentPid,
  [Parameter(Mandatory=$true)][string]$ExpectedHash,
  [switch]$StartInTray
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
function Remove-TreeQuickly([string]$path) {
  if (-not $path -or -not (Test-Path -LiteralPath $path)) { return }
  try {
    [System.IO.Directory]::Delete($path, $true)
  } catch {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Stop-StaleUpdaters([int]$minAgeSeconds = 180) {
  try {
    $cutoff = (Get-Date).AddSeconds(-$minAgeSeconds)
    $stale = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
      Where-Object {
        $_.ProcessId -ne $PID -and
        $_.CommandLine -match '(?i)Update-DeskTouch[.]ps1' -and
        $_.CreationDate -and
        $_.CreationDate -lt $cutoff
      })
    foreach ($old in $stale) {
      Log ("Stopping stale updater PID={0} started={1} so automatic update is not blocked." -f $old.ProcessId, $old.CreationDate)
      Stop-Process -Id $old.ProcessId -Force -ErrorAction SilentlyContinue
    }
  } catch { }
}

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
  # Signal all running DeskTouch processes gracefully first so hooks/TLS tear down cleanly.
  $exitHandle = $null
  if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\DeskTouchVision.WindowsDisplays.ExitEvent.2026', [ref]$exitHandle)) {
    try { [void]$exitHandle.Set() } finally { $exitHandle.Dispose() }
  }
  foreach ($exeName in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe')) {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue)
    foreach ($info in $processes) {
      $p = Get-Process -Id $info.ProcessId -ErrorAction SilentlyContinue
      if (-not $p -or $p.Id -eq $PID) { continue }
      Log "Requesting graceful close: $exeName PID=$($p.Id) ($($info.ExecutablePath))"
      [void]$p.CloseMainWindow()
    }
  }
  $deadline = (Get-Date).AddMilliseconds(4500)
  while ((Get-Date) -lt $deadline) {
    $remaining = @(Get-Process -Name 'DeskTouchVision','DeskTouch.DeviceShare' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID })
    if ($remaining.Count -eq 0) { return }
    Start-Sleep -Milliseconds 150
  }
  # Ensure NO DeskTouch process (installed, tray, hung, or external) blocks automatic update installation.
  foreach ($proc in @(Get-Process -Name 'DeskTouchVision','DeskTouch.DeviceShare' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID })) {
    try {
      Log "Stopping remaining $(([string]$proc.ProcessName)) PID=$($proc.Id) so automatic update installs cleanly."
      Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
      $null = $proc.WaitForExit(3000)
    } catch { }
  }
}

$stage = $null
$backup = $null
$movedOriginal = $false
$activated = $false
try {
  Stop-StaleUpdaters -minAgeSeconds 180
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
  $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
  if (Test-Path -LiteralPath $tarExe -PathType Leaf) {
    & $tarExe -xf $pkg -C $stage
    if ($LASTEXITCODE -ne 0) { throw "Package archive extraction failed with exit code $LASTEXITCODE" }
  } else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($pkg, $stage)
  }
  $payload = Join-Path $stage 'app'
  if (-not (Test-Path (Join-Path $payload 'DeskTouchVision.exe') -PathType Leaf)) {
    throw 'Invalid package: missing DeskTouchVision.exe'
  }
  if (-not (Test-Path (Join-Path $payload 'DeviceSharing\DeskTouch.DeviceShare.exe') -PathType Leaf)) {
    throw 'Invalid package: missing Windows sharing module'
  }
  # The official ZIP SHA-256 protects transport; the per-file manifest also
  # diagnoses a missing/quarantined DLL or executable after extraction.
  & (Join-Path $payload 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $payload

  $parent = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
  if ($parent -and -not $parent.WaitForExit(5000)) {
    Log "DeskTouch shell PID=$CurrentPid did not exit gracefully within 5s; forcing exit for automatic update."
    Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
    $null = $parent.WaitForExit(3000)
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
  # A late antivirus quarantine must not be treated as update success:
  # restore the previous directory under the existing rollback path.
  & (Join-Path $targetFull 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $targetFull
  try {
    $choiceFile = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\windows-startup.choice'
    $startupDisabled = (Test-Path -LiteralPath $choiceFile -PathType Leaf) -and
      ((Get-Content -LiteralPath $choiceFile -Raw -ErrorAction SilentlyContinue).Trim().Equals('disabled', [StringComparison]::OrdinalIgnoreCase))
    if (-not $startupDisabled) {
      $startupDir = [Environment]::GetFolderPath('Startup')
      if ($startupDir) {
        New-Item -ItemType Directory -Force -Path $startupDir | Out-Null
        $trayExe = Join-Path $targetFull 'DeviceSharing\DeskTouch.DeviceShare.exe'
        $wsh = New-Object -ComObject WScript.Shell
        $sc = $wsh.CreateShortcut((Join-Path $startupDir 'DeskTouch Vision.lnk'))
        $sc.TargetPath = $trayExe
        $sc.Arguments = '--tray'
        $sc.WorkingDirectory = (Split-Path -Path $trayExe -Parent)
        $sc.Description = 'DeskTouch Vision - Keep Windows device sharing available in tray'
        $sc.Save()
      }
    }
  } catch { Log ("Startup entry sync skipped: {0}" -f $_.Exception.Message) }
  try {
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    Set-Content -LiteralPath (Join-Path (Split-Path $logs -Parent) 'just-updated.txt') -Value ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()) -Encoding utf8
  } catch { }
  $restartArgs = if ($StartInTray) { @('--tray') } else { @() }
  if ($restartArgs.Count -gt 0) {
    Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe') -ArgumentList $restartArgs
  } else {
    Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe')
  }
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
        if ($StartInTray) {
          Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe') -ArgumentList '--tray' -ErrorAction Stop
        } else {
          Start-Process -FilePath (Join-Path $targetFull 'DeskTouchVision.exe') -ErrorAction Stop
        }
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
      try { [System.IO.Directory]::Delete($backup, $true) }
      catch { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if ($pkg -and (Test-Path -LiteralPath $pkg)) {
      Remove-Item -LiteralPath $pkg -Force -ErrorAction SilentlyContinue
      $downloadFolder = Split-Path -Path $pkg -Parent
      if ($downloadFolder -like (Join-Path $env:TEMP 'DeskTouchVision-Update*')) {
        Remove-TreeQuickly $downloadFolder
      }
    }
  }
  if ($stage -and (Test-Path -LiteralPath $stage)) {
    try { [System.IO.Directory]::Delete($stage, $true) }
    catch { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
  }
  if ($activated) { Log "POST-INSTALL CLEANUP FINISHED PID=$PID." }
}
