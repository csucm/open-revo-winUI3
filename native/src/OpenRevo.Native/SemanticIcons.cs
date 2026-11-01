using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static string SemanticBrush(string key)=>key switch{"overview" or "cpu" or "zap"=>"SemanticBlue","display" or "tuning" or "rocket"=>"SemanticPurple","power" or "gpu" or "leaf"=>"SemanticGreen","devices" or "sun" or "star"=>"SemanticAmber","plugins" or "color"=>"SemanticPink","fan"=>"SemanticCyan","flame"=>"SemanticRed",_=>"SemanticSlate"};
    private static string SemanticPath(string key)=>key switch
    {
        "overview"=>"M4,19 A10,10 0 1 1 20,19 M12,13 L17,7 M12,14 A1,1 0 1 1 12,12 A1,1 0 1 1 12,14",
        "display"=>"M3,3 L21,3 L21,16 L3,16 Z M8,21 L16,21 M12,16 L12,21 M6,7 L13,7 L13,12 L6,12 Z M16,7 L18,7 M16,11 L18,11",
        "power"=>"M2,6 L19,6 L19,19 L2,19 Z M19,10 L22,10 L22,15 L19,15 M11,8 L7,13 L11,13 L10,17 L15,11 L11,11 Z",
        "devices"=>"M2,8 L22,8 L22,21 L2,21 Z M5,12 L6,12 M9,12 L10,12 M13,12 L14,12 M17,12 L18,12 M6,17 L18,17 M16,1 L17,4 L20,5 L17,6 L16,8 L15,6 L12,5 L15,4 Z",
        "plugins"=>"M3,3 L9,3 C8,0 14,0 13,3 L20,3 L20,9 C24,8 24,14 20,13 L20,20 L13,20 C14,16 8,16 9,20 L3,20 L3,13 C7,14 7,8 3,9 Z",
        "settings"=>"M9,2 L15,2 L16,6 L20,6 L23,11 L20,14 L20,18 L15,21 L12,18 L8,21 L3,18 L4,14 L1,11 L4,6 L8,6 Z M16,11 A4,4 0 1 1 8,11 A4,4 0 1 1 16,11",
        "cpu"=>"M6,6 L18,6 L18,18 L6,18 Z M9,9 L15,9 L15,15 L9,15 Z M8,2 L8,6 M12,2 L12,6 M16,2 L16,6 M8,18 L8,22 M12,18 L12,22 M16,18 L16,22 M2,8 L6,8 M2,12 L6,12 M2,16 L6,16 M18,8 L22,8 M18,12 L22,12 M18,16 L22,16",
        "gpu"=>"M2,6 L22,6 L22,19 L2,19 Z M5,19 L5,22 M9,19 L9,22 M13,19 L13,22 M12,12 A4,4 0 1 1 4,12 A4,4 0 1 1 12,12 M15,10 L19,10 M15,14 L19,14",
        "color"=>"M12,2 C6,2 2,6 2,12 C2,18 6,22 12,22 C17,22 11,17 16,16 L19,16 C24,16 23,2 12,2 Z M7,8 L7,8.1 M12,6 L12,6.1 M17,8 L17,8.1 M6,13 L6,13.1",
        "tuning"=>"M5,2 L5,7 M5,11 L5,22 M2,7 L8,7 L8,11 L2,11 Z M12,2 L12,14 M12,18 L12,22 M9,14 L15,14 L15,18 L9,18 Z M19,2 L19,5 M19,9 L19,22 M16,5 L22,5 L22,9 L16,9 Z",
        "fan"=>"M12,11 C5,10 3,4 8,2 C12,0 15,4 13,10 M13,12 C14,5 20,3 22,8 C24,12 20,15 14,13 M12,13 C19,14 21,20 16,22 C12,24 9,20 11,14 M11,12 C10,19 4,21 2,16 C0,12 4,9 10,11 M14,12 A2,2 0 1 1 10,12 A2,2 0 1 1 14,12",
        "leaf"=>"M21,3 C7,1 1,8 5,16 C11,24 22,16 21,3 Z M4,22 L16,9",
        "flame"=>"M13,2 C17,8 8,8 11,13 C7,12 7,9 7,9 C2,14 5,22 12,22 C20,22 23,13 16,8 C17,13 13,14 13,14",
        "rocket"=>"M9,15 L4,14 L7,9 L11,9 C14,3 18,2 22,2 C22,6 21,10 15,13 L15,17 L10,20 Z M16,8 A2,2 0 1 1 20,8 A2,2 0 1 1 16,8 M7,17 L3,21 M5,16 L2,19",
        "zap"=>"M13,2 L4,14 L11,14 L10,22 L21,10 L14,10 Z",
        "star"=>"M12,2 L15,8 L22,9 L17,14 L18,22 L12,18 L6,22 L7,14 L2,9 L9,8 Z",
        _=>"M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4"
    };
    private static FrameworkElement SemanticGraphic(string key,double size=22)
    {
        if(key is "overview" or "display" or "power" or "devices" or "plugins" or "settings")
        {var icon=PageIcon(key);icon.Width=size;icon.Height=size;return icon;}
        string optical=key=="gpu"?"<Path.RenderTransform><TranslateTransform Y='-2'/></Path.RenderTransform>":"";
        var element=(FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='{size}' Height='{size}'><Canvas Width='24' Height='24'><Path Data='{SemanticPath(key)}' Stroke='{{ThemeResource {SemanticBrush(key)}}}' StrokeThickness='1.8' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'>{optical}</Path></Canvas></Viewbox>");
        return element;
    }
    private static ImageIcon NavigationOriginalIcon(string id)
    {
        var icon=new ImageIcon();
        FrameworkElement? themeRoot=null;
        void UpdateSource()
        {
            // ImageIcon does not inherit Foreground. Use the window theme rather than
            // the icon's template-local theme when selecting the original SVG.
            var owner=themeRoot??icon.XamlRoot?.Content as FrameworkElement;
            bool dark=owner?.RequestedTheme==ElementTheme.Dark||owner?.RequestedTheme==ElementTheme.Default&&owner.ActualTheme==ElementTheme.Dark;
            string name=id+"-original"+(dark?"-dark":"")+".svg";
            var source=new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","Nav",name));
            if(icon.Source is not Microsoft.UI.Xaml.Media.Imaging.SvgImageSource current||current.UriSource!=source)
                icon.Source=new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(source);
        }
        void ThemeChanged(FrameworkElement sender,object args)=>UpdateSource();
        icon.Loaded+=(_,_)=>
        {
            if(themeRoot!=null)themeRoot.ActualThemeChanged-=ThemeChanged;
            themeRoot=icon.XamlRoot?.Content as FrameworkElement;
            if(themeRoot!=null)themeRoot.ActualThemeChanged+=ThemeChanged;
            UpdateSource();
        };
        icon.Unloaded+=(_,_)=>{if(themeRoot!=null)themeRoot.ActualThemeChanged-=ThemeChanged;themeRoot=null;};
        UpdateSource();return icon;
    }
}
