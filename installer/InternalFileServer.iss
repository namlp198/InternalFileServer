#define MyAppName "Internal File Server"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "HP3"
#define MyAppExeName "InternalFileServer.exe"

#ifndef SourceDir
#define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{C3D3F599-5A2B-4B8C-8BE0-9ED6A05B4F7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\InternalFileServer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=InternalFileServerSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Internal File Server when Windows starts"; GroupDescription: "Startup:"; Flags: checkedonce

[Dirs]
Name: "{app}\uploads"; Permissions: users-modify
Name: "{app}\data"; Permissions: users-modify

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Internal File Server"; Filename: "{app}\start-server.bat"; WorkingDir: "{app}"
Name: "{group}\Start Internal File Server in Background"; Filename: "{app}\start-server-hidden.vbs"; WorkingDir: "{app}"
Name: "{group}\Uploads Folder"; Filename: "{app}\uploads"
Name: "{group}\Open Web UI"; Filename: "http://localhost:5088"
Name: "{autodesktop}\Internal File Server"; Filename: "{app}\start-server.bat"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{commonstartup}\Internal File Server"; Filename: "{app}\start-server-hidden.vbs"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\start-server.bat"; Description: "Start Internal File Server now"; Flags: postinstall nowait skipifsilent unchecked
