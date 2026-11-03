param()
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
New-Item -ItemType Directory -Force -Path $logs, $target | Out-Null
$log = Join-Path $logs 'install.log'
function Log([string]$msg) {
  ("{0:u} {1}" -f (Get-Date), $msg) | Out-File $log -Append -Encoding utf8
}
try {
  if (-not (Test-Path (Join-Path $source 'DeskTouchVision.exe'))) { throw 'App payload missing: app\DeskTouchVision.exe' }
  # Never forcibly terminate active keyboard/mouse sharing: it must release
  # held keys/buttons and network ownership through its normal FormClosing path.
  foreach ($exeName in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe')) {
    $expected = if ($exeName -eq 'DeskTouchVision.exe') {
      Join-Path $target $exeName
    } else { Join-Path $target "DeviceSharing\$exeName" }
    Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction Stop |
      Where-Object { [string]::Equals($_.ExecutablePath, $expected, [StringComparison]::OrdinalIgnoreCase) } |
      ForEach-Object {
        $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
          [void]$process.CloseMainWindow()
          if (-not $process.WaitForExit(12000)) {
            throw "$exeName is still running; install deferred to preserve native input safety."
          }
        }
      }
  }
  if (-not (Test-Path (Join-Path $source 'DeviceSharing\DeskTouch.DeviceShare.exe'))) {
    throw 'Package missing Windows Device Sharing module'
  }
  Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
  $exe = Join-Path $target 'DeskTouchVision.exe'
  $w = New-Object -ComObject WScript.Shell
  $shortcut = $w.CreateShortcut((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeskTouch Vision.lnk'))
  $shortcut.TargetPath = $exe
  $shortcut.WorkingDirectory = $target
  $shortcut.Save()
  Log "Installed to $target"
  Start-Process -FilePath $exe
  Write-Host 'DeskTouch Vision installed. Windows sharing is experimental: opt in on both PCs. Disconnected camera devices are not supported yet.' -ForegroundColor Cyan
} catch {
  Log "FAILED: $($_.Exception.Message)"
  throw
}
