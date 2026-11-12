using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using OpenRevo.Core;
namespace OpenRevo.Native;

/// <summary>Compact native OSD: alpha/desktop acrylic, no activation or input capture.</summary>
internal sealed class NativeOsd:IDisposable
{
    private readonly Window window;
    private readonly Grid root=new(),cardContent=new();
    private readonly Border frame=new();
    private readonly DispatcherQueueTimer holdTimer,opacityTimer;
    private readonly System.Diagnostics.Stopwatch opacityClock=new();
    private int presentationGeneration;
    private byte currentOpacity,targetOpacity;
    private byte animationFrom,animationTo;
    private int opacityDuration;
    private bool leavingOpacity;
    private readonly nint hwnd;
    private readonly Subclass callback;
    private OpenRevo.Core.SystemPreferences preferences=new();
    internal bool CapsuleRegionApplied{get;private set;}
    internal bool WindowTranslucencyApplied{get;private set;}
    private bool disposed;
    private TextBlock? label;
    private Border? iconBadge;
    private bool originalIconLoaded;
    private string originalAsset="",iconFailure="";
    private object? frameDiagnostics;
    private string lastFrameSignature="";
    private readonly bool reviewOsdWindow;
    private readonly bool frameHookAttached;
    internal object Diagnostics=>new{Theme,preferences.OsdScale,preferences.OsdPosition,frame=frameDiagnostics,layout=LayoutReport,CapsuleRegionApplied};
    internal OsdMessage Message{get;private set;}=new("Open-Revo · 屏幕提示预览","balance","#38BDF8");
    internal byte WindowOpacity
    {
        get=>GetLayeredWindowAttributes(hwnd,out _,out byte alpha,out _)?alpha:(byte)255;
    }
    internal float CardOpacity=>frame.IsLoaded
        ?Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(frame).Opacity:1;
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
    internal bool BackdropClipped=>CapsuleRegionApplied;
    internal object LayoutReport
    {
        get{GetClientRect(hwnd,out var client);return new{outerWidth=window.AppWindow.Size.Width,outerHeight=window.AppWindow.Size.Height,clientWidth=client.Right-client.Left,clientHeight=client.Bottom-client.Top,frameHookAttached,NativeFrameBorderless,root.ActualWidth,root.ActualHeight,desiredWidth=root.DesiredSize.Width,desiredHeight=root.DesiredSize.Height,dpi=GetDpiForWindow(hwnd),raster=root.XamlRoot?.RasterizationScale,textRight=label?.TransformToVisual(root).TransformPoint(new Point()).X+label?.ActualWidth,rightPadding=frame.Padding.Right,TextFits,IconFits,originalAsset,originalIconLoaded,iconFailure};}
    }
    public NativeOsd(DispatcherQueue dispatcher,string windowTitle="Open-Revo OSD",bool reviewWindow=false)
    {
        OpenRevo.Core.DiagnosticLog.Record("osd.create.started");
        window=new Window();
        reviewOsdWindow=reviewWindow;
        window.Title=windowTitle;window.Content=root;
        root.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);root.Padding=new Thickness(12);root.Children.Add(frame);frame.Child=cardContent;
        frame.HorizontalAlignment=HorizontalAlignment.Left;frame.VerticalAlignment=VerticalAlignment.Top;
        var presenter=OverlappedPresenter.Create();presenter.SetBorderAndTitleBar(false,false);presenter.IsAlwaysOnTop=true;presenter.IsResizable=false;presenter.IsMinimizable=false;presenter.IsMaximizable=false;
        window.AppWindow.SetPresenter(presenter);window.AppWindow.IsShownInSwitchers=reviewWindow;
        hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback=WindowMessage;frameHookAttached=SetWindowSubclass(hwnd,callback,0x4F5344,0);
        if(!frameHookAttached)OpenRevo.Core.DiagnosticLog.Failure("osd.frame_hook.failed",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        OpenRevo.Core.DiagnosticLog.Record("osd.create.frame");
        ApplyNativeFrame();
        holdTimer=dispatcher.CreateTimer();holdTimer.Interval=TimeSpan.FromMilliseconds(1600);holdTimer.IsRepeating=false;holdTimer.Tick+=(_,_)=>StartExit();
        opacityTimer=dispatcher.CreateTimer();
        opacityTimer.Interval=TimeSpan.FromMilliseconds(16);
        opacityTimer.IsRepeating=true;
        opacityTimer.Tick+=(_,_)=>TickOpacity();
        Configure(preferences);
    }
    public void Configure(OpenRevo.Core.SystemPreferences config)=>preferences=config;

