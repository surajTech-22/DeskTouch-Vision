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
  Get-CimInstance Win32_Process -Filter "Name='DeskTouchVision.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($target,[StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
      $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
      if ($p) {
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(6000)) { Stop-Process -Id $p.Id -Force }
      }
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
  Write-Host "DeskTouch Vision installed. Remember: this is a camera calibration PREVIEW, not a working disconnected keyboard." -ForegroundColor Cyan
} catch {
  Log "FAILED: $($_.Exception.Message)"
  throw
}
