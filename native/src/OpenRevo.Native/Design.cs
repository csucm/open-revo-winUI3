using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace OpenRevo.Native;

internal static class Design
{
    private static readonly UISettings Settings=new();
    internal static bool MotionEnabled=>Settings.AnimationsEnabled;
    internal static double FontSize(double requested)=>requested<=12?12:requested<20?14:requested<=24?24:28;
    internal static void Typography(DependencyObject parent)
    {
        if(parent is TextBlock text&&!text.FontFamily.Source.Contains("Icons",StringComparison.OrdinalIgnoreCase)&&!text.FontFamily.Source.Contains("Assets",StringComparison.OrdinalIgnoreCase))
            text.FontSize=FontSize(text.FontSize);
        if(parent is Control control)control.FontSize=FontSize(control.FontSize);
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)Typography(VisualTreeHelper.GetChild(parent,i));
    }
    internal static void AttachPressMotion(Button button)
    {
        void Animate(float target,bool release)
        {
            var visual=ElementCompositionPreview.GetElementVisual(button);
            visual.CenterPoint=new Vector3((float)button.ActualWidth/2,(float)button.ActualHeight/2,0);
            if(!MotionEnabled){visual.StopAnimation("Scale");visual.Scale=Vector3.One;return;}
            if(release)
            {
                var spring=visual.Compositor.CreateSpringVector3Animation();
                spring.FinalValue=Vector3.One;spring.DampingRatio=.88f;spring.Period=TimeSpan.FromMilliseconds(150);
                visual.StartAnimation("Scale",spring);
            }
            else
            {
                var animation=visual.Compositor.CreateVector3KeyFrameAnimation();animation.Duration=TimeSpan.FromMilliseconds(150);
                animation.InsertKeyFrame(1,new Vector3(target,target,1),visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f,0),new Vector2(0,1)));
                visual.StartAnimation("Scale",animation);
            }
        }
        button.AddHandler(UIElement.PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>Animate(.985f,false)),true);
        button.AddHandler(UIElement.PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>Animate(1,true)),true);
        button.PointerCaptureLost+=(_,_)=>Animate(1,true);
        button.PointerExited+=(_,_)=>Animate(1,false);
    }
}

public sealed partial class MainWindow
{
    private int designGeneration;
    private string lastDesignSurface="";
    private Storyboard? pageEntrance;
    private void ApplyPageDesign()
    {
        int generation=++designGeneration;
        DispatcherQueue.TryEnqueue(()=>
        {
            if(closed||generation!=designGeneration)return;
            root.UpdateLayout();Design.Typography(root);
            string surface=mini?"mini":currentPage+(customTuningOpen?"-tuning":colorTuningOpen?"-color":"");
            if(surface==lastDesignSurface)return;lastDesignSurface=surface;
            pageEntrance?.Stop();page.Opacity=1;
            if(!Design.MotionEnabled)return;
            var fade=new DoubleAnimation{From=0,To=1,Duration=TimeSpan.FromMilliseconds(250),EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            Storyboard.SetTarget(fade,page);Storyboard.SetTargetProperty(fade,"Opacity");
            pageEntrance=new Storyboard();pageEntrance.Children.Add(fade);pageEntrance.Begin();
        });
    }
    private static IconElement PageIcon(string id)
    {
        IconElement icon=id switch
        {
            "display" or "power"=>NavigationOriginalIcon(id),
            _=>new SymbolIcon(id switch{"devices"=>Symbol.Keyboard,"plugins"=>Symbol.Library,"settings"=>Symbol.Setting,_=>Symbol.Home})
        };
        icon.Width=20;icon.Height=20;icon.HorizontalAlignment=HorizontalAlignment.Center;icon.VerticalAlignment=VerticalAlignment.Center;
        return icon;
    }
}
