using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private DesktopTiles? overviewMetrics,overviewModes,overviewTools,overviewSwitches;
    private Button? overviewBoost,overviewLowRefresh;
    private ProgressBar? overviewFanBar;
    private readonly Dictionary<int,TextBlock> overviewModeNotes=[];
    private readonly Dictionary<int,bool> overviewModeAccent=[];
    private readonly List<Button> overviewPending=[];

    private void ResetOverviewBindings()
    {
        overviewMetrics=null;overviewModes=null;overviewTools=null;overviewSwitches=null;
        overviewBoost=null;overviewLowRefresh=null;overviewFanBar=null;
        overviewModeNotes.Clear();overviewModeAccent.Clear();overviewPending.Clear();extraSwitchButtons.Clear();overviewGpuBar=null;overviewVramBar=null;
    }
    private void ArrangeDesktopViewport()
    {
        if(mini)
        {
            if(ReferenceEquals(scroll.Content,desktopSurface)){desktopSurface.Children.Clear();scroll.Content=page;}
            page.RenderTransform=null;
            navigation.ClearValue(Control.HorizontalContentAlignmentProperty);navigation.ClearValue(Control.VerticalContentAlignmentProperty);
            scroll.ClearValue(Control.HorizontalContentAlignmentProperty);scroll.ClearValue(Control.VerticalContentAlignmentProperty);
            page.Width=double.NaN;page.MaxWidth=1120;page.Margin=new Thickness(0);
            page.HorizontalAlignment=HorizontalAlignment.Stretch;page.VerticalAlignment=VerticalAlignment.Top;return;
        }
        if(!ReferenceEquals(scroll.Content,desktopSurface)){scroll.Content=null;desktopSurface.Children.Add(page);scroll.Content=desktopSurface;}
        UpdateDesktopNavigationMode();
        navigation.HorizontalContentAlignment=HorizontalAlignment.Stretch;navigation.VerticalContentAlignment=VerticalAlignment.Stretch;
        scroll.HorizontalContentAlignment=HorizontalAlignment.Stretch;scroll.VerticalContentAlignment=VerticalAlignment.Top;
        page.HorizontalAlignment=HorizontalAlignment.Stretch;page.VerticalAlignment=VerticalAlignment.Top;page.Margin=new Thickness(0);page.Width=double.NaN;
        double maximum=currentPage=="plugins"?1120:1536;
        page.MaxWidth=maximum;desktopSurface.MaximumContentWidth=maximum;desktopSurface.CenterVertically=currentPage=="overview";desktopSurface.ViewportHeight=Math.Max(0,scroll.ActualHeight);
        double padding=scroll.ActualWidth>0&&scroll.ActualWidth<1100?20:currentPage=="settings"?28:24;
        double top=navigation.PaneDisplayMode==NavigationViewPaneDisplayMode.LeftMinimal?64:currentPage is "overview" or "plugins" or "settings"?padding:20;
        if(page.Padding.Left!=padding||page.Padding.Top!=top)page.Padding=new Thickness(padding,top,padding,padding);
        page.Spacing=padding==20?12:currentPage=="settings"?18:16;desktopSurface.InvalidateMeasure();
    }
    private FrameworkElement OverviewHeader()
    {
        var meter=PageIcon("overview");
        var badge=new Border{Style=(Style)Application.Current.Resources["DetailBadgeStyle"],Child=meter};
        var title=Ui.Text("状态总览",28,true);title.VerticalAlignment=VerticalAlignment.Center;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14,Margin=new Thickness(0,0,0,10)};
        row.Children.Add(badge);row.Children.Add(title);row.Tag=new DesktopAlignment(badge,title,"overview-page-header");return row;
    }
    private static DesktopTiles OverviewGrid(int preferred,double minimum,params FrameworkElement[] items)
    {
        var grid=new DesktopTiles{PreferredColumns=preferred,MinimumItemWidth=minimum,PairFourColumns=true,QuickSwitchColumns=preferred>=10,ColumnSpacing=16,RowSpacing=16};
        foreach(var item in items){item.HorizontalAlignment=HorizontalAlignment.Stretch;item.VerticalAlignment=VerticalAlignment.Stretch;grid.Children.Add(item);}return grid;
    }
    private static Border OverviewCard(UIElement content)
    {
        var card=Ui.Card(content);card.Padding=new Thickness(18);return card;
    }
    private static TextBlock OverviewText(string text,double size=14,bool strong=false,bool muted=false)
    {
        string brush=muted?"OverviewMuted":"OverviewInk";
        var block=(TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{{ThemeResource {brush}}}'/>");
        block.Text=text;block.FontSize=Design.FontSize(size);block.FontFamily=new FontFamily("Segoe UI");block.TextWrapping=TextWrapping.Wrap;
        block.FontWeight=strong?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;return block;
    }
    private TextBlock OverviewValue(string key,double size)
    {var block=OverviewText("—",size,size>=28);live[key]=block;return block;}
    private static Border OverviewBadge(FrameworkElement text)
    {
        var badge=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='8,3' CornerRadius='5' Background='{ThemeResource SubtleFillColorSecondaryBrush}'/>");
        badge.Child=text;return badge;
    }
    private FrameworkElement OverviewNumber(FrameworkElement value,string unit,string? unitKey=null)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=2,VerticalAlignment=VerticalAlignment.Bottom};
        row.Children.Add(value);var label=OverviewText(unit,12,muted:true);if(unitKey!=null)live[unitKey]=label;label.Margin=new Thickness(0,0,0,5);label.VerticalAlignment=VerticalAlignment.Bottom;row.Children.Add(label);return row;
    }
    private static Grid OverviewPair(FrameworkElement left,FrameworkElement right)
    {
        var grid=new Grid{ColumnSpacing=10};
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1)});
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var divider=new Border{Width=1,Height=18,Background=(Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]};
        grid.Children.Add(left);Grid.SetColumn(divider,1);grid.Children.Add(divider);
        Grid.SetColumn(right,2);right.HorizontalAlignment=HorizontalAlignment.Right;grid.Children.Add(right);return grid;
    }
    private FrameworkElement OverviewTelemetryCard(string title,string glyph,FrameworkElement numbers,ProgressBar bar,string footer,FrameworkElement badge,bool power=false,string? powerKey=null)
    {
        var trailing=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        if(power){var watts=powerKey==null?OverviewText("— W",12,muted:true):OverviewValue(powerKey,12);watts.VerticalAlignment=VerticalAlignment.Center;trailing.Children.Add(watts);}
        var symbol=OverviewTelemetryIcon(glyph);symbol.VerticalAlignment=VerticalAlignment.Center;trailing.Children.Add(symbol);
        var head=DesktopAlignedRow(OverviewText(title,13,muted:true),trailing,"telemetry-heading");
        // A shared four-row layout keeps bars and footers on the same line
        // even when the fan value area contains an extra CPU/GPU label row.
        var body=new Grid{RowSpacing=12};
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        body.Children.Add(head);Grid.SetRow(numbers,1);body.Children.Add(numbers);
        Grid.SetRow(bar,2);body.Children.Add(bar);
        var foot=DesktopAlignedRow(OverviewText(footer,12,muted:true),OverviewBadge(badge),"telemetry-footer");
        Grid.SetRow(foot,3);body.Children.Add(foot);numbers.VerticalAlignment=VerticalAlignment.Bottom;
        numbers.Margin=new Thickness(0,8,0,-8);
        var card=OverviewCard(body);card.MinHeight=168;return card;
    }
    private void RenderReplicaOverview()
    {
        detailSectionTitles.AddRange(["运行监测","性能释放模式","快捷设备硬件开关"]);
        desktopCpuBar=new ProgressBar{Minimum=0,Maximum=100,Height=4};
        var cpu=OverviewTelemetryCard("CPU 处理器状态","\uE950",
            OverviewPair(OverviewNumber(OverviewValue("overviewCpuTemp",30),"°C"),OverviewNumber(OverviewValue("overviewCpuMhz",30),"MHz")),desktopCpuBar,
            identity.Processor.Replace("(R)","").Replace("(TM)","").Replace("Intel Core","Intel Core "),OverviewValue("overviewCpuUsage",12),true,"overviewCpuPower");
        ToolTipService.SetToolTip(cpu,"温度为Windows处理器温区；频率和功耗来自Core。无能耗计时沿用原版功耗模型并标注估算。");
        overviewGpuBar=new ProgressBar{Height=4,Minimum=0,Maximum=100};
        var gpu=OverviewTelemetryCard("GPU 独立显卡","pulse",
            OverviewPair(OverviewNumber(OverviewValue("overviewGpuTemp",30),"°C"),OverviewNumber(OverviewValue("overviewGpuMhz",30),"MHz")),overviewGpuBar,
            identity.Gpus.FirstOrDefault(g=>g.Contains("NVIDIA",StringComparison.OrdinalIgnoreCase))?.Replace("NVIDIA GeForce ","").Replace(" Laptop GPU","")??"独立显卡",OverviewValue("overviewGpuUsage",12),true,"overviewGpuPower");
        ToolTipService.SetToolTip(gpu,"Core按需读取NVIDIA驱动的温度、实际核心时钟、功耗和占用率；离电或休眠时暂停独显查询。");
        var cpuFan=Ui.Stack(Ui.Text("CPU 扇",12,muted:true),OverviewNumber(OverviewValue("overviewCpuFan",30),"RPM"));cpuFan.Spacing=3;
        var gpuFan=Ui.Stack(Ui.Text("GPU 扇",12,muted:true),OverviewNumber(OverviewValue("overviewGpuFan",30),"RPM"));gpuFan.Spacing=3;
        overviewFanBar=new ProgressBar{Minimum=0,Maximum=100,Height=4};
        var fans=OverviewTelemetryCard("散热系统状态","\uE9D9",OverviewPair(cpuFan,gpuFan),overviewFanBar,"双风扇散热",OverviewValue("overviewFanMode",12));
        overviewVramBar=new ProgressBar{Height=4,Minimum=0,Maximum=100};
        var vram=OverviewTelemetryCard("VRAM 显存容量","sparkles",
            OverviewPair(OverviewNumber(OverviewValue("overviewVramUsed",30),"/ — GB","overviewVramTotal"),OverviewNumber(OverviewValue("overviewMemoryMhz",30),"MHz")),overviewVramBar,
            "专用显存 (VRAM)",OverviewValue("overviewVramUsage",12));
        ToolTipService.SetToolTip(vram,"Core读取独显专用显存已用/总量与实际显存时钟；GB按原版以1024³字节显示，MHz不乘等效传输倍率。");
        overviewMetrics=OverviewGrid(4,220,cpu,gpu,fans,vram);page.Children.Add(overviewMetrics);

        var modeButtons=new List<FrameworkElement>();
        string[] names=["办公","均衡","狂暴","自定义"],glyphs=["\uE70F","\uE945","\uEC4A","\uE713"],notes=["静音","均衡","狂暴","极客"];
        for(int i=0;i<4;i++)
        {
            int mode=i+1;var note=Ui.Text(notes[i],12,muted:true);overviewModeNotes[mode]=note;
            note.TextAlignment=TextAlignment.Center;
            bool selected=hardware.CurrentMode==mode;overviewModeAccent[mode]=selected;
            var icon=DetailGlyph(glyphs[i],24,selected);var label=Ui.Text(names[i],14);icon.HorizontalAlignment=HorizontalAlignment.Center;label.TextAlignment=TextAlignment.Center;
            var content=Ui.Stack(icon,label,note);content.Spacing=6;content.HorizontalAlignment=HorizontalAlignment.Center;
            var button=new Button{Content=content,Tag="detail-choice",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,MinHeight=100,Padding=new Thickness(10),CornerRadius=new CornerRadius(8)};
            button.Click+=async(_,_)=>{if(mode==4){OpenTuning();return;}if(hardware.Active)await RunHardware(()=>hardware.ApplyModeCommand(upstream,mode,mode==3),"性能档位已回读确认。");};
            button.IsEnabled=mode==4||hardware.Active&&!hardwareBusy;SetChoiceVisual(button,hardware.CurrentMode==mode);
            performanceButtons[mode]=button;modeButtons.Add(button);
        }
        overviewModes=OverviewGrid(4,145,modeButtons.ToArray());overviewModes.ColumnSpacing=10;
        var heading=Ui.Stack(Ui.Text("性能释放模式 (Power Profiles)",16,true),OverviewValue("overviewPerformance",12));heading.Spacing=2;
        var powerTarget=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='10,4' CornerRadius='20' BorderThickness='1' Background='{ThemeResource DetailBadgeFill}' BorderBrush='{ThemeResource ChoiceStroke}'/>");powerTarget.Child=OverviewValue("overviewPowerTarget",12);
        var undo=MiniControl("撤销上次档位",()=>hardware.UndoPerformance(),"恢复上一次性能设置");undo.Padding=new Thickness(12,6,12,6);
        var powerActions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};powerActions.Children.Add(powerTarget);powerActions.Children.Add(undo);
        var powerHead=OverviewGrid(2,300,heading,powerActions);powerActions.HorizontalAlignment=HorizontalAlignment.Right;powerActions.VerticalAlignment=VerticalAlignment.Center;
        var power=Ui.Stack(powerHead,overviewModes);power.Spacing=12;page.Children.Add(OverviewCard(power));

        overviewLowRefresh=Ui.Button("关闭",async(_,_)=>{lowRefreshOnBattery=!lowRefreshOnBattery;UpdateOverviewValues();await SavePreferences();await CheckAutomaticRefresh(true);});
        var refreshHost=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        foreach(int rate in displayState?.Rates.Distinct().Order()??Enumerable.Empty<int>())
        {var b=Ui.Button($"{rate}Hz",async(_,_)=>await ChangeRefresh(rate));b.Padding=new Thickness(8,6,8,6);refreshButtons[rate]=b;refreshHost.Children.Add(b);}
        if(refreshHost.Children.Count==0)refreshHost.Children.Add(Ui.Text("待读取",12,muted:true));
        var low=OverviewInlineControl("\uE945","拔电自动低刷","离电续航最大化",overviewLowRefresh);
        var refresh=OverviewInlineControl("\uE7F4","屏幕刷新率",displayState is {} ds?$"{ds.Width} × {ds.Height} · 内置屏":"内置屏未激活",refreshHost);
        overviewTools=OverviewGrid(2,300,low,refresh);overviewTools.ColumnSpacing=12;overviewTools.RowSpacing=12;page.Children.Add(overviewTools);

        var quickHeading=DesktopAlignedRow(Ui.Text("▏ 快捷设备硬件开关",14),Ui.Text("原厂热键与外设直控",12,muted:true));
        var switches=new List<FrameworkElement>();
        foreach(var(key,title,glyph)in new[]{("boost","一键强冷","\uE9D9"),("wifi","WIFI","\uE701"),("bluetooth","蓝牙","\uE702"),("touchpad","触摸板","\uEFA5"),("camera","摄像头","\uE706"),("mic","麦克风","\uE720"),("osd","OSD显示","\uE7F4"),("win","Win键锁","\uE782"),("fn","F1-F12","\uE765"),("awake","防休眠","\uE708"),("screen","关闭屏幕","\uE708")})
        {
            Button b;
            if(key is "camera" or "mic" or "win" or "awake" or "screen")b=QuickActionButton(key);
            else if(key=="boost")
            {
                b=overviewBoost=Ui.Button("\uE9D9\n一键强冷",async(_,_)=>{if(hardware.Active)await RunHardware(()=>hardware.SetFanBoost(!(hardware.State?.FanBoost??ec.FanBoost??false)),"强冷状态已回读确认。");});
            }
            else b=ExtraSwitchButton(key,title,glyph);
            b.MinHeight=90;b.MinWidth=0;b.Padding=new Thickness(4,12,4,12);b.HorizontalContentAlignment=HorizontalAlignment.Center;switches.Add(b);
        }
        overviewSwitches=OverviewGrid(11,90,switches.ToArray());overviewSwitches.ColumnSpacing=8;
        var quick=Ui.Stack(quickHeading,overviewSwitches);quick.Spacing=12;page.Children.Add(quick);UpdateExtraSwitchButtons();UpdateOverviewValues();
    }
    private static FrameworkElement OverviewTelemetryIcon(string key)
    {
        string? data=key switch
        {
            "pulse"=>"M2,13 L7,13 L10,3 L15,22 L18,10 L22,10",
            "sparkles"=>"M14,3 L16,9 L22,11 L16,13 L14,19 L12,13 L6,11 L12,9 Z M5,2 L6,5 L9,6 L6,7 L5,10 L4,7 L1,6 L4,5 Z M4,16 L5,19 L8,20 L5,21 L4,24 L3,21 L0,20 L3,19 Z",
            _=>null
        };
        return data==null?DetailGlyph(key,22):(FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='22' Height='22'><Canvas Width='24' Height='24'><Path Data='{data}' Stroke='{{ThemeResource OverviewInk}}' StrokeThickness='1.7' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/></Canvas></Viewbox>");
    }
    private static Border OverviewInlineControl(string glyph,string title,string detail,FrameworkElement action)
    {
        var labels=Ui.Stack(Ui.Text(title,13),Ui.Text(detail,11,muted:true));labels.Spacing=2;
        var leading=new Grid{ColumnSpacing=10,VerticalAlignment=VerticalAlignment.Center};leading.ColumnDefinitions.Add(new(){Width=new GridLength(24)});leading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var icon=DetailGlyph(glyph,22);icon.VerticalAlignment=VerticalAlignment.Center;labels.VerticalAlignment=VerticalAlignment.Center;
        leading.Children.Add(icon);Grid.SetColumn(labels,1);leading.Children.Add(labels);leading.Tag=new DesktopAlignment(icon,labels,"overview-row-icon");
        var row=DesktopAlignedRow(leading,action);row.ColumnSpacing=8;
        var card=OverviewCard(row);card.Padding=new Thickness(12);return card;
    }
    private void UpdateOverviewValues()
    {
        if(overviewMetrics==null)return;
        var metrics=DateTimeOffset.Now-overviewTelemetryAt<TimeSpan.FromSeconds(6)?overviewTelemetry:null;
        var cpu=metrics?.Cpu;var gpu=metrics?.Gpu;
        Put("overviewCpuMhz",cpu?.Mhz?.ToString()??"—");
        Put("overviewCpuPower",cpu?.PowerW is double cw?$"{(cpu.PowerEstimated?"估算 ":"")}{cw:0.0} W":"— W");
        Put("overviewGpuMhz",gpu?.CoreMhz?.ToString()??"—");Put("overviewMemoryMhz",gpu?.MemoryMhz?.ToString()??"—");
        Put("overviewGpuPower",gpu?.PowerW is double gw?$"{gw:0.0} W":"— W");Put("overviewGpuUsage",gpu?.Usage is int gu?$"{gu}%":"—%");
        Put("overviewVramUsed",gpu?.UsedGiB is double used?$"{used:0.0}":"—");Put("overviewVramTotal",gpu?.TotalGiB is double total?$"/ {total:0.0} GB":"/ — GB");
        Put("overviewVramUsage",gpu?.MemoryPercent is double mp?$"{mp:0}%":"—%");
        if(overviewGpuBar!=null){overviewGpuBar.Value=gpu?.Usage??0;overviewGpuBar.Opacity=gpu?.Usage.HasValue==true?1:.35;}
        if(overviewVramBar!=null){overviewVramBar.Value=gpu?.MemoryPercent??0;overviewVramBar.Opacity=gpu?.HasMemory==true?1:.35;}
        Put("overviewCpuTemp",temperature.Cpu?.ToString()??"—");Put("overviewGpuTemp",temperature.Gpu?.ToString()??"—");
        Put("overviewCpuUsage",latest?.CpuPercent is double cpuUsage?$"{cpuUsage:0}%":"—%");
        Put("overviewCpuFan",ec.CpuFan?.ToString()??"—");Put("overviewGpuFan",ec.GpuFan?.ToString()??"—");
        bool boost=(hardware.State?.FanBoost??ec.FanBoost)==true;
        Put("overviewFanMode",boost?"强冷模式":ec.CpuFan.HasValue?"自动温控":"读取中");
        // No fan percentage is inferred from arbitrary RPM or temperature limits.
        if(overviewFanBar!=null){overviewFanBar.Value=boost?100:0;overviewFanBar.Opacity=boost?1:.35;}
        Put("overviewPerformance",$"当前配置：{PerformanceName}模式 · "+(hardware.CurrentMode switch{1=>"低噪办公",2=>"日常娱乐与工作，平稳释放兼顾流畅散热",3=>"优先性能释放",4=>"使用自定义 CPU、GPU 与风扇配置",_=>"正在读取"}));
        string[] notes=["静音","均衡","狂暴","极客"];
        foreach(var(mode,note)in overviewModeNotes)note.Text=hardware.CurrentMode==mode?"● 当前":notes[mode-1];
        string[] glyphs=["\uE70F","\uE945","\uEC4A","\uE713"];
        foreach(var(mode,button)in performanceButtons)
        {
            button.IsEnabled=hardware.Active&&!hardwareBusy&&(mode==4?hardware.SupportsCustomTuning:hardware.SupportsPerformance);
            bool selected=hardware.CurrentMode==mode;
            if(overviewModeAccent.TryGetValue(mode,out bool old)&&old!=selected&&button.Content is StackPanel body)
            {body.Children.RemoveAt(0);body.Children.Insert(0,DetailGlyph(glyphs[mode-1],24,selected));overviewModeAccent[mode]=selected;}
        }
        UpdateOverviewPowerTarget();
        if(overviewBoost!=null){overviewBoost.IsEnabled=hardware.Active&&!hardwareBusy&&hardware.SupportsFanBoost;SetChoiceVisual(overviewBoost,boost);}
        if(overviewLowRefresh!=null){overviewLowRefresh.Content=lowRefreshOnBattery?"开启":"关闭";SetChoiceVisual(overviewLowRefresh,lowRefreshOnBattery);}
    }
}
