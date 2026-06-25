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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"
Name: "{group}\Open data folder"; Filename: "{commonappdata}\{#ServiceName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\tray\{#TrayExe}"; WorkingDir: "{app}\tray"; IconFilename: "{app}\app.ico"; Tasks: autostart

[Run]
; Ensure the machine-wide data directory exists and is writable by standard users (the tray
; runs per-user and must be able to save config.json even though the service runs as SYSTEM).
Filename: "{cmd}"; Parameters: "/c mkdir ""{commonappdata}\{#ServiceName}"" 2> nul & icacls ""{commonappdata}\{#ServiceName}"" /grant *S-1-5-32-545:(OI)(CI)M /T /C"; Flags: runhidden waituntilterminated; StatusMsg: "Preparing data folder..."
; Register and start the Windows service (LocalSystem => runs elevated, shows in services.msc).
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\service\{#ServiceExe}"" start= auto obj= LocalSystem DisplayName= ""{#ServiceDisplay}"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering Windows service..."
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Collects SSD/HDD read-write trends to protect drive endurance."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting service..."
; Launch the tray dashboard at the end of setup.
Filename: "{app}\tray\{#TrayExe}"; Parameters: "--show"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#TrayExe} /F"; Flags: runhidden waituntilterminated; RunOnceId: "KillTray"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteSvc"

[Code]
// Stop and remove any prior install of the service and tray so their files unlock before copy.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#TrayExe} /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1000);
  Result := '';
end;
