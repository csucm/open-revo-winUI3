{ .NET 10 x64 desktop prerequisite. No .NET is needed to run this page. }
const
  RuntimeDownloadUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeInformationUrl = 'https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0';
  RuntimeInstallerName = 'windowsdesktop-runtime-win-x64.exe';

var
  RuntimePage: TWizardPage;
  RuntimeStatus: TNewStaticText;
  RuntimeDownloadButton: TNewButton;
  RuntimeInformationButton: TNewButton;
  RuntimeDownloadPage: TDownloadWizardPage;
  RuntimeBusy: Boolean;
#ifdef RuntimePrerequisiteTests
  RuntimeFixtureRoot: String;
#endif

function GetBinaryTypeW(FileName: String; var BinaryType: Cardinal): Boolean;
  external 'GetBinaryTypeW@kernel32.dll stdcall';

function IsStableNet10Version(Version: String): Boolean;
var
  I, Patch: Integer;
begin
  Result := False;
  if (Copy(Version, 1, 5) <> '10.0.') or (Length(Version) < 6) then Exit;
  for I := 6 to Length(Version) do
    if (Version[I] < '0') or (Version[I] > '9') then Exit;
  Patch := StrToIntDef(Copy(Version, 6, Length(Version)), -1);
  Result := Patch >= 0;
end;

