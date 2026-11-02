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
function Log([string]$msg) { ("{0:u} {1}" -f (Get-Date), $msg) | Out-File $log -Append -Encoding utf8 }
try {
  if (-not (Test-Path $Package)) { throw 'Downloaded archive missing' }
  $actual = (Get-FileHash -Algorithm SHA256 $Package).Hash
  if ($actual -ine $ExpectedHash) { throw 'SHA-256 checksum mismatch' }
  $stage = Join-Path $env:TEMP ('DeskTouchVision-Extract-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $stage | Out-Null
  Expand-Archive -Path $Package -DestinationPath $stage -Force
  $payload = Join-Path $stage 'app'
  if (-not (Test-Path (Join-Path $payload 'DeskTouchVision.exe'))) { throw 'Invalid app archive' }
  $parent = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
  if ($parent) {
    [void]$parent.WaitForExit(12000)
    if (-not $parent.HasExited -and $parent.Path -like '*DeskTouchVision.exe') {
      Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
    }
  }
  Start-Sleep -Milliseconds 800
  New-Item -ItemType Directory -Force -Path $Target | Out-Null
  Copy-Item -Path (Join-Path $payload '*') -Destination $Target -Recurse -Force
  Log "Installed update from $Package"
  Start-Process -FilePath (Join-Path $Target 'DeskTouchVision.exe')
  Remove-Item -Path $stage -Recurse -Force -ErrorAction SilentlyContinue
} catch {
  Log "FAILED: $($_.Exception.Message)"
  throw
}
