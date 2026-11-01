using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
namespace OpenRevo.Native;

// A measured panel avoids changing Grid definitions from SizeChanged/layout callbacks.
internal sealed class DesktopTiles : Panel
{
    internal int PreferredColumns{get;init;}=2;
    internal double MinimumItemWidth{get;init;}=340;
    internal bool PairFourColumns{get;init;}
    internal bool QuickSwitchColumns{get;init;}
    internal int Columns{get;private set;}=1;
    private double columnSpacing=14,rowSpacing=14;
    public double ColumnSpacing{get=>columnSpacing;set{if(columnSpacing==value)return;columnSpacing=value;InvalidateMeasure();}}
    public double RowSpacing{get=>rowSpacing;set{if(rowSpacing==value)return;rowSpacing=value;InvalidateMeasure();}}
    private readonly List<double> rowHeights=[];
    private readonly List<UIElement> visibleChildren=[];
    private IReadOnlyList<DesktopGridRow> rows=[];
    private static readonly DependencyProperty FullRowProperty=DependencyProperty.RegisterAttached("FullRow",typeof(bool),typeof(DesktopTiles),new PropertyMetadata(false));
    internal static void SetFullRow(UIElement child)=>child.SetValue(FullRowProperty,true);
    private int PickColumns(double width)
    {
        return DesktopGridGeometry.Columns(width,PreferredColumns,MinimumItemWidth,ColumnSpacing,PairFourColumns,QuickSwitchColumns);
    }
    protected override Size MeasureOverride(Size available)
    {
        double width=double.IsFinite(available.Width)?Math.Max(0,available.Width):PreferredColumns*(MinimumItemWidth+ColumnSpacing)-ColumnSpacing;
        Columns=PickColumns(width);rowHeights.Clear();visibleChildren.Clear();
        foreach(var child in Children)if(child.Visibility!=Visibility.Collapsed)visibleChildren.Add(child);
        rows=DesktopGridGeometry.Rows(width,Columns,ColumnSpacing,visibleChildren.Select(c=>(bool)c.GetValue(FullRowProperty)).ToArray());
        foreach(var row in rows)
        {
            double height=0;
            for(int i=0;i<row.Count;i++){var child=visibleChildren[row.Start+i];child.Measure(new Size(row.ItemWidth,double.PositiveInfinity));height=Math.Max(height,child.DesiredSize.Height);}
            rowHeights.Add(height);
        }
        return new Size(width,rowHeights.Sum()+Math.Max(0,rowHeights.Count-1)*RowSpacing);
    }
    protected override Size ArrangeOverride(Size final)
    {
        double y=0;
        for(int r=0;r<rows.Count;r++)
        {
            var row=rows[r];double width=Math.Max(0,(final.Width-ColumnSpacing*(row.Count-1))/row.Count),height=rowHeights[r];
            for(int i=0;i<row.Count;i++)visibleChildren[row.Start+i].Arrange(new Rect(i*(width+ColumnSpacing),y,width,height));
            y+=height+RowSpacing;
        }
        return final;
    }
}
internal sealed class DesktopSurface : Panel
{
    private readonly ScaleTransform scaleTransform=new();
    internal double ContentScale{get;private set;}=1;
    internal double MaximumContentWidth{get;set;}=1536;
    internal bool CenterVertically{get;set;}
    internal double ViewportHeight{get;set;}
    private double contentWidth,contentHeight;
    protected override Size MeasureOverride(Size available)
    {
        if(Children.Count==0)return new Size();
        double width=double.IsFinite(available.Width)?Math.Max(0,available.Width):MaximumContentWidth;
        ContentScale=1; // Preserve the 12 DIP text floor at narrow window sizes.
        contentWidth=Math.Min(MaximumContentWidth,width/ContentScale);
        Children[0].Measure(new Size(contentWidth,double.PositiveInfinity));contentHeight=Children[0].DesiredSize.Height;
        return new Size(width,CenterVertically?Math.Max(ViewportHeight,contentHeight*ContentScale):contentHeight*ContentScale);
    }
    protected override Size ArrangeOverride(Size final)
    {
        if(Children.Count==0)return final;
        double x=Math.Max(0,(final.Width-contentWidth*ContentScale)/2),y=CenterVertically?Math.Max(0,(final.Height-contentHeight*ContentScale)/2):0;
        var child=Children[0];child.RenderTransform=scaleTransform;scaleTransform.ScaleX=scaleTransform.ScaleY=ContentScale;
        child.Arrange(new Rect(x,y,contentWidth,contentHeight));return final;
    }
}
