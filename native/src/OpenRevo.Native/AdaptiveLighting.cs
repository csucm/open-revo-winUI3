using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private CancellationTokenSource? lightingDebounce;
    private Task? lightingWork;
    private int lightingGeneration;
    private bool syncingLighting;
    private bool lightingReadFresh;
    private string? lightingProbeError;
    private int lastLightingLevel = 3;
    private uint? customRgb;
    private ComboBox? lightingModeBox;
    private ComboBox? lightingColorBox;
    private Slider? lightingLevelBar;
    private TextBlock? lightingLevelLabel;
    private ColorPicker? lightingColorPicker;
    private uint SelectedLightingColor => customRgb ?? colorPresets.FirstOrDefault(x=>x.Name==rgbColor).Hex;
    // A display classification only. Never replace the device's actual customRgb.
    private int LightingPresetIndex
    {
        get
        {
            if(!lightingReadFresh)return -1;
            uint actual=SelectedLightingColor; int best=0,bestDistance=int.MaxValue;
            for(int i=0;i<colorPresets.Length;i++)
            {
                uint preset=colorPresets[i].Hex;
                int r=(int)(actual>>16)-(int)(preset>>16),g=(int)((actual>>8)&255)-(int)((preset>>8)&255),b=(int)(actual&255)-(int)(preset&255);
                int distance=r*r+g*g+b*b;
                if(distance<bestDistance){best=i;bestDistance=distance;}
            }
            return best;
        }
    }
    private static Windows.UI.Color LightingColor(uint hex) => Windows.UI.Color.FromArgb(255,(byte)(hex>>16),(byte)(hex>>8),(byte)hex);
    private void SyncLightingEditors()
    {
        syncingLighting=true;
        try
        {
            if(lightingModeBox!=null) lightingModeBox.SelectedItem=lightingReadFresh?rgbMode:null;
            if(lightingColorBox!=null)lightingColorBox.SelectedIndex=LightingPresetIndex;
            if(lightingLevelBar!=null) lightingLevelBar.Value=rgbLevel;
            if(lightingLevelLabel!=null) lightingLevelLabel.Text=!lightingReadFresh?"待读取":rgbLevel==0?"关闭":$"{rgbLevel} / 4";
            Put("lightingTitle",MiniLightingTitle);
            if(lightingColorPicker!=null) lightingColorPicker.Color=LightingColor(SelectedLightingColor);
        }
        finally { syncingLighting=false; }
    }
    private static readonly (string Name, uint Hex)[] colorPresets =
    [ ("冰晶蓝",0x29b6f2),("机械青",0x12c7bf),("电竞紫",0x9662ed),("极光绿",0x13af7d),
      ("烈焰红",0xf5436a),("暖阳金",0xffc83d),("幻影粉",0xed4ba0),("纯净白",0xe7ecf2) ];
    private FrameworkElement AdaptiveLightingControls()
    {
        bool unknown = !hardware.SupportsDeclaredRgb && keyboard.Kind is ("None" or "Unknown");
        bool supported = hardware.Active && (hardware.SupportsDeclaredRgb || keyboard.Kind == "SingleZone" && lightingReadFresh);
        string[] effects = keyboard.Kind switch
        {
            "White" => ["开启照明","关闭背光"],
            "PerKey" => ["炫彩波浪","静态单色","单色呼吸","彩虹循环","关闭背光"],
            "4Zone" => ["静态单色","单色呼吸","彩虹循环","关闭背光"],
            _ => ["彩虹循环","静态单色","关闭背光"]
        };
        var mode = new ComboBox { ItemsSource=effects, SelectedItem=lightingReadFresh?(effects.Contains(rgbMode)?rgbMode:effects[0]):null,PlaceholderText="状态待读取", FontFamily=new FontFamily("Segoe UI"),FontSize=12, MinWidth=100, IsEnabled=supported };
        mode.ItemContainerStyle=new Style(typeof(ComboBoxItem));
        mode.ItemContainerStyle.Setters.Add(new Setter(Control.FontSizeProperty,12d));
        lightingModeBox=mode;
        mode.SelectionChanged += async (_,_)=> {if(!syncingLighting && mode.SelectedItem is string value){rgbMode=value;if(value=="关闭背光"){if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;}else if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncLightingEditors();await ScheduleLighting();}};
        ToolTipService.SetToolTip(mode,unknown?"背光参数未识别，控件保留并禁用":supported?"选择灯效，立即生效":"已识别背光类型，此机型调节尚未验证");
        var level = lightingLevelBar = new Slider { Minimum=0,Maximum=4,StepFrequency=1,TickFrequency=1,SnapsTo=Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.Ticks,Value=rgbLevel,IsEnabled=supported&&!hardware.SupportsDeclaredRgb,MinHeight=25 };
        lightingLevelLabel=Ui.Text(!lightingReadFresh?"待读取":rgbLevel==0?"关闭":$"{rgbLevel} / 4",11,muted:true);
        level.ValueChanged += async (_,_)=>{if(syncingLighting)return;rgbLevel=(int)level.Value;if(rgbLevel==0)rgbMode="关闭背光";else{lastLightingLevel=rgbLevel;if(rgbMode=="关闭背光")rgbMode="静态单色";}SyncLightingEditors();await ScheduleLighting();};
        ToolTipService.SetToolTip(level,"拖动切换背光亮度，最左侧关闭");
        var more=Ui.Button("更多",(_,_)=>{currentPage="devices";if(mini)ToggleMini();else Render();});
        var moreContent=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,HorizontalAlignment=HorizontalAlignment.Center};
        moreContent.Children.Add(Ui.Icon("",14));moreContent.Children.Add(Ui.Text("更多",12));more.Content=moreContent;
        more.IsEnabled=!unknown;
        ToolTipService.SetToolTip(more,"打开背光详细设置");
        ComboBox Colors(string name)
        {
            var box = new ComboBox {MinWidth=100,IsEnabled=supported,FontSize=12,PlaceholderText="当前颜色"};
            foreach(var preset in colorPresets)
            {
                var swatch = new StackPanel {Orientation=Orientation.Horizontal,Spacing=6};
                swatch.Children.Add(new Ellipse {Width=10,Height=10,Fill=new SolidColorBrush(Windows.UI.Color.FromArgb(255,(byte)(preset.Hex>>16),(byte)(preset.Hex>>8),(byte)preset.Hex))});
                swatch.Children.Add(Ui.Text(preset.Name,12));
                box.Items.Add(new ComboBoxItem {Content=swatch,Tag=preset.Name,FontSize=12});
            }
            lightingColorBox=box;
            box.SelectedIndex=LightingPresetIndex;
            box.SelectionChanged+=async (_,_)=>{if(!syncingLighting && box.SelectedItem is ComboBoxItem item){customRgb=null;rgbColor=(string)item.Tag;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncLightingEditors();await ScheduleLighting();}};
            ToolTipService.SetToolTip(box,name+"，选择后立即生效"); return box;
        }
        var controls = new StackPanel { Spacing=mini?2:12 };
        if(mini)controls.Children.Add((keyboard.HasColor||hardware.SupportsDeclaredRgb||unknown)?Columns(mode,Colors("背光颜色"),more):Columns(mode,more));
        else
        {
            mode.FontSize=13;mode.MinWidth=180;
            controls.Children.Add(DetailRow("\uE765","灯光效果","选择后立即生效。",mode));
            if(keyboard.HasColor||hardware.SupportsDeclaredRgb){var colors=Colors("背光颜色");colors.FontSize=13;colors.MinWidth=180;controls.Children.Add(DetailRow("\uE790","预设颜色","使用静态单色灯效。",colors));}
        }
        if(mini)
        {
            controls.Children.Add(BrightnessTrack(level,stepped:true));
        }
        else {var levelControls=Ui.Stack(level,lightingLevelLabel);levelControls.Width=220;levelControls.Spacing=2;controls.Children.Add(DetailRow("\uE706","背光亮度","0–4 档，最左侧关闭。",levelControls));}
        if(!mini && (keyboard.HasColor||hardware.SupportsDeclaredRgb))
        {
            var picker=lightingColorPicker=new ColorPicker { Color=LightingColor(SelectedLightingColor), IsAlphaEnabled=false,IsAlphaSliderVisible=false,IsAlphaTextInputVisible=false,IsHexInputVisible=true,IsColorChannelTextInputVisible=true,IsEnabled=supported };
            picker.ColorChanged+=async (_,args)=>{uint next=(uint)(args.NewColor.R<<16|args.NewColor.G<<8|args.NewColor.B);if(syncingLighting||next==SelectedLightingColor)return;customRgb=next;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;SyncLightingEditors();await ScheduleLighting();};
            controls.Children.Add(new Expander{Header="自定义颜色",Content=picker,IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch});
        }
        return controls;
    }
    private Task ScheduleLighting()
    {
        if(!hardware.Active || (!hardware.SupportsDeclaredRgb&&(keyboard.Kind!="SingleZone" ||!lightingReadFresh))|| closed||exitRequested||desktopRuntimeClosed)return Task.CompletedTask;
        ++lightingGeneration;
        if(lightingWork is {IsCompleted:false})return lightingWork;
        lightingDebounce?.Dispose();lightingDebounce=new();
        return lightingWork=ApplyRequestedLighting(lightingDebounce.Token);
    }
    private async Task ApplyRequestedLighting(CancellationToken token)
    {
        try
        {
            while(!closed&&!exitRequested&&!desktopRuntimeClosed&&!token.IsCancellationRequested&&hardware.Active&&(lightingReadFresh||hardware.SupportsDeclaredRgb))
            {
                int generation=lightingGeneration;
                uint color=SelectedLightingColor;
                int level=rgbMode=="关闭背光"?0:rgbLevel;bool rainbow=rgbMode=="彩虹循环";
                bool attempted=false,applied=false;
                await RunHardware(()=>
                {
                    if(generation!=Volatile.Read(ref lightingGeneration)||token.IsCancellationRequested||closed||exitRequested||desktopRuntimeClosed)return new{Skipped=true};
                    attempted=true;
                    var receipt=hardwareRuntime.Desktop.SetKeyboardRgb(color,level,rainbow);
                    applied=true;return receipt;
                },hardware.SupportsDeclaredRgb?"Core 已发送 RGB 背光命令。":"背光已更新。");
                if(attempted&&!applied)
                {
                    lightingReadFresh=false;lightingProbeError=hardwareMessage;SyncLightingEditors();
                    foreach(var control in new Control?[]{lightingModeBox,lightingColorBox,lightingLevelBar,lightingColorPicker})if(control!=null)control.IsEnabled=false;
                    return;
                }
                if(generation==lightingGeneration)
                {
                    if(applied&&!hardware.SupportsDeclaredRgb)ConfirmedActionOsd(level==0?"关闭背光":rainbow?"彩色循环":rgbMode,"lighting");
                    return;
                }
                // Leading update is immediate. While busy, coalesce into the latest
                // request; a moving pointer must not keep postponing all writes.
                await Task.Delay(75,token);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
    }
}


