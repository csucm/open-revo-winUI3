using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
using OpenRevo.Core.Hal;
using System.Text.Json;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool hardwareOperationBusy,syncingTakeover,takeoverChanging;
    private int managedHardwareActions;
    private bool hardwareBusy=>hardwareOperationBusy||managedHardwareActions>0;
    private string hardwareMessage="正在核验本机并自动接管…";
    private DateTimeOffset? controlAt;
    private ToggleSwitch? takeoverSwitch;
    private ToggleSwitch TakeoverSwitch()
    {
        var toggle=takeoverSwitch=ServiceToggle("硬件接管",hardware.Active,(_,_)=>{});
        toggle.IsEnabled=!hardwareBusy&&!quickBusy&&CoreMachine.Current.Supported;
        toggle.Toggled+=async (_,_)=>
        {
            if(syncingTakeover||takeoverChanging||closed)return;
            takeoverChanging=true;toggle.IsEnabled=false;
            try
            {
            if(!toggle.IsOn)
            {
                await ReleaseHardwareControl();
                return;
            }
            toggle.IsEnabled=false;
            await TakeoverHardware();
            }
            finally{takeoverChanging=false;if(!closed)UpdateControlValues();}
        };
        ToolTipService.SetToolTip(toggle,"关闭后停止本次控制；重新打开会重新读取设备状态");
        return toggle;
    }

    private async Task TakeoverHardware()
    {
        if(closed||exitRequested||settingsDataBusy||DeveloperLayoutPreview)return;
        managedHardwareActions++;
        InvalidateOverviewPowerTargets();
        using var trace=DiagnosticLog.Begin("hardware.action",new{page=currentPage,mini,connected=hardware.Active});
        try
        {
            var result=await devices.TakeoverAsync();
            if(closed)return;
            if(result.Error is { } error)
            {
                trace.Fail(error);
                await ShowHardwareFailure(error);
            }
            else if(result.Outcome==TakeoverOutcome.Active)
            {
                trace.Complete(new{connected=true,result.State});
                ShowHardwareSuccess("已重新读取并接管。");
            }
            if(hardware.Active&&!closed&&!exitRequested)
            {
                await LoadPanelHardware();
                if(!closed)ApplyQuickRead(await Task.Run(ReadQuickState));
            }
        }
        finally{managedHardwareActions--;if(!closed){UpdateControlValues();Tick();}}
    }

    private async Task ReleaseHardwareControl()
    {
        if(!await hardwareRuntime.ReleaseControlAsync()||closed)return;
        tuningBaseline=null;tuningDraft=null;
        quickState=quickState with{Camera=null};
        hardwareMessage="已停止本次硬件控制。";
    }

    private async Task RunHardware(Func<object> operation,string success)
    {
        if(closed||exitRequested||DeveloperLayoutPreview||!await operationQueue.EnterAsync())return;
        hardwareOperationBusy=true;InvalidateOverviewPowerTargets();
        using var trace=OpenRevo.Core.DiagnosticLog.Begin("hardware.action",new{page=currentPage,mini,connected=hardware.Active});
        try
        {
            int? beforeLimit=hardware.State?.Upper;var result=await Task.Run(operation);actions.Add(JsonSerializer.Serialize(result));
            trace.Complete(new{result});
            if(hardware.State is {} charge&&beforeLimit!=charge.Upper)ConfirmedActionOsd("充电上限 · "+charge.Upper+"%","battery");if(success.Contains("撤销")&&success.Contains("灯光"))ConfirmedActionOsd("已撤销上次灯光更改","lighting");
            ShowHardwareSuccess(success);
        }
        catch(AcPowerRequiredException error)
        {
            trace.Complete(new{outcome="ac-power-required"});
            hardwareMessage=error.Message;
            actions.Add($"{DateTimeOffset.Now:O} hardware operation blocked: {error.Message}");
            if(!closed)await ShowMessage("需要接通电源",error.Message);
        }
        catch(Exception error)
        {
            trace.Fail(error,new{page=currentPage,connected=hardware.Active});
            await ShowHardwareFailure(error);
        }
        finally{hardwareOperationBusy=false;operationQueue.Release();if(!closed){UpdateControlValues();Tick();}}
    }

    private Task SetChargeLimit(int limit,bool explainDischarge=false) =>
        residentClient!=null?RunResidentActionAsync(PanelOperation.SetBatteryLimit,limit):RunDeviceCommand(()=>devices.SetChargeLimitAsync(limit),
            explainDischarge?"充电上限已回读确认；已有电量不会主动下降。":"充电上限已回读确认。",
            "Core 已发送充电上限命令。");

    private Task ToggleFanBoost(bool? observed) =>
        residentClient!=null?RunResidentActionAsync(PanelOperation.ToggleFanBoost):
        RunDeviceCommand(()=>devices.ToggleFanBoostAsync(observed),"强冷状态已回读确认。","Core 已发送强冷命令。");

    private async Task RunDeviceCommand(Func<Task<DeviceCommandResult>> command,string confirmed,string sent)
    {
        if(closed||exitRequested||sessionEnding||settingsDataBusy||dataStorageExitPending||DeveloperLayoutPreview)return;
        managedHardwareActions++;
        InvalidateOverviewPowerTargets();
        using var trace=DiagnosticLog.Begin("hardware.action",new{page=currentPage,mini,connected=hardware.Active});
        try
        {
            var result=await command();
            if(result.Error is { } error)
            {
                trace.Fail(error);
                if(!closed)await ShowHardwareFailure(error);
                return;
            }
            var summary=new{result.Outcome,result.Receipt};
            trace.Complete(summary);
            if(closed)return;
            if(result.Outcome is DeviceCommandOutcome.ReadbackConfirmed or DeviceCommandOutcome.CommandSent)
            {
                actions.Add(JsonSerializer.Serialize(summary));
                if(result.Receipt is { } receipt&&receipt.Before.Upper!=receipt.After.Upper)
                    ConfirmedActionOsd("充电上限 · "+receipt.After.Upper+"%","battery");
                ShowHardwareSuccess(result.Outcome==DeviceCommandOutcome.ReadbackConfirmed?confirmed:sent);
            }
        }
        finally{managedHardwareActions--;if(!closed){UpdateControlValues();Tick();}}
    }

    private async Task<PerformanceResult> RunPerformance(Func<Task<PerformanceResult>> command, string success,
        string? rejectionTitle=null)
    {
        if(closed||exitRequested||sessionEnding||settingsDataBusy||dataStorageExitPending||DeveloperLayoutPreview)
            return new(PerformanceOutcome.Skipped);
        managedHardwareActions++;
        InvalidateOverviewPowerTargets();
        using var trace=DiagnosticLog.Begin("hardware.action",new{page=currentPage,mini,connected=hardware.Active});
        try
        {
            var result=await command();
            var summary=new{result.Outcome,result.Mode,result.PreviousMode,result.Classic,result.Tuning,result.IndicatorWarning};
            if(result.Error is { } error)trace.Fail(error,summary);
            else trace.Complete(summary);
            if(closed)return result;
            switch(result.Outcome)
            {
                case PerformanceOutcome.ReadbackConfirmed:
                    actions.Add(JsonSerializer.Serialize(summary));
                    ShowHardwareSuccess(success,result.IndicatorWarning);
                    break;
                case PerformanceOutcome.CommandSent:
                    actions.Add(JsonSerializer.Serialize(summary));
                    ShowHardwareSuccess("Core 已发送 CPU 调校命令，未回读确认。");
                    break;
                case PerformanceOutcome.Rejected when result.Error is AcPowerRequiredException ac:
                    hardwareMessage=ac.Message;
                    actions.Add($"{DateTimeOffset.Now:O} hardware operation blocked: {ac.Message}");
                    if(!exitRequested)await ShowMessage("需要接通电源",ac.Message);
                    break;
                case PerformanceOutcome.Rejected when rejectionTitle!=null:
                    if(!exitRequested)await ShowMessage(rejectionTitle,result.Error!.Message);
                    break;
                case PerformanceOutcome.Rejected or PerformanceOutcome.Unknown:
                    await ShowHardwareFailure(result.Error!);
                    break;
            }
            return result;
        }
        finally{managedHardwareActions--;if(!closed){UpdateControlValues();Tick();}}
    }

    private void ShowHardwareSuccess(string message,string? warning=null)
    {
        hardwareMessage=warning==null?message:message+" "+warning;
        controlAt=DateTimeOffset.Now;ecEnabled=true;lastEc=DateTimeOffset.MinValue;
        if(hardware.State is {} state)ec=ec with{BatteryLimit=state.Upper,FanBoost=state.FanBoost};
        if(actions.Count>128)actions.RemoveRange(0,actions.Count-128);
    }

    private async Task ShowHardwareFailure(Exception error)
    {
        bool active=hardware.Active;
        hardwareMessage=active?"本次设置未完成，硬件仍已接管。":"操作已停止，请重新读取并接管。";
        actions.Add($"{DateTimeOffset.Now:O} hardware operation stopped: {error.Message}");
        if(!closed&&!exitRequested)await ShowMessage("设置未完成",error.Message+"\n\n"+(active?"硬件仍已接管，可继续使用其他已支持功能。":"请在设置中关闭再打开硬件接管，重新读取当前状态。"));
    }
    private void UpdateControlValues()
    {
        SynchronizeModeKeyMonitor();
        RefreshPaintedState();
        if(takeoverSwitch!=null)
        {
            syncingTakeover=true;
            try{takeoverSwitch.IsOn=hardware.Active;takeoverSwitch.IsEnabled=!takeoverChanging&&!hardwareBusy&&!quickBusy&&CoreMachine.Current.Supported;}
            finally{syncingTakeover=false;}
        }
        Put("performanceTitle","性能模式："+PerformanceName);Put("performanceDetail","当前："+PerformanceName);
        foreach(var(mode,button)in performanceButtons)
        {
            button.IsEnabled=hardware.Active&&!hardwareBusy&&(mode==4?hardware.SupportsCustomTuning:hardware.SupportsPerformance);SetChoiceVisual(button,hardware.CurrentMode==mode);
        }
        if(compactStatus!=null)
        {
            compactStatus.Content=hardware.Active?"● 已接管":"● 未接管";
            ToolTipService.SetToolTip(compactStatus,hardware.Active?"已连接，可以直接调整设置":hardwareMessage);
        }
        UpdateQuickActions();UpdateCompactValues();UpdateExtraSwitchButtons();ObserveHardwareOsd();UpdateOverviewValues();
    }
}
