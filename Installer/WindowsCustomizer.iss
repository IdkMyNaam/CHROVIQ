; Windows Customizer setup (Inno Setup 6). Built by Scripts\Build-Installer.ps1, which publishes the app first
; and passes the version:  iscc /DAppVersion=1.0.0 Installer\WindowsCustomizer.iss
;
; Installs for the current user only (no admin needed) in %LocalAppData%\Programs\Windows Customizer.
; The uninstaller first turns shell mode off and brings the Windows taskbar back, then asks whether to keep
; your settings (%AppData%\WindowsCustomizer).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "CHROVIQ"
#define AppExe "WindowsCustomizer.exe"
#define PublishDir "..\bin\publish"

[Setup]
; A fixed id: an update installs over the old version instead of next to it. Never change it.
AppId={{7C3E8A52-4B1D-4E7F-9A61-2D5C9B0E4F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\bin\installer
OutputBaseFilename=CHROVIQ-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Close a running Windows Customizer before files are replaced (and start it again afterwards).
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start CHROVIQ when I sign in"; GroupDescription: "Starting:"
Name: "desktopicon"; Description: "A shortcut on the desktop"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Start with Windows (for this user); removed by the uninstaller.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start CHROVIQ now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 1. Close the app (it can't clean up itself during an uninstall).
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "CloseApp"
; 2. Explorer as the shell again, if "Use as shell" was on (does nothing otherwise).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Restore-Shell.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RestoreShell"
; 3. The Windows taskbar and tray icons back.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Restore-Taskbar.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RestoreTaskbar"

[Code]
// After the files are gone: keep or remove the settings, profiles, themes and designs.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Settings := ExpandConstant('{userappdata}\WindowsCustomizer');
    if DirExists(Settings) and not UninstallSilent then
      if MsgBox('Also remove your CHROVIQ settings (profiles, themes, taskbar designs, widgets, backups)?' + #13#10 + #13#10 +
                'Choose No to keep them, for example to install it again later.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Settings, True, True, True);
  end;
end;
