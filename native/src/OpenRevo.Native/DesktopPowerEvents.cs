using System.Runtime.InteropServices;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private nint lidNotification;
    private bool sleepOfficeBusy;
    private readonly SemaphoreSlim sleepOfficeQueue=new(1,1);
    private PerformanceReceipt? automaticOfficeReceipt;
    private static readonly Guid LidPowerGuid=new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    [DllImport("user32.dll",SetLastError=true)]private static extern nint RegisterPowerSettingNotification(nint window,ref Guid setting,uint flags);
    [DllImport("user32.dll")]private static extern bool UnregisterPowerSettingNotification(nint notification);
    private void AttachDesktopPowerEvents()
    {
        Guid lid=LidPowerGuid;lidNotification=RegisterPowerSettingNotification(captionWindow,ref lid,0);
        if(lidNotification==0)actions.Add("合盖通知未注册；睡眠通知仍可用。");
    }
    private void DetachDesktopPowerEvents(){if(lidNotification!=0){UnregisterPowerSettingNotification(lidNotification);lidNotification=0;}zoneDebounce?.Cancel();colorDebounce?.Cancel();}
    private void HandleDesktopPowerMessage(uint message,nuint wParam,nint lParam)
    {
        if(message!=0x0218||closed||settingsDataBusy||settingsPowerBusy)return;
        bool? sleeping=wParam switch{4=>true,7 or 18=>false,_=>null};
        if(wParam==0x8013&&lParam!=0&&Marshal.PtrToStructure<Guid>(lParam)==LidPowerGuid&&Marshal.ReadInt32(lParam,16)==4)sleeping=Marshal.ReadInt32(lParam,20)==0;
        if(sleeping is bool suspended&&suspended!=declaredPowerSuspended)
        {
            declaredPowerSuspended=suspended;
            declaredRestoreAc=null;
        }
        if(sleeping.HasValue&&(desktopPreferences.SleepOffice||automaticOfficeReceipt!=null))
        {
            bool value=sleeping.Value;
            DispatcherQueue.TryEnqueue(async()=>await ApplyAutomaticOffice(value));
        }
    }
    private async Task ApplyAutomaticOffice(bool sleeping)
    {
        await sleepOfficeQueue.WaitAsync();
        try
        {
            if(closed||!hardware.Active||hardware.CoreDelegated||sleeping&&(!desktopPreferences.SleepOffice||automaticOfficeReceipt!=null)||!sleeping&&automaticOfficeReceipt==null)return;
            sleepOfficeBusy=true;await operationQueue.WaitAsync();
            try
            {
                await Task.Run(()=>
                {
                    if(sleeping)
                    {
                        int mode=hardware.ProbePerformance().Mode;if(mode==1)return;
                        automaticOfficeReceipt=hardware.SetPerformanceMode(1);
                    }
                    else
                    {
                        var receipt=automaticOfficeReceipt!;automaticOfficeReceipt=null;
                        hardware.RestoreAutomaticOffice(receipt);
                    }
                    if(desktopPreferences.PerformanceLink&&upstream!=null)
                    {
                        int mode=hardware.CurrentMode??2;
                        OpenRevo.Core.DesktopPower.SetOverlay(upstream,mode);linkedPerformanceMode=mode;
                    }
                });
            }
            catch(Exception error){actions.Add("睡眠自动办公："+error.Message);}
            finally{operationQueue.Release();if(!closed)UpdateControlValues();}
        }
        finally{sleepOfficeBusy=false;sleepOfficeQueue.Release();}
    }
}
