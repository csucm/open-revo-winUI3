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

internal static class Paint
{
    private static ControlTemplate? presenterTemplate;
    private static readonly Dictionary<(string Path,double Pixels),SvgImageSource> iconSources=[];
    internal static ControlTemplate PresenterTemplate()=>presenterTemplate??=(ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'><ContentPresenter Content='{TemplateBinding Content}' HorizontalAlignment='Stretch' VerticalAlignment='Stretch'/></ControlTemplate>");
    internal static bool Dark(FrameworkElement e)=>e.ActualTheme==ElementTheme.Dark;
    internal static string Bg(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"background");
    internal static string Surface(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"surface");
    internal static string Line(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"border");
    internal static string Ink(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"text");
    internal static string Muted(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"muted");
    internal static string Tint(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"tint");
    internal static string Subtle(FrameworkElement e)=>NativePluginThemePalette.Resolve(e,"subtle");
    internal static string Accent(FrameworkElement e,string family="blue")=>family=="blue"?NativePluginThemePalette.Resolve(e,"accent"):(Dark(e),family) switch
    {(false,"green")=>"208070",(true,"green")=>"80D4BF",(false,"rose")=>"A64F76",(true,"rose")=>"EFA6C5",(false,"orange")=>"9B5F1B",(true,"orange")=>"EDBE80",(false,"violet")=>"6556C7",(true,"violet")=>"B7A9FF",(false,"cyan")=>"167BA1",(true,"cyan")=>"7BCEE6",(true,_)=>"92B9FF",_=>"3468CE"};
    internal static SolidColorBrush Brush(string hex)=>new(Windows.UI.Color.FromArgb(hex.Length==8?Convert.ToByte(hex[..2],16):(byte)255,Convert.ToByte(hex[^6..^4],16),Convert.ToByte(hex[^4..^2],16),Convert.ToByte(hex[^2..],16)));
    internal static string Mix(string a,string b,double ratio)=>string.Concat(Enumerable.Range(0,3).Select(i=>((int)Math.Round(Convert.ToInt32(a.Substring(i*2,2),16)*ratio+Convert.ToInt32(b.Substring(i*2,2),16)*(1-ratio))).ToString("X2")));
    internal static TextBlock Text(string value,double size=14,bool bold=false,bool muted=false)
    {
        var t=new TextBlock{Text=value,Tag=muted?"paint-muted":"paint-label",FontSize=size,FontFamily=new("Segoe UI, Microsoft YaHei"),Language="zh-CN",FontWeight=bold?(value.Any(c=>c is >= '\u4e00' and <= '\u9fff')?Microsoft.UI.Text.FontWeights.Bold:Microsoft.UI.Text.FontWeights.SemiBold):Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap,LineHeight=size==24?30:size*1.5,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
        SolidColorBrush? themedBrush=null;void Theme()=>t.Foreground=themedBrush=Brush(muted?Muted(t):Ink(t));NativePluginThemePalette.Bind(t,()=>{if(ReferenceEquals(t.Foreground,themedBrush))Theme();});t.Loaded+=(_,_)=>Theme();t.ActualThemeChanged+=(_,_)=>Theme();Theme();return t;
    }
    internal static StackPanel Stack(double gap,params UIElement[] children){var p=new StackPanel{Spacing=gap};foreach(var c in children)p.Children.Add(c);return p;}
    internal static StackPanel Inline(double gap,params UIElement[] children){var p=Stack(gap,children);p.Orientation=Orientation.Horizontal;p.VerticalAlignment=VerticalAlignment.Center;foreach(var c in children.OfType<FrameworkElement>())c.VerticalAlignment=VerticalAlignment.Center;return p;}
    internal static Grid Between(FrameworkElement a,FrameworkElement b,double gap=16)
    {var g=new Grid{ColumnSpacing=gap};g.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=GridLength.Auto});a.VerticalAlignment=b.VerticalAlignment=VerticalAlignment.Center;g.Children.Add(a);Grid.SetColumn(b,1);g.Children.Add(b);return g;}
    internal static Border Card(UIElement content,double padding=16,double radius=16,bool inset=false)
    {
        var b=new Border{Child=content,Padding=new(padding),CornerRadius=new(radius),BorderThickness=new(1)};void Theme(){b.Background=Brush(inset?Bg(b):Surface(b));b.BorderBrush=Brush(Line(b));}NativePluginThemePalette.Bind(b,Theme);b.Loaded+=(_,_)=>Theme();b.ActualThemeChanged+=(_,_)=>Theme();Theme();return b;
    }
    internal static StackPanel StackCard(StackPanel panel, double padding = 16)
    {
        panel.Padding = new Thickness(padding);
        panel.CornerRadius = new CornerRadius(16);
        panel.BorderThickness = new Thickness(1);

        void ApplyTheme()
        {
            panel.Background = Brush(Surface(panel));
            panel.BorderBrush = Brush(Line(panel));
        }

        NativePluginThemePalette.Bind(panel, ApplyTheme);
        panel.Loaded += (_, _) => ApplyTheme();
        panel.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        return panel;
    }
    internal static Border Rule(UIElement content,double padding=14)
    {var b=Card(content,0,0);b.Padding=new(0,padding,0,padding);b.BorderThickness=new(0,1,0,0);return b;}
    internal static Image Icon(string id,double size=34,bool on=true,bool? compact=null,bool loadOnAttach=false)
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
        image.Loaded += (_, _) => Theme();
        image.ActualThemeChanged += (_, _) => { if (!loadOnAttach || image.IsLoaded) Theme(); };
        if (!loadOnAttach) Theme();
        return image;
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
    internal bool DimWhenDisabled { get; init; } = true;
    internal bool Selected{get=>selected;set{if(selected==value)return;selected=value;Repaint();}}
    internal string Variant{get=>variant;set{variant=value;Repaint();}}
    internal new object? Content{get=>presenter.Content;set=>presenter.Content=value;}
    internal PaintButton(string name,Action? action=null,bool createDefaultContent=true)
    {
        Template=Paint.PresenterTemplate();base.Content=chrome;chrome.Child=presenter;IsTabStop=true;UseSystemFocusVisuals=false;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Center;FontSize=14;
        Padding=new(12,6,12,6);MinHeight=34;CornerRadius=new(9);presenter.HorizontalAlignment=HorizontalAlignment.Center;presenter.VerticalAlignment=VerticalAlignment.Center;
        AutomationProperties.SetName(this,name);if(action!=null)Click+=action;
        if(createDefaultContent)Content=Paint.Text(name);
        PointerEntered+=(_,_)=>{hover=true;Repaint();};PointerExited+=(_,_)=>{hover=false;Repaint();};
        PointerPressed+=(_,e)=>{if(!IsEnabled||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus(FocusState.Pointer);held=CapturePointer(e.Pointer);Repaint();e.Handled=true;};
        PointerReleased+=(_,e)=>{bool invoke=held&&IsEnabled;held=false;ReleasePointerCaptures();Repaint();var p=e.GetCurrentPoint(this).Position;if(invoke&&p.X>=0&&p.Y>=0&&p.X<=ActualWidth&&p.Y<=ActualHeight)Invoke();e.Handled=true;};
        PointerCaptureLost+=(_,_)=>{held=false;Repaint();};KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Space or VirtualKey.Enter){Invoke();e.Handled=true;}};
        GotFocus+=(_,_)=>{focused=FocusState==FocusState.Keyboard;Repaint();};LostFocus+=(_,_)=>{focused=false;Repaint();};
        IsEnabledChanged+=(_,_)=>Repaint();Loaded+=(_,_)=>Repaint();ActualThemeChanged+=(_,_)=>Repaint();
        NativePluginThemePalette.Bind(this,Repaint);
    }
    internal void Invoke(){if(IsEnabled)Click?.Invoke();}
    internal void StretchContent(){presenter.HorizontalAlignment=HorizontalAlignment.Stretch;}
    internal virtual void Repaint()
    {
        chrome.Padding=Padding;chrome.CornerRadius=CornerRadius;chrome.Opacity=IsEnabled||!DimWhenDisabled?1:.6;
        string fill=variant=="plain"||variant=="icon"?"00000000":selected?Paint.Tint(this):variant=="primary"?Paint.Accent(this):Paint.Surface(this);
        if(hover&&IsEnabled&&variant!="primary")fill=NativePluginThemePalette.Hover(this,Paint.Dark(this)?"2C3443":"ECF0F8");
        chrome.Background=Paint.Brush(fill);chrome.BorderBrush=Paint.Brush(focused?Paint.Accent(this):selected?Paint.Accent(this):Paint.Line(this));chrome.BorderThickness=new((variant is "plain" or "icon" or "navigation")&&!focused?0:1);
        presenter.Opacity=held ? .8 : 1;
        void Color(DependencyObject item){if(item is TextBlock t&&Equals(t.Tag,"paint-label"))t.Foreground=Paint.Brush(variant=="primary"?(Paint.Dark(this)?"171B24":"FFFFFF"):selected&&variant!="navigation"?Paint.Accent(this):Paint.Ink(this));for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)Color(VisualTreeHelper.GetChild(item,i));}if(presenter.Content is DependencyObject content)Color(content);
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new PaintButtonPeer(this);
    private sealed class PaintButtonPeer(PaintButton owner):FrameworkElementAutomationPeer(owner),IInvokeProvider
    {protected override string GetClassNameCore()=>"OpenRevo.Button";protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Button;protected override object GetPatternCore(PatternInterface pattern)=>pattern==PatternInterface.Invoke?this:base.GetPatternCore(pattern);public void Invoke()=>owner.Invoke();}
}

