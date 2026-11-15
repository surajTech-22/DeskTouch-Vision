param(
    [Parameter(Mandatory=$true)][string]$Root
)
$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$manifestPath = Join-Path $rootPath 'payload-integrity.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "MISSING PACKAGE MANIFEST: $manifestPath. Do not install an incomplete package."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -ne 1 -or -not $manifest.files -or $manifest.files.Count -lt 2) {
    throw 'Invalid DeskTouch package integrity manifest.'
}
$required = @('DeskTouchVision.exe', 'DeviceSharing/DeskTouch.DeviceShare.exe')
$seen = @{}
foreach ($entry in @($manifest.files)) {
    $relative = [string]$entry.path
    $expectedHash = [string]$entry.sha256
    $expectedBytes = [long]$entry.bytes
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or
        $relative -match '(^|[/\\])\.\.([/\\]|$)' -or
        $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $expectedBytes -lt 1 -or $seen.ContainsKey($relative)) {
        throw "Invalid or duplicate file entry in package manifest: $relative"
    }
    $seen[$relative] = $true
    $file = Join-Path $rootPath ($relative -replace '/', '\')
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "MISSING FILE: $relative. The package may be incomplete or antivirus may have quarantined it. Check Windows Security Protection history; do not disable protection."
    }
    $info = Get-Item -LiteralPath $file -ErrorAction Stop
    if ($info.Length -ne $expectedBytes) {
        throw "FILE SIZE MISMATCH: $relative. Installation was blocked to prevent a damaged program."
    }
    $actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($actualHash -ine $expectedHash) {
        throw "SHA-256 MISMATCH: $relative. Installation was blocked to prevent an altered program."
    }
}
foreach ($requiredFile in $required) {
    if (-not $seen.ContainsKey($requiredFile)) {
        throw "Package manifest omits a required executable: $requiredFile"
    }
}
Write-Host ("PASS: DeskTouch package integrity ({0} files) at {1}" -f $manifest.files.Count,$rootPath) -ForegroundColor Green
