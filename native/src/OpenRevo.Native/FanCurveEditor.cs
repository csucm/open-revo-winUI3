using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using OpenRevo.Core.Hal;
using Windows.Foundation;
using Windows.System;

namespace OpenRevo.Native;
internal sealed class FanCurveEditor:StackPanel
{
    private FanPoint[] points;
    private readonly Canvas graph=new(){Height=220,MinWidth=260,HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Polyline line;
    private readonly Polygon area;
    private readonly List<Line> horizontal=[];
    private readonly List<Line> vertical=[];
    private readonly List<TextBlock> yLabels=[];
    private readonly List<TextBlock> xLabels=[];
    private readonly List<Thumb> handles=[];
    private readonly List<TextBlock> duties=[];
    private readonly List<(RepeatButton Plus,RepeatButton Minus)> steps=[];
    private readonly TextBlock selection=Ui.Text("",12,muted:true);
    private readonly Action<FanPoint[]> changed;
    private int selected;
    private const double Left=40,Right=14,Top=12,Bottom=30;
    private double PlotWidth=>Math.Max(1,graph.ActualWidth-Left-Right);
    private double PlotHeight=>graph.Height-Top-Bottom;
    private static T Shape<T>(string xaml)where T:FrameworkElement=>(T)Microsoft.UI.Xaml.Markup.XamlReader.Load("<"+xaml+" xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>");
    public FanCurveEditor(FanPoint[] curve,Action<FanPoint[]> changed,List<NumberBox> validation,bool gpu=false)
    {
        if(curve.Length!=8)throw new ArgumentException("风扇曲线需要8个节点。",nameof(curve));
        points=curve.ToArray();this.changed=changed;Spacing=8;
        string brush=gpu?"SemanticGreen":"SemanticBlue";
        area=Shape<Polygon>($"Polygon Fill='{{ThemeResource {brush}}}' Opacity='0.08'");
        line=Shape<Polyline>($"Polyline Stroke='{{ThemeResource {brush}}}' StrokeThickness='2'");
        Children.Add(Ui.Row(Ui.Text("转速（%）",12,muted:true),selection));
        graph.Background=new SolidColorBrush(Colors.Transparent);Children.Add(graph);
        for(int i=0;i<5;i++)
        {
            var gridLine=Shape<Line>("Line Stroke='{ThemeResource DesignStroke}' StrokeThickness='1'");horizontal.Add(gridLine);graph.Children.Add(gridLine);
            var label=Ui.Text((100-i*25).ToString(),12,muted:true);yLabels.Add(label);graph.Children.Add(label);
        }
        for(int i=0;i<8;i++)
        {
            var gridLine=Shape<Line>("Line Stroke='{ThemeResource DesignStroke}' StrokeThickness='1' Opacity='0.45'");vertical.Add(gridLine);graph.Children.Add(gridLine);
            var label=Ui.Text((30+i*10).ToString(),12,muted:true);xLabels.Add(label);graph.Children.Add(label);
        }
        graph.Children.Add(area);graph.Children.Add(line);
        var nodes=new Grid{ColumnSpacing=4};
        for(int i=0;i<8;i++)
        {
            int index=i;nodes.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var thumb=Shape<Thumb>($"Thumb Width='14' Height='14' Background='{{ThemeResource {brush}}}'");
            thumb.Style=(Style)Application.Current.Resources["FanCurveThumbStyle"];
            thumb.IsTabStop=true;AutomationProperties.SetName(thumb,$"{(gpu?"GPU":"CPU")}节点{i+1}，上下方向键调整转速");
            handles.Add(thumb);graph.Children.Add(thumb);
            double dragDuty=points[i].Percent;
            thumb.DragStarted+=(_,_)=>{selected=index;dragDuty=points[index].Percent;Draw();};
            thumb.GotFocus+=(_,_)=>{selected=index;Draw();};
            thumb.DragDelta+=(_,e)=>{if(index==7)return;dragDuty=Math.Clamp(dragDuty-e.VerticalChange/PlotHeight*100,0,100);Update(index,(int)Math.Round(dragDuty));};
            thumb.KeyDown+=(_,e)=>
            {
                if(e.Key is VirtualKey.Up or VirtualKey.Down){Update(index,points[index].Percent+(e.Key==VirtualKey.Up?1:-1));e.Handled=true;}
            };
            RepeatButton Step(string content,int delta)
            {
                var button=new RepeatButton{Content=content,MinWidth=0,MinHeight=24,Height=24,Padding=new Thickness(0),FontSize=14,Delay=260,Interval=50,HorizontalAlignment=HorizontalAlignment.Stretch};
                AutomationProperties.SetName(button,$"{(gpu?"GPU":"CPU")}节点{index+1}{(delta>0?"增加":"降低")}转速");
                button.Click+=(_,_)=>Update(index,points[index].Percent+delta);return button;
            }
            var plus=Step("+",1);var minus=Step("−",-1);steps.Add((plus,minus));
            var duty=Ui.Text(points[i].Percent+"%",12,true);duty.HorizontalAlignment=HorizontalAlignment.Center;duties.Add(duty);
            var temperature=Ui.Text(points[i].Celsius+"°",12,muted:true);temperature.HorizontalAlignment=HorizontalAlignment.Center;
            var column=Ui.Stack(plus,duty,minus,temperature);column.Spacing=2;Grid.SetColumn(column,i);nodes.Children.Add(column);
        }
        Children.Add(Ui.Text("温度（°C） · 拖动节点或用下方 ± 微调",12,muted:true));
        Children.Add(nodes);graph.SizeChanged+=(_,_)=>Draw();Loaded+=(_,_)=>Draw();Draw();
    }
    private void Update(int index,int duty)
    {
        selected=index;var next=FanCurveEditing.WithDuty(points,index,duty);
        if(next.SequenceEqual(points)){Draw();return;}
        points=next;Draw();changed(points.ToArray());
    }
    private void Draw()
    {
        double right=Left+PlotWidth,bottom=Top+PlotHeight;
        for(int i=0;i<5;i++)
        {
            double y=Top+i*PlotHeight/4;var l=horizontal[i];l.X1=Left;l.X2=right;l.Y1=l.Y2=y;
            var label=yLabels[i];label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Canvas.SetLeft(label,Left-label.DesiredSize.Width-8);Canvas.SetTop(label,y-label.DesiredSize.Height/2);
        }
        for(int i=0;i<8;i++)
        {
            double x=Left+PlotWidth*i/7;var l=vertical[i];l.X1=l.X2=x;l.Y1=Top;l.Y2=bottom;
            var label=xLabels[i];label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Canvas.SetLeft(label,x-label.DesiredSize.Width/2);Canvas.SetTop(label,bottom+5);
        }
        var plot=new PointCollection{new Point(Left,bottom-points[0].Percent/100d*PlotHeight)};
        for(int i=0;i<8;i++)
        {
            double x=Left+(points[i].Celsius-30)/70d*PlotWidth,y=bottom-points[i].Percent/100d*PlotHeight;
            plot.Add(new Point(x,y));Canvas.SetLeft(handles[i],x-7);Canvas.SetTop(handles[i],y-7);
            ToolTipService.SetToolTip(handles[i],$"节点{i+1} · {points[i].Celsius}°C · {points[i].Percent}%");
            duties[i].Text=points[i].Percent+"%";steps[i].Plus.IsEnabled=i<7&&points[i].Percent<100;steps[i].Minus.IsEnabled=i<7&&points[i].Percent>0;
        }
        plot.Add(new Point(right,bottom-points[^1].Percent/100d*PlotHeight));line.Points=plot;
        var fill=new PointCollection{new Point(Left,bottom)};foreach(var point in plot)fill.Add(point);fill.Add(new Point(right,bottom));area.Points=fill;
        selection.Text=$"节点{selected+1} · {points[selected].Celsius}°C · {points[selected].Percent}%";
    }
}
