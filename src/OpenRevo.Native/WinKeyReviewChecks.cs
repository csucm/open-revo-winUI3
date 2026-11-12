using Microsoft.UI.Xaml;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunWinQueueChecks(Action<string,bool> check)
    {
        var original=quickState;var writer=winWriterOverride;
        var release=new TaskCompletionSource<OpenRevo.Core.Hal.DeviceSwitchState>();
        var writes=new List<bool>();var savedTimings=winUiTimings.ToArray();
        try
        {
            quickState=quickState with{WinLocked=false};UpdateQuickActions();
            winWriterOverride=target=>{writes.Add(target);return writes.Count==1?release.Task:Task.FromResult(new OpenRevo.Core.Hal.DeviceSwitchState(target,false));};
            var first=RunQuickAction("win");var reverse=RunQuickAction("win");
            check("Rapid Win clicks retain the reverse request while a write is pending",requestedWinLocked==false&&writes.SequenceEqual(new[]{true})&&quickButtons["win"].Tag?.ToString()=="切换中");
            check("Pending Win request does not claim an unconfirmed lock",quickState.WinLocked==false);
            release.SetResult(new(true,false));await Task.WhenAll(first,reverse);
            check("Win worker confirms the final intent instead of dropping a busy click",writes.SequenceEqual(new[]{true,false})&&requestedWinLocked==null&&quickState.WinLocked==false&&quickButtons["win"].Tag?.ToString()=="已解锁");
        }
        finally
        {
            release.TrySetResult(new(true,false));if(winWork!=null)await winWork;
            winWriterOverride=writer;quickState=original;winUiTimings.Clear();winUiTimings.AddRange(savedTimings);UpdateQuickActions();
        }
    }
    private async Task RunLiveWinQueueChecks(Action<string,bool> check)
    {
        bool before=hardware.ProbeSwitches().WinKeyLocked;
        quickState=quickState with{WinLocked=before};UpdateQuickActions();
        winUiTimings.Clear();
        try
        {
            var tasks=new List<Task>();var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<8;i++)tasks.Add(RunQuickAction("win"));
            check("Eight rapid Win clicks are accepted immediately with pending feedback",quickButtons["win"].Tag?.ToString()=="切换中"&&watch.ElapsedMilliseconds<500);
            await Task.WhenAll(tasks);
            check("Rapid Win UI workflow confirms and restores the last requested state",hardware.ProbeSwitches().WinKeyLocked==before&&quickState.WinLocked==before&&requestedWinLocked==null&&winUiTimings.Count==2);
        }
        finally
        {
            if(hardware.Active&&hardware.ProbeSwitches().WinKeyLocked!=before)
                await Task.Run(()=>hardware.SetWinKeyLocked(before));
        }
    }
}
