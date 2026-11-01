using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool customTuningOpen;
    private readonly List<string> detailSectionTitles=[];
    private static FontIcon DetailIcon(string glyph,double size=20)=>new(){FontFamily=new FontFamily("Segoe Fluent Icons"),Glyph=glyph,FontSize=size};
    private static FrameworkElement DetailGlyph(string glyph,double size=20,bool accent=false)
    {
        string? geometry=glyph switch
        {
            "sun"=>"M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
            "moon"=>"M20,15 A9,9 0 1 1 9,3 A8,8 0 0 0 20,15 Z",
            "blocked"=>"M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M5,5 L19,19",
            "usb"=>"M12,2 L9,6 L15,6 Z M12,6 L12,21 M12,15 L6,11 L6,8 M12,12 L18,8 L18,5 M4,8 L8,8 M16,3 L20,3 L20,7 L16,7 Z M14,21 A2,2 0 1 1 10,21 A2,2 0 1 1 14,21",
            _=>null
        };
        if(geometry==null)return Ui.Icon(glyph,size,accent);
        var element=(FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Canvas Width='24' Height='24'><Path Data='{geometry}' Stroke='{{ThemeResource TextFillColorPrimaryBrush}}' StrokeThickness='1.6' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/></Canvas></Viewbox>");
        element.Width=size;element.Height=size;return element;
    }
    private FrameworkElement DetailHeader(string title,string description,string glyph)
    {
        FrameworkElement icon=title is "自定义调校" or "调色控制"?SemanticGraphic(title=="自定义调校"?"tuning":"color",20):PageIcon(title switch{"自定义调校"=>"tuning","调色控制"=>"color","显卡与屏幕"=>"display","电源与电池"=>"power","灯光与设备"=>"devices","设置与诊断"=>"settings",_=>"overview"});
        icon.HorizontalAlignment=HorizontalAlignment.Center;icon.VerticalAlignment=VerticalAlignment.Center;
        var badge=new Border{Style=(Style)Application.Current.Resources["DetailBadgeStyle"],Child=icon,VerticalAlignment=VerticalAlignment.Center};
        var labels=Ui.Stack(Ui.Text(title,24,true),Ui.Text(description,12,muted:true));labels.Spacing=4;labels.VerticalAlignment=VerticalAlignment.Center;
        var grid=new Grid{ColumnSpacing=14,Margin=new Thickness(0,2,0,4)};
        grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.Children.Add(badge);Grid.SetColumn(labels,1);grid.Children.Add(labels);grid.Tag=new DesktopAlignment(badge,labels,"page-header");return grid;
    }
    private Border DetailSection(string title,UIElement content,string? description=null,FrameworkElement? trailing=null)
    {
        detailSectionTitles.Add(title);
        var heading=Ui.Text(title,15,true);
        FrameworkElement header=trailing==null?heading:DesktopAlignedRow(heading,trailing,"section-heading");
        var stack=Ui.Stack(header);stack.Spacing=12;
        if(!string.IsNullOrEmpty(description))stack.Children.Add(Ui.Text(description,12,muted:true));
        stack.Children.Add(content);
        var card=Ui.Card(stack);card.Padding=new Thickness(18);return card;
    }
    private static DesktopTiles DetailGrid(params FrameworkElement[] items)=>DetailGridWithColumns(2,items);
    private static DesktopTiles DetailGridWithColumns(int preferredColumns,params FrameworkElement[] items)
    {
        var grid=new DesktopTiles{PreferredColumns=preferredColumns,MinimumItemWidth=preferredColumns==3?200:312,ColumnSpacing=16,RowSpacing=8};
        foreach(var item in items){item.HorizontalAlignment=HorizontalAlignment.Stretch;item.VerticalAlignment=VerticalAlignment.Stretch;grid.Children.Add(item);}return grid;
    }
    private static void SetChoiceVisual(Button button,bool selected)
    {
        var style=selected?(Style)Application.Current.Resources["SelectedChoiceStyle"]:Equals(button.Tag,"detail-choice")?(Style)Application.Current.Resources["DetailChoiceStyle"]:null;
        if(style!=null){if(!ReferenceEquals(button.Style,style))button.Style=style;}
        else if(button.Style!=null)button.ClearValue(FrameworkElement.StyleProperty);
    }
    private static Button DetailChoice(string glyph,string title,string description,RoutedEventHandler action,bool navigate=false)
    {
        var labels=Ui.Stack(Ui.Text(title,14,true),Ui.Text(description,12,muted:true));labels.Spacing=4;labels.VerticalAlignment=VerticalAlignment.Center;
        var icon=DetailGlyph(glyph);icon.VerticalAlignment=VerticalAlignment.Center;icon.HorizontalAlignment=HorizontalAlignment.Center;
        var body=new Grid{ColumnSpacing=12};body.ColumnDefinitions.Add(new(){Width=new GridLength(28)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        body.Children.Add(icon);Grid.SetColumn(labels,1);body.Children.Add(labels);body.Tag=new DesktopAlignment(icon,labels,"choice-icon");
        if(navigate){body.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var arrow=DetailIcon("\uE76C",12);arrow.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(arrow,2);body.Children.Add(arrow);}
        var button=new Button{Content=body,Tag="detail-choice",MinHeight=92,Padding=new Thickness(16),CornerRadius=new CornerRadius(10),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center};
        button.Click+=action;SetChoiceVisual(button,false);return button;
    }
    private static FrameworkElement DetailRow(string glyph,string title,string description,FrameworkElement control,double stackBelow=520,TextBlock? status=null)
    {
        var titleText=Ui.Text(title,13,true);
        var labels=Ui.Stack(titleText,Ui.Text(description,12,muted:true));if(status!=null)labels.Children.Add(status);labels.Spacing=3;labels.VerticalAlignment=VerticalAlignment.Center;
        var leading=new Grid{ColumnSpacing=12,VerticalAlignment=VerticalAlignment.Center};leading.ColumnDefinitions.Add(new(){Width=new GridLength(26)});leading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var icon=DetailGlyph(glyph,18);icon.VerticalAlignment=VerticalAlignment.Center;icon.HorizontalAlignment=HorizontalAlignment.Center;
        leading.Children.Add(icon);Grid.SetColumn(labels,1);leading.Children.Add(labels);leading.Tag=new DesktopAlignment(icon,labels,"row-icon");
        control.VerticalAlignment=VerticalAlignment.Center;
        var row=DesktopAlignedRow(leading,control);row.MinHeight=56;bool stacked=false;
        row.SizeChanged+=(_,_)=>
        {
            bool narrow=row.ActualWidth>0&&row.ActualWidth<stackBelow;
            if(stacked==narrow)return;stacked=narrow;
            row.Tag=narrow?null:new DesktopAlignment(leading,control,"label-control");
            row.RowDefinitions.Clear();row.RowDefinitions.Add(new(){Height=GridLength.Auto});
            if(narrow)row.RowDefinitions.Add(new(){Height=GridLength.Auto});
            row.ColumnDefinitions[1].Width=narrow?new GridLength(0):GridLength.Auto;
            row.ColumnSpacing=narrow?0:20;
            Grid.SetColumn(control,narrow?0:1);Grid.SetRow(control,narrow?1:0);
            control.HorizontalAlignment=HorizontalAlignment.Right;
            control.Margin=narrow?new Thickness(0,8,0,0):new Thickness(0);
        };
        return row;
    }
    private void BackFromTuning()
    {
        if(tuningApplying)return;
        customTuningOpen=false;currentPage=tuningReturnPage;
        if(tuningReturnMini&&!mini)ToggleMini();else Render();
    }
}