internal static class PaintSwitch
{
    private sealed class Readback { internal bool Applying; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToggleSwitch, Readback> readbacks = new();

    internal static ToggleSwitch Create(string name, bool value, Action<bool>? changed = null, string family = "blue")
    {
        var toggle = new ToggleSwitch
        {
            Template = (ControlTemplate)Application.Current.Resources["PaintSwitchTemplate"],
            Width = 40, Height = 24, MinHeight = 24, MinWidth = 0, Padding = new(0),
            IsTabStop = true, UseSystemFocusVisuals = false, IsOn = value
        };
        var readback = new Readback();
        readbacks.Add(toggle, readback);
        AutomationProperties.SetName(toggle, name);
        void Repaint()
        {
            var (fill, line, dot) = TrackColors(toggle, toggle.IsOn, family);
            toggle.Background = Paint.Brush(fill);
            toggle.BorderBrush = Paint.Brush(toggle.FocusState == FocusState.Keyboard ? Paint.Accent(toggle) : line);
            toggle.Foreground = Paint.Brush(dot);
        }
        toggle.Toggled += (_, _) =>
        {
            Repaint();
            if (!readback.Applying && toggle.IsEnabled) changed?.Invoke(toggle.IsOn);
        };
        toggle.Loaded += (_, _) => Repaint();
        toggle.ActualThemeChanged += (_, _) => Repaint();
        toggle.GotFocus += (_, _) => Repaint();
        toggle.LostFocus += (_, _) => Repaint();
        NativePluginThemePalette.Bind(toggle, Repaint);
        Repaint();
        return toggle;
    }

