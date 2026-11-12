#define AppName "Open-Revo"
#define SetupId "{8626505B-AD8A-45AA-B1EB-8AB75B60524A}"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SetupId + "_is1"
#ifndef AppVersion
#define AppVersion "0.7.0"
#endif
#ifndef AppPayloadDir
#define AppPayloadDir "app-" + AppVersion + "-shared"
#endif
#ifndef SourceZipName
#define SourceZipName "native-source-" + AppVersion + "-shared.zip"
#endif
#ifndef OutputName
#define OutputName "Open-Revo-" + AppVersion + "-Shared-Setup-x64"
#endif
#ifndef InstallerFilterPath
#define InstallerFilterPath "installer/InstallerFolderFilter.dll"
#endif
[Setup]
SetupArchitecture=x86
AppId={{8626505B-AD8A-45AA-B1EB-8AB75B60524A}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
#ifdef InternalBuildVersion
VersionInfoVersion={#InternalBuildVersion}
#else
VersionInfoVersion={#AppVersion}.0
#endif
AppPublisher=Open-Revo Native Community Build
DefaultDirName={code:DefaultInstallDirectory}
UsePreviousAppDir=no
DisableDirPage=no
DefaultGroupName={#AppName}
UsePreviousGroup=no
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..
OutputBaseFilename={#OutputName}
SetupIconFile=src\Branding\App.ico
#ifdef NativeResident
UninstallDisplayIcon={app}\panel-native\Open Revo.exe
#elif defined(ResidentLayout)
UninstallDisplayIcon={app}\panel-native\OpenRevo.ResidentNativePanel.exe
#else
UninstallDisplayIcon={app}\OpenRevo.Native.exe
#endif
AppMutex=Local\OpenRevo.Native.InstalledPreview,Global\OpenRevo.RevoCore.HardwareOwner
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
[Dirs]
Name: "{app}\plugins"; Flags: uninsneveruninstall
[Files]
Source: "{#InstallerFilterPath}"; Flags: dontcopy
Source: "{#AppPayloadDir}\OpenRevo.Start.exe"; Flags: dontcopy
Source: "{#AppPayloadDir}\OpenRevo.Start.dll"; Flags: dontcopy
Source: "{#AppPayloadDir}\OpenRevo.Start.deps.json"; Flags: dontcopy
Source: "{#AppPayloadDir}\OpenRevo.Start.runtimeconfig.json"; Flags: dontcopy
Source: "{#AppPayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "plugins\*,diagnostics\*,layout-review\*,*.pdb,ui-review.json,ui-caption-steps.txt,caption-error.txt,overview-layout.json,overview-core-readings.json"
Source: "LICENSE"; DestDir: "{app}"
Source: "INSTALL-NOTES.txt"; DestDir: "{app}"
Source: "{#SourceZipName}"; DestDir: "{app}\source"; DestName: "native-source.zip"
Source: "RELEASE-{#AppVersion}.md"; DestDir: "{app}"
[Icons]
Name: "{userprograms}\{#AppName}\{#AppName}"; Filename: "{app}\OpenRevo.Start.exe"; WorkingDir: "{app}"; IconFilename: "{app}\App.ico"; AppUserModelID: "OpenRevo.Native.ControlPanel"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\OpenRevo.Start.exe"; WorkingDir: "{app}"; IconFilename: "{app}\App.ico"; AppUserModelID: "OpenRevo.Native.ControlPanel"; Check: InstallDesktopShortcut
[Run]
Filename: "{app}\OpenRevo.Start.exe"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall runasoriginaluser skipifsilent unchecked; Check: HasLaunchAuthorization
#include "legacy-files.iss"
[Code]
var
  LaunchAuthorized: Boolean;
  UpgradeStarted: Boolean;
  PreviousUserInstall: String;
  ConsoleReservation: THandle;
  HardwareReservation: THandle;
function CreateReservation(Attributes: NativeUInt; InitialOwner: BOOL; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function CloseReservation(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function CheckInstallDirectory(): String; forward;
function HasExistingInstall(): Boolean; forward;
#define WithRuntimePrerequisite
#define InstallerPreflight
#include "RuntimePrerequisite.iss"
#include "InstallerDirectoryPicker.iss"
function RegisteredPath(RootKey: Integer): String;
var
  Path: String;
begin
  Result := '';
  if not RegQueryStringValue(RootKey, '{#UninstallKey}', 'Inno Setup: App Path', Path) then Exit;
  Path := RemoveBackslashUnlessRoot(Path);
  if (Length(Path) < 4) or (Copy(Path, 2, 2) <> ':\') or (Pos('"', Path) <> 0) then Exit;
  if FileExists(AddBackslash(Path) + 'OpenRevo.Native.exe') or
    ((FileExists(AddBackslash(Path) + 'OpenRevo.Resident.exe') or
      FileExists(AddBackslash(Path) + 'RevoCore.exe')) and
     (FileExists(AddBackslash(Path) + 'panel-native\Open Revo.exe') or
      FileExists(AddBackslash(Path) + 'panel-native\OpenRevo.ResidentNativePanel.exe'))) or
    FileExists(Path + '.openrevo-previous\installation\OpenRevo.Native.exe') then
    Result := Path;
end;

function PreviousUserDirectory(): String;
begin
  Result := RegisteredPath(HKCU);
  if Result = '' then Result := RegisteredPath(HKCU32);
end;

function ExistingInstallDirectory(): String;
begin
  Result := RegisteredPath(HKLM);
  if Result = '' then Result := PreviousUserDirectory();
end;

function HasExistingInstall(): Boolean;
begin
  Result := ExistingInstallDirectory() <> '';
end;

function DefaultInstallDirectory(Param: String): String;
begin
  Result := ExistingInstallDirectory();
  if Result = '' then Result := ExpandConstant('{autopf}\OpenRevo');
end;

function HasLaunchAuthorization(): Boolean;
begin
  Result := LaunchAuthorized;
end;

procedure ReleaseApplicationReservations();
begin
  if HardwareReservation <> 0 then
  begin
    CloseReservation(HardwareReservation);
    HardwareReservation := 0;
  end;
  if ConsoleReservation <> 0 then
  begin
    CloseReservation(ConsoleReservation);
    ConsoleReservation := 0;
  end;
end;

function ReserveApplication(): Boolean;
var
  ErrorCode: Integer;
begin
  ConsoleReservation := CreateReservation(0, False, 'Local\OpenRevo.Native.InstalledPreview');
  ErrorCode := DLLGetLastError;
  Result := (ConsoleReservation <> 0) and (ErrorCode <> 183);
  if Result then
  begin
    HardwareReservation := CreateReservation(0, False, 'Global\OpenRevo.RevoCore.HardwareOwner');
    ErrorCode := DLLGetLastError;
    Result := (HardwareReservation <> 0) and (ErrorCode <> 183);
  end;
  if not Result then ReleaseApplicationReservations();
end;

function InstallDesktopShortcut(): Boolean;
begin
  Result := WizardIsTaskSelected('desktopicon') or
    ((PreviousUserInstall <> '') and FileExists(ExpandConstant('{userdesktop}\{#AppName}.lnk')));
end;

function RunLaunchMaintenance(const Executable, Operation: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Executable, Operation + ' "' + ExpandConstant('{app}') + '"',
    ExtractFileDir(Executable), SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function CheckInstallDirectory(): String;
begin
  Result := '';
  ExtractTemporaryFile('OpenRevo.Start.exe');
  ExtractTemporaryFile('OpenRevo.Start.dll');
  ExtractTemporaryFile('OpenRevo.Start.deps.json');
  ExtractTemporaryFile('OpenRevo.Start.runtimeconfig.json');
  if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--prepare') then
    Result := '安装目录或当前登录账户未通过管理员启动授权检查。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if not ReserveApplication() then
      RaiseException('请先从托盘完全退出 Open-Revo，再继续安装。');
    if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--require-stopped') then
      RaiseException('检测到 Open-Revo 仍在运行，已停止安装。');
    PreviousUserInstall := PreviousUserDirectory();
    UpgradeStarted := (PreviousUserInstall <> '') and
      (CompareText(RemoveBackslashUnlessRoot(PreviousUserInstall), ExpandConstant('{app}')) = 0);
    if UpgradeStarted and not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--begin-upgrade') then
      RaiseException('原位升级准备失败，旧版备份不会被覆盖。');
  end;
  if CurStep = ssPostInstall then
  begin
#ifdef ResidentLayout
    if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--validate-resident-payload') then
      RaiseException('独立后台与面板的安装文件未通过检查。');
#endif
    if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--register') then
      RaiseException('管理员启动任务登记失败，请重新运行安装程序修复。');
    if (PreviousUserInstall <> '') and (Pos('"', PreviousUserInstall) = 0) then
      if not Exec(ExpandConstant('{app}\OpenRevo.Start.exe'), '--migrate-legacy-logon "' + PreviousUserInstall + '"',
        ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
        RaiseException('旧版登录自启迁移失败，请检查原安装后重新运行安装程序。');
#ifdef ResidentLayout
    if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--retire-legacy-console') then
      RaiseException('未能退役原单进程控制台。请查看备份并重新运行安装程序。');
#endif
    if UpgradeStarted then
    begin
      if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--commit-upgrade') then
        RaiseException('原位升级提交失败。');
      UpgradeStarted := False;
      if CompareText(RegisteredPath(HKCU), PreviousUserInstall) = 0 then
        RegDeleteKeyIncludingSubkeys(HKCU, '{#UninstallKey}');
      if CompareText(RegisteredPath(HKCU32), PreviousUserInstall) = 0 then
        RegDeleteKeyIncludingSubkeys(HKCU32, '{#UninstallKey}');
    end;
    LaunchAuthorized := True;
    ReleaseApplicationReservations();
  end;
end;

procedure DeinitializeSetup();
begin
  if UpgradeStarted then
    if not RunLaunchMaintenance(ExpandConstant('{tmp}\OpenRevo.Start.exe'), '--restore-upgrade') then
      Log('Open-Revo upgrade restore did not finish. The previous installation remains in the backup directory.');
  ReleaseApplicationReservations();
end;

function InitializeUninstall(): Boolean;
begin
  Result := RunLaunchMaintenance(ExpandConstant('{app}\OpenRevo.Start.exe'), '--check-uninstall');
  if not Result then
    MsgBox('无法确认此安装的授权账户，已取消卸载。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if not ReserveApplication() then
      RaiseException('请先从托盘完全退出 Open-Revo，再继续卸载。');
    if not RunLaunchMaintenance(ExpandConstant('{app}\OpenRevo.Start.exe'), '--require-stopped') then
      RaiseException('检测到 Open-Revo 仍在运行，已停止卸载。');
    if not RunLaunchMaintenance(ExpandConstant('{app}\OpenRevo.Start.exe'), '--remove') then
      RaiseException('启动任务清理失败，已停止卸载。');
  end;
end;

procedure DeinitializeUninstall();
begin
  ReleaseApplicationReservations();
end;




