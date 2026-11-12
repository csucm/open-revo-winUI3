using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private DisplayState? displayState;
    private int? screenBrightness;
    private bool displayBusy;
    private async Task ReadDisplay()
    {
        if(residentClient!=null){await SendResidentAsync(PanelOperation.ReadDisplay,backgroundRead:true);return;}
        try
        {
            var sample=await hardwareRuntime.Display.ReadAsync();
            if(closed)return;
            displayState=sample.Panel;screenBrightness=sample.Brightness;
            DiagnosticLog.Record("display.state",displayState);
            DiagnosticLog.Record("brightness.state",new{value=screenBrightness});
        }
        catch(Exception error)
        {
            displayState=null;screenBrightness=null;
            DiagnosticLog.Failure("display.read",error,new{topology=DisplayTopology.LastSnapshot},true);
        }
    }
    private async Task ChangeRefresh(int target)
    {
        if (dialogOpen || displayBusy || brightnessApplying || displayState is not { Active: true } expected || expected.Hertz == target) return;
        if(residentClient!=null){await ChangeResidentRefreshAsync(expected.Hertz,target);return;}
        displayBusy = true;
        UpdateDisplayValues();
        bool keep = false;
        RefreshTrial? trial = null;
        try
        {
            trial=await hardwareRuntime.Display.BeginRefreshTrialAsync(expected,target);
            int remaining=Math.Clamp((int)(trial.Deadline-DateTimeOffset.UtcNow).TotalMilliseconds,1,15000);
            if(!closed)keep=await PaintDialog($"保留 {target} Hz？",Paint.Text("15 秒内确认，否则自动恢复原刷新率。"),"保留","恢复",remaining);
        }
        catch (Exception e) { DiagnosticLog.Failure("display.refresh",e,new{original=expected.Hertz,target});actions.Add("刷新率操作：" + e.Message); }
        finally
        {
            dialogOpen = false;
            try
            {
                var result=trial==null?null:await hardwareRuntime.Display.FinishRefreshTrialAsync(trial.Id,keep);
                if(!closed)
                {
                    if(result?.Error is { } error&&!exitRequested)
                        await ShowMessage(result.Kept?"刷新率保留未确认":"刷新率恢复未确认",error.Message);
                    await ReadDisplay();
                    if(result?.ConfirmedHertz is int confirmed&&displayState?.Hertz==confirmed)
                        ConfirmedActionOsd((result.Kept?"屏幕刷新率 · ":"已恢复刷新率 · ")+confirmed+" Hz","refresh");
                }
            }
            finally{displayBusy=false;if(!closed)UpdateDisplayValues();}
        }
    }
}
