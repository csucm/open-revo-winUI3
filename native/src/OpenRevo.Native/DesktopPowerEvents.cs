using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private DesktopPowerNotification? desktopPowerNotification;
    private bool sleepOfficeBusy=>hardwareRuntime.PerformanceInput.SleepOfficeBusy;
    private void AttachDesktopPowerEvents()
    {
        desktopPowerNotification=new(captionWindow);
        if(!desktopPowerNotification.LidRegistered)actions.Add("合盖通知未注册；睡眠通知仍可用。");
    }
    private void DetachDesktopPowerEvents(){desktopPowerNotification?.Dispose();desktopPowerNotification=null;zoneDebounce?.Cancel();colorDebounce?.Cancel();}
    private void HandleDesktopPowerMessage(uint message,nuint wParam,nint lParam)
    {
        if(closed||exitRequested||uiReviewMode||settingsDataBusy||settingsPowerBusy)return;
        if(DesktopPowerNotification.ReadSuspension(message,wParam,lParam) is bool sleeping)
            _=ApplyAutomaticOffice(sleeping);
    }
    private async Task ApplyAutomaticOffice(bool sleeping)
    {
        try
        {
            var result=await hardwareRuntime.PerformanceInput.ObserveSuspensionAsync(sleeping);
            if(!closed&&(result.Error??result.FollowUpError) is { } error)actions.Add("睡眠自动办公："+error.Message);
            if(!closed&&result.Outcome!=PerformanceOutcome.Skipped)UpdateControlValues();
        }
        catch(Exception error){DiagnosticLog.Failure("power.sleep-office.failed",error);}
    }
}
