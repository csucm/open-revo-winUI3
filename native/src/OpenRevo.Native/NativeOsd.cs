using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
namespace OpenRevo.Native;

internal sealed record OsdMessage(string Text,string Icon,string Accent,string? Dot=null,int? Steps=null)
{
    internal static OsdMessage FromFeature(string feature,string value)
    {
        bool on=value=="1";
        const string blue="#38BDF8",green="#10B981",red="#F43F5E",neutral="#94A3B8",purple="#6366F1";
        if(feature=="power_mode")return value switch
        {"1"=>new("办公模式","office",green),"2"=>new("均衡模式","balance","#3B82F6"),"3"=>new("狂暴模式","beast",red),"4"=>new("极客调校","geek",purple),_=>new("性能模式待读取","balance",neutral)};
        return feature switch
        {
            "fan_boost"=>new("一键强冷 "+(on?"开启":"关闭"),"fan",on?"#06B6D4":neutral,on?"on":"neutral"),
            "wifi"=>new("Wi-Fi 无线网 "+(on?"开启":"关闭"),"wifi",on?blue:red,on?"on":"off"),
            "bluetooth"=>new("蓝牙 "+(on?"开启":"关闭"),"bluetooth",on?blue:red,on?"on":"off"),
            "touchpad"=>new("触控板 "+(on?"开启":"禁用"),"touchpad",on?blue:red,on?"on":"off"),
            "camera"=>new("摄像头 "+(on?"开启":"关闭"),"camera",on?green:red,on?"on":"off"),
            "mic"=>new("麦克风 "+(on?"开启":"静音"),"mic",on?green:red,on?"on":"off"),
            "win_key"=>new("Win键 "+(on?"锁定":"解锁"),"lock",on?"#F59E0B":green,on?"off":"on"),
            "fn_lock"=>new("Fn键 "+(on?"锁定":"解锁"),"fn",on?purple:neutral,on?"on":"neutral"),
            "caps_lock"=>new(on?"Caps已锁定":"Caps已解锁","caps",on?blue:neutral,on?"on":"neutral"),
            "keep_awake"=>new("防休眠 "+(on?"开启":"关闭"),"awake",on?blue:neutral,on?"on":"neutral"),
            "keyboard_brightness" when int.TryParse(value,out int level)&&level is >=0 and <=4=>new(level==0?"键盘背光 关闭":$"键盘背光 {level*25}%","keyboard",blue,Steps:level),
            _=>new(feature+" "+value,"balance",blue)
        };
    }
}

