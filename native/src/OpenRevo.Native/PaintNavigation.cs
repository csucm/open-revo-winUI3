using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;

internal sealed record PaintNavItem(string Title,string Id,string Icon){internal object Tag=>Id;}
internal sealed record PaintSelectionChanged(object? SelectedItem);

internal sealed class PaintNavigation:ContentControl
{
    private readonly Grid layout=new(){ColumnSpacing=0};
    private readonly Border sidebar=new(){BorderThickness=new(0,0,1,0)};
    private readonly Grid body=new();private readonly StackPanel links=Paint.Stack(5);
    private sealed record NavigationEntry(PaintNavItem Item,PaintButton Button,Grid Content,
        ColumnDefinition IconColumn,TextBlock Label,Border Indicator);
    private readonly List<NavigationEntry> buttons=[];
    private bool? renderedCompact;
    private double renderedWidth=-1;
    private readonly TextBlock heading=Paint.Text("设备控制中心",12,muted:true),status=Paint.Text("● 未接管硬件",12,muted:true),detail=Paint.Text("",12,muted:true);
    private bool? manualCompact;
    private readonly PaintButton paneToggle=new("展开或收起导航"){Variant="plain",MinHeight=32,Padding=new(0)};
    private object? selected;private bool visible=true;private double viewport=1180;
    internal List<object> MenuItems{get;}=[];
    internal event EventHandler<PaintSelectionChanged>? SelectionChanged;
    internal object? SelectedItem{get=>selected;set{if(Equals(selected,value))return;selected=value;Refresh();SelectionChanged?.Invoke(this,new(value));}}
    internal bool IsPaneVisible{get=>visible;set{visible=value;Refresh();}}
    internal bool IsPaneOpen{get;set;}=true;
    internal NavigationViewPaneDisplayMode PaneDisplayMode{get;set;}
    internal double OpenPaneLength{get;set;}=174;
    internal double CompactPaneLength{get;set;}=54;
    internal bool IsPaneToggleButtonVisible{get;set;}
    internal NavigationViewBackButtonVisible IsBackButtonVisible{get;set;}
    internal bool IsSettingsVisible{get;set;}
    internal object? PaneHeader{get;set;}
    internal object? PaneFooter{get;set;}
    internal new object? Content{get=>body.Children.FirstOrDefault();set{body.Children.Clear();if(value is UIElement ui)body.Children.Add(ui);}}
    internal PaintNavigation()
    {
        Template=Paint.PresenterTemplate();base.Content=layout;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        layout.ColumnDefinitions.Add(new(){Width=new(174)});layout.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});layout.Children.Add(sidebar);Grid.SetColumn(body,1);layout.Children.Add(body);
        var pane=new Grid{Padding=new(10,20,10,20)};pane.RowDefinitions.Add(new(){Height=GridLength.Auto});pane.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});pane.RowDefinitions.Add(new(){Height=GridLength.Auto});
        heading.TextAlignment=TextAlignment.Center;heading.Margin=new(0,0,0,4);
        paneToggle.HorizontalAlignment=HorizontalAlignment.Left;paneToggle.Width=32;paneToggle.Content=Paint.Text("☰",16);paneToggle.Click+=()=>{manualCompact=!(manualCompact??viewport<=960);Refresh();};
        var top=Paint.Stack(5,heading,paneToggle,links);pane.Children.Add(top);var footer=Paint.Stack(6,status,detail);footer.Margin=new(12,16,4,0);Grid.SetRow(footer,2);pane.Children.Add(footer);sidebar.Child=pane;
        Loaded+=(_,_)=>Refresh();ActualThemeChanged+=(_,_)=>Refresh();
    }
    internal void Build()
    {
        links.Children.Clear();
        buttons.Clear();
        renderedCompact=null;
        foreach(var item in MenuItems.OfType<PaintNavItem>())
        {
            var button=new PaintButton(item.Title,()=>SelectedItem=item)
            {
                Variant="navigation",Padding=new(10),MinHeight=48,CornerRadius=new(9),
                HorizontalAlignment=HorizontalAlignment.Stretch
            };
            button.StretchContent();
            var content=new Grid{ColumnSpacing=9};
            var iconColumn=new ColumnDefinition{Width=new(28)};
            content.ColumnDefinitions.Add(iconColumn);
            content.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            var icon=Paint.Icon(item.Icon,28,true,false);
            icon.HorizontalAlignment=HorizontalAlignment.Center;
            content.Children.Add(icon);
            var label=Paint.Text(item.Title);
            Grid.SetColumn(label,1);
            content.Children.Add(label);
            var indicator=new Border
            {
                Width=3,Height=18,CornerRadius=new(3),
                HorizontalAlignment=HorizontalAlignment.Left,
                VerticalAlignment=VerticalAlignment.Center
            };
            content.Children.Add(indicator);
            button.Content=content;
            buttons.Add(new(item,button,content,iconColumn,label,indicator));
            links.Children.Add(button);
        }
        Refresh();
    }

    internal void SetViewport(double width)
    {
        if(Math.Abs(width-viewport)<.5)return;
        if((width<=960)!=(viewport<=960))manualCompact=null;
        viewport=width;
        bool compact=manualCompact??viewport<=960;
        double paneWidth=!visible?0:compact?(viewport<=820?54:64):viewport<=1180?156:174;
        if(renderedCompact!=compact||renderedWidth!=paneWidth)Refresh();
    }
    internal void SetStatus(bool connected,string text){status.Text=connected?"● 已接管硬件":"● 未接管硬件";detail.Text=text;}
    private void Refresh()
    {
        if(layout.ColumnDefinitions.Count==0)return;bool compact=manualCompact??viewport<=960;double width=!visible?0:compact?(viewport<=820?54:64):viewport<=1180?156:174;
        bool geometryChanged=renderedCompact!=compact;
        if(renderedWidth!=width)
        {
            layout.ColumnDefinitions[0].Width=new(width);
            renderedWidth=width;
        }
        sidebar.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        sidebar.Background=Paint.Brush(Paint.Mix(Paint.Surface(this),Paint.Bg(this),.35));
        sidebar.BorderBrush=Paint.Brush(Paint.Line(this));
        body.Background=Paint.Brush(Paint.Bg(this));
        if(geometryChanged)
        {
            heading.Visibility=status.Visibility=detail.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            if(sidebar.Child is Grid pane)pane.Padding=compact?new(7,10,7,20):new(10,10,10,20);
            paneToggle.HorizontalAlignment=compact?HorizontalAlignment.Center:HorizontalAlignment.Left;
            paneToggle.Margin=new(compact?0:8,0,0,0);
        }
        foreach(var entry in buttons)
        {
            bool active=ReferenceEquals(selected,entry.Item);
            if(geometryChanged)
            {
                entry.Content.ColumnSpacing=compact?0:9;
                entry.IconColumn.Width=compact?new(1,GridUnitType.Star):new(28);
                entry.Content.ColumnDefinitions[1].Width=compact?new(0):new(1,GridUnitType.Star);
                entry.Label.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
                entry.Indicator.Margin=new(compact?-3:-10,0,0,0);
                entry.Button.Padding=compact?new(3,10,3,10):new(10);
            }
            entry.Indicator.Background=Paint.Brush(Paint.Accent(this));
            entry.Indicator.Visibility=active?Visibility.Visible:Visibility.Collapsed;
            entry.Button.Selected=active;
            if(geometryChanged)entry.Button.Repaint();
        }
        renderedCompact=compact;
    }
}
