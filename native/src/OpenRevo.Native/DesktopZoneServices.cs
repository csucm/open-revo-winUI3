using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private string selectedLightingZone="keyboard";
    private readonly Dictionary<string,Button> lightingZoneTabs=[];
    private Border? zoneEditor;
    private ToggleSwitch? zoneEnabled;
    private ComboBox? zoneEffect;
    private Slider? zoneBrightness,zoneSpeed;
    private ColorPicker? zoneColor;
    private TextBlock? zoneStatus;
    private bool syncingZone;
    private CancellationTokenSource? zoneDebounce;
    private (string Name,int Id)[] zoneEffects=[];
    private TextBlock? zoneBrightnessValue,zoneSpeedValue;
    private void RenderDesktopZone()
    {
        zoneEffects=selectedLightingZone=="logo"&&JsonBool(serviceLighting,"ec_logo")?[("单色常亮",1),("能量呼吸",2),("彩虹呼吸",20)]:[("单色常亮",1),("能量呼吸",2),("彩虹呼吸",20),("全彩波浪",3),("动态对撞",4),("极速流星",6),("跑马流水",9),("璀璨闪烁",10)];
        if(selectedLightingZone=="logo")zoneEffects=zoneEffects.Where(e=>e.Id!=10).ToArray();
        var keys=new[]{"front","hinge","logo"};
        var selection=new ComboBox{ItemsSource=new[]{"前置灯条","转轴氛围灯","Logo 灯光"},SelectedIndex=Array.IndexOf(keys,selectedLightingZone),MinWidth=170};
        selection.SelectionChanged+=(_,_)=>{if(selection.SelectedIndex>=0){selectedLightingZone=keys[selection.SelectedIndex];Render();}};
        zoneEnabled=ServiceToggle("灯区电源",false,async(_,_)=>{if(!syncingZone)await ScheduleZone();});
        zoneEffect=new ComboBox{ItemsSource=zoneEffects.Select(e=>e.Name).ToArray(),SelectedIndex=-1,PlaceholderText="待读取",MinWidth=170};
        zoneBrightness=new Slider{Minimum=0,Maximum=4,StepFrequency=1};
        zoneSpeed=new Slider{Minimum=0,Maximum=4,StepFrequency=1};
        zoneColor=new ColorPicker{IsAlphaEnabled=false,IsAlphaSliderVisible=false,IsAlphaTextInputVisible=false,IsHexInputVisible=true,IsColorChannelTextInputVisible=true,MaxWidth=420};
        var colorSwatch=new Microsoft.UI.Xaml.Shapes.Rectangle{Width=30,Height=22,Fill=new Microsoft.UI.Xaml.Media.SolidColorBrush(zoneColor.Color)};
        var color=Ui.Button("自定义颜色",(_,_)=>{});color.Content=colorSwatch;color.Padding=new Thickness(3);color.Width=40;color.Height=32;color.MinHeight=0;color.MinWidth=0;color.CornerRadius=new CornerRadius(8);color.Flyout=new Flyout{Content=zoneColor};
        zoneColor.ColorChanged+=(_,_)=>{colorSwatch.Fill=new Microsoft.UI.Xaml.Media.SolidColorBrush(zoneColor.Color);color.IsEnabled=zoneColor.IsEnabled;};
        color.Loaded+=(_,_)=>color.IsEnabled=zoneColor.IsEnabled;
        zoneBrightnessValue=Ui.Text("待读取",14);zoneSpeedValue=Ui.Text("待读取",14);
        FrameworkElement Range(string title,string desc,Slider slider,TextBlock value)=>SuiteVisuals.Rule(SuiteVisuals.Stack(8,DesktopAlignedRow(SuiteVisuals.Stack(4,Ui.Text(title,14),Ui.Text(desc,12,muted:true)),SettingsControls(value,Ui.Text("档",12,muted:true))),slider,Ui.Row(Ui.Text("0 档",12,muted:true),Ui.Text("4 档",12,muted:true))));
        var left=DetailSection("灯区与效果",SuiteVisuals.Stack(0,SuiteSettingRow("当前灯区","能力由实际设备决定。",selection),SuiteSettingRow("灯区电源","单独控制此灯区，不改变键盘背光。",zoneEnabled),SuiteSettingRow("灯光效果","按实际控制器能力过滤列表。",zoneEffect)));
        var right=DetailSection("亮度与速度",SuiteVisuals.Stack(0,Range("灯区亮度","",zoneBrightness,zoneBrightnessValue),Range("灯效速度","固定速度控制器保留轨道并禁用。",zoneSpeed,zoneSpeedValue),SuiteSettingRow("自定义颜色","HEX / RGB · 不包含透明通道。",color)));
        page.Children.Add(DetailGrid(left,right));
        zoneStatus=Ui.Text("读取灯区状态",12,muted:true);
        zoneEditor=DetailSection("灯区状态",zoneStatus);page.Children.Add(zoneEditor);
        SyncZoneServices();
        zoneEffect.SelectionChanged+=async(_,_)=>{if(!syncingZone)await ScheduleZone();};
        zoneBrightness.ValueChanged+=async(_,_)=>{zoneBrightnessValue.Text=((int)zoneBrightness.Value).ToString();if(!syncingZone)await ScheduleZone();};
        zoneSpeed.ValueChanged+=async(_,_)=>{zoneSpeedValue.Text=((int)zoneSpeed.Value).ToString();if(!syncingZone)await ScheduleZone();};
        zoneColor.ColorChanged+=async(_,_)=>{if(!syncingZone)await ScheduleZone();};
    }
    private void SyncZoneServices()
    {
        RefreshPaintedState();
        foreach(var(key,button)in lightingZoneTabs)
        {
            bool supported=key=="keyboard"||JsonBool(serviceLighting,key);
            button.IsEnabled=!zoneServicesBusy;
            ToolTipService.SetToolTip(button,supported?"打开此灯区":JsonBool(serviceLighting,"capability_known")?"本机未检测到此灯区":"硬件能力尚未读取，需要管理员权限和可用驱动");
        }
        if(zoneEditor==null||zoneEnabled==null||zoneEffect==null||zoneBrightness==null||zoneSpeed==null||zoneColor==null)return;
        syncingZone=true;
        try
        {
            bool available=JsonBool(serviceLighting,selectedLightingZone)&&hardware.Active;
            foreach(var control in new Control?[]{zoneEnabled,zoneEffect,zoneBrightness,zoneSpeed,zoneColor})if(control!=null)control.IsEnabled=available&&!zoneServicesBusy;
            if(zoneServicesBusy)return;
            if(serviceLighting.ValueKind!=JsonValueKind.Object)return;
            var saved=serviceLighting.GetProperty("saved");string prefix=selectedLightingZone switch{"front"=>"lb","hinge"=>"hinge",_=>"logo"};
            zoneEnabled.IsOn=saved.GetProperty(prefix+"_enabled").GetBoolean();
            int effect=saved.GetProperty(prefix+"_effect").GetInt32();zoneEffect.SelectedIndex=Array.FindIndex(zoneEffects,p=>p.Id==effect);
            zoneBrightness.Value=saved.GetProperty(prefix+"_brightness").GetInt32();
            zoneSpeed.IsEnabled=available&&!zoneServicesBusy&&selectedLightingZone!="logo"&&!(selectedLightingZone=="front"&&JsonBool(serviceLighting,"ec_front"));
            ToolTipService.SetToolTip(zoneSpeed,zoneSpeed.IsEnabled?"调节硬件灯效速度":"此控制器使用固定速度");
            zoneSpeed.Value=saved.TryGetProperty(prefix+"_speed",out var speed)?speed.GetInt32():2;
            if(zoneBrightnessValue!=null)zoneBrightnessValue.Text=available?((int)zoneBrightness.Value).ToString():"待读取";
            if(zoneSpeedValue!=null)zoneSpeedValue.Text=available?((int)zoneSpeed.Value).ToString():"待读取";
            string color=saved.GetProperty(prefix+"_color").GetString()??"#00FFFF";
            if(uint.TryParse(color.TrimStart('#'),System.Globalization.NumberStyles.HexNumber,null,out uint rgb))zoneColor.Color=LightingColor(rgb);
            if(zoneStatus!=null)zoneStatus.Text=available?"显示上次 Core 已确认的指令；此灯区不提供完整硬件状态回读。":"本机未检测到此灯区，或尚未连接设备。";
        }
        finally{syncingZone=false;}
    }
    private async Task ScheduleZone()
    {
        if(upstream==null||zoneEnabled==null||zoneEffect==null||zoneBrightness==null||zoneSpeed==null||zoneColor==null||zoneEffect.SelectedIndex<0)return;
        zoneDebounce?.Cancel();var debounce=zoneDebounce=new CancellationTokenSource();
        var color=zoneColor.Color;
        var request=new{zone=selectedLightingZone,enabled=zoneEnabled.IsOn,effect=zoneEffects[zoneEffect.SelectedIndex].Id,brightness=(int)zoneBrightness.Value,speed=(int)zoneSpeed.Value,color=$"#{color.R:X2}{color.G:X2}{color.B:X2}"};
        try
        {
            await Task.Delay(250,debounce.Token);
            while(zoneServicesBusy)await Task.Delay(60,debounce.Token);
            if(!closed)await DesktopServiceAction("lighting-"+request.zone,()=>upstream.ExecuteFeature(6,request),true);
        }
        catch(OperationCanceledException){}
    }
}
