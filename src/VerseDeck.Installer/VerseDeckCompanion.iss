#define MyAppName "VerseDeck Companion"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Zowix"
#define MyAppExeName "VerseDeck.App.exe"
#define MyAppSource "..\..\artifacts\publish\win-x64\*"

[Setup]
AppId={{C9A21D37-7D50-4F62-8D5B-FE16D23B49E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DefaultDirName={localappdata}\Programs\VerseDeck Companion
DefaultGroupName=VerseDeck Companion
AllowNoIcons=yes
OutputDir=..\..\artifacts\installer
OutputBaseFilename=VerseDeckCompanionSetup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
CloseApplications=yes
CloseApplicationsFilter=VerseDeck.App.exe
RestartApplications=no
RestartIfNeededByRun=no
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos"; Flags: unchecked
Name: "startup"; Description: "Iniciar con Windows"; GroupDescription: "Inicio"; Flags: unchecked

[Files]
Source: "{#MyAppSource}"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VerseDeck Companion"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\VerseDeck Companion"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VerseDeck Companion"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "VerseDeck Companion"; Flags: deletevalue; Tasks: not startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar VerseDeck Companion"; Flags: nowait postinstall skipifsilent
