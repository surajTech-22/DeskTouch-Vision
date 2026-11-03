# Regression for v0.2.7. No installed DeskTouch files touched.
$ErrorActionPreference = 'Stop'
$updater = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Update-DeskTouch.ps1') -Raw
$lockReleased = $updater.LastIndexOf('$installMutex.ReleaseMutex()')
$backupDeleted = $updater.LastIndexOf('Remove-Item -LiteralPath $backup')
$stageDeleted = $updater.LastIndexOf('Remove-Item -LiteralPath $stage')
$committed = $updater.IndexOf('INSTALL COMMITTED')
if ($lockReleased -lt 0 -or $backupDeleted -lt 0 -or $stageDeleted -lt 0 -or
    $committed -lt 0 -or $lockReleased -le $committed -or
    $lockReleased -ge $backupDeleted -or $lockReleased -ge $stageDeleted) {
  throw 'Global install lock must be released after commit and before backup/stage cleanup.'
}
if ($updater.Contains('skipping duplicate attempt')) {
  throw 'Contending update should be deferred with an error rather than silently skipped.'
}
$client = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UpdateClient.cs') -Raw
if (-not $client.Contains('IsInstallationRunning()') -or
    -not $client.Contains('CheckResult.InstallationBusy')) {
  throw 'UpdateClient must detect an existing updater and avoid duplicate downloads.'
}
$root = Join-Path $env:TEMP ('DeskTouch-mutex-regression-' + [guid]::NewGuid().ToString('N'))
$mutexName = 'Local\DeskTouchVision.Test.' + [guid]::NewGuid().ToString('N')
$first = $null
$second = $null
try {
  New-Item -ItemType Directory -Path $root -Force | Out-Null
  $workerPath = Join-Path $root 'worker.ps1'
  @'
param([string]$Name, [string]$Marker, [int]$DelayMs)
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, $Name)
$acquired = $false
try {
  $acquired = $mutex.WaitOne(3000)
  if (-not $acquired) { throw 'Timed out waiting for test mutex.' }
  [IO.File]::WriteAllText($Marker, 'acquired')
  $mutex.ReleaseMutex()
  $acquired = $false
  [IO.File]::WriteAllText($Marker, 'released')
  Start-Sleep -Milliseconds $DelayMs
}
finally {
  if ($acquired) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
}
'@ | Set-Content -LiteralPath $workerPath -Encoding ASCII
  $out1 = Join-Path $root 'one.txt'
  $out2 = Join-Path $root 'two.txt'
  $startWorker = {
    param($path, $out, $delay)
    $args = '-NoProfile -ExecutionPolicy Bypass -File "' + $path +
      '" -Name "' + $mutexName + '" -Marker "' + $out +
      '" -DelayMs ' + $delay
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -WorkingDirectory $root -ArgumentList $args
  }
  $first = & $startWorker $workerPath $out1 4500
  $deadline = (Get-Date).AddSeconds(7)
  while (-not (Test-Path $out1) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 80
  }
  if (-not (Test-Path $out1)) { throw 'First updater did not acquire/release test lock.' }
  if (-not $first -or $first.HasExited) { throw 'First updater ended before cleanup simulation.' }
  $second = & $startWorker $workerPath $out2 0
  if (-not $second.WaitForExit(3500)) {
    throw 'Second updater could not acquire mutex while first performs slow cleanup.'
  }
  if ($second.ExitCode -ne 0 -or -not (Test-Path $out2) -or
      (Get-Content $out2 -Raw) -notmatch 'released') {
    throw 'Second updater failed to release test mutex.'
  }
  if ($first.HasExited) { throw 'First cleanup ended before second updater acquired lock.' }
  Write-Host 'PASS: Windows PowerShell 5.1 second installer can acquire lock during first updater cleanup.'
  Write-Host 'PASS: production updater releases lock before backup and extraction cleanup.'
}
finally {
  # Stop only disposable CI child workers; never installed DeskTouch.
  foreach ($child in @($first, $second)) {
    if ($child -and -not $child.HasExited) {
      Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
    }
  }
  if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
