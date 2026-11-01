#define AppName "Open-Revo"
#ifndef AppVersion
#define AppVersion "0.9.7"
#endif
[Setup]
SetupArchitecture=x86
AppId={{8626505B-AD8A-45AA-B1EB-8AB75B60524A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Open-Revo Native Community Build
DefaultDirName={localappdata}\Programs\OpenRevo
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..
OutputBaseFilename=Open-Revo-{#AppVersion}-Setup-x64
SetupIconFile=src\Branding\App.ico
UninstallDisplayIcon={app}\OpenRevo.Native.exe
AppMutex=Local\OpenRevo.Native.InstalledPreview
LicenseFile=LICENSE
InfoBeforeFile=INSTALL-NOTES.txt
#ifdef CompactPackage
Compression=lzma2/ultra64
#else
Compression=lzma2
#endif
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "installer/InstallerFolderFilter.dll"; Flags: dontcopy
Source: "app-{#AppVersion}-final\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "diagnostics\*,layout-review\*,*.pdb,ui-review.json,ui-caption-steps.txt,caption-error.txt,overview-layout.json,overview-core-readings.json"
Source: "LICENSE"; DestDir: "{app}"
Source: "INSTALL-NOTES.txt"; DestDir: "{app}"
Source: "native-source-{#AppVersion}.zip"; DestDir: "{app}\source"; DestName: "native-source.zip"
Source: "RELEASE-{#AppVersion}.md"; DestDir: "{app}"
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\OpenRevo.Native.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\OpenRevo.Native.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\OpenRevo.Native.exe"; Description: "Launch {#AppName}"; Verb: "runas"; Flags: shellexec nowait postinstall skipifsilent unchecked
[UninstallRun]
Filename: "{app}\OpenRevo.Native.exe"; Parameters: "--startup-remove"; Verb: "runas"; Flags: shellexec waituntilterminated runhidden skipifdoesntexist; RunOnceId: "OpenRevoNativeStartupCleanup"
#include "legacy-files.iss"
[Code]
var
  FreshInstall: Boolean;
#include "InstallerDirectoryPicker.iss"
procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyCommand: String;
  MaintenanceCommand: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    FreshInstall := not FileExists(ExpandConstant('{app}\OpenRevo.Native.exe'));
  if CurStep = ssPostInstall then
  begin
    MaintenanceCommand := '';
    if FreshInstall then
      MaintenanceCommand := '--startup-enable'
    else if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OpenRevoNative', LegacyCommand) and
      (CompareText(LegacyCommand, '"' + ExpandConstant('{app}\OpenRevo.Native.exe') + '" --startup') = 0) then
      MaintenanceCommand := '--startup-migrate';
    if MaintenanceCommand <> '' then
      if (not ShellExec('runas', ExpandConstant('{app}\OpenRevo.Native.exe'), MaintenanceCommand, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        MsgBox('Startup setup was not completed. Open the app and configure startup in Settings.', mbInformation, MB_OK);
  end;
end;

