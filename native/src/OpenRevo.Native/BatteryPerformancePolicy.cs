using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool batteryGuardBusy;
    private async Task EnforceBatteryPerformanceGuard()
    {
        if(uiReviewMode||closed||exitRequested||batteryGuardBusy||settingsDataBusy||settingsPowerBusy||hardwareBusy||quickBusy||tuningApplying||gpuChanging||desktopServiceBusy||sleepOfficeBusy||!performance.ShouldCheckBattery)return;
        batteryGuardBusy=true;
        try
        {
            var result=await performance.EnforceBatteryGuardAsync();
            if(closed)return;
            if(result.Outcome==PerformanceOutcome.ReadbackConfirmed)
            {
                hardwareMessage="供电保护：离电 / Type-C 已回到均衡。";
                UpdateControlValues();
                NativeAppLog.Write("[power] battery guard restored balanced mode");
            }
            else if(result.Error is { } error)actions.Add("供电保护："+error.Message);
        }
        finally{batteryGuardBusy=false;}
    }
}