/// <summary>Compact native OSD: alpha/desktop acrylic, no activation or input capture.</summary>
internal sealed class NativeOsd:IDisposable
{
    private readonly Window window=new();
    private readonly Grid root=new(),cardContent=new();
    private readonly Border frame=new();
    private readonly DispatcherQueueTimer holdTimer,exitTimer;
    private readonly nint hwnd;
    private readonly Subclass callback;
    private readonly TranslateTransform offset=new();
    private OpenRevo.Core.SystemPreferences preferences=new();
    private string appliedTheme="";
    private bool disposed;
    private Storyboard? animation;
    private TextBlock? label;
    private Border? iconBadge;
    private bool originalIconLoaded;
    private string originalAsset="",iconFailure="";
    private OsdGlassBackdrop? glass;
    private object? frameDiagnostics;
    private string lastFrameSignature="";
    private readonly bool reviewOsdWindow;
    private readonly bool frameHookAttached;
    internal object Diagnostics=>new{Theme,preferences.OsdScale,preferences.OsdPosition,frame=frameDiagnostics,layout=LayoutReport};
    internal OsdMessage Message{get;private set;}=new("Open-Revo · 屏幕提示预览","balance","#38BDF8");
    public bool IsVisible=>!disposed&&window.AppWindow.IsVisible;
    public FrameworkElement Surface=>root;
    public bool DoesNotActivate=>(GetWindowLongPtr(hwnd,-20)&0x08000000)!=0;
    public bool ClickThrough=>(GetWindowLongPtr(hwnd,-20)&0x20)!=0;
    internal bool NativeFrameBorderless=>frameHookAttached&&OsdFramePolicy.Borderless(unchecked((uint)GetWindowLongPtr(hwnd,-16)),unchecked((uint)GetWindowLongPtr(hwnd,-20)));
    public static nint Foreground=>GetForegroundWindow();
    internal RectInt32 Bounds=>new(window.AppWindow.Position.X,window.AppWindow.Position.Y,window.AppWindow.Size.Width,window.AppWindow.Size.Height);
    internal string Theme=>preferences.OsdTheme;
    private bool Preview=>Message.Text.Contains("屏幕提示预览",StringComparison.Ordinal);
    internal double TextSize=> ((Theme=="cinema"?21:Theme=="fade"?20:19)-(Preview?1:0))*preferences.OsdScale/100d;
    internal bool TextFits=>label!=null&&label.ActualWidth>0&&label.TransformToVisual(frame).TransformPoint(new Point()).X+label.ActualWidth+frame.Padding.Right+frame.BorderThickness.Right<=frame.ActualWidth+1;
    internal bool IconFits=>iconBadge!=null&&iconBadge.TransformToVisual(frame).TransformPoint(new Point()).Y>=frame.Padding.Top-1&&iconBadge.TransformToVisual(frame).TransformPoint(new Point()).Y+iconBadge.ActualHeight<=frame.ActualHeight-frame.Padding.Bottom+1;
    internal bool OriginalIconLoaded=>originalIconLoaded;
    internal double CardHeight=>frame.ActualHeight;
    internal bool BackdropClipped=>glass?.Clipped??true;
    internal object LayoutReport
    {
        get{GetClientRect(hwnd,out var client);return new{outerWidth=window.AppWindow.Size.Width,outerHeight=window.AppWindow.Size.Height,clientWidth=client.Right-client.Left,clientHeight=client.Bottom-client.Top,frameHookAttached,NativeFrameBorderless,root.ActualWidth,root.ActualHeight,desiredWidth=root.DesiredSize.Width,desiredHeight=root.DesiredSize.Height,dpi=GetDpiForWindow(hwnd),raster=root.XamlRoot?.RasterizationScale,textRight=label?.TransformToVisual(root).TransformPoint(new Point()).X+label?.ActualWidth,rightPadding=frame.Padding.Right,TextFits,IconFits,originalAsset,originalIconLoaded,iconFailure,glassClipped=glass?.Clipped,glassError=glass?.Error};}
    }
    public NativeOsd(DispatcherQueue dispatcher,string windowTitle="Open-Revo OSD",bool reviewWindow=false)
    {
        reviewOsdWindow=reviewWindow;
        window.Title=windowTitle;window.Content=root;
        root.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);root.Padding=new Thickness(12);root.Children.Add(frame);frame.Child=cardContent;frame.RenderTransform=offset;
        frame.HorizontalAlignment=HorizontalAlignment.Left;frame.VerticalAlignment=VerticalAlignment.Top;
        frame.SizeChanged+=(_,_)=>UpdateBackdropClip();root.SizeChanged+=(_,_)=>UpdateBackdropClip();root.Loaded+=(_,_)=>UpdateBackdropClip();
        var presenter=OverlappedPresenter.Create();presenter.SetBorderAndTitleBar(false,false);presenter.IsAlwaysOnTop=true;presenter.IsResizable=false;presenter.IsMinimizable=false;presenter.IsMaximizable=false;
        window.AppWindow.SetPresenter(presenter);window.AppWindow.IsShownInSwitchers=reviewWindow;
        hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback=WindowMessage;frameHookAttached=SetWindowSubclass(hwnd,callback,0x4F5344,0);
        if(!frameHookAttached)OpenRevo.Core.DiagnosticLog.Failure("osd.frame_hook.failed",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        ApplyNativeFrame();
        holdTimer=dispatcher.CreateTimer();holdTimer.Interval=TimeSpan.FromMilliseconds(1600);holdTimer.IsRepeating=false;holdTimer.Tick+=(_,_)=>StartExit();
        exitTimer=dispatcher.CreateTimer();exitTimer.Interval=TimeSpan.FromMilliseconds(220);exitTimer.IsRepeating=false;exitTimer.Tick+=(_,_)=>Hide();
        Configure(preferences);
    }
    public void Configure(OpenRevo.Core.SystemPreferences config)
    {
        preferences=config;
        if(appliedTheme==config.OsdTheme)return;
        appliedTheme=config.OsdTheme;
        glass=config.OsdTheme=="pill"?new OsdGlassBackdrop():null;
        window.SystemBackdrop=glass is{} material?material:new OsdTransparentBackdrop();PrepareAlpha();
    }
    private void ApplyNativeFrame()
    {
        SetWindowLongPtr(hwnd,-16,(nint)OsdFramePolicy.Style(unchecked((uint)GetWindowLongPtr(hwnd,-16))));
        SetWindowLongPtr(hwnd,-20,(nint)OsdFramePolicy.Extended(unchecked((uint)GetWindowLongPtr(hwnd,-20)),reviewOsdWindow));
        int border=-2,round=1,nc=1;
        int borderResult=DwmSetWindowAttribute(hwnd,34,ref border,4),roundResult=DwmSetWindowAttribute(hwnd,33,ref round,4),ncResult=DwmSetWindowAttribute(hwnd,2,ref nc,4);
        bool frameChanged=SetWindowPos(hwnd,0,0,0,0,0,0x37);
        int frameWin32=frameChanged?0:Marshal.GetLastWin32Error();
        long style=GetWindowLongPtr(hwnd,-16),extended=GetWindowLongPtr(hwnd,-20);
        frameDiagnostics=new{style=$"0x{style:X}",extendedStyle=$"0x{extended:X}",borderResult=$"0x{borderResult:X8}",roundResult=$"0x{roundResult:X8}",ncResult=$"0x{ncResult:X8}",frameChanged,frameWin32,frameHookAttached,edgeBitsCleared=(extended&OsdFramePolicy.EdgeBits)==0,NativeFrameBorderless};
        string signature=$"{style}:{extended}:{borderResult}:{roundResult}:{ncResult}:{frameChanged}";
        if(lastFrameSignature!=signature){lastFrameSignature=signature;OpenRevo.Core.DiagnosticLog.Record("osd.window_frame",frameDiagnostics);}
    }
    public void Show(string heading,string value)=>Show(new OsdMessage(heading+" · "+value,"balance","#38BDF8"));
    public void ShowFeature(string feature,string value)=>Show(OsdMessage.FromFeature(feature,value));
    internal void Show(OsdMessage message)
    {
        if(disposed)return;holdTimer.Stop();exitTimer.Stop();animation?.Stop();frame.Opacity=1;offset.Y=0;Message=message;
        BuildContent(message);
        double dpi=root.XamlRoot?.RasterizationScale??GetDpiForWindow(hwnd)/96d;if(dpi<=0)dpi=1;
        root.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        GetClientRect(hwnd,out var currentClient);
        int chromeWidth=Math.Max(0,window.AppWindow.Size.Width-(currentClient.Right-currentClient.Left));
        int chromeHeight=Math.Max(0,window.AppWindow.Size.Height-(currentClient.Bottom-currentClient.Top));
        int width=(int)Math.Ceiling((root.DesiredSize.Width+2*preferences.OsdScale/100d)*dpi)+chromeWidth,height=(int)Math.Ceiling((root.DesiredSize.Height+2*preferences.OsdScale/100d)*dpi)+chromeHeight;
        var area=DisplayArea.GetFromWindowId(window.AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        double scale=preferences.OsdScale/100d;
        width=Math.Min(width,area.Width);height=Math.Min(height,area.Height);
        int x=preferences.OsdPosition=="top-left"?area.X+(int)Math.Round(4*dpi):area.X+(area.Width-width)/2;
        int y=preferences.OsdPosition=="center"?area.Y+(area.Height-height)/2:preferences.OsdPosition=="bottom-center"?area.Y+area.Height-height-(int)Math.Round(24*dpi):area.Y+(int)Math.Round(4*dpi);
        x=Math.Clamp(x,area.X,area.X+area.Width-width);y=Math.Clamp(y,area.Y,area.Y+area.Height-height);
        window.AppWindow.MoveAndResize(new RectInt32(x,y,Math.Max(1,width),Math.Max(1,height)));
        SetWindowRgn(hwnd,0,true);
        ApplyNativeFrame();window.AppWindow.Show(false);ApplyNativeFrame();root.UpdateLayout();UpdateBackdropClip();Animate(false);holdTimer.Start();
        OpenRevo.Core.DiagnosticLog.Record("osd.presentation",new{Theme,preferences.OsdScale,preferences.OsdPosition,layout=LayoutReport});
    }
    private void BuildContent(OsdMessage message)
    {
        double s=preferences.OsdScale/100d;bool pill=Theme=="pill",fade=Theme=="fade";
        cardContent.Children.Clear();cardContent.ColumnDefinitions.Clear();cardContent.ColumnSpacing=(pill?10:12)*s;
        cardContent.ColumnDefinitions.Add(new(){Width=GridLength.Auto});cardContent.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        frame.Padding=pill?new Thickness(10*s,6*s,(Preview?24:18)*s,6*s):fade?new Thickness(14*s,6*s,28*s,6*s):new Thickness(0,6*s,(Preview?4:0)*s,6*s);
        frame.CornerRadius=new CornerRadius(pill?22*s:fade?4*s:0);frame.BorderThickness=pill?new Thickness(s):fade?new Thickness(3.5*s,0,0,0):new Thickness(0);
        frame.BorderBrush=Brush(pill?"#29FFFFFF":message.Accent);
        if(fade)
        {
            var gradient=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,0)};
            gradient.GradientStops.Add(new(){Color=Color(166,"#0F172A"),Offset=0});gradient.GradientStops.Add(new(){Color=Color(64,"#0F172A"),Offset=.75});gradient.GradientStops.Add(new(){Color=Color(0,"#0F172A"),Offset=1});frame.Background=gradient;
        }
        else frame.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        double badgeSize=(pill?30:32)*s;
        var badge=iconBadge=new Border{Width=badgeSize,Height=badgeSize,CornerRadius=new CornerRadius(pill?badgeSize/2:0),VerticalAlignment=VerticalAlignment.Center,
            Background=new SolidColorBrush(Color(pill?(byte)38:(byte)0,message.Accent)),BorderBrush=new SolidColorBrush(Color(89,message.Accent)),BorderThickness=new Thickness(pill?s:0),Child=OsdIcon(message.Icon,message.Accent,22*s)};
        cardContent.Children.Add(badge);
        var body=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8*s,VerticalAlignment=VerticalAlignment.Center};
        var title=new TextBlock{Text=message.Text,FontFamily=new FontFamily("Segoe UI"),FontSize=TextSize,FontWeight=new Windows.UI.Text.FontWeight{Weight=(ushort)(Theme=="cinema"?800:750)},Foreground=Brush("#FFFFFF"),TextWrapping=TextWrapping.NoWrap,CharacterSpacing=pill?21:24,VerticalAlignment=VerticalAlignment.Center};
        label=title;
        var textHost=new Grid();var shadowHost=new Grid();textHost.Children.Add(shadowHost);textHost.Children.Add(title);body.Children.Add(textHost);
        title.Loaded+=(_,_)=>
        {
            var compositor=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(title).Compositor;
            var sprite=compositor.CreateSpriteVisual();var shadow=compositor.CreateDropShadow();
            shadow.Color=Microsoft.UI.Colors.Black;shadow.Opacity=.5f;shadow.BlurRadius=(float)((pill?2:fade?3:6)*s);shadow.Offset=new System.Numerics.Vector3(0,(float)s,0);shadow.Mask=title.GetAlphaMask();sprite.Shadow=shadow;
            void Resize(){sprite.Size=new System.Numerics.Vector2((float)title.ActualWidth,(float)title.ActualHeight);}
            title.SizeChanged+=(_,_)=>Resize();Resize();Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(shadowHost,sprite);
        };
        if(message.Dot is{} dot)body.Children.Add(new Ellipse{Width=7.5*s,Height=7.5*s,Fill=Brush(dot=="on"?"#10B981":dot=="off"?"#F43F5E":"#94A3B8"),Stroke=Brush("#66FFFFFF"),StrokeThickness=s,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4*s,0,0,0)});
        if(message.Steps is int steps)
        {
            var meter=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4*s,Margin=new Thickness(6*s,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            for(int i=1;i<=4;i++)meter.Children.Add(new Border{Width=12*s,Height=5*s,CornerRadius=new CornerRadius(2*s),Background=Brush(i<=steps?message.Accent:"#59FFFFFF")});body.Children.Add(meter);
        }
        Grid.SetColumn(body,1);cardContent.Children.Add(body);
    }
    private void Animate(bool leaving)
    {
        glass?.Animate(leaving,Design.MotionEnabled,preferences.OsdScale/100d,root.XamlRoot?.RasterizationScale??1);
        animation?.Stop();frame.Opacity=leaving?1:0;offset.Y=leaving?0:-8*preferences.OsdScale/100d;
        if(!Design.MotionEnabled){frame.Opacity=leaving?0:1;offset.Y=0;return;}
        var fade=new DoubleAnimation{From=leaving?1:0,To=leaving?0:1,Duration=TimeSpan.FromMilliseconds(220),EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
        var slide=new DoubleAnimation{From=offset.Y,To=leaving?-8*preferences.OsdScale/100d:0,Duration=TimeSpan.FromMilliseconds(220),EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},EnableDependentAnimation=true};
        Storyboard.SetTarget(fade,frame);Storyboard.SetTargetProperty(fade,"Opacity");Storyboard.SetTarget(slide,offset);Storyboard.SetTargetProperty(slide,"Y");
        animation=new Storyboard();animation.Children.Add(fade);animation.Children.Add(slide);animation.Begin();
    }
    private void StartExit(){if(disposed)return;Animate(true);if(Design.MotionEnabled)exitTimer.Start();else Hide();}
    internal void HoldForReview(){holdTimer.Stop();exitTimer.Stop();animation?.Stop();frame.Opacity=1;offset.Y=0;glass?.Hold();}
    public void Hide(){if(disposed)return;holdTimer.Stop();exitTimer.Stop();animation?.Stop();window.AppWindow.Hide();}
    public void Dispose(){if(disposed)return;Hide();disposed=true;if(frameHookAttached)RemoveWindowSubclass(hwnd,callback,0x4F5344);window.Close();}
    private void PrepareAlpha()
    {
        var margins=new Margins();DwmExtendFrameIntoClientArea(hwnd,ref margins);nint region=CreateRectRgn(0,0,0,0);
        var blur=new Blur{Flags=3,Enable=true,Region=region};DwmEnableBlurBehindWindow(hwnd,ref blur);DeleteObject(region);
    }
    private nint WindowMessage(nint window,uint message,nuint wp,nint lp,nuint id,nuint data)
    {
        if(message==OsdFramePolicy.StyleChanging&&lp!=0)
        {
            DefSubclassProc(window,message,wp,lp);
            OsdFramePolicy.Rewrite(unchecked((int)wp),lp,reviewOsdWindow);return 0;
        }
        // No non-client inset: the transparent XAML surface covers the full HWND.
        if(message==OsdFramePolicy.CalculateClient)return 0;
        if(message==0x84)return -1;if(message==0x21)return 3;
        if(message==0x14&&GetClientRect(window,out var rect)){FillRect((nint)wp,ref rect,GetStockObject(4));return 1;}
        if(message==0x31E)PrepareAlpha();return DefSubclassProc(window,message,wp,lp);
    }
    private void UpdateBackdropClip()
    {
        if(disposed||glass==null||frame.ActualWidth<=0||frame.ActualHeight<=0)return;
        double dpi=root.XamlRoot?.RasterizationScale??1;
        glass.UpdateClip((float)(root.ActualWidth*dpi),(float)(root.ActualHeight*dpi),(float)(12*dpi),(float)(12*dpi),(float)(frame.ActualWidth*dpi),(float)(frame.ActualHeight*dpi));
        if(glass.Error!=null)frame.Background=new SolidColorBrush(Color(107,"#0F172A"));
    }
    private static SolidColorBrush Brush(string hex)=>new(Parse(hex));
    private static Color Color(byte alpha,string hex){var c=Parse(hex);c.A=alpha;return c;}
    private static Color Parse(string hex)
    {uint n=Convert.ToUInt32(hex.TrimStart('#'),16);return Windows.UI.Color.FromArgb(hex.Length==9?(byte)(n>>24):(byte)255,(byte)(n>>16),(byte)(n>>8),(byte)n);}
    private Image OsdIcon(string key,string accent,double size)
    {
        string asset=key=="awake"?"balance":key;
        if(asset is not("office" or "balance" or "beast" or "geek" or "keyboard" or "touchpad" or "fan" or "lock" or "fn" or "caps" or "camera" or "mic" or "wifi" or "bluetooth"))asset="balance";
        originalAsset=asset+"-"+accent.TrimStart('#').ToUpperInvariant()+".svg";
        var source=new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Osd",originalAsset)));
        var image=new Image{Width=size,Height=size,Stretch=Stretch.Uniform,Source=source,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        originalIconLoaded=false;image.ImageOpened+=(_,_)=>originalIconLoaded=true;image.ImageFailed+=(_,e)=>{iconFailure=e.ErrorMessage;OpenRevo.Core.DiagnosticLog.Event("osd.icon_failed","error",new{asset=originalAsset,error=iconFailure});};return image;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Margins{public int Left,Right,Top,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Blur{public uint Flags;[MarshalAs(UnmanagedType.Bool)]public bool Enable;public nint Region;[MarshalAs(UnmanagedType.Bool)]public bool Transition;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    private delegate nint Subclass(nint window,uint message,nuint wp,nint lp,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)]private static extern bool SetWindowSubclass(nint window,Subclass callback,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(nint window,Subclass callback,nuint id);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint window,uint message,nuint wp,nint lp);
    [DllImport("dwmapi.dll")]private static extern int DwmExtendFrameIntoClientArea(nint window,ref Margins margins);
    [DllImport("dwmapi.dll")]private static extern int DwmEnableBlurBehindWindow(nint window,ref Blur blur);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attr,ref int value,int size);
    [DllImport("gdi32.dll")]private static extern nint CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int l,int t,int r,int b,int width,int height);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]private static extern nint GetStockObject(int obj);
    [DllImport("user32.dll")]private static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
