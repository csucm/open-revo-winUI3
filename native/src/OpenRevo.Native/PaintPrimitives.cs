using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;
using Windows.System;

namespace OpenRevo.Native;

// Deliberately uses only layout, text and geometry. No stock Button,
// ToggleSwitch, Slider, ComboBox or NavigationView templates are loaded.
internal static class Paint
{
    private static ControlTemplate? presenterTemplate;
    private static readonly Dictionary<(string Path,double Pixels),SvgImageSource> iconSources=[];
    internal static ControlTemplate PresenterTemplate()=>presenterTemplate??=(ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'><ContentPresenter Content='{TemplateBinding Content}' HorizontalAlignment='Stretch' VerticalAlignment='Stretch'/></ControlTemplate>");
    internal static bool Dark(FrameworkElement e)=>e.ActualTheme==ElementTheme.Dark;
    internal static string Bg(FrameworkElement e)=>Dark(e)?"171B24":"ECEEF2";
    internal static string Surface(FrameworkElement e)=>Dark(e)?"252B37":"F8F9FB";
    internal static string Line(FrameworkElement e)=>Dark(e)?"3D4451":"DCDFE4";
    internal static string Ink(FrameworkElement e)=>Dark(e)?"EEF1F8":"242B38";
    internal static string Muted(FrameworkElement e)=>Dark(e)?"ACB6C8":"5F697A";
    internal static string Tint(FrameworkElement e)=>Dark(e)?"333D51":"DFE6F5";
    internal static string Subtle(FrameworkElement e)=>Dark(e)?"343C4B":"E8ECF2";
    internal static string Accent(FrameworkElement e,string family="blue")=>(Dark(e),family) switch
    {(false,"green")=>"208070",(true,"green")=>"80D4BF",(false,"rose")=>"A64F76",(true,"rose")=>"EFA6C5",(false,"orange")=>"9B5F1B",(true,"orange")=>"EDBE80",(false,"violet")=>"6556C7",(true,"violet")=>"B7A9FF",(false,"cyan")=>"167BA1",(true,"cyan")=>"7BCEE6",(true,_)=>"92B9FF",_=>"3468CE"};
    internal static SolidColorBrush Brush(string hex)=>new(Windows.UI.Color.FromArgb(hex.Length==8?Convert.ToByte(hex[..2],16):(byte)255,Convert.ToByte(hex[^6..^4],16),Convert.ToByte(hex[^4..^2],16),Convert.ToByte(hex[^2..],16)));
    internal static string Mix(string a,string b,double ratio)=>string.Concat(Enumerable.Range(0,3).Select(i=>((int)Math.Round(Convert.ToInt32(a.Substring(i*2,2),16)*ratio+Convert.ToInt32(b.Substring(i*2,2),16)*(1-ratio))).ToString("X2")));
    internal static TextBlock Text(string value,double size=14,bool bold=false,bool muted=false)
    {
        var t=new TextBlock{Text=value,Tag=muted?"paint-muted":"paint-label",FontSize=size,FontFamily=new("Segoe UI, Microsoft YaHei"),Language="zh-CN",FontWeight=bold?(value.Any(c=>c is >= '\u4e00' and <= '\u9fff')?Microsoft.UI.Text.FontWeights.Bold:Microsoft.UI.Text.FontWeights.SemiBold):Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap,LineHeight=size==24?30:size*1.5,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
        void Theme()=>t.Foreground=Brush(muted?Muted(t):Ink(t));t.Loaded+=(_,_)=>Theme();t.ActualThemeChanged+=(_,_)=>Theme();Theme();return t;
    }
    internal static StackPanel Stack(double gap,params UIElement[] children){var p=new StackPanel{Spacing=gap};foreach(var c in children)p.Children.Add(c);return p;}
    internal static StackPanel Inline(double gap,params UIElement[] children){var p=Stack(gap,children);p.Orientation=Orientation.Horizontal;p.VerticalAlignment=VerticalAlignment.Center;foreach(var c in children.OfType<FrameworkElement>())c.VerticalAlignment=VerticalAlignment.Center;return p;}
    internal static Grid Between(FrameworkElement a,FrameworkElement b,double gap=16)
    {var g=new Grid{ColumnSpacing=gap};g.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=GridLength.Auto});a.VerticalAlignment=b.VerticalAlignment=VerticalAlignment.Center;g.Children.Add(a);Grid.SetColumn(b,1);g.Children.Add(b);return g;}
    internal static Border Card(UIElement content,double padding=16,double radius=16,bool inset=false)
    {
        var b=new Border{Child=content,Padding=new(padding),CornerRadius=new(radius),BorderThickness=new(1)};void Theme(){b.Background=Brush(inset?Bg(b):Surface(b));b.BorderBrush=Brush(Line(b));}b.Loaded+=(_,_)=>Theme();b.ActualThemeChanged+=(_,_)=>Theme();Theme();return b;
    }
    internal static Border Rule(UIElement content,double padding=14)
    {var b=Card(content,0,0);b.Padding=new(0,padding,0,padding);b.BorderThickness=new(0,1,0,0);return b;}
    internal static Image Icon(string id,double size=34,bool on=true,bool? compact=null)
    {
        var image=new Image{Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};
        string? loadedPath=null;
        void Theme()
        {
            string path=System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Approved",id+"-"+(Dark(image)?"dark":"light")+((compact??size<=24)?"-compact":"")+(on?"":"-off")+".svg");
            if(loadedPath==path)return;
            loadedPath=path;
            var key=(path,size*2);
            if(!iconSources.TryGetValue(key,out var source))
            {
                source=new SvgImageSource(new Uri(path)){RasterizePixelWidth=size*2,RasterizePixelHeight=size*2};
                iconSources.Add(key,source);
            }
            image.Source=source;
        }
        image.Loaded+=(_,_)=>Theme();image.ActualThemeChanged+=(_,_)=>Theme();Theme();return image;
    }
    internal static Image Utility(string id,double size=18)=>Icon("utility-"+id,size,true,false);
}

