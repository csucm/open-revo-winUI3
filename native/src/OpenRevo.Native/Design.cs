using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenRevo.Native;

internal static class Design
{
    private static readonly ConditionalWeakTable<Button,object> MotionButtons=new();
    internal static bool MotionEnabled=>MotionDesign.Enabled;
    internal static double FontSize(double requested)=>requested<=12?12:requested<=14?14:requested<=20?16:24;
    internal static void Typography(DependencyObject parent)
    {
        if(parent is TextBlock text&&!text.FontFamily.Source.Contains("Icons",StringComparison.OrdinalIgnoreCase)&&!text.FontFamily.Source.Contains("Assets",StringComparison.OrdinalIgnoreCase))
            text.FontSize=FontSize(text.FontSize);
        if(parent is Control control)control.FontSize=FontSize(control.FontSize);
        if(parent is Button button)AttachPressMotion(button);
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)Typography(VisualTreeHelper.GetChild(parent,i));
    }
    internal static void AttachPressMotion(Button button)
    {
        if(MotionButtons.TryGetValue(button,out _))return;MotionButtons.Add(button,new());
        bool pressed=false;Microsoft.UI.Composition.Visual? activeVisual=null;
        void Animate(float target,bool release)
        {
            var visual=activeVisual=ElementCompositionPreview.GetElementVisual(button);
            visual.CenterPoint=new Vector3((float)button.ActualWidth/2,(float)button.ActualHeight/2,0);
            if(!MotionEnabled){visual.StopAnimation("Scale");visual.Scale=Vector3.One;return;}
            if(release)
            {
                var spring=visual.Compositor.CreateSpringVector3Animation();
                spring.FinalValue=Vector3.One;spring.DampingRatio=1;spring.Period=TimeSpan.FromMilliseconds(MotionDesign.Duration(MotionDesign.Release));
                visual.StartAnimation("Scale",spring);
            }
            else
            {
                MotionDesign.Scale(visual,new(target,target,1),MotionDesign.Press);
            }
        }
        void Release(){if(!pressed)return;pressed=false;Animate(1,true);}
        button.AddHandler(UIElement.PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>{if(button.IsEnabled){pressed=true;Animate(.985f,false);}}),true);
        button.AddHandler(UIElement.PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>Release()),true);
        button.PointerCaptureLost+=(_,_)=>Release();button.PointerExited+=(_,_)=>Release();
        button.Unloaded+=(_,_)=>{pressed=false;if(activeVisual!=null){activeVisual.StopAnimation("Scale");activeVisual.Scale=Vector3.One;activeVisual=null;}};
    }
}

public sealed partial class MainWindow
{
    private int designGeneration;
    private string lastDesignSurface="";
    private void ApplyPageDesign()
    {
        int generation=++designGeneration;
        bool shapeChange=panelShapeChanging;
        string surface=mini?"mini":currentPage+(customTuningOpen?"-tuning":colorTuningOpen?"-color":"");
        if(shapeChange)
        {
            Design.Typography(root);lastDesignSurface=surface;
            MotionDesign.Reset(page);page.Opacity=1;
            return;
        }
        DispatcherQueue.TryEnqueue(()=>
        {
            if(closed||generation!=designGeneration)return;
            root.UpdateLayout();Design.Typography(root);
            if(surface==lastDesignSurface)return;lastDesignSurface=surface;
            MotionDesign.Reset(page);page.Opacity=1;
            if(shapeChange||!Design.MotionEnabled)return;
            MotionDesign.Surface(page,false,0);
        });
    }
    private static IconElement PageIcon(string id)
    {
        IconElement icon=ApprovedIcons.Create(id switch{"overview"=>"home","devices"=>"lighting",_=>id},40);
        icon.HorizontalAlignment=HorizontalAlignment.Center;icon.VerticalAlignment=VerticalAlignment.Center;
        return icon;
    }
}
