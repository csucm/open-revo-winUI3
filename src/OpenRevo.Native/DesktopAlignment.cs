using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;
internal sealed record DesktopAlignment(FrameworkElement First,FrameworkElement Second,string Role,bool Horizontal=false);
public sealed partial class MainWindow
{
    private static Grid DesktopAlignedRow(FrameworkElement left,FrameworkElement right,string role="label-control")
    {
        left.VerticalAlignment=VerticalAlignment.Center;right.VerticalAlignment=VerticalAlignment.Center;
        var row=Ui.Row(left,right);row.Tag=new DesktopAlignment(left,right,role);return row;
    }
    private static Border DesktopSetting(string title,string description,FrameworkElement control)
    {
        var labels=Ui.Stack(Ui.Text(title,15,true),Ui.Text(description,12,muted:true));
        return Ui.Card(DesktopAlignedRow(labels,control,"settings-row"));
    }
}
