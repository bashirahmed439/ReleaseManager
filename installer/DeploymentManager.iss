#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Deployment Manager"
#define AppExeName "DeploymentManager.exe"

[Setup]
AppId={{83E32692-7412-4B6E-99CA-8F4840FE1E26}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Deployment Manager
DefaultDirName={localappdata}\Programs\Deployment Manager
DefaultGroupName=Deployment Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=DeploymentManager-Setup-{#AppVersion}-x64
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Deployment Manager"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Deployment Manager"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Deployment Manager"; Flags: postinstall nowait skipifsilent