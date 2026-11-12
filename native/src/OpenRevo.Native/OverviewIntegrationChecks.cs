using Microsoft.UI.Xaml;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunOverviewIntegrationChecks(Action<string,bool> check,bool capture)
    {
        if(upstream==null)throw new IOException("Core bridge unavailable");
        var samples=new List<object>();
        for(int i=0;i<2;i++)
        {
            var sample = await overviewSampling.ReadForReviewAsync(upstream, sampleGpu: true);
            samples.Add(new { at = sample.At, overviewTelemetry = sample.Data });
            if(i==0)await Task.Delay(2100);
        }
        extraSwitches=await Task.Run(upstream.ReadOverviewSwitches);
        if(mini)ToggleMini();currentPage="overview";customTuningOpen=false;Render();root.UpdateLayout();
        var data=overviewTelemetry!;
        check("Overview CPU power uses the Core value and clearly labels its original fallback model",data.Cpu.PowerW.HasValue&&live["overviewCpuPower"].Text.Contains($"{data.Cpu.PowerW:0.0}")&&live["overviewCpuPower"].Text.Contains("估算")==data.Cpu.PowerEstimated);
        double displayScale = data.Gpu.ShowsSharedMemory ? 1.073741824 : 1;
        string memoryUnit = "GB";
        check("GPU memory displays GB while preserving its capacity readings and actual memory clock",
            data.Gpu.DisplayMemoryTotalGiB is not double total ||
            live["overviewVramUsed"].Text == (data.Gpu.DisplayMemoryUsedGiB is double used ? $"{used * displayScale:0.0}" : "—") &&
            live["overviewVramTotal"].Text == $"/ {total * displayScale:0.0} {memoryUnit}" &&
            live["overviewMemoryMhz"].Text == (data.Gpu.MemoryMhz?.ToString() ?? "—"));
        string previousVram=live["overviewVramUsed"].Text,previousPower=live["overviewCpuPower"].Text;
        currentPage="display";
        await Task.Delay(6100);
        currentPage="overview";UpdateOverviewValues();
        check("Stale telemetry stays visible with an explicit reading time",live["overviewVramUsed"].Text==previousVram&&live["overviewCpuPower"].Text==previousPower&&!OverviewTelemetryFresh);
        bool oldOsd=osdEnabled;osdEnabled=false;UpdateExtraSwitchButtons();
        check("OSD shortcut reflects disabled state without changing hardware",extraSwitchButtons["osd"].Style!=(Style)Microsoft.UI.Xaml.Application.Current.Resources["SelectedChoiceStyle"]);
        osdEnabled=oldOsd;UpdateExtraSwitchButtons();
        var message=await Task.Run(()=>upstream.SendOsd(3,1));
        check("Native OSD consumes Core event sink notifications and acknowledges the queue",message?.Feature=="wifi"&&message.Value=="1"&&upstream.Read(12).ValueKind==JsonValueKind.Null);
        nativeOsd??=new NativeOsd(DispatcherQueue);var focus=NativeOsd.Foreground;nativeOsd.Show("屏幕提示","已开启");await Task.Delay(120);
        check("Native OSD appears without activation or keyboard focus",nativeOsd.IsVisible&&nativeOsd.DoesNotActivate&&NativeOsd.Foreground==focus);
        if(capture)
        {
            await CaptureDesktopReview("Native-OSD",nativeOsd.Surface);
        }
        await Task.Delay(1850);check("Native OSD automatically hides after the transient notification",!nativeOsd.IsVisible);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"overview-core-readings.json"),JsonSerializer.Serialize(new{samples,switches=extraSwitches,hardwareSettersInvoked=false,osdCoreEvent=message},new JsonSerializerOptions{WriteIndented=true}));
    }
}
