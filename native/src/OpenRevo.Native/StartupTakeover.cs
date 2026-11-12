using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool automaticTakeoverPending=>devices.AutomaticTakeoverPending;
    private bool startupAcquiring;
    private async Task<bool> AcquireAtStartup(bool retryOnly=false)
    {
        var result=await devices.AutomaticTakeoverAsync(retryOnly);
        if(closed)return false;
        if(result.Outcome==TakeoverOutcome.Blocked)
        {
            hardwareMessage = automaticTakeoverPending ? "等待原版退出，随后自动连接。" : "此设备尚未完成适配。";
            actions.Add(result.Reason!);
            return false;
        }
        if(result.Outcome==TakeoverOutcome.Active)
        {
            controlAt = DateTimeOffset.Now;
            hardwareMessage = "已自动连接，可以直接调整设置。";
            if(result.State is { } state)ec = ec with { BatteryLimit = state.Upper, FanBoost = state.FanBoost };
            if(result.Error is { } error)actions.Add(error.Message);
            return true;
        }
        if(result.Error is { } failure)
        {
            DiagnosticLog.Failure("takeover.startup",failure);
            hardwareMessage = "暂未连接，可到设置中重新接管。";
            actions.Add(failure.ToString());
        }
        return false;
    }
    private async Task RetryStartupTakeover()
    {
        if (!automaticTakeoverPending || startupAcquiring || closed || exitRequested) return;
        startupAcquiring = true;
        try { if(await AcquireAtStartup(retryOnly:true)&&!closed&&!exitRequested) { await LoadPanelHardware(); if(!closed)Render(); } }
        finally { startupAcquiring = false; }
    }
}
