using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
namespace OpenRevo.Native;
internal static class ApprovedIcons
{
    internal static ImageIcon Create(string id,double size=34,bool on=true,bool? compact=null)
    {
        var icon=new ImageIcon{Width=size,Height=size}; FrameworkElement? owner=null;
        void Update(){bool dark=owner?.ActualTheme==ElementTheme.Dark;var source=new SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","Approved",id+"-"+(dark?"dark":"light")+((compact??size<=24)?"-compact":"")+(on?"":"-off")+".svg")));source.OpenFailed+=(_,args)=>OpenRevo.Core.DiagnosticLog.Event("ui.icon.failed","error",new{id,status=args.Status.ToString()});icon.Source=source;}
        void Changed(FrameworkElement sender,object args)=>Update();
        icon.Loaded+=(_,_)=>{owner=icon.XamlRoot?.Content as FrameworkElement;if(owner!=null)owner.ActualThemeChanged+=Changed;Update();};
        icon.Unloaded+=(_,_)=>{if(owner!=null)owner.ActualThemeChanged-=Changed;owner=null;};Update();return icon;
    }
    internal static ImageIcon Utility(string id,double size=18)=>Create("utility-"+id,size,true,false);
    internal static string? Glyph(string glyph)=>glyph switch {
        "\uE70F"=>"mode-office","\uE945"=>"mode-balanced","\uEC4A"=>"mode-performance","\uE713"=>"mode-custom",
        "\uE950"=>"cpu","\uE7F4"=>"display","\uE706" or "\uE722"=>"camera","\uE720"=>"mic",
        "\uE72E" or "\uE782"=>"win-lock","\uE708"=>"awake","\uE728"=>"display","\uE8F1"=>"battery",
        "\uE765"=>"fn-lock","\uE9D9"=>"fan","\uE701"=>"wifi","\uE702"=>"bluetooth","\uEFA5"=>"touchpad","sun"=>"brightness",_=>null};
}