    internal static bool IsPainted(ToggleSwitch toggle) => readbacks.TryGetValue(toggle, out _);

    internal static void SetReadback(ToggleSwitch toggle, bool value)
    {
        if (toggle.IsOn == value) return;
        if (!readbacks.TryGetValue(toggle, out var readback))
            throw new ArgumentException("Expected a painted switch.", nameof(toggle));
        readback.Applying = true;
        try { toggle.IsOn = value; }
        finally { readback.Applying = false; }
    }
    internal static Border CreateIndicator(string family, Func<bool> read, out Action refresh)
    {
        var track = new Border { Width = 40, Height = 24, CornerRadius = new(12), BorderThickness = new(1), IsHitTestVisible = false };
        var knob = new Ellipse { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        track.Child = knob;
        bool state = read();
        void ApplyTheme()
        {
            DrawTrack(track, track, knob, state, family);
            knob.HorizontalAlignment = state ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = state ? new(0, 0, 3, 0) : new(3, 0, 0, 0);
        }
        refresh = () =>
        {
            bool next = read();
            if (state == next) return;
            state = next;
            ApplyTheme();
        };
        track.Loaded += (_, _) => ApplyTheme();
        track.ActualThemeChanged += (_, _) => ApplyTheme();
        NativePluginThemePalette.Bind(track, ApplyTheme);
        ApplyTheme();
        return track;
    }

    private static void DrawTrack(FrameworkElement theme, Border track, Ellipse knob, bool on, string family)
    {
        var (fill, line, dot) = TrackColors(theme, on, family);
        track.Background = Paint.Brush(fill);
        track.BorderBrush = Paint.Brush(line);
        knob.Fill = Paint.Brush(dot);
    }

    private static (string Fill, string Line, string Dot) TrackColors(FrameworkElement theme, bool on, string family)
    {
        if (!on) return (Paint.Subtle(theme), Paint.Line(theme), Paint.Muted(theme));
        string color = Paint.Accent(theme, family);
        return (Paint.Mix(color, Paint.Surface(theme), .13), color, color);
    }
}

internal sealed class PaintSlider : Slider
{
    private bool applyingReadback;
    private Grid? surface;

    internal new double Value
    {
        get => base.Value;
        set
        {
            applyingReadback = true;
            try { base.Value = double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Minimum; }
            finally { applyingReadback = false; }
        }
    }