internal class PaintButton:ContentControl
{
    private readonly Border chrome=new();
    private readonly ContentPresenter presenter=new();
    private bool hover,held,focused;
    private bool selected;
    private string variant="normal";
    internal event Action? Click;
    internal bool Selected{get=>selected;set{if(selected==value)return;selected=value;Repaint();}}
    internal string Variant{get=>variant;set{variant=value;Repaint();}}
    internal new object? Content{get=>presenter.Content;set=>presenter.Content=value;}
    internal PaintButton(string name,Action? action=null)
    {
        Template=Paint.PresenterTemplate();base.Content=chrome;chrome.Child=presenter;IsTabStop=true;UseSystemFocusVisuals=false;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Center;FontSize=14;
        Padding=new(12,6,12,6);MinHeight=34;CornerRadius=new(9);presenter.HorizontalAlignment=HorizontalAlignment.Center;presenter.VerticalAlignment=VerticalAlignment.Center;
        AutomationProperties.SetName(this,name);if(action!=null)Click+=action;Content=Paint.Text(name);
        PointerEntered+=(_,_)=>{hover=true;Repaint();};PointerExited+=(_,_)=>{hover=false;Repaint();};
        PointerPressed+=(_,e)=>{if(!IsEnabled||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus(FocusState.Pointer);held=CapturePointer(e.Pointer);Repaint();e.Handled=true;};
        PointerReleased+=(_,e)=>{bool invoke=held&&IsEnabled;held=false;ReleasePointerCaptures();Repaint();var p=e.GetCurrentPoint(this).Position;if(invoke&&p.X>=0&&p.Y>=0&&p.X<=ActualWidth&&p.Y<=ActualHeight)Invoke();e.Handled=true;};
        PointerCaptureLost+=(_,_)=>{held=false;Repaint();};KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Space or VirtualKey.Enter){Invoke();e.Handled=true;}};
        GotFocus+=(_,_)=>{focused=FocusState==FocusState.Keyboard;Repaint();};LostFocus+=(_,_)=>{focused=false;Repaint();};
        IsEnabledChanged+=(_,_)=>Repaint();Loaded+=(_,_)=>Repaint();ActualThemeChanged+=(_,_)=>Repaint();
    }
    internal void Invoke(){if(IsEnabled)Click?.Invoke();}
    internal void StretchContent(){presenter.HorizontalAlignment=HorizontalAlignment.Stretch;}
    internal virtual void Repaint()
    {
        chrome.Padding=Padding;chrome.CornerRadius=CornerRadius;chrome.Opacity=IsEnabled?1:.6;
        string fill=variant=="plain"||variant=="icon"?"00000000":selected?Paint.Tint(this):variant=="primary"?Paint.Accent(this):Paint.Surface(this);
        if(hover&&IsEnabled&&variant!="primary")fill=Paint.Dark(this)?"2C3443":"ECF0F8";
        chrome.Background=Paint.Brush(fill);chrome.BorderBrush=Paint.Brush(focused?Paint.Accent(this):selected?Paint.Accent(this):Paint.Line(this));chrome.BorderThickness=new((variant is "plain" or "icon" or "navigation")&&!focused?0:1);
        presenter.Opacity=held ? .8 : 1;
        void Color(DependencyObject item){if(item is TextBlock t&&Equals(t.Tag,"paint-label"))t.Foreground=Paint.Brush(variant=="primary"?(Paint.Dark(this)?"171B24":"FFFFFF"):selected&&variant!="navigation"?Paint.Accent(this):Paint.Ink(this));for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)Color(VisualTreeHelper.GetChild(item,i));}if(presenter.Content is DependencyObject content)Color(content);
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new PaintButtonPeer(this);
    private sealed class PaintButtonPeer(PaintButton owner):FrameworkElementAutomationPeer(owner),IInvokeProvider
    {protected override string GetClassNameCore()=>"OpenRevo.Button";protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Button;protected override object GetPatternCore(PatternInterface pattern)=>pattern==PatternInterface.Invoke?this:base.GetPatternCore(pattern);public void Invoke()=>owner.Invoke();}
}

