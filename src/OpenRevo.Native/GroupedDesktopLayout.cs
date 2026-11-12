using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    // Two fields at 640 DIP of actual group content. Cards are never used as
    // independent uneven columns inside a group.
    private static DesktopTiles GroupedColumns(params FrameworkElement[] fields)
    {
        var panel=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=312,ColumnSpacing=16,RowSpacing=8};
        foreach(var field in fields){field.HorizontalAlignment=HorizontalAlignment.Stretch;field.VerticalAlignment=VerticalAlignment.Stretch;panel.Children.Add(field);}
        return panel;
    }
    private static StackPanel GroupedField(string title,FrameworkElement content)
    {
        content.HorizontalAlignment=HorizontalAlignment.Stretch;
        var labels=Ui.Stack(Ui.Text(title,14,true),content);labels.Spacing=8;return labels;
    }
    private static Border GroupDivider()=> (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Height='1' Background='{ThemeResource DesignStroke}' Margin='0,4,0,4'/>");
    private static Button CompactModeChoice(string glyph,string title,string description,RoutedEventHandler action)
    {
        var button=DetailChoice(glyph,title,description,action);button.MinHeight=64;button.Padding=new Thickness(12,8,12,8);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,title);return button;
    }
}
