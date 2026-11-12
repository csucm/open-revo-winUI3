using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Task<TrayTelemetrySample?>? trayHoverRead;
    private string trayTip = new TrayTelemetrySample(DateTimeOffset.MinValue,null,null,null,null,false).ToTooltip();
    private async Task RefreshTrayTelemetry()
    {
        if(uiReviewMode || closed || exitRequested || gpuChanging || RuntimeBusy || !trayAvailable) return;
        var inputs=new TrayTelemetryInputs(identity,ec,ecTask,lastEc,latest,
            temperatureTask==null?temperature:null,lastTemperature);
        var sample=await (trayHoverRead=hardwareRuntime.Telemetry.ReadTrayAsync(ecEnabled,inputs));
        if(sample==null||closed||exitRequested||!trayAvailable)return;
        trayTip=sample.ToTooltip();var record=TrayRecord();record.Flags=0x84;ShellNotify(1,ref record);
    }
}


