using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly Dictionary<string,ToggleSwitch> desktopPowerSwitches=[];
    private readonly Dictionary<string,TextBlock> desktopPowerHints=[];
    private PowerPolicyValue[] powerPolicies=[];
    private JsonElement serviceLighting,serviceIcc;
    private DesktopPowerState? servicePower;
    private ColorServiceState? serviceColor;
    private readonly HashSet<string> desktopBusyServices=[];
    private string? desktopRunningService;
    private Func<string,Task>? desktopServiceReviewExecutor;
    private bool desktopServiceBusy=>desktopBusyServices.Count>0;
    private bool colorServicesBusy=>desktopBusyServices.Any(name=>name is "display-color" or "restore-saved-color"||name.StartsWith("icc-"));
    private bool zoneServicesBusy=>desktopBusyServices.Any(name=>name.StartsWith("lighting-"));
    private bool syncingDesktopServices,desktopServicesReading;
    private DateTimeOffset lastDesktopServicesRead;
    private string desktopServiceError="";
    private enum DesktopServiceResult { Skipped, Succeeded, Failed }
    private sealed record DesktopServiceOutcome(DesktopServiceResult Result,string? Failure=null);
    private bool closingDesktopRuntime,desktopRuntimeClosed,hardwareRuntimeReleased;
    private async Task CloseDesktopRuntime()
    {
        if(closingDesktopRuntime)return;closingDesktopRuntime=true;
        DiagnosticLog.Record("exit.services.started");
        try
        {
            operationQueue.StopAccepting();
            await Task.Yield(); // Leave the AppWindow.Closing callback before closing again.
            if(!desktopRuntimeClosed)await hardwareRuntime.StopDesktopServicesAsync();
            desktopRuntimeClosed=true;
            DiagnosticLog.Record("exit.services.completed");
            if(closed)return;
            StartPendingUpdate();
            try{await hardwareRuntime.ReleaseAsync();}
            catch(Exception error){DiagnosticLog.Failure("exit.hardware.release_failed",error);}
            hardwareRuntimeReleased=true;
            if(!closed)Close();
        }
        catch(Exception error)
        {
            DiagnosticLog.Failure("exit.services.failed",error,new{trayHidden,mini});
            if(!closed&&!hardwareRuntimeReleased)
            {
                // Recovery must precede the modal await. Otherwise a tray-hidden
                // window waits for an invisible dialog while rejecting all wakes.
                exitRequested=false;
                if(!sessionEnding)operationQueue.Resume();
                WakeFromTray(mini?"mini":"full");
                DiagnosticLog.Record("exit.recovery.visible",new{mini});
                await ShowMessage("退出前恢复桌面状态",error.Message);
            }
        }
        finally{closingDesktopRuntime=false;}
    }
    private async Task InitializeDesktopServicesRuntime()
    {
        await ReadDesktopServices(true);while(desktopServicesReading&&!closed)await Task.Delay(30);
        if(!closed&&upstream!=null&&serviceColor?.SavedEnabled==true)
        {
            var preset=serviceColor!.ActivePreset.Copy();
            await DesktopServiceAction("restore-saved-color",()=>hardwareRuntime.Desktop.ApplyColor(true,preset));
        }
    }
    private ToggleSwitch ServiceToggle(string name,bool enabled,RoutedEventHandler handler)
    {
        var toggle=new ToggleSwitch{IsOn=enabled,OnContent="",OffContent="",MinWidth=0,Width=40,Height=24,MinHeight=24,HorizontalAlignment=HorizontalAlignment.Right};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle,name);toggle.Toggled+=handler;return toggle;
    }
    private FrameworkElement PowerServiceRow(string key,string glyph,string title,string description,bool inset=true)
    {
        var toggle=ServiceToggle(title,false,async(_,_)=>{if(!syncingDesktopServices)await ChangePowerService(key);});
        desktopPowerSwitches[key]=toggle;
        var hint=Ui.Text("读取中",11,muted:true);hint.MinHeight=14;desktopPowerHints[key]=hint;
        var row=DetailRow("",title,"",toggle,0,hint);row.MinHeight=40;return SuiteVisuals.Rule(row);
    }

    private async Task ReadDesktopServices(bool force=false)
    {
        if(closed||upstream==null||desktopServicesReading||desktopServiceBusy||(!force&&DateTimeOffset.Now-lastDesktopServicesRead<TimeSpan.FromSeconds(8)))return;
        desktopServicesReading=true;lastDesktopServicesRead=DateTimeOffset.Now;
        try
        {
            var state=await Task.Run(()=>(policies:DesktopPower.ReadPolicies(),power:upstream.ReadDesktopPower(),lighting:upstream.Read(15),color:upstream.ReadColorService(),icc:upstream.Read(14)));
            powerPolicies=state.policies;servicePower=state.power;serviceLighting=state.lighting;serviceColor=state.color;serviceIcc=state.icc;desktopServiceError="";
        }
        catch(Exception error){desktopServiceError=error.Message;actions.Add("服务读取："+error.Message);}
        finally{desktopServicesReading=false;if(!closed){SyncDesktopServices();SyncColorServices();SyncZoneServices();}}
    }
    private bool? PowerServiceValue(string key)=>key switch
    {
        "office"=>performance.SleepOfficeEnabled,"link"=>performance.PowerLinkEnabled,
        "master"=>powerPolicies.Any(p=>p.Supported)?powerPolicies.Where(p=>p.Supported).All(p=>p.Enabled==true):null,
        "usb"=>servicePower?.Trigger is byte trigger?(trigger&Vault.UsbChargeMask)!=0:null,
        "recovery"=>servicePower?.Recovery is byte recovery?(recovery&Vault.AcRecoveryMask)!=0:null,
        _=>powerPolicies.SingleOrDefault(p=>p.Definition.Key==key)?.Enabled
    };
    private static bool JsonBool(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.True;
    private string? PowerBusyOperation(string key)
    {
        bool policy=key=="master"||DesktopPower.Policies.Any(p=>p.Key==key);
        return desktopBusyServices.FirstOrDefault(name=>name=="power-"+key||key=="link"&&name=="power-overlay"||policy&&(name=="power-master"||key=="master"&&DesktopPower.Policies.Any(p=>name=="power-"+p.Key)));
    }
    private void SyncDesktopServices()
    {
        RefreshPaintedState();
        syncingDesktopServices=true;
        try
        {
            foreach(var(key,toggle)in desktopPowerSwitches)
            {
                bool? value=PowerServiceValue(key);var policy=powerPolicies.SingleOrDefault(p=>p.Definition.Key==key);
                bool supported=key is "office" or "link"?true:key=="master"?powerPolicies.Any(p=>p.Supported):policy?.Supported??value.HasValue;
                string? operation=PowerBusyOperation(key);bool busy=operation!=null;
                if(!busy)toggle.IsOn=value==true;toggle.IsEnabled=upstream!=null&&!busy&&supported&&(key is not("usb" or "recovery" or "office")||hardware.Active);
                string hint=!supported?desktopServicesReading?"读取中":"系统未提供":value.HasValue?value.Value?"已开启":"已关闭":"接电/电池不同";
                if(key=="master")hint=$"{powerPolicies.Count(p=>p.Supported&&p.Enabled==true)} / {powerPolicies.Count(p=>p.Supported)} 项";
                if(desktopPowerHints.TryGetValue(key,out var text))text.Text=busy?operation==desktopRunningService?"设置中":"等待中":hint;
                ToolTipService.SetToolTip(toggle,desktopServiceError.Length>0?desktopServiceError:key=="master"?"统一切换Windows实际支持的睡眠策略；不支持的单项保持禁用。":hint);
            }
        }
        finally{syncingDesktopServices=false;}
    }
    private async Task<DesktopServiceOutcome> DesktopServiceAction(string name,Action operation,bool takeover=false,bool showFailure=true)
    {
        if(closed||upstream==null||!desktopBusyServices.Add(name))return new(DesktopServiceResult.Skipped);
        SyncDesktopServiceGroup(name);
        if(!await operationQueue.EnterAsync())
        {
            desktopBusyServices.Remove(name);
            if(!closed)SyncDesktopServiceGroup(name);
            return new(DesktopServiceResult.Skipped);
        }
        string? failure=null;
        using var trace=DiagnosticLog.Begin("desktop.service",new{name,takeover});
        void RecordFailure(Exception error)
        {
            failure=error.Message;
            trace.Fail(error);
            actions.Add(name+"："+error);
            if(actions.Count>128)actions.RemoveAt(0);
        }
        try
        {
            while(desktopServicesReading&&!closed)await Task.Delay(30);
            if(closed)return new(DesktopServiceResult.Skipped);
            desktopRunningService=name;SyncDesktopServiceGroup(name);
            try
            {
                if(uiReviewMode&&desktopServiceReviewExecutor!=null)await desktopServiceReviewExecutor(name);
                else await Task.Run(()=>hardware.ApplyDesktopService(name,operation,takeover));
            }
            catch(Exception error){RecordFailure(error);}
            await RefreshDesktopServiceGroup(name);
        }
        catch(Exception error){RecordFailure(error);}
        finally{desktopBusyServices.Remove(name);desktopRunningService=null;operationQueue.Release();if(!closed)SyncDesktopServiceGroup(name);}
        if(failure==null)trace.Complete();
        if(failure!=null&&!closed&&showFailure)await ShowMessage("设置未完成",failure);
        return new(failure==null?DesktopServiceResult.Succeeded:DesktopServiceResult.Failed,failure);
    }
    private void SyncDesktopServiceGroup(string name)
    {
        if(name.StartsWith("power-"))SyncDesktopServices();
        else if(name.StartsWith("lighting-"))SyncZoneServices();
        else SyncColorServices();
    }
    private async Task RefreshDesktopServiceGroup(string name)
    {
        if(upstream==null)return;
        if(name is "power-usb" or "power-recovery")servicePower=await Task.Run(()=>upstream.ReadDesktopPower());
        else if(name=="power-master"||DesktopPower.Policies.Any(p=>name=="power-"+p.Key))powerPolicies=await Task.Run(DesktopPower.ReadPolicies);
        else if(name.StartsWith("lighting-"))serviceLighting=await Task.Run(()=>upstream.Read(15));
        else if(!name.StartsWith("power-"))
        {
            if(!name.StartsWith("icc-"))serviceColor=await Task.Run(()=>upstream.ReadColorService());
            serviceIcc=await Task.Run(()=>upstream.Read(14));
        }
    }
    private async Task ChangePowerService(string key)
    {
        if(upstream==null)return;
        bool target=desktopPowerSwitches[key].IsOn;
        await ChangePowerService(key,target);
    }
    private async Task ChangePowerService(string key,bool target)
    {
        if(upstream==null)return;
        if(key is "office" or "link")
        {
            if(uiReviewMode&&desktopServiceReviewExecutor!=null)
            {
                await DesktopServiceAction("power-"+key,()=>{});
                return;
            }
            var policy=key=="office"?PerformanceDesktopPolicy.SleepOffice:PerformanceDesktopPolicy.PowerLink;
            await RunDesktopPerformanceAction("power-"+key,()=>performance.SetDesktopPolicy(policy,target));
            return;
        }
        await DesktopServiceAction("power-"+key,()=>hardwareRuntime.Desktop.SetPowerPolicy(key,target),key is "usb" or "recovery");
    }
    private async Task SyncPerformanceOverlay()
    {
        if(desktopServiceBusy||hardwareBusy||quickBusy||tuningApplying||sleepOfficeBusy||!performance.ShouldSynchronizePowerLink)return;
        await RunDesktopPerformanceAction("power-overlay",performance.SynchronizePowerLink);
    }

    private async Task RunDesktopPerformanceAction(string name,Func<DesktopPerformanceOperation> command)
    {
        if(closed||exitRequested||settingsDataBusy||dataStorageExitPending||!desktopBusyServices.Add(name))return;
        SyncDesktopServiceGroup(name);
        Exception? report=null;
        using var trace=DiagnosticLog.Begin("desktop.service",new{name});
        try
        {
            while(desktopServicesReading&&!closed&&!exitRequested)await Task.Delay(30);
            if(closed||exitRequested)return;
            var operation=command();
            if(await operation.Started&&!operation.Completion.IsCompleted&&!closed)
            {
                desktopRunningService=name;
                SyncDesktopServiceGroup(name);
            }
            var result=await operation.Completion;
            var summary=new{result.Outcome,result.Mode};
            if(result.Error is { } failure)
            {
                trace.Fail(failure,summary);
                actions.Add(name+"："+failure);
                if(actions.Count>128)actions.RemoveAt(0);
            }
            else trace.Complete(summary);
            if(result.ReportFailure)report=result.Error;
        }
        finally
        {
            desktopBusyServices.Remove(name);
            if(desktopRunningService==name)desktopRunningService=null;
            if(!closed)SyncDesktopServiceGroup(name);
        }
        if(report!=null&&!closed&&!exitRequested)await ShowMessage("设置未完成",report.Message);
    }
}
