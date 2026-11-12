using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace OpenRevo.Native;

// Shared motion language. Only active interactions allocate animations.
internal static class MotionDesign
{
    private static readonly UISettings Settings=new();
    private static string speed="normal";
    internal static void Configure(string choice)=>speed=choice;
    internal static bool Enabled=>speed!="off"&&Settings.AnimationsEnabled;
    internal static int Duration(int milliseconds)=>(int)Math.Round(milliseconds*(speed switch
    {
        "fast"=>.325, "slow"=>.75, _=>.5
    }));
    internal const int Enter=250,Exit=200,Press=120,Release=180,Data=240;
    internal static CompositionEasingFunction Easing(Compositor compositor,bool leaving=false)=>
        compositor.CreateCubicBezierEasingFunction(leaving?new(.4f,0):new(.2f,0),leaving?new(1,1):new(.2f,1));
    internal static Windows.UI.Composition.CompositionEasingFunction Easing(Windows.UI.Composition.Compositor compositor,bool leaving=false)=>
        compositor.CreateCubicBezierEasingFunction(leaving?new(.4f,0):new(.2f,0),leaving?new(1,1):new(.2f,1));
    internal static void Scale(Visual visual,Vector3 target,int milliseconds)
    {
        var animation=visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration=TimeSpan.FromMilliseconds(Duration(milliseconds));animation.StopBehavior=AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertKeyFrame(1,target,Easing(visual.Compositor));visual.StartAnimation("Scale",animation);
    }
    internal static void Surface(FrameworkElement element,bool leaving,float distance=6,bool fresh=true,int? milliseconds=null)
    {
        if(!element.IsLoaded)return;
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);
        var visual=ElementCompositionPreview.GetElementVisual(element);
        if(!Enabled){Reset(element);visual.Opacity=leaving?0:1;return;}
        if(!leaving&&fresh){visual.Opacity=0;visual.Properties.InsertVector3("Translation",new(0,distance,0));}
        var easing=Easing(visual.Compositor,leaving);var duration=TimeSpan.FromMilliseconds(Duration(milliseconds??(leaving?Exit:Enter)));
        var fade=visual.Compositor.CreateScalarKeyFrameAnimation();fade.Duration=duration;fade.StopBehavior=AnimationStopBehavior.LeaveCurrentValue;
        fade.InsertKeyFrame(1,leaving?0:1,easing);visual.StartAnimation("Opacity",fade);
        var move=visual.Compositor.CreateVector3KeyFrameAnimation();move.Duration=duration;move.StopBehavior=AnimationStopBehavior.LeaveCurrentValue;
        move.InsertKeyFrame(1,new(0,leaving?-distance:0,0),easing);visual.StartAnimation("Translation",move);
    }
    internal static void Reset(FrameworkElement element)
    {
        if(!element.IsLoaded)return;
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);
        var visual=ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");visual.StopAnimation("Translation");visual.Opacity=1;
        visual.Properties.InsertVector3("Translation",Vector3.Zero);
    }
}
