; Inno Setup script for CamSwitch. Built by .github/workflows/release.yml:
;   iscc /DAppVersion=1.2.3 /DSourceExe=path\to\CamSwitch.exe installer\CamSwitch.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\CamSwitch.exe"
#endif

[Setup]
AppId={{6F1B4C2E-8A3D-4E57-9C21-CA5E5A17C4E1}
AppName=CamSwitch
AppVersion={#AppVersion}
AppVerName=CamSwitch {#AppVersion}
AppPublisher=pikkon38
AppPublisherURL=https://github.com/pikkon38/CamSwitcher
AppSupportURL=https://github.com/pikkon38/CamSwitcher/issues
AppUpdatesURL=https://github.com/pikkon38/CamSwitcher/releases
DefaultDirName={autopf}\CamSwitch
DefaultGroupName=CamSwitch
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=CamSwitch-Setup-{#AppVersion}
SetupIconFile=..\src\CamSwitch\Assets\app.ico
UninstallDisplayIcon={app}\CamSwitch.exe
UninstallDisplayName=CamSwitch
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CamSwitch"; Filename: "{app}\CamSwitch.exe"

[Run]
; Start-at-sign-in task (runs elevated without a prompt).
Filename: "{app}\CamSwitch.exe"; Parameters: "--install-task"; Flags: runhidden waituntilterminated; StatusMsg: "Setting CamSwitch to start when you sign in..."
; Start it: open the window after a normal install, stay in the tray after a silent update.
Filename: "{app}\CamSwitch.exe"; Description: "Open CamSwitch"; Flags: postinstall nowait skipifsilent
Filename: "{app}\CamSwitch.exe"; Parameters: "--background"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\CamSwitch.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "Quit"
Filename: "{app}\CamSwitch.exe"; Parameters: "--restore-cameras"; Flags: runhidden waituntilterminated; RunOnceId: "Restore"
Filename: "{app}\CamSwitch.exe"; Parameters: "--remove-task"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[Code]
// Close the running copy before replacing it; it turns both cameras back on as it exits.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\CamSwitch.exe');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM CamSwitch.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
