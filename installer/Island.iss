; Build: dotnet publish first (see README), then: iscc installer\Island.iss
#define AppName "Island"
#define AppVersion "1.0.0"
#define AppExe "Island.exe"

[Setup]
AppId={{B6E2D1A4-3F7C-4E8A-9C5D-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
; Per-user install into %LocalAppData%\Programs: no admin prompt.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Same name as App.xaml.cs: setup/uninstall ask the user to exit Island first.
AppMutex=Island.SingleInstance.6F1B3C2E
OutputDir=..\dist
OutputBaseFilename=IslandSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\Island.ico

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut for {#AppName} Settings"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} Settings"; Filename: "{app}\{#AppExe}"; Parameters: "--settings"; IconFilename: "{app}\Assets\IslandSettings.ico"
Name: "{autodesktop}\{#AppName} Settings"; Filename: "{app}\{#AppExe}"; Parameters: "--settings"; IconFilename: "{app}\Assets\IslandSettings.ico"; Tasks: desktopicon

[Registry]
; Created by the app's "Start with Windows" setting; ValueType none = only removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Island"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Island"
