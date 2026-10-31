; Otter Monitor installer. Build with installer.ps1, which publishes the app first and passes AppVersion.
; Installs for the current user only: no administrator prompt for installs or updates.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "..\app"
#endif

[Setup]
; Never change AppId: it is how updates and the uninstaller find an existing installation.
AppId={{11E1AA95-0783-4496-81EC-975756CEBE2E}
AppName=Otter Monitor
AppVersion={#AppVersion}
AppVerName=Otter Monitor {#AppVersion}
AppPublisher=aerksn
AppPublisherURL=https://github.com/aerksn/DevGeek
AppComments=Lokala utvecklingsportar, snabbstarter och kodassistenternas kvoter
PrivilegesRequired=lowest
DefaultDirName={autopf}\Otter Monitor
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=OtterMonitor-Setup-{#AppVersion}
SetupIconFile=..\src\OtterMonitor.App\otter-monitor.ico
UninstallDisplayIcon={app}\OtterMonitor.exe
UninstallDisplayName=Otter Monitor
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app quits itself through --exit (see [Code]); Restart Manager is only the fallback.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
swedish.AutoStart=Starta Otter Monitor när jag loggar in (i systemfältet)
english.AutoStart=Start Otter Monitor when I sign in (in the notification area)
swedish.LaunchNow=Starta Otter Monitor
english.LaunchNow=Launch Otter Monitor

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\OtterMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSourceDir}\BricolageGrotesque-OFL.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Otter Monitor"; Filename: "{app}\OtterMonitor.exe"; Comment: "Lokala utvecklingsportar och snabbstarter"
Name: "{autodesktop}\Otter Monitor"; Filename: "{app}\OtterMonitor.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Otter Monitor"; ValueData: """{app}\OtterMonitor.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\OtterMonitor.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quit a running copy so its exe can be removed. Started quick starts keep running, as on every exit.
Filename: "{app}\OtterMonitor.exe"; Parameters: "--exit"; RunOnceId: "ExitOtterMonitor"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
// An update must replace the exe of a running Otter Monitor, which hides to the tray instead of closing.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\OtterMonitor.exe')) then
    Exec(ExpandConstant('{app}\OtterMonitor.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Turning the task off in an update must also remove an earlier sign-in entry.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Otter Monitor');
end;
