using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private ProgressBar? desktopCpuBar;
    private readonly Dictionary<int,TextBlock> desktopBatteryChoices=[];
    private void RenderDesktopOverview()
    {
        RenderReplicaOverview();
    }
    private void RenderDesktopPower()
    {
        if(currentPage=="sleep")
        {
            var guard=ServiceToggle("智能睡眠守护",false,async(_,_)=>{if(!syncingDesktopServices)await ChangePowerService("master");});desktopPowerSwitches["master"]=guard;
            page.Children.Add(DetailSection("守护总模式",SuiteSettingRow("智能睡眠守护","只应用系统实际支持的策略。",guard)));
            page.Children.Add(DetailGrid(DetailSection("网络与应用",SuiteVisuals.Stack(0,
                PowerServiceRow("disconnect_net","","待机断开网络连接","减少待机联网唤醒",false),PowerServiceRow("block_away","","阻止离开模式","避免应用在待机时持续运行",false),PowerServiceRow("ignore_sysreq","","忽略阻止待机请求","管理应用发出的系统待机请求",false))),
                DetailSection("休眠与唤醒",SuiteVisuals.Stack(0,PowerServiceRow("disable_wake_timers","","禁用 RTC 唤醒","限制定时任务自动唤醒",false),PowerServiceRow("deep_sleep","","ACPI 深度睡眠","管理硬件的深度休眠策略",false),PowerServiceRow("hibernate_30m","","30 分钟转休眠","使用电池时，待机30分钟后休眠",false)))));
            SyncDesktopServices();return;
        }
        var choices=new List<FrameworkElement>();
        foreach(var(title,description,limit)in new[]{("长效模式","经常使用电池",100),("平衡模式","兼顾便携与桌面",80),("工作站模式","长期连接适配器",60)})
        {
            var button=Ui.Button(title,async(_,_)=>{if(hardware.Active)await RunHardware(()=>hardware.ApplyBatteryCommand(limit),hardware.CoreDelegated?"Core 已发送充电上限命令。":"充电上限已回读确认；已有电量不会主动下降。");});
            var labels=SuiteVisuals.Stack(4,Ui.Text(title,14),Ui.Text(description,12,muted:true));button.Content=Ui.Row(labels,Ui.Text(limit+"%",14));button.Tag="detail-choice";button.Padding=new Thickness(12);button.MinHeight=0;button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,$"{title}，充电上限 {limit}%");ToolTipService.SetToolTip(button,description);
            button.IsEnabled=hardware.Active&&!hardwareBusy&&hardware.SupportsBattery;compactChargeButtons[limit]=button;choices.Add(button);
        }
        var presets=Ui.Stack(choices.Cast<UIElement>().ToArray());presets.Spacing=8;
        page.Children.Add(DetailSection("电池健康",new ApprovedBatteryLayout(presets,BatteryGauge())));
        page.Children.Add(DetailGrid(DetailSection("固件供电选项",SuiteVisuals.Stack(0,
            PowerServiceRow("usb","usb","关机 USB 供电","关机或休眠时为外部设备供电",false),
            PowerServiceRow("recovery","\uE945","来电自动开机","AC Recovery · 接入电源后启动",false),
            PowerServiceRow("link","\uE945","高性能电源模式","随性能档位联动 Windows 策略",false),
            PowerServiceRow("office","moon","睡眠时进入办公模式","唤醒后恢复之前的性能档位",false))),
            DetailSection("睡眠与系统行为",SuiteVisuals.Stack(0,SubpageLink("sleep","智能睡眠守护","网络、应用与休眠策略"),SubpageLink("settings","Windows 基础电源方案","选择或修复系统电源方案"),QuickActionButton("awake"),QuickActionButton("screen")))));
        SyncDesktopServices();
    }
    private FrameworkElement DesktopGpuChoices()
    {
        gpuButtons.Clear();var controls=new List<FrameworkElement>();
        foreach(var(mode,title,icon,chip,description)in originalGpuModes.OrderBy(m=>m.Mode))
        {
            var button=OriginalGpuCard(mode,title,icon,chip,description);
            button.IsEnabled=hardware.Active&&upstream!=null&&!gpuChanging&&gpuReadFresh&&gpuState?.Supports(mode)==true;
            gpuButtons[mode]=button;SetChoiceVisual(button,gpuState?.Mode==mode);controls.Add(button);
        }
        var grid=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=460,ColumnSpacing=10,RowSpacing=10};foreach(var item in controls)grid.Children.Add(item);return grid;
    }
    private void RenderDesktopDisplay()
    {
        gpuReadButton=Ui.Button("刷新状态",async(_,_)=>await RunGpuRequest(0));
        var status=Value("gpuTitle",13);
        page.Children.Add(DetailSection("显卡模式",Ui.Stack(DesktopGpuChoices(),Ui.Row(status,gpuReadButton)),"热切换按已确认的具体路径能力执行；其余保存启动目标并提示待重启。"));
        int first=page.Children.Count;RenderSuiteColorControls();var color=page.Children.Skip(first).ToArray();foreach(var item in color)page.Children.Remove(item);
        page.Children.Add(DetailGrid(DetailSection("内置显示器",SuiteDisplayOptions()),DetailSection("色彩与校准",SuiteVisuals.Stack(0,color))));
    }
    private FrameworkElement DesktopBrightnessCard()
    {
        var presets=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        foreach(var(label,value)in new[]{("25%",25),("60%",60),("100%",100)})
        {
            var button=Ui.Button(label,async(_,_)=>await SetCompactBrightness(value));button.Padding=new Thickness(10,6,10,6);brightnessButtons[value]=button;presets.Children.Add(button);
        }
        brightnessSlider=new Slider{Minimum=10,Maximum=100,StepFrequency=1,Value=screenBrightness??100,IsEnabled=displayState?.Active==true&&screenBrightness.HasValue,MinHeight=32,MinWidth=120};
        AttachBrightnessEvents(brightnessSlider);
        var title=Ui.Text($"内置屏亮度：{screenBrightness?.ToString()??"—"}%",13,true);live["brightnessDetailTitle"]=title;
        var leading=ReplicaLeading("sun",title,"内置显示器背光");
        var content=Ui.Stack(DesktopAlignedRow(leading,presets),BrightnessTrack(brightnessSlider));content.Spacing=8;
        var card=Ui.Card(content);card.Padding=new Thickness(16);return card;
    }
    private void RenderDesktopDevices()
    {
        if(DeveloperLayoutPreview){page.Children.Add(DeveloperLightingPreview());return;}
        if(selectedLightingZone=="keyboard")
        {
            page.Children.Add(DesktopLightingTabs());var studio=Ui.Card(DesktopLightingStudio());studio.Padding=new Thickness(22);page.Children.Add(studio);
        }
        else RenderDesktopZone();
        SyncZoneServices();
    }
}

