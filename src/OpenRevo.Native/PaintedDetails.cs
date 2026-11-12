using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool paintRgbExpanded;
    private PaintSelect? paintPlanChoice;
    private PowerPlan[] paintPlans=[];
    private bool? paintStartup;
    private FrameworkElement PaintSection(string title,string icon,UIElement content)
    {FrameworkElement heading=IsSecondaryPaintRoute(PaintRoute)?Paint.Text(title,16,true):Paint.Inline(12,Paint.Icon(icon),Paint.Text(title,16,true));return Paint.Card(Paint.Stack(14,heading,content),20);}
    private FrameworkElement PaintSetting(string title,string description,FrameworkElement control)
    {var copy=Paint.Stack(4,Paint.Text(title));if(description.Length>0)copy.Children.Add(Paint.Text(description,12,muted:true));return Paint.Rule(Paint.Between(copy,control,16));}
    private void RenderPaintedDetails(string route)
    {
#if NATIVE_PLUGIN_FOLDER_ENTRY
        if(route=="plugins"){RenderNativePluginFolders();return;}
#endif
        if(route=="minicustomization"){RenderMiniCustomization();return;}
        if(route=="devices"){RenderPaintedLighting();return;}
        if(route=="tuning"&&PaintDeclaredLimits is {} limits){page.Children.Add(PaintDeclaredCpuTuning(limits));return;}
        if(route=="tuning"&&residentTuning?.Unverified is { } factory)
        {page.Children.Add(PaintUnverifiedTuning(factory.Office,factory.Balance,factory.Turbo,factory.Presets));return;}
        if(route=="tuning"&&rejectedTuningRegisters is {} registers){page.Children.Add(PaintUnverifiedTuning(registers));return;}
        if(route=="color"&&colorDraft==null&&serviceColor!=null)colorDraft=serviceColor!.ActivePreset.Copy();
        if(route=="updates"){RenderPaintedUpdates();return;}
        if(!PaintPageSchema.Pages.TryGetValue(route,out var specs))return;
        var grid=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=960,ColumnSpacing=16,RowSpacing=16};
        foreach(var spec in specs)
        {
            if(route=="diagnostics"&&spec.Title!="诊断报告"&&(!PaintDebugSettingsVisible||debugSettingsHidden))continue;
            var rows=Paint.Stack(0);
            if(route=="display"&&spec.Title=="显卡模式"){rows.Children.Add(PaintGpuDetails());}
            else foreach(var row in spec.Rows)
            {
                if(spec.Collapsible&&!paintRgbExpanded)continue;
                if(row.Id.StartsWith("tuning-1-")&&PaintClassicTuningReady&&!PaintClassicCpuTuning)continue;
                if(row.Id.StartsWith("tuning-2-")&&PaintClassicTuningReady&&PaintIntegratedOnly)continue;
                if(row.Id=="tuning-2-1"&&PaintTuningGpu is {Supported:true,ConfigurableBase:false})continue;
                if(row.Id=="settings-1-4"&&residentClient==null)continue;
                rows.Children.Add(PaintDetailRow(route,row));
            }
            if(route=="tuning"&&rows.Children.Count==0)continue;
            if(spec.Collapsible){var expand=new PaintButton(paintRgbExpanded?"收起微调":"展开微调",()=>{paintRgbExpanded=!paintRgbExpanded;Render();}){Variant="plain",Padding=new(0),MinHeight=32};rows.Children.Insert(0,expand);}
            var card=PaintSection(spec.Title,spec.Icon,rows);if(spec.Span==2)DesktopTiles.SetFullRow(card);grid.Children.Add(card);
        }
        page.Children.Add(grid);
        if(route=="tuning")
        {
            var apply=PB("应用极客调校",PaintApplyTuning,true);
            paintBindings.Add(()=>apply.IsEnabled=PaintTuningCanApply);
            page.Children.Add(Paint.Card(Paint.Between(PT(()=>tuningStatus,12,muted:true),apply),16,12));
        }
        if(route is "display" or "power" or "sleep" or "lightingzone" or "color")
        {
            if(residentClient==null)_=ReadDesktopServices();
            else if(route is "power" or "sleep")_=ReadResidentPowerAsync();
            else if(route is "display" or "color")_=ReadResidentColorAsync();
        }
        if(route=="settings")
        {
            if(residentClient==null){_=PaintReadPlans();_=PaintReadStartup();}
            else{_=ReadResidentPowerAsync();_=ReadResidentStartupAsync();}
        }
    }
    private FrameworkElement PaintDetailRow(string route,PaintRowSpec row)
    {
        if(row.Kind=="repository")
            return Paint.Rule(CommunityRepositoryInfo(true));
        if(row.Kind=="battery-health")return PaintBatteryHealth();
        if(row.Kind=="curves")return PaintFanCurves();
        if(row.Kind=="osd")return PaintOsdPreview();
        if(row.Kind=="range")return PaintRange(row);
        if(row.Kind=="brightness")return PaintSetting(row.Label,row.Description,Paint.Inline(6,new[]{25,60,100}.Select(v=>(UIElement)PB(v+"%",()=>SetCompactBrightness(v),true)).ToArray()));
        if(row.Kind=="update")
        {
            var check=PB("检查更新",async()=>{if(!uiReviewMode)await CheckNativeUpdate();});
            var details=new PaintButton("更新详情",()=>PaintNavigate("updates"));
            paintBindings.Add(()=>check.IsEnabled=!PaintUpdateBusy&&!exitRequested&&
                (residentClient==null||residentSnapshot?.Ready==true&&!ResidentUpdateRequestBusy));
            return Paint.Stack(6,PaintSetting(row.Label,row.Description,Paint.Inline(8,check,details)),
                PT(()=>PaintUpdateMessage,12,muted:true));
        }
        if(row.Kind=="tools")return Paint.Rule(PaintTools(route));
        if(row.Kind=="toggle")
        {var toggle=PS(row.Label,()=>PaintReadToggle(row.Id),value=>PaintWriteToggle(row.Id,value),()=>PaintToggleEnabled(row.Id));return PaintSetting(row.Label,row.Description,toggle);}
        if(row.Kind is "select" or "refresh")return PaintSetting(row.Label,row.Description,PaintChoice(row));
        if(row.Kind=="status")return PaintSetting(row.Label,row.Description,PT(()=>PaintStatus(row.Id),12,muted:true));
        if(row.Kind=="color")return PaintSetting(row.Label,row.Description,PaintColorEntry(false));
        string label=row.Kind=="link"?"查看":row.Label switch{"选择位置并导出"=>"导出","打开默认目录" or "打开目录" or "查看日志"=>"打开目录","退出程序"=>"退出","预览提示"=>"预览","关闭屏幕"=>"息屏",_=>row.Label};
        var button=PB(label,()=>PaintPageAction(row.Id),row.Id is "power-2-3" or "settings-2-2");if(row.Kind=="link"){button.Variant="plain";button.Content=Paint.Inline(6,Paint.Text("查看",12),Paint.Utility("chevron",14));}
        if(row.Id=="tuning-0-2")paintBindings.Add(()=>button.IsEnabled=!tuningLoading&&!tuningApplying);
        if(residentClient!=null&&row.Id=="display-2-3")paintBindings.Add(()=>button.IsEnabled=residentSnapshot?.Ready==true&&!PanelActionBusy(PanelOperation.Desktop,typeof(PanelApplyIcc)));
        return PaintSetting(row.Label,row.Description,button);
    }
    private static string? PaintPowerPolicyKey(string id)=>id switch
    {
        "power-1-0"=>"usb","power-1-1"=>"recovery","power-1-2"=>"link","power-1-3"=>"office",
        "sleep-0-0"=>"master","sleep-1-0"=>"disconnect_net","sleep-1-1"=>"block_away","sleep-1-2"=>"ignore_sysreq",
        "sleep-2-0"=>"disable_wake_timers","sleep-2-1"=>"deep_sleep","sleep-2-2"=>"hibernate_30m",_=>null
    };
    private PanelPowerPolicy? ResidentPowerPolicy(string key) => residentPower?.Policies.FirstOrDefault(policy => policy.Key == key);
    private bool ResidentSettingBusy(string id) => id switch
    {
        "settings-0-0" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.Theme),
        "settings-0-1" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.WakeMode),
        "settings-0-2" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.MiniAutoHide),
        "settings-1-0" => PanelActionBusy(PanelOperation.Application, PanelApplicationAction.SetStartup),
        "settings-1-1" => PanelActionBusy(PanelOperation.Takeover),
        "settings-1-2" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.SampleSeconds),
        "settings-1-3" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.BatteryOverride),
        "settings-1-4" => PanelActionBusy(PanelOperation.Preferences, PanelPreferenceKind.TrayTelemetry),
        "osdsettings-0-0" => PanelActionBusy(PanelOperation.SetOsdEnabled),
        "settings-0-4" or "osdsettings-0-1" or "osdsettings-0-2" or "osdsettings-0-3" => PanelActionBusy(PanelOperation.SetOsdAppearance),
        _ => ResidentSessionChanging
    };
    private bool? PaintReadToggle(string id)=>residentClient!=null&&PaintPowerPolicyKey(id) is { } key
        ? ResidentPowerPolicy(key)?.Enabled : id switch
    {
        "display-1-3"=>lowRefreshOnBattery,"display-2-0" or "color-0-2"=>PaintColorEnabled,
        "power-1-0"=>PowerServiceValue("usb"),"power-1-1"=>PowerServiceValue("recovery"),"power-1-2"=>PowerServiceValue("link"),"power-1-3"=>PowerServiceValue("office"),"power-2-2"=>keepAwakeEnabled,
        "sleep-0-0"=>PowerServiceValue("master"),"sleep-1-0"=>PowerServiceValue("disconnect_net"),"sleep-1-1"=>PowerServiceValue("block_away"),"sleep-1-2"=>PowerServiceValue("ignore_sysreq"),"sleep-2-0"=>PowerServiceValue("disable_wake_timers"),"sleep-2-1"=>PowerServiceValue("deep_sleep"),"sleep-2-2"=>PowerServiceValue("hibernate_30m"),
        "settings-0-2"=>systemPreferences.MiniAutoHide,"settings-1-0"=>paintStartup,"settings-1-1"=>PanelHardwareActive,"settings-1-3"=>systemPreferences.UnlockBatteryLimit,"osdsettings-0-0"=>osdEnabled,
        "settings-1-4"=>residentSnapshot?.Preferences.TrayTelemetry,
        "plugins-0-0"=>summaryExtension?.Enabled??true,"lightingzone-0-1"=>PaintZoneBool(),
        "tuning-1-3"=>tuningDraft?.TemperatureEnabled,"tuning-2-0"=>tuningDraft?.GpuSuperPerformance,"tuning-2-2"=>tuningDraft?.GpuBoostEnabled,"tuning-3-0"=>tuningDraft?.CustomFans,"tuning-3-2"=>tuningDraft?.SmoothFans,_=>null
    };
    private bool PaintToggleEnabled(string id)
    {
        if(residentClient!=null&&PaintPowerPolicyKey(id) is { } policy)
            return ResidentPowerPolicy(policy)?.Supported==true&&!PanelPowerPolicyBusy(policy);
        if(residentClient!=null&&(id.StartsWith("settings-")||id.StartsWith("osdsettings-")))
            return residentSnapshot?.Ready==true&&!ResidentSettingBusy(id)&&(id!="settings-1-0"||residentStartupAvailable);
        if (residentClient != null && id == "display-1-3")
            return !PanelActionBusy(PanelOperation.Desktop, typeof(PanelSetLowRefresh));
        if (residentClient != null && id == "power-2-2") return !ResidentQuickBusy("awake");
        if(id.StartsWith("tuning"))
        {
            if(!PaintClassicTuningReady||tuningDraft==null||tuningLoading||tuningApplying||
                residentClient!=null&&PanelTuningBusy)return false;
            bool gpuSupported=PaintTuningGpu?.Supported==true;
            return id is not ("tuning-2-0" or "tuning-2-2")||gpuSupported;
        }
        if(id.StartsWith("sleep")||id.StartsWith("power-1"))return upstream!=null&&!desktopServiceBusy&&(id is not("power-1-0" or "power-1-1" or "power-1-3")||hardware.Active);
        if(id is "display-2-0" or "color-0-2")return PaintColorAvailable;
        if(id=="lightingzone-0-1")return PaintZoneEnabled;
        return true;
    }
    private async Task PaintWriteToggle(string id,bool value)
    {
        if(id.StartsWith("tuning"))
        {
            if(tuningDraft is not{} p||!PaintToggleEnabled(id))return;
            tuningDraft=id switch
            {
                "tuning-1-3"=>p with{TemperatureEnabled=value},
                "tuning-2-0"=>p with{GpuSuperPerformance=value},
                "tuning-2-2"=>p with{GpuBoostEnabled=value},
                "tuning-3-0"=>p with{CustomFans=value},
                "tuning-3-2"=>p with{SmoothFans=value},
                _=>p
            };
            PaintDraftChanged();return;
        }
        if(uiReviewMode&&id is not("settings-0-2" or "settings-1-3" or "osdsettings-0-0"))return;
        if(residentClient!=null)
        {
            switch(id)
            {
                case "settings-0-2":
                    await SetResidentPreferenceAsync(new(PanelPreferenceKind.MiniAutoHide,Enabled:value));return;
                case "settings-1-0":await SetResidentStartupAsync(value);return;
                case "settings-1-1":
                    await RunResidentActionAsync(value?PanelOperation.Takeover:PanelOperation.Release);
                    return;
                case "settings-1-4":
                    await SetResidentPreferenceAsync(new(PanelPreferenceKind.TrayTelemetry,Enabled:value));return;
                case "settings-1-3":
                    if(value&&!await Confirm("解除离电限制？",
                        "允许在电池或 Type-C 供电下使用狂暴与自定义模式，并停止拔电时自动降档。可能增加发热和耗电，供电不足时可能关机或丢失未保存内容。"))return;
                    await SetResidentPreferenceAsync(new(PanelPreferenceKind.BatteryOverride,Enabled:value,Confirmed:value));
                    return;
                case "osdsettings-0-0":
                    await RunResidentActionAsync(PanelOperation.SetOsdEnabled,value?1:0);return;
            }
        }
        string? policy=PaintPowerPolicyKey(id);
        if(policy!=null)
        {
            if(residentClient!=null)await RunResidentActionAsync(PanelOperation.Desktop,desktop:new PanelSetPowerPolicy(policy,value));
            else await ChangePowerService(policy,value);
            return;
        }
        switch(id)
        {
            case "display-1-3":
                if(residentClient!=null)
                    await RunResidentActionAsync(PanelOperation.Desktop,desktop:new PanelSetLowRefresh(value));
                else
                {
                    lowRefreshOnBattery=value;await SavePreferences();await CheckAutomaticRefresh(true);
                    ConfirmedActionOsd("拔电自动低刷 · "+(value?"已开启":"已关闭"),"refresh");
                }
                break;
            case "display-2-0":case "color-0-2":await SubmitColor(enabledOverride:value);break;
            case "power-2-2":await RunQuickAction("awake");break;
            case "settings-0-2":await SaveSystemSettings(systemPreferences with{MiniAutoHide=value});break;
            case "settings-1-0":await Task.Run(()=>StartupRegistration.SetEnabled(value));await PaintReadStartup();break;
            case "settings-1-1":await PaintTakeover(value);break;
            case "settings-1-3":if(value&&!await Confirm("解除离电限制？","允许电池或Type-C使用狂暴及自定义模式。供电保护和参数边界仍保留。"))return;await SaveSystemSettings(systemPreferences with{UnlockBatteryLimit=value});if(!value)await EnforceBatteryPerformanceGuard();break;
            case "osdsettings-0-0":osdEnabled=value;await SavePreferences();if(!value)nativeOsd?.Hide();else ConfirmedActionOsd("屏幕提示 · 已开启","osd");break;
            case "plugins-0-0":EnsureExtensionHost();using(var doc=JsonDocument.Parse(value?"{\"enabled\":true}":"{\"enabled\":false}")){summaryExtension!.SaveConfig(doc.RootElement);summaryExtension.SetEnabled(value);}break;
            case "lightingzone-0-1":await PaintSetZone("enabled",JsonValue.Create(value)!);break;
        }
    }
    private PaintSelect PaintChoice(PaintRowSpec row)
    {
        if (row.Id == "display-1-2") return PaintRefreshChoice(row.Label);
        if(residentClient!=null)
        {
            if(row.Id=="display-2-2")return ResidentIccChoice(row.Label);
            if(row.Id=="color-0-0")return ResidentColorChoice(row.Label);
            if(row.Id=="lightingzone-0-2")return ResidentZoneEffectChoice(row.Label);
            if(row.Id=="settings-2-0")return ResidentPowerPlanChoice(row.Label);
        }
        string[] items=[];int index=-1;Func<int,Task> changed=_=>Task.CompletedTask;
        Func<int>? readSelection=null;
        switch(row.Id)
        {
            case "display-2-2":var profiles=serviceIcc.ValueKind==JsonValueKind.Object?serviceIcc.GetProperty("profiles").EnumerateArray().Select(p=>(Id:p.GetProperty("id").GetString()!,Name:p.GetProperty("name").GetString()!)).ToArray():[];items=profiles.Select(p=>p.Name).ToArray();index=serviceIcc.ValueKind==JsonValueKind.Object?Array.FindIndex(profiles,p=>p.Id==serviceIcc.GetProperty("active_profile_id").GetString()):-1;changed=i=>ApplyIcc(profiles[i].Id);break;
            case "settings-0-0":items=["跟随 Windows","浅色","深色"];index=selectedTheme;readSelection=()=>selectedTheme;changed=ChangeApplicationTheme;break;
            case "settings-0-4":
                string[] speeds=["fast","normal","slow","off"];
                items=["快","标准","慢","关闭动效"];
                index=Array.IndexOf(speeds,systemPreferences.MotionSpeed);
                readSelection=()=>Array.IndexOf(speeds,systemPreferences.MotionSpeed);
                changed=i=>residentClient==null?SaveSystemSettings(systemPreferences with{MotionSpeed=speeds[i]}):
                    SetResidentOsdAppearanceAsync(value=>value with{Motion=speeds[i]});
                break;
            case "settings-0-1":
                string[] modes=["remember","mini","full"];
                items=["记忆上次形态","始终迷你面板","始终完整面板"];index=Array.IndexOf(modes,systemPreferences.WakeMode);
                readSelection=()=>Array.IndexOf(modes,systemPreferences.WakeMode);
                changed=i=>residentClient==null?SaveSystemSettings(systemPreferences with{WakeMode=modes[i]}):
                    SetResidentPreferenceAsync(new(PanelPreferenceKind.WakeMode,Text:modes[i]));break;
            case "settings-1-2":
                int[] intervals=[2,5,10];
                items=["2 秒","5 秒","10 秒"];index=Array.IndexOf(intervals,interval);
                readSelection=()=>Array.IndexOf(intervals,interval);
                changed=async i=>
                {
                    if(residentClient!=null){await SetResidentPreferenceAsync(new(PanelPreferenceKind.SampleSeconds,Number:intervals[i]));return;}
                    interval=intervals[i];timer.Interval=TimeSpan.FromSeconds(interval);await SavePreferences();
                };break;
            case "settings-2-0":items=paintPlans.Select(p=>p.Name).ToArray();changed=i=>PaintSwitchPlan(paintPlans[i]);break;
            case "osdsettings-0-1":
                string[] styles=["cinema","fade","pill"];
                items=["电影字幕","向右羽化","磨砂胶囊"];index=Array.IndexOf(styles,systemPreferences.OsdTheme);
                readSelection=()=>Array.IndexOf(styles,systemPreferences.OsdTheme);
                changed=async i=>
                {
                    if(residentClient==null)await SaveSystemSettings(systemPreferences with{OsdTheme=styles[i]});
                    else await SetResidentOsdAppearanceAsync(value=>value with{Theme=styles[i]});
                };break;
            case "osdsettings-0-2":
                int[] sizes=[80,100,120,140];
                items=["80%","100%","120%","140%"];index=Array.IndexOf(sizes,systemPreferences.OsdScale);
                readSelection=()=>Array.IndexOf(sizes,systemPreferences.OsdScale);
                changed=async i=>
                {
                    if(residentClient==null)await SaveSystemSettings(systemPreferences with{OsdScale=sizes[i]});
                    else await SetResidentOsdAppearanceAsync(value=>value with{Scale=sizes[i]});
                };break;
            case "osdsettings-0-3":
                string[] places=["top-left","bottom-center","center"];
                items=["左上角","下方中央","屏幕正中"];index=Array.IndexOf(places,systemPreferences.OsdPosition);
                readSelection=()=>Array.IndexOf(places,systemPreferences.OsdPosition);
                changed=async i=>
                {
                    if(residentClient==null)await SaveSystemSettings(systemPreferences with{OsdPosition=places[i]});
                    else await SetResidentOsdAppearanceAsync(value=>value with{Position=places[i]});
                };break;
            case "color-0-0":var presets=serviceColor?.Presets??[];items=presets.Select(p=>p.Name!).ToArray();index=presets.ToList().FindIndex(p=>p.Id==colorDraft?.Id);changed=async i=>{colorDraft=presets[i].Copy();await SubmitColor(enabledOverride:true);Render();};break;
            case "tuning-0-0":
                var choices=tuningPresets.ToArray();
                items=choices.Select(p=>p.Name).ToArray();index=Array.FindIndex(choices,p=>p.Id==tuningPresetId);
                changed=async i=>
                {
                    if(i<0||i>=choices.Length)return;
                    if(residentClient!=null){await SelectResidentTuningPresetAsync(choices[i].Id);return;}
                    RememberPresetDraft();
                    var p=tuningPresets[i];tuningPresetId=p.Id;
                    if(!uiReviewMode)AppData.Update(a=>a with{SelectedTuningPreset=p.Id});
                    LoadPaintedPresetDraft(p);Render();
                };
                break;
            case "lightingzone-0-0":items=["前置灯条","转轴氛围灯","Logo 灯光"];index=Array.IndexOf(new[]{"front","hinge","logo"},selectedLightingZone);changed=i=>{selectedLightingZone=new[]{"front","hinge","logo"}[i];Render();return Task.CompletedTask;};break;
            case "lightingzone-0-2":var effects=PaintZoneEffects();items=effects.Select(e=>e.Name).ToArray();index=Array.FindIndex(effects,e=>e.Id==PaintZoneNumber("effect"));changed=i=>PaintSetZone("effect",JsonValue.Create(effects[i].Id)!);break;
            case "diagnostics-1-0":items=["关闭","仅错误","所有日志"];index=PaintLogLevel;changed=i=>SavePaintLoggingAsync(i,PaintLogFilter);break;
            case "diagnostics-1-1":items=["全部模块","外置水冷","自定义追踪","RGB 与推流","电源与风扇","底层 EC","设备外设","窗口与系统"];var filters=new[]{"","cooler","custom","light","power","ec","device","ui"};index=Array.IndexOf(filters,PaintLogFilter);changed=i=>SavePaintLoggingAsync(PaintLogLevel,filters[i]);break;
            case "diagnostics-2-0":items=["真实本机","星耀14轻薄本","游戏本形态"];index=Array.IndexOf(new[]{"real","thin_light","gaming"},developerShape);changed=i=>{developerShape=new[]{"real","thin_light","gaming"}[i];RefreshPaintedState();return Task.CompletedTask;};break;
        }
        var choice=new PaintSelect(row.Label,items,index,async i=>{try{if(!uiReviewMode||row.Id.StartsWith("osdsettings")||row.Id.StartsWith("settings-0")||row.Id=="settings-1-2"||row.Id.StartsWith("tuning"))await changed(i);}catch(Exception error){await ShowMessage("设置未完成",error.Message);}RefreshPaintedState();}){Width=180};
        if(row.Id=="tuning-0-0")
        {
            if(tuningDraft!=null)choice.UnselectedText="当前硬件参数";
            paintBindings.Add(()=>choice.IsEnabled=items.Length>0&&!tuningLoading&&!tuningApplying&&
                (residentClient==null||PaintClassicTuningReady&&!PanelTuningBusy));
        }
        if(row.Id=="settings-2-0")paintPlanChoice=choice;
        if(residentClient!=null&&readSelection!=null)
            paintBindings.Add(()=>
            {
                bool busy = ResidentSettingBusy(row.Id);
                choice.IsEnabled=residentSnapshot?.Ready==true&&!busy;
                if (!busy) choice.SelectedIndex=readSelection();
            });
        if(row.Id.StartsWith("display"))paintBindings.Add(()=>choice.IsEnabled=serviceIcc.ValueKind==JsonValueKind.Object&&!colorServicesBusy);
        return choice;
    }
    private string PaintStatus(string id)=>id switch
    {
        "plugins-0-1"=>latest==null?"等待下一次采样…":$"CPU {latest.CpuPercent:0.0}% · 电池 {latest.BatteryPercent}%","plugins-1-0" or "plugins-1-1"=>"已提供","plugins-1-2"=>"未开放",
        "settings-4-0"=>"GPL-3.0-or-later","storage-0-0"=>residentClient==null?AppData.DirectoryPath:residentMaintenance?.DataDirectory??"待读取",
        "storage-1-1"=>"仅本版本","diagnostics-0-0"=>"脱敏导出","diagnostics-2-1"=>developerShape=="real"?identity.Product:"开发者布局预览，不修改本机能力",
        "color-4-0"=>colorPending?"更新中":residentColorEdit!=null?"草稿已保留，后台尚未确认。":PaintColorEnabled==null?"状态待读取":
            PaintColorEnabled==true?"全局滤镜已开启"+((residentClient!=null?residentColor?.GammaApplied:serviceColor?.GammaApplied)==true?" · Gamma 已确认":" · Gamma 未确认"):"已关闭",
        "lightingzone-2-0" when residentClient!=null=>residentLighting==null?"待设备识别":ResidentSelectedZone?.CanSet==true?"可控制":"当前不可控制",
        "lightingzone-2-1" when residentClient!=null=>ResidentSelectedZone!=null?"显示后台保存的指令值 · 此灯区无完整回读":"能力未知",
        "lightingzone-2-0"=>JsonBool(serviceLighting,"capability_known")?(JsonBool(serviceLighting,selectedLightingZone)?"已识别":"不支持"):"待设备识别","lightingzone-2-1"=>JsonBool(serviceLighting,"capability_known")?"指令由 Core 确认；此灯区无完整回读":"能力未知",_=>"状态待读取"
    };
    private async Task PaintPageAction(string id)
    {
        if(residentClient!=null)
        {
            switch(id)
            {
                case "settings-2-1":await ReadResidentPowerAsync(force:true);return;
                case "settings-2-2":
                    if(!PanelHardwareActive||!await Confirm("修复 Windows 电源计划？",
                        "先备份全部方案，再重建内置计划并尝试恢复 OEM 调度。自建方案会检查保留，定制的内置参数和睡眠策略可能重置。"))return;
                    var recovery=await SendResidentAsync(PanelOperation.Desktop,desktop:new PanelRecoverPowerPlans(true));
                    await ShowMessage("电源计划恢复结果",recovery.Message??"后台未确认恢复。");return;
                case "settings-3-2":
                    RequireResidentResult(await SendResidentApplicationAsync(PanelApplicationAction.ShowTrayMenu),PanelOutcome.CommandSent);return;
                case "settings-3-3":await ExitResidentFromPanelAsync();return;
                case "storage-0-1":await OpenResidentMaintenanceFolderAsync(false);return;
                case "storage-0-2":await RunResidentStorageChangeAsync(false);return;
                case "storage-1-0":await RunResidentStorageChangeAsync(true);return;
                case "diagnostics-0-1":case "diagnostics-1-2":await OpenResidentMaintenanceFolderAsync(true);return;
                case "diagnostics-0-2":await ExportResidentDiagnosticAsync();return;
            }
        }
        switch(id)
        {
            case "display-2-1":PaintNavigate("color");break;case "display-2-3":if(!uiReviewMode)await ImportIcc();break;
            case "power-2-0":PaintNavigate("sleep");break;case "power-2-1":PaintNavigate("settings");break;case "power-2-3":await PaintQuickAction("screen");break;
            case "plugins-0-2":PaintNavigate("overview");break;
            case "settings-0-5":PaintNavigate("minicustomization");break;
            case "settings-0-3":PaintNavigate("osdsettings");break;case "settings-3-0":PaintNavigate("storage");break;case "settings-3-1":PaintNavigate("diagnostics");break;
            case "settings-2-1":await PaintReadPlans();break;case "settings-2-2":await PaintRecoverPlans();break;
            case "settings-3-2":TrayMenu();break;case "settings-3-3":if(!uiReviewMode)await ExitFromTray();break;
            case "storage-0-1":if(!uiReviewMode)OpenLocalFolder(AppData.DirectoryPath);break;case "storage-0-2":await MigrateDataStorage();break;case "storage-1-0":await ClearDataStorage();break;
            case "diagnostics-0-1":case "diagnostics-1-2":if(!uiReviewMode)OpenLocalFolder(AppData.DiagnosticsPath);break;case "diagnostics-0-2":if(!uiReviewMode)await Export();break;
            case "diagnostics-0-3":await SubmitBugReport();break;
            case "diagnostics-1-3":await SavePaintLoggingAsync(0,PaintLogFilter);debugSettingsHidden=true;Render();break;
            case "osdsettings-1-1":PaintOsdReplay();break;
            case "tuning-0-2":await PaintReadTuning();break;
        }
    }
    private FrameworkElement PaintRange(PaintRowSpec row)
    {
        var binding=PaintRangeBinding(row.Id);
        var field=new PaintField(row.Label,binding.Value?.ToString("0.##")??"—");
        if (residentClient != null)
        {
            field.CanCommit = () => !residentDetaching && !closed;
            field.CaptureDraft = text => CaptureResidentRangeDraft(row.Id, text);
        }
        field.Committed+=async text=>
        {
            var state=PaintRangeBinding(row.Id);
            if(!TryPaintRangeValue(text, state.Min, state.Max, out double next))
            {await ShowMessage("数值无效","请填写实际范围内的完整数值。");return;}
            try{await PaintSetNumber(row.Id,next);RefreshPaintedState();}
            catch(Exception error){DiagnosticLog.Failure("paint.range.failed",error,throttle:true);if(!closed)await ShowMessage("调整未完成",error.Message);}
        };
        var slider=new PaintSlider(row.Label,binding.Min??0,binding.Max??1,binding.Value??0,binding.Step,async next=>
        {
            try{await PaintSetNumber(row.Id,next,coalesceBrightness:true);}
            catch(Exception error){DiagnosticLog.Failure("paint.range.failed",error,throttle:true);if(!closed)await ShowMessage("调整未完成",error.Message);}
        });
        var minLabel=Paint.Text("—",12,muted:true);var maxLabel=Paint.Text("—",12,muted:true);
        PaintButton? adjustMax=null;
        if(PaintCpuLimit(row.Id) is {} cpuLimit)
        {
            adjustMax=PB($"调整 {cpuLimit} 上限",()=>PaintAdjustCpuCeiling(cpuLimit));
            adjustMax.Variant="plain";adjustMax.Padding=new(4,0,4,0);adjustMax.MinHeight=20;
            adjustMax.Content=Paint.Inline(5,maxLabel,Paint.Text("调整上限",12,muted:true));
        }
        void RefreshRange()
        {
            var state=PaintRangeBinding(row.Id);
            bool known=state.Value.HasValue&&state.Min.HasValue&&state.Max.HasValue;
            field.IsEnabled=slider.IsEnabled=known&&state.Enabled;
            if(adjustMax!=null)adjustMax.IsEnabled=known&&state.Enabled&&!uiReviewMode;
            minLabel.Text=known?state.Min+" "+state.Unit:"—";
            maxLabel.Text=known?state.Max+" "+state.Unit:"—";
            if(!known)return;
            slider.SetReadback(state.Min!.Value, state.Max!.Value, state.Value!.Value);
            field.Value=state.Value.Value.ToString("0.##");
        }
        RefreshRange();paintBindings.Add(RefreshRange);
        var labels=Paint.Stack(4,Paint.Text(row.Label));if(row.Description.Length>0)labels.Children.Add(Paint.Text(row.Description,12,muted:true));
        return Paint.Rule(Paint.Stack(8,Paint.Between(labels,Paint.Inline(5,field,Paint.Text(binding.Unit,12,muted:true))),slider,Paint.Between(minLabel,(FrameworkElement?)adjustMax??maxLabel)));
    }
    private static CpuPowerLimit? PaintCpuLimit(string id)=>id switch
    {
        "tuning-1-0"=>CpuPowerLimit.Pl1,"tuning-1-1"=>CpuPowerLimit.Pl2,"tuning-1-2"=>CpuPowerLimit.Pl4,_=>null
    };
    private (double? Value,double? Min,double? Max,double Step,string Unit,bool Enabled) PaintRangeBinding(string id)
    {
        var p=tuningDraft;var caps=PaintTuningGpu;
        var cpuMax=PaintTuningCpuMax;
        bool canEdit=PaintClassicTuningReady&&p!=null&&!tuningLoading&&!tuningApplying&&
            (residentClient==null||!PanelTuningBusy);
        bool cpuEditable=PaintClassicCpuTuning&&canEdit;
        if(id=="display-1-0")return(DisplayedScreenBrightness,10,100,1,"%",displayState?.Active==true&&!displayBusy);
        if(id.StartsWith("color")){var key=PaintColorParameter(id);var range=ColorPreset.Range(key);return(colorDraft?.Get(key),range.Minimum,range.Maximum,id=="color-1-0"?50:.02,id=="color-1-0"?"K":"",residentClient!=null?PaintColorAvailable:serviceColor!=null&&!colorServicesBusy);}
        if(id.StartsWith("lightingzone")){bool speed=id=="lightingzone-1-1";return(PaintZoneNumber(speed?"speed":"brightness"),0,4,1,"档",PaintZoneEnabled&&(!speed||PaintZoneHasSpeed));}
        return id switch
        {
            "tuning-1-0"=>(p?.Pl1,15,cpuMax.Pl1,1,"W",cpuEditable),
            "tuning-1-1"=>(p?.Pl2,15,cpuMax.Pl2,1,"W",cpuEditable),
            "tuning-1-2"=>(p?.Pl4,15+TuningSnapshot.Pl4Headroom,cpuMax.Pl4,1,"W",cpuEditable),
            "tuning-1-4"=>(p?.TemperatureC,80,95,1,"°C",cpuEditable),
            "tuning-2-1"=>(p?.GpuBaseWatts,caps?.FloorWatts,caps?.BaseMaxWatts,1,"W",canEdit&&caps?.ConfigurableBase==true),
            "tuning-2-3"=>(p?.GpuBoostWatts,5,caps?.BoostMaxWatts,1,"W",canEdit&&caps?.Supported==true),
            "tuning-2-4"=>(p?.Offsets?.CoreMhz,0,caps?.CoreOffsetMax,1,"MHz",canEdit&&caps?.CoreOffsetMax>0),
            "tuning-2-5"=>(p?.Offsets?.MemoryMhz,0,caps?.MemoryOffsetMax,1,"MHz",canEdit&&caps?.MemoryOffsetMax>0),
            "tuning-3-3"=>(p?.FanStepMs,100,12700,100,"ms",canEdit),
            _=>nullRange()
        };
        static (double?,double?,double?,double,string,bool) nullRange()=>(null,null,null,1,"",false);
    }
    private static ColorParameter PaintColorParameter(string id)=>id switch{"color-1-0"=>ColorParameter.TemperatureKelvin,"color-1-1"=>ColorParameter.Saturation,"color-2-0"=>ColorParameter.Contrast,"color-2-1"=>ColorParameter.Gamma,"color-3-0"=>ColorParameter.RedGain,"color-3-1"=>ColorParameter.GreenGain,_=>ColorParameter.BlueGain};
    private static bool TryPaintRangeValue(string text, double? minimum, double? maximum, out double value) =>
        double.TryParse(text, out value) && double.IsFinite(value) &&
        minimum.HasValue && maximum.HasValue && value >= minimum && value <= maximum;

    private void CaptureResidentRangeDraft(string id, string text)
    {
        var bounds = PaintRangeBinding(id);
        if (!bounds.Value.HasValue || !TryPaintRangeValue(text, bounds.Min, bounds.Max, out double value)) return;
        if (id.StartsWith("tuning-", StringComparison.Ordinal) && value == Math.Truncate(value))
            SetPaintTuningDraftNumber(id, (int)value);
        else if (id.StartsWith("color-", StringComparison.Ordinal) && colorDraft != null)
        {
            var parameter = PaintColorParameter(id);
            if (parameter == ColorParameter.TemperatureKelvin && value != Math.Truncate(value)) return;
            colorDraft.Set(parameter, value);
            residentColorEdit = ResidentColorIntent(colorDraft, residentColorEdit?.Enabled ?? (PaintColorEnabled == true));
        }
    }

    private async Task PaintSetNumber(string id,double value,bool coalesceBrightness=false)
    {
        var bounds=PaintRangeBinding(id);if(!bounds.Enabled||!bounds.Min.HasValue||value<bounds.Min||value>bounds.Max)return;
        if(id.StartsWith("tuning"))
        {
            if(value!=Math.Round(value))throw new ArgumentException("调校参数必须为整数。");
            SetPaintTuningDraftNumber(id, (int)value);
            RefreshPaintedState();
            return;
        }
        if(uiReviewMode)return;if(id=="display-1-0"){await SetCompactBrightness((int)value,coalesceBrightness);return;}
        if(id.StartsWith("color")&&colorDraft!=null){colorDraft.Set(PaintColorParameter(id),value);await SubmitColor(true);RefreshPaintedState();return;}
        if(id.StartsWith("lightingzone"))await PaintSetZone(id=="lightingzone-1-1"?"speed":"brightness",JsonValue.Create((int)value)!);
    }
    private void SetPaintTuningDraftNumber(string id, int value)
    {
        if (tuningDraft is not { } current) return;
        tuningDraft = PaintCpuLimit(id) is { } cpuLimit ? EditPaintCpuDraft(current, cpuLimit, value) : id switch
        {
            "tuning-1-4" => current with { TemperatureC = value },
            "tuning-2-1" => current with { GpuBaseWatts = value },
            "tuning-2-3" => current with { GpuBoostWatts = value },
            "tuning-2-4" => current with { Offsets = new(value, current.Offsets?.MemoryMhz ?? 0) },
            "tuning-2-5" => current with { Offsets = new(current.Offsets?.CoreMhz ?? 0, value) },
            "tuning-3-3" => current with { FanStepMs = value },
            _ => current
        };
        PaintDraftChanged();
    }
    private void PaintDraftChanged()
    {
        if(residentClient!=null)TrackResidentTuningDraft();else RememberPresetDraft();
        tuningStatus="草稿已修改，尚未应用。";
    }
    private bool PaintIntegratedOnly => !uiReviewMode && (residentClient != null
        ? residentSnapshot?.Device?.IntegratedOnly == true : CoreMachine.Current.IntegratedOnly);

    private FrameworkElement PaintGpuAvailability(FrameworkElement modes)
    {
        var integrated=Paint.Inline(8,Paint.Icon("gpu-integrated",24),
            Paint.Stack(3,PT(() => IntegratedGpuModel ?? "集成显卡"),Paint.Text("此机型不支持三模切换",12,muted:true)));
        void Update()
        {
            bool integratedOnly=PaintIntegratedOnly;
            modes.Visibility=integratedOnly?Visibility.Collapsed:Visibility.Visible;
            integrated.Visibility=integratedOnly?Visibility.Visible:Visibility.Collapsed;
        }
        Update();
        paintBindings.Add(Update);
        return Paint.Stack(0,modes,integrated);
    }

    private FrameworkElement PaintGpuDetails()
    {
        var grid=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=460,ColumnSpacing=10,RowSpacing=10};foreach(var definition in originalGpuModes.OrderBy(m=>m.Mode)){int mode=definition.Mode;var b=PB(definition.Title,()=>RunGpuRequest(mode),true);var icon=Paint.Icon(mode==1?"gpu-integrated":mode==2?"gpu-hybrid":"gpu-discrete");icon.HorizontalAlignment=HorizontalAlignment.Center;var name=Paint.Text(definition.Title);name.TextAlignment=TextAlignment.Center;var desc=Paint.Text(definition.Description,12,muted:true);desc.TextAlignment=TextAlignment.Center;b.Content=Paint.Stack(8,icon,name,desc);b.Padding=new(16);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.StretchContent();paintBindings.Add(()=>{b.Selected=gpuState?.Mode==mode;b.IsEnabled=PanelHardwareActive&&gpuReadFresh&&!PaintGpuBusy&&gpuState?.Supports(mode)==true;});grid.Children.Add(b);}
        var read = PB("刷新状态", () => RunGpuRequest(0), true);
        paintBindings.Add(() => read.IsEnabled = !PaintGpuBusy);
        return PaintGpuAvailability(Paint.Stack(14,grid,Paint.Between(PT(()=>UsesDeclaredGpuMux?"Core 读取模式："+OriginalGpuName(gpuState?.Mode??0):"当前运行："+OriginalGpuName(GpuRuntimeMode)+(GpuRestartPending?" · 目标："+OriginalGpuName(gpuState?.Mode??0)+" · 待重启":""),12,muted:true),read)));
    }
    private FrameworkElement PaintBatteryHealth()
    {
        var choices=Paint.Stack(8);foreach(var(name,desc,limit)in new[]{("长效模式","经常使用电池",100),("平衡模式","兼顾便携与桌面",80),("工作站模式","长期连接适配器",60)}){var b=PB(name,()=>SetChargeLimit(limit),true);b.Content=Paint.Between(Paint.Stack(4,Paint.Text(name),Paint.Text(desc,12,muted:true)),Paint.Text(limit+"%"));b.Padding=new(12);b.StretchContent();paintBindings.Add(()=>{b.Selected=PanelBatteryLimit==limit;b.IsEnabled=PanelHardwareActive&&PanelSupportsBattery&&!PanelHardwareActionBusy(PanelOperation.SetBatteryLimit);});choices.Children.Add(b);}
        BindCapability(choices,()=>PanelSupportsBattery);
        var battery=PaintBatteryPicture(220);battery.Width=double.NaN;battery.MaxWidth=220;battery.HorizontalAlignment=HorizontalAlignment.Center;
        var amount=Paint.Inline(3,PT(()=>latest?.BatteryPercent?.ToString()??"—",24),Paint.Text("%"));amount.HorizontalAlignment=HorizontalAlignment.Center;
        var copy=Paint.Stack(6,Paint.Text("剩余电量",12,muted:true),amount,PT(()=>latest?.OnAc==true?"已连接电源":latest?.OnAc==false?"正在使用电池":"供电状态未知",12,muted:true),PT(()=>!ShowDeclaredControl(PanelSupportsBattery)?"":PanelBatteryLimit is int n?"充电上限 "+n+"%":"充电上限 —",12,muted:true));
        foreach(var text in copy.Children.OfType<TextBlock>())text.TextAlignment=TextAlignment.Center;
        var status=Paint.Stack(12,battery,copy);status.Padding=new(16);status.VerticalAlignment=VerticalAlignment.Center;
        return new ApprovedBatteryLayout(choices,status);
    }
    private double PaintMiniHeight(double width)
    {
        page.Measure(new Windows.Foundation.Size(Math.Max(300,width-12),double.PositiveInfinity));
        return Math.Ceiling(page.DesiredSize.Height+32+8);
    }
}


