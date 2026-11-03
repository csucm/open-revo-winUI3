using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private double captionScale;
    [StructLayout(LayoutKind.Sequential)]private struct CaptionRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct CaptionPoint{public int X,Y;}
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint window,uint attribute,out CaptionRect rect,uint size);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out CaptionRect rect);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(nint window,ref CaptionPoint point);
    private void AlignCaption()
    {
        if(captionGrid==null||root.XamlRoot==null)return;
        double scale=captionScale=root.XamlRoot.RasterizationScale;
        if(scale<=0)return;
        double height=AppWindow.TitleBar.Height/scale;
        if(height<24||height>40)height=32;
        double offset=0;
        nint window=WinRT.Interop.WindowNative.GetWindowHandle(this);
        var client=new CaptionPoint();
        if(AppWindow.IsVisible && DwmGetWindowAttribute(window,5,out var buttons,16)==0 &&
           buttons.Bottom>buttons.Top && GetWindowRect(window,out var bounds) && ClientToScreen(window,ref client))
        {
            double center=((buttons.Top+buttons.Bottom)/2d-(client.Y-bounds.Top))/scale;
            double candidate=center-height/2;
            if(Math.Abs(candidate)<=8)offset=candidate;
        }
        root.RowDefinitions[0].Height=new GridLength(height);
        captionGrid.Height=height;
        double inset=AppWindow.TitleBar.RightInset/scale;
        captionGrid.Margin=new Thickness(12,0,inset>0?inset:138,0);
        if(captionGrid.RenderTransform is TranslateTransform transform)
        {
            if(Math.Abs(transform.Y-offset)>.01)transform.Y=offset;
        }
        else captionGrid.RenderTransform=new TranslateTransform{Y=offset};
        if(panelToggle!=null)panelToggle.Height=height;
        if(themeToggle!=null)themeToggle.Height=height;
    }
}
