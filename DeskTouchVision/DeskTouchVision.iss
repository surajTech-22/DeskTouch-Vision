#ifndef AppVersion
  #define AppVersion "0.3.91"
#endif
#ifndef SourceDir
  #define SourceDir "dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "published"
#endif

[Setup]
AppId={{D9C8A231-5E4A-4B8F-9F1C-8A77E20B0384}
AppName=DeskTouch Vision
AppVersion={#AppVersion}
AppVerName=DeskTouch Vision {#AppVersion}
AppPublisher=surajTech-22
AppPublisherURL=https://github.com/surajTech-22/DeskTouch-Vision
DefaultDirName={localappdata}\Programs\DeskTouchVision
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DeskTouchVision-Install
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=force
CloseApplicationsFilter=*.exe
SetupMutex=DeskTouchVision.InnoInstaller
UninstallDisplayName=DeskTouch Vision
UninstallDisplayIcon={app}\DeskTouchVision.exe

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\DeskTouch Vision"; Filename: "{app}\DeskTouchVision.exe"; WorkingDir: "{app}"; Comment: "DeskTouch Vision - Displays & Device Sharing"
Name: "{userprograms}\DeskTouch Vision"; Filename: "{app}\DeskTouchVision.exe"; WorkingDir: "{app}"; Comment: "DeskTouch Vision - Displays & Device Sharing"
Name: "{userstartup}\DeskTouch Vision"; Filename: "{app}\DeviceSharing\DeskTouch.DeviceShare.exe"; Parameters: "--tray"; WorkingDir: "{app}\DeviceSharing"; Comment: "DeskTouch Vision - Keep Windows device sharing available in tray"; Check: ShouldCreateStartupShortcut

[Run]
Filename: "{app}\DeskTouchVision.exe"; Description: "Launch DeskTouch Vision now"; Flags: nowait postinstall skipifsilent

[Code]
function ShouldCreateStartupShortcut(): Boolean;
var
  ChoiceFile: String;
  Content: AnsiString;
  ContentText: String;
begin
  Result := True;
  ChoiceFile := ExpandConstant('{localappdata}\DeskTouchVision\windows-startup.choice');
  if FileExists(ChoiceFile) and LoadStringFromFile(ChoiceFile, Content) then
  begin
    ContentText := Content;
    if Pos('disabled', Lowercase(Trim(ContentText))) > 0 then
      Result := False;
  end;
end;

procedure StopRunningDeskTouchProcesses();
var
  ResultCode: Integer;
  PsCmd: String;
begin
  PsCmd := '-NoProfile -NonInteractive -WindowStyle Hidden -Command "' +
    '$names=@(''DeskTouchVision'',''DeskTouch.DeviceShare''); ' +
    '$running=@(Get-Process -Name $names -ErrorAction SilentlyContinue); ' +
    'if($running.Count -gt 0){' +
      '$h=$null; if([System.Threading.EventWaitHandle]::TryOpenExisting(''Local\DeskTouchVision.WindowsDisplays.ExitEvent.2026'',[ref]$h)){try{[void]$h.Set()}finally{$h.Dispose()}}; ' +
      'Start-Sleep -Milliseconds 1200; ' +
      'Get-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue' +
    '}"';
  Exec('powershell.exe', PsCmd, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopRunningDeskTouchProcesses();
end;
