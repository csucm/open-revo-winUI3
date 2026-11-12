using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OpenRevo.Native;

internal sealed class MiniEditItem : ContentControl
{
    internal string? Value { get; init; }
    internal TextBlock? PositionLabel { get; init; }
    internal MiniEditItem(string name, UIElement body)
    {
        // The readonly device face cannot receive input. Give its editor slot a
        // transparent hit surface, including when the device button is disabled.
        var hitSurface = new Grid { Background = Paint.Brush("00000000") };
        hitSurface.Children.Add(body);
        Template = Paint.PresenterTemplate(); Content = hitSurface;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = true; UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, "Enter 替换，Alt 加方向键排序，Delete 移除；拖动中 Esc 取消。");
    }
}

// Slots are post-layout DIP rectangles, never animated visual bounds.
internal sealed class MiniEditorBoard(bool horizontal, int? columns = null) : Panel
{
    internal readonly List<MiniEditItem> Order = [];
    private readonly Dictionary<MiniEditItem, Rect> slots = [];
    internal bool Horizontal => horizontal;
    internal int? Columns => columns;
    internal Rect Slot(MiniEditItem item) => slots.GetValueOrDefault(item);
    internal void Add(MiniEditItem item) { Order.Add(item); Children.Add(item); }
    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : 400;
        int count = columns ?? Math.Max(1, Order.Count);
        double cell = horizontal ? Math.Max(0, (width - 4 * (count - 1)) / count) : width;
        foreach (var item in Order) item.Measure(new(cell, double.PositiveInfinity));
        double height = horizontal ? Order.Select(item => item.DesiredSize.Height).DefaultIfEmpty(50).Max()
            : Order.Sum(item => item.DesiredSize.Height) + Math.Max(0, Order.Count - 1) * 6;
        if (columns.HasValue) height = Math.Ceiling(Order.Count / (double)count) * (height + 6) - (Order.Count > 0 ? 6 : 0);
        return new(width, Math.Max(0, height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        int count = columns ?? Math.Max(1, Order.Count);
        double axis = 0, width = horizontal ? Math.Max(0, (final.Width - 4 * (count - 1)) / count) : final.Width;
        double rowHeight = columns.HasValue ? Order.Select(item => item.DesiredSize.Height).DefaultIfEmpty(0).Max() : final.Height;
        for (int i = 0; i < Order.Count; i++)
        {
            var item = Order[i];
            var rect = columns.HasValue ? new Rect((i % count) * (width + 4), (i / count) * (rowHeight + 6), width, rowHeight)
                : horizontal ? new Rect(axis, 0, width, final.Height) : new Rect(0, axis, width, item.DesiredSize.Height);
            slots[item] = rect; item.Arrange(rect); axis += (horizontal ? width : rect.Height) + (horizontal ? 4 : 6);
        }
        return final;
    }
    internal void Reorder(IEnumerable<MiniEditItem> order)
    {
        Order.Clear(); Order.AddRange(order);
        // Text changes invalidate measure. Never make them during ArrangeOverride.
        for (int i = 0; i < Order.Count; i++)
        {
            string number = (horizontal ? "" : "⠿  ") + (i + 1);
            if (Order[i].PositionLabel is {} label && label.Text != number) label.Text = number;
        }
        InvalidateMeasure(); UpdateLayout();
    }
}

// The same interaction engine also drives the overview tiles.
internal sealed class MiniEditorDrag : IDisposable
{
    private readonly MobileDragController controller;
    internal bool Busy=>controller.Busy;
    internal MiniEditorDrag(MiniEditorBoard board,FrameworkElement root,Window window,ScrollViewer scroll,
        Action<IReadOnlyList<MiniEditItem>> commit,Action<int> choose,Action<string> announce,Func<bool> canBegin)
    {
        controller=new(new(board,()=>board.Order.Cast<FrameworkElement>().ToArray(),item=>board.Slot((MiniEditItem)item),
            order=>board.Reorder(order.Cast<MiniEditItem>()),board.Columns.HasValue?null:board.Horizontal),root,window,scroll,
            order=>commit(order.Cast<MiniEditItem>().ToArray()),item=>choose(board.Order.IndexOf((MiniEditItem)item)),announce,canBegin,item=>((MiniEditItem)item).Value!=null);
    }
    internal void Link(MiniEditorDrag library,Action<MiniEditItem,MiniEditItem> exchange)
    {library.controller.ReorderWithin=false;controller.Link(library.controller,(a,b)=>exchange((MiniEditItem)a,(MiniEditItem)b));}
    internal void Cancel()=>controller.Cancel();
    public void Dispose()=>controller.Dispose();
}
