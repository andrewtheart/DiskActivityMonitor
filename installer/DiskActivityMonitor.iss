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
  InstallPreparationStepCount = 16;

var
  UseExistingSettings: Boolean;
  KeepExistingDatabase: Boolean;
  KeepSettingsAfterUninstall: Boolean;
  InstallProgressPage: TOutputProgressWizardPage;
  InstallProgressPosition: Integer;

type
  TDamSystemTime = record
    Year: Word;
    Month: Word;
    DayOfWeek: Word;
    Day: Word;
    Hour: Word;
    Minute: Word;
    Second: Word;
    Milliseconds: Word;
  end;

function GetFileAttributes(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function FileTimeToLocalFileTime(var FileTime: TFileTime; var LocalFileTime: TFileTime): Boolean;
  external 'FileTimeToLocalFileTime@kernel32.dll stdcall';

function FileTimeToSystemTime(var FileTime: TFileTime; var SystemTime: TDamSystemTime): Boolean;
  external 'FileTimeToSystemTime@kernel32.dll stdcall';

procedure InitializeWizard;
begin
  InstallProgressPage := CreateOutputProgressPage(
    'Preparing Disk Activity Monitor',
    'Setup is securing folders and replacing the existing installation.');
end;

procedure BeginInstallPreparation;
begin
  InstallProgressPosition := 0;
  if not WizardSilent then
  begin
    InstallProgressPage.SetText('Checking the installation target...', '');
    InstallProgressPage.SetProgress(0, InstallPreparationStepCount);
    InstallProgressPage.Show;
  end;
end;

procedure SetInstallPreparationStatus(Status: String; Detail: String);
begin
  if not WizardSilent then
  begin
    InstallProgressPage.SetText(Status, Detail);
    InstallProgressPage.SetProgress(InstallProgressPosition, InstallPreparationStepCount);
  end;
end;

procedure CompleteInstallPreparationStep;
begin
  InstallProgressPosition := InstallProgressPosition + 1;
  if not WizardSilent then
    InstallProgressPage.SetProgress(InstallProgressPosition, InstallPreparationStepCount);
end;

procedure FinishInstallPreparation;
begin
  if not WizardSilent then
    InstallProgressPage.Hide;
end;

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

function AiSecretsPath(): String;
begin
  Result := AddBackslash(UserSettingsDirectory()) + 'ai-secrets.json';
end;

function DatabasePath(): String;
begin
  Result := AddBackslash(DataPath()) + 'diskactivity.db';
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

function PadTwoDigits(Value: Integer): String;
begin
  Result := IntToStr(Value);
  if Length(Result) < 2 then
    Result := '0' + Result;
end;

function FormatByteSize(Value: Int64): String;
var
  Scale: Int64;
  Suffix: String;
  Fraction: Integer;
begin
  if Value >= 1073741824 then
  begin
    Scale := 1073741824;
    Suffix := ' GB';
  end
  else if Value >= 1048576 then
  begin
    Scale := 1048576;
    Suffix := ' MB';
  end
  else if Value >= 1024 then
  begin
    Scale := 1024;
    Suffix := ' KB';
  end
  else
  begin
    Result := IntToStr(Value) + ' bytes';
    Exit;
  end;

  Fraction := ((Value mod Scale) * 100) div Scale;
  Result := IntToStr(Value div Scale) + '.' + PadTwoDigits(Fraction) + Suffix;
end;

function DescribeFileTime(var Value: TFileTime): String;
var
  LocalValue: TFileTime;
  Parts: TDamSystemTime;
begin
  Result := '';
  if not FileTimeToLocalFileTime(Value, LocalValue) then
    Exit;
  if not FileTimeToSystemTime(LocalValue, Parts) then
    Exit;
  Result := IntToStr(Parts.Year) + '-' + PadTwoDigits(Parts.Month) + '-' + PadTwoDigits(Parts.Day) +
    ' ' + PadTwoDigits(Parts.Hour) + ':' + PadTwoDigits(Parts.Minute);
end;

// Size and dates make the keep-or-replace choice concrete instead of a blind guess.
function DescribeExistingDatabase(): String;
var
  FindRec: TFindRec;
  Size: Int64;
  Created: String;
  Updated: String;
begin
  Result := DatabasePath();
  try
    if FileSize64(DatabasePath(), Size) then
      Result := Result + #13#10 + 'Size: ' + FormatByteSize(Size);

    if FindFirst(DatabasePath(), FindRec) then
    begin
      try
        Created := DescribeFileTime(FindRec.CreationTime);
        Updated := DescribeFileTime(FindRec.LastWriteTime);
        if Created <> '' then
          Result := Result + #13#10 + 'Collecting since: ' + Created;
        if Updated <> '' then
          Result := Result + #13#10 + 'Last updated: ' + Updated;
      finally
        FindClose(FindRec);
      end;
    end;
  except
    // The details are a convenience; never block setup because one could not be read.
  end;
end;

// Replacing history is destructive, so the old database is renamed rather than deleted.
function ArchiveExistingDatabase(var ErrorText: String): Boolean;
var
  ArchiveBase: String;
begin
  Result := True;
  if not FileExists(DatabasePath()) then
    Exit;

  Result := False;
  if IsReparsePoint(DataPath()) or IsReparsePoint(DatabasePath()) then
  begin
    ErrorText := 'Setup refused to replace a database reached through a reparse point:' + #13#10 +
      DatabasePath();
    Exit;
  end;

  ArchiveBase := AddBackslash(DataPath()) + 'diskactivity-replaced-' +
    GetDateTimeString('yyyymmdd-hhnnss', '-', '-');

  if not RenameFile(DatabasePath(), ArchiveBase + '.db') then
  begin
    ErrorText := 'Setup could not rename the existing database:' + #13#10 + DatabasePath() + #13#10 + #13#10 +
      'Close anything using it (including the tray dashboard) and run setup again.';
    Exit;
  end;

  // The write-ahead log and shared-memory files belong to that database, not the new one.
  if FileExists(DatabasePath() + '-wal') then
    RenameFile(DatabasePath() + '-wal', ArchiveBase + '.db-wal');
  if FileExists(DatabasePath() + '-shm') then
    RenameFile(DatabasePath() + '-shm', ArchiveBase + '.db-shm');

  Result := True;
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
  ErrorFile: String;
  HelperDetail: AnsiString;
begin
  Result := False;
  try
    ExtractTemporaryFile('secure-directory.ps1');
  except
    ErrorText := 'Setup could not extract its directory security helper: ' +
      GetExceptionMessage();
    Exit;
  end;
  ErrorFile := ExpandConstant('{tmp}\dam-security-error.txt');
  DeleteFile(ErrorFile);
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%s" -Path "%s" -Profile %s -ErrorFile "%s"';
  Parameters := Format(Parameters, [ExpandConstant('{tmp}\secure-directory.ps1'), Path, Profile, ErrorFile]);
  ResultCode := -1;
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Parameters, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    ErrorText := 'Setup could not validate or secure this directory without following ' +
      'reparse points:' + #13#10 + Path + #13#10 +
      'Security helper exit code: ' + IntToStr(ResultCode);
    if LoadStringFromFile(ErrorFile, HelperDetail) then
      ErrorText := ErrorText + #13#10 + #13#10 + String(HelperDetail);
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

  SetInstallPreparationStatus('Checking application folders...', AppPath);
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
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Validating the Program Files directory...', ExpandConstant('{autopf}'));
  if not RunDirectorySecurity(ExpandConstant('{autopf}'), 'Validate', ErrorText) then
    Exit;
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Securing the application directory...', AppPath);
  if not RunDirectorySecurity(AppPath, 'Install', ErrorText) then
    Exit;
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Securing the service directory...', ServicePath);
  if DirExists(ServicePath) and
     (not RunDirectorySecurity(ServicePath, 'Install', ErrorText)) then
    Exit;
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Securing the tray directory...', TrayPath);
  if DirExists(TrayPath) and
     (not RunDirectorySecurity(TrayPath, 'Install', ErrorText)) then
    Exit;
  CompleteInstallPreparationStep;
  Result := True;
end;

function SecureDataDirectory(var ErrorText: String): Boolean;
begin
  Result := False;
  SetInstallPreparationStatus('Checking the monitoring data directory...', DataPath());
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
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Validating the ProgramData directory...', ExpandConstant('{commonappdata}'));
  if not RunDirectorySecurity(ExpandConstant('{commonappdata}'), 'Validate', ErrorText) then
    Exit;
  CompleteInstallPreparationStep;

  SetInstallPreparationStatus('Securing the monitoring data directory...', DataPath());
  if not RunDirectorySecurity(DataPath(), 'Data', ErrorText) then
    Exit;
  CompleteInstallPreparationStep;
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
  if not DeleteRegularFile(AiSecretsPath(), ErrorText) then
    Exit;
  if not DeleteRegularFile(AiSecretsPath() + '.tmp', ErrorText) then
    Exit;
  Result := True;
end;

function CommandLineHasSwitch(const Switch: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Switch) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

// The form height must be known before the form exists, so wrap the text on a throwaway form.
procedure MeasureRetentionText(const Instruction, Body: String; ContentWidth: Integer;
  var TitleHeight, BodyHeight: Integer);
var
  Form: TSetupForm;
  Title: TNewStaticText;
  Details: TNewStaticText;
begin
  Form := CreateCustomForm(ContentWidth, ScaleY(10), True, True);
  try
    Title := TNewStaticText.Create(Form);
    Title.Parent := Form;
    Title.Width := ContentWidth;
    Title.WordWrap := True;
    Title.Font.Style := [fsBold];
    Title.Caption := Instruction;
    Title.AdjustHeight;
    TitleHeight := Title.Height;

    Details := TNewStaticText.Create(Form);
    Details.Parent := Form;
    Details.Width := ContentWidth;
    Details.WordWrap := True;
    Details.Caption := Body;
    Details.AdjustHeight;
    BodyHeight := Details.Height;
  finally
    Form.Free();
  end;
end;

const
  KeepButtonColor = $D47800;

var
  RetentionForm: TSetupForm;
  RetentionDeleteButton: TNewButton;
  RetentionKeepPanel: TPanel;

procedure RetentionKeepClick(Sender: TObject);
begin
  // Closing yields mrCancel, which the caller treats as keep.
  RetentionForm.Close;
end;

procedure RetentionKeyDown(Sender: TObject; var Key: Word; Shift: TShiftState);
begin
  // Enter and Esc keep, unless the delete button explicitly has focus.
  if ((Key = 13) and (RetentionForm.ActiveControl <> RetentionDeleteButton)) or (Key = 27) then
  begin
    Key := 0;
    RetentionForm.Close;
  end;
end;

// Keeping is the safe answer, so it is the rightmost button and the accent-coloured one.
function AskKeepExistingData(const Instruction, Body: String): Boolean;
var
  Title: TNewStaticText;
  Details: TNewStaticText;
  FormWidth, ContentLeft, ContentWidth: Integer;
  TitleHeight, BodyHeight, ButtonTop, ButtonWidth, ButtonHeight: Integer;
begin
  // An unattended install must never block on this prompt.
  if WizardSilent and CommandLineHasSwitch('/SUPPRESSMSGBOXES') then
  begin
    Result := True;
    Exit;
  end;

  FormWidth := ScaleX(560);
  ContentLeft := ScaleX(24);
  ContentWidth := FormWidth - (2 * ContentLeft);
  MeasureRetentionText(Instruction, Body, ContentWidth, TitleHeight, BodyHeight);
  ButtonTop := ScaleY(22) + TitleHeight + ScaleY(14) + BodyHeight + ScaleY(24);
  ButtonHeight := ScaleY(25);

  RetentionForm := CreateCustomForm(FormWidth, ButtonTop + ButtonHeight + ScaleY(22), True, True);
  try
    RetentionForm.Caption := '{#AppName}';
    RetentionForm.KeyPreview := True;
    RetentionForm.OnKeyDown := @RetentionKeyDown;

    Title := TNewStaticText.Create(RetentionForm);
    Title.Parent := RetentionForm;
    Title.Left := ContentLeft;
    Title.Top := ScaleY(22);
    Title.Width := ContentWidth;
    Title.WordWrap := True;
    Title.Font.Style := [fsBold];
    Title.Caption := Instruction;
    Title.AdjustHeight;

    Details := TNewStaticText.Create(RetentionForm);
    Details.Parent := RetentionForm;
    Details.Left := ContentLeft;
    Details.Top := Title.Top + Title.Height + ScaleY(14);
    Details.Width := ContentWidth;
    Details.WordWrap := True;
    Details.Caption := Body;
    Details.AdjustHeight;

    RetentionDeleteButton := TNewButton.Create(RetentionForm);
    RetentionDeleteButton.Parent := RetentionForm;
    RetentionDeleteButton.Caption := '&Delete existing data';
    RetentionDeleteButton.ModalResult := mrNo;

    ButtonWidth := RetentionForm.CalculateButtonWidth(['&Delete existing data', 'Keep existing data']);

    // A themed push button ignores Color/Font.Color, so the accent button is a painted panel.
    RetentionKeepPanel := TPanel.Create(RetentionForm);
    RetentionKeepPanel.Parent := RetentionForm;
    RetentionKeepPanel.Caption := 'Keep existing data';
    RetentionKeepPanel.BevelOuter := bvNone;
    RetentionKeepPanel.ParentBackground := False;
    RetentionKeepPanel.Color := KeepButtonColor;
    RetentionKeepPanel.Font.Color := clWhite;
    RetentionKeepPanel.Cursor := crHand;
    RetentionKeepPanel.OnClick := @RetentionKeepClick;
    RetentionKeepPanel.Width := ButtonWidth;
    RetentionKeepPanel.Height := ButtonHeight;
    RetentionKeepPanel.Top := ButtonTop;
    RetentionKeepPanel.Left := RetentionForm.ClientWidth - ContentLeft - ButtonWidth;

    RetentionDeleteButton.Width := ButtonWidth;
    RetentionDeleteButton.Height := ButtonHeight;
    RetentionDeleteButton.Top := RetentionKeepPanel.Top;
    RetentionDeleteButton.Left := RetentionKeepPanel.Left - ScaleX(8) - ButtonWidth;

    // Only the delete button returns mrNo; Esc and the title-bar X therefore keep.
    Result := RetentionForm.ShowModal() <> mrNo;
  finally
    RetentionForm.Free();
  end;
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
  if FileExists(SettingsPath()) or FileExists(UserSettingsPath()) or FileExists(AiSecretsPath()) then
    UseExistingSettings :=
      AskKeepExistingData(
        'Settings from a previous installation were found',
        'Machine settings, current-account preferences, or API credentials are already present.' + #13#10 + #13#10 +
        'Keep existing data reuses your saved values. Settings added by this version use their ' +
        'new defaults.' + #13#10 + #13#10 +
        'Delete existing data starts from default settings and will remove saved API credentials. ' +
        'Your monitoring history is not affected by this choice.');

  KeepExistingDatabase := True;
  if FileExists(DatabasePath()) then
    KeepExistingDatabase :=
      AskKeepExistingData(
        'A monitoring database from a previous installation was found',
        DescribeExistingDatabase() + #13#10 + #13#10 +
        'Keep existing data continues adding to this history, so your trends, endurance ' +
        'history, and alert log carry over.' + #13#10 + #13#10 +
        'Delete existing data starts a new, empty database. The existing file is renamed with ' +
        'a timestamp beside it so you can inspect or remove it yourself; setup does not erase ' +
        'your history.');
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ErrorText: String;
begin
  KeepSettingsAfterUninstall := True;
  if FileExists(SettingsPath()) or FileExists(UserSettingsPath()) or FileExists(AiSecretsPath()) then
    KeepSettingsAfterUninstall :=
      SuppressibleMsgBox(
        'Do you want to keep your Disk Activity Monitor settings for a future reinstall?' + #13#10 + #13#10 +
        'Choose Yes to keep them, or No to delete the machine settings, ' +
        'preferences, and saved API credentials for this Windows account.' + #13#10 + #13#10 +
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
function ServiceExists(): Boolean;
var
  rc: Integer;
begin
  // sc.exe returns 1060 (ERROR_SERVICE_DOES_NOT_EXIST) once the registration is really gone.
  Result := False;
  if Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Result := rc <> 1060;
end;

function WaitForServiceRemoval(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while (Waited < TimeoutMs) and ServiceExists() do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  Result := not ServiceExists();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Result := '';
  BeginInstallPreparation;
  try
    SetInstallPreparationStatus('Validating the installation target...', ExpandConstant('{app}'));
    if not ValidateInstallDirectory(Result) then
      Exit;
    CompleteInstallPreparationStep;

    if not SecureApplicationDirectory(Result) then
      Exit;
    if not SecureDataDirectory(Result) then
      Exit;

    SetInstallPreparationStatus('Closing the existing tray application...', '');
    if not StopInstalledTray(Result) then
      Exit;
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Stopping the collector service...', '');
    if ServiceExists() then
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Waiting for the collector service to stop...', '');
    Sleep(1500);
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Removing the previous collector service...', '');
    if ServiceExists() then
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Waiting for service removal to complete...', '');
    if not WaitForServiceRemoval(30000) then
    begin
      Result := 'Setup could not unregister the existing "{#ServiceDisplay}" service.' + #13#10 +
        'A deletion stays pending while something still holds the service open.' + #13#10 + #13#10 +
        'Close the Services console (services.msc) and Task Manager, then run setup again.';
      Exit;
    end;
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Applying your settings choice...', '');
    if not UseExistingSettings then
    begin
      if not DeleteSelectedSettings(Result) then
        Exit;
    end;
    CompleteInstallPreparationStep;

    SetInstallPreparationStatus('Applying your database choice...', '');
    if not KeepExistingDatabase then
    begin
      if not ArchiveExistingDatabase(Result) then
        Exit;
    end;
    CompleteInstallPreparationStep;
  finally
    FinishInstallPreparation;
  end;
  if Result <> '' then
  begin
    Exit;
  end;
end;

// [Run] re-registers the service; confirm it actually came back rather than failing silently.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and (not ServiceExists()) then
    MsgBox(
      'Setup finished copying files, but the "{#ServiceDisplay}" service is not registered.' + #13#10 + #13#10 +
      'Collection will not run until it exists. Run setup again, or register it manually with:' + #13#10 +
      'sc create {#ServiceName} binPath= "' + ExpandConstant('{app}\service\{#ServiceExe}') +
      '" start= auto obj= LocalSystem', mbError, MB_OK);
end;

// Setup unregisters the old service before copying files. If the install then aborts (a locked
// file, for example), re-register whatever service binary is still on disk so the machine is not
// left without a collector.
procedure DeinitializeSetup();
var
  ServicePath: String;
  rc: Integer;
begin
  ServicePath := ExpandConstant('{app}\service\{#ServiceExe}');
  if ServiceExists() or (not FileExists(ServicePath)) then
    Exit;

  Exec(ExpandConstant('{sys}\sc.exe'),
    'create {#ServiceName} binPath= "' + ServicePath + '" start= auto obj= LocalSystem DisplayName= "{#ServiceDisplay}"',
    '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
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
