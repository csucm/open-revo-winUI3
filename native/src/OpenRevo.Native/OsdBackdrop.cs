using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;

namespace OpenRevo.Native;

// The cinema/fade skins need actual alpha outside their XAML content.
internal sealed class OsdTransparentBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? clear;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target,XamlRoot root)
    {
        base.OnTargetConnected(target,root);
        compositor=new();clear=compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));target.SystemBackdrop=clear;
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {target.SystemBackdrop=null;clear?.Dispose();compositor?.Dispose();clear=null;compositor=null;base.OnTargetDisconnected(target);}
}
internal sealed class OsdGlassBackdrop : SystemBackdrop
{
    private Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? controller;
    private ICompositionSupportsSystemBackdrop? target;
    private Windows.UI.Composition.CompositionMaskBrush? mask;
    private Windows.UI.Composition.ShapeVisual? shapeVisual;
    private Windows.UI.Composition.CompositionSpriteShape? shape;
    private Windows.UI.Composition.CompositionRoundedRectangleGeometry? geometry;
    private Windows.UI.Composition.CompositionVisualSurface? surface;
    private Windows.UI.Composition.CompositionSurfaceBrush? surfaceBrush;
    private Windows.UI.Composition.CompositionColorBrush? white;
    private float width=1,height=1,left,top,cardWidth=1,cardHeight=1;
    internal bool Clipped{get;private set;}
    internal string? Error{get;private set;}
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connected,XamlRoot root)
    {
        base.OnTargetConnected(connected,root);target=connected;
        if(!Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()){Error="Desktop Acrylic not supported";return;}
        var config=GetDefaultSystemBackdropConfiguration(connected,root);config.IsInputActive=true;
        controller=new(){TintColor=Windows.UI.Color.FromArgb(255,15,23,42),TintOpacity=.42f,LuminosityOpacity=.1f,FallbackColor=Windows.UI.Color.FromArgb(215,15,23,42)};
        controller.SetSystemBackdropConfiguration(config);controller.AddSystemBackdropTarget(connected);ApplyMask();
    }
    internal void UpdateClip(float clientWidth,float clientHeight,float x,float y,float w,float h)
    {width=Math.Max(1,clientWidth);height=Math.Max(1,clientHeight);left=x;top=y;cardWidth=Math.Max(1,w);cardHeight=Math.Max(1,h);ApplyMask();}
    private void ApplyMask()
    {
        if(target?.SystemBackdrop is not{} source||Error!=null)return;
        try
        {
            if(mask==null)
            {
                var compositor=source.Compositor;
                geometry=compositor.CreateRoundedRectangleGeometry();white=compositor.CreateColorBrush(Microsoft.UI.Colors.White);
                shape=compositor.CreateSpriteShape(geometry);shape.FillBrush=white;
                shapeVisual=compositor.CreateShapeVisual();shapeVisual.Shapes.Add(shape);
                surface=compositor.CreateVisualSurface();surface.SourceVisual=shapeVisual;
                surfaceBrush=compositor.CreateSurfaceBrush(surface);surfaceBrush.Stretch=Windows.UI.Composition.CompositionStretch.Fill;
                mask=compositor.CreateMaskBrush();mask.Source=source;mask.Mask=surfaceBrush;
            }
            else if(!source.Equals(mask))mask.Source=source;
            shapeVisual!.Size=new System.Numerics.Vector2(width,height);surface!.SourceSize=shapeVisual.Size;
            geometry!.Size=new System.Numerics.Vector2(cardWidth,cardHeight);geometry.CornerRadius=new System.Numerics.Vector2(cardHeight/2);
            shape!.Offset=new System.Numerics.Vector2(left,top);
            target.SystemBackdrop=mask;Clipped=true;
        }
        catch(Exception error)
        {
            Error=error.Message;Clipped=false;
            target.SystemBackdrop=source.Compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
    internal void Animate(bool leaving,bool enabled,double scale,double dpi)
    {
        if(shape==null||white==null)return;
        shape.StopAnimation("Offset");white.StopAnimation("Color");
        if(!enabled){shape.Offset=new(left,top);white.Color=Windows.UI.Color.FromArgb(leaving?(byte)0:(byte)255,255,255,255);return;}
        var compositor=white.Compositor;var easing=compositor.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1));
        var position=compositor.CreateVector2KeyFrameAnimation();position.Duration=TimeSpan.FromMilliseconds(220);
        position.InsertKeyFrame(0,new(left,top+(leaving?0:(float)(-8*scale*dpi))));position.InsertKeyFrame(1,new(left,top+(leaving?(float)(-8*scale*dpi):0)),easing);shape.StartAnimation("Offset",position);
        var fade=compositor.CreateColorKeyFrameAnimation();fade.Duration=TimeSpan.FromMilliseconds(220);
        fade.InsertKeyFrame(0,Windows.UI.Color.FromArgb(leaving?(byte)255:(byte)0,255,255,255));fade.InsertKeyFrame(1,Windows.UI.Color.FromArgb(leaving?(byte)0:(byte)255,255,255,255),easing);white.StartAnimation("Color",fade);
    }
    internal void Hold(){shape?.StopAnimation("Offset");white?.StopAnimation("Color");if(shape!=null)shape.Offset=new(left,top);if(white!=null)white.Color=Microsoft.UI.Colors.White;}
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop connected,XamlRoot root)
    {base.OnDefaultSystemBackdropConfigurationChanged(connected,root);GetDefaultSystemBackdropConfiguration(connected,root).IsInputActive=true;ApplyMask();}
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connected)
    {
        controller?.RemoveSystemBackdropTarget(connected);controller?.Dispose();controller=null;
        mask?.Dispose();surfaceBrush?.Dispose();surface?.Dispose();shapeVisual?.Dispose();shape?.Dispose();geometry?.Dispose();white?.Dispose();
        mask=null;target=null;base.OnTargetDisconnected(connected);
    }
}