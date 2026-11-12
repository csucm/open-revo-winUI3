using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using Windows.Foundation;

namespace OpenRevo.Native;

// Gesture-only presentation above layout/scroll clips. The original controls
// retain their parents, layout slots and pointer capture.
internal sealed class MobileDragLayer(FrameworkElement root,Action failed):IDisposable
{
    private sealed class Entry(FrameworkElement item,Func<Rect> bounds)
    {
        internal readonly FrameworkElement Item=item;
        internal readonly Func<Rect> Bounds=bounds;
        internal readonly double Opacity=item.Opacity;
        internal readonly CancellationTokenSource Stop=new();
        internal Image? Picture;
        internal Vector3 Position;
        internal float Scale=1;
    }
    private readonly Dictionary<FrameworkElement,Entry> entries=[];
    private Canvas? canvas;
    private int generation;
    private bool disposed;
    internal void Lift(FrameworkElement item,Func<Rect> bounds,bool held)
    {
        if(disposed||entries.ContainsKey(item))return;
        if(root is not Panel panel)throw new InvalidOperationException("拖动浮层需要页面根面板。");
        if(canvas==null)
        {
            canvas=new Canvas{IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
            Grid.SetRowSpan(canvas,Math.Max(1,root is Grid grid?grid.RowDefinitions.Count:1));
            Grid.SetColumnSpan(canvas,Math.Max(1,root is Grid columns?columns.ColumnDefinitions.Count:1));
            panel.Children.Add(canvas);
        }
        // WinUI limits Canvas.ZIndex to 1,000,000, despite its Int32 type.
        Canvas.SetZIndex(canvas,held?1_000_000:999_999);
        var entry=new Entry(item,bounds);entries.Add(item,entry);_=Capture(entry,held,generation);
    }
    private bool Current(Entry entry,int stamp)=>!disposed&&stamp==generation&&entries.TryGetValue(entry.Item,out var current)&&ReferenceEquals(current,entry);
    private async Task Capture(Entry entry,bool held,int stamp)
    {
        try
        {
            var bounds=entry.Bounds();double dpi=root.XamlRoot?.RasterizationScale??1;
            int width=Math.Max(1,(int)Math.Ceiling(bounds.Width*dpi)),height=Math.Max(1,(int)Math.Ceiling(bounds.Height*dpi));
            if(!entry.Item.IsLoaded||bounds.Width<=0||bounds.Height<=0||width>8192||height>8192||(long)width*height>8*1024*1024)
                throw new InvalidOperationException("拖动图像尺寸暂不可用。");
            var bitmap=new RenderTargetBitmap();
            // TryRender holds the original at rest until capture completes.
            await bitmap.RenderAsync(entry.Item,width,height).AsTask(entry.Stop.Token);
            if(!Current(entry,stamp))return;
            if(bitmap.PixelWidth<=0||bitmap.PixelHeight<=0)throw new InvalidOperationException("拖动图像未生成。");
            var image=new Image{Source=bitmap,Width=bounds.Width,Height=bounds.Height,Stretch=Stretch.Uniform,IsHitTestVisible=false,Opacity=0};
            Canvas.SetZIndex(image,held?2:1);entry.Picture=image;
            void Show()
            {
                if(!Current(entry,stamp)||!image.IsLoaded)return;
                ElementCompositionPreview.SetIsTranslationEnabled(image,true);
                if(Design.MotionEnabled)image.Shadow=new ThemeShadow();
                Write(entry);image.Opacity=1;entry.Item.Opacity=0;
            }
            image.Loaded+=(_,_)=>Show();canvas!.Children.Add(image);if(image.IsLoaded)Show();
        }
        catch(OperationCanceledException){ }
        catch(Exception error)
        {
            if(!Current(entry,stamp))return;
            OpenRevo.Core.DiagnosticLog.Failure("drag.overlay.capture.failed",error);
            root.DispatcherQueue.TryEnqueue(()=>{if(Current(entry,stamp))failed();});
        }
    }
    internal bool TryRender(FrameworkElement item,Vector3 position,float scale)
    {
        if(!entries.TryGetValue(item,out var entry))return false;
        entry.Position=position;entry.Scale=scale;Write(entry);return true;
    }
    private static void Write(Entry entry)
    {
        if(entry.Picture is not {IsLoaded:true} picture)return;
        var bounds=entry.Bounds();
        // Uniformly map local deltas into root DIPs, including the 75% preview.
        double factor=entry.Item.ActualWidth>0?bounds.Width/entry.Item.ActualWidth:1;
        var visual=ElementCompositionPreview.GetElementVisual(picture);
        visual.Properties.InsertVector3("Translation",new((float)(bounds.X+entry.Position.X*factor),(float)(bounds.Y+entry.Position.Y*factor),entry.Position.Z));
        visual.CenterPoint=new((float)picture.Width/2,(float)picture.Height/2,0);visual.Scale=new(entry.Scale,entry.Scale,1);
    }
    internal void Refresh(){foreach(var entry in entries.Values)Write(entry);}
    internal void Clear()
    {
        generation++;
        foreach(var entry in entries.Values)
        {
            entry.Stop.Cancel();entry.Stop.Dispose();entry.Item.Opacity=entry.Opacity;
            if(entry.Picture is {} picture){picture.Shadow=null;picture.Source=null;}
        }
        entries.Clear();
        if(canvas!=null){canvas.Children.Clear();if(root is Panel panel)panel.Children.Remove(canvas);canvas=null;}
    }
    public void Dispose(){if(disposed)return;Clear();disposed=true;}
}
