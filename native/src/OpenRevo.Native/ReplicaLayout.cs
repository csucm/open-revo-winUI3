using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static DesktopTiles TuningColumns(params FrameworkElement[] items)
    {
        var panel=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=960,ColumnSpacing=16,RowSpacing=16};
        foreach(var item in items){item.HorizontalAlignment=HorizontalAlignment.Stretch;item.VerticalAlignment=VerticalAlignment.Stretch;panel.Children.Add(item);}return panel;
    }
    private static FrameworkElement ReplicaLeading(string key,TextBlock title,string detail)
    {
        var labels=Ui.Stack(title);labels.Spacing=3;labels.VerticalAlignment=VerticalAlignment.Center;
        if(!string.IsNullOrWhiteSpace(detail))labels.Children.Add(Ui.Text(detail,12,muted:true));
        var icon=SemanticGraphic(key,24);icon.VerticalAlignment=VerticalAlignment.Center;
        var row=new Grid{ColumnSpacing=12,VerticalAlignment=VerticalAlignment.Center};row.ColumnDefinitions.Add(new(){Width=new GridLength(24)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        row.Children.Add(icon);Grid.SetColumn(labels,1);row.Children.Add(labels);row.Tag=new DesktopAlignment(icon,labels,"replica-icon");return row;
    }
    private static Border CurvePanel(string title,UIElement content)
    {
        var border=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignInsetFill}' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='1' CornerRadius='12' Padding='14'/>");border.Child=SuiteVisuals.Stack(8,Ui.Text(title,14),content);return border;
    }
    private static Border ReplicaCard(string key,string title,UIElement content,FrameworkElement? trailing=null)
    {
        var heading=ReplicaLeading(key,Ui.Text(title,16,true),"");
        var body=Ui.Stack(trailing==null?heading:DesktopAlignedRow(heading,trailing),content);body.Spacing=14;
        var card=Ui.Card(body);card.Padding=new Thickness(20);return card;
    }
}