    internal PaintSlider(string name, double min, double max, double initial, double step = 1, Action<double>? changed = null)
    {
        Template = (ControlTemplate)Application.Current.Resources["PaintSliderTemplate"];
        Height = MinHeight = 28;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        IsThumbToolTipEnabled = false;
        StepFrequency = SmallChange = step;
        LargeChange = step * 5;
        SnapsTo = SliderSnapsTo.StepValues;
        SetReadback(min, max, initial);
        AutomationProperties.SetName(this, name);
        if (changed != null)
            ValueChanged += (_, args) => { if (!applyingReadback && IsEnabled) changed(args.NewValue); };
        Loaded += (_, _) => Repaint();
        ActualThemeChanged += (_, _) => Repaint();
        IsEnabledChanged += (_, _) => Repaint();
        NativePluginThemePalette.Bind(this, Repaint);
    }

    internal void SetReadback(double minimum, double maximum, double value)
    {
        maximum = Math.Max(minimum, maximum);
        applyingReadback = true;
        try
        {
            Maximum = Math.Max(maximum, Minimum);
            Minimum = minimum;
            Maximum = maximum;
            base.Value = double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
        }
        finally { applyingReadback = false; }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        surface = GetTemplateChild("SliderContainer") as Grid;
        Repaint();
    }

    private void Repaint()
    {
        Background = Paint.Brush(Paint.Line(this));
        Foreground = Paint.Brush(IsEnabled ? Paint.Accent(this) : Paint.Muted(this));
        if (surface != null) surface.Opacity = IsEnabled ? 1 : .6;
    }
}
internal sealed class PaintSelect:PaintButton
{
    internal string[] Items{get;private set;}=[];
    private int index=-1;
    private readonly TextBlock? label;
    private Popup? popup;
    private string unselectedText="待读取";
    internal string UnselectedText{get=>unselectedText;set{if(unselectedText==value)return;unselectedText=value;if(label!=null&&(index<0||index>=Items.Length))label.Text=value;}}
    internal int SelectedIndex
    {
        get => index;
        set
        {
            index = value;
            string text = value >= 0 && value < Items.Length ? Items[value] : unselectedText;
            if (label != null && label.Text != text) label.Text = text;
        }
    }
    internal event Action<int>? Changed;
    internal PaintSelect(string name, string[] items, int selected, Action<int>? changed = null,
        bool createDefaultContent = true) : base(name, createDefaultContent: false)
    {
        Items=items;MinWidth=120;
        if (createDefaultContent)
        {
            label = Paint.Text(unselectedText);
            Content = Paint.Between(label, Paint.Utility("chevron", 14), 10);
        }
        StretchContent();SelectedIndex=selected;Variant="normal";
        if (changed != null) Changed += changed;
        Click += Open;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) DismissPopup(); };
        Unloaded += (_, _) => { if (popup != null) popup.IsOpen = false; };
        KeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Down or VirtualKey.Up)) return;
            e.Handled = true;
            if (!IsEnabled || Items.Length == 0) return;
            int next = Math.Clamp(index + (e.Key == VirtualKey.Down ? 1 : -1), 0, Items.Length - 1);
            SelectedIndex = next;
            Changed?.Invoke(next);
        };
    }
    internal void Replace(string[] items,int selected){Items=items;SelectedIndex=selected;}
    internal void DismissPopup()
    {
        if (popup is { IsOpen: true } menu) menu.IsOpen = false;
    }
    private void Open()
    {
        if (Items.Length == 0 || XamlRoot is not { } root || popup is { IsOpen: true }) return;
        var menu = new Popup { XamlRoot = root, IsLightDismissEnabled = true };
        var list = Paint.Stack(3);
        for(int i=0;i<Items.Length;i++){int at=i;var b=new PaintButton(Items[i],()=>{menu.IsOpen=false;Focus(FocusState.Programmatic);if(index!=at){SelectedIndex=at;Changed?.Invoke(at);}}){Selected=i==index,HorizontalAlignment=HorizontalAlignment.Stretch};b.StretchContent();list.Children.Add(b);}
        double width=Math.Max(ActualWidth,160),height=Math.Min(320,Items.Length*39+12);var card=Paint.Card(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=height-12},6,12);card.Width=width;card.RequestedTheme=ActualTheme;
        NativePluginThemePalette.RegisterRoot(card,"menu");
        var p=TransformToVisual(root.Content).TransformPoint(new Point());double top=p.Y+ActualHeight+6;if(top+height>root.Size.Height-8)top=Math.Max(8,p.Y-height-6);
        menu.Child = card;
        menu.HorizontalOffset = Math.Clamp(p.X, 8, Math.Max(8, root.Size.Width - width - 8));
        menu.VerticalOffset = top;
        menu.Closed += ReleaseMenu;
        popup = menu;
        menu.IsOpen = true;
    }

    private void ReleaseMenu(object? sender, object args)
    {
        if (sender is not Popup menu) return;
        menu.Closed -= ReleaseMenu;
        menu.Child = null;
        if (ReferenceEquals(popup, menu)) popup = null;
    }
}
