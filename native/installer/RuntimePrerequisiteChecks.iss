#define RuntimePrerequisiteTests
[Setup]
AppName=OpenRevo Runtime Prerequisite Checks
AppVersion=1
DefaultDirName={tmp}\never-install-openrevo
PrivilegesRequired=lowest
Uninstallable=no
CreateAppDir=no
OutputDir=.
OutputBaseFilename=runtime-prerequisite-checks
[Code]
#include "..\RuntimePrerequisite.iss"
var
  Checks: Integer;
  ReportPath: String;
procedure Assert(Value: Boolean; Name: String);
begin
  if not Value then begin SaveStringToFile(ReportPath, '{"allPassed":false,"failed":"' + Name + '"}', False); RaiseException(Name); end;
  Checks := Checks + 1;
end;
function InitializeSetup(): Boolean;
var
  Root, Dotnet64, Dotnet32, DesktopPath, CorePath, FxrPath: String;
  NeedsRestart: Boolean;
  ImageData: AnsiString;
  PeOffset: Integer;
begin
  Result := False;
  ReportPath := ExpandConstant('{param:Report}');
  Root := ExpandConstant('{param:Fixture}');
  Dotnet64 := ExpandConstant('{param:Dotnet64}');
  Dotnet32 := ExpandConstant('{param:Dotnet32}');
  RuntimeFixtureRoot := Root;
  DesktopPath := Root + '\shared\Microsoft.WindowsDesktop.App\10.0.14';
  CorePath := Root + '\shared\Microsoft.NETCore.App\10.0.13';
  FxrPath := Root + '\host\fxr\10.0.13';
  Assert(IsStableNet10Version('10.0.0'), 'minimum stable');
  Assert(IsStableNet10Version('10.0.14'), 'later patch');
  Assert(not IsStableNet10Version('9.0.14'), 'old major rejected');
  Assert(not IsStableNet10Version('11.0.0'), 'new major not substituted');
  Assert(not IsStableNet10Version('10.1.0'), 'latest patch policy');
  Assert(not IsStableNet10Version('10.0.0-preview.1'), 'preview rejected');
  Assert(not IsStableNet10Version('10.0.-1'), 'negative patch rejected');
  Assert(not IsStableNet10Version('10.0.2147483648'), 'overflow rejected');
  Assert(not IsStableNet10Version('10.0.14x'), 'malformed rejected');
  ForceDirectories(Root);
  Assert(not Net10RuntimeReady(), 'missing root blocked');
  FileCopy(Dotnet64, Root + '\dotnet.exe', False);
  Assert(not Net10RuntimeReady(), 'host executable alone insufficient');
  ForceDirectories(DesktopPath); ForceDirectories(CorePath); ForceDirectories(FxrPath);
  SaveStringToFile(DesktopPath + '\WindowsBase.dll', 'fixture', False);
  SaveStringToFile(DesktopPath + '\Microsoft.WindowsDesktop.App.deps.json', '{}', False);
  SaveStringToFile(CorePath + '\coreclr.dll', 'fixture', False);
  SaveStringToFile(CorePath + '\System.Private.CoreLib.dll', 'fixture', False);
  SaveStringToFile(FxrPath + '\hostfxr.dll', 'fixture', False);
  Assert(Net10RuntimeReady(), 'complete x64 stable 10 accepted');
  Assert(PrepareToInstall(NeedsRestart) = '', 'ready installer proceeds');
  DeleteFile(DesktopPath + '\WindowsBase.dll');
  Assert(not Net10RuntimeReady(), 'core without desktop blocked');
  Assert(PrepareToInstall(NeedsRestart) <> '', 'missing dependency blocks file installation');
  SaveStringToFile(DesktopPath + '\WindowsBase.dll', 'fixture', False);
  DeleteFile(CorePath + '\coreclr.dll'); Assert(not Net10RuntimeReady(), 'desktop without core blocked');
  SaveStringToFile(CorePath + '\coreclr.dll', 'fixture', False);
  DeleteFile(FxrPath + '\hostfxr.dll'); Assert(not Net10RuntimeReady(), 'missing hostfxr blocked');
  SaveStringToFile(FxrPath + '\hostfxr.dll', 'fixture', False);
  DeleteFile(DesktopPath + '\Microsoft.WindowsDesktop.App.deps.json'); Assert(not Net10RuntimeReady(), 'broken desktop files blocked');
  SaveStringToFile(DesktopPath + '\Microsoft.WindowsDesktop.App.deps.json', '{}', False);
  FileCopy(Dotnet32, Root + '\dotnet.exe', False); Assert(not Net10RuntimeReady(), 'x86 executable rejected');
  FileCopy(Dotnet64, Root + '\dotnet.exe', False); Assert(Net10RuntimeReady(), 'recheck accepts newly installed runtime');
  Assert(IsAmd64Executable(Root + '\dotnet.exe'), 'AMD64 PE confirmed');
  LoadStringFromFile(Root + '\dotnet.exe', ImageData);
  PeOffset := Ord(ImageData[61]) + Ord(ImageData[62]) * 256 + Ord(ImageData[63]) * 65536 + Ord(ImageData[64]) * 16777216;
  ImageData[PeOffset + 5] := #$64;
  ImageData[PeOffset + 6] := #$AA;
  SaveStringToFile(Root + '\dotnet.exe', ImageData, False);
  Assert(not Net10RuntimeReady(), 'ARM64 runtime not mistaken for x64');
  FileCopy(Dotnet64, Root + '\dotnet.exe', False);
  Assert(RuntimeDownloadUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe', 'exact direct x64 URL');
  Assert(RuntimeInformationUrl = 'https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0', 'exact information URL');
  SaveStringToFile(ReportPath, '{"allPassed":true,"checks":' + IntToStr(Checks) + ',"networkRequests":0,"runtimeInstallerLaunches":0,"applicationInstallations":0}', False);
end;


