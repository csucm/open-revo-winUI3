using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool? requestedWinLocked;
    private long requestedWinAt;
    private Task? winWork;
    private Func<bool,Task<DeviceSwitchState>>? winWriterOverride;
    private sealed record WinUiTiming(bool Target,double QueueMs,double HardwareMs,double TotalMs);
    private readonly List<WinUiTiming> winUiTimings=[];
    private Task RequestWinToggle()
    {
        if(closed||!hardware.Active||quickState.WinLocked is not bool confirmed)return Task.CompletedTask;
        requestedWinLocked=!(requestedWinLocked??confirmed);
        requestedWinAt=System.Diagnostics.Stopwatch.GetTimestamp();
        quickGeneration++;UpdateQuickActions();
        if(winWork is {IsCompleted:false})return winWork;
        return winWork=ApplyWinRequests();
    }
    private async Task ApplyWinRequests()
    {
        await operationQueue.WaitAsync();
        if(closed){requestedWinLocked=null;operationQueue.Release();return;}
        quickBusy=true;
        try
        {
            while(!closed&&requestedWinLocked is bool target)
            {
                long requested=requestedWinAt;
                double queued=System.Diagnostics.Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                var watch=System.Diagnostics.Stopwatch.StartNew();
                var result=await(winWriterOverride?.Invoke(target)??Task.Run(()=>hardware.SetWinKeyLocked(target)));
                double hardwareMs=watch.Elapsed.TotalMilliseconds;
                quickState=quickState with{WinLocked=result.WinKeyLocked};
                if(requestedWinLocked==target){requestedWinLocked=null;ConfirmShortcutOsd(8,result.WinKeyLocked?1u:0u);}
                var timing=new WinUiTiming(target,queued,hardwareMs,System.Diagnostics.Stopwatch.GetElapsedTime(requested).TotalMilliseconds);
                winUiTimings.Add(timing);if(winUiTimings.Count>16)winUiTimings.RemoveAt(0);
                actions.Add(System.Text.Json.JsonSerializer.Serialize(new{at=DateTimeOffset.Now,operation="win-key-ui-confirmed",timing}));
                UpdateQuickActions();
            }
        }
        catch(Exception error)
        {
            requestedWinLocked=null;
            if(!hardware.Active)quickState=quickState with{WinLocked=null};
            actions.Add(error.ToString());
            if(!closed)await ShowMessage("Win 键设置未完成",error.Message);
        }
        finally
        {
            quickBusy=false;lastQuickRead=DateTimeOffset.Now;operationQueue.Release();
            if(!closed){UpdateQuickActions();UpdateControlValues();}
        }
    }
}
