using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool updateBusy;
    private NativeRelease? availableUpdate;
    private DownloadedUpdate? installOnExit;
    private string updateMessage = "检查社区版发布；下载校验后自动安装。";
    private Button? updateCheckButton, updateInstallButton;
    private TextBlock? updateHint;
    private CancellationTokenSource? updateCancellation;

    private FrameworkElement UpdateControls()
    {
        updateHint = Ui.Text(updateMessage,12,muted:true);
        updateCheckButton = Ui.Button("检查更新",async(_,_)=>await CheckNativeUpdate());
        updateInstallButton = Ui.Button("下载并安装",async(_,_)=>await DownloadNativeUpdate());
        var cancel=Ui.Button("取消下载",(_,_)=>updateCancellation?.Cancel());
        var body=Ui.Stack(DetailRow("\uE895","软件版本与分发更新",$"本版 {typeof(App).Assembly.GetName().Version?.ToString(3)} · 社区版发布源",SettingsControls(updateCheckButton,updateInstallButton,cancel,Ui.Button("发布页",async(_,_)=>await OpenSettings(ReleaseUpdater.ReleasesPage))),1050),updateHint,Ui.Button("打开下载与安装日志",(_,_)=>{string cache=Path.Combine(AppData.DirectoryPath,"updates");Directory.CreateDirectory(cache);OpenLocalFolder(cache);}));
        body.Spacing=4;SyncUpdateControls();var inset=DetailInset(body);inset.Padding=new Thickness(14,8,14,8);return inset;
    }
    private void SyncUpdateControls()
    {
        if(updateHint!=null)updateHint.Text=updateMessage;
        if(updateCheckButton!=null)updateCheckButton.IsEnabled=!updateBusy&&!exitRequested;
        if(updateInstallButton!=null)updateInstallButton.IsEnabled=availableUpdate!=null&&!updateBusy&&!exitRequested;
    }
    private async Task CheckNativeUpdate()
    {
        if(updateBusy)return;updateBusy=true;updateMessage="正在检查社区版更新…";SyncUpdateControls();
        try
        {
            using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
            availableUpdate=await new ReleaseUpdater(http).CheckAsync(typeof(App).Assembly.GetName().Version??new Version(0,0,0));
            updateMessage=availableUpdate==null?"已是最新社区版。":$"发现 {availableUpdate.Version.ToString(3)} · {availableUpdate.Size/1048576.0:F1} MiB；下载完成后将退出并自动安装。";
        }
        catch(Exception error){availableUpdate=null;updateMessage="检查失败："+error.Message;DiagnosticLog.Failure("update.check.failed",error);}
        finally{updateBusy=false;SyncUpdateControls();}
    }
    private async Task DownloadNativeUpdate()
    {
        if(updateBusy||availableUpdate==null)return;
        if(!File.Exists(Path.Combine(AppContext.BaseDirectory,"unins000.exe"))){await ShowMessage("源码预览","自动安装更新仅适用于已安装的社区版。请使用发布页安装器。");return;}
        updateBusy=true;updateCancellation=new CancellationTokenSource();SyncUpdateControls();
        try
        {
            using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(20)};
            var progress=new Progress<double>(value=>{updateMessage=$"正在下载 {availableUpdate.Version.ToString(3)}：{value:P0}";SyncUpdateControls();});
            var download=await new ReleaseUpdater(http).DownloadAsync(availableUpdate,Path.Combine(AppData.DirectoryPath,"updates"),progress,updateCancellation.Token);
            DiagnosticLog.Record("update.download.verified",new{version=availableUpdate.Version.ToString(),sha256=availableUpdate.Sha256,size=availableUpdate.Size});
            updateMessage="校验通过，正在退出并启动安装…";installOnExit=download;
        }
        catch(OperationCanceledException){updateMessage="下载已取消。";}
        catch(Exception error){updateMessage="更新失败："+error.Message;DiagnosticLog.Failure("update.download.failed",error);}
        finally{updateCancellation.Dispose();updateCancellation=null;updateBusy=false;SyncUpdateControls();}
        if(installOnExit!=null)
        {
            await ExitFromTray();
            if(!closed){installOnExit=null;updateMessage="程序未完成退出，自动安装已取消；可以重试。";SyncUpdateControls();}
        }
    }
    private void StartPendingUpdate()
    {
        if(installOnExit==null)return;
        ReleaseUpdater.StartInstallerAfterExit(installOnExit,AppContext.BaseDirectory);
        DiagnosticLog.Record("update.install.queued",new{version=installOnExit.Release.Version.ToString()});
        installOnExit=null;
    }
}
