; All runtime dependencies are included in PackageDir before compiling.
#ifndef PackageDir
  #error Publish the app, then run scripts\Build-Installer.ps1.
#endif
#ifndef PackageVersion
  #error PackageVersion is required.
#endif
#ifndef PackageFileVersion
  #error PackageFileVersion is required.
#endif

[Setup]
AppId={{8B229946-6D62-47D3-B682-7D5BFC20B7D0}
AppName=Repo Backup
AppVersion={#PackageVersion}
AppPublisher=c0gen
AppPublisherURL=https://github.com/c0gen/Repo-Backup
AppSupportURL=https://github.com/c0gen/Repo-Backup/issues
AppUpdatesURL=https://github.com/c0gen/Repo-Backup/releases
VersionInfoVersion={#PackageFileVersion}
VersionInfoProductVersion={#PackageVersion}
DefaultDirName={localappdata}\Programs\RepoBackup
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=RepoBackup-{#PackageVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\RepoBackup.Desktop\Assets\RepoBackup.ico
UninstallDisplayIcon={app}\RepoBackup.exe
AppMutex=Local\RepoBackup.InUse
CloseApplications=no
RestartApplications=no
LicenseFile={#PackageDir}\LICENSE
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Repo Backup"; Filename: "{app}\RepoBackup.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Repo Backup"; Filename: "{app}\RepoBackup.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RepoBackup.exe"; Description: "Open Repo Backup"; Flags: nowait postinstall skipifsilent

; The catalog, DPAPI credentials, cache, and backup repositories live outside
; {app}. Inno Setup removes only installed files and its own shortcuts.
