using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    // Keep native input/automation. Mini cards and the desktop lighting editor
    // share this geometry; visible track endpoints match the thumb centers.
    private static FrameworkElement BrightnessTrack(Slider slider,bool stepped=false)
    {
        slider.MinHeight=28;slider.Height=28;slider.Margin=new Thickness(0);
        slider.HorizontalAlignment=HorizontalAlignment.Stretch;
        slider.TickPlacement=TickPlacement.None;slider.TickFrequency=0;
        slider.SnapsTo=SliderSnapsTo.StepValues;
        slider.Resources["SliderHorizontalHeight"]=28d;
        slider.Resources["SliderPreContentMargin"]=12d;
        slider.Resources["SliderPostContentMargin"]=12d;
        var host=new Grid{Height=28,HorizontalAlignment=HorizontalAlignment.Stretch};
        host.Children.Add(slider);
        var marks=new Canvas{IsHitTestVisible=false};
        host.Children.Add(marks);
        if(stepped)for(int i=1;i<4;i++)
            marks.Children.Add((Rectangle)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='1' Height='4' Fill='{ThemeResource DesignCardFill}' Opacity='0.8'/>"));
        FrameworkElement? Part(DependencyObject parent,string name)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is FrameworkElement element&&element.Name==name)return element;
                if(Part(child,name) is {} found)return found;
            }
            return null;
        }
        void ArrangeTrack()
        {
            if(Part(slider,"HorizontalThumb") is not Thumb thumb||
               Part(slider,"HorizontalTrackRect") is not Rectangle track||
               Part(slider,"HorizontalDecreaseRect") is not Rectangle progress)return;
            double thumbWidth=thumb.ActualWidth>0?thumb.ActualWidth:thumb.Width;
            if(!double.IsFinite(thumbWidth)||thumbWidth<=0)return;
            double inset=thumbWidth/2;
            track.Margin=new Thickness(inset,0,inset,0);
            progress.RenderTransform=new TranslateTransform{X=inset};
            double travel=Math.Max(0,slider.ActualWidth-thumbWidth);
            for(int i=0;i<marks.Children.Count;i++)
            {
                var mark=(Rectangle)marks.Children[i];
                Canvas.SetLeft(mark,inset+travel*(i+1)/4-.5);
                Canvas.SetTop(mark,12);
                mark.Visibility=Math.Abs(slider.Value-(i+1))<.01?Visibility.Collapsed:Visibility.Visible;
            }
        }
        slider.Loaded+=(_,_)=>ArrangeTrack();
        slider.SizeChanged+=(_,_)=>ArrangeTrack();
        slider.ValueChanged+=(_,_)=>ArrangeTrack();
        return host;
    }
    private string MiniLightingTitle=>"键盘背光："+(!lightingReadFresh?"待读取":rgbLevel==0?"关闭":$"{rgbLevel} / 4");
}
