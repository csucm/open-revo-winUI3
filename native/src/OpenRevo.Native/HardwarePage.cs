using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core.Hal;
using System.Text.Json;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly HardwareSession hardware=HardwareSession.ForThisMachine();
    private bool hardwareBusy,syncingTakeover,takeoverChanging;
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
            automaticTakeoverPending=false;
            if(!toggle.IsOn)
            {
                hardware.Release();tuningBaseline=null;tuningDraft=null;
                quickState=quickState with{Camera=null,WinLocked=null};
                hardwareMessage="已停止本次硬件控制。";UpdateControlValues();return;
            }
            toggle.IsEnabled=false;
            await RunHardware(()=>{var state=hardware.Takeover();if(!hardware.CoreDelegated)hardware.ProbePerformance();return new{connected=hardware.Active,state};},"已重新读取并接管。");
            if(hardware.Active){await LoadPanelHardware();quickState=await Task.Run(ReadQuickState);}
            }
            finally{takeoverChanging=false;if(!closed)UpdateControlValues();}
        };
        ToolTipService.SetToolTip(toggle,"关闭后停止本次控制；重新打开会重新读取设备状态");
        return toggle;
    }
    private async Task RunHardware(Func<object> operation,string success)
    {
        if(closed||DeveloperLayoutPreview)return;
        await operationQueue.WaitAsync();
        if(closed){operationQueue.Release();return;}
        hardwareBusy=true;InvalidateOverviewPowerTargets();
        using var trace=OpenRevo.Core.DiagnosticLog.Begin("hardware.action",new{page=currentPage,mini,connected=hardware.Active});
        try
        {
            var result=await Task.Run(operation);actions.Add(JsonSerializer.Serialize(result));
            trace.Complete(new{result});
            hardwareMessage=success;controlAt=DateTimeOffset.Now;ecEnabled=true;lastEc=DateTimeOffset.MinValue;
            if(hardware.State is {} state)ec=ec with{BatteryLimit=state.Upper,FanBoost=state.FanBoost};
            if(actions.Count>128)actions.RemoveRange(0,actions.Count-128);
        }
        catch(Exception error)
        {
            trace.Fail(error,new{page=currentPage,connected=hardware.Active});
            hardwareMessage="设置未完成，可到设置中重新接管。";actions.Add($"{DateTimeOffset.Now:O} hardware operation stopped: {error.Message}");
            if(!closed)await ShowMessage("设置未完成","已停止后续操作。请检查连接，并在设置中关闭再打开硬件接管开关。");
        }
        finally{hardwareBusy=false;operationQueue.Release();if(!closed){UpdateControlValues();Tick();}}
    }
    private void UpdateControlValues()
    {
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
