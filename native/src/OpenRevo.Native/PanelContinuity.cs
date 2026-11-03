using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.Graphics;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly Dictionary<string,FrameworkElement> panelMotionAnchors=[];
    private int lastPanelMotionFrames;
    private CancellationTokenSource? panelMotionStop;
    private T PanelAnchor<T>(string key,T element) where T:FrameworkElement
    {panelMotionAnchors[key]=element;return element;}
    private async Task AnimatePanelContinuity(RectInt32 from,RectInt32 target,
        double restoreWidth,double restoreHeight,bool showFallback)
    {
        var visual=ElementCompositionPreview.GetElementVisual(navigation);
        var oldScale=visual.Scale;var oldCenter=visual.CenterPoint;
        var oldHorizontal=root.HorizontalAlignment;var oldVertical=root.VerticalAlignment;
        double oldSurfaceMinWidth=windowSurface.MinWidth,oldSurfaceMinHeight=windowSurface.MinHeight;
        var oldSurfaceHorizontal=windowSurface.HorizontalAlignment;
        var oldSurfaceVertical=windowSurface.VerticalAlignment;
        bool oldHitTest=navigation.IsHitTestVisible;
        using var stop=new CancellationTokenSource();panelMotionStop=stop;
        var captionVisual=captionGrid==null?null:ElementCompositionPreview.GetElementVisual(captionGrid);
        var oldCaptionScale=captionVisual?.Scale??Vector3.One;
        lastPanelMotionFrames=0;
        try
        {
            root.HorizontalAlignment=HorizontalAlignment.Left;root.VerticalAlignment=VerticalAlignment.Top;
            navigation.IsHitTestVisible=false;
            GetPanelClientRect(captionWindow,out var client);
            int borderWidth=Math.Max(0,from.Width-(client.Right-client.Left));
            int borderHeight=Math.Max(0,from.Height-(client.Bottom-client.Top));
            double dpi=GetDpiForWindow(captionWindow)/96d;if(dpi<=0)dpi=1;
            // Native resize can lead XAML layout by one frame. Paint the entire
            // swept client area before moving, so growth cannot expose a gap.
            windowSurface.HorizontalAlignment=HorizontalAlignment.Left;
            windowSurface.VerticalAlignment=VerticalAlignment.Top;
            windowSurface.MinWidth=Math.Max(oldSurfaceMinWidth,Math.Ceiling(Math.Max(from.Width,target.Width)/dpi));
            windowSurface.MinHeight=Math.Max(oldSurfaceMinHeight,Math.Ceiling(Math.Max(from.Height,target.Height)/dpi));
            root.UpdateLayout();
            double finalWidth=Math.Max(1,root.Width),finalBodyHeight=Math.Max(1,root.Height-root.RowDefinitions[0].ActualHeight);
            void SetFrame(RectInt32 bounds)
            {
                AppWindow.MoveAndResize(bounds);
                visual.CenterPoint=Vector3.Zero;
                visual.Scale=new((float)((bounds.Width-borderWidth)/dpi/finalWidth),
                    (float)Math.Max(.01,((bounds.Height-borderHeight)/dpi-root.RowDefinitions[0].ActualHeight)/finalBodyHeight),1);
                if(captionGrid!=null)
                {
                    captionGrid.Width=Math.Max(1,finalWidth-captionGrid.Margin.Left-captionGrid.Margin.Right);
                    if(captionVisual!=null){captionVisual.CenterPoint=Vector3.Zero;captionVisual.Scale=new(visual.Scale.X,1,1);}
                }
            }
            // Transform the common ancestor only. Cards and buttons must stay attached
            // to their background instead of taking independent connected-animation paths.
            SetFrame(from);
            await NextPanelFrame();
            var commit=visual.Compositor.RequestCommitAsync().AsTask();await Task.WhenAny(commit,Task.Delay(80));
            if(commit.IsCompleted)await commit;
            if(closed)return;
            ResetCaptionHover();panelCompositionGuard?.Reveal();
            if(showFallback&&!trayHidden&&!exitRequested)AppWindow.Show(false);
            var start=new PanelMotionRect(from.X,from.Y,from.Width,from.Height);
            var end=new PanelMotionRect(target.X,target.Y,target.Width,target.Height);
            int duration=MotionDesign.Duration(PanelMotionGeometry.DurationMilliseconds);
            MotionDesign.Scale(visual,Vector3.One,PanelMotionGeometry.DurationMilliseconds);
            if(captionVisual!=null)MotionDesign.Scale(captionVisual,Vector3.One,PanelMotionGeometry.DurationMilliseconds);
            lastPanelMotionFrames=await NativePanelMotion.Run(captionWindow,start,end,stop.Token,duration);
            if(!closed)AppWindow.MoveAndResize(target);
        }
        catch(Exception error){DiagnosticLog.Failure("window.continuity",error);if(!closed)AppWindow.MoveAndResize(target);}
        finally
        {
            panelMotionStop=null;visual.StopAnimation("Scale");
            if(captionVisual!=null){captionVisual.StopAnimation("Scale");captionVisual.Scale=oldCaptionScale;}
            visual.Scale=oldScale;visual.CenterPoint=oldCenter;
            if(captionGrid!=null)captionGrid.Width=double.NaN;
            windowSurface.MinWidth=oldSurfaceMinWidth;windowSurface.MinHeight=oldSurfaceMinHeight;
            windowSurface.HorizontalAlignment=oldSurfaceHorizontal;windowSurface.VerticalAlignment=oldSurfaceVertical;
            root.Width=restoreWidth;root.Height=restoreHeight;
            root.HorizontalAlignment=oldHorizontal;root.VerticalAlignment=oldVertical;
            navigation.IsHitTestVisible=oldHitTest;
            if(!closed){AlignCaption();ArrangeDesktopViewport();root.UpdateLayout();ResetCaptionHover();}
            await FinishPanelShape(showFallback);
            DiagnosticLog.Record("window.continuity_completed",new{mini,frames=lastPanelMotionFrames,durationMs=MotionDesign.Duration(PanelMotionGeometry.DurationMilliseconds)});
        }
    }
}