internal sealed class PaintSwitch:PaintButton
{
    private readonly Border track=new(){Width=40,Height=24,CornerRadius=new(12),BorderThickness=new(1)};
    private readonly Ellipse knob=new(){Width=16,Height=16,VerticalAlignment=VerticalAlignment.Center};
    private bool on;
    internal string Family="blue";
    internal bool IsOn{get=>on;set{if(on==value)return;on=value;Repaint();}}
    internal event Action<bool>? Toggled;
    internal PaintSwitch(string name,bool value,Action<bool>? changed=null):base(name)
    {
        Variant="icon";Width=40;Height=24;MinHeight=24;MinWidth=0;Padding=new(0);track.Child=knob;Content=track;on=value;
        if(changed!=null)Toggled+=changed;Click+=()=>{IsOn=!IsOn;Toggled?.Invoke(IsOn);};Repaint();
    }
    internal override void Repaint()
    {
        base.Repaint();if(track==null||knob==null)return;
        string color=Paint.Accent(this,Family);track.Background=Paint.Brush(on?Paint.Mix(color,Paint.Surface(this),.13):Paint.Subtle(this));track.BorderBrush=Paint.Brush(on?color:Paint.Line(this));knob.Fill=Paint.Brush(on?color:Paint.Muted(this));knob.HorizontalAlignment=on?HorizontalAlignment.Right:HorizontalAlignment.Left;knob.Margin=on?new(0,0,3,0):new(3,0,0,0);
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new SwitchPeer(this);
    private sealed class SwitchPeer(PaintSwitch owner):FrameworkElementAutomationPeer(owner),IToggleProvider
    {protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.CheckBox;protected override object GetPatternCore(PatternInterface p)=>p==PatternInterface.Toggle?this:base.GetPatternCore(p);public ToggleState ToggleState=>owner.IsOn?ToggleState.On:ToggleState.Off;public void Toggle()=>owner.Invoke();}
}

internal sealed class PaintSlider:ContentControl
{
    private readonly Canvas canvas=new(){Background=Paint.Brush("00000000")};
    private readonly Border rail=new(){Height=4,CornerRadius=new(2)},fill=new(){Height=4,CornerRadius=new(2)};
    private readonly Ellipse thumb=new(){Width=16,Height=16};private bool dragging;
    private double value;
    internal double Minimum{get;set;}=0;internal double Maximum{get;set;}=100;internal double Step{get;set;}=1;
    internal double Value{get=>value;set{this.value=double.IsFinite(value)?Math.Clamp(value,Minimum,Maximum):Minimum;Draw();}}
    internal event Action<double>? Changed;
    internal PaintSlider(string name,double min,double max,double initial,double step=1,Action<double>? changed=null)
    {
        Minimum=min;Maximum=Math.Max(min,max);Step=step;value=initial;Height=28;MinHeight=28;IsTabStop=true;Content=canvas;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        canvas.Children.Add(rail);canvas.Children.Add(fill);canvas.Children.Add(thumb);AutomationProperties.SetName(this,name);if(changed!=null)Changed+=changed;
        void Point(PointerRoutedEventArgs e){double width=Math.Max(1,ActualWidth-16),ratio=Math.Clamp((e.GetCurrentPoint(this).Position.X-8)/width,0,1);Set(Minimum+Math.Round((Maximum-Minimum)*ratio/Step)*Step);}
        PointerPressed+=(_,e)=>{if(!IsEnabled||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus(FocusState.Pointer);dragging=CapturePointer(e.Pointer);Point(e);e.Handled=true;};PointerMoved+=(_,e)=>{if(dragging){Point(e);e.Handled=true;}};PointerReleased+=(_,e)=>{if(dragging)Point(e);dragging=false;ReleasePointerCaptures();e.Handled=true;};PointerCaptureLost+=(_,_)=>dragging=false;
        KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Left or VirtualKey.Down){Set(value-Step);e.Handled=true;}else if(e.Key is VirtualKey.Right or VirtualKey.Up){Set(value+Step);e.Handled=true;}else if(e.Key==VirtualKey.Home){Set(Minimum);e.Handled=true;}else if(e.Key==VirtualKey.End){Set(Maximum);e.Handled=true;}};
        SizeChanged+=(_,_)=>Draw();Loaded+=(_,_)=>Draw();ActualThemeChanged+=(_,_)=>Draw();IsEnabledChanged+=(_,_)=>Draw();
    }
    internal void Set(double next){if(!IsEnabled)return;double before=value;Value=next;if(before!=value)Changed?.Invoke(value);}
    private void Draw()
    {double width=Math.Max(0,ActualWidth-16),ratio=Maximum>Minimum?Math.Clamp((value-Minimum)/(Maximum-Minimum),0,1):0;canvas.Opacity=IsEnabled?1:.6;rail.Width=width;fill.Width=width*ratio;rail.Background=Paint.Brush(Paint.Line(this));fill.Background=thumb.Fill=Paint.Brush(IsEnabled?Paint.Accent(this):Paint.Muted(this));Canvas.SetLeft(rail,8);Canvas.SetTop(rail,12);Canvas.SetLeft(fill,8);Canvas.SetTop(fill,12);Canvas.SetLeft(thumb,width*ratio);Canvas.SetTop(thumb,6);}
    protected override AutomationPeer OnCreateAutomationPeer()=>new SliderPeer(this);
    private sealed class SliderPeer(PaintSlider owner):FrameworkElementAutomationPeer(owner),IRangeValueProvider
    {protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Slider;protected override object GetPatternCore(PatternInterface p)=>p==PatternInterface.RangeValue?this:base.GetPatternCore(p);public bool IsReadOnly=>!owner.IsEnabled;public double LargeChange=>owner.Step*5;public double SmallChange=>owner.Step;public double Minimum=>owner.Minimum;public double Maximum=>owner.Maximum;public double Value=>owner.Value;public void SetValue(double value)=>owner.Set(value);}
}

