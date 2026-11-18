param()
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$parent = Split-Path $target -Parent
$logFolder = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
New-Item -ItemType Directory -Force -Path $parent, $logFolder | Out-Null
$log = Join-Path $logFolder 'install.log'
function Log([string]$message) {
  ("{0:u} {1}" -f (Get-Date), $message) | Out-File -FilePath $log -Append -Encoding utf8
}

function Stop-DeskTouchGracefully([string]$root) {
  foreach ($exeName in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe')) {
    $installed = @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue)
    foreach ($info in $installed) {
      $p = Get-Process -Id $info.ProcessId -ErrorAction SilentlyContinue
      if (!$p -or $p.Id -eq $PID) { continue }
      Log "Closing running $exeName PID=$($p.Id) ($($info.ExecutablePath)) gracefully"
      if ($exeName -eq 'DeskTouch.DeviceShare.exe') {
        $exitHandle = $null
        if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\DeskTouchVision.WindowsDisplays.ExitEvent.2026', [ref]$exitHandle)) {
          try { [void]$exitHandle.Set() } finally { $exitHandle.Dispose() }
        }
      }
      [void]$p.CloseMainWindow()
      if (-not $p.WaitForExit(8000)) {
        Log "Force closing unresponsive $exeName PID=$($p.Id) after graceful timeout."
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $null = $p.WaitForExit(3000)
      }
    }
  }
}

$stage = Join-Path $parent ('DeskTouchVision.stage-' + [guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('DeskTouchVision.backup-' + [guid]::NewGuid().ToString('N'))
$movedOriginal = $false
$activated = $false
try {
  if (-not (Test-Path -LiteralPath (Join-Path $source 'DeskTouchVision.exe') -PathType Leaf)) {
    throw 'Incomplete installer package: DeskTouchVision.exe not found.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $source 'DeviceSharing\DeskTouch.DeviceShare.exe') -PathType Leaf)) {
    throw 'Incomplete installer package: Windows Displays module not found.'
  }
  # Verify every published executable against the release manifest before any
  # existing installation or input-sharing process is touched.
  & (Join-Path $source 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $source
  New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
  Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force -ErrorAction Stop
  & (Join-Path $stage 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $stage

  Stop-DeskTouchGracefully $target
  if (Test-Path -LiteralPath $target) {
    Move-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
    $movedOriginal = $true
  }
  Move-Item -LiteralPath $stage -Destination $target -ErrorAction Stop
  # Antivirus can quarantine a file between staging and activation; do not
  # declare the install successful or delete the backup until this passes.
  & (Join-Path $target 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $target

  $exe = Join-Path $target 'DeskTouchVision.exe'
  $wsh = New-Object -ComObject WScript.Shell
  $shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeskTouch Vision.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskTouch Vision.lnk')
  )
  foreach ($shortcutPath in $shortcutPaths) {
    $shortcutDir = Split-Path -Path $shortcutPath -Parent
    if ($shortcutDir -and -not (Test-Path -LiteralPath $shortcutDir)) {
      New-Item -ItemType Directory -Path $shortcutDir -Force | Out-Null
    }
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = 'DeskTouch Vision - Displays & Device Sharing'
    $shortcut.Save()
  }
  try {
    $choiceFile = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\windows-startup.choice'
    $startupDisabled = (Test-Path -LiteralPath $choiceFile -PathType Leaf) -and
      ((Get-Content -LiteralPath $choiceFile -Raw -ErrorAction SilentlyContinue).Trim().Equals('disabled', [StringComparison]::OrdinalIgnoreCase))
    if (-not $startupDisabled) {
      $startupDir = [Environment]::GetFolderPath('Startup')
      if ($startupDir) {
        New-Item -ItemType Directory -Force -Path $startupDir | Out-Null
        $trayExe = Join-Path $target 'DeviceSharing\DeskTouch.DeviceShare.exe'
        $sc = $wsh.CreateShortcut((Join-Path $startupDir 'DeskTouch Vision.lnk'))
        $sc.TargetPath = $trayExe
        $sc.Arguments = '--tray'
        $sc.WorkingDirectory = (Split-Path -Path $trayExe -Parent)
        $sc.Description = 'DeskTouch Vision - Keep Windows device sharing available in tray'
        $sc.Save()
      }
    }
  } catch { Log ("Startup entry skipped: {0}" -f $_.Exception.Message) }
  Start-Process -FilePath $exe -ErrorAction Stop
  $activated = $true
  Log "Verified and installed Windows Displays application from package under $target"
  if ($movedOriginal) {
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
  }
  Write-Host 'DeskTouch Windows Displays installed and launched. Native keyboard/mouse drivers remain unchanged.' -ForegroundColor Cyan
}
catch {
  Log "Installation FAILED: $($_.Exception.Message)"
  if (-not $activated -and $movedOriginal -and (Test-Path -LiteralPath $backup)) {
    try {
      if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
      }
      Move-Item -LiteralPath $backup -Destination $target -ErrorAction Stop
      Log 'Previous installation restored after failure.'
      Start-Process -FilePath (Join-Path $target 'DeskTouchVision.exe') -ErrorAction SilentlyContinue
    }
    catch { Log "Rollback failed: $($_.Exception.Message)" }
  }
  throw
}
finally {
  if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
  }
}
