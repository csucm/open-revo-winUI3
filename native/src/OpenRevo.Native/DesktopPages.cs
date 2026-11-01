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
        var guard=ServiceToggle("智能睡眠守护",false,async(_,_)=>{if(!syncingDesktopServices)await ChangePowerService("master");});desktopPowerSwitches["master"]=guard;
        var policies=GroupedColumns(
            PowerServiceRow("disconnect_net","\uE701","待机断开网络连接","减少待机联网唤醒",false),
            PowerServiceRow("block_away","blocked","阻止离开模式","避免应用在待机时持续运行",false),
            PowerServiceRow("ignore_sysreq","\uE72E","忽略阻止待机请求","管理应用发出的系统待机请求",false),
            PowerServiceRow("disable_wake_timers","\uE823","禁用 RTC 唤醒","限制定时任务自动唤醒",false),
            PowerServiceRow("deep_sleep","moon","ACPI 深度睡眠","管理硬件的深度休眠策略",false),
            PowerServiceRow("hibernate_30m","\uE823","30 分钟转休眠","使用电池时，待机30分钟后休眠",false));
        page.Children.Add(DetailSection("智能睡眠守护 / 背包防烫模式",policies,"仅切换系统实际支持的策略；不支持的单项保留原因。",guard));
        page.Children.Add(DetailSection("供电与性能",GroupedColumns(
            PowerServiceRow("usb","usb","关机 USB 供电","关机或休眠时为外部设备供电",false),
            PowerServiceRow("recovery","\uE945","来电自动开机","AC Recovery · 接入电源后启动",false),
            PowerServiceRow("link","\uE945","高性能电源模式","随性能档位联动 Windows 策略",false),
            PowerServiceRow("office","moon","睡眠时进入办公模式","唤醒后恢复之前的性能档位",false))));
        var choices=new List<FrameworkElement>();
        foreach(var(title,description,limit)in new[]{("长效模式","适合很长时间使用电池工作的环境。",100),("平衡模式","偶而会携带电脑工作，且不需要多余靠电池供电。",80),("工作站模式","如果很容易接续适配器工作，无携带电脑外出工作的需求，强烈建议使用此模式，延长电池的寿命。",60)})
        {
            var button=Ui.Button(title,async(_,_)=>{if(hardware.Active)await RunHardware(()=>hardware.SetBatteryPreset(limit),"充电上限已回读确认。");});
            var radio=Ui.Text("○",14);radio.VerticalAlignment=VerticalAlignment.Center;desktopBatteryChoices[limit]=radio;
            var labels=Ui.Stack(Ui.Text($"{title} ({limit}%)",14,true),Ui.Text(description,12,muted:true));labels.Spacing=3;
            var body=new Grid{ColumnSpacing=10};body.ColumnDefinitions.Add(new(){Width=GridLength.Auto});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});body.Children.Add(radio);Grid.SetColumn(labels,1);body.Children.Add(labels);
            button.Content=body;button.Tag="detail-choice";button.MinHeight=54;button.Padding=new Thickness(10,6,10,6);button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,$"{title}，充电上限 {limit}%");ToolTipService.SetToolTip(button,description);
            button.IsEnabled=hardware.Active&&!hardwareBusy&&hardware.SupportsBattery;compactChargeButtons[limit]=button;choices.Add(button);
        }
        var presets=Ui.Stack(choices.Cast<UIElement>().ToArray());presets.Spacing=6;
        var batteryLayout=new Grid{ColumnSpacing=14};batteryLayout.ColumnDefinitions.Add(new(){Width=new GridLength(1.3,GridUnitType.Star)});batteryLayout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        batteryLayout.Children.Add(presets);var gauge=BatteryGauge();Grid.SetColumn(gauge,1);batteryLayout.Children.Add(gauge);
        page.Children.Add(DetailSection("电池健康",batteryLayout,"主板硬件级充电控制 · 电池健康长寿模式",Value("miniLimit",12)));
        SyncDesktopServices();
    }
    private FrameworkElement DesktopGpuChoices()
    {
        gpuButtons.Clear();var controls=new List<FrameworkElement>();
        foreach(var(mode,title,icon,chip,description)in originalGpuModes)
        {
            var button=OriginalGpuCard(mode,title,icon,chip,description);
            button.IsEnabled=hardware.Active&&upstream!=null&&!gpuChanging&&gpuReadFresh&&gpuState?.Supports(mode)==true;
            gpuButtons[mode]=button;SetChoiceVisual(button,gpuState?.Mode==mode);controls.Add(button);
        }
        var grid=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=200,ColumnSpacing=8,RowSpacing=8};foreach(var item in controls)grid.Children.Add(item);return grid;
    }
    private void RenderDesktopDisplay()
    {
        gpuReadButton=Ui.Button("刷新状态",async(_,_)=>await RunGpuRequest(0));
        var status=Value("gpuTitle",13);
        page.Children.Add(DetailSection("显卡模式",Ui.Stack(status,DesktopGpuChoices()),"保存下次启动设置；切回本次开机的原模式可撤销，不会自动重启。",gpuReadButton));
        page.Children.Add(DesktopBrightnessCard());
        RenderDesktopColorServices();
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
        page.Children.Add(DesktopLightingTabs());
        if(selectedLightingZone=="keyboard")page.Children.Add(Ui.Card(DesktopLightingStudio()));
        else RenderDesktopZone();
        SyncZoneServices();
    }
}