internal sealed class PaintSelect:PaintButton
{
    internal string[] Items{get;private set;}=[];
    private int index=-1;private readonly TextBlock label=Paint.Text("待读取");private Popup? popup;
    private string unselectedText="待读取";
    internal string UnselectedText{get=>unselectedText;set{if(unselectedText==value)return;unselectedText=value;if(index<0||index>=Items.Length)label.Text=value;}}
    internal int SelectedIndex{get=>index;set{index=value;label.Text=value>=0&&value<Items.Length?Items[value]:unselectedText;}}
    internal event Action<int>? Changed;
    internal PaintSelect(string name,string[] items,int selected,Action<int>? changed=null):base(name)
    {
        Items=items;MinWidth=120;Content=Paint.Between(label,Paint.Utility("chevron",14),10);StretchContent();SelectedIndex=selected;Variant="normal";
        if(changed!=null)Changed+=changed;Click+=Open;Unloaded+=(_,_)=>{if(popup!=null)popup.IsOpen=false;};KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Down or VirtualKey.Up){int next=Math.Clamp(index+(e.Key==VirtualKey.Down?1:-1),0,Items.Length-1);if(Items.Length>0){SelectedIndex=next;Changed?.Invoke(next);}e.Handled=true;}};
    }
    internal void Replace(string[] items,int selected){Items=items;SelectedIndex=selected;}
    private void Open()
    {
        if(Items.Length==0||XamlRoot==null)return;var list=Paint.Stack(3);int chosen=index;
        for(int i=0;i<Items.Length;i++){int at=i;var b=new PaintButton(Items[i],()=>{popup!.IsOpen=false;Focus(FocusState.Programmatic);if(index!=at){SelectedIndex=at;Changed?.Invoke(at);}}){Selected=i==index,HorizontalAlignment=HorizontalAlignment.Stretch};b.StretchContent();list.Children.Add(b);}
        double width=Math.Max(ActualWidth,160),height=Math.Min(320,Items.Length*39+12);var card=Paint.Card(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=height-12},6,12);card.Width=width;card.RequestedTheme=ActualTheme;
        var p=TransformToVisual(XamlRoot.Content).TransformPoint(new Point());double top=p.Y+ActualHeight+6;if(top+height>XamlRoot.Size.Height-8)top=Math.Max(8,p.Y-height-6);
        popup=new Popup{XamlRoot=XamlRoot,Child=card,IsLightDismissEnabled=true,HorizontalOffset=Math.Clamp(p.X,8,Math.Max(8,XamlRoot.Size.Width-width-8)),VerticalOffset=top};popup.IsOpen=true;
    }
}
