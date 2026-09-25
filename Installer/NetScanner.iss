; Inno Setup script for the NetScanner desktop app.
; Built by Scripts/build-release.ps1, which passes:
;   /DAppVersion=1.0.0  /DSourceDir=<self-contained publish folder>  /DOutputDir=<artifacts>
;
; Per-user install (no admin prompt) into %LOCALAPPDATA%\Programs\NetScanner,
; with a Start menu entry and an uninstaller in Settings > Apps. The app is
; published self-contained (.NET + Windows App SDK), so there are no
; prerequisites to install.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (see Scripts/build-release.ps1)
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder> (see Scripts/build-release.ps1)
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "NetScanner"
#define AppExe "NetScannerDesktop.exe"
#define AppUrl "https://github.com/Criseda/NetScannerDesktop"

[Setup]
; Never change AppId: it is how upgrades find the existing install.
AppId={{1EBB780C-4C78-4EB4-AABA-B2A6228307E6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Criseda
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE.txt
SetupIconFile=..\NetScannerDesktop\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=NetScanner-Setup-{#AppVersion}-x64
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
; Close a running NetScanner before replacing its files on upgrade.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades replace the whole app folder, so files dropped by a newer
; build never linger next to the new ones.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings, scan history and any self-updated engine live in
; %LOCALAPPDATA%\NetScanner and are kept on uninstall, like most apps.
