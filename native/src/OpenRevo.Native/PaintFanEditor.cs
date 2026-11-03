using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenRevo.Core.Hal;
using Windows.Foundation;
using Windows.System;
namespace OpenRevo.Native;
internal sealed class PaintFanEditor:StackPanel
{
    private readonly Canvas graph=new(){Height=148,Background=Paint.Brush("00000000")};
    private readonly Polyline line=new(){StrokeThickness=2};
    private readonly List<Line> horizontal=[],vertical=[];private readonly List<TextBlock> ticks=[],temperatures=[];
    private readonly List<PaintButton> handles=[];private readonly List<PaintField> fields=[];
    private FanPoint[] points;private readonly Action<FanPoint[]> changed;
    private double PlotWidth=>Math.Max(1,graph.ActualWidth-53);private double PlotHeight=>Math.Max(1,graph.Height-48);
    internal PaintFanEditor(FanPoint[] curve,Action<FanPoint[]> changed)
    {
        if(curve.Length!=8)throw new ArgumentException("风扇协议需要8个节点。");points=curve.ToArray();this.changed=changed;Spacing=8;Children.Add(graph);graph.Children.Add(Paint.Text("转速 %",12,muted:true));
        for(int i=0;i<3;i++){var l=new Line{StrokeThickness=1};horizontal.Add(l);graph.Children.Add(l);var t=Paint.Text((100-i*50).ToString(),12,muted:true);ticks.Add(t);graph.Children.Add(t);}
        for(int i=0;i<4;i++){var l=new Line{StrokeThickness=1};vertical.Add(l);graph.Children.Add(l);var t=Paint.Text("",12,muted:true);temperatures.Add(t);graph.Children.Add(t);}graph.Children.Add(line);
        var nodes=new Grid{ColumnSpacing=6};for(int i=0;i<8;i++)
        {
            int at=i;nodes.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});var dot=new Ellipse{Width=8,Height=8,StrokeThickness=1.8,Stroke=Paint.Brush(Paint.Accent(this,"cyan")),Fill=Paint.Brush(Paint.Surface(this))};var handle=new PaintButton("风扇节点 "+(i+1)){Content=dot,Variant="icon",Width=16,Height=16,MinHeight=0,Padding=new(0)};bool dragging=false;
            handle.PointerPressed+=(_,e)=>{if(at==7||!handle.IsEnabled)return;dragging=handle.CapturePointer(e.Pointer);e.Handled=true;};handle.PointerMoved+=(_,e)=>{if(!dragging)return;double y=e.GetCurrentPoint(graph).Position.Y;Update(at,(int)Math.Round((graph.Height-28-y)/PlotHeight*100));e.Handled=true;};handle.PointerReleased+=(_,_)=>{dragging=false;handle.ReleasePointerCaptures();};handle.PointerCaptureLost+=(_,_)=>dragging=false;
            handle.KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Up or VirtualKey.Down){Update(at,points[at].Percent+(e.Key==VirtualKey.Up?1:-1));e.Handled=true;}};handles.Add(handle);graph.Children.Add(handle);
            var field=new PaintField(points[i].Celsius+"°C 转速",points[i].Percent.ToString()){Width=double.NaN,MinWidth=0,Padding=new(3,6,3,6),IsEnabled=i<7};field.Committed+=text=>{if(int.TryParse(text,out int percent))Update(at,percent);};fields.Add(field);var label=Paint.Text(points[i].Celsius+"°C",12,muted:true);label.TextAlignment=TextAlignment.Center;var unit=Paint.Text("%",12,muted:true);unit.TextAlignment=TextAlignment.Center;var column=Paint.Stack(5,label,field,unit);Grid.SetColumn(column,i);nodes.Children.Add(column);
        }Children.Add(nodes);graph.SizeChanged+=(_,_)=>{graph.Height=Math.Max(100,graph.ActualWidth*148/420);Draw();};Loaded+=(_,_)=>Draw();ActualThemeChanged+=(_,_)=>Draw();
    }
    private void Update(int at,int percent){var next=FanCurveEditing.WithDuty(points,at,percent);if(next.SequenceEqual(points))return;points=next;Draw();changed(points.ToArray());}
    private void Draw()
    {
        for(int i=0;i<3;i++){var l=horizontal[i];l.Stroke=Paint.Brush(Paint.Line(this));l.X1=38;l.X2=38+PlotWidth;l.Y1=l.Y2=20+i*PlotHeight/2;Canvas.SetLeft(ticks[i],2);Canvas.SetTop(ticks[i],l.Y1-9);}
        int low=Math.Min(40,points[0].Celsius),high=Math.Max(95,points[^1].Celsius);for(int i=0;i<4;i++){var l=vertical[i];l.Stroke=Paint.Brush(Paint.Line(this));l.X1=l.X2=38+i*PlotWidth/3;l.Y1=20;l.Y2=20+PlotHeight;temperatures[i].Text=((int)Math.Round(low+(high-low)*i/3d))+(i==3?"°C":"");Canvas.SetLeft(temperatures[i],l.X1-6);Canvas.SetTop(temperatures[i],l.Y2+4);}
        var plot=new PointCollection();for(int i=0;i<8;i++){double x=38+(points[i].Celsius-low)/(double)(high-low)*PlotWidth,y=20+PlotHeight*(1-points[i].Percent/100d);plot.Add(new(x,y));Canvas.SetLeft(handles[i],x-8);Canvas.SetTop(handles[i],y-8);fields[i].Value=points[i].Percent.ToString();}line.Stroke=Paint.Brush(Paint.Accent(this,"cyan"));line.Points=plot;
    }
}
