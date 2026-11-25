#ifndef PassloadVersion
  #error PassloadVersion must be defined
#endif
#ifndef PassloadBinary
  #error PassloadBinary must be defined
#endif

[Setup]
AppId=Mertioz.Passload
AppName=Passload
AppVersion={#PassloadVersion}
AppPublisher=mertioz
AppPublisherURL=https://github.com/mertioz/passload
AppSupportURL=https://github.com/mertioz/passload/issues
AppUpdatesURL=https://github.com/mertioz/passload/releases
DefaultDirName={localappdata}\Programs\Passload
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
UninstallDisplayName=Passload
UninstallDisplayIcon={app}\Passload.exe
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\src\Passload\Assets\Passload.ico
OutputBaseFilename=Passload-Setup
OutputDir=..\..\artifacts\installers
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PassloadBinary}"; DestDir: "{app}"; DestName: "Passload.exe"
Source: "..\..\LICENSE"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\Passload"; Filename: "{app}\Passload.exe"; WorkingDir: "{app}"
