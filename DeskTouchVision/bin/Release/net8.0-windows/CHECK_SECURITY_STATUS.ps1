# Read-only security / incomplete-install diagnostics. Never changes antivirus
# settings, restores quarantined software or requests permanent exclusions.
$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
$downloads = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'
if (-not (Test-Path -LiteralPath $downloads)) { $downloads = $env:TEMP }
$report = Join-Path $downloads ('DeskTouch_Security_Diagnostic_{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$lines = New-Object 'System.Collections.Generic.List[string]'
function Note([string]$msg) {
    Write-Host $msg
    [void]$lines.Add($msg)
}
Note ('DeskTouch security and install diagnostic: ' + (Get-Date))
Note 'READ ONLY - no antivirus exclusions, disabled protections, removed malware or restored quarantined files.'
Note ('Install root: ' + $root)
foreach ($relative in @('DeskTouchVision.exe',
                        'DeviceSharing\DeskTouch.DeviceShare.exe',
                        'payload-integrity.json',
                        'Update-DeskTouch.ps1')) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        Note ('MISSING: ' + $relative)
        continue
    }
    $item = Get-Item -LiteralPath $file
    Note ("PRESENT: {0} | size={1} bytes | version={2}" -f $relative,$item.Length,$item.VersionInfo.FileVersion)
    if ($relative -like '*.exe') {
        try {
            $signature = Get-AuthenticodeSignature -LiteralPath $file
            Note ("  Authenticode: {0} | signer={1}" -f $signature.Status,$signature.SignerCertificate.Subject)
        } catch { Note ('  Signature query unavailable: ' + $_.Exception.Message) }
    }
}
try {
    $engine = Get-MpComputerStatus -ErrorAction Stop
    Note ("Microsoft Defender active={0} antivirus={1} real-time={2}" -f
        $engine.AMServiceEnabled,$engine.AntivirusEnabled,$engine.RealTimeProtectionEnabled)
} catch { Note 'Microsoft Defender status unavailable (third-party antivirus or access limitation).' }

try {
    $detections = @(Get-MpThreatDetection -ErrorAction Stop | Where-Object {
        ($_.Resources -join '; ') -match '(?i)desktouch|desk[ -]?touch\.device'
    } | Sort-Object InitialDetectionTime -Descending | Select-Object -First 12)
    foreach ($hit in $detections) {
        Note ("DEFENDER DETECTION: {0} | threat ID={1} | action success={2}" -f
            $hit.InitialDetectionTime,$hit.ThreatID,$hit.ActionSuccess)
        Note ('  Affected resources: ' + ($hit.Resources -join '; '))
    }
    if ($detections.Count -eq 0) {
        Note 'No DeskTouch-specific Defender detections returned (does not rule out another antivirus).'
    }
} catch {
    Note ('Defender Protection History query unavailable: ' + $_.Exception.Message)
}
try {
    $events = @(Get-WinEvent -FilterHashtable @{
        LogName='Microsoft-Windows-Windows Defender/Operational'
        Id=1116,1117,1118
        StartTime=(Get-Date).AddDays(-7)
    } -MaxEvents 200 -ErrorAction Stop | Where-Object {
        $_.Message -match '(?i)desktouch|desk[ -]?touch\.device'
    } | Select-Object -First 8)
    foreach ($event in $events) {
        Note ("DEFENDER EVENT: {0} | event={1}" -f $event.TimeCreated,$event.Id)
        Note ('  ' + (($event.Message -split "\r?\n") |
            Where-Object { $_ -match '(?i)desktouch|file|threat|action' } |
            Select-Object -First 5) -join ' | ')
    }
} catch {
    Note ('Defender event log unavailable: ' + $_.Exception.Message)
}
foreach ($name in @('install.log','update.log','recovery.log')) {
    $file = Join-Path $logs $name
    if (Test-Path -LiteralPath $file) {
        Note ("RECENT " + $name + ':')
        foreach ($line in @(Get-Content -LiteralPath $file -Tail 15)) {Note ('  ' + $line)}
    }
}
Note 'Next: Inspect Windows Security > Virus & threat protection > Protection history for the exact item and publisher.'
Note 'If a detection is present, submit the SHA-256 and the original GitHub file to Microsoft/your AV vendor for false-positive review. Do NOT turn off antivirus or add blanket folder exclusions.'
[IO.File]::WriteAllLines($report, $lines.ToArray(), [Text.Encoding]::UTF8)
Write-Host ("Saved diagnostics: " + $report) -ForegroundColor Cyan
