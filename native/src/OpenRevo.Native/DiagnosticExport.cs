using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool diagnosticExportBusy;
    private string? diagnosticExportLastPath;
    private object BuildDiagnosticReport()=>new
    {
        diagnosticsSchema=3,exportedAt=DateTimeOffset.Now,
        version=BuildInfo.PublicReleaseVersion.ToString(3),upstream="72e98685b2c0538fa0a58409a6bcce9e7b6336f7",
        build=BuildInfo.Snapshot,modelDeclaration=SafeDiagnostic(()=>upstream?.Read(23)),
        device=identity,biosVersion=DeviceIdentity.ReadBiosVersion(),administrator=GpuModeControl.IsAdministrator(),
        admission=CoreMachine.Current,takeover=new{active=hardware.Active,message=hardwareMessage,automaticRetryPending=automaticTakeoverPending},
        defaultDiagnosticsDirectory=AppData.DiagnosticsPath,system=latest,hardware=ec,display=displayState,control=hardware.State,
        performanceOperation=hardware.PerformanceDiagnostics,
        physicalModeKey=new{attempted=modeKeyAttempted,starting=modeKeyStarting,subscribed=modeKeyObserver?.Installed==true,failure=modeKeyObserver?.Failure,indicatorWarning=hardware.ModeIndicatorWarning},
        startup=StartupRegistration.LastInspection,displayDiagnostics=DisplayTopology.LastSnapshot,
        lighting=new{detected=keyboard,readSucceeded=keyboard.Kind=="White"?CurrentMonochromeLevel.HasValue:lightingReadFresh,
            readError=keyboard.Kind=="White"?CurrentMonochromeLevel.HasValue?null:"白色背光当前档位未能读取。":lightingProbeError,protocol=hardware.LightingDiagnostics},
        quick=new{quickState=new{quickState.Camera,quickState.CameraAvailable,quickState.Microphone,quickState.WinLocked},winLockSource="Windows native keyboard hook; software-only; no firmware readback",winLockPending=requestedWinLocked,extraSwitches},gpuTelemetry=overviewTelemetry,
        cameraWindowsDiagnostics,
        gpuTelemetryAt=overviewTelemetryAt,gpuTelemetryDiagnostics=SafeDiagnostic(()=>upstream?.GpuTelemetryDiagnostics),
        gpuPresentation=new{currentPage,mini,visible=IsVisibleNow(),fresh=CurrentOverviewGpu!=null,
            sampleAgeMs=overviewTelemetry==null?(double?)null:(DateTimeOffset.Now-overviewTelemetryAt).TotalMilliseconds},
        monochrome=new{canSend=MonochromeEnabled,requested=monochromeRequested,levels=monochromeLevels,
            currentLevel=CurrentMonochromeLevel,readAt=monochromeReadAt,message=MonochromeStatus,hardwareReadback=CurrentMonochromeLevel.HasValue},
        osd=SafeDiagnostic(()=>nativeOsd?.Diagnostics),colorDiagnostics=serviceColor.ValueKind==JsonValueKind.Object?(object)serviceColor:null,logging=DiagnosticLog.Collect(AppData.DiagnosticsPath),
        hardwareWritesEnabled=hardware.Active,ecReadEnabled=ecEnabled,actions
    };
    private static object? SafeDiagnostic(Func<object?> read){try{return read();}catch(Exception e){return new{error=DiagnosticJournal.Scrub(e.Message),hresult=$"0x{e.HResult:X8}"};}}
    private async Task ExportDiagnosticReport()
    {
        if(diagnosticExportBusy||closed)return;diagnosticExportBusy=true;
        using var trace=DiagnosticLog.Begin("diagnostics.export");
        try
        {
            string directory=AppData.DiagnosticsPath;Directory.CreateDirectory(directory);
            var picker=new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName=$"report-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                SuggestedStartFolder=directory
            };
            picker.FileTypeChoices.Add("诊断报告 JSON",new List<string>{".json"});
            var selected=await picker.PickSaveFileAsync();if(selected==null){trace.Complete(outcome:"cancelled");return;}
            string content=JsonSerializer.Serialize(BuildDiagnosticReport(),new JsonSerializerOptions{WriteIndented=true});
            await File.WriteAllTextAsync(selected.Path,content);
            diagnosticExportLastPath=selected.Path;
            trace.Complete(new{path=selected.Path,bytes=System.Text.Encoding.UTF8.GetByteCount(content),schema=3});
            if(UsePaintedUi){if(await PaintDialog("诊断报告已保存",Paint.Text(selected.Path),"打开所在文件夹","完成"))OpenLocalFolder(Path.GetDirectoryName(selected.Path)!);}
            else
            {
            var dialog=new ContentDialog{XamlRoot=root.XamlRoot,Title="诊断报告已保存",Content=Ui.Text(selected.Path,14),PrimaryButtonText="打开所在文件夹",CloseButtonText="完成",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)OpenLocalFolder(Path.GetDirectoryName(selected.Path)!);
            }
        }
        catch(Exception error){trace.Fail(error);if(!closed)await ShowMessage("诊断导出失败",error.Message);}
        finally{diagnosticExportBusy=false;}
    }
}


