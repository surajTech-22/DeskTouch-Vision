$ErrorActionPreference='Stop'
$verify = Join-Path $PSScriptRoot 'VERIFY_DESKTOUCH_PAYLOAD.ps1'
$root = Join-Path $env:TEMP ('DeskTouchTest-' + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path (Join-Path $root 'DeviceSharing') -Force | Out-Null
  [IO.File]::WriteAllBytes((Join-Path $root 'DeskTouchVision.exe'),[byte[]](65,66,67))
  [IO.File]::WriteAllBytes((Join-Path $root 'DeviceSharing\DeskTouch.DeviceShare.exe'),[byte[]](68,69,70))
  $files = @('DeskTouchVision.exe','DeviceSharing/DeskTouch.DeviceShare.exe') | ForEach-Object {
    $relative = $_
    $file = Join-Path $root ($relative -replace '/','\')
    [pscustomobject]@{path=$relative;sha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item $file).Length}
  }
  [IO.File]::WriteAllText((Join-Path $root 'payload-integrity.json'),
    ([pscustomobject]@{schema=1;files=$files}|ConvertTo-Json -Depth 5), [Text.Encoding]::UTF8)
  & $verify -Root $root
  Remove-Item -LiteralPath (Join-Path $root 'DeviceSharing\DeskTouch.DeviceShare.exe') -Force
  $errorFound=$false
  try { & $verify -Root $root | Out-Null } catch { $errorFound=$_.Exception.Message -match 'MISSING FILE' }
  if (-not $errorFound) { throw 'Missing Windows sharing module was not detected.' }
  [IO.File]::WriteAllBytes((Join-Path $root 'DeviceSharing\DeskTouch.DeviceShare.exe'),[byte[]](68,69,70))
  [IO.File]::WriteAllBytes((Join-Path $root 'DeskTouchVision.exe'),[byte[]](65,66,88))
  $errorFound=$false
  try { & $verify -Root $root | Out-Null } catch { $errorFound=$_.Exception.Message -match 'SHA-256 MISMATCH' }
  if (-not $errorFound) { throw 'Modified binary was not detected.' }
  Write-Host 'PASS: genuine package verifies; missing or modified Desktop files fail closed.' -ForegroundColor Green
} finally {Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
