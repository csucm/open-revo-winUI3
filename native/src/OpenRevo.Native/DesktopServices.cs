using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly DesktopServicePreferences desktopPreferences=DesktopServicePreferences.Load();
    private readonly Dictionary<string,ToggleSwitch> desktopPowerSwitches=[];
    private readonly Dictionary<string,TextBlock> desktopPowerHints=[];
    private PowerPolicyValue[] powerPolicies=[];
    private JsonElement servicePower,serviceLighting,serviceColor,serviceIcc;
    private readonly HashSet<string> desktopBusyServices=[];
    private string? desktopRunningService;
    private Func<string,Task>? desktopServiceReviewExecutor;
    private bool desktopServiceBusy=>desktopBusyServices.Count>0;
    private bool colorServicesBusy=>desktopBusyServices.Any(name=>name is "display-color" or "restore-saved-color"||name.StartsWith("icc-"));
    private bool zoneServicesBusy=>desktopBusyServices.Any(name=>name.StartsWith("lighting-"));
    private bool syncingDesktopServices,desktopServicesReading;
    private DateTimeOffset lastDesktopServicesRead;
    private string desktopServiceError="";
    private int linkedPerformanceMode;
    private bool closingDesktopRuntime,desktopRuntimeClosed;
    private async Task CloseDesktopRuntime()
    {
        if(closingDesktopRuntime)return;closingDesktopRuntime=true;
        DiagnosticLog.Record("exit.services.started");
        try{if(upstream!=null)await Task.Run(upstream.StopDesktopServices);desktopRuntimeClosed=true;DiagnosticLog.Record("exit.services.completed");if(!closed){StartPendingUpdate();Close();}}
        catch(Exception error)
        {
            DiagnosticLog.Failure("exit.services.failed",error,new{trayHidden,mini});
            if(!closed)
            {
                // Recovery must precede the modal await. Otherwise a tray-hidden
                // window waits for an invisible dialog while rejecting all wakes.
                exitRequested=false;
                WakeFromTray(mini?"mini":"full");
                DiagnosticLog.Record("exit.recovery.visible",new{mini});
                await ShowMessage("退出前恢复色彩",error.Message);
            }
        }
        finally{closingDesktopRuntime=false;}
    }
    private async Task InitializeDesktopServicesRuntime()
    {
        await ReadDesktopServices(true);while(desktopServicesReading&&!closed)await Task.Delay(30);
        if(!closed&&upstream!=null&&JsonBool(serviceColor,"enabled"))
        {
            var preset=serviceColor.GetProperty("active_preset").Clone();
            await DesktopServiceAction("restore-saved-color",()=>upstream.ExecuteFeature(7,new{enabled=true,preset}));
        }
    }
    private ToggleSwitch ServiceToggle(string name,bool enabled,RoutedEventHandler handler)
    {
        var toggle=new ToggleSwitch{IsOn=enabled,OnContent="",OffContent="",MinWidth=0,Width=52,Height=32,MinHeight=32,HorizontalAlignment=HorizontalAlignment.Right};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle,name);toggle.Toggled+=handler;return toggle;
    }
    private FrameworkElement PowerServiceRow(string key,string glyph,string title,string description,bool inset=true)
    {
        var toggle=ServiceToggle(title,false,async(_,_)=>{if(!syncingDesktopServices)await ChangePowerService(key);});
        desktopPowerSwitches[key]=toggle;
        var hint=Ui.Text("读取中",11,muted:true);hint.MinHeight=14;desktopPowerHints[key]=hint;
        var row=DetailRow(glyph,title,description,toggle,0,hint);row.MinHeight=56;
        if(!inset)return row;
        var card=DetailInset(row);card.Padding=new Thickness(8);return card;
    }
    private async Task ReadDesktopServices(bool force=false)
    {
        if(closed||upstream==null||desktopServicesReading||desktopServiceBusy||(!force&&DateTimeOffset.Now-lastDesktopServicesRead<TimeSpan.FromSeconds(8)))return;
        desktopServicesReading=true;lastDesktopServicesRead=DateTimeOffset.Now;
        try
        {
            var state=await Task.Run(()=>(policies:DesktopPower.ReadPolicies(),power:upstream.Read(13),lighting:upstream.Read(15),color:upstream.Read(16),icc:upstream.Read(14)));
            powerPolicies=state.policies;servicePower=state.power;serviceLighting=state.lighting;serviceColor=state.color;serviceIcc=state.icc;desktopServiceError="";
        }
        catch(Exception error){desktopServiceError=error.Message;actions.Add("服务读取："+error.Message);}
        finally{desktopServicesReading=false;if(!closed){SyncDesktopServices();SyncColorServices();SyncZoneServices();}}
    }
    private bool? PowerServiceValue(string key)=>key switch
    {
        "office"=>desktopPreferences.SleepOffice,"link"=>desktopPreferences.PerformanceLink,
        "master"=>powerPolicies.Any(p=>p.Supported)?powerPolicies.Where(p=>p.Supported).All(p=>p.Enabled==true):null,
        "usb"=>JsonByte(servicePower,"trigger") is byte trigger?(trigger&Vault.UsbChargeMask)!=0:null,
        "recovery"=>JsonByte(servicePower,"recovery") is byte recovery?(recovery&Vault.AcRecoveryMask)!=0:null,
        _=>powerPolicies.SingleOrDefault(p=>p.Definition.Key==key)?.Enabled
    };
    private static byte? JsonByte(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.Number&&item.TryGetByte(out byte number)?number:null;
    private static bool JsonBool(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.True;
    private string? PowerBusyOperation(string key)
    {
        bool policy=key=="master"||DesktopPower.Policies.Any(p=>p.Key==key);
        return desktopBusyServices.FirstOrDefault(name=>name=="power-"+key||key=="link"&&name=="power-overlay"||policy&&(name=="power-master"||key=="master"&&DesktopPower.Policies.Any(p=>name=="power-"+p.Key)));
    }
    private void SyncDesktopServices()
    {
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
    private async Task DesktopServiceAction(string name,Action operation,bool takeover=false)
    {
        if(closed||upstream==null||!desktopBusyServices.Add(name))return;
        SyncDesktopServiceGroup(name);
        await operationQueue.WaitAsync();
        string? failure=null;
        using var trace=DiagnosticLog.Begin("desktop.service",new{name,takeover});
        try
        {
            while(desktopServicesReading&&!closed)await Task.Delay(30);
            if(closed)return;
            desktopRunningService=name;SyncDesktopServiceGroup(name);
            try
            {
                if(uiReviewMode&&desktopServiceReviewExecutor!=null)await desktopServiceReviewExecutor(name);
                else await Task.Run(()=>hardware.ApplyDesktopService(name,operation,takeover));
            }
            catch(Exception error){failure=error.Message;trace.Fail(error);actions.Add(name+"："+error);}
            await RefreshDesktopServiceGroup(name);
        }
        catch(Exception error){failure=error.Message;trace.Fail(error);actions.Add(name+"："+error);}
        finally{desktopBusyServices.Remove(name);desktopRunningService=null;operationQueue.Release();if(!closed)SyncDesktopServiceGroup(name);}
        if(failure==null)trace.Complete();
        if(failure!=null&&!closed)await ShowMessage("设置未完成",failure);
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
        if(name is "power-usb" or "power-recovery")servicePower=await Task.Run(()=>upstream.Read(13));
        else if(name=="power-master"||DesktopPower.Policies.Any(p=>name=="power-"+p.Key))powerPolicies=await Task.Run(DesktopPower.ReadPolicies);
        else if(name.StartsWith("lighting-"))serviceLighting=await Task.Run(()=>upstream.Read(15));
        else if(!name.StartsWith("power-"))
        {
            if(!name.StartsWith("icc-"))serviceColor=await Task.Run(()=>upstream.Read(16));
            serviceIcc=await Task.Run(()=>upstream.Read(14));
        }
    }
    private async Task ChangePowerService(string key)
    {
        if(upstream==null)return;
        bool target=desktopPowerSwitches[key].IsOn;
        await DesktopServiceAction("power-"+key,()=>
        {
            switch(key)
            {
                case "office":desktopPreferences.SleepOffice=target;try{desktopPreferences.Save();}catch{desktopPreferences.SleepOffice=!target;throw;}break;
                case "link":
                    if(target){int mode=hardware.CurrentMode??throw new IOException("性能档位未读取。");DesktopPower.SetOverlay(upstream,mode);linkedPerformanceMode=mode;}
                    desktopPreferences.PerformanceLink=target;try{desktopPreferences.Save();}catch{desktopPreferences.PerformanceLink=!target;throw;}break;
                case "usb":case "recovery":hardware.SetDesktopPowerOption(upstream,key,target);break;
                case "master":
                    foreach(var p in DesktopPower.ReadPolicies().Where(p=>p.Supported))DesktopPower.SetPolicy(upstream,p.Definition.Key,target);break;
                default:DesktopPower.SetPolicy(upstream,key,target);break;
            }
        },key is "usb" or "recovery" or "office");
    }
    private async Task SyncPerformanceOverlay()
    {
        int mode=hardware.CurrentMode??0;
        if(!desktopPreferences.PerformanceLink||mode==0||mode==linkedPerformanceMode||desktopServiceBusy||hardwareBusy||quickBusy||tuningApplying||sleepOfficeBusy||upstream==null||!hardware.Active)return;
        linkedPerformanceMode=mode;
        await DesktopServiceAction("power-overlay",()=>DesktopPower.SetOverlay(upstream,mode));
    }
}
