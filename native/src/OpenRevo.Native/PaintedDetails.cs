using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
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
    {return Paint.Card(Paint.Stack(14,Paint.Inline(12,Paint.Icon(icon),Paint.Text(title,16,true)),content),20);}
    private FrameworkElement PaintSetting(string title,string description,FrameworkElement control)
    {var copy=Paint.Stack(4,Paint.Text(title));if(description.Length>0)copy.Children.Add(Paint.Text(description,12,muted:true));return Paint.Rule(Paint.Between(copy,control,16));}
    private void RenderPaintedDetails(string route)
    {
        if(route=="devices"){RenderPaintedLighting();return;}
        if(route=="tuning"&&hardware.DeclaredCpuTuning is {} limits){page.Children.Add(PaintDeclaredCpuTuning(limits));return;}
        if(route=="tuning"&&tuningBaseline==null&&!tuningLoading&&!uiReviewMode)_=PaintReadTuning();
        if(route=="color"&&colorDraft==null&&serviceColor.ValueKind==JsonValueKind.Object)colorDraft=JsonNode.Parse(serviceColor.GetProperty("active_preset").GetRawText())!.AsObject();
        if(route=="updates"){RenderPaintedUpdates();return;}
        if(!PaintPageSchema.Pages.TryGetValue(route,out var specs))return;
        var grid=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=960,ColumnSpacing=16,RowSpacing=16};
        foreach(var spec in specs)
        {
            if(route=="diagnostics"&&spec.Title!="诊断报告"&&(!debugSettingsVisible||debugSettingsHidden))continue;
            var rows=Paint.Stack(0);
            if(route=="display"&&spec.Title=="显卡模式"){rows.Children.Add(PaintGpuDetails());}
            else foreach(var row in spec.Rows)
            {
                if(spec.Collapsible&&!paintRgbExpanded)continue;
                rows.Children.Add(PaintDetailRow(route,row));
            }
            if(spec.Collapsible){var expand=new PaintButton(paintRgbExpanded?"收起微调":"展开微调",()=>{paintRgbExpanded=!paintRgbExpanded;Render();}){Variant="plain",Padding=new(0),MinHeight=32};rows.Children.Insert(0,expand);}
            var card=PaintSection(spec.Title,spec.Icon,rows);if(spec.Span==2)DesktopTiles.SetFullRow(card);grid.Children.Add(card);
        }
        page.Children.Add(grid);
        if(route=="tuning")page.Children.Add(Paint.Card(Paint.Between(PT(()=>tuningStatus,12,muted:true),PB("应用极客调校",PaintApplyTuning,true)),16,12));
        if(route is "display" or "power" or "sleep" or "lightingzone" or "color")_=ReadDesktopServices();
        if(route=="settings"){_=PaintReadPlans();_=PaintReadStartup();}
    }
    private FrameworkElement PaintDetailRow(string route,PaintRowSpec row)
    {
        if(row.Kind=="battery-health")return PaintBatteryHealth();
        if(row.Kind=="curves")return PaintFanCurves();
        if(row.Kind=="osd")return PaintOsdPreview();
        if(row.Kind=="range")return PaintRange(row);
        if(row.Kind=="brightness")return PaintSetting(row.Label,row.Description,Paint.Inline(6,new[]{25,60,100}.Select(v=>(UIElement)PB(v+"%",()=>SetCompactBrightness(v),true)).ToArray()));
        if(row.Kind=="update")
        {
            var check=PB("检查更新",async()=>{if(!uiReviewMode)await CheckNativeUpdate();});
            var details=new PaintButton("更新详情",()=>PaintNavigate("updates"));
            paintBindings.Add(()=>check.IsEnabled=!updateBusy&&!exitRequested);
            return Paint.Stack(6,PaintSetting(row.Label,row.Description,Paint.Inline(8,check,details)),
                PT(()=>updateMessage,12,muted:true));
        }
        if(row.Kind=="tools")return Paint.Rule(PaintTools(route));
        if(row.Kind=="toggle")
        {var toggle=PS(row.Label,()=>PaintReadToggle(row.Id),value=>PaintWriteToggle(row.Id,value),()=>PaintToggleEnabled(row.Id));return PaintSetting(row.Label,row.Description,toggle);}
        if(row.Kind is "select" or "refresh")return PaintSetting(row.Label,row.Description,PaintChoice(row));
        if(row.Kind=="status")return PaintSetting(row.Label,row.Description,PT(()=>PaintStatus(row.Id),12,muted:true));
        if(row.Kind=="color")return PaintSetting(row.Label,row.Description,PaintColorEntry(false));
        string label=row.Kind=="link"?"查看":row.Label switch{"选择位置并导出"=>"导出","打开默认目录" or "打开目录" or "查看日志"=>"打开目录","退出程序"=>"退出","预览提示"=>"预览","关闭屏幕"=>"息屏",_=>row.Label};
        var button=PB(label,()=>PaintPageAction(row.Id),row.Id is "power-2-3" or "settings-2-2");if(row.Kind=="link"){button.Variant="plain";button.Content=Paint.Inline(6,Paint.Text("查看",12),Paint.Utility("chevron",14));}
        return PaintSetting(row.Label,row.Description,button);
    }
    private bool? PaintReadToggle(string id)=>id switch
    {
        "display-1-3"=>lowRefreshOnBattery,"display-2-0" or "color-0-2"=>serviceColor.ValueKind==JsonValueKind.Object?JsonBool(serviceColor,"runtime_enabled"):null,
        "power-1-0"=>PowerServiceValue("usb"),"power-1-1"=>PowerServiceValue("recovery"),"power-1-2"=>PowerServiceValue("link"),"power-1-3"=>PowerServiceValue("office"),"power-2-2"=>sessionPower?.Enabled==true,
        "sleep-0-0"=>PowerServiceValue("master"),"sleep-1-0"=>PowerServiceValue("disconnect_net"),"sleep-1-1"=>PowerServiceValue("block_away"),"sleep-1-2"=>PowerServiceValue("ignore_sysreq"),"sleep-2-0"=>PowerServiceValue("disable_wake_timers"),"sleep-2-1"=>PowerServiceValue("deep_sleep"),"sleep-2-2"=>PowerServiceValue("hibernate_30m"),
        "settings-0-2"=>systemPreferences.MiniAutoHide,"settings-1-0"=>paintStartup,"settings-1-1"=>hardware.Active,"settings-1-3"=>systemPreferences.UnlockBatteryLimit,"osdsettings-0-0"=>osdEnabled,
        "plugins-0-0"=>summaryExtension?.Enabled??true,"lightingzone-0-1"=>PaintZoneBool(),
        "tuning-1-3"=>tuningDraft?.TemperatureEnabled,"tuning-2-0"=>tuningDraft?.GpuSuperPerformance,"tuning-2-2"=>tuningDraft?.GpuBoostEnabled,"tuning-3-0"=>tuningDraft?.CustomFans,"tuning-3-2"=>tuningDraft?.SmoothFans,_=>null
    };
    private bool PaintToggleEnabled(string id)
    {
        if(id.StartsWith("tuning"))return tuningDraft!=null&&!tuningApplying;
        if(id.StartsWith("sleep")||id.StartsWith("power-1"))return upstream!=null&&!desktopServiceBusy&&(id is not("power-1-0" or "power-1-1" or "power-1-3")||hardware.Active);
        if(id is "display-2-0" or "color-0-2")return serviceColor.ValueKind==JsonValueKind.Object&&!colorServicesBusy;
        if(id=="lightingzone-0-1")return JsonBool(serviceLighting,selectedLightingZone)&&hardware.Active&&!zoneServicesBusy;
        return true;
    }
    private async Task PaintWriteToggle(string id,bool value)
    {
        if(id.StartsWith("tuning"))
        {
            if(tuningDraft is not{} p)return;tuningDraft=id switch{"tuning-1-3"=>p with{TemperatureEnabled=value},"tuning-2-0"=>p with{GpuSuperPerformance=value},"tuning-2-2"=>p with{GpuBoostEnabled=value},"tuning-3-0"=>p with{CustomFans=value},"tuning-3-2"=>p with{SmoothFans=value},_=>p};PaintDraftChanged();return;
        }
        if(uiReviewMode&&id is not("settings-0-2" or "settings-1-3" or "osdsettings-0-0"))return;
        string? policy=id switch{"power-1-0"=>"usb","power-1-1"=>"recovery","power-1-2"=>"link","power-1-3"=>"office","sleep-0-0"=>"master","sleep-1-0"=>"disconnect_net","sleep-1-1"=>"block_away","sleep-1-2"=>"ignore_sysreq","sleep-2-0"=>"disable_wake_timers","sleep-2-1"=>"deep_sleep","sleep-2-2"=>"hibernate_30m",_=>null};
        if(policy!=null){await ChangePowerService(policy,value);return;}
        switch(id)
        {
            case "display-1-3":lowRefreshOnBattery=value;await SavePreferences();await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(value?"已开启":"已关闭"),"refresh");break;
            case "display-2-0":case "color-0-2":await SubmitColor(enabledOverride:value);break;
            case "power-2-2":await RunQuickAction("awake");break;
            case "settings-0-2":await SaveSystemSettings(systemPreferences with{MiniAutoHide=value});break;
            case "settings-1-0":await Task.Run(()=>StartupRegistration.SetEnabled(value));await PaintReadStartup();break;
            case "settings-1-1":await PaintTakeover(value);break;
            case "settings-1-3":if(value&&!await Confirm("解除离电限制？","允许电池或Type-C使用狂暴及自定义模式。供电保护和参数边界仍保留。"))return;await SaveSystemSettings(systemPreferences with{UnlockBatteryLimit=value});if(!value)await EnforceBatteryPerformanceGuard();break;
            case "osdsettings-0-0":osdEnabled=value;await SavePreferences();if(!value)nativeOsd?.Hide();else ConfirmedActionOsd("屏幕提示 · 已开启","osd");Render();break;
            case "plugins-0-0":EnsureExtensionHost();using(var doc=JsonDocument.Parse(value?"{\"enabled\":true}":"{\"enabled\":false}")){summaryExtension!.SaveConfig(doc.RootElement);summaryExtension.SetEnabled(value);}break;
            case "lightingzone-0-1":await PaintSetZone("enabled",JsonValue.Create(value)!);break;
        }
    }
    private PaintSelect PaintChoice(PaintRowSpec row)
    {
        string[] items=[];int index=-1;Func<int,Task> changed=_=>Task.CompletedTask;
        switch(row.Id)
        {
            case "display-1-2":int[] rates=displayState?.Rates.Distinct().Order().ToArray()??[];items=rates.Select(r=>r+" Hz").ToArray();index=Array.IndexOf(rates,displayState?.Hertz??0);changed=i=>ChangeRefresh(rates[i]);break;
            case "display-2-2":var profiles=serviceIcc.ValueKind==JsonValueKind.Object?serviceIcc.GetProperty("profiles").EnumerateArray().Select(p=>(Id:p.GetProperty("id").GetString()!,Name:p.GetProperty("name").GetString()!)).ToArray():[];items=profiles.Select(p=>p.Name).ToArray();index=serviceIcc.ValueKind==JsonValueKind.Object?Array.FindIndex(profiles,p=>p.Id==serviceIcc.GetProperty("active_profile_id").GetString()):-1;changed=i=>ApplyIcc(profiles[i].Id);break;
            case "settings-0-0":items=["跟随 Windows","浅色","深色"];index=selectedTheme;changed=async i=>{await ChangeApplicationTheme(i);Render();};break;
            case "settings-0-4":
                string[] speeds=["fast","normal","slow","off"];
                items=["快","标准","慢","关闭动效"];
                index=Array.IndexOf(speeds,systemPreferences.MotionSpeed);
                changed=i=>SaveSystemSettings(systemPreferences with{MotionSpeed=speeds[i]});
                break;
            case "settings-0-1":items=["记忆上次形态","始终迷你面板","始终完整面板"];index=Array.IndexOf(new[]{"remember","mini","full"},systemPreferences.WakeMode);changed=i=>SaveSystemSettings(systemPreferences with{WakeMode=new[]{"remember","mini","full"}[i]});break;
            case "settings-1-2":items=["2 秒","5 秒","10 秒"];index=Array.IndexOf(new[]{2,5,10},interval);changed=async i=>{interval=new[]{2,5,10}[i];timer.Interval=TimeSpan.FromSeconds(interval);await SavePreferences();};break;
            case "settings-2-0":items=paintPlans.Select(p=>p.Name).ToArray();changed=i=>PaintSwitchPlan(paintPlans[i]);break;
            case "osdsettings-0-1":items=["电影字幕","向右羽化","磨砂胶囊"];index=Array.IndexOf(new[]{"cinema","fade","pill"},systemPreferences.OsdTheme);changed=async i=>{await SaveSystemSettings(systemPreferences with{OsdTheme=new[]{"cinema","fade","pill"}[i]});Render();};break;
            case "osdsettings-0-2":items=["80%","100%","120%","140%"];index=Array.IndexOf(new[]{80,100,120,140},systemPreferences.OsdScale);changed=async i=>{await SaveSystemSettings(systemPreferences with{OsdScale=new[]{80,100,120,140}[i]});Render();};break;
            case "osdsettings-0-3":items=["左上角","下方中央","屏幕正中"];index=Array.IndexOf(new[]{"top-left","bottom-center","center"},systemPreferences.OsdPosition);changed=async i=>{await SaveSystemSettings(systemPreferences with{OsdPosition=new[]{"top-left","bottom-center","center"}[i]});Render();};break;
            case "color-0-0":var presets=serviceColor.ValueKind==JsonValueKind.Object?serviceColor.GetProperty("presets").EnumerateArray().Select(p=>p.Clone()).ToArray():[];items=presets.Select(p=>p.GetProperty("name").GetString()!).ToArray();index=Array.FindIndex(presets,p=>p.GetProperty("id").GetString()==colorDraft?["id"]?.GetValue<string>());changed=async i=>{colorDraft=JsonNode.Parse(presets[i].GetRawText())!.AsObject();await SubmitColor(enabledOverride:true);Render();};break;
            case "tuning-0-0":items=tuningPresets.Select(p=>p.Name).ToArray();index=tuningPresets.FindIndex(p=>p.Id==tuningPresetId);changed=i=>{RememberPresetDraft();var p=tuningPresets[i];tuningPresetId=p.Id;if(!uiReviewMode)AppData.Update(a=>a with{SelectedTuningPreset=p.Id});tuningDraft=p.Profile.Copy();tuningStatus="方案已载入草稿，尚未应用。";Render();return Task.CompletedTask;};break;
            case "lightingzone-0-0":items=["前置灯条","转轴氛围灯","Logo 灯光"];index=Array.IndexOf(new[]{"front","hinge","logo"},selectedLightingZone);changed=i=>{selectedLightingZone=new[]{"front","hinge","logo"}[i];Render();return Task.CompletedTask;};break;
            case "lightingzone-0-2":var effects=PaintZoneEffects();items=effects.Select(e=>e.Name).ToArray();index=Array.FindIndex(effects,e=>e.Id==PaintZoneNumber("effect"));changed=i=>PaintSetZone("effect",JsonValue.Create(effects[i].Id)!);break;
            case "diagnostics-1-0":items=["关闭","仅错误","所有日志"];index=systemPreferences.LogLevel;changed=async i=>{await SaveSystemSettings(systemPreferences with{LogLevel=i});await ConfigureDebugLog();};break;
            case "diagnostics-1-1":items=["全部模块","外置水冷","自定义追踪","RGB 与推流","电源与风扇","底层 EC","设备外设","窗口与系统"];var filters=new[]{"","cooler","custom","light","power","ec","device","ui"};index=Array.IndexOf(filters,systemPreferences.LogFilter);changed=async i=>{await SaveSystemSettings(systemPreferences with{LogFilter=filters[i]});await ConfigureDebugLog();};break;
            case "diagnostics-2-0":items=["真实本机","星耀14轻薄本","游戏本形态"];index=Array.IndexOf(new[]{"real","thin_light","gaming"},developerShape);changed=i=>{developerShape=new[]{"real","thin_light","gaming"}[i];RefreshPaintedState();return Task.CompletedTask;};break;
        }
        var choice=new PaintSelect(row.Label,items,index,async i=>{try{if(!uiReviewMode||row.Id.StartsWith("osdsettings")||row.Id.StartsWith("settings-0")||row.Id=="settings-1-2"||row.Id.StartsWith("tuning"))await changed(i);}catch(Exception error){await ShowMessage("设置未完成",error.Message);}RefreshPaintedState();}){Width=180};
        if(row.Id=="settings-2-0")paintPlanChoice=choice;
        if(row.Id.StartsWith("display"))paintBindings.Add(()=>choice.IsEnabled=row.Id=="display-1-2"?displayState?.Active==true&&!displayBusy:serviceIcc.ValueKind==JsonValueKind.Object&&!colorServicesBusy);
        return choice;
    }
    private string PaintStatus(string id)=>id switch
    {
        "plugins-0-1"=>latest==null?"等待下一次采样…":$"CPU {latest.CpuPercent:0.0}% · 电池 {latest.BatteryPercent}%","plugins-1-0" or "plugins-1-1"=>"已提供","plugins-1-2"=>"未开放","plugins-2-0"=>"未接入","plugins-2-1"=>"不提供市场",
        "settings-4-0"=>"GPL-3.0-or-later","storage-0-0"=>AppData.DirectoryPath,"storage-1-1"=>"仅本版本","diagnostics-0-0"=>"脱敏导出","diagnostics-2-1"=>developerShape=="real"?identity.Product:"开发者布局预览，不修改本机能力",
        "color-4-0"=>colorPending?"更新中":serviceColor.ValueKind!=JsonValueKind.Object?"状态待读取":JsonBool(serviceColor,"runtime_enabled")?"全局滤镜已开启"+(JsonBool(serviceColor,"gamma_applied")?" · Gamma 已确认":" · Gamma 未确认"):"已关闭",
        "lightingzone-2-0"=>JsonBool(serviceLighting,"capability_known")?(JsonBool(serviceLighting,selectedLightingZone)?"已识别":"不支持"):"待设备识别","lightingzone-2-1"=>JsonBool(serviceLighting,"capability_known")?"指令由 Core 确认；此灯区无完整回读":"能力未知",_=>"状态待读取"
    };
    private async Task PaintPageAction(string id)
    {
        switch(id)
        {
            case "display-2-1":PaintNavigate("color");break;case "display-2-3":if(!uiReviewMode)await ImportIcc();break;
            case "power-2-0":PaintNavigate("sleep");break;case "power-2-1":PaintNavigate("settings");break;case "power-2-3":await PaintQuickAction("screen");break;
            case "plugins-0-2":PaintNavigate("overview");break;
            case "settings-0-3":PaintNavigate("osdsettings");break;case "settings-3-0":PaintNavigate("storage");break;case "settings-3-1":PaintNavigate("diagnostics");break;
            case "settings-2-1":await PaintReadPlans();break;case "settings-2-2":await PaintRecoverPlans();break;
            case "settings-3-2":TrayMenu();break;case "settings-3-3":if(!uiReviewMode)await ExitFromTray();break;
            case "storage-0-1":if(!uiReviewMode)OpenLocalFolder(AppData.DirectoryPath);break;case "storage-0-2":await MigrateDataStorage();break;case "storage-1-0":await ClearDataStorage();break;
            case "diagnostics-0-1":case "diagnostics-1-2":if(!uiReviewMode)OpenLocalFolder(AppData.DiagnosticsPath);break;case "diagnostics-0-2":if(!uiReviewMode)await Export();break;
            case "diagnostics-1-3":await SaveSystemSettings(systemPreferences with{LogLevel=0});await ConfigureDebugLog();debugSettingsHidden=true;Render();break;
            case "osdsettings-1-1":PaintOsdReplay();break;
            case "tuning-0-2":await PaintReadTuning();break;
        }
    }
    private FrameworkElement PaintRange(PaintRowSpec row)
    {
        var binding=PaintRangeBinding(row.Id);bool known=binding.Value.HasValue&&binding.Min.HasValue&&binding.Max.HasValue;
        var field=new PaintField(row.Label,binding.Value?.ToString("0.##")??"—");field.Committed+=async text=>{if(!double.TryParse(text,out double next)||!double.IsFinite(next)||!binding.Min.HasValue||next<binding.Min||next>binding.Max){await ShowMessage("数值无效","请填写实际范围内的完整数值。");return;}await PaintSetNumber(row.Id,next);RefreshPaintedState();};
        var slider=new PaintSlider(row.Label,binding.Min??0,binding.Max??1,binding.Value??0,binding.Step,async next=>await PaintSetNumber(row.Id,next));
        paintBindings.Add(()=>{var state=PaintRangeBinding(row.Id);bool ready=state.Value.HasValue&&state.Min.HasValue&&state.Max.HasValue&&state.Enabled;field.IsEnabled=slider.IsEnabled=ready;if(state.Value.HasValue){field.Value=state.Value.Value.ToString("0.##");slider.Value=state.Value.Value;}});
        var labels=Paint.Stack(4,Paint.Text(row.Label));if(row.Description.Length>0)labels.Children.Add(Paint.Text(row.Description,12,muted:true));
        return Paint.Rule(Paint.Stack(8,Paint.Between(labels,Paint.Inline(5,field,Paint.Text(binding.Unit,12,muted:true))),slider,Paint.Between(Paint.Text(known?binding.Min+" "+binding.Unit:"—",12,muted:true),Paint.Text(known?binding.Max+" "+binding.Unit:"—",12,muted:true))));
    }
    private (double? Value,double? Min,double? Max,double Step,string Unit,bool Enabled) PaintRangeBinding(string id)
    {
        var p=tuningDraft;var caps=tuningBaseline?.Gpu.Capabilities;
        if(id=="display-1-0")return(screenBrightness,10,100,1,"%",displayState?.Active==true&&!displayBusy);
        if(id.StartsWith("color")){string key=PaintColorKey(id);double min=id=="color-1-0"?4000:id=="color-1-1"?0:id=="color-2-1"?.7:.5,max=id=="color-1-0"?9000:id=="color-1-1"?2:id=="color-2-1"?1.3:1.5;return(colorDraft?[key]?.GetValue<double>(),min,max,id=="color-1-0"?50:.02,id=="color-1-0"?"K":"",serviceColor.ValueKind==JsonValueKind.Object&&!colorServicesBusy);}
        if(id.StartsWith("lightingzone")){bool speed=id=="lightingzone-1-1";return(PaintZoneNumber(speed?"speed":"brightness"),0,4,1,"档",JsonBool(serviceLighting,selectedLightingZone)&&hardware.Active&&!zoneServicesBusy&&(!speed||selectedLightingZone!="logo"&&!(selectedLightingZone=="front"&&JsonBool(serviceLighting,"ec_front"))));}
        return id switch
        {"tuning-1-0"=>(p?.Pl1,15,tuningBaseline?.Pl1Max,1,"W",p!=null&&!tuningApplying),"tuning-1-1"=>(p?.Pl2,15,tuningBaseline?.Pl2Max,1,"W",p!=null&&!tuningApplying),"tuning-1-2"=>(p?.Pl4,35,tuningBaseline?.Pl4Max,1,"W",p!=null&&!tuningApplying),"tuning-1-4"=>(p?.TemperatureC,80,95,1,"°C",p?.TemperatureEnabled==true&&!tuningApplying),
        "tuning-2-1"=>(p?.GpuBaseWatts,caps?.FloorWatts,caps?.BaseMaxWatts,1,"W",caps?.ConfigurableBase==true&&p?.GpuSuperPerformance==false),"tuning-2-3"=>(p?.GpuBoostWatts,5,caps?.BoostMaxWatts,1,"W",p?.GpuBoostEnabled==true&&p?.GpuSuperPerformance==false),"tuning-2-4"=>(p?.Offsets?.CoreMhz,0,caps?.CoreOffsetMax,1,"MHz",p?.GpuSuperPerformance==true&&caps?.CoreOffsetMax>0),"tuning-2-5"=>(p?.Offsets?.MemoryMhz,0,caps?.MemoryOffsetMax,1,"MHz",p?.GpuSuperPerformance==true&&caps?.MemoryOffsetMax>0),"tuning-3-3"=>(p?.FanStepMs,100,12700,100,"ms",p?.SmoothFans==true),_=>nullRange()};
        static (double?,double?,double?,double,string,bool) nullRange()=>(null,null,null,1,"",false);
    }
    private static string PaintColorKey(string id)=>id switch{"color-1-0"=>"color_temp_k","color-1-1"=>"saturation","color-2-0"=>"contrast","color-2-1"=>"gamma","color-3-0"=>"gain_r","color-3-1"=>"gain_g",_=>"gain_b"};
    private async Task PaintSetNumber(string id,double value)
    {
        var bounds=PaintRangeBinding(id);if(!bounds.Enabled||!bounds.Min.HasValue||value<bounds.Min||value>bounds.Max)return;
        if(id.StartsWith("tuning")){if(value!=Math.Round(value))throw new ArgumentException("调校参数必须为整数。");if(tuningDraft is not{} p)return;int n=(int)value;tuningDraft=id switch{"tuning-1-0"=>p with{Pl1=n},"tuning-1-1"=>p with{Pl2=n},"tuning-1-2"=>p with{Pl4=n},"tuning-1-4"=>p with{TemperatureC=n},"tuning-2-1"=>p with{GpuBaseWatts=n},"tuning-2-3"=>p with{GpuBoostWatts=n},"tuning-2-4"=>p with{Offsets=new(n,p.Offsets?.MemoryMhz??0)},"tuning-2-5"=>p with{Offsets=new(p.Offsets?.CoreMhz??0,n)},"tuning-3-3"=>p with{FanStepMs=n},_=>p};PaintDraftChanged();RefreshPaintedState();return;}
        if(uiReviewMode)return;if(id=="display-1-0"){await SetCompactBrightness((int)value);return;}
        if(id.StartsWith("color")&&colorDraft!=null){string key=PaintColorKey(id);if(key=="color_temp_k")colorDraft[key]=(int)value;else colorDraft[key]=value;await SubmitColor(true);RefreshPaintedState();return;}
        if(id.StartsWith("lightingzone"))await PaintSetZone(id=="lightingzone-1-1"?"speed":"brightness",JsonValue.Create((int)value)!);
    }
    private void PaintDraftChanged(){RememberPresetDraft();tuningStatus="草稿已修改，尚未应用。";}
    private bool PaintIntegratedOnly => !uiReviewMode && CoreMachine.Current.IntegratedOnly;

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
        var grid=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=460,ColumnSpacing=10,RowSpacing=10};foreach(var definition in originalGpuModes.OrderBy(m=>m.Mode)){int mode=definition.Mode;var b=PB(definition.Title,()=>RunGpuRequest(mode),true);var icon=Paint.Icon(mode==1?"gpu-integrated":mode==2?"gpu-hybrid":"gpu-discrete");icon.HorizontalAlignment=HorizontalAlignment.Center;var name=Paint.Text(definition.Title);name.TextAlignment=TextAlignment.Center;var desc=Paint.Text(definition.Description,12,muted:true);desc.TextAlignment=TextAlignment.Center;b.Content=Paint.Stack(8,icon,name,desc);b.Padding=new(16);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.StretchContent();paintBindings.Add(()=>{b.Selected=gpuState?.Mode==mode;b.IsEnabled=hardware.Active&&gpuReadFresh&&!gpuChanging&&gpuState?.Supports(mode)==true;});grid.Children.Add(b);}
        return PaintGpuAvailability(Paint.Stack(14,grid,Paint.Between(PT(()=>UsesDeclaredGpuMux?"Core 读取模式："+OriginalGpuName(gpuState?.Mode??0):"当前运行："+OriginalGpuName(GpuRuntimeMode)+(GpuRestartPending?" · 目标："+OriginalGpuName(gpuState?.Mode??0)+" · 待重启":""),12,muted:true),PB("刷新状态",()=>RunGpuRequest(0),true))));
    }
    private FrameworkElement PaintBatteryHealth()
    {
        var choices=Paint.Stack(8);foreach(var(name,desc,limit)in new[]{("长效模式","经常使用电池",100),("平衡模式","兼顾便携与桌面",80),("工作站模式","长期连接适配器",60)}){var b=PB(name,()=>RunHardware(()=>hardware.ApplyBatteryCommand(limit),hardware.CoreDelegated?"Core 已发送充电上限命令。":"充电上限已回读确认。"),true);b.Content=Paint.Between(Paint.Stack(4,Paint.Text(name),Paint.Text(desc,12,muted:true)),Paint.Text(limit+"%"));b.Padding=new(12);b.StretchContent();paintBindings.Add(()=>{b.Selected=hardware.State?.Upper==limit;b.IsEnabled=hardware.Active&&hardware.SupportsBattery&&!hardwareBusy;});choices.Children.Add(b);}
        BindCapability(choices,()=>hardware.SupportsBattery);
        var battery=PaintBatteryPicture(220);battery.Width=double.NaN;battery.MaxWidth=220;var copy=Paint.Stack(6,Paint.Text("剩余电量",12,muted:true),Paint.Inline(3,PT(()=>latest?.BatteryPercent?.ToString()??"—",24),Paint.Text("%")),PT(()=>latest?.OnAc==true?"已连接电源":latest?.OnAc==false?"正在使用电池":"供电状态未知",12,muted:true),PT(()=>!ShowDeclaredControl(hardware.SupportsBattery)?"":hardware.State?.Upper is byte n?"充电上限 "+n+"%":"充电上限 —",12,muted:true));var status=new Grid{ColumnSpacing=24,Padding=new(16),VerticalAlignment=VerticalAlignment.Center};status.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star),MaxWidth=220});status.ColumnDefinitions.Add(new(){Width=GridLength.Auto,MinWidth=120});status.Children.Add(battery);Grid.SetColumn(copy,1);status.Children.Add(copy);return new ApprovedBatteryLayout(choices,status);
    }
    private double PaintMiniHeight(double width)
    {
        if(!UsePaintedUi)return 652;
        page.Measure(new Windows.Foundation.Size(Math.Max(300,width-12),double.PositiveInfinity));
        return Math.Ceiling(page.DesiredSize.Height+32+8);
    }
}


