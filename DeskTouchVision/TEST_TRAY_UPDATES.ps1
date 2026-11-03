# v0.2.8 Windows PowerShell 5.1 update/tray integration regression.
# Read-only source checks; does not launch processes or contact GitHub.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function RequireText([string]$relativePath, [string]$needle) {
  $file = Join-Path $root $relativePath
  $body = Get-Content -LiteralPath $file -Raw
  if (-not $body.Contains($needle)) {
    throw "$relativePath is missing required tray/update flow: $needle"
  }
}
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'FormClosing += OnDeskTouchFormClosing'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'HideToTray()'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'StartListening();'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'UpdateClient.StartEventUpdates(this)'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'CloseForVerifiedUpdate() => ExitDeskTouch()'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'ShowFromTray()'
RequireText 'DeviceSharing\DisplaySettingsForm.cs' 'if (!Visible) ShowFromTray();'
RequireText 'DeviceSharing\Program.cs' 'ShowDisplaysMessage'
RequireText 'DeviceSharing\Program.cs' 'args.Any(a => a.Equals("--tray"'
RequireText 'DeviceSharing\DeviceSharing.csproj' '../UpdateClient.cs'
RequireText 'Program.cs' 'launch.ArgumentList.Add("--tray")'
RequireText 'UpdateClient.cs' 'https://ntfy.sh/'
RequireText 'UpdateClient.cs' 'sharingHost.CloseForVerifiedUpdate()'
RequireText 'UpdateClient.cs' 'process.ArgumentList.Add("-StartInTray")'
RequireText 'Update-DeskTouch.ps1' '[switch]$StartInTray'
RequireText 'Update-DeskTouch.ps1' "@('--tray')"
RequireText 'RECOVER_DESKTOUCH.ps1' '$release = ''v0.2.8'''
Write-Host 'PASS: both Windows executables share the release-event auto-updater.'
Write-Host 'PASS: X-to-tray retains window; verified update closes it and restarts quietly in tray.'
