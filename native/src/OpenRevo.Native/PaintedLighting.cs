using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenRevo.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool PaintKeyboardEnabled=>(hardware.SupportsDeclaredRgb||keyboard.Kind=="SingleZone"&&lightingReadFresh)&&hardware.Active&&!hardwareBusy;
    private async Task PaintKeyboardChange(Action change)
    {if(uiReviewMode||!PaintKeyboardEnabled)return;change();RefreshPaintedState();await ScheduleLighting();RefreshPaintedState();}
    private FrameworkElement PaintMiniLighting(bool readOnly=false)
    {
        if(keyboard.Kind=="White"&&hardware.CoreDelegated)return PaintMonochrome(true,readOnly);
        string[] effects=["静态单色","彩虹循环","关闭背光"];
        var effect=new PaintSelect("背光模式",effects,lightingReadFresh?Array.IndexOf(effects,rgbMode):-1,readOnly?null:async i=>await PaintKeyboardChange(()=>{rgbMode=effects[i];if(i==2){if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;}else if(rgbLevel==0)rgbLevel=lastLightingLevel;})){MinWidth=0,MinHeight=34,Padding=new(6,3,6,3)};
        var color=new PaintSelect("背光颜色",colorPresets.Select(p=>p.Name).ToArray(),LightingPresetIndex,readOnly?null:async i=>await PaintKeyboardChange(()=>{customRgb=null;rgbColor=colorPresets[i].Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;})){MinWidth=0,Padding=new(6,3,6,3)};
        var more=new PaintButton("更多",readOnly?null:()=>{selectedLightingZone="keyboard";currentPage="devices";ToggleMini();}){Content=Paint.Inline(5,Paint.Icon("lighting",18),Paint.Text("更多",12)),Padding=new(6,3,6,3)};
        var slider=new PaintSlider("键盘背光亮度",0,4,rgbLevel,1,readOnly?null:async v=>await PaintKeyboardChange(()=>{rgbLevel=(int)v;if(rgbLevel==0)rgbMode="关闭背光";else{lastLightingLevel=rgbLevel;if(rgbMode=="关闭背光")rgbMode="静态单色";}}));
        paintBindings.Add(()=>{effect.IsEnabled=color.IsEnabled=PaintKeyboardEnabled;slider.IsEnabled=PaintKeyboardEnabled&&!hardware.SupportsDeclaredRgb;effect.SelectedIndex=lightingReadFresh?Array.IndexOf(effects,rgbMode):-1;color.UnselectedText="状态未知";color.SelectedIndex=LightingPresetIndex;slider.Value=rgbLevel;});
        return Paint.Card(Paint.Stack(4,Paint.Between(Paint.Inline(6,Paint.Icon("keyboard",24),PT(()=>MiniLightingTitle,12,true)),PT(()=>keyboard.Description,12,muted:true),7),PaintColumns([effect,color,more]),slider),7,12);
    }
    private void RenderPaintedLighting()
    {
        if(keyboard.Kind=="White"&&hardware.CoreDelegated){page.Children.Add(PaintMonochrome(false));return;}
        var tabs=new SuiteWrapPanel{ItemGap=5};foreach(var(title,key)in new[]{("键盘背光","keyboard"),("前置灯条","front"),("转轴氛围灯","hinge"),("Logo 灯光","logo")}){var tab=new PaintButton(title,()=>{selectedLightingZone=key;Render();}){Selected=key==selectedLightingZone,Padding=new(12,9,12,9)};if(key=="keyboard")tab.Content=Paint.Inline(8,Paint.Icon("keyboard",24),Paint.Text(title,12));else tab.Content=Paint.Text(title,12);tabs.Children.Add(tab);}page.Children.Add(tabs);
        var undo=PB("撤销灯光更改",async()=>{await RunHardware(()=>hardware.UndoKeyboardLighting(),"已撤销上次灯光更改。");await LoadPanelHardware();RefreshPaintedState();},true);paintBindings.Add(()=>undo.IsEnabled=hardware.Active&&!hardwareBusy);page.Children.Add(Paint.Between(PT(()=>keyboard.Description,12,muted:true),undo));
        var power=PS("键盘背光",()=>lightingReadFresh?rgbLevel>0:null,on=>PaintKeyboardChange(()=>{if(on){rgbLevel=lastLightingLevel;rgbMode="静态单色";}else{if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;rgbMode="关闭背光";}}),()=>PaintKeyboardEnabled,"green",true);
        var header=Paint.Between(Paint.Inline(10,Paint.Icon("keyboard"),Paint.Stack(4,PT(()=>keyboard.Kind=="SingleZone"?"单区 RGB 键盘工作台":"键盘背光工作台",16,true),PT(()=>lightingReadFresh?keyboard.Description:lightingProbeError??"背光状态待读取",12,muted:true))),power);
        var effects=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=560,ColumnSpacing=10,RowSpacing=10};
        foreach(var(mode,title,description,icon)in new[]{("静态单色","单色常亮","全键盘同色照明","brightness"),("彩虹循环","彩虹循环","全键盘同色循环","lighting")})
        {var button=PB(title,()=>PaintKeyboardChange(()=>{rgbMode=mode;if(rgbLevel==0)rgbLevel=lastLightingLevel;}),true);button.Content=Paint.Inline(8,Paint.Icon(icon),Paint.Stack(3,Paint.Text(title),Paint.Text(description,12,muted:true)));button.Padding=new(14);button.StretchContent();button.HorizontalAlignment=HorizontalAlignment.Stretch;paintBindings.Add(()=>{button.IsEnabled=PaintKeyboardEnabled;button.Selected=lightingReadFresh&&rgbMode==mode;});effects.Children.Add(button);}
        var brightness=new PaintSlider("键盘背光亮度",0,4,rgbLevel,1,async v=>await PaintKeyboardChange(()=>{rgbLevel=(int)v;if(rgbLevel==0)rgbMode="关闭背光";else{lastLightingLevel=rgbLevel;if(rgbMode=="关闭背光")rgbMode="静态单色";}}));paintBindings.Add(()=>{brightness.IsEnabled=PaintKeyboardEnabled&&!hardware.SupportsDeclaredRgb;brightness.Value=rgbLevel;});
        var level=Paint.Card(Paint.Stack(8,Paint.Between(Paint.Inline(8,Paint.Icon("brightness",24),Paint.Text("亮度等级")),PT(()=>lightingReadFresh?rgbLevel+" / 4":"待读取",12,muted:true)),brightness),17,12);level.Loaded+=(_,_)=>level.Background=Paint.Brush(Paint.Subtle(level));
        var swatches=new SuiteWrapPanel{ItemGap=7};foreach(var preset in colorPresets){var button=PB(preset.Name,()=>PaintKeyboardChange(()=>{customRgb=null;rgbColor=preset.Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;}),true);button.Content=new Ellipse{Width=16,Height=16,Fill=new SolidColorBrush(LightingColor(preset.Hex)),Stroke=Paint.Brush("22000000"),StrokeThickness=1};button.Width=button.Height=32;button.MinHeight=0;button.Padding=new(0);button.CornerRadius=new(16);paintBindings.Add(()=>{button.IsEnabled=PaintKeyboardEnabled;button.Selected=lightingReadFresh&&rgbMode=="静态单色"&&colorPresets[LightingPresetIndex].Hex==preset.Hex;});swatches.Children.Add(button);}
        var palette=Paint.Card(new LightingPaletteRow(Paint.Inline(8,Paint.Icon("lighting",24),Paint.Text("定制主色调")),swatches,Paint.Inline(8,Paint.Text("自定义颜色",12),PaintColorEntry(true))),17,12);palette.Loaded+=(_,_)=>palette.Background=Paint.Brush(Paint.Subtle(palette));
        var more=new PaintButton("更多设置",async()=>await ShowMessage("键盘背光",keyboard.Description+"。"+(lightingProbeError??"设备确认后更新状态。"))){Content=Paint.Inline(8,Paint.Text("更多设置"),Paint.Utility("chevron"))};
        page.Children.Add(Paint.Card(Paint.Stack(16,header,effects,level,palette,Paint.Between(PT(()=>hardware.SupportsDeclaredRgb?"可发送灯效和颜色命令 · 未回读确认":lightingReadFresh?"设置由设备确认。":"背光状态待读取。",12,muted:true),more)),22));
    }
    private PaintButton PaintColorEntry(bool keyboardColor)
    {
        uint? Read()=>keyboardColor?lightingReadFresh?SelectedLightingColor:null:PaintZoneColor();
        var swatch=new Border{Width=30,Height=22,CornerRadius=new(3)};
        var button=new PaintButton("自定义颜色",async()=>
        {
            uint current=Read()??0;var controls=Paint.Stack(12);var hex=new PaintField("HEX 颜色",current.ToString("X6")){Hex=true,Width=120};
            async Task Set(uint rgb){if(uiReviewMode)return;if(keyboardColor)await PaintKeyboardChange(()=>{customRgb=rgb;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;});else await PaintSetZone("color",JsonValue.Create("#"+rgb.ToString("X6"))!);}
            hex.Committed+=async text=>{if(uint.TryParse(text.TrimStart('#'),System.Globalization.NumberStyles.HexNumber,null,out uint rgb)&&rgb<=0xFFFFFF){current=rgb;await Set(rgb);}else await ShowMessage("颜色无效","请填写6位HEX色值。");};controls.Children.Add(Paint.Between(Paint.Text("HEX"),hex));
            foreach(var(name,shift)in new[]{("红色 R",16),("绿色 G",8),("蓝色 B",0)}){int offset=shift;var slider=new PaintSlider(name,0,255,(current>>offset)&255,1,async value=>{current=(current&~(255u<<offset))|((uint)value<<offset);hex.Value=current.ToString("X6");await Set(current);});controls.Children.Add(Paint.Stack(6,Paint.Text(name,12),slider));}
            await PaintDialog("自定义颜色",controls);
        }){Content=swatch,Width=40,Height=32,MinHeight=0,Padding=new(3),CornerRadius=new(8)};
        paintBindings.Add(()=>{button.IsEnabled=keyboardColor?PaintKeyboardEnabled:JsonBool(serviceLighting,selectedLightingZone)&&hardware.Active&&!zoneServicesBusy;swatch.Background=Paint.Brush(Read()?.ToString("X6")??Paint.Subtle(swatch));});return button;
    }
    private string PaintZonePrefix=>selectedLightingZone switch{"front"=>"lb","hinge"=>"hinge",_=>"logo"};
    private JsonElement? PaintZoneSaved=>serviceLighting.ValueKind==JsonValueKind.Object&&serviceLighting.TryGetProperty("saved",out var saved)?saved:null;
    private bool? PaintZoneBool()=>PaintZoneSaved is{} saved&&saved.TryGetProperty(PaintZonePrefix+"_enabled",out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():null;
    private int? PaintZoneNumber(string property)=>PaintZoneSaved is{} saved&&saved.TryGetProperty(PaintZonePrefix+"_"+property,out var value)&&value.TryGetInt32(out int n)?n:null;
    private uint? PaintZoneColor()=>PaintZoneSaved is{} saved&&saved.TryGetProperty(PaintZonePrefix+"_color",out var value)&&uint.TryParse(value.GetString()?.TrimStart('#'),System.Globalization.NumberStyles.HexNumber,null,out uint rgb)?rgb:null;
    private (string Name,int Id)[] PaintZoneEffects()
    {var list=new[]{("单色常亮",1),("能量呼吸",2),("彩虹呼吸",20),("全彩波浪",3),("动态对撞",4),("极速流星",6),("跑马流水",9),("璀璨闪烁",10)};return selectedLightingZone=="logo"&&JsonBool(serviceLighting,"ec_logo")?list.Take(3).ToArray():selectedLightingZone=="logo"?list.Where(e=>e.Item2!=10).ToArray():list;}
    private async Task PaintSetZone(string property,JsonNode value)
    {
        if(uiReviewMode||upstream==null||!hardware.Active||!JsonBool(serviceLighting,selectedLightingZone)||PaintZoneSaved is not{} saved)return;
        string prefix=PaintZonePrefix;var request=new JsonObject{["zone"]=selectedLightingZone,["enabled"]=saved.GetProperty(prefix+"_enabled").GetBoolean(),["effect"]=saved.GetProperty(prefix+"_effect").GetInt32(),["brightness"]=saved.GetProperty(prefix+"_brightness").GetInt32(),["speed"]=saved.TryGetProperty(prefix+"_speed",out var speed)?speed.GetInt32():2,["color"]=saved.GetProperty(prefix+"_color").GetString()};request[property]=value.DeepClone();
        await DesktopServiceAction("lighting-"+selectedLightingZone,()=>upstream.ExecuteFeature(6,request),true);RefreshPaintedState();
    }
}

