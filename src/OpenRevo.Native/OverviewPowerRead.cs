using OpenRevo.Core.Hal;
using Microsoft.UI.Xaml.Controls;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private OverviewPowerTargets? overviewPowerTargets;
    private Task<OverviewPowerTargets>? overviewPowerRead;
    private DateTimeOffset lastOverviewPowerRead;
    private int overviewPowerGeneration,overviewPowerReadGeneration;
    private string overviewPowerError="正在读取本机EC与GPU功耗边界。";
    private string? overviewPowerTooltip;
    private void InvalidateOverviewPowerTargets()
    {overviewPowerGeneration++;overviewPowerTargets=null;lastOverviewPowerRead=DateTimeOffset.MinValue;}
    private async Task ReadOverviewPowerTargets()
    {
        if(overviewPowerRead!=null)return;
        lastOverviewPowerRead=DateTimeOffset.Now;overviewPowerReadGeneration=overviewPowerGeneration;
        overviewPowerRead=Task.Run(hardware.ProbeOverviewPowerTargets);
        try
        {
            var result=await overviewPowerRead;
            if(overviewPowerReadGeneration==overviewPowerGeneration){overviewPowerTargets=result;overviewPowerError=result.Detail;}
        }
        catch(Exception error){overviewPowerTargets=null;overviewPowerError="原厂目标读取未完成："+error.Message;}
        finally{overviewPowerRead=null;if(!closed)UpdateOverviewPowerTarget();}
    }
    private void SampleOverviewPowerTargets()
    {
        if(!mini&&currentPage=="overview"&&IsVisibleNow()&&!hardwareBusy&&!gpuChanging&&!tuningApplying&&overviewPowerRead==null&&
            DateTimeOffset.Now-lastOverviewPowerRead>TimeSpan.FromSeconds(6))_ = ReadOverviewPowerTargets();
    }
    private void UpdateOverviewPowerTarget()
    {
        var state=overviewPowerTargets;
        bool fresh=state!=null&&DateTimeOffset.Now-state.At<TimeSpan.FromSeconds(14);
        Put("overviewPowerTarget",fresh?state!.Text:"原厂目标 PL1: — W | TGP: — W");
        if(overviewPowerTooltip==overviewPowerError||!live.TryGetValue("overviewPowerTarget",out var text))return;
        ToolTipService.SetToolTip(text,overviewPowerError);
        overviewPowerTooltip=overviewPowerError;
    }
}
