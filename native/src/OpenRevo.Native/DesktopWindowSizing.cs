using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private const int DesktopMinimumWidth=840,DesktopMinimumHeight=600;
    private readonly DesktopSurface desktopSurface=new();
    private bool desktopViewportQueued;
    [StructLayout(LayoutKind.Sequential)]private struct TrackPoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct WindowMinMax{public TrackPoint Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    private SizeInt32 MinimumDesktopPixels()
    {
        double dpi=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d;if(dpi<=0)dpi=1;
        var work=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        return new(Math.Min((int)Math.Ceiling(DesktopMinimumWidth*dpi),Math.Max(1,work.Width-16)),Math.Min((int)Math.Ceiling(DesktopMinimumHeight*dpi),Math.Max(1,work.Height-16)));
    }
    private void ApplyDesktopMinMax(nint pointer)
    {
        if(mini||pointer==0)return;var info=Marshal.PtrToStructure<WindowMinMax>(pointer);var min=MinimumDesktopPixels();
        info.MinTrack.X=Math.Max(info.MinTrack.X,min.Width);info.MinTrack.Y=Math.Max(info.MinTrack.Y,min.Height);Marshal.StructureToPtr(info,pointer,false);
    }
    private void QueueDesktopViewport()
    {
        if(closed||desktopViewportQueued)return;desktopViewportQueued=true;
        DispatcherQueue.TryEnqueue(()=>{desktopViewportQueued=false;if(!closed)ArrangeDesktopViewport();});
    }
    private void UpdateDesktopNavigationMode()
    {
        if(mini)return;double dpi=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d;if(dpi<=0)dpi=1;
        double width=AppWindow.Size.Width/dpi;
        var mode=width<1180?NavigationViewPaneDisplayMode.LeftCompact:NavigationViewPaneDisplayMode.Left;
        if(navigation.PaneDisplayMode!=mode){navigation.PaneDisplayMode=mode;navigation.IsPaneOpen=mode==NavigationViewPaneDisplayMode.Left;}
    }
}
