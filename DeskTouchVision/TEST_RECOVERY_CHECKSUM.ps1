# Regression for Windows PowerShell 5.1 checksum response format.
# This test does not download or install software, change Windows settings,
# close processes, or access the user's installed DeskTouch application.
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'RECOVER_DESKTOUCH.ps1') -Raw
if ($source -notmatch 'Invoke-WebRequest -Uri \$hashUrl -UseBasicParsing -OutFile \$checksumFile') {
  throw 'Recovery must download checksum to a file for Windows PowerShell 5.1.'
}
if ($source -notmatch 'Get-Content -LiteralPath \$checksumFile -Raw -Encoding ASCII') {
  throw 'Recovery must explicitly decode the SHA-256 file as text.'
}
if ($source -match '\.Content\.Trim\(') {
  throw 'Recovery still calls .Trim() on an untyped web response Content value.'
}
if ($source.IndexOf("Write-Record 'Downloading verified Windows install package.'") -gt
    $source.IndexOf('  # Stop only the exact DeskTouch executables')) {
  throw 'Recovery must verify download before closing the running app.'
}

$path = Join-Path $env:TEMP ('DeskTouch-sha-test-' + [guid]::NewGuid().ToString('N') + '.sha256')
try {
  $fakeHash = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
  $payload = "$fakeHash  DeskTouchVision-win-x64.zip" + [Environment]::NewLine
  # Equivalent to Invoke-WebRequest returning byte[] rather than string.
  [IO.File]::WriteAllBytes($path, [Text.Encoding]::ASCII.GetBytes($payload))
  $checksum = (Get-Content -LiteralPath $path -Raw -Encoding ASCII).Trim()
  $parts = @($checksum -split '\s+' | Where-Object { $_ })
  if ($parts.Count -ne 2 -or $parts[0] -ne $fakeHash -or
      $parts[1] -ne 'DeskTouchVision-win-x64.zip') {
    throw 'Windows PowerShell failed to parse the ASCII checksum.'
  }
  Write-Host "PASS: checksum decoding works with Windows PowerShell $($PSVersionTable.PSVersion)"
  Write-Host 'PASS: recovery verifies package before closing installed DeskTouch.'
}
finally {
  Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
