; Per-user installer for SaveCloud (no admin rights needed). Built by .github/workflows/build.yml.
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

[Setup]
AppId={{6B1F3C3E-5A7B-4C1E-9E61-3D2C8B7A9F10}
AppName=SaveCloud
AppVersion={#AppVersion}
AppPublisher=SaveCloud
DefaultDirName={localappdata}\Programs\SaveCloud
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
OutputDir=output
OutputBaseFilename=SaveCloudSetup
SetupIconFile=..\SaveCloud\app.ico
UninstallDisplayIcon={app}\SaveCloud.exe
UninstallDisplayName=SaveCloud
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running SaveCloud during upgrades and starts it again afterwards.
CloseApplications=force
RestartApplications=no

[Files]
Source: "..\SaveCloud\bin\Release\net481\*"; DestDir: "{app}"; Excludes: "*.pdb,Microsoft.Web.WebView2.Wpf.dll"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SaveCloud"; Filename: "{app}\SaveCloud.exe"

[Registry]
; The app adds itself to sign-in startup; remove that entry on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SaveCloud"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\SaveCloud.exe"; Description: "Start SaveCloud"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im SaveCloud.exe"; Flags: runhidden; RunOnceId: "StopSaveCloud"
