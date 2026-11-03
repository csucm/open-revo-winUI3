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
    private readonly Canvas graph=new(){Height=148,MinWidth=220,HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly Polyline line;
    private readonly List<Line> horizontal=[],vertical=[];
    private readonly List<TextBlock> yLabels=[],xLabels=[];
    private readonly List<Thumb> handles=[];
    private readonly List<NumberBox> numbers=[];
    private readonly Action<FanPoint[]> changed;
    private bool syncing;
    private const double Left=38,Right=15,Top=20,Bottom=28;
    private double PlotWidth=>Math.Max(1,graph.ActualWidth-Left-Right);
    private double PlotHeight=>Math.Max(1,graph.Height-Top-Bottom);
    private int MinTemp=>Math.Min(40,points[0].Celsius);
    private int MaxTemp=>Math.Max(95,points[^1].Celsius);
    private static T Shape<T>(string xaml)where T:FrameworkElement=>(T)Microsoft.UI.Xaml.Markup.XamlReader.Load("<"+xaml+" xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>");
    public FanCurveEditor(FanPoint[] curve,Action<FanPoint[]> changed,List<NumberBox> validation,bool gpu=false)
    {
        if(curve.Length!=8)throw new ArgumentException("风扇曲线需要8个节点。");points=curve.ToArray();this.changed=changed;Spacing=8;
        line=Shape<Polyline>("Polyline Stroke='{ThemeResource SemanticCyan}' StrokeThickness='2'");
        graph.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);Children.Add(graph);
        var unit=Ui.Text("转速 %",12,muted:true);graph.Children.Add(unit);
        for(int i=0;i<3;i++){var l=Shape<Line>("Line Stroke='{ThemeResource DesignStroke}' StrokeThickness='1'");horizontal.Add(l);graph.Children.Add(l);var label=Ui.Text((100-i*50).ToString(),12,muted:true);yLabels.Add(label);graph.Children.Add(label);}
        for(int i=0;i<4;i++){var l=Shape<Line>("Line Stroke='{ThemeResource DesignStroke}' StrokeThickness='1'");vertical.Add(l);graph.Children.Add(l);var label=Ui.Text("",12,muted:true);xLabels.Add(label);graph.Children.Add(label);}
        graph.Children.Add(line);var nodes=new Grid{ColumnSpacing=6};
        for(int i=0;i<8;i++)
        {
            int index=i;nodes.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var thumb=new Thumb{Width=16,Height=16,IsTabStop=true,Template=(ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Grid Background='Transparent'><Ellipse Width='8' Height='8' Fill='{ThemeResource DesignCardFill}' Stroke='{ThemeResource SemanticCyan}' StrokeThickness='1.8'/></Grid></ControlTemplate>")};
            AutomationProperties.SetName(thumb,$"{(gpu?"GPU":"CPU")}节点{i+1}，上下方向键调整转速");handles.Add(thumb);graph.Children.Add(thumb);double duty=points[i].Percent;
            thumb.DragStarted+=(_,_)=>duty=points[index].Percent;thumb.DragDelta+=(_,e)=>{if(index==7)return;duty=Math.Clamp(duty-e.VerticalChange/PlotHeight*100,0,100);Update(index,(int)Math.Round(duty));};
            thumb.KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Up or VirtualKey.Down){Update(index,points[index].Percent+(e.Key==VirtualKey.Up?1:-1));e.Handled=true;}};
            var number=new NumberBox{Value=points[i].Percent,Minimum=0,Maximum=100,SmallChange=1,MinWidth=0,FontSize=12,IsEnabled=i<7,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Hidden,HorizontalAlignment=HorizontalAlignment.Stretch};numbers.Add(number);validation.Add(number);
            AutomationProperties.SetName(number,$"{(gpu?"GPU":"CPU")} {points[i].Celsius} 摄氏度时转速百分比");number.ValueChanged+=(_,e)=>{if(!syncing&&double.IsFinite(e.NewValue))Update(index,(int)Math.Round(e.NewValue));};
            var temperature=Ui.Text(points[i].Celsius+"°C",12,muted:true);temperature.HorizontalAlignment=HorizontalAlignment.Center;var percent=Ui.Text("%",12,muted:true);percent.HorizontalAlignment=HorizontalAlignment.Center;
            var column=SuiteVisuals.Stack(5,temperature,number,percent);Grid.SetColumn(column,i);nodes.Children.Add(column);
        }
        Children.Add(nodes);graph.SizeChanged+=(_,_)=>{double h=Math.Max(100,graph.ActualWidth*148/420);if(Math.Abs(graph.Height-h)>.5)graph.Height=h;Draw();};Loaded+=(_,_)=>Draw();Draw();
    }
    private void Update(int index,int duty)
    {
        var next=FanCurveEditing.WithDuty(points,index,duty);if(next.SequenceEqual(points)){Draw();return;}points=next;Draw();changed(points.ToArray());
    }
    private void Draw()
    {
        double right=Left+PlotWidth,bottom=Top+PlotHeight;
        for(int i=0;i<3;i++){double y=Top+i*PlotHeight/2;horizontal[i].X1=Left;horizontal[i].X2=right;horizontal[i].Y1=horizontal[i].Y2=y;Canvas.SetLeft(yLabels[i],2);Canvas.SetTop(yLabels[i],y-9);}
        for(int i=0;i<4;i++){double x=Left+PlotWidth*i/3;vertical[i].X1=vertical[i].X2=x;vertical[i].Y1=Top;vertical[i].Y2=bottom;xLabels[i].Text=((int)Math.Round(MinTemp+(MaxTemp-MinTemp)*i/3d))+(i==3?"°C":"");Canvas.SetLeft(xLabels[i],x-6);Canvas.SetTop(xLabels[i],bottom+4);}
        var plot=new PointCollection();syncing=true;
        try{for(int i=0;i<8;i++){double x=Left+(points[i].Celsius-MinTemp)/(double)(MaxTemp-MinTemp)*PlotWidth,y=bottom-points[i].Percent/100d*PlotHeight;plot.Add(new(x,y));Canvas.SetLeft(handles[i],x-8);Canvas.SetTop(handles[i],y-8);numbers[i].Value=points[i].Percent;}}finally{syncing=false;}
        line.Points=plot;
    }
}
