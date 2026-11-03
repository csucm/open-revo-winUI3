using OpenRevo.Core.Hal;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly TrayTelemetryCache trayTelemetry = new();
    private readonly Telemetry trayCpu = new();
    private Task<TrayTelemetrySample>? trayHoverRead;
    private DateTimeOffset trayLastRequested;
    private string trayTip = new TrayTelemetrySample(DateTimeOffset.MinValue,null,null,null,null,false).ToTooltip();
    private async Task RefreshTrayTelemetry()
    {
        if(uiReviewMode || closed || exitRequested || gpuChanging || RuntimeBusy || !trayAvailable) return;
        if(trayHoverRead is {IsCompleted:false})return;
        if(DateTimeOffset.UtcNow-trayLastRequested is {TotalSeconds: >=0 and <5})return;
        if(!await operationQueue.WaitAsync(0))return;
        try
        {
            trayLastRequested=DateTimeOffset.UtcNow;
            bool readEc=ecEnabled&&hardware.Active;
            var savedEc=ec;var pendingEc=ecTask;bool freshEc=pendingEc==null&&DateTimeOffset.Now-lastEc<TimeSpan.FromSeconds(6);
            var core=upstream;
            var cachedCpu=latest;
            bool cpuFresh=cachedCpu?.CpuPercent!=null&&DateTimeOffset.Now-cachedCpu.At<TimeSpan.FromSeconds(4);
            var cachedTemperature=temperature;
            bool tempFresh=temperatureTask==null&&cachedTemperature.Cpu!=null&&DateTimeOffset.Now-lastTemperature<TimeSpan.FromSeconds(4);
            DateTimeOffset tempAt=lastTemperature;
            var cachedGpu=overviewTelemetry;
            bool gpuFresh=cachedGpu?.GpuSampled==true&&DateTimeOffset.Now-overviewTelemetryAt<TimeSpan.FromSeconds(4);
            DateTimeOffset gpuAt=overviewTelemetryAt;
            trayHoverRead=trayTelemetry.RequestAsync(()=>Task.Run(async()=>
            {
                // Two GetSystemTimes samples only; do not wake hidden UI polling.
                if(!cpuFresh)trayCpu.ReadCpuPercent();
                var fans=savedEc;
                if(readEc&&!freshEc){try{fans=pendingEc!=null?await pendingEc:EcReader.Read(identity);}catch{fans=new(null,null,null,null,"不可用");}}
                bool readGpu=readEc&&Telemetry.ReadAc()==true&&fans.GpuFan is >0&&core!=null;
                var temps=tempFresh?cachedTemperature:temperatures.Read(false);
                OverviewTelemetry? detail=null;
                if(readGpu) { try { detail=gpuFresh?cachedGpu:core!.ReadOverviewTelemetry(true); } catch { } }
                if(!cpuFresh)await Task.Delay(350);
                var at=cpuFresh?cachedCpu!.At:DateTimeOffset.UtcNow;
                if(tempFresh&&tempAt<at)at=tempAt;
                if(readGpu&&gpuFresh&&gpuAt<at)at=gpuAt;
                return new TrayTelemetrySample(at,temps.Cpu,cpuFresh?cachedCpu!.CpuPercent:trayCpu.ReadCpuPercent(),
                    readGpu?detail?.Gpu.Temperature:null,readGpu?detail?.Gpu.Usage:null,readGpu);
            }));
            var sample=await trayHoverRead;
            if(closed||exitRequested||!trayAvailable)return;
            trayTip=sample.ToTooltip();var record=TrayRecord();record.Flags=0x84;ShellNotify(1,ref record);
        }
        finally { operationQueue.Release(); }
    }
}


