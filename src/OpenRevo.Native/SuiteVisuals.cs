using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace OpenRevo.Native;
internal static class SuiteVisuals
{
    internal static StackPanel Stack(double gap,params UIElement[] children){var p=Ui.Stack(children);p.Spacing=gap;return p;}
    internal static Border Rule(FrameworkElement child,double vertical=14)
    {
        var b=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='0,1,0,0' Padding='0,14,0,14'/>");
        b.Padding=new Thickness(0,vertical,0,vertical);b.Child=child;return b;
    }
    internal static void Flat(Button b,double height=28)
    {
        b.Padding=new Thickness(2,3,2,3);b.MinHeight=height;b.FontSize=12;b.BorderThickness=new Thickness(0);b.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);b.HorizontalAlignment=HorizontalAlignment.Stretch;b.HorizontalContentAlignment=HorizontalAlignment.Stretch;
    }
    internal static FrameworkElement SwitchIndicator(string family,bool on)
    {
        string fill=on?"SuiteSwitchOnFill":"SuiteOffFill",stroke=on?"SuiteSwitchAccent":"DesignStroke",knob=on?"SuiteSwitchAccent":"OverviewMuted";
        var indicator=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='40' Height='24' CornerRadius='12' Padding='3' BorderThickness='1' Background='{{ThemeResource {fill}}}' BorderBrush='{{ThemeResource {stroke}}}'><Ellipse Width='16' Height='16' HorizontalAlignment='Left' Fill='{{ThemeResource {knob}}}'><Ellipse.RenderTransform><TranslateTransform X='{(on?16:0)}'/></Ellipse.RenderTransform></Ellipse></Border>");SwitchFamily(indicator,family);return indicator;
    }
    internal static void SwitchFamily(FrameworkElement control,string family)
    {
        string light=family switch{"green"=>"208070","violet"=>"6556C7","rose"=>"A64F76","orange"=>"9B5F1B","cyan"=>"167BA1",_=>"3468CE"};
        string dark=family switch{"green"=>"80D4BF","violet"=>"B7A9FF","rose"=>"EFA6C5","orange"=>"EDBE80","cyan"=>"7BCEE6",_=>"92B9FF"};
        string Mix(string ink,string surface)=>"#"+string.Concat(Enumerable.Range(0,3).Select(i=>((int)Math.Round(Convert.ToInt32(ink.Substring(i*2,2),16)*.13+Convert.ToInt32(surface.Substring(i*2,2),16)*.87)).ToString("X2")));
        foreach(var(theme,ink,surface)in new[]{("Light",light,"F8F9FB"),("Default",dark,"252B37")})
        {
            var d=(ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><SolidColorBrush x:Key='SuiteSwitchOnFill' Color='{Mix(ink,surface)}'/><SolidColorBrush x:Key='SuiteSwitchAccent' Color='#{ink}'/></ResourceDictionary>");
            control.Resources.ThemeDictionaries[theme]=d;
        }
    }
}
internal sealed class SuiteWrapPanel:Panel
{
    internal double ItemGap{get;set;}=8;
    private readonly List<Windows.Foundation.Rect> slots=[];
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double width=double.IsFinite(available.Width)?available.Width:900,x=0,y=0,row=0;slots.Clear();
        foreach(var child in Children){child.Measure(new Windows.Foundation.Size(width,double.PositiveInfinity));double w=Math.Min(width,child.DesiredSize.Width),h=child.DesiredSize.Height;if(x>0&&x+w>width){x=0;y+=row+ItemGap;row=0;}slots.Add(new(x,y,w,h));x+=w+ItemGap;row=Math.Max(row,h);}return new(width,y+row);
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final){for(int i=0;i<Children.Count;i++)Children[i].Arrange(slots[i]);return final;}
}
