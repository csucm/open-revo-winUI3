using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool? requestedWinLocked;
    private int winStateGeneration;
    private long requestedWinAt;
    private Task? winWork;
    private Func<bool,Task<DeviceSwitchState>>? winWriterOverride;
    private sealed record WinUiTiming(bool Target,double QueueMs,double NativeMs,double TotalMs);
    private readonly List<WinUiTiming> winUiTimings=[];
    private Task RequestWinToggle()
    {
        if(closed||exitRequested||upstream==null&&winWriterOverride==null||quickState.WinLocked is not bool confirmed)return Task.CompletedTask;
        requestedWinLocked=!(requestedWinLocked??confirmed);
        requestedWinAt=System.Diagnostics.Stopwatch.GetTimestamp();
        winStateGeneration++;
        quickGeneration++;UpdateQuickActions();RefreshPaintedState();
        if(winWork is {IsCompleted:false})return winWork;
        return winWork=ApplyWinRequests();
    }
    private async Task ApplyWinRequests()
    {
        try
        {
            while(!closed&&requestedWinLocked is bool target)
            {
                long requested=requestedWinAt;
                double queued=System.Diagnostics.Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                var watch=System.Diagnostics.Stopwatch.StartNew();
                // One worker owns the latest intent. No hardware queue, EC
                // transaction, or optimistic firmware state participates here.
                bool locked=winWriterOverride!=null
                    ?(await winWriterOverride(target)).WinKeyLocked
                    :await Task.Run(()=>upstream!.SetWindowsWinKeyLocked(target));
                if(locked!=target)throw new IOException("Windows Win 软件锁未确认改变。");
                double nativeMs=watch.Elapsed.TotalMilliseconds;
                winStateGeneration++;
                quickState=quickState with{WinLocked=locked};
                if(requestedWinLocked==target){requestedWinLocked=null;ConfirmShortcutOsd(8,locked?1u:0u);}
                var timing=new WinUiTiming(target,queued,nativeMs,System.Diagnostics.Stopwatch.GetElapsedTime(requested).TotalMilliseconds);
                winUiTimings.Add(timing);if(winUiTimings.Count>16)winUiTimings.RemoveAt(0);
                actions.Add(System.Text.Json.JsonSerializer.Serialize(new{at=DateTimeOffset.Now,operation="windows-win-lock-confirmed",source="native-keyboard-hook",timing}));
                UpdateQuickActions();UpdateControlValues();
            }
        }
        catch(Exception error)
        {
            requestedWinLocked=null;
            winStateGeneration++;
            quickState=quickState with{WinLocked=null};
            try{if(upstream!=null)quickState=quickState with{WinLocked=await Task.Run(upstream.ReadWindowsWinKeyLocked)};}catch{}
            actions.Add(error.ToString());
            if(!closed)await ShowMessage("Windows Win 软件锁设置未完成",error.Message);
        }
        finally
        {
            lastQuickRead=DateTimeOffset.Now;
            if(!closed){UpdateQuickActions();UpdateControlValues();}
        }
    }

    private async Task RefreshWindowsWinLock()
    {
        if(upstream==null)return;
        int generation=winStateGeneration;
        try
        {
            bool locked=await Task.Run(upstream.ReadWindowsWinKeyLocked);
            if(!closed&&generation==winStateGeneration&&requestedWinLocked==null)
            {winStateGeneration++;quickState=quickState with{WinLocked=locked};UpdateQuickActions();UpdateControlValues();}
        }
        catch(Exception error){actions.Add("Windows Win 软件锁读取："+error.Message);}
    }
}
