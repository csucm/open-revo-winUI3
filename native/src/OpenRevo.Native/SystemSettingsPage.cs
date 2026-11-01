using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool settingsDataBusy,settingsPowerBusy,dataStorageExitPending;
    private readonly bool debugSettingsVisible=Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--debug",StringComparison.OrdinalIgnoreCase)||a=="--log-errors"||a=="--ui-settings-checks");
    private bool debugSettingsHidden;
    private string developerShape="real";
    private string? dataMigrationTarget;
    private async Task InitializeSystemSettingsRuntime()
    {
        if(uiReviewMode)return;
        try{UserDataStore.EnsureMarker();}catch(Exception error){actions.Add("数据目录："+error.Message);}
        if(debugSettingsVisible)
        {
            string? option=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("--debug=",StringComparison.OrdinalIgnoreCase));
            systemPreferences=systemPreferences with{LogLevel=Environment.GetCommandLineArgs().Contains("--log-errors")?1:2,LogFilter=option?.Split('=',2)[1]??systemPreferences.LogFilter};
            try{await ConfigureDebugLog();}catch(Exception error){actions.Add("调试日志："+error.Message);}
        }
    }
    private async Task SaveSystemSettings(SystemPreferences next)
    {
        if(uiReviewMode){systemPreferences=next;return;}
        bool changed=next.OsdTheme!=systemPreferences.OsdTheme||next.OsdScale!=systemPreferences.OsdScale||next.OsdPosition!=systemPreferences.OsdPosition;
        next.Save();systemPreferences=next;hardware.SetBatteryPerformancePermission(next.UnlockBatteryLimit);nativeOsd?.Configure(next);
        if(changed&&osdEnabled){nativeOsd??=new NativeOsd(DispatcherQueue);nativeOsd.Configure(next);nativeOsd.Show("Open-Revo","屏幕提示预览");}
        await Task.CompletedTask;
    }
    private async Task ConfigureDebugLog()
    {
        NativeAppLog.Level=systemPreferences.LogLevel;NativeAppLog.Filter=systemPreferences.LogFilter;
        DiagnosticLog.Record("logging.debug_configured",new{level=systemPreferences.LogLevel,filterConfigured=!string.IsNullOrEmpty(systemPreferences.LogFilter)});
        if(upstream!=null)await Task.Run(()=>upstream.ExecuteFeature(9,new{level=systemPreferences.LogLevel,filter=systemPreferences.LogFilter}));
        NativeAppLog.Write("[ui] logging configuration active");
    }
    private static StackPanel SettingsControls(params UIElement[] controls)
    {var panel=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center};foreach(var c in controls){if(c is FrameworkElement element)element.VerticalAlignment=VerticalAlignment.Center;panel.Children.Add(c);}return panel;}
    private void RenderSystemSettings()
    {
        var rows=Ui.Stack();rows.Spacing=8;
        void Add(string glyph,string title,string detail,FrameworkElement control,double stackBelow=0)
        {var row=DetailRow(glyph,title,detail,control,stackBelow);var inset=DetailInset(row);inset.Padding=new Thickness(14,8,14,8);rows.Children.Add(inset);}
        ComboBox Choice(string name,string[] labels,int selected,Func<int,Task> changed,double width=150)
        {
            var box=new ComboBox{ItemsSource=labels,SelectedIndex=selected,Width=width};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box,name);
            box.SelectionChanged+=async(_,_)=>{if(box.SelectedIndex<0)return;try{await changed(box.SelectedIndex);}catch(Exception error){await ShowMessage("设置未保存",error.Message);}};return box;
        }
        ToggleSwitch Switch(string name,bool value,Func<bool,Task> changed)
        {
            var toggle=ServiceToggle(name,value,(_,_)=>{});bool syncing=false;
            toggle.Toggled+=async(_,_)=>{if(syncing)return;bool next=toggle.IsOn;try{await changed(next);}catch(Exception error){syncing=true;toggle.IsOn=!next;syncing=false;await ShowMessage("设置未完成",error.Message);}};return toggle;
        }
        Add("\uE790","应用主题","保存到本机；重新启动和托盘唤醒后保持所选主题。",Choice("应用主题",["跟随 Windows","浅色 · Fluent","深色"],selectedTheme,ChangeApplicationTheme,180));
        var osdStyle=Choice("OSD 风格",["电影字幕","向右羽化","磨砂胶囊"],Array.IndexOf(new[]{"cinema","fade","pill"},systemPreferences.OsdTheme),i=>SaveSystemSettings(systemPreferences with{OsdTheme=new[]{"cinema","fade","pill"}[i]}),125);
        var osdScale=Choice("OSD 大小",["80%","100%","120%","140%"],Array.IndexOf(new[]{80,100,120,140},systemPreferences.OsdScale),i=>SaveSystemSettings(systemPreferences with{OsdScale=new[]{80,100,120,140}[i]}),82);
        var osdPosition=Choice("OSD 位置",["左上角","下方中央","屏幕正中"],Array.IndexOf(new[]{"top-left","bottom-center","center"},systemPreferences.OsdPosition),i=>SaveSystemSettings(systemPreferences with{OsdPosition=new[]{"top-left","bottom-center","center"}[i]}),112);
        var osdToggle=Switch("OSD 屏幕悬浮提示",osdEnabled,async on=>{osdEnabled=on;await SavePreferences();UpdateExtraSwitchButtons();if(!on)nativeOsd?.Hide();});
        Add("\uE7F4","OSD 屏幕悬浮提示","性能与设备状态提示；风格、大小和位置立即用于下一次提示。",SettingsControls(osdStyle,osdScale,osdPosition,Ui.Button("预览",(_,_)=>{nativeOsd??=new NativeOsd(DispatcherQueue);nativeOsd.Configure(systemPreferences);nativeOsd.Show("Open-Revo","屏幕提示预览");}),osdToggle),1050);
        Add("\uE7E8","开机自动启动（最小化至托盘）","登录后启动后台控制；从托盘点击唤醒面板。",StartupCheckBox());
        var unlock=ServiceToggle("解除离电限制",systemPreferences.UnlockBatteryLimit,(_,_)=>{});bool unlocking=false;
        unlock.Toggled+=async(_,_)=>
        {
            if(unlocking)return;bool next=unlock.IsOn;unlocking=true;
            try
            {
                if(next&&!await Confirm("解除离电限制？","将允许在电池或 Type-C 供电下使用狂暴和自定义模式，并停止拔电时自动降档。可能增加发热、耗电，供电不足时可能关机或丢失未保存内容。CPU/GPU/风扇数值保护仍保留。")){unlock.IsOn=false;return;}
                await SaveSystemSettings(systemPreferences with{UnlockBatteryLimit=next});if(!next)await EnforceBatteryPerformanceGuard();
            }
            catch(Exception error){unlock.IsOn=!next;await ShowMessage("设置未完成",error.Message);}
            finally{unlocking=false;}
        };
        Add("\uE945","解除离电限制","默认使用原装 DC 供电保护；开启后允许电池 / Type-C 使用狂暴与自定义。",unlock);
        Add("\uE740","唤醒面板形态","托盘点击或重复启动时，记忆最后关闭形态或使用固定形态。",Choice("唤醒面板形态",["记忆上次形态","始终迷你面板","始终完整面板"],Array.IndexOf(new[]{"remember","mini","full"},systemPreferences.WakeMode),i=>SaveSystemSettings(systemPreferences with{WakeMode=new[]{"remember","mini","full"}[i]}),180));
        Add("\uE73F","迷你面板点击外部自动收起","仅迷你模式失去焦点时收回托盘，完整面板不受影响。",Switch("迷你面板自动收起",systemPreferences.MiniAutoHide,on=>SaveSystemSettings(systemPreferences with{MiniAutoHide=on})));
        Add("\uE7F8","硬件接管",(hardware.Active?"已接管":"未接管")+" · "+CoreMachine.Current.Reason+"。关闭后停止控制；重新打开会核验并接管。",TakeoverSwitch());
        Add("\uE823","刷新间隔（秒）","面板隐藏后暂停界面采样；后台睡眠和供电策略继续运行。",Choice("刷新间隔",["2 秒","5 秒","10 秒"],Array.IndexOf(new[]{2,5,10},interval),async i=>{interval=new[]{2,5,10}[i];timer.Interval=TimeSpan.FromSeconds(interval);await SavePreferences();},112));
        var dataActions=SettingsControls(Ui.Button("打开目录",(_,_)=>OpenLocalFolder(AppData.DirectoryPath)),Ui.Button("迁移存储",async(_,_)=>await MigrateDataStorage()),Ui.Button("清除数据",async(_,_)=>await ClearDataStorage()));
        Add("\uEDA2","数据存储管理",dataMigrationTarget==null?"当前目录："+AppData.DirectoryPath:"已复制至："+dataMigrationTarget+"；下次完整退出并启动后使用。",dataActions,850);
        rows.Children.Add(UpdateControls());
        var planChoice=new ComboBox{Width=180,MinHeight=36,Height=36,VerticalContentAlignment=VerticalAlignment.Center};bool syncingPlans=false;
        async Task ReadPlans()
        {
            try{var state=await Task.Run(()=>{var p=new WindowsPowerPlans();return(plans:p.List(),active:p.Active());});syncingPlans=true;planChoice.ItemsSource=state.plans;planChoice.SelectedItem=state.plans.FirstOrDefault(p=>p.Id==state.active);}
            catch(Exception error){planChoice.IsEnabled=false;ToolTipService.SetToolTip(planChoice,error.Message);}finally{syncingPlans=false;}
        }
        planChoice.SelectionChanged+=async(_,_)=>
        {
            if(syncingPlans||planChoice.SelectedItem is not PowerPlan requested||upstream==null||settingsPowerBusy)return;
            settingsPowerBusy=true;planChoice.IsEnabled=false;await operationQueue.WaitAsync();
            try{await Task.Run(()=>{var plans=new WindowsPowerPlans();Guid before=plans.Active();if(!plans.List().Any(p=>p.Id==requested.Id))throw new IOException("目标方案不存在。");if(plans.Active()!=before)throw new IOException("当前方案已被外部修改，请刷新。");upstream.ExecuteFeature(11,new{scheme=requested.Id.ToString("D")});if(plans.Active()!=requested.Id)throw new IOException("电源方案回读不一致。");});await ReadPlans();}
            catch(Exception error){await ShowMessage("方案切换未完成",error.Message);await ReadPlans();}finally{settingsPowerBusy=false;planChoice.IsEnabled=true;operationQueue.Release();}
        };
        var resetPlans=Ui.Button("修复 / 重置计划",async(_,_)=>
        {
            if(upstream==null||!hardware.Active||settingsPowerBusy)return;
            if(!await Confirm("修复 Windows 电源计划？","先备份全部电源方案，再重建内置计划、尝试恢复本机 OEM PPM 调度。自建方案会检查保留；内置方案的定制参数和睡眠策略可能重置。不会自动重启。"))return;
            settingsPowerBusy=true;await operationQueue.WaitAsync();
            try{string result=await Task.Run(()=>PowerPlanRecovery.Restore(upstream));await ReadPlans();await ReadDesktopServices(true);await ShowMessage("电源计划恢复结果",result);}
            catch(Exception error){await ShowMessage("修复未完成",error.Message);}finally{settingsPowerBusy=false;operationQueue.Release();}
        });resetPlans.IsEnabled=hardware.Active&&upstream!=null;
        var refreshPlans=Ui.Button("刷新",async(_,_)=>await ReadPlans());
        foreach(var button in new[]{resetPlans,refreshPlans}){button.MinHeight=36;button.Height=36;button.VerticalContentAlignment=VerticalAlignment.Center;}
        var planControls=SettingsControls(resetPlans,planChoice,refreshPlans);planControls.Tag="power-plan-controls";
        Add("\uE945","Windows 基础电源方案","保留自建方案，可独立于硬件性能档位切换；修复前自动备份。",planControls,980);_=ReadPlans();
        Add("\uE9F9","导出诊断","可选择保存位置。默认诊断目录："+AppData.DiagnosticsPath,SettingsControls(Ui.Button("打开默认目录",(_,_)=>OpenLocalFolder(AppData.DiagnosticsPath)),Ui.Button("选择位置并导出",async(_,_)=>await Export())),980);
        Add("\uE713","后台与托盘","关闭或最小化面板后继续运行；点击托盘图标唤醒，右键菜单退出。",Ui.Button("退出程序",async(_,_)=>await ExitFromTray()));
        if(debugSettingsVisible&&!debugSettingsHidden)RenderDebugSettings(rows,Choice,Switch);
        page.Children.Add(ReplicaCard("settings","系统功能",rows));
        page.Children.Add(Ui.Text("Open-Revo 原生控制台 · 基于 faintonce/open-revo / openrevo-core · GPL-3.0-or-later",11,muted:true));
    }
    private static void OpenLocalFolder(string directory)
    {Directory.CreateDirectory(directory);var info=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};info.ArgumentList.Add(directory);Process.Start(info);}
    private async Task MigrateDataStorage()
    {
        if(settingsDataBusy||uiReviewMode)return;
        try
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);var folder=await picker.PickSingleFolderAsync();if(folder==null)return;
            string destination=Path.Combine(folder.Path,"OpenRevoNative");
            if(!await Confirm("迁移数据存储？","将完整复制并校验到："+destination+"。保留原目录；迁移并校验后退出程序，重新启动使用新目录。"))return;
            RememberPanelShape();
            await PauseForDataMaintenance(async()=>{if(upstream!=null)await Task.Run(upstream.StopDesktopServices);dataMigrationTarget=await Task.Run(()=>UserDataStore.Migrate(AppData.DirectoryPath,destination));desktopRuntimeClosed=true;dataStorageExitPending=true;});
            if(!closed){await ShowMessage("数据已复制并校验","原目录保持完整。关闭此提示后退出程序，请重新打开以使用新目录："+destination);exitRequested=true;Close();}
        }
        catch(Exception error){await InitializeDesktopServicesRuntime();await ShowMessage("迁移未完成",error.Message);}
    }
    private async Task ClearDataStorage()
    {
        if(settingsDataBusy||uiReviewMode)return;
        if(!await Confirm("清除本版用户数据？","将本版配置、方案和诊断目录移至相邻备份，恢复空目录后退出程序；不删除官方控制台数据，不取消Windows自启任务，也不自动重置硬件性能档位。下次启动使用默认设置。"))return;
        try
        {
            string backup="";
            await PauseForDataMaintenance(async()=>{if(upstream!=null)await Task.Run(upstream.StopDesktopServices);backup=await Task.Run(()=>UserDataStore.Reset(AppData.DirectoryPath));desktopRuntimeClosed=true;dataStorageExitPending=true;});
            await ShowMessage("数据已备份并清空","原数据保留在："+backup+"。关闭提示后退出程序。");exitRequested=true;Close();
        }
        catch(Exception error){await InitializeDesktopServicesRuntime();await ShowMessage("数据未清除",error.Message);}
    }
    private async Task PauseForDataMaintenance(Func<Task> action)
    {
        settingsDataBusy=true;navigation.IsEnabled=false;if(themeToggle!=null)themeToggle.IsEnabled=false;if(panelToggle!=null)panelToggle.IsEnabled=false;
        timer.Stop();powerTimer?.Stop();while(hardwareBusy||quickBusy||takeoverChanging||requestedWinLocked.HasValue||batteryGuardBusy||tuningApplying||displayBusy||brightnessApplying||gpuChanging||desktopServiceBusy||sleepOfficeBusy||desktopServicesReading||colorPending)await Task.Delay(40);await operationQueue.WaitAsync();
        try{UserDataStore.EnsureMarker();await action();}
        finally{operationQueue.Release();settingsDataBusy=false;if(!dataStorageExitPending){navigation.IsEnabled=true;if(themeToggle!=null)themeToggle.IsEnabled=true;if(panelToggle!=null)panelToggle.IsEnabled=true;UpdatePolling();if(pollingActive)timer.Start();powerTimer?.Start();}}
    }
    private void RenderDebugSettings(StackPanel rows,Func<string,string[],int,Func<int,Task>,double,ComboBox> choice,Func<string,bool,Func<bool,Task>,ToggleSwitch> toggle)
    {
        var levels=choice("调试日志等级",["关闭","仅错误","所有日志"],systemPreferences.LogLevel,async i=>{await SaveSystemSettings(systemPreferences with{LogLevel=i});await ConfigureDebugLog();},130);
        var filters=choice("日志模块过滤",["全部模块","外置水冷","自定义追踪","RGB 与推流","电源与风扇","底层 EC","设备外设","窗口与系统"],Math.Max(0,Array.IndexOf(new[]{"","cooler","custom","light","power","ec","device","ui"},systemPreferences.LogFilter)),async i=>{await SaveSystemSettings(systemPreferences with{LogFilter=new[]{"","cooler","custom","light","power","ec","device","ui"}[i]});await ConfigureDebugLog();},140);
        var logRow=DetailRow("\uE9D9","运行与异常调试日志","基础诊断与硬件审计自动保留；此处仅控制额外调试日志。",SettingsControls(levels,filters,Ui.Button("查看日志",(_,_)=>OpenLocalFolder(AppData.DiagnosticsPath)),Ui.Button("关闭隐藏",async(_,_)=>{await SaveSystemSettings(systemPreferences with{LogLevel=0});await ConfigureDebugLog();debugSettingsHidden=true;Render();})),1100);
        rows.Children.Add(DetailInset(logRow));
        var preview=new StackPanel{Spacing=8};
        void FillPreview()
        {
            preview.Children.Clear();bool gaming=developerShape!="thin_light";
            preview.Children.Add(Ui.Text(developerShape=="real"?"真实本机能力信息："+identity.Product:"布局演示："+(gaming?"标准全能游戏本":"星耀14轻薄本")+"；此预览不修改本机能力和硬件状态。",12,muted:true));
            preview.Children.Add(OverviewGrid(gaming?4:2,150,Ui.Text("处理器 · 已显示",12),Ui.Text(gaming?"独显插槽 · 已显示":"集成显示 · 已显示",12),Ui.Text(gaming?"水冷插槽 · 已显示":"键盘 · 单色背光",12),Ui.Text(gaming?"键盘 · 全功能":"独显 / 水冷 · 已裁切",12)));
        }
        var shape=choice("机型形态预览",["真实本机","星耀14轻薄本","游戏本形态"],Array.IndexOf(new[]{"real","thin_light","gaming"},developerShape),i=>{developerShape=new[]{"real","thin_light","gaming"}[i];FillPreview();return Task.CompletedTask;},180);
        FillPreview();rows.Children.Add(ReplicaCard("settings","机型形态与能力预览（开发者）",preview,shape));
    }
}