    private void ApplyOsdRegion(double dpi,int width,int height)
    {
        CapsuleRegionApplied=false;
        WindowTranslucencyApplied=false;
        const int layered=0x80000;
        nint style=GetWindowLongPtr(hwnd,-20);
        if((style&layered)==0)SetWindowLongPtr(hwnd,-20,style|layered);
        byte opacity=Theme switch{"pill"=>213,"fade"=>214,_=>226};
        targetOpacity=opacity;
        WindowTranslucencyApplied=SetLayeredWindowAttributes(hwnd,0,currentOpacity,2);
        int left=(int)Math.Round(root.Padding.Left*dpi);
        int top=(int)Math.Round(root.Padding.Top*dpi);
        int right=Math.Min(width,left+(int)Math.Floor(frame.DesiredSize.Width*dpi));
        int bottom=Math.Min(height,top+(int)Math.Floor(frame.DesiredSize.Height*dpi));
        int diameter=Math.Min(bottom-top,(int)Math.Round(frame.CornerRadius.TopLeft*2*dpi));
        nint region=CreateRoundRectRgn(left+1,top+1,right-1,bottom-1,Math.Max(1,diameter-2),Math.Max(1,diameter-2));
        if(region==0)return;
        // Windows owns the region once SetWindowRgn succeeds.
        CapsuleRegionApplied=SetWindowRgn(hwnd,region,true)!=0;
        if(!CapsuleRegionApplied)DeleteObject(region);
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
        if(disposed)return;
        bool fresh=!window.AppWindow.IsVisible;
        int generation=++presentationGeneration;
        holdTimer.Stop();
        opacityTimer.Stop();
        if(fresh)currentOpacity=0;
        Message=message;
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
        if(fresh)ApplyNativeFrame();
        ApplyOsdRegion(dpi,width,height);
        // Paint the complete card at zero window opacity before revealing it.
        // Fading XAML alone exposed the opaque backing surface between frames.
        MotionDesign.Reset(frame);
        if(fresh)
        {
            window.AppWindow.Show(false);
            _=RevealAfterCommit(generation);
        }
        else StartOpacity(false);
        OpenRevo.Core.DiagnosticLog.Record("osd.presentation",new{Theme,preferences.OsdScale,preferences.OsdPosition,message=message.Icon});
    }
    private void BuildContent(OsdMessage message)
    {
        double s=preferences.OsdScale/100d;bool pill=Theme=="pill",fade=Theme=="fade";
        cardContent.Children.Clear();cardContent.ColumnDefinitions.Clear();cardContent.ColumnSpacing=(pill?10:12)*s;
        cardContent.ColumnDefinitions.Add(new(){Width=GridLength.Auto});cardContent.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        frame.Padding=pill?new Thickness(10*s,6*s,(Preview?24:18)*s,6*s):fade?new Thickness(14*s,6*s,28*s,6*s):new Thickness(8*s,6*s,(Preview?12:8)*s,6*s);
        frame.CornerRadius=new CornerRadius(pill?22*s:fade?4*s:6*s);frame.BorderThickness=fade?new Thickness(3.5*s,0,0,0):new Thickness(0);
        frame.BorderBrush=Brush(message.Accent);
        if(fade)
        {
            var gradient=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,0)};
            gradient.GradientStops.Add(new(){Color=Parse("#1F2F46"),Offset=0});gradient.GradientStops.Add(new(){Color=Parse("#344459"),Offset=.75});gradient.GradientStops.Add(new(){Color=Parse("#49586C"),Offset=1});frame.Background=gradient;
        }
        else if(pill)
        {
            var glassFill=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1)};
            glassFill.GradientStops.Add(new(){Color=Parse("#3C4C63"),Offset=0});
            glassFill.GradientStops.Add(new(){Color=Parse("#233146"),Offset=1});
            frame.Background=glassFill;
        }
        else frame.Background=Brush("#182232");
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
    private async Task RevealAfterCommit(int generation)
    {
        try
        {
            root.UpdateLayout();
            var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(root);
            await visual.Compositor.RequestCommitAsync();
            // Let DWM consume the first committed card while the HWND is invisible.
            await Task.Delay(32);
            if(disposed||generation!=presentationGeneration)return;
            MotionDesign.Reset(frame);
            StartOpacity(false);
        }
        catch(Exception error)
        {
            OpenRevo.Core.DiagnosticLog.Failure("osd.reveal.failed",error);
            if(!disposed&&generation==presentationGeneration)Hide();
        }
    }
    private void SetOpacity(byte opacity)
    {
        if(SetLayeredWindowAttributes(hwnd,0,opacity,2))
            currentOpacity=opacity;
    }
    private void StartOpacity(bool leaving)
    {
        opacityTimer.Stop();
        leavingOpacity=leaving;
        animationFrom=currentOpacity;
        animationTo=leaving?(byte)0:targetOpacity;
        opacityDuration=MotionDesign.Duration(leaving?150:180);
        if(!Design.MotionEnabled||animationFrom==animationTo)
        {
            SetOpacity(animationTo);
            FinishOpacity();
            return;
        }
        opacityClock.Restart();
        opacityTimer.Start();
    }
    private void TickOpacity()
    {
        if(disposed){opacityTimer.Stop();return;}
        double t=Math.Clamp(opacityClock.Elapsed.TotalMilliseconds/opacityDuration,0,1);
        double eased=leavingOpacity?t*t:1-Math.Pow(1-t,3);
        SetOpacity((byte)Math.Round(animationFrom+(animationTo-animationFrom)*eased));
        if(t>=1){opacityTimer.Stop();FinishOpacity();}
    }
    private void FinishOpacity()
    {
        if(leavingOpacity)Hide();
        else holdTimer.Start();
    }
    private void StartExit()
    {
        if(disposed)return;
        StartOpacity(true);
    }
    internal void HoldForReview()
    {
        ++presentationGeneration;
        holdTimer.Stop();
        opacityTimer.Stop();
        MotionDesign.Reset(frame);
        SetOpacity(targetOpacity);
    }
    public void Hide()
    {
        if(disposed)return;
        ++presentationGeneration;
        holdTimer.Stop();
        opacityTimer.Stop();
        SetOpacity(0);
        window.AppWindow.Hide();
        MotionDesign.Reset(frame);
    }
    public void Dispose()
    {
        if(disposed)return;
        Hide();
        disposed=true;
        if(frameHookAttached)RemoveWindowSubclass(hwnd,callback,0x4F5344);
        window.Close();
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
        return DefSubclassProc(window,message,wp,lp);
    }
    private static SolidColorBrush Brush(string hex)=>new(Parse(hex));
    private static Color Color(byte alpha,string hex){var c=Parse(hex);c.A=alpha;return c;}
    private static Color Parse(string hex)
    {uint n=Convert.ToUInt32(hex.TrimStart('#'),16);return Windows.UI.Color.FromArgb(hex.Length==9?(byte)(n>>24):(byte)255,(byte)(n>>16),(byte)(n>>8),(byte)n);}
    private Image OsdIcon(string key,string accent,double size)
    {
        string asset=key switch{"office"=>"mode-office","balance"=>"mode-balanced","beast"=>"mode-performance","geek"=>"mode-custom","lock"=>"win-lock","fn"=>"fn-lock","caps"=>"caps",_=>key};
        string[] allowed=["mode-office","mode-balanced","mode-performance","mode-custom","win-lock","fn-lock","caps","keyboard","touchpad","fan","camera","mic","wifi","bluetooth","awake","display","refresh","brightness","battery","lighting","osd","gpu-integrated","gpu-hybrid","gpu-discrete"];
        if(!allowed.Contains(asset))asset="info";
        bool on=accent is not("#94A3B8" or "#F43F5E");originalAsset=asset+"-dark"+(on?"":"-off")+".svg";
        var source=new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Approved",originalAsset)));
        var image=new Image{Width=size,Height=size,Stretch=Stretch.Uniform,Source=source,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        originalIconLoaded=false;image.ImageOpened+=(_,_)=>originalIconLoaded=true;image.ImageFailed+=(_,e)=>{iconFailure=e.ErrorMessage;OpenRevo.Core.DiagnosticLog.Event("osd.icon_failed","error",new{asset=originalAsset,error=iconFailure});};return image;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    private delegate nint Subclass(nint window,uint message,nuint wp,nint lp,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)]private static extern bool SetWindowSubclass(nint window,Subclass callback,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(nint window,Subclass callback,nuint id);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint window,uint message,nuint wp,nint lp);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attr,ref int value,int size);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int l,int t,int r,int b,int width,int height);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]private static extern nint GetStockObject(int obj);
    [DllImport("user32.dll")]private static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetLayeredWindowAttributes(nint hwnd,out uint colorKey,out byte alpha,out uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint hwnd,uint colorKey,byte alpha,uint flags);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}



