using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using OpenRevo.Core;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private List<string> overviewSaved=[..OverviewLayout.Load()];
    private List<string>? overviewDraft;
    private OverviewBoard? overviewBoard;
    private readonly Dictionary<string,string> tileTitles=new(){["cpu"]="CPU",["gpu"]="GPU",["vram"]="显存",["fan"]="散热",["performance"]="性能模式",["battery"]="电源与电池",["display"]="屏幕"};
    private FrameworkElement OverviewEditActions()
    {
        if(overviewDraft==null){var edit=Ui.Button("编辑磁贴",(_,_)=>{overviewDraft=[..overviewSaved];Render();});edit.MinHeight=36;edit.Padding=new Thickness(10,5,10,5);edit.Content=SettingsControls(ApprovedIcons.Create("edit-tiles",24),Ui.Text("编辑磁贴"));return edit;}
        var save=IconAction("save-layout","保存磁贴布局",async()=>
        {
            try{var next=overviewDraft!.ToArray();if(!uiReviewMode)await Task.Run(()=>OverviewLayout.Save(next));overviewSaved=[..next];overviewDraft=null;Render();}
            catch(Exception error){await ShowMessage("布局未保存",error.Message);}
        });
        var cancel=IconAction("cancel-layout","取消编辑磁贴",()=>{overviewDraft=null;Render();return Task.CompletedTask;});
        return SettingsControls(save,cancel);
    }
    private static Button IconAction(string icon,string name,Func<Task> action)
    {
        var b=Ui.Button(name,async(_,_)=>await action());b.Content=ApprovedIcons.Create(icon);b.Width=b.Height=34;b.MinWidth=b.MinHeight=0;b.Padding=new Thickness(0);b.CornerRadius=new CornerRadius(11);b.BorderThickness=new Thickness(0);b.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);return b;
    }
    private FrameworkElement Sensor(string id)
    {
        if(id=="fan")return OverviewCard(SuiteVisuals.Stack(8,Ui.Text("散热",14,true),Ui.Row(Ui.Text("CPU 风扇",12,muted:true),OverviewNumber(OverviewValue("overviewCpuFan",16),"RPM")),Ui.Row(Ui.Text("GPU 风扇",12,muted:true),OverviewNumber(OverviewValue("overviewGpuFan",16),"RPM"))));
        string prefix=id=="vram"?"overviewMemory":"overview"+(id=="cpu"?"Cpu":"Gpu");
        var mhz=OverviewValue(prefix+"Mhz",12);var head=Ui.Row(Ui.Text(tileTitles[id],14,true),OverviewNumber(mhz,"MHz"));
        var number=id=="vram"?OverviewNumber(OverviewValue("overviewVramUsed",24),"/ — GB","overviewVramTotal"):OverviewNumber(OverviewValue(prefix+"Temp",24),"°C");
        var foot=Ui.Row(OverviewValue(id=="vram"?"overviewVramUsage":prefix+"Usage",12),id=="vram"?Ui.Text("专用显存",12,muted:true):OverviewValue(prefix+"Power",12));
        var bar=new ProgressBar{Minimum=0,Maximum=100,Height=3};if(id=="cpu")desktopCpuBar=bar;else if(id=="gpu")overviewGpuBar=bar;else overviewVramBar=bar;
        var card=OverviewCard(SuiteVisuals.Stack(8,head,number,foot,bar));card.MinHeight=120;return card;
    }
    private FrameworkElement OverviewTile(string id)
    {
        if(id is "cpu" or "gpu" or "vram" or "fan")return Sensor(id);
        if(id=="performance")
        {
            var heading=SettingsControls(ApprovedIcons.Create("performance",24),Ui.Text("性能模式",16,true));
            var target=(TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontSize='12' TextWrapping='Wrap' Foreground='{ThemeResource SemanticBlue}'/>");live["overviewPowerTarget"]=target;target.HorizontalAlignment=HorizontalAlignment.Right;target.TextAlignment=TextAlignment.Right;
            var value=OverviewValue("overviewProfile",14);var content=new Grid{RowSpacing=8};content.RowDefinitions.Add(new(){Height=GridLength.Auto});content.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});content.RowDefinitions.Add(new(){Height=GridLength.Auto});content.Children.Add(heading);var meta=Ui.Row(value,target);meta.VerticalAlignment=VerticalAlignment.Center;Grid.SetRow(meta,1);content.Children.Add(meta);var modes=PerformanceChoices();Grid.SetRow(modes,2);content.Children.Add(modes);var card=OverviewCard(content);card.MinHeight=180;return PanelAnchor("performance",card);
        }
        if(id=="battery")
        {
            var head=SettingsControls(ApprovedIcons.Create("battery",24),Ui.Text("电源与电池",16,true));
            var value=Value("battery",24);value.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
            var sub=Value("batteryDetail",12);sub.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
            var limit=Ui.Button("充电上限",(_,_)=>{});var limitText=Value("approvedBatteryLimit",12);limitText.FontWeight=Microsoft.UI.Text.FontWeights.Normal;limit.Content=Ui.Row(Ui.Text("充电上限",12),SettingsControls(limitText,ApprovedIcons.Utility("chevron",13)));SuiteVisuals.Flat(limit);
            var choices=new MenuFlyout();foreach(int cap in new[]{100,80,60}){var item=new MenuFlyoutItem{Text=$"充电上限 {cap}%"};item.Click+=async(_,_)=>await RunHardware(()=>hardware.SetBatteryPreset(cap),"充电上限已回读确认。");choices.Items.Add(item);}limit.Flyout=choices;limit.IsEnabled=hardware.Active&&hardware.SupportsBattery;
            var plan=Ui.Button("Windows 电源计划",(_,_)=>{currentPage="settings";Render();});plan.Content=Ui.Row(Ui.Text("Windows 电源计划",12),ApprovedIcons.Utility("chevron",13));SuiteVisuals.Flat(plan);
            var controls=SuiteVisuals.Rule(SuiteVisuals.Stack(1,limit,plan),4);
            var glance=SettingsControls(MiniBatteryPicture(),value);glance.Spacing=12;
            return OverviewCard(TileBody(head,glance,sub,controls));
        }
        var refresh=Value("overviewRefresh",24);refresh.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
        var rates=new MenuFlyout();foreach(int rate in displayState?.Rates.Distinct().Order()??Enumerable.Empty<int>()){var item=new MenuFlyoutItem{Text=rate+" Hz"};item.Click+=async(_,_)=>await ChangeRefresh(rate);rates.Items.Add(item);}
        var choose=Ui.Button("调整刷新率",(_,_)=>{});choose.Flyout=rates;choose.IsEnabled=rates.Items.Count>0;choose.Content=Ui.Row(Ui.Text("调整刷新率",12),ApprovedIcons.Utility("chevron",13));SuiteVisuals.Flat(choose);
        overviewLowRefresh=Ui.Button("拔电低刷",async(_,_)=>{lowRefreshOnBattery=!lowRefreshOnBattery;await SavePreferences();await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(lowRefreshOnBattery?"已开启":"已关闭"),"refresh");UpdateOverviewValues();});
        var autoLabel=OverviewValue("approvedAutoRefresh",12);overviewLowRefresh.Content=Ui.Row(Ui.Text("拔电低刷",12),autoLabel);SuiteVisuals.Flat(overviewLowRefresh);
        return OverviewCard(TileBody(SettingsControls(ApprovedIcons.Create("display",24),Ui.Text("屏幕",16,true)),refresh,Ui.Text("当前刷新率",12,muted:true),SuiteVisuals.Rule(SuiteVisuals.Stack(1,choose,overviewLowRefresh),4)));
    }
    private Border? approvedBatteryMiniFill;
    private FrameworkElement MiniBatteryPicture()
    {
        var frame=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='44' Height='20' Padding='2' BorderBrush='{ThemeResource OverviewMuted}' BorderThickness='1.5' CornerRadius='5'/>");
        approvedBatteryMiniFill=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='36' Height='13' Background='{ThemeResource SemanticGreen}' CornerRadius='2' HorizontalAlignment='Left'/>");frame.Child=approvedBatteryMiniFill;return frame;
    }
    private static Grid TileBody(FrameworkElement header,FrameworkElement number,FrameworkElement caption,FrameworkElement controls)
    {
        var grid=new Grid{RowSpacing=8};foreach(var h in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})grid.RowDefinitions.Add(new(){Height=h});
        int row=0;foreach(var child in new[]{header,number,caption,controls}){Grid.SetRow(child,row++);grid.Children.Add(child);}if(controls is FrameworkElement tail)tail.VerticalAlignment=VerticalAlignment.Bottom;return grid;
    }
    private void RenderApprovedOverview()
    {
        overviewBoard=new OverviewBoard{Order=overviewDraft??overviewSaved};
        var ids=overviewDraft??overviewSaved;
        foreach(string id in ids)
        {
            var tile=new Grid{Tag=id};tile.Children.Add(OverviewTile(id));
            if(overviewDraft!=null)
            {
                tile.Children[0].IsHitTestVisible=false;
                tile.Loaded+=(_,_)=>{foreach(var control in DetailDescendants(tile.Children[0]).OfType<Control>())control.IsTabStop=false;};
                var remove=Ui.Button("−",(_,_)=>{overviewDraft.Remove(id);Render();});remove.Width=remove.Height=24;remove.MinWidth=remove.MinHeight=0;remove.Padding=new Thickness(0);remove.CornerRadius=new CornerRadius(12);remove.HorizontalAlignment=HorizontalAlignment.Left;remove.VerticalAlignment=VerticalAlignment.Top;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove,"移除 "+tileTitles[id]);tile.Children.Add(remove);
                var handle=Ui.Button("⠿",(_,_)=>{});handle.Width=handle.Height=24;handle.MinWidth=handle.MinHeight=0;handle.Padding=new Thickness(0);handle.HorizontalAlignment=HorizontalAlignment.Right;handle.VerticalAlignment=VerticalAlignment.Top;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle,"拖动 "+tileTitles[id]+"，Alt 加左右方向键排序");tile.Children.Add(handle);
                handle.KeyDown+=(_,e)=>{if((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)==0)return;if(e.Key is VirtualKey.Left or VirtualKey.Right){int from=overviewDraft.IndexOf(id),to=Math.Clamp(from+(e.Key==VirtualKey.Left?-1:1),0,overviewDraft.Count-1);overviewDraft.RemoveAt(from);overviewDraft.Insert(to,id);Render();e.Handled=true;}};
                AttachTileDrag(tile,id,remove);
            }
            overviewBoard.Children.Add(tile);
        }
        if(overviewDraft!=null)
        {
            var add=Ui.Button("＋\n添加面板",(_,_)=>{});add.MinHeight=100;var flyout=new MenuFlyout();
            foreach(string id in OverviewLayout.BuiltIns.Except(overviewDraft)){var item=new MenuFlyoutItem{Text=tileTitles[id]};item.Click+=(_,_)=>{overviewDraft.Add(id);Render();};flyout.Items.Add(item);}
            if(flyout.Items.Count==0)flyout.Items.Add(new MenuFlyoutItem{Text="内置磁贴均已添加；插件面板待适配",IsEnabled=false});add.Flyout=flyout;overviewBoard.Children.Add(add);
        }
        page.Children.Add(overviewBoard);page.Children.Add(Ui.Text("快捷控制",16,true));
        var groups=new List<FrameworkElement>();
        foreach(var(title,keys)in new[]{("键盘与触控",new[]{"touchpad","win","fn"}),("隐私与提示",new[]{"camera","mic","osd"}),("散热与唤醒",new[]{"boost","awake","screen"})})
        {
            var body=Ui.Stack();body.Spacing=0;FrameworkElement head=Ui.Text(title,16,true);
            if(title=="键盘与触控")head=Ui.Row(head,Ui.Button("灯光 ›",(_,_)=>{currentPage="devices";Render();}));body.Children.Add(head);
            foreach(string key in keys)
            {
                Button b;
                if(key is "camera" or "mic" or "win" or "awake" or "screen")b=QuickActionButton(key);
                else if(key=="boost")b=overviewBoost=Ui.Button("一键强冷",async(_,_)=>await RunHardware(()=>hardware.SetFanBoost(!(hardware.State?.FanBoost??ec.FanBoost??false)),"强冷状态已回读确认。"));
                else b=ExtraSwitchButton(key,key=="touchpad"?"触摸板":key=="fn"?"F1–F12 模式":"OSD 提示","");
                body.Children.Add(b);
            }
            groups.Add(OverviewCard(body));
        }
        var shortcuts=new DesktopTiles{PreferredColumns=3,MinimumItemWidth=0,SingleColumnBelow=639,ColumnSpacing=12,RowSpacing=12};foreach(var group in groups)shortcuts.Children.Add(group);page.Children.Add(shortcuts);UpdateExtraSwitchButtons();UpdateQuickActions();UpdateOverviewValues();
    }
    private void UpdateApprovedQuickButton(Button b,string key,bool? on,bool busy=false)
    {
        if(mini)return;
        string name=key switch{"touchpad"=>"触摸板","win"=>"Win 键锁","fn"=>"F1–F12 模式","camera"=>"相机","mic"=>"麦克风","osd"=>"OSD 提示","boost"=>"一键强冷","awake"=>"防休眠",_=>"关闭屏幕"};
        string sub=busy?"切换中":key=="screen"?"仅息屏，电脑继续运行":!on.HasValue?"状态未知":key=="win"?(on.Value?"已锁定":"已解锁"):key=="fn"?(on.Value?"Fn Lock 已开启":"Fn Lock 已关闭"):key=="boost"?(on.Value?"强冷已开启":"自动温控"):key=="awake"?(on.Value?"保持唤醒":"随系统休眠"):(on.Value?"已开启":"已关闭");
        string stamp="approved:"+key+":"+sub;if(Equals(b.Tag,stamp))return;b.Tag=stamp;
        string icon=key switch{"win"=>"win-lock","fn"=>"fn-lock","boost"=>"fan","screen"=>"display",_=>key};
        var labels=Ui.Stack(Ui.Text(name),Ui.Text(sub,12,muted:true));labels.Spacing=2;
        var leading=SettingsControls(ApprovedIcons.Create(icon,34,on==true||key=="screen"),labels);
        FrameworkElement tail=key=="screen"?Ui.Text("息屏",12):SuiteVisuals.SwitchIndicator(key switch{"win"=>"rose","fn"=>"violet","camera"=>"green","mic"=>"orange","boost"=>"cyan",_=>"blue"},on==true);
        b.Content=Ui.Row(leading,tail);b.MinWidth=0;b.MinHeight=46;b.Padding=new Thickness(0,4,0,4);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Stretch;b.BorderThickness=new Thickness(0);b.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);b.ClearValue(FrameworkElement.StyleProperty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b,name+"，"+sub);
    }
    private void AttachTileDrag(Grid tile,string id,Button remove)
    {
        bool held=false;Point grab=default;List<string>? start=null;uint pointer=0;
        void Finish(bool accept){if(!held)return;held=false;tile.ReleasePointerCaptures();ElementCompositionPreview.GetElementVisual(tile).Offset=Vector3.Zero;Canvas.SetZIndex(tile,0);if(!accept&&start!=null)overviewDraft=[..start];Render();}
        tile.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,e)=>{if(overviewDraft==null||e.OriginalSource is DependencyObject source&&IsWithin(source,remove))return;var p=e.GetCurrentPoint(tile);if(!p.Properties.IsLeftButtonPressed)return;grab=p.Position;held=tile.CapturePointer(e.Pointer);if(!held)return;pointer=e.Pointer.PointerId;start=[..overviewDraft];Canvas.SetZIndex(tile,10);e.Handled=true;}),true);
        tile.PointerMoved+=(_,e)=>{if(!held||e.Pointer.PointerId!=pointer||overviewBoard==null||overviewDraft==null)return;Point p=e.GetCurrentPoint(overviewBoard).Position;
            foreach(var other in overviewBoard.Children.OfType<Grid>().Where(x=>x!=tile)){var at=overviewBoard.Slot(other);if(p.X>at.X+at.Width*.12&&p.X<at.Right-at.Width*.12&&p.Y>at.Y+at.Height*.12&&p.Y<at.Bottom-at.Height*.12){int from=overviewDraft.IndexOf(id),to=overviewDraft.IndexOf((string)other.Tag);if(from==to)break;var others=overviewBoard.Children.OfType<Grid>().Where(x=>x!=tile).ToArray();var before=others.Select(x=>overviewBoard.Slot(x)).ToArray();overviewDraft.RemoveAt(from);overviewDraft.Insert(to,id);overviewBoard.InvalidateMeasure();overviewBoard.UpdateLayout();if(Design.MotionEnabled)for(int i=0;i<others.Length;i++){var now=overviewBoard.Slot(others[i]);var v=ElementCompositionPreview.GetElementVisual(others[i]);var a=v.Compositor.CreateVector3KeyFrameAnimation();a.InsertKeyFrame(0,new Vector3((float)(before[i].X-now.X),(float)(before[i].Y-now.Y),0));a.InsertKeyFrame(1,Vector3.Zero);a.Duration=TimeSpan.FromMilliseconds(155);v.StartAnimation("Offset",a);}break;}}
            var slot=overviewBoard.Slot(tile);ElementCompositionPreview.GetElementVisual(tile).Offset=new Vector3((float)(p.X-grab.X-slot.X),(float)(p.Y-grab.Y-slot.Y),0);};
        tile.PointerReleased+=(_,e)=>{if(e.Pointer.PointerId==pointer)Finish(true);};tile.PointerCanceled+=(_,_)=>Finish(false);tile.PointerCaptureLost+=(_,_)=>Finish(false);tile.KeyDown+=(_,e)=>{if(e.Key==VirtualKey.Escape){Finish(false);e.Handled=true;}};
        void Escape(object sender,KeyRoutedEventArgs e){if(held&&e.Key==VirtualKey.Escape){Finish(false);e.Handled=true;}}root.KeyDown+=Escape;
        void Deactivated(object sender,WindowActivatedEventArgs e){if(e.WindowActivationState==WindowActivationState.Deactivated)Finish(false);}Activated+=Deactivated;tile.Unloaded+=(_,_)=>{Activated-=Deactivated;root.KeyDown-=Escape;};
    }
    private static bool IsWithin(DependencyObject child,DependencyObject parent){for(var x=child;x!=null;x=VisualTreeHelper.GetParent(x))if(ReferenceEquals(x,parent))return true;return false;}
}
internal sealed class OverviewBoard:Panel
{
    private readonly List<Rect> slots=[];
    private UIElement[] ordered=[];
    internal List<string> Order{get;init;}=[];
    internal Rect Slot(UIElement child){int i=Array.IndexOf(ordered,child);return i>=0&&i<slots.Count?slots[i]:new Rect();}
    protected override Size MeasureOverride(Size available)
    {
        double width=double.IsFinite(available.Width)?available.Width:960;int cols=width>740?4:width>390?2:1;double unit=Math.Max(0,(width-12*(cols-1))/cols),x=0,y=0,row=0;int used=0;slots.Clear();
        ordered=Children.OrderBy(c=>c is Grid{Tag:string id}?Order.IndexOf(id):int.MaxValue).ToArray();
        foreach(var child in ordered){int span=child is Grid{Tag:"performance"}?Math.Min(2,cols):1;if(used+span>cols){for(int i=slots.Count-1;i>=0&&slots[i].Y==y;i--){var s=slots[i];slots[i]=new Rect(s.X,s.Y,s.Width,row);}x=0;y+=row+12;row=0;used=0;}double w=unit*span+12*(span-1);child.Measure(new Size(w,double.PositiveInfinity));double h=child.DesiredSize.Height;slots.Add(new Rect(x,y,w,h));row=Math.Max(row,h);used+=span;x+=w+12;if(used==cols){for(int i=slots.Count-1;i>=0&&slots[i].Y==y;i--){var s=slots[i];slots[i]=new Rect(s.X,s.Y,s.Width,row);}y+=row+12;row=0;used=0;x=0;}}
        if(used>0)for(int i=slots.Count-1;i>=0&&slots[i].Y==y;i--){var s=slots[i];slots[i]=new Rect(s.X,s.Y,s.Width,row);}
        return new Size(width,y+row-(used==0&&Children.Count>0?12:0));
    }
    protected override Size ArrangeOverride(Size final){for(int i=0;i<ordered.Length&&i<slots.Count;i++)ordered[i].Arrange(slots[i]);return final;}
}
