using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool automaticTakeoverPending;
    private bool startupAcquiring;
    private DateTimeOffset lastTakeoverAttempt;
    private async Task AcquireAtStartup()
    {
        lastTakeoverAttempt = DateTimeOffset.Now;
        string? blocked = await Task.Run(HardwareSession.CheckEnvironment);
        if (blocked != null)
        {
            automaticTakeoverPending = blocked.StartsWith("请先正常退出其他硬件控制程序：", StringComparison.Ordinal);
            hardwareMessage = automaticTakeoverPending ? "等待原版退出，随后自动连接。" : "此设备尚未完成适配。";
            actions.Add(blocked); return;
        }
        try
        {
            var state = await Task.Run(() => hardware.Takeover());
            automaticTakeoverPending = false; controlAt = DateTimeOffset.Now;
            hardwareMessage = "已自动连接，可以直接调整设置。";
            if(state!=null)ec = ec with { BatteryLimit = state.Upper, FanBoost = state.FanBoost };
            if(!hardware.CoreDelegated)try { await Task.Run(() => hardware.ProbePerformance()); } catch (Exception error) { actions.Add(error.Message); }
        }
        catch (Exception error) { OpenRevo.Core.DiagnosticLog.Failure("takeover.startup",error);automaticTakeoverPending = false; hardwareMessage = "暂未连接，可到设置中重新接管。"; actions.Add(error.ToString()); }
    }
    private async Task RetryStartupTakeover()
    {
        if (!automaticTakeoverPending || startupAcquiring || closed || DateTimeOffset.Now-lastTakeoverAttempt < TimeSpan.FromSeconds(6)) return;
        startupAcquiring = true;
        try { await AcquireAtStartup(); if (hardware.Active && !closed) { await LoadPanelHardware(); if(!closed)Render(); } }
        finally { startupAcquiring = false; }
    }
}
