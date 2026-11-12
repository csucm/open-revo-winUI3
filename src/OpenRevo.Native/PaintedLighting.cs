using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool ResidentKeyboardUndoBusy => PanelHardwareActionBusy(PanelOperation.Desktop, typeof(PanelUndoKeyboardRgb));
    private bool PaintKeyboardEnabled=>residentClient!=null?PanelHardwareActive&&residentLighting?.Keyboard?.CanSetRgb==true&&!ResidentKeyboardUndoBusy:(hardware.SupportsDeclaredRgb||keyboard.Kind=="SingleZone"&&lightingReadFresh)&&hardware.Active&&!hardwareBusy;
    private async Task PaintKeyboardChange(Action change, bool immediate = false)
    {
        if (uiReviewMode || !PaintKeyboardEnabled) return;
        change();
        if (residentClient != null)
        {
            QueueResidentKeyboardEdit(immediate);
            RefreshPaintedState();
            return;
        }
        RefreshPaintedState();
        await ScheduleLighting();
        RefreshPaintedState();
    }
    private FrameworkElement PaintKeyboardLevel(bool readOnly = false)
    {
        var slider = new PaintSlider("键盘背光亮度，最左侧关闭，向右四档", 0, 4, rgbLevel, 1,
            readOnly ? null : value => _ = PaintKeyboardChange(() =>
            {
                rgbLevel = (int)value;
                if (rgbLevel == 0) rgbMode = "关闭背光";
                else
                {
                    lastLightingLevel = rgbLevel;
                    if (rgbMode == "关闭背光") rgbMode = "静态单色";
                }
            }, immediate: true));
        paintBindings.Add(() =>
        {
            slider.IsEnabled = PaintKeyboardEnabled && !(residentClient != null
                ? residentLighting?.Keyboard?.CommandOnly == true : hardware.SupportsDeclaredRgb);
            slider.Value = rgbLevel;
        });
        return BrightnessTrack(slider, stepped: true);
    }
    private FrameworkElement PaintMiniLighting(bool readOnly=false)
    {
        if(keyboard.Kind=="White"&&(residentClient!=null||hardware.CoreDelegated))return PaintMonochrome(true,readOnly);
        string[] effects=["静态单色","彩虹循环","关闭背光"];
        var effect=new PaintSelect("背光模式",effects,lightingReadFresh?Array.IndexOf(effects,rgbMode):-1,readOnly?null:async i=>await PaintKeyboardChange(()=>{rgbMode=effects[i];if(i==2){if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;}else if(rgbLevel==0)rgbLevel=lastLightingLevel;})){MinWidth=0,MinHeight=34,Padding=new(6,3,6,3)};
        var color=new PaintSelect("背光颜色",colorPresets.Select(p=>p.Name).ToArray(),LightingPresetIndex,readOnly?null:async i=>await PaintKeyboardChange(()=>{customRgb=null;rgbColor=colorPresets[i].Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;})){MinWidth=0,Padding=new(6,3,6,3)};
        var more=new PaintButton("更多",readOnly?null:()=>{selectedLightingZone="keyboard";currentPage="devices";ToggleMini();}){Content=Paint.Inline(5,Paint.Icon("lighting",18),Paint.Text("更多",12)),Padding=new(6,3,6,3)};
        paintBindings.Add(()=>{effect.IsEnabled=color.IsEnabled=PaintKeyboardEnabled;effect.SelectedIndex=lightingReadFresh?Array.IndexOf(effects,rgbMode):-1;color.UnselectedText="状态未知";color.SelectedIndex=LightingPresetIndex;});
        return Paint.Card(Paint.Stack(4,Paint.Between(Paint.Inline(6,Paint.Icon("keyboard",24),PT(()=>MiniLightingTitle,12,true)),PT(()=>keyboard.Description,12,muted:true),7),PaintColumns([effect,color,more]),PaintKeyboardLevel(readOnly)),7,12);
    }
    private void RenderPaintedLighting()
    {
        if(keyboard.Kind=="White"&&(residentClient!=null||hardware.CoreDelegated)){page.Children.Add(PaintMonochrome(false));return;}
        bool secondary=IsSecondaryPaintRoute(PaintRoute);
        var tabs=new SuiteWrapPanel{ItemGap=5};foreach(var(title,key)in new[]{("键盘背光","keyboard"),("前置灯条","front"),("转轴氛围灯","hinge"),("Logo 灯光","logo")}){var tab=new PaintButton(title,()=>{selectedLightingZone=key;Render();}){Selected=key==selectedLightingZone,Padding=new(12,9,12,9)};if(key=="keyboard")tab.Content=Paint.Inline(8,Paint.Icon("keyboard",24),Paint.Text(title,12));else tab.Content=Paint.Text(title,12);tabs.Children.Add(tab);}page.Children.Add(tabs);
        var undo=PB("撤销灯光更改",async()=>{if(residentClient!=null){await UndoResidentKeyboardAsync();return;}await RunHardware(()=>hardwareRuntime.Desktop.UndoKeyboardLighting(),"已撤销上次灯光更改。");await LoadPanelHardware();RefreshPaintedState();},true);paintBindings.Add(()=>undo.IsEnabled=residentClient!=null?residentLighting?.Keyboard?.CanUndoRgb==true&&!ResidentKeyboardUndoBusy&&!ResidentKeyboardEditing:hardware.Active&&!hardwareBusy);page.Children.Add(Paint.Between(PT(()=>keyboard.Description,12,muted:true),undo));
        var power=PS("键盘背光",()=>lightingReadFresh?rgbLevel>0:null,on=>PaintKeyboardChange(()=>{if(on){rgbLevel=lastLightingLevel;rgbMode="静态单色";}else{if(rgbLevel>0)lastLightingLevel=rgbLevel;rgbLevel=0;rgbMode="关闭背光";}}),()=>PaintKeyboardEnabled,"green",true);
        var workbench=Paint.Stack(4,PT(()=>keyboard.Kind=="SingleZone"?"单区 RGB 键盘工作台":"键盘背光工作台",16,true),PT(()=>lightingReadFresh?keyboard.Description:lightingProbeError??"背光状态待读取",12,muted:true));
        var header=Paint.Between(secondary?workbench:Paint.Inline(10,Paint.Icon("keyboard"),workbench),power);
        var effects=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=560,ColumnSpacing=10,RowSpacing=10};
        foreach(var(mode,title,description,icon)in new[]{("静态单色","单色常亮","全键盘同色照明","brightness"),("彩虹循环","彩虹循环","全键盘同色循环","lighting")})
        {var button=PB(title,()=>PaintKeyboardChange(()=>{rgbMode=mode;if(rgbLevel==0)rgbLevel=lastLightingLevel;}),true);button.Content=Paint.Inline(8,Paint.Icon(icon),Paint.Stack(3,Paint.Text(title),Paint.Text(description,12,muted:true)));button.Padding=new(14);button.StretchContent();button.HorizontalAlignment=HorizontalAlignment.Stretch;paintBindings.Add(()=>{button.IsEnabled=PaintKeyboardEnabled;button.Selected=lightingReadFresh&&rgbMode==mode;});effects.Children.Add(button);}
        var brightness=PaintKeyboardLevel();
        FrameworkElement levelHeading=secondary?Paint.Text("亮度等级"):Paint.Inline(8,Paint.Icon("brightness",24),Paint.Text("亮度等级"));
        var level=Paint.Card(Paint.Stack(8,Paint.Between(levelHeading,PT(()=>lightingReadFresh?rgbLevel+" / 4":"待读取",12,muted:true)),brightness),17,12);level.Loaded+=(_,_)=>level.Background=Paint.Brush(Paint.Subtle(level));
        var swatches=new SuiteWrapPanel{ItemGap=7};foreach(var preset in colorPresets){var button=PB(preset.Name,()=>PaintKeyboardChange(()=>{customRgb=null;rgbColor=preset.Name;rgbMode="静态单色";if(rgbLevel==0)rgbLevel=lastLightingLevel;}),true);button.Content=new Ellipse{Width=16,Height=16,Fill=new SolidColorBrush(LightingColor(preset.Hex)),Stroke=Paint.Brush("22000000"),StrokeThickness=1};button.Width=button.Height=32;button.MinHeight=0;button.Padding=new(0);button.CornerRadius=new(16);paintBindings.Add(()=>{button.IsEnabled=PaintKeyboardEnabled;button.Selected=lightingReadFresh&&rgbMode=="静态单色"&&colorPresets[LightingPresetIndex].Hex==preset.Hex;});swatches.Children.Add(button);}
        FrameworkElement paletteHeading=secondary?Paint.Text("定制主色调"):Paint.Inline(8,Paint.Icon("lighting",24),Paint.Text("定制主色调"));
        var palette=Paint.Card(new LightingPaletteRow(paletteHeading,swatches,Paint.Inline(8,Paint.Text("自定义颜色",12),PaintColorEntry(true))),17,12);palette.Loaded+=(_,_)=>palette.Background=Paint.Brush(Paint.Subtle(palette));
        var more=new PaintButton("更多设置",async()=>await ShowMessage("键盘背光",keyboard.Description+"。"+(lightingProbeError??"设备确认后更新状态。"))){Content=Paint.Inline(8,Paint.Text("更多设置"),Paint.Utility("chevron"))};
        page.Children.Add(Paint.Card(Paint.Stack(16,header,effects,level,palette,Paint.Between(PT(()=>ResidentKeyboardEditing?"正在应用背光…":(residentClient!=null?residentLighting?.Keyboard?.CommandOnly==true:hardware.SupportsDeclaredRgb)?"可发送灯效和颜色命令 · 未回读确认":lightingReadFresh?"设置由设备确认。":"背光状态待读取。",12,muted:true),more)),22));
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
        paintBindings.Add(()=>{button.IsEnabled=keyboardColor?PaintKeyboardEnabled:PaintZoneEnabled;swatch.Background=Paint.Brush(Read()?.ToString("X6")??Paint.Subtle(swatch));});return button;
    }
    private PanelZoneSnapshot? ResidentSelectedZone =>
        residentLighting?.Zones.FirstOrDefault(zone => zone.SavedCommand.Zone == selectedLightingZone);
    private bool PaintZoneEnabled => residentClient != null
        ? ResidentSelectedZone?.CanSet == true && PanelHardwareActive &&
            !PanelHardwareActionBusy(PanelOperation.Desktop, (typeof(PanelSetLightingZone), selectedLightingZone))
        : JsonBool(serviceLighting, selectedLightingZone) && hardware.Active && !zoneServicesBusy;
    private bool PaintZoneHasSpeed => residentClient != null ? ResidentSelectedZone?.HasSpeed == true
        : selectedLightingZone != "logo" && !(selectedLightingZone == "front" && JsonBool(serviceLighting, "ec_front"));
    private string PaintZonePrefix=>selectedLightingZone switch{"front"=>"lb","hinge"=>"hinge",_=>"logo"};
    private JsonElement? PaintZoneSaved=>serviceLighting.ValueKind==JsonValueKind.Object&&serviceLighting.TryGetProperty("saved",out var saved)?saved:null;
    private bool? PaintZoneBool()=>residentClient!=null?ResidentSelectedZone?.SavedCommand.Enabled:PaintZoneSaved is{} saved&&saved.TryGetProperty(PaintZonePrefix+"_enabled",out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():null;
    private int? PaintZoneNumber(string property)
    {
        if (residentClient != null)
        {
            var zone = ResidentSelectedZone?.SavedCommand;
            return property switch { "effect" => zone?.Effect, "brightness" => zone?.Level, "speed" => zone?.Speed, _ => null };
        }
        return PaintZoneSaved is { } saved && saved.TryGetProperty(PaintZonePrefix + "_" + property, out var value) &&
            value.TryGetInt32(out int number) ? number : null;
    }
    private uint? PaintZoneColor()=>residentClient!=null?ResidentSelectedZone?.SavedCommand.Rgb:PaintZoneSaved is{} saved&&saved.TryGetProperty(PaintZonePrefix+"_color",out var value)&&uint.TryParse(value.GetString()?.TrimStart('#'),System.Globalization.NumberStyles.HexNumber,null,out uint rgb)?rgb:null;
    private (string Name,int Id)[] PaintZoneEffects()
    {
        (string Name,int Id)[] list = [("单色常亮",1),("能量呼吸",2),("彩虹呼吸",20),("全彩波浪",3),("动态对撞",4),
            ("极速流星",6),("跑马流水",9),("璀璨闪烁",10)];
        if (residentClient != null)
        {
            var supported = ResidentSelectedZone?.Effects;
            return supported == null ? [] : list.Where(effect => supported.Contains(effect.Id) &&
                (selectedLightingZone != "logo" || effect.Id != 10)).ToArray();
        }
        return selectedLightingZone=="logo"&&JsonBool(serviceLighting,"ec_logo")?list.Take(3).ToArray():selectedLightingZone=="logo"?list.Where(e=>e.Id!=10).ToArray():list;
    }
    private PaintSelect ResidentZoneEffectChoice(string name)
    {
        var choice = new PaintSelect(name, [], -1, async index =>
        {
            var effects = PaintZoneEffects();
            if (index < 0 || index >= effects.Length) return;
            try { await PaintSetZone("effect", JsonValue.Create(effects[index].Id)!); }
            catch (Exception error) { if (!closed) await ShowMessage("设置未完成", error.Message); }
        }) { Width = 180 };
        paintBindings.Add(() =>
        {
            var effects = PaintZoneEffects();
            choice.Replace(effects.Select(effect => effect.Name).ToArray(),
                Array.FindIndex(effects, effect => effect.Id == PaintZoneNumber("effect")));
            choice.IsEnabled = PaintZoneEnabled;
        });
        return choice;
    }
    private async Task PaintSetZone(string property,JsonNode value)
    {
        if (residentClient != null)
        {
            if (uiReviewMode || !PaintZoneEnabled || ResidentSelectedZone is not { } selected) return;
            var state = selected.SavedCommand;
            var change = property switch
            {
                "enabled" => state with { Enabled = value.GetValue<bool>() },
                "effect" => state with { Effect = value.GetValue<int>() },
                "brightness" => state with { Level = value.GetValue<int>() },
                "speed" when selected.HasSpeed => state with { Speed = value.GetValue<int>() },
                "color" => state with { Rgb = Convert.ToUInt32(value.GetValue<string>().TrimStart('#'), 16) },
                _ => null
            };
            if (change != null) await RunResidentActionAsync(PanelOperation.Desktop, desktop: change);
            return;
        }
        if(uiReviewMode||upstream==null||!hardware.Active||!JsonBool(serviceLighting,selectedLightingZone)||PaintZoneSaved is not{} saved)return;
        string prefix=PaintZonePrefix;var request=new JsonObject{["zone"]=selectedLightingZone,["enabled"]=saved.GetProperty(prefix+"_enabled").GetBoolean(),["effect"]=saved.GetProperty(prefix+"_effect").GetInt32(),["brightness"]=saved.GetProperty(prefix+"_brightness").GetInt32(),["speed"]=saved.TryGetProperty(prefix+"_speed",out var speed)?speed.GetInt32():2,["color"]=saved.GetProperty(prefix+"_color").GetString()};request[property]=value.DeepClone();
        var zone=new OpenRevo.Core.ZoneLightingRequest(selectedLightingZone,request["enabled"]!.GetValue<bool>(),
            request["effect"]!.GetValue<int>(),request["brightness"]!.GetValue<int>(),request["speed"]!.GetValue<int>(),
            request["color"]?.GetValue<string>());
        await DesktopServiceAction("lighting-"+zone.Zone,()=>hardwareRuntime.Desktop.SetLightingZone(zone),true);RefreshPaintedState();
    }
}

