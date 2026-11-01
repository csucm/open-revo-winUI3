using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly Dictionary<string,Button> desktopEffects=[];
    private readonly Dictionary<uint,Button> desktopColors=[];
    private Button? desktopLightToggle;
    private TextBlock? desktopLightState,desktopColorHex;
    private Border? desktopCustomSwatch;
    private readonly List<ToggleSwitch> unavailablePowerSwitches=[];
    private readonly List<Button> unavailableLightingTabs=[];
    private static Border DetailInset(UIElement content)
    {
        var border=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignInsetFill}' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='1' CornerRadius='8' Padding='14'/>");
        border.Child=content;return border;
    }
    private FrameworkElement DesktopLightingTabs()
    {
        var tabs=new List<FrameworkElement>();
        foreach(var(title,key,icon)in new[]{("键盘背光","keyboard","keyboard"),("前置灯条","front","zap"),("转轴氛围灯","hinge","wind"),("Logo 灯光","logo","sparkles")})
        {
            var button=Ui.Button(title,(_,_)=>{selectedLightingZone=key;Render();});
            button.Content=OriginalIconLabel(icon,title);button.Padding=new Thickness(10,6,10,6);button.MinHeight=36;
            button.IsEnabled=key=="keyboard"||JsonBool(serviceLighting,key);
            if(!button.IsEnabled){unavailableLightingTabs.Add(button);ToolTipService.SetToolTip(button,JsonBool(serviceLighting,"capability_known")?"本机未检测到此灯区":"硬件能力尚未读取，需要管理员权限和可用驱动");}
            lightingZoneTabs[key]=button;SetChoiceVisual(button,selectedLightingZone==key);tabs.Add(button);
        }
        var grid=OverviewGrid(4,124,tabs.ToArray());grid.ColumnSpacing=4;grid.RowSpacing=4;
        var undo=MiniControl("撤销灯光更改",()=>hardware.UndoKeyboardLighting(),"恢复上一次灯光设置");undo.FontSize=12;undo.Padding=new Thickness(10,6,10,6);
        var bar=DetailInset(DesktopAlignedRow(grid,undo));bar.Padding=new Thickness(6);return bar;
    }
    private FrameworkElement DesktopLightingStudio()
    {
        if(keyboard.Kind is "None" or "Unknown")return Ui.Text(keyboard.Description,13,muted:true);
        bool supported=keyboard.Kind=="SingleZone"&&hardware.Active&&lightingReadFresh;
        desktopLightState=Ui.Text("",12);
        var toggleContent=OriginalIconLabel("power","");toggleContent.Children.RemoveAt(1);toggleContent.Children.Add(desktopLightState);
        desktopLightToggle=Ui.Button("键盘背光",async(_,_)=>
        {
            if(syncingLighting)return;
            if(rgbLevel>0){lastLightingLevel=rgbLevel;rgbLevel=0;rgbMode="关闭背光";}
            else {rgbLevel=lastLightingLevel;rgbMode="静态单色";}
            SyncDesktopLighting();await ScheduleLighting();
        });
        desktopLightToggle.Content=toggleContent;desktopLightToggle.Tag="detail-choice";desktopLightToggle.IsEnabled=supported;
        var heading=OriginalIconLabel("keyboard",keyboard.Kind=="SingleZone"?"单区 RGB 键盘工作台":"键盘背光工作台",14);
        var capability=Ui.Text(keyboard.Kind=="SingleZone"?"单区同色 RGB [EC硬件调光]":keyboard.Description,12,muted:true);
        var badge=DetailInset(capability);badge.Padding=new Thickness(6,2,6,2);
        var titleRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};titleRow.Children.Add(heading);titleRow.Children.Add(badge);
        var introduction=Ui.Stack(titleRow,Ui.Text(keyboard.Kind=="SingleZone"?"全键盘同色调光，支持纯色常亮、彩虹呼吸与4档亮度微调":keyboard.Description,12,muted:true));introduction.Spacing=4;
        var content=Ui.Stack(DesktopAlignedRow(introduction,desktopLightToggle));content.Spacing=16;
        string[] effects=keyboard.Kind=="White"?[]:keyboard.Kind=="SingleZone"?["静态单色","彩虹循环"]:["静态单色","单色呼吸","彩虹循环"];
        var modes=new List<FrameworkElement>();
        foreach(string effect in effects)
        {
            string title=effect=="静态单色"?"单色常亮":effect;
            var button=Ui.Button(title,async(_,_)=>
            {rgbMode=effect;if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();});
            var labels=Ui.Stack(Ui.Text(title,14,true),Ui.Text(effect=="彩虹循环"?"全键盘呼吸渐变循环":"全键盘同色静态照明",12,muted:true));labels.Spacing=4;
            button.Content=labels;button.Tag="detail-choice";button.Padding=new Thickness(12,8,12,8);button.MinHeight=54;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.IsEnabled=supported;desktopEffects[effect]=button;modes.Add(button);
        }
        lightingLevelBar=new Slider{Minimum=0,Maximum=4,StepFrequency=1,TickFrequency=1,SnapsTo=Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.Ticks,Value=rgbLevel,IsEnabled=supported};
        lightingLevelLabel=Ui.Text("",12,muted:true);
        var track=BrightnessTrack(lightingLevelBar,stepped:true);track.VerticalAlignment=VerticalAlignment.Center;
        var trackRow=new Grid{MinHeight=36};trackRow.Children.Add(track);
        var brightness=Ui.Stack(DesktopAlignedRow(OriginalIconLabel("sun","亮度等级",14),lightingLevelLabel),trackRow);brightness.Spacing=8;
        lightingLevelBar.ValueChanged+=async(_,_)=>{if(syncingLighting)return;rgbLevel=(int)lightingLevelBar.Value;if(rgbLevel==0)rgbMode="关闭背光";else{lastLightingLevel=rgbLevel;if(rgbMode=="关闭背光")rgbMode="静态单色";}SyncDesktopLighting();await ScheduleLighting();};
        if(effects.Length>0)
        {
            content.Children.Add(Ui.Text("原厂硬件独立灯效 (内置 MCU 固件算法 - 稳定节能):",12,muted:true));
            var modeGrid=OverviewGrid(effects.Length,200,modes.ToArray());modeGrid.ColumnSpacing=6;content.Children.Add(modeGrid);
        }
        content.Children.Add(DetailInset(brightness));
        if(keyboard.HasColor)
        {
            var palette=new List<FrameworkElement>();
            foreach(var preset in colorPresets)
            {
                var dot=new Ellipse{Width=16,Height=16,Fill=new SolidColorBrush(LightingColor(preset.Hex)),Stroke=(Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],StrokeThickness=1};
                var button=new Button{Content=dot,Tag="detail-choice",IsEnabled=supported,Width=30,Height=32,MinWidth=0,MinHeight=0,Padding=new Thickness(6),CornerRadius=new CornerRadius(16),HorizontalContentAlignment=HorizontalAlignment.Center};
                button.Click+=async(_,_)=>{customRgb=null;rgbColor=preset.Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();};
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,preset.Name);ToolTipService.SetToolTip(button,preset.Name);desktopColors[preset.Hex]=button;palette.Add(button);
            }
            var picker=lightingColorPicker=new ColorPicker{Color=LightingColor(SelectedLightingColor),IsAlphaEnabled=false,IsAlphaSliderVisible=false,IsAlphaTextInputVisible=false,IsHexInputVisible=true,IsColorChannelTextInputVisible=true,IsEnabled=supported,MaxWidth=420,HorizontalAlignment=HorizontalAlignment.Left};
            picker.ColorChanged+=async(_,args)=>{uint next=(uint)(args.NewColor.R<<16|args.NewColor.G<<8|args.NewColor.B);if(syncingLighting||next==SelectedLightingColor)return;customRgb=next;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();};
            desktopCustomSwatch=new Border{Width=18,Height=18,CornerRadius=new CornerRadius(4),Background=new SolidColorBrush(LightingColor(SelectedLightingColor))};
            var custom=Ui.Button("自定义颜色",(_,_)=>{});custom.IsEnabled=supported;custom.Padding=new Thickness(6);custom.MinWidth=32;custom.MinHeight=32;
            custom.Content=desktopCustomSwatch;ToolTipService.SetToolTip(custom,"自定义颜色");
            custom.Flyout=new Flyout{Content=picker,Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight};
            custom.Unloaded+=(_,_)=>custom.Flyout?.Hide();
            picker.Loaded+=(_,_)=>Design.Typography(picker);
            var left=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};left.Children.Add(OriginalIconLabel("palette","定制主色调："));foreach(var item in palette)left.Children.Add(item);
            desktopColorHex=Ui.Text("",12,muted:true);var right=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};right.Children.Add(custom);right.Children.Add(desktopColorHex);
            content.Children.Add(DetailInset(DesktopAlignedRow(left,right)));
        }
        SyncDesktopLighting();return content;
    }
    private void SyncDesktopLighting()
    {
        if(mini)return;
        syncingLighting=true;
        try
        {
            if(desktopLightToggle!=null)SetChoiceVisual(desktopLightToggle,lightingReadFresh&&rgbLevel>0);
            if(desktopLightState!=null)desktopLightState.Text=!lightingReadFresh?"待读取":rgbLevel>0?"已开启":"已关闭";
            if(desktopColorHex!=null)desktopColorHex.Text=$"#{SelectedLightingColor:X6}";
            if(lightingLevelBar!=null)lightingLevelBar.Value=rgbLevel;
            if(lightingLevelLabel!=null)lightingLevelLabel.Text=!lightingReadFresh?"待读取":rgbLevel==0?"关闭 (0)":$"{rgbLevel} 档";
            if(lightingColorPicker!=null)lightingColorPicker.Color=LightingColor(SelectedLightingColor);
            if(desktopCustomSwatch?.Background is SolidColorBrush swatch&&swatch.Color!=LightingColor(SelectedLightingColor))swatch.Color=LightingColor(SelectedLightingColor);
            foreach(var(mode,button)in desktopEffects)SetChoiceVisual(button,mode==rgbMode);
            foreach(var(color,button)in desktopColors)SetChoiceVisual(button,color==SelectedLightingColor&&rgbMode=="静态单色");
        }
        finally{syncingLighting=false;}
    }
}
