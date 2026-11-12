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
    private const double Left = 38, Right = 24, Top = 20, Bottom = 8;
    private const double MinFieldWidth = 34, MaxFieldWidth = 48, FieldGap = 4;
    private readonly Canvas graph=new(){Background=Paint.Brush("00000000")};
    private readonly Grid inputs = new();
    private readonly StackPanel content = new() { Spacing = 8 };
    private readonly Polyline line=new(){StrokeThickness=2};
    private readonly List<Line> horizontal=[],vertical=[];private readonly List<TextBlock> ticks=[];
    private readonly List<PaintButton> handles=[];private readonly List<PaintField> fields=[];
    private readonly List<StackPanel> columns = [];
    private FanPoint[] points;private readonly Action<FanPoint[]> changed;
    private readonly int low, temperatureRange, closestTemperatureGap;
    private double PlotWidth => content.Width - Left - Right;
    private double PlotBottom => graph.Height - Bottom;
    private double PlotHeight => PlotBottom - Top;
    internal PaintFanEditor(FanPoint[] curve,Action<FanPoint[]> changed,Func<bool>? canCommit=null)
    {
        if(curve.Length!=8)throw new ArgumentException("风扇协议需要8个节点。");points=curve.ToArray();this.changed=changed;
        low = Math.Min(40, points[0].Celsius);
        temperatureRange = Math.Max(95, points[^1].Celsius) - low;
        closestTemperatureGap = Math.Max(1, points.Zip(points.Skip(1), (a, b) => b.Celsius - a.Celsius).Min());
        content.Children.Add(graph);
        content.Children.Add(inputs);
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false
        };
        Children.Add(scroll);
        graph.Children.Add(Paint.Text("转速 %",12,muted:true));
        for(int i=0;i<3;i++){var l=new Line{StrokeThickness=1};horizontal.Add(l);graph.Children.Add(l);var t=Paint.Text((100-i*50).ToString(),12,muted:true);ticks.Add(t);graph.Children.Add(t);}
        for(int i=0;i<points.Length;i++){var l=new Line{StrokeThickness=1};vertical.Add(l);graph.Children.Add(l);}graph.Children.Add(line);
        for(int i=0;i<points.Length;i++)
        {
            int at=i;var dot=new Ellipse{Width=8,Height=8,StrokeThickness=1.8,Stroke=Paint.Brush(Paint.Accent(this,"cyan")),Fill=Paint.Brush(Paint.Surface(this))};var handle=new PaintButton("风扇节点 "+(i+1)){Content=dot,Variant="icon",Width=16,Height=16,MinHeight=0,Padding=new(0)};bool dragging=false;
            handle.PointerPressed+=(_,e)=>{if(at==7||!handle.IsEnabled)return;dragging=handle.CapturePointer(e.Pointer);e.Handled=true;};handle.PointerMoved+=(_,e)=>{if(!dragging)return;double y=e.GetCurrentPoint(graph).Position.Y;Update(at,(int)Math.Round((PlotBottom-y)/PlotHeight*100));e.Handled=true;};handle.PointerReleased+=(_,_)=>{dragging=false;handle.ReleasePointerCaptures();};handle.PointerCaptureLost+=(_,_)=>dragging=false;
            handle.KeyDown+=(_,e)=>{if(e.Key is VirtualKey.Up or VirtualKey.Down){Update(at,points[at].Percent+(e.Key==VirtualKey.Up?1:-1));e.Handled=true;}};handles.Add(handle);graph.Children.Add(handle);
            void ReadDuty(string text)
            {
                if(int.TryParse(text,out int percent))Update(at,percent);
            }
            var field=new PaintField(points[i].Celsius+"°C 转速",points[i].Percent.ToString(),ReadDuty)
            {
                Width=double.NaN,MinWidth=0,Padding=new(3,6,3,6),IsEnabled=i<7,ValueAlignment=TextAlignment.Center,
                CanCommit=canCommit,CaptureDraft=canCommit==null?null:ReadDuty
            };
            fields.Add(field);
            var label = Paint.Text(points[i].Celsius + "°C", 12, muted: true);
            var unit = Paint.Text("%", 12, muted: true);
            label.TextAlignment = unit.TextAlignment = TextAlignment.Center;
            var column = Paint.Stack(5, label, field, unit);
            column.HorizontalAlignment = HorizontalAlignment.Left;
            columns.Add(column);
            inputs.Children.Add(column);
        }
        scroll.SizeChanged += (_, e) =>
        {
            if (e.PreviousSize.Width != e.NewSize.Width) Resize(e.NewSize.Width);
        };
        Resize(0);
        Loaded+=(_,_)=>Resize(scroll.ActualWidth);ActualThemeChanged+=(_,_)=>Draw();
    }
    private void Resize(double viewportWidth)
    {
        double minimumPlotWidth = (MinFieldWidth + FieldGap) * temperatureRange / closestTemperatureGap;
        content.Width = Math.Max(viewportWidth, Left + minimumPlotWidth + Right);
        graph.Height = Math.Max(80, viewportWidth * 148 / 420 - 20);
        Draw();
    }
    private void Update(int at,int percent){var next=FanCurveEditing.WithDuty(points,at,percent);if(next.SequenceEqual(points))return;points=next;Draw();changed(points.ToArray());}
    private void Draw()
    {
        var gridStroke = Paint.Brush(Paint.Line(this));
        for(int i=0;i<3;i++){var l=horizontal[i];l.Stroke=gridStroke;l.X1=Left;l.X2=Left+PlotWidth;l.Y1=l.Y2=Top+i*PlotHeight/2;Canvas.SetLeft(ticks[i],2);Canvas.SetTop(ticks[i],l.Y1-9);}
        double fieldWidth = Math.Clamp(PlotWidth * closestTemperatureGap / temperatureRange - FieldGap, MinFieldWidth, MaxFieldWidth);
        var plot = new PointCollection();
        for (int i = 0; i < points.Length; i++)
        {
            double x = Left + (points[i].Celsius - low) * PlotWidth / temperatureRange;
            double y = PlotBottom - points[i].Percent / 100d * PlotHeight;
            plot.Add(new(x, y));
            Canvas.SetLeft(handles[i], x - 8);
            Canvas.SetTop(handles[i], y - 8);

            var guide = vertical[i];
            guide.Stroke = gridStroke;
            guide.X1 = guide.X2 = x;
            guide.Y1 = Top;
            guide.Y2 = PlotBottom;

            columns[i].Width = fieldWidth;
            columns[i].Margin = new(x - fieldWidth / 2, 0, 0, 0);
            fields[i].Value = points[i].Percent.ToString();
        }
        line.Stroke=Paint.Brush(Paint.Accent(this,"cyan"));line.Points=plot;
    }
}
