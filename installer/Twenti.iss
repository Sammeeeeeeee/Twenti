; Twenti — Inno Setup 6 script
; Wraps the dotnet publish output into a Win11-native installer.

#define AppName        "Twenti"
#define AppVersion     "1.0.0"
#define AppPublisher   "Twenti"
#define AppExe         "Twenti.exe"
#define PublishDir     "..\publish"

[Setup]
AppId={{B6F8C0AB-5A3D-4A6E-93C2-7AC3D9F8E120}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppMutex=Twenti.SingleInstance.{{6c3b2a91-4fa2-4e7b-9d8c-twentiapp}}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=Twenti-Setup
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupLogging=yes
CloseApplications=yes
RestartApplications=yes
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";  Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts"; Flags: unchecked
Name: "autostart";    Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Twenti.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; \
  Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
