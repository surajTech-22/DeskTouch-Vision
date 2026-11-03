# CI-only Windows regression: the detached update host must not pin the
# app folder as its working directory. Only a disposable test process is ended.
$ErrorActionPreference = 'Stop'
$client = Get-Content (Join-Path $PSScriptRoot 'UpdateClient.cs') -Raw
if (-not $client.Contains('WorkingDirectory = safeWorkingDirectory') -or
    -not $client.Contains('File.Copy(updater, detachedUpdater')) {
  throw 'Updater must copy its script to TEMP and explicitly set a safe working directory.'
}
$updater = Get-Content (Join-Path $PSScriptRoot 'Update-DeskTouch.ps1') -Raw
if (-not $updater.Contains('Set-Location -LiteralPath $independentWorkingDirectory')) {
  throw 'Updater script must leave the installed application directory before renaming.'
}

$root = Join-Path $env:TEMP ('DeskTouch-lock-regression-' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $root 'DeskTouchVision'
$outside = Join-Path $root 'detached-host'
$renamed = Join-Path $root 'previous-install'
$child = $null
try {
  New-Item -ItemType Directory -Force -Path $installed, $outside | Out-Null
  'original user data' | Set-Content -Path (Join-Path $installed 'do-not-lose.txt')
  $hostScript = Join-Path $outside 'wait.ps1'
  'Start-Sleep -Seconds 8' | Set-Content -Path $hostScript
  $child = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -WorkingDirectory $outside -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $hostScript + '"'))
  Start-Sleep -Milliseconds 500
  if ($child.HasExited) { throw 'Detached test host terminated before directory rename.' }
  Move-Item -LiteralPath $installed -Destination $renamed -ErrorAction Stop
  if (-not (Test-Path (Join-Path $renamed 'do-not-lose.txt'))) {
    throw 'Old installed folder was lost during detached host update test.'
  }
  Write-Host 'PASS: detached updater process runs while installed directory is safely renamed.'
}
finally {
  if ($child -and -not $child.HasExited) {
    Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue # CI-only disposable child
  }
  if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
