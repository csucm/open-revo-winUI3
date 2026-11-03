using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly Dictionary<string,Button> desktopEffects=[];
    private readonly Dictionary<uint,Button> desktopColors=[];
    private ToggleSwitch? desktopLightToggle;
    private TextBlock? desktopLightState,desktopColorHex;
    private Border? desktopCustomSwatch;
    private readonly List<ToggleSwitch> unavailablePowerSwitches=[];
    private readonly List<Button> unavailableLightingTabs=[];
    private static Border DetailInset(UIElement content)
    {
        var border=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignSubtleFill}' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='1' CornerRadius='12' Padding='17'/>");
        border.Child=content;return border;
    }
    private FrameworkElement DesktopLightingTabs()
    {
        var tabs=new SuiteWrapPanel{Margin=new Thickness(0,0,0,2)};
        foreach(var(title,key)in new[]{("键盘背光","keyboard"),("前置灯条","front"),("转轴氛围灯","hinge"),("Logo 灯光","logo")})
        {
            var button=Ui.Button(title,(_,_)=>{selectedLightingZone=key;Render();});
            if(key=="keyboard")button.Content=SettingsControls(ApprovedIcons.Create("keyboard",24),Ui.Text(title,12));
            button.FontSize=12;button.Padding=new Thickness(12,9,12,9);button.MinHeight=0;
            // Tabs remain navigable even when the hardware editor is unavailable.
            lightingZoneTabs[key]=button;SetChoiceVisual(button,selectedLightingZone==key);tabs.Children.Add(button);
        }
        var undo=MiniControl("撤销灯光更改",()=>hardware.UndoKeyboardLighting(),"恢复上一次灯光设置");undo.FontSize=12;
        var toolbar=DesktopAlignedRow(Ui.Text(keyboard.Description,12,muted:true),undo);toolbar.Margin=new Thickness(0,0,0,14);
        return SuiteVisuals.Stack(14,tabs,toolbar);
    }
    private FrameworkElement DesktopLightingStudio()
    {
        bool unknown=keyboard.Kind is "None" or "Unknown";
        bool supported=keyboard.Kind=="SingleZone"&&hardware.Active&&lightingReadFresh;
        desktopLightState=Ui.Text(!lightingReadFresh?"背光状态待读取":keyboard.Description,12,muted:true);
        desktopLightToggle=ServiceToggle("键盘背光",lightingReadFresh&&rgbLevel>0,async(_,_)=>
        {
            if(syncingLighting)return;
            if(!desktopLightToggle!.IsOn){if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;rgbMode="关闭背光";}
            else {rgbLevel=lastLightingLevel;rgbMode="静态单色";}
            SyncDesktopLighting();await ScheduleLighting();
        });desktopLightToggle.IsEnabled=supported;SuiteVisuals.SwitchFamily(desktopLightToggle,"green");
        var heading=SettingsControls(ApprovedIcons.Create("keyboard",34),SuiteVisuals.Stack(4,Ui.Text(keyboard.Kind=="SingleZone"?"单区 RGB 键盘工作台":"键盘背光工作台",16,true),desktopLightState));heading.Spacing=10;
        var content=SuiteVisuals.Stack(16,DesktopAlignedRow(heading,desktopLightToggle));
        string[] effects=keyboard.Kind=="White"?[]:keyboard.Kind=="SingleZone"?["静态单色","彩虹循环"]:["静态单色","单色呼吸","彩虹循环"];
        var modes=new List<FrameworkElement>();
        foreach(string effect in effects)
        {
            string title=effect=="静态单色"?"单色常亮":effect;
            var button=Ui.Button(title,async(_,_)=>{rgbMode=effect;if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();});
            var labels=SuiteVisuals.Stack(3,Ui.Text(title,14),Ui.Text(effect=="彩虹循环"?"全键盘同色循环":"全键盘同色照明",12,muted:true));
            var row=SettingsControls(ApprovedIcons.Create(effect=="彩虹循环"?"lighting":"brightness",34),labels);row.Spacing=8;
            button.Content=row;button.Tag="detail-choice";button.Padding=new Thickness(14);button.MinHeight=0;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.IsEnabled=supported;desktopEffects[effect]=button;modes.Add(button);
        }
        if(effects.Length>0)
        {
            var grid=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=560,ColumnSpacing=10,RowSpacing=10};foreach(var item in modes)grid.Children.Add(item);content.Children.Add(grid);
        }
        lightingLevelBar=new Slider{Minimum=0,Maximum=4,StepFrequency=1,TickFrequency=1,SnapsTo=Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.Ticks,Value=rgbLevel,IsEnabled=supported};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(lightingLevelBar,"键盘背光亮度");
        lightingLevelLabel=Ui.Text("",12,muted:true);
        var titleRow=SettingsControls(ApprovedIcons.Create("brightness",24),Ui.Text("亮度等级",14));titleRow.Spacing=8;
        content.Children.Add(DetailInset(SuiteVisuals.Stack(8,DesktopAlignedRow(titleRow,lightingLevelLabel),BrightnessTrack(lightingLevelBar,stepped:true))));
        lightingLevelBar.ValueChanged+=async(_,_)=>{if(syncingLighting)return;rgbLevel=(int)lightingLevelBar.Value;if(rgbLevel==0)rgbMode="关闭背光";else{lastLightingLevel=rgbLevel;if(rgbMode=="关闭背光")rgbMode="静态单色";}SyncDesktopLighting();await ScheduleLighting();};
        if(keyboard.HasColor||unknown)
        {
            var swatches=new SuiteWrapPanel();
            foreach(var preset in colorPresets)
            {
                var dot=new Ellipse{Width=16,Height=16,Fill=new SolidColorBrush(LightingColor(preset.Hex)),Stroke=new SolidColorBrush(Windows.UI.Color.FromArgb(34,0,0,0)),StrokeThickness=1};
                var button=new Button{Content=dot,IsEnabled=supported,Width=32,Height=32,MinWidth=0,MinHeight=0,Padding=new Thickness(0),CornerRadius=new CornerRadius(16),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
                button.Click+=async(_,_)=>{customRgb=null;rgbColor=preset.Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();};
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,preset.Name);ToolTipService.SetToolTip(button,preset.Name);desktopColors[preset.Hex]=button;swatches.Children.Add(button);
            }
            swatches.ItemGap=7;
            var picker=lightingColorPicker=new ColorPicker{Color=LightingColor(SelectedLightingColor),IsAlphaEnabled=false,IsAlphaSliderVisible=false,IsAlphaTextInputVisible=false,IsHexInputVisible=true,IsColorChannelTextInputVisible=true,IsEnabled=supported,MaxWidth=420};
            picker.ColorChanged+=async(_,args)=>{uint next=(uint)(args.NewColor.R<<16|args.NewColor.G<<8|args.NewColor.B);if(syncingLighting||next==SelectedLightingColor)return;customRgb=next;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncDesktopLighting();await ScheduleLighting();};
            desktopCustomSwatch=new Border{Width=30,Height=22,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(LightingColor(SelectedLightingColor))};
            var custom=Ui.Button("自定义颜色",(_,_)=>{});custom.IsEnabled=supported;custom.Content=desktopCustomSwatch;custom.Padding=new Thickness(3);custom.Width=40;custom.Height=32;custom.MinHeight=0;custom.MinWidth=0;custom.CornerRadius=new CornerRadius(8);
            custom.Flyout=new Flyout{Content=picker,Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight};custom.Unloaded+=(_,_)=>custom.Flyout?.Hide();
            desktopColorHex=Ui.Text("",12,muted:true);ToolTipService.SetToolTip(custom,desktopColorHex);
            var label=SettingsControls(ApprovedIcons.Create("lighting",24),Ui.Text("定制主色调",14));label.Spacing=8;
            var customRow=SettingsControls(Ui.Text("自定义颜色",12),custom);customRow.Spacing=8;
            content.Children.Add(DetailInset(new LightingPaletteRow(label,swatches,customRow)));
        }
        var more=Ui.Button("更多设置",(_,_)=>{});more.Content=SettingsControls(Ui.Text("更多设置",14),ApprovedIcons.Utility("chevron",18));
        more.Flyout=new Flyout{Content=SuiteVisuals.Stack(8,Ui.Text("键盘背光",16,true),Ui.Text(keyboard.Description,12,muted:true),Ui.Text(lightingProbeError??(lightingReadFresh?"设置由硬件协议确认后生效。":"硬件状态尚未读取；控件保留并禁用。"),12,muted:true))};
        content.Children.Add(DesktopAlignedRow(Ui.Text(lightingReadFresh?"设备确认后更新状态。":"背光状态待读取。",12,muted:true),more));
        SyncDesktopLighting();return PanelAnchor("lighting",content);
    }
    private void SyncDesktopLighting()
    {
        if(mini)return;
        syncingLighting=true;
        try
        {
            if(desktopLightToggle!=null)desktopLightToggle.IsOn=lightingReadFresh&&rgbLevel>0;
            if(desktopLightState!=null)desktopLightState.Text=!lightingReadFresh?lightingProbeError??"背光状态待读取":keyboard.Description;
            if(desktopColorHex!=null)desktopColorHex.Text=$"#{SelectedLightingColor:X6}";
            if(lightingLevelBar!=null)lightingLevelBar.Value=rgbLevel;
            if(lightingLevelLabel!=null)lightingLevelLabel.Text=!lightingReadFresh?"待读取":$"{rgbLevel} / 4";
            if(lightingColorPicker!=null)lightingColorPicker.Color=LightingColor(SelectedLightingColor);
            if(desktopCustomSwatch?.Background is SolidColorBrush swatch&&swatch.Color!=LightingColor(SelectedLightingColor))swatch.Color=LightingColor(SelectedLightingColor);
            foreach(var(mode,button)in desktopEffects)SetChoiceVisual(button,mode==rgbMode);
            foreach(var(color,button)in desktopColors)SetChoiceVisual(button,color==SelectedLightingColor&&rgbMode=="静态单色");
        }
        finally{syncingLighting=false;}
    }
}
// The source palette is a wrapping flex row. Keep every swatch visible without
// imposing a minimum-width card on the page.
internal sealed class LightingPaletteRow:Panel
{
    private readonly FrameworkElement label,swatches,custom;
    private bool narrow;private double middle;
    internal LightingPaletteRow(FrameworkElement label,FrameworkElement swatches,FrameworkElement custom){this.label=label;this.swatches=swatches;this.custom=custom;Children.Add(label);Children.Add(swatches);Children.Add(custom);}
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double width=double.IsFinite(available.Width)?available.Width:900;
        label.Measure(new(width,double.PositiveInfinity));custom.Measure(new(width,double.PositiveInfinity));
        middle=Math.Max(0,width-label.DesiredSize.Width-custom.DesiredSize.Width-24);narrow=middle<305;
        swatches.Measure(new(narrow?width:middle,double.PositiveInfinity));
        return new(width,narrow?label.DesiredSize.Height+swatches.DesiredSize.Height+custom.DesiredSize.Height+24:Math.Max(swatches.DesiredSize.Height,Math.Max(label.DesiredSize.Height,custom.DesiredSize.Height)));
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        if(narrow){double y=0;foreach(var item in new[]{label,swatches,custom}){item.Arrange(new(0,y,final.Width,item.DesiredSize.Height));y+=item.DesiredSize.Height+12;}}
        else {label.Arrange(new(0,(final.Height-label.DesiredSize.Height)/2,label.DesiredSize.Width,label.DesiredSize.Height));swatches.Arrange(new(label.DesiredSize.Width+12,(final.Height-swatches.DesiredSize.Height)/2,middle,swatches.DesiredSize.Height));custom.Arrange(new(final.Width-custom.DesiredSize.Width,(final.Height-custom.DesiredSize.Height)/2,custom.DesiredSize.Width,custom.DesiredSize.Height));}return final;
    }
}
