using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly List<object> functionOwnershipSamples=[];
    private void AuditFunctionOwnership(string name,Action<string,bool> check)
    {
        int entries=DetailDescendants(page).OfType<Button>().Count(button=>AutomationProperties.GetName(button)=="进入调校");
        bool owns(string owner,bool present)=>!present||currentPage==owner&&!customTuningOpen;
        bool valid=!mini&&owns("overview",performanceButtons.Count>0)&&owns("overview",refreshButtons.Count>0)&&owns("overview",quickButtons.Count>0||extraSwitchButtons.Count>0||overviewBoost!=null)
            &&owns("display",gpuButtons.Count>0||brightnessButtons.Count>0||entries>0)&&owns("power",compactChargeButtons.Count>0)&&owns("devices",desktopLightToggle!=null||desktopEffects.Count>0);
        valid&=customTuningOpen?tuningPanel!=null&&currentPage=="display":tuningPanel==null;
        if(!customTuningOpen)
        {
            if(currentPage=="overview")valid&=performanceButtons.Count==4&&quickButtons.Count==5&&extraSwitchButtons.Count==5&&overviewBoost!=null;
            if(currentPage=="display")valid&=(colorTuningOpen?gpuButtons.Count==0&&brightnessButtons.Count==0:gpuButtons.Count==3&&brightnessButtons.Count==3)&&entries==0;
            if(currentPage=="devices")valid&=!desktopEffects.ContainsKey("关闭背光");
        }
        check(name+" exposes each executable control group only on its owner page",valid);
        functionOwnershipSamples.Add(new{name,currentPage,customTuningOpen,factoryButtons=performanceButtons.Count,refreshButtons=refreshButtons.Count,quickButtons=quickButtons.Count,extraSwitches=extraSwitchButtons.Count,boost=overviewBoost!=null,gpuButtons=gpuButtons.Count,brightnessButtons=brightnessButtons.Count,batteryButtons=compactChargeButtons.Count,lighting=desktopLightToggle!=null,effects=desktopEffects.Keys.ToArray(),tuningEntries=entries,editor=tuningPanel!=null,valid});
    }
}
