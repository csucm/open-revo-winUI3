using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

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
        overviewPowerTooltip = null;
        overviewBoard = null;
        overviewMetrics=null;overviewModes=null;overviewTools=null;overviewSwitches=null;
        overviewBoost=null;overviewLowRefresh=null;overviewFanBar=null;
        overviewModeNotes.Clear();overviewModeAccent.Clear();overviewPending.Clear();extraSwitchButtons.Clear();overviewGpuBar=null;overviewVramBar=null;
    }
    private void ArrangeDesktopViewport()
    {
        // Keep one parent for the page so connected source visuals remain valid.
        if(!ReferenceEquals(scroll.Content,desktopSurface)){scroll.Content=null;desktopSurface.Children.Add(page);scroll.Content=desktopSurface;}
        if(mini)
        {
            desktopSurface.CenterVertically=false;desktopSurface.MaximumContentWidth=1120;desktopSurface.ViewportHeight=0;
            navigation.HorizontalContentAlignment=HorizontalAlignment.Stretch;navigation.VerticalContentAlignment=VerticalAlignment.Stretch;
            scroll.ClearValue(Control.HorizontalContentAlignmentProperty);scroll.ClearValue(Control.VerticalContentAlignmentProperty);
            page.Width=double.NaN;page.MaxWidth=1120;page.Margin=new Thickness(0);
            page.HorizontalAlignment=HorizontalAlignment.Stretch;page.VerticalAlignment=VerticalAlignment.Top;return;
        }
        UpdateDesktopNavigationMode();
        navigation.HorizontalContentAlignment=HorizontalAlignment.Stretch;navigation.VerticalContentAlignment=VerticalAlignment.Stretch;
        scroll.HorizontalContentAlignment=HorizontalAlignment.Stretch;scroll.VerticalContentAlignment=VerticalAlignment.Top;
        page.HorizontalAlignment=HorizontalAlignment.Stretch;page.VerticalAlignment=VerticalAlignment.Top;page.Margin=new Thickness(0);page.Width=double.NaN;
        double maximum=1280;
        page.MaxWidth=maximum;desktopSurface.MaximumContentWidth=maximum;desktopSurface.CenterVertically=false;desktopSurface.ViewportHeight=Math.Max(0,scroll.ActualHeight);
        double padding=scroll.ActualWidth>0&&scroll.ActualWidth<720?18:24;
        double top=padding;
        if(page.Padding.Left!=padding||page.Padding.Top!=top)page.Padding=new Thickness(padding,top,padding,padding);
        page.Spacing=16;
    }
    private FrameworkElement OverviewHeader()=>Ui.Row(DetailHeader("状态总览","运行状态与常用控制，一眼掌握。","overview"),OverviewEditActions());
    private static DesktopTiles OverviewGrid(int preferred,double minimum,params FrameworkElement[] items)
    {
        var grid=new DesktopTiles{PreferredColumns=preferred,MinimumItemWidth=minimum,PairFourColumns=true,QuickSwitchColumns=preferred>=10,ColumnSpacing=16,RowSpacing=16};
        foreach(var item in items){item.HorizontalAlignment=HorizontalAlignment.Stretch;item.VerticalAlignment=VerticalAlignment.Stretch;grid.Children.Add(item);}return grid;
    }
    private static Border OverviewCard(UIElement content)
    {
        var card=Ui.Card(content);card.Padding=new Thickness(16);return card;
    }
    private static TextBlock OverviewText(string text,double size=14,bool strong=false,bool muted=false)
    {
        string brush=muted?"OverviewMuted":"OverviewInk";
        var block=(TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{{ThemeResource {brush}}}'/>");
        block.Text=text;block.FontSize=Design.FontSize(size);block.LineHeight=size==24?28.8:Design.FontSize(size)*1.5;block.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;block.FontFamily=new FontFamily("Segoe UI");block.TextWrapping=TextWrapping.Wrap;
        block.FontWeight=strong?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;return block;
    }
    private TextBlock OverviewValue(string key,double size)
    {var block=OverviewText("—",size,false);live[key]=block;return block;}
    private static Border OverviewBadge(FrameworkElement text)
    {
        var badge=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='8,3' CornerRadius='5' Background='{ThemeResource SubtleFillColorSecondaryBrush}'/>");
        badge.Child=text;return badge;
    }
    private FrameworkElement OverviewNumber(FrameworkElement value,string unit,string? unitKey=null)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=2,VerticalAlignment=VerticalAlignment.Bottom};
        row.Children.Add(value);var label=OverviewText(unit,12,muted:true);if(unitKey!=null)live[unitKey]=label;label.Margin=new Thickness(0);label.VerticalAlignment=VerticalAlignment.Bottom;row.Children.Add(label);return row;
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
    private void RenderReplicaOverview()=>RenderApprovedOverview();
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
    private bool OverviewTelemetryFresh => residentClient != null
        ? residentOverview?.Fresh == true : overviewSampling.ReadState().IsFresh;
    private OverviewGpu? CurrentOverviewGpu =>
        overviewTelemetry is { } data && (residentClient != null ? data.GpuSampled : data.TopologyGeneration == DisplayTopology.GpuGeneration)
            ? data.Gpu : null;

    private string? IntegratedGpuModel =>
        CurrentOverviewGpu is { ShowsSharedMemory: true, AdapterName: { } name } ? name : null;

    private bool OverviewUsesSharedMemory => CurrentOverviewGpu?.ShowsSharedMemory == true || PaintIntegratedOnly;
    private string OverviewMemoryTitle => OverviewUsesSharedMemory ? "共享 GPU 内存" : "显存";
    private string OverviewMemoryUnit => "GB";
    private const double BytesPerGb = 1_000_000_000d;
    private string OverviewMemoryNote => OverviewUsesSharedMemory
        ? CurrentOverviewGpu is { TotalGiB: not null, MemoryTotalBytes: ulong reserved } ? $"专用预留 {reserved / BytesPerGb:0.0} GB" : ""
        : "专用显存";

    private void UpdateOverviewValues()
    {
        RefreshPaintedState();
        if(mini||currentPage!="overview")return;
        var metrics=overviewTelemetry;
        var cpu=metrics?.Cpu;var gpu=CurrentOverviewGpu;
        Put("overviewCpuMhz",cpu?.Mhz?.ToString()??"—");
        Put("overviewCpuPower",cpu?.PowerW is double cw?$"{(cpu.PowerEstimated?"估算 ":"")}{cw:0.0} W":"— W");
        Put("overviewGpuMhz",gpu?.CoreMhz?.ToString()??"—");Put("overviewMemoryMhz",gpu?.MemoryMhz?.ToString()??"—");
        if (live.TryGetValue("overviewGpuTitle", out var gpuTitle) && gpuTitle.Text != (gpu?.AdapterName ?? "GPU"))
        {
            gpuTitle.Text = gpu?.AdapterName ?? "GPU";
            ToolTipService.SetToolTip(gpuTitle, gpu?.AdapterName);
        }
        Put("overviewGpuPower",gpu?.PowerW is double gw?$"{gw:0.0} W":"— W");Put("overviewGpuUsage",gpu?.Usage is int gu?$"{gu}%":"—%");
        double? used = OverviewUsesSharedMemory
            ? gpu is { ShowsSharedMemory: true, DisplayMemoryUsedGiB: not null } ? gpu.SharedUsedBytes / BytesPerGb : null
            : gpu?.UsedGiB;
        double? total = OverviewUsesSharedMemory
            ? gpu is { ShowsSharedMemory: true, SharedLimitBytes: > 0 } ? gpu.SharedLimitBytes / BytesPerGb : null
            : gpu?.TotalGiB;
        Put("overviewVramUsed",used is double usedValue?$"{usedValue:0.0}":"—");
        Put("overviewVramTotal",$"/ {(total is double totalValue ? $"{totalValue:0.0}" : "—")} {OverviewMemoryUnit}");
        Put("overviewVramUsage",gpu?.DisplayMemoryPercent is double mp?$"{mp:0}%":"—%");
        Put("overviewVramTitle",OverviewMemoryTitle);
        Put("overviewVramNote",OverviewMemoryNote);
        if(overviewGpuBar!=null){overviewGpuBar.Value=gpu?.Usage??0;overviewGpuBar.Opacity=gpu?.Usage.HasValue==true?1:.35;}
        if(overviewVramBar!=null){overviewVramBar.Value=gpu?.DisplayMemoryPercent??0;overviewVramBar.Opacity=gpu?.DisplayMemoryPercent.HasValue==true?1:.35;}
        Put("overviewCpuTemp",temperature.Cpu?.ToString()??"—");Put("overviewGpuTemp",gpu?.Temperature?.ToString()??"—");
        Put("overviewCpuUsage",latest?.CpuPercent is double cpuUsage?$"{cpuUsage:0}%":"—%");
        Put("overviewCpuFan",ec.CpuFan?.ToString()??"—");Put("overviewGpuFan",ec.GpuFan?.ToString()??"—");
        bool boost=PanelFanBoost==true;
        Put("overviewFanMode",boost?"强冷模式":ec.CpuFan.HasValue?"自动温控":"读取中");
        // No fan percentage is inferred from arbitrary RPM or temperature limits.
        if(overviewFanBar!=null){overviewFanBar.Value=boost?100:0;overviewFanBar.Opacity=boost?1:.35;}
        Put("overviewPerformance",$"当前配置：{PerformanceName}模式 · "+(PanelMode switch{1=>"低噪办公",2=>"日常娱乐与工作，平稳释放兼顾流畅散热",3=>"优先性能释放",4=>"使用自定义 CPU、GPU 与风扇配置",_=>"正在读取"}));
        string[] notes=["静音","均衡","狂暴","极客"];
        foreach(var(mode,note)in overviewModeNotes)note.Text=PanelMode==mode?"● 当前":notes[mode-1];
        string[] glyphs=["\uE70F","\uE945","\uEC4A","\uE713"];
        foreach(var(mode,button)in performanceButtons)
        {
            button.IsEnabled=PanelHardwareActive&&!PanelTuningBusy&&(mode==4?PanelSupportsCustom:PanelSupportsPerformance);
            bool selected=PanelMode==mode;
            if(overviewModeAccent.TryGetValue(mode,out bool old)&&old!=selected&&button.Content is StackPanel body)
            {body.Children.RemoveAt(0);body.Children.Insert(0,ApprovedIcons.Create(mode switch{1=>"mode-office",2=>"mode-balanced",3=>"mode-performance",_=>"mode-custom"},mini?28:mode==4?34:24,selected,mini||mode!=4));overviewModeAccent[mode]=selected;}
        }
        Put("approvedBatteryLimit",PanelBatteryLimit is int batteryLimit?batteryLimit+"%":"—");if(approvedBatteryMiniFill!=null)approvedBatteryMiniFill.RenderTransform=new ScaleTransform{ScaleX=(latest?.BatteryPercent??0)/100d,ScaleY=1};
        Put("overviewProfile",PerformanceName);Put("overviewRefresh",displayState is {} ds?ds.Hertz+" Hz":"— Hz");
        UpdateOverviewPowerTarget();
        if(overviewBoost!=null){overviewBoost.IsEnabled=PanelHardwareActive&&!PanelHardwareActionBusy(PanelOperation.ToggleFanBoost)&&PanelSupportsBoost;UpdateApprovedQuickButton(overviewBoost,"boost",PanelFanBoost);}
        if(overviewLowRefresh!=null){Put("approvedAutoRefresh",lowRefreshOnBattery?"已开启":"已关闭");SetChoiceVisual(overviewLowRefresh,lowRefreshOnBattery);}
    }
}

