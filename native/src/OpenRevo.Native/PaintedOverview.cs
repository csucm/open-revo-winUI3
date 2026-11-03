using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using Windows.Foundation;
using Windows.System;
using System.Numerics;
namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly List<Action> paintBindings=[];
    private bool refreshingPaint;
    private static bool UsePaintedUi=>true;
    private void RefreshPaintedState()
    {
        if(!UsePaintedUi||refreshingPaint||closed)return;refreshingPaint=true;
        try{foreach(var bind in paintBindings.ToArray())bind();navigation.SetStatus(hardware.Active,identity.Board=="未知"?"设备状态待读取":identity.Board);}finally{refreshingPaint=false;}
    }
    private TextBlock PT(Func<string> value,double size=14,bool bold=false,bool muted=false)
    {var text=Paint.Text(value(),size,bold,muted);paintBindings.Add(()=>{string next=value();if(text.Text!=next)text.Text=next;});return text;}
    private PaintButton PB(string name,Func<Task> action,bool hardwareAction=false)
    {return new(name,async()=>{if(uiReviewMode&&hardwareAction)return;try{await action();}catch(Exception error){await ShowMessage("操作未完成",error.Message);}finally{RefreshPaintedState();}});}
    private PaintButton PL(string name,Action action)
    {var b=new PaintButton(name,action){Variant="plain",Padding=new(0),MinHeight=28};b.Content=Paint.Between(Paint.Text(name,12),Paint.Utility("chevron",14));b.StretchContent();return b;}
    private PaintSwitch PS(string name,Func<bool?> read,Func<bool,Task> action,Func<bool>? enabled=null,string family="blue",bool hardwareAction=false)
    {
        var toggle=new PaintSwitch(name,read()==true,async value=>{if(uiReviewMode&&hardwareAction){toggleReset();return;}try{await action(value);}catch(Exception error){await ShowMessage("设置未完成",error.Message);}RefreshPaintedState();});
        void toggleReset()=>RefreshPaintedState();toggle.Family=family;
        paintBindings.Add(()=>{toggle.IsOn=read()==true;toggle.IsEnabled=(enabled?.Invoke()??true)&&read().HasValue;});return toggle;
    }
    private FrameworkElement PaintHeader(string route)
    {
        string key=route=="overview"?"home":route;string title=route switch{"overview"=>"状态总览","devices"=>"灯光与设备",_=>route},subtitle="";
        if(route=="updates"){key="info";title="软件更新";subtitle="查看社区版发布与下载状态。";}
        if(ApprovedCopy.Pages.TryGetValue(route,out var copy)){title=copy.Title;subtitle=copy.Subtitle;key=copy.Icon switch{"tuning"=>"mode-custom","color"=>"lighting",_=>copy.Icon};}
        if(route=="overview")subtitle="运行状态与常用控制，一眼掌握。";
        if(route=="devices")key="keyboard";
        return Paint.Inline(10,Paint.Icon(key,40),Paint.Stack(route=="overview"?11:6,Paint.Text(title,24,true),Paint.Text(subtitle,12,muted:true)));
    }
    private void PaintNavigate(string route)
    {customTuningOpen=route=="tuning";colorTuningOpen=route=="color";if(route=="lightingzone"){selectedLightingZone="front";currentPage="devices";}else currentPage=route is "tuning" or "color"?"display":route;Render();}
    private string PaintRoute=>customTuningOpen?"tuning":colorTuningOpen?"color":currentPage=="devices"&&selectedLightingZone!="keyboard"?"lightingzone":currentPage;
    private void RenderPaintedPage()
    {
        paintBindings.Clear();string route=PaintRoute;
        syncingNavigation=true;navigation.SelectedItem=navigation.MenuItems.OfType<PaintNavItem>().FirstOrDefault(i=>i.Id==(route is "tuning" or "color"?"display":route=="lightingzone"?"devices":route is "osdsettings" or "diagnostics" or "storage" or "updates"?"settings":route=="sleep"?"power":currentPage));syncingNavigation=false;
        scroll.VerticalScrollMode=mini?ScrollMode.Disabled:ScrollMode.Auto;scroll.VerticalScrollBarVisibility=mini?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
        if(mini){RenderPaintedMini();RefreshPaintedState();return;}
        if(route is "sleep" or "lightingzone" or "tuning" or "color" or "osdsettings" or "storage" or "diagnostics" or "updates")
        {string parent=route=="sleep"?"power":route=="lightingzone"?"devices":route is "tuning" or "color"?"display":"settings";var back=PB("返回",()=>{if(route=="tuning"){BackFromTuning();return Task.CompletedTask;}if(route=="lightingzone")selectedLightingZone="keyboard";PaintNavigate(parent);return Task.CompletedTask;});back.HorizontalAlignment=HorizontalAlignment.Left;back.Variant="plain";back.MinHeight=22;back.Padding=new(0);back.Content=Paint.Inline(6,Paint.Utility("back",16),Paint.Text("返回 "+(parent=="power"?"电源与电池":parent=="devices"?"灯光与设备":parent=="display"?TuningReturnLabel:"设置与诊断"),12));page.Children.Add(back);}
        if(route=="overview"){page.Children.Add(Paint.Between(PaintHeader(route),PaintEditActions()));PaintHome();}
        else {page.Children.Add(PaintHeader(route));RenderPaintedDetails(route);}
        RefreshPaintedState();ArrangeDesktopViewport();
    }
    private FrameworkElement PaintEditActions()
    {
        if(overviewDraft==null){var edit=PB("编辑磁贴",()=>{overviewDraft=[..overviewSaved];Render();return Task.CompletedTask;});edit.Padding=new(10,5,10,5);edit.Content=Paint.Inline(8,Paint.Icon("edit-tiles",24),Paint.Text("编辑磁贴"));return edit;}
        PaintButton Icon(string icon,string name,Func<Task> action){var b=PB(name,action);b.Content=Paint.Icon(icon);b.Width=b.Height=34;b.MinHeight=0;b.Padding=new(0);b.Variant="icon";return b;}
        return Paint.Inline(8,Icon("save-layout","保存磁贴布局",async()=>{var order=overviewDraft!.ToArray();if(!uiReviewMode)await Task.Run(()=>OverviewLayout.Save(order));overviewSaved=[..order];overviewDraft=null;Render();}),Icon("cancel-layout","取消编辑磁贴",()=>{overviewDraft=null;Render();return Task.CompletedTask;}));
    }
    private void PaintHome()
    {
        var ids=overviewDraft??overviewSaved;overviewBoard=new OverviewBoard{Order=ids};
        foreach(string id in ids)
        {
            var tile=new Grid{Tag=id,Background=Paint.Brush("00000000")};var body=PaintTile(id);tile.Children.Add(body);
            if(overviewDraft!=null)
            {
                body.IsHitTestVisible=false;foreach(var input in DetailDescendants(body).OfType<Control>())input.IsTabStop=false;
                var remove=new PaintButton("移除 "+tileTitles[id],()=>{overviewDraft.Remove(id);Render();}){Content=Paint.Utility("minus",14),Width=24,Height=24,MinHeight=0,Padding=new(0),CornerRadius=new(12),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};tile.Children.Add(remove);
                var handle=new PaintButton("拖动 "+tileTitles[id]){IsHitTestVisible=false,Content=Paint.Text("⠿",16),Width=24,Height=24,MinHeight=0,Padding=new(0),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};tile.Children.Add(handle);
                handle.KeyDown+=(_,e)=>{if((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0&&e.Key is VirtualKey.Left or VirtualKey.Right){int from=ids.IndexOf(id),to=Math.Clamp(from+(e.Key==VirtualKey.Left?-1:1),0,ids.Count-1);ids.RemoveAt(from);ids.Insert(to,id);Render();e.Handled=true;}};
                PaintTileDrag(tile,id,remove,handle);
            }
            overviewBoard.Children.Add(tile);
        }
        if(overviewDraft!=null)
        {
            string[] missing=OverviewLayout.BuiltIns.Except(ids).ToArray();var add=new PaintSelect("添加面板",missing.Select(id=>tileTitles[id]).ToArray(),-1,i=>{if(i>=0){overviewDraft.Add(missing[i]);Render();}}){MinHeight=92};add.Content=Paint.Stack(4,Paint.Text("＋",24),Paint.Text("添加面板",12));overviewBoard.Children.Add(add);
        }
        page.Children.Add(overviewBoard);page.Children.Add(Paint.Text("快捷控制",16,true));
        var groups=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=639,ColumnSpacing=12,RowSpacing=12};
        foreach(var(title,keys)in new[]{("键盘与触控",new[]{"touchpad","win","fn"}),("隐私与提示",new[]{"camera","mic","osd"}),("散热与唤醒",new[]{"boost","awake","screen"})})
        {
            FrameworkElement heading=Paint.Text(title,16,true);if(title=="键盘与触控")heading=Paint.Between(heading,PL("灯光",()=>PaintNavigate("devices")));
            heading.Margin=new(4,0,4,4);var stack=Paint.Stack(0,heading);foreach(string key in keys)stack.Children.Add(PaintQuickRow(key));groups.Children.Add(Paint.Card(stack,13));
        }page.Children.Add(groups);
    }
    private FrameworkElement PaintTile(string id)
    {
        if(id=="fan")return PaintMinHeight(Paint.Card(Paint.Stack(8,Paint.Text("散热",14,true),Paint.Between(Paint.Text("CPU 风扇",12,muted:true),PT(()=>ec.CpuFan is int n?$"{n} RPM":"— RPM",16)),Paint.Between(Paint.Text("GPU 风扇",12,muted:true),PT(()=>ec.GpuFan is int n?$"{n} RPM":"— RPM",16)))),120);
        if(id is "cpu" or "gpu" or "vram")
        {
            FrameworkElement Live(string key,double size=12){var t=Paint.Text("—",size);live[key]=t;return t;}
            string prefix=id=="vram"?"overviewMemory":id=="cpu"?"overviewCpu":"overviewGpu";
            var head=Paint.Between(Paint.Text(tileTitles[id],14,true),Paint.Inline(3,Live(prefix+"Mhz"),Paint.Text("MHz",12,muted:true)));
            var value=id=="vram"?Paint.Inline(3,Live("overviewVramUsed",24),Live("overviewVramTotal")):Paint.Inline(2,Live(prefix+"Temp",24),Paint.Text("°C",14));
            var foot=Paint.Between(Paint.Inline(5,Paint.Text("占用",12,muted:true),Live(id=="vram"?"overviewVramUsage":prefix+"Usage")),id=="vram"?Paint.Text("",12):Live(prefix+"Power"),8);
            var meter=new Border{Height=3,CornerRadius=new(2),Background=Paint.Brush(Paint.Line(root))};var fill=new Border{Height=3,CornerRadius=new(2),Background=Paint.Brush(Paint.Accent(root)),HorizontalAlignment=HorizontalAlignment.Left};meter.Child=fill;
            paintBindings.Add(()=>{double ratio=id=="cpu"?latest?.CpuPercent??0:id=="gpu"?overviewTelemetry?.Gpu?.Usage??0:overviewTelemetry?.Gpu?.MemoryPercent??0;fill.Width=Math.Max(0,meter.ActualWidth)*Math.Clamp(ratio/100,0,1);});
            return PaintMinHeight(Paint.Card(Paint.Stack(8,head,value,foot,meter)),120);
        }
        string icon=id=="performance"?"performance":id=="battery"?"battery":"display";var heading=Paint.Inline(8,Paint.Icon(icon,28,true,false),Paint.Text(tileTitles[id],16,true));
        if(id=="performance")
        {
            var target=Paint.Text("—",12,muted:true);live["overviewPowerTarget"]=target;var grid=new Grid{RowSpacing=8};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.Children.Add(heading);var meta=Paint.Between(PT(()=>PerformanceName,14,muted:true),target);Grid.SetRow(meta,1);grid.Children.Add(meta);var modes=PaintModes(false);Grid.SetRow(modes,2);grid.Children.Add(modes);return PaintMinHeight(Paint.Card(grid),180);
        }
        FrameworkElement number=id=="battery"?Paint.Inline(12,PaintBatteryPicture(44),PT(()=>latest?.BatteryPercent is int n?n+"%":"—",24)):PT(()=>displayState?.Active==true?displayState.Hertz+" Hz":"— Hz",24);
        var caption=PT(()=>id=="battery"?latest?.OnAc switch{true=>"已连接电源",false=>"正在使用电池",_=>"供电状态未知"}:"当前刷新率",12,muted:true);
        FrameworkElement controls;
        if(id=="battery")
        {
            var cap=new PaintSelect("充电上限",new[]{"100%","80%","60%"},-1,async i=>{if(!uiReviewMode)await RunHardware(()=>hardware.SetBatteryPreset(new[]{100,80,60}[i]),"充电上限已回读确认。");}){Variant="plain",MinWidth=0,MinHeight=28,Padding=new(0)};
            cap.Content=Paint.Between(Paint.Text("充电上限",12),Paint.Inline(6,PT(()=>hardware.State?.Upper is byte limit?limit+"%":"—",12),Paint.Utility("chevron",14)));cap.StretchContent();paintBindings.Add(()=>cap.IsEnabled=hardware.Active&&hardware.SupportsBattery&&!hardwareBusy);controls=Paint.Stack(0,cap,PL("Windows 电源计划",()=>PaintNavigate("settings")));
        }
        else
        {
            int[] rates=displayState?.Rates.Distinct().Order().ToArray()??[];var choice=new PaintSelect("调整刷新率",rates.Select(r=>r+" Hz").ToArray(),-1,async i=>{if(!uiReviewMode)await ChangeRefresh(rates[i]);}){Variant="plain",MinHeight=28,MinWidth=0,Padding=new(0)};choice.Content=Paint.Between(Paint.Text("调整刷新率",12),Paint.Utility("chevron",14));choice.StretchContent();paintBindings.Add(()=>choice.IsEnabled=displayState?.Active==true&&rates.Length>0&&!displayBusy);
            var automatic=PB("拔电低刷",async()=>{lowRefreshOnBattery=!lowRefreshOnBattery;await SavePreferences();if(!uiReviewMode)await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(lowRefreshOnBattery?"已开启":"已关闭"),"refresh");});automatic.Variant="plain";automatic.Padding=new(0);automatic.MinHeight=28;automatic.Content=Paint.Between(Paint.Text("拔电低刷",12),PT(()=>lowRefreshOnBattery?"已开启":"已关闭",12,muted:true));automatic.StretchContent();controls=Paint.Stack(0,choice,automatic);
        }
        var body=TileBody(heading,number,caption,Paint.Rule(controls,4));return PaintMinHeight(Paint.Card(body),180);
    }
    private FrameworkElement PaintModes(bool compact,bool gpu=false)
    {
        int count=gpu?3:4;var grid=new Grid{ColumnSpacing=6};for(int i=0;i<count;i++)grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        for(int i=0;i<count;i++)
        {
            int mode=i+1;string name=gpu?OriginalGpuName(mode):new[]{"办公","均衡","狂暴","自定义"}[i],icon=gpu?new[]{"gpu-integrated","gpu-hybrid","gpu-discrete"}[i]:new[]{"mode-office","mode-balanced","mode-performance","mode-custom"}[i];
            var img=Paint.Icon(icon,compact?28:mode==4?34:24,false,compact||!gpu&&mode!=4);var label=Paint.Text(name,12);label.TextWrapping=TextWrapping.NoWrap;label.LineHeight=15;label.TextAlignment=TextAlignment.Center;
            var b=PB(name,()=>gpu?RunGpuRequest(mode):ApplySavedPerformance(mode),true);b.Content=Paint.Stack(4,img,label);b.Padding=new(3);b.Height=b.MinHeight=compact?56:68;b.HorizontalAlignment=HorizontalAlignment.Stretch;bool last=false;
            paintBindings.Add(()=>{bool selected=gpu?gpuState?.Mode==mode:hardware.CurrentMode==mode;b.Selected=selected;b.IsEnabled=hardware.Active&&!hardwareBusy&&(gpu?gpuReadFresh&&!gpuChanging&&gpuState?.Supports(mode)==true:mode==4?hardware.SupportsCustomTuning:hardware.SupportsPerformance);if(selected!=last){((StackPanel)b.Content!).Children[0]=Paint.Icon(icon,compact?28:mode==4?34:24,selected,compact||!gpu&&mode!=4);last=selected;}});
            FrameworkElement item=b;if(!gpu&&mode==4){var cell=new Grid();cell.Children.Add(b);var gear=new PaintButton("编辑自定义预设",OpenTuning){Content=Paint.Utility("settings",16),Variant="icon",Width=24,Height=24,MinHeight=0,Padding=new(2),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};cell.Children.Add(gear);item=cell;}
            Grid.SetColumn(item,i);grid.Children.Add(item);
        }return grid;
    }
    private bool? PaintQuickState(string key)=>key switch{"touchpad"=>extraSwitches.Touchpad,"fn"=>extraSwitches.FnLock,"win"=>quickState.WinLocked,"camera"=>quickState.Camera,"mic"=>quickState.Microphone,"osd"=>osdEnabled,"boost"=>hardware.State?.FanBoost??ec.FanBoost,"awake"=>sessionPower?.Enabled==true,_=>false};
    private bool PaintQuickEnabled(string key)=>key switch{"camera"=>hardware.Active&&quickState.CameraAvailable&&quickState.Camera.HasValue&&upstream!=null,"mic"=>quickState.Microphone.HasValue&&upstream!=null,"win"=>hardware.Active&&quickState.WinLocked.HasValue,"touchpad" or "fn"=>hardware.Active&&upstream!=null&&PaintQuickState(key).HasValue,"boost"=>hardware.Active&&hardware.SupportsFanBoost&&!hardwareBusy,_=>true};
    private async Task PaintQuickAction(string key)
    {if(uiReviewMode)return;if(key is "touchpad" or "fn")await SetExtraSwitch(key);else if(key=="osd"){osdEnabled=!osdEnabled;await SavePreferences();if(!osdEnabled)nativeOsd?.Hide();else ConfirmedActionOsd("屏幕提示 · 已开启","osd");}else if(key=="boost")await RunHardware(()=>hardware.SetFanBoost(PaintQuickState(key)!=true),"强冷状态已回读确认。");else await RunQuickAction(key);}
    private FrameworkElement PaintQuickRow(string key,bool compact=false)
    {
        string name=key switch{"touchpad"=>"触摸板","win"=>"Win 键锁","fn"=>"F1–F12 模式","camera"=>"相机","mic"=>"麦克风","osd"=>"OSD 提示","boost"=>"一键强冷","awake"=>"防休眠",_=>"关闭屏幕"};
        string id=key switch{"win"=>"win-lock","fn"=>"fn-lock","boost"=>"fan","screen"=>"display",_=>key};string family=key switch{"win"=>"rose","fn"=>"violet","camera"=>"green","mic"=>"orange","boost"=>"cyan",_=>"blue"};
        var picture=new Grid{Width=compact?24:34,Height=compact?24:34};bool? last=null;
        void Icon(){bool on=PaintQuickState(key)==true||key=="screen";if(last==on&&picture.Children.Count>0)return;last=on;picture.Children.Clear();picture.Children.Add(Paint.Icon(id,compact?24:34,on));}Icon();paintBindings.Add(Icon);
        string State(){bool? on=PaintQuickState(key);return key=="screen"?"仅息屏，电脑继续运行":!on.HasValue?"状态未知":key=="win"?(on.Value?"已锁定":"已解锁"):key=="fn"?(on.Value?"Fn Lock 已开启":"Fn Lock 已关闭"):key=="mic"?(on.Value?"收音已开启":"已静音"):key=="boost"?(on.Value?"强冷已开启":"自动温控"):key=="awake"?(on.Value?"保持唤醒":"随系统休眠"):(on.Value?"已开启":"已关闭");}
        var button=PB(name,()=>PaintQuickAction(key),true);paintBindings.Add(()=>button.IsEnabled=PaintQuickEnabled(key));button.StretchContent();
        if(compact){var text=Paint.Text(name.Replace(" ",""),12);text.TextAlignment=TextAlignment.Center;text.TextWrapping=TextWrapping.NoWrap;picture.HorizontalAlignment=HorizontalAlignment.Center;button.Content=Paint.Stack(2,picture,text);button.Height=50;button.Padding=new(1,2,1,2);return button;}
        FrameworkElement tail;if(key=="screen"){tail=Paint.Card(Paint.Text("息屏",12),0,8,true);((Border)tail).Padding=new(8,2,8,2);}else{var indicator=new PaintSwitch(name,PaintQuickState(key)==true){Family=family,IsHitTestVisible=false,IsTabStop=false};paintBindings.Add(()=>indicator.IsOn=PaintQuickState(key)==true);tail=indicator;}
        button.Content=Paint.Between(Paint.Inline(8,picture,Paint.Stack(2,Paint.Text(name),PT(State,12,muted:true))),tail,10);button.Variant="plain";button.Padding=new(4,2,4,2);button.MinHeight=46;return button;
    }
    private FrameworkElement PaintBatteryPicture(double width)
    {
        var canvas=new Canvas{Width=160,Height=50};var shell=new Border{Width=150,Height=48,BorderThickness=new(2),CornerRadius=new(6),BorderBrush=Paint.Brush(Paint.Muted(root))};Canvas.SetLeft(shell,1);Canvas.SetTop(shell,1);canvas.Children.Add(shell);var tip=new Border{Width=5,Height=16,CornerRadius=new(2),Background=Paint.Brush(Paint.Muted(root))};Canvas.SetLeft(tip,154);Canvas.SetTop(tip,17);canvas.Children.Add(tip);var fill=new Border{Height=38,CornerRadius=new(3),Background=Paint.Brush(Paint.Accent(root,"green"))};Canvas.SetLeft(fill,6);Canvas.SetTop(fill,6);canvas.Children.Add(fill);paintBindings.Add(()=>fill.Width=latest?.BatteryPercent is int p?140*Math.Clamp(p,5,100)/100d:0);return new Viewbox{Child=canvas,Width=width,Height=width*50/160,Stretch=Stretch.Uniform};
    }
    private void RenderPaintedMini()
    {
        page.Spacing=6;page.Padding=new(12,4,12,6);
        FrameworkElement Group(string icon,Func<string> title,Func<string> meta,FrameworkElement content)
        {var heading=Paint.Between(Paint.Inline(6,Paint.Icon(icon,24,true,true),PT(title,12,true)),PT(meta,12,muted:true),7);return Paint.Card(Paint.Stack(4,heading,content),7,12);}
        page.Children.Add(Group("performance",()=>"性能模式："+PerformanceName,()=>"CPU "+(temperature.Cpu?.ToString()??"—")+"°C · fans "+(ec.CpuFan?.ToString()??"—")+" RPM",PaintModes(true)));
        page.Children.Add(Group("gpu",()=>"显卡模式："+gpuModeLabel,()=>"GPU "+(temperature.Gpu?.ToString()??"—")+"°C · fans "+(ec.GpuFan?.ToString()??"—")+" RPM",PaintModes(true,true)));
        var rates=new List<FrameworkElement>();var low=PB("拔电低刷",async()=>{lowRefreshOnBattery=!lowRefreshOnBattery;await SavePreferences();if(!uiReviewMode)await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(lowRefreshOnBattery?"已开启":"已关闭"),"refresh");});low.Padding=new(3);low.MinHeight=30;rates.Add(low);paintBindings.Add(()=>low.Selected=lowRefreshOnBattery);
        int[] values=displayState?.Rates.Distinct().Order().ToArray()??[];
        foreach(bool high in new[]{false,true}){int hz=values.Length>0?(high?values[^1]:values[0]):0;var b=PB(hz>0?hz+" Hz":high?"最高刷新率":"最低刷新率",()=>ChangeRefresh(hz),true);b.MinHeight=30;b.Padding=new(3);paintBindings.Add(()=>{b.IsEnabled=hz>0&&displayState?.Active==true&&!displayBusy;b.Selected=displayState?.Hertz==hz;});rates.Add(b);}
        page.Children.Add(Group("display",()=>"屏幕刷新率："+(displayState?.Hertz.ToString()??"—")+" Hz",()=>displayState?.Panel??"内置屏待识别",PaintColumns(rates.ToArray())));
        var presets=Paint.Inline(4);foreach(int n in new[]{25,60,100}){var b=PB(n==25?"暗光25%":n==60?"室内60%":"户外100%",()=>SetCompactBrightness(n),true);b.MinHeight=23;b.Padding=new(4,1,4,1);b.Content=Paint.Text(n==25?"暗光25%":n==60?"室内60%":"户外100%",12);paintBindings.Add(()=>b.IsEnabled=displayState?.Active==true&&screenBrightness.HasValue&&!displayBusy);presets.Children.Add(b);}
        var brightness=new PaintSlider("屏幕亮度",10,100,screenBrightness??100,1,async v=>{if(!uiReviewMode)await SetCompactBrightness((int)v);});paintBindings.Add(()=>{brightness.IsEnabled=displayState?.Active==true&&screenBrightness.HasValue&&!displayBusy;brightness.Value=screenBrightness??100;});page.Children.Add(Paint.Card(Paint.Stack(0,Paint.Between(Paint.Inline(6,Paint.Icon("brightness",24),PT(()=>"屏幕亮度："+(screenBrightness?.ToString()??"—")+"%",12,true)),presets,4),brightness),7,12));
        page.Children.Add(PaintMiniLighting());
        var charge=new List<FrameworkElement>();foreach(var(name,cap)in new[]{("工作站",60),("平衡",80),("长效",100)}){var b=PB(name,()=>RunHardware(()=>hardware.SetBatteryPreset(cap),"充电上限已回读确认。"),true);b.MinHeight=30;b.Padding=new(4);b.Content=Paint.Text(name,12);paintBindings.Add(()=>{b.IsEnabled=hardware.Active&&hardware.SupportsBattery&&!hardwareBusy;b.Selected=hardware.State?.Upper==cap;});charge.Add(b);}page.Children.Add(Group("battery",()=>"电池健康",()=>hardware.State?.Upper is byte n?"上限 "+n+"%":"上限 —",PaintColumns(charge.ToArray())));
        page.Children.Add(PaintColumns(new[]{"boost","camera","mic","win","awake","screen"}.Select(k=>PaintQuickRow(k,true)).ToArray(),4));
        var status=PB("硬件接管状态",()=>{PaintNavigate("settings");if(mini)ToggleMini();return Task.CompletedTask;});
        status.MinHeight=22;status.Padding=new(6,1,6,1);status.CornerRadius=new(5);
        status.Content=PT(()=>hardware.Active?"● 已接管":"● 未接管",12,muted:true);
        var footer=Paint.Between(PT(()=>identity.Board=="未知"?"机型待读取":identity.Board,12,muted:true),status);
        footer.Margin=new(0,12,0,0);page.Children.Add(footer);
    }
    private static FrameworkElement PaintMinHeight(FrameworkElement item,double height){item.MinHeight=height;return item;}
    private static Grid PaintColumns(FrameworkElement[] children,double gap=6)
    {var grid=new Grid{ColumnSpacing=gap};for(int i=0;i<children.Length;i++){grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});children[i].HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(children[i],i);grid.Children.Add(children[i]);}return grid;}
    private void PaintTileDrag(Grid tile,string id,PaintButton remove,PaintButton handle)
    {
        var translation=new TranslateTransform();tile.RenderTransform=translation;
        bool held=false;Point grab=default;List<string>? start=null;uint pointer=0;
        void Finish(bool keep){if(!held)return;held=false;tile.ReleasePointerCaptures();translation.X=translation.Y=0;Canvas.SetZIndex(tile,0);if(!keep&&start!=null)overviewDraft=[..start];Render();}
        tile.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,e)=>{if(overviewDraft==null||e.OriginalSource is DependencyObject source&&IsWithin(source,remove))return;var p=e.GetCurrentPoint(tile);if(!p.Properties.IsLeftButtonPressed)return;grab=p.Position;held=tile.CapturePointer(e.Pointer);if(held){pointer=e.Pointer.PointerId;handle.Focus(FocusState.Programmatic);start=[..overviewDraft];Canvas.SetZIndex(tile,10);e.Handled=true;}}),true);
        tile.PointerMoved+=(_,e)=>{if(!held||e.Pointer.PointerId!=pointer||overviewBoard==null||overviewDraft==null)return;var p=e.GetCurrentPoint(overviewBoard).Position;foreach(var other in overviewBoard.Children.OfType<Grid>().Where(x=>x!=tile)){var at=overviewBoard.Slot(other);if(p.X>at.X+at.Width*.12&&p.X<at.Right-at.Width*.12&&p.Y>at.Y+at.Height*.12&&p.Y<at.Bottom-at.Height*.12){int from=overviewDraft.IndexOf(id),to=overviewDraft.IndexOf((string)other.Tag);if(from!=to){var neighbors=overviewBoard.Children.OfType<Grid>().Where(item=>item!=tile)
                .Select(item=>(Item:item,Slot:overviewBoard.Slot(item))).ToArray();
            overviewDraft.RemoveAt(from);overviewDraft.Insert(to,id);
            overviewBoard.InvalidateMeasure();overviewBoard.UpdateLayout();
            if(Design.MotionEnabled)
            {
                foreach(var (item,previous) in neighbors)
                {
                    var next=overviewBoard.Slot(item);
                    if(previous.X==next.X&&previous.Y==next.Y)continue;
                    ElementCompositionPreview.SetIsTranslationEnabled(item,true);
                    var visual=ElementCompositionPreview.GetElementVisual(item);
                    var animation=visual.Compositor.CreateVector3KeyFrameAnimation();
                    animation.InsertKeyFrame(0,new Vector3((float)(previous.X-next.X),(float)(previous.Y-next.Y),0));
                    animation.InsertKeyFrame(1,Vector3.Zero);
                    animation.Duration=TimeSpan.FromMilliseconds(MotionDesign.Duration(155));
                    visual.Properties.StartAnimation("Translation",animation);
                }
            }}break;}}var slot=overviewBoard.Slot(tile);translation.X=p.X-grab.X-slot.X;translation.Y=p.Y-grab.Y-slot.Y;};
        tile.PointerReleased+=(_,_)=>Finish(true);tile.PointerCaptureLost+=(_,_)=>Finish(false);tile.PointerCanceled+=(_,_)=>Finish(false);void Escape(object sender,KeyRoutedEventArgs e){if(held&&e.Key==VirtualKey.Escape){Finish(false);e.Handled=true;}}root.KeyDown+=Escape;void Blur(object sender,WindowActivatedEventArgs e){if(e.WindowActivationState==WindowActivationState.Deactivated)Finish(false);}Activated+=Blur;tile.Unloaded+=(_,_)=>{root.KeyDown-=Escape;Activated-=Blur;};
    }
}

