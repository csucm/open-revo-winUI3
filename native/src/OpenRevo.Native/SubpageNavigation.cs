using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static Button SubpageBack(string destination,RoutedEventHandler action)
    {
        var button=Ui.Button(destination,action);
        button.Style=(Style)Application.Current.Resources["SubtleButtonStyle"];
        button.MinWidth=0;button.MinHeight=32;button.Height=32;
        button.Padding=new Thickness(0,4,6,4);
        button.HorizontalAlignment=HorizontalAlignment.Left;
        var content=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,VerticalAlignment=VerticalAlignment.Center};
        content.Children.Add(new FontIcon{FontFamily=new FontFamily("Segoe Fluent Icons"),Glyph="\uE76B",FontSize=12,Width=16,Height=16,VerticalAlignment=VerticalAlignment.Center});
        content.Children.Add(Ui.Text(destination,14));button.Content=content;
        AutomationProperties.SetName(button,"返回"+destination);
        ToolTipService.SetToolTip(button,"返回"+destination);
        return button;
    }
}