function HasNet10Files(Root, RelativeDirectory, FirstFile, SecondFile: String): Boolean;
var
  Find: TFindRec;
  Folder: String;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + RelativeDirectory + '\*', Find) then
  try
    repeat
      if ((Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         IsStableNet10Version(Find.Name) then
      begin
        Folder := AddBackslash(Root) + RelativeDirectory + '\' + Find.Name + '\';
        if FileExists(Folder + FirstFile) and
           ((SecondFile = '') or FileExists(Folder + SecondFile)) then
        begin
          Result := True;
          Exit;
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

function IsAmd64Executable(FileName: String): Boolean;
var
  Data: AnsiString;
  Offset: Integer;
begin
  Result := False;
  if not LoadStringFromFile(FileName, Data) then Exit;
  if Length(Data) < 64 then Exit;
  if (Data[1] <> 'M') or (Data[2] <> 'Z') or (Ord(Data[64]) > 127) then Exit;
  Offset := Ord(Data[61]) + Ord(Data[62]) * 256 + Ord(Data[63]) * 65536 + Ord(Data[64]) * 16777216;
  if (Offset < 64) or (Offset > Length(Data) - 6) then Exit;
  Result := (Copy(Data, Offset + 1, 4) = 'PE' + #0 + #0) and
    (Ord(Data[Offset + 5]) = $64) and (Ord(Data[Offset + 6]) = $86);
end;

function Net10ReadyAtRoot(Root: String): Boolean;
var
  BinaryType: Cardinal;
begin
  Result := False;
  if not GetBinaryTypeW(AddBackslash(Root) + 'dotnet.exe', BinaryType) then Exit;
  if not IsAmd64Executable(AddBackslash(Root) + 'dotnet.exe') then Exit;
  if BinaryType <> 6 then Exit; { SCS_64BIT_BINARY; a 32-bit install is insufficient. }
  Result := HasNet10Files(Root, 'shared\Microsoft.NETCore.App', 'coreclr.dll', 'System.Private.CoreLib.dll') and
    HasNet10Files(Root, 'shared\Microsoft.WindowsDesktop.App', 'WindowsBase.dll', 'Microsoft.WindowsDesktop.App.deps.json') and
    HasNet10Files(Root, 'host\fxr', 'hostfxr.dll', '');
end;

function Net10RuntimeReady(): Boolean;
var
  Root: String;
begin
  Result := False;
  #ifdef RuntimePrerequisiteTests
  Result := Net10ReadyAtRoot(RuntimeFixtureRoot);
  Exit;
  #endif
  if not IsWin64 then Exit;
  { Match apphost global discovery. Do not rely on PATH or the x86 registry view. }
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
    Root := ExpandConstant('{commonpf64}\dotnet');
  if Trim(Root) = '' then Root := ExpandConstant('{commonpf64}\dotnet');
  Result := Net10ReadyAtRoot(Root);
end;

procedure RefreshRuntimeStatus();
begin
  if Net10RuntimeReady() then
  begin
    RuntimeStatus.Caption := '.NET 10 x64 桌面运行时已就绪，可以继续安装。';
    WizardForm.NextButton.Enabled := True;
  end
  else
  begin
    RuntimeStatus.Caption := '需要 .NET 10 x64 桌面运行时。点击下方按钮即可直接下载并打开微软安装程序，无需选择版本或寻找文件。安装完成后将自动重新检测。';
    WizardForm.NextButton.Enabled := False;
  end;
end;

procedure OpenRuntimeInformation(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', RuntimeInformationUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    RuntimeStatus.Caption := '无法打开微软说明页面，请检查默认浏览器。';
end;

procedure DownloadAndInstallRuntime(Sender: TObject);
var
  ExitCode: Integer;
  Downloaded: Boolean;
begin
  if RuntimeBusy then Exit;
  RuntimeBusy := True;
  RuntimeDownloadButton.Enabled := False;
  RuntimeInformationButton.Enabled := False;
  WizardForm.NextButton.Enabled := False;
  WizardForm.BackButton.Enabled := False;
  Downloaded := False;
  try
    RuntimeDownloadPage.Clear;
    { The official evergreen URL follows HTTPS redirects; no version-selection page. }
    RuntimeDownloadPage.Add(RuntimeDownloadUrl, RuntimeInstallerName, '');
    RuntimeDownloadPage.Show;
    try
      try
        RuntimeDownloadPage.Download;
        Downloaded := True;
      except
        if RuntimeDownloadPage.AbortedByUser then
          RuntimeStatus.Caption := '下载已取消。可以重新下载，或取消本次安装。'
        else
          RuntimeStatus.Caption := '下载失败，请检查网络后重试。' + #13#10 + GetExceptionMessage;
      end;
    finally
      RuntimeDownloadPage.Hide;
    end;
    if Downloaded then
    begin
      WizardForm.CancelButton.Enabled := False;
      RuntimeStatus.Caption := '正在打开微软 .NET 安装程序。请完成安装；本程序不会自动重启电脑。';
      { Interactive Microsoft setup retains its publisher/UAC and consent screens. }
      if not ShellExec('open', ExpandConstant('{tmp}\') + RuntimeInstallerName,
        '/install /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
        RuntimeStatus.Caption := '无法打开微软安装程序。可以重试下载。'
      else if Net10RuntimeReady() then
      begin
        RefreshRuntimeStatus();
        WizardForm.NextButton.OnClick(WizardForm.NextButton);
      end
      else
        RuntimeStatus.Caption := '.NET 10 x64 桌面运行时尚未就绪（微软安装程序退出码 ' + IntToStr(ExitCode) + '）。未继续安装 Open-Revo。请重试，或取消后重新运行本安装包。';
    end;
  finally
    WizardForm.CancelButton.Enabled := True;
    RuntimeBusy := False;
    RuntimeDownloadButton.Enabled := True;
    RuntimeInformationButton.Enabled := True;
    WizardForm.BackButton.Enabled := True;
  end;
end;

procedure InitializeRuntimePrerequisite();
begin
  RuntimePage := CreateCustomPage(wpWelcome, '.NET 10 环境检查', '安装 Open-Revo 前准备共享运行时');
  RuntimeStatus := TNewStaticText.Create(RuntimePage);
  RuntimeStatus.Parent := RuntimePage.Surface;
  RuntimeStatus.SetBounds(0, 0, RuntimePage.SurfaceWidth, ScaleY(90));
  RuntimeStatus.AutoSize := False;
  RuntimeStatus.WordWrap := True;
  RuntimeDownloadButton := TNewButton.Create(RuntimePage);
  RuntimeDownloadButton.Parent := RuntimePage.Surface;
  RuntimeDownloadButton.SetBounds(0, ScaleY(110), ScaleX(210), ScaleY(32));
  RuntimeDownloadButton.Caption := '下载并安装 .NET 10（x64）';
  RuntimeDownloadButton.OnClick := @DownloadAndInstallRuntime;
  RuntimeInformationButton := TNewButton.Create(RuntimePage);
  RuntimeInformationButton.Parent := RuntimePage.Surface;
  RuntimeInformationButton.SetBounds(0, ScaleY(160), ScaleX(210), ScaleY(28));
  RuntimeInformationButton.Caption := '查看微软运行时说明（可选）';
  RuntimeInformationButton.OnClick := @OpenRuntimeInformation;
  RuntimeDownloadPage := CreateDownloadPage('下载 .NET 10 x64 桌面运行时', '正在直接从微软下载所需安装程序。', nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

#ifndef RuntimePrerequisiteTests
function InitializeSetup(): Boolean;
begin
  Result := True;
  if WizardSilent and not Net10RuntimeReady() then
  begin
    Log('Missing .NET 10 x64 Desktop Runtime. Silent install stopped without download or changes.');
    Result := False;
  end;
end;

#endif
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = RuntimePage.ID) and Net10RuntimeReady();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = RuntimePage.ID then RefreshRuntimeStatus();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := (CurPageID <> RuntimePage.ID) or Net10RuntimeReady();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not Net10RuntimeReady() then
    Result := '需要 .NET 10 x64 桌面运行时。请重新运行安装包，并按引导下载所需运行时。';
end;





