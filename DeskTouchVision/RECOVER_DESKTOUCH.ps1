# DeskTouch Vision one-time safe recovery for versions whose updater cannot
# rename its own installation directory. Run from Downloads, NOT from the
# installed application's folder. Windows PowerShell 5.1 compatible.
# The downloaded release ZIP and its publisher-supplied SHA-256 are verified.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$release = 'v0.3.11'
$baseUrl = "https://github.com/surajTech-22/DeskTouch-Vision/releases/download/$release"
$archiveName = 'DeskTouchVision-win-x64.zip'
$expectedAppVersion = '0.3.11.0'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\DeskTouchVision'
$logs = Join-Path $env:LOCALAPPDATA 'DeskTouchVision\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs 'recovery.log'
function Write-Record([string]$message) {
  ("{0:u} {1}" -f (Get-Date), $message) | Out-File -FilePath $log -Append -Encoding utf8
  Write-Host $message
}

# Move this shell outside the installation. Do not load update scripts from
# the running app; they can retain directory handles on Windows.
Set-Location -LiteralPath ([IO.Path]::GetFullPath($env:TEMP))
$work = Join-Path $env:TEMP ('DeskTouchVision-Recovery-' + [guid]::NewGuid().ToString('N'))
$mutex = [System.Threading.Mutex]::new($false, 'Local\DeskTouchVision.ManualRecovery')
$acquired = $false
$installGate = $null
$hasInstallGate = $false
try {
  try { $acquired = $mutex.WaitOne(0) }
  catch [System.Threading.AbandonedMutexException] { $acquired = $true }
  if (-not $acquired) { throw 'Another DeskTouch recovery is already running.' }

  Write-Record "Recovering DeskTouch to $release with native Windows controls preserved."
  New-Item -ItemType Directory -Force -Path $work | Out-Null
  $zip = Join-Path $work $archiveName
  $hashUrl = "$baseUrl/$archiveName.sha256"
  $zipUrl = "$baseUrl/$archiveName"

  Write-Record "Fetching publisher release checksum for $release."
  # In Windows PowerShell 5.1 Invoke-WebRequest.Content can be byte[]
  # (or even a scalar byte), so it has no string Trim() method.
  # Save the checksum to disk first, then decode it explicitly as ASCII.
  $checksumFile = Join-Path $work ($archiveName + '.sha256')
  Invoke-WebRequest -Uri $hashUrl -UseBasicParsing -OutFile $checksumFile
  $checksum = (Get-Content -LiteralPath $checksumFile -Raw -Encoding ASCII).Trim()
  $parts = @($checksum -split '\s+' | Where-Object { $_ })
  if ($parts.Count -lt 2 -or $parts[0] -notmatch '^[0-9A-Fa-f]{64}$' -or
      -not $checksum.Contains($archiveName)) {
    throw 'Official release checksum file is malformed.'
  }

  Write-Record 'Downloading verified Windows install package.'
  Invoke-WebRequest -Uri $zipUrl -UseBasicParsing -OutFile $zip
  $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
  if ($actual -ine $parts[0]) { throw 'Download failed SHA-256 validation; old installation was not modified.' }
  Write-Record 'Package checksum verified.'

  $unpack = Join-Path $work 'extracted'
  Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
  $app = Join-Path $unpack 'app\DeviceSharing\DeskTouch.DeviceShare.exe'
  $installer = Join-Path $unpack 'Install-DeskTouch.ps1'
  if (-not (Test-Path -LiteralPath $app -PathType Leaf) -or
      -not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'Release package missing Windows sharing executable or installer.'
  }
  & (Join-Path $unpack 'app\VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root (Join-Path $unpack 'app')
  $payloadVersion = (Get-Item -LiteralPath $app).VersionInfo.FileVersion
  if ($payloadVersion -ne $expectedAppVersion) {
    throw "Unexpected package version $payloadVersion; expected $expectedAppVersion."
  }

  # Older auto-update scripts can hold the install folder open by inheriting
  # its working directory. Do not race one; let it finish first.
  for ($iteration = 0; $iteration -lt 12; $iteration++) {
    $updaters = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -match '(?i)Update-DeskTouch[.]ps1' })
    if ($updaters.Count -eq 0) { break }
    if ($iteration -eq 11) {
      foreach ($owner in $updaters) {
        Write-Record ("Old updater process is still active: PID={0} started={1}" -f $owner.ProcessId, $owner.CreationDate)
      }
      throw 'An older installer is still running. Recovery has NOT closed DeskTouch or overwritten any app files. Run CHECK_UPDATE_LOCK.ps1 and inspect its PIDs before retrying.'
    }
    Start-Sleep -Seconds 2
  }

  # Close no application until the earlier auto-installer host has
  # finished and the installation mutex is free. This prevents concurrent
  # manual recovery and stale background download from replacing each other.
  $installGate = [System.Threading.Mutex]::new(
    $false, 'Local\DeskTouchVision.Installer')
  try { $hasInstallGate = $installGate.WaitOne(0) }
  catch [System.Threading.AbandonedMutexException] { $hasInstallGate = $true }
  if (-not $hasInstallGate) {
    throw 'The DeskTouch automatic updater still owns the install lock. Recovery left the installed app unchanged. Run CHECK_UPDATE_LOCK.ps1 for read-only diagnostics.'
  }
  Write-Record 'Install lock acquired; existing package verified before app shutdown.'

  # Download and checksum validation have finished successfully. Only now
  # close running applications, so a failed download leaves DeskTouch running.
  # Stop only the exact DeskTouch executables in the canonical per-user path.
  # Graceful window close releases its keyboard/mouse hooks; NEVER force-kill.
  foreach ($name in @('DeskTouchVision.exe', 'DeskTouch.DeviceShare.exe')) {
    $expected = if ($name -eq 'DeskTouchVision.exe') {
      Join-Path $installRoot $name
    } else { Join-Path $installRoot "DeviceSharing\$name" }
    $found = @(Get-CimInstance Win32_Process -Filter "Name='$name'" -ErrorAction Stop)
    foreach ($info in $found) {
      if (-not $info.ExecutablePath -or -not [string]::Equals(
          $info.ExecutablePath, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        continue
      }
      $process = Get-Process -Id $info.ProcessId -ErrorAction SilentlyContinue
      if ($process) {
        Write-Record "Closing installed $name (PID $($process.Id)) gracefully."
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) {
          throw "DeskTouch input-sharing process $($process.Id) did not close safely."
        }
      }
    }
  }

  # Install-DeskTouch.ps1 already stages, safely closes only installed apps,
  # atomically swaps with rollback, and starts the Windows Displays launcher.
  Write-Record 'Installing from a detached TEMP directory with rollback support.'
  & $installer

  $installedSharing = Join-Path $installRoot 'DeviceSharing\DeskTouch.DeviceShare.exe'
  if (-not (Test-Path -LiteralPath $installedSharing)) {
    throw 'Installation exited without creating the sharing module.'
  }
  & (Join-Path $installRoot 'VERIFY_DESKTOUCH_PAYLOAD.ps1') -Root $installRoot
  $installedVersion = (Get-Item $installedSharing).VersionInfo.FileVersion
  if ($installedVersion -ne $expectedAppVersion) {
    throw "Expected $expectedAppVersion, but sharing module remains $installedVersion."
  }
  Write-Record "RECOVERY SUCCESS: DeskTouch Windows sharing version $installedVersion installed."
}
catch {
  Write-Record ("RECOVERY FAILED: " + $_.Exception.Message)
  throw
}
finally {
  # Release both mutexes before expensive TEMP folder removal.
  if ($hasInstallGate) {
    try { $installGate.ReleaseMutex() } catch { }
  }
  if ($installGate) { $installGate.Dispose() }
  if ($work -and (Test-Path -LiteralPath $work)) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
  }
  if ($acquired) { try { $mutex.ReleaseMutex() } catch { } }
  $mutex.Dispose()
}
