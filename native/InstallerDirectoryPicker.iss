{ Keep Inno's original dialog, images, controls and path-selection handler. }
var
  OriginalDirectoryBrowse: TNotifyEvent;

function BeginDirectoryFilter(): Cardinal;
  external 'BeginDirectoryFilter@files:InstallerFolderFilter.dll stdcall setuponly';
function EndDirectoryFilter(): Cardinal;
  external 'EndDirectoryFilter@files:InstallerFolderFilter.dll stdcall setuponly';

procedure BrowseVisibleInstallDirectory(Sender: TObject);
var
  Error: Cardinal;
begin
  Error := BeginDirectoryFilter();
  if Error <> 0 then
  begin
    MsgBox('无法启用目录筛选，请直接在安装路径框填写位置。错误码：' + IntToStr(Error), mbError, MB_OK);
    Exit;
  end;
  try
    OriginalDirectoryBrowse(Sender);
  finally
    Error := EndDirectoryFilter();
    if Error <> 0 then Log('Directory filter restore: ' + IntToStr(Error));
  end;
end;

procedure InitializeWizard();
begin
  OriginalDirectoryBrowse := WizardForm.DirBrowseButton.OnClick;
  WizardForm.DirBrowseButton.OnClick := @BrowseVisibleInstallDirectory;
end;
