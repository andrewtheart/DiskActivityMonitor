; Inno Setup script for Disk Activity Monitor.
; Installs the collector as a Windows service (LocalSystem = elevated, visible in the
; Services snap-in) and the tray dashboard as a per-user app. Self-contained publish output,
; so no .NET runtime is required on the target machine.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif

#define AppName "Disk Activity Monitor"
#define ServiceName "DiskActivityMonitor"
#define ServiceDisplay "Disk Activity Monitor"
#define ServiceExe "DiskActivityMonitor.Service.exe"
#define TrayExe "DiskActivityMonitor.Tray.exe"
#define Publisher "Disk Activity Monitor"

[Setup]
AppId={{8B5F2E2A-7C3D-4F2B-9E1A-2D6C9F4A1B77}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DiskActivityMonitor-Setup-{#AppVersion}-{#AppArch}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\app.ico
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
#if AppArch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; We stop the service / tray ourselves in [Code], so don't let Inno prompt to close apps.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start the tray icon automatically at sign-in (all users)"
Name: "desktopicon"; Description: "Create a desktop shortcut for the dashboard"; Flags: unchecked

[Files]
Source: "publish\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "publish\tray\*";    DestDir: "{app}\tray";    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\assets\app.ico"; DestDir: "{app}";          Flags: ignoreversion
Source: "..\scripts\secure-directory.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"
Name: "{group}\Open data folder"; Filename: "{commonappdata}\{#ServiceName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"; Tasks: autostart

[Run]
; Register and start the Windows service (LocalSystem => runs elevated, shows in services.msc).
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\service\{#ServiceExe}"" start= auto obj= LocalSystem DisplayName= ""{#ServiceDisplay}"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering Windows service..."
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Collects SSD/HDD read-write trends to protect drive endurance."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting service..."
; Launch the tray dashboard at the end of setup.
Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteSvc"

[Code]
const
  DamFileAttributeReparsePoint = $400;
  DamInvalidFileAttributes = $FFFFFFFF;

var
  UseExistingSettings: Boolean;
  KeepSettingsAfterUninstall: Boolean;

function GetFileAttributes(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function DataPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\{#ServiceName}');
end;

function SettingsPath(): String;
begin
  Result := AddBackslash(DataPath()) + 'config.json';
end;

function UserSettingsDirectory(): String;
begin
  Result := ExpandConstant('{localappdata}\{#ServiceName}');
end;

function UserSettingsPath(): String;
begin
  Result := AddBackslash(UserSettingsDirectory()) + 'user-settings.json';
end;

function IsReparsePoint(Path: String): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributes(Path);
  Result := (Attributes <> DamInvalidFileAttributes) and
    ((Attributes and DamFileAttributeReparsePoint) <> 0);
end;

function IsSamePath(Path: String; ExpectedPath: String): Boolean;
begin
  Result := CompareText(
    AddBackslash(ExpandFileName(Path)),
    AddBackslash(ExpandFileName(ExpectedPath))) = 0;
end;

function ValidateInstallDirectory(var ErrorText: String): Boolean;
var
  AppPath: String;
  ServicePath: String;
  TrayPath: String;
begin
  Result := False;
  AppPath := ExpandConstant('{app}');
  ServicePath := AddBackslash(AppPath) + 'service';
  TrayPath := AddBackslash(AppPath) + 'tray';

  if IsReparsePoint(ExpandConstant('{autopf}')) then
  begin
    ErrorText := 'Setup refused to use Program Files because it is a reparse point.';
    Exit;
  end;

  if not IsSamePath(AppPath, ExpandConstant('{autopf}\{#AppName}')) then
  begin
    ErrorText := 'For service security, Disk Activity Monitor must be installed exactly at:' + #13#10 +
      ExpandConstant('{autopf}\{#AppName}');
    Exit;
  end;

  if (DirExists(AppPath) and IsReparsePoint(AppPath)) or
     (DirExists(ServicePath) and IsReparsePoint(ServicePath)) or
     (DirExists(TrayPath) and IsReparsePoint(TrayPath)) then
  begin
    ErrorText := 'Setup refused to use an application, service, or tray directory that is a reparse point.';
    Exit;
  end;

  Result := True;
end;

function PowerShellLiteral(Value: String): String;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := '''' + Value + '''';
end;

function RunDirectorySecurity(Path: String; Profile: String; var ErrorText: String): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Result := False;
  try
    ExtractTemporaryFile('secure-directory.ps1');
  except
    ErrorText := 'Setup could not extract its directory security helper: ' +
      GetExceptionMessage();
    Exit;
  end;
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%s" -Path "%s" -Profile %s';
  Parameters := Format(Parameters, [ExpandConstant('{tmp}\secure-directory.ps1'), Path, Profile]);
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Parameters, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    ErrorText := 'Setup could not validate or secure this directory without following ' +
      'reparse points:' + #13#10 + Path + #13#10 +
      'Security helper exit code: ' + IntToStr(ResultCode);
    Exit;
  end;
  Result := True;
end;

function SecureApplicationDirectory(var ErrorText: String): Boolean;
var
  AppPath: String;
  ServicePath: String;
  TrayPath: String;
begin
  Result := False;
  AppPath := ExpandConstant('{app}');
  ServicePath := AddBackslash(AppPath) + 'service';
  TrayPath := AddBackslash(AppPath) + 'tray';

  if not ForceDirectories(AppPath) then
  begin
    ErrorText := 'Setup could not create the application directory:' + #13#10 + AppPath;
    Exit;
  end;
  if IsReparsePoint(AppPath) then
  begin
    ErrorText := 'Setup refused to secure an application directory that is a reparse point.';
    Exit;
  end;
  if not RunDirectorySecurity(ExpandConstant('{autopf}'), 'Validate', ErrorText) then
    Exit;
  if not RunDirectorySecurity(AppPath, 'Install', ErrorText) then
    Exit;
  if DirExists(ServicePath) and
     (not RunDirectorySecurity(ServicePath, 'Install', ErrorText)) then
    Exit;
  if DirExists(TrayPath) and
     (not RunDirectorySecurity(TrayPath, 'Install', ErrorText)) then
    Exit;
  Result := True;
end;

function SecureDataDirectory(var ErrorText: String): Boolean;
begin
  Result := False;
  if IsReparsePoint(ExpandConstant('{commonappdata}')) then
  begin
    ErrorText := 'Setup refused to use ProgramData because it is a reparse point.';
    Exit;
  end;
  if DirExists(DataPath()) and IsReparsePoint(DataPath()) then
  begin
    ErrorText := 'Setup refused to use the data directory because it is a reparse point:' + #13#10 +
      DataPath();
    Exit;
  end;

  if not ForceDirectories(DataPath()) then
  begin
    ErrorText := 'Setup could not create the data directory:' + #13#10 + DataPath();
    Exit;
  end;

  if IsReparsePoint(DataPath()) then
  begin
    ErrorText := 'Setup refused to secure the data directory because it became a reparse point:' + #13#10 +
      DataPath();
    Exit;
  end;

  if not RunDirectorySecurity(ExpandConstant('{commonappdata}'), 'Validate', ErrorText) then
    Exit;
  if not RunDirectorySecurity(DataPath(), 'Data', ErrorText) then
    Exit;
  Result := True;
end;

function StopInstalledTray(var ErrorText: String): Boolean;
var
  ResultCode: Integer;
  Script: String;
  Parameters: String;
  TargetPath: String;
begin
  Result := False;
  TargetPath := ExpandConstant('{app}\tray\{#TrayExe}');
  Script := '$ErrorActionPreference = ''Stop''; ' +
    '$target = [IO.Path]::GetFullPath(' + PowerShellLiteral(TargetPath) + '); ' +
    'Get-CimInstance Win32_Process -Filter ' +
      PowerShellLiteral('Name = ''{#TrayExe}''') + ' | ' +
    'Where-Object { $_.ExecutablePath -and ' +
      '[string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $target, ' +
      '[StringComparison]::OrdinalIgnoreCase) } | ' +
    'ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }';
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"';
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    ErrorText := 'Setup could not safely inspect or stop the installed tray process. ' +
      'PowerShell exit code: ' + IntToStr(ResultCode);
    Exit;
  end;
  Sleep(500);
  Result := True;
end;

function DeleteRegularFile(Path: String; var ErrorText: String): Boolean;
begin
  Result := False;
  if IsReparsePoint(Path) then
  begin
    ErrorText := 'Setup refused to delete a settings file that is a reparse point:' + #13#10 + Path;
    Exit;
  end;
  if FileExists(Path) and (not DeleteFile(Path)) then
  begin
    ErrorText := 'Setup could not remove the settings file. Close any programs using it:' + #13#10 +
      Path;
    Exit;
  end;
  Result := True;
end;

function DeleteSelectedSettings(var ErrorText: String): Boolean;
begin
  Result := False;
  if DirExists(UserSettingsDirectory()) and IsReparsePoint(UserSettingsDirectory()) then
  begin
    ErrorText := 'Setup refused to delete settings through a reparse point:' + #13#10 +
      UserSettingsDirectory();
    Exit;
  end;
  if not DeleteRegularFile(SettingsPath(), ErrorText) then
    Exit;
  if not DeleteRegularFile(SettingsPath() + '.tmp', ErrorText) then
    Exit;
  if not DeleteRegularFile(UserSettingsPath(), ErrorText) then
    Exit;
  if not DeleteRegularFile(UserSettingsPath() + '.tmp', ErrorText) then
    Exit;
  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  if DirExists(DataPath()) and IsReparsePoint(DataPath()) then
  begin
    SuppressibleMsgBox(
      'Setup cannot use the Disk Activity Monitor data directory because it is a reparse point.' + #13#10 + #13#10 +
      DataPath(), mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  UseExistingSettings := True;
  if FileExists(SettingsPath()) or FileExists(UserSettingsPath()) then
    UseExistingSettings :=
      SuppressibleMsgBox(
        'Existing machine settings or current-account preferences were found.' + #13#10 + #13#10 +
        'Do you want to use these settings?' + #13#10 + #13#10 +
        'Choose Yes to keep your existing values. Settings added by this version ' +
        'will use their new defaults.' + #13#10 + #13#10 +
        'Choose No to start with default settings. Your monitoring history will not be removed.',
        mbConfirmation, MB_YESNO, IDYES) = IDYES;
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ErrorText: String;
begin
  KeepSettingsAfterUninstall := True;
  if FileExists(SettingsPath()) or FileExists(UserSettingsPath()) then
    KeepSettingsAfterUninstall :=
      SuppressibleMsgBox(
        'Do you want to keep your Disk Activity Monitor settings for a future reinstall?' + #13#10 + #13#10 +
        'Choose Yes to keep them, or No to delete the machine settings and ' +
        'preferences for this Windows account.' + #13#10 + #13#10 +
        'Your monitoring history will remain on this computer either way.',
        mbConfirmation, MB_YESNO, IDYES) = IDYES;
  if not StopInstalledTray(ErrorText) then
  begin
    SuppressibleMsgBox(ErrorText, mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  Result := True;
end;

// Stop and remove any prior install of the service and tray so their files unlock before copy.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  if not ValidateInstallDirectory(Result) then
    Exit;
  if not SecureApplicationDirectory(Result) then
    Exit;
  if not SecureDataDirectory(Result) then
    Exit;

  if not StopInstalledTray(Result) then
    Exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1000);
  if not UseExistingSettings then
  begin
    if not DeleteSelectedSettings(Result) then
      Exit;
  end;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ErrorText: String;
begin
  if (CurUninstallStep = usPostUninstall) and (not KeepSettingsAfterUninstall) then
  begin
    if not DeleteSelectedSettings(ErrorText) then
      MsgBox(
        ErrorText + #13#10 + #13#10 +
        'You can remove the files manually after checking their paths.', mbError, MB_OK);
  end;
end;
