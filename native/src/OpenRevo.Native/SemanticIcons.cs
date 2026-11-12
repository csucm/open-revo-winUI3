using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static FrameworkElement SemanticGraphic(string key,double size=22)
    {
        return ApprovedIcons.Create(key switch{"overview"=>"home","devices"=>"lighting","tuning"=>"mode-custom","color"=>"lighting","leaf"=>"mode-office","flame"=>"mode-performance","rocket"=>"mode-custom","zap"=>"mode-balanced","star"=>"lighting",_=>key},size);
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
