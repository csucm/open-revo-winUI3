namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool batteryGuardBusy;
    private DateTimeOffset lastBatteryGuard;
    private async Task EnforceBatteryPerformanceGuard()
    {
        if(uiReviewMode||closed||exitRequested||systemPreferences.UnlockBatteryLimit||!hardware.Active||batteryGuardBusy||settingsDataBusy||settingsPowerBusy||hardwareBusy||quickBusy||tuningApplying||gpuChanging||desktopServiceBusy||sleepOfficeBusy||hardware.CurrentMode is not(3 or 4)||DateTimeOffset.Now-lastBatteryGuard<TimeSpan.FromSeconds(3))return;
        batteryGuardBusy=true;lastBatteryGuard=DateTimeOffset.Now;
        await operationQueue.WaitAsync();
        try
        {
            if(systemPreferences.UnlockBatteryLimit||!hardware.Active||upstream==null)return;
            bool? original=hardware.CoreDelegated?latest?.OnAc:await Task.Run(hardware.ProbeOriginalAdapter);
            if(original!=false)return;
            await Task.Run(()=>{if(hardware.CoreDelegated){if(hardware.CurrentMode is 3 or 4)hardware.ApplyModeCommand(upstream,2);}else if(hardware.ProbePerformance().Mode is 3 or 4)hardware.SetFactoryPerformance(upstream,2);});
            hardwareMessage="供电保护：离电 / Type-C 已回到均衡。";UpdateControlValues();NativeAppLog.Write("[power] battery guard restored balanced mode");
        }
        catch(Exception error){actions.Add("供电保护："+error.Message);}
        finally{batteryGuardBusy=false;operationQueue.Release();}
    }
}
