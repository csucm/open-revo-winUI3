using System.Diagnostics;
using OpenRevo.Core;
using OpenRevo.Core.Hal;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private AcpiModeKeyObserver? modeKeyObserver;
    private bool modeKeyStarting,modeKeyAttempted,physicalModeChanging;
    private int modeKeyGeneration;
    private long lastModeKey;
    private void SynchronizeModeKeyMonitor()
    {
        if(uiReviewMode||closed||exitRequested||upstream==null||!hardware.Active||!CoreMachine.Current.Ready||!CoreMachine.Current.ClassicEc||!hardware.SupportsPerformance)
        {StopModeKeyMonitor();return;}
        if(modeKeyObserver!=null||modeKeyStarting||modeKeyAttempted)return;
        modeKeyStarting=true;modeKeyAttempted=true;
        _=StartModeKeyMonitor(modeKeyGeneration);
    }
    private async Task StartModeKeyMonitor(int generation)
    {
        AcpiModeKeyObserver? created=null;
        try
        {
            created=await Task.Run(()=>new AcpiModeKeyObserver(code=>DispatcherQueue.TryEnqueue(()=>
            {if(generation==modeKeyGeneration)_=HandlePhysicalModeKey(code);})));
            if(generation!=modeKeyGeneration||closed||exitRequested||!hardware.Active){created.Dispose();return;}
            modeKeyObserver=created;created=null;
        }
        catch(Exception error){DiagnosticLog.Failure("modekey.subscription.failed",error);}
        finally{created?.Dispose();if(generation==modeKeyGeneration)modeKeyStarting=false;}
    }
    private void StopModeKeyMonitor()
    {
        if(modeKeyObserver==null&&!modeKeyStarting&&!modeKeyAttempted)return;
        modeKeyGeneration++;modeKeyAttempted=false;modeKeyStarting=false;
        var previous=modeKeyObserver;modeKeyObserver=null;previous?.Dispose();
    }
    private async Task HandlePhysicalModeKey(int code)
    {
        if(closed||exitRequested||sessionEnding||uiReviewMode||!hardware.Active||!CoreMachine.Current.ClassicEc||!hardware.SupportsPerformance)return;
        long now=Stopwatch.GetTimestamp();
        if(lastModeKey!=0&&Stopwatch.GetElapsedTime(lastModeKey,now)<TimeSpan.FromMilliseconds(500))return;
        if(physicalModeChanging||hardwareBusy||quickBusy||tuningApplying||gpuChanging||takeoverChanging||operationQueue.CurrentCount==0)
        {DiagnosticLog.Record("modekey.ignored",new{code,reason="hardware-busy"});return;}
        lastModeKey=now;physicalModeChanging=true;
        try
        {
            await RunHardware(()=>
            {
                int before=hardware.ProbePerformance().Mode;
                bool limited=!hardware.AllowBatteryPerformance&&Telemetry.ReadAc()!=true;
                int target=limited?(before==1?2:1):(before==1?2:before==2?3:1);
                DiagnosticLog.Record("modekey.intent",new{code,before,target,batteryLimited=limited});
                return hardware.ApplyModeCommand(upstream,target,target==3);
            },"机身按键切换档位已回读确认。");
        }
        finally{physicalModeChanging=false;}
    }
}
