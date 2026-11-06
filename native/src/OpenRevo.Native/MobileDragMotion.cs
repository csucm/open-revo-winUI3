using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using Windows.Foundation;
using Windows.System;

namespace OpenRevo.Native;

// Critical damping, retargeting with velocity continuity. Rendering is subscribed
// only while a gesture or a spring is moving. No idle timer or worker.
internal sealed class MobileDragMotion : IDisposable
{
    private readonly Func<FrameworkElement,Vector3,float,bool>? present;
    internal MobileDragMotion(Func<FrameworkElement,Vector3,float,bool>? present=null){this.present=present;}
    private sealed class State
    {
        internal Vector3 Position, Velocity, Target;
        internal float Scale=1, ScaleVelocity, TargetScale=1;
    }
    private readonly Dictionary<FrameworkElement,State> states=[];
    private bool listening, disposed;
    private long previous;
    internal Vector3 Position(FrameworkElement item)=>states.TryGetValue(item,out var s)?s.Position:Vector3.Zero;
    private State Get(FrameworkElement item)
    {
        if(!states.TryGetValue(item,out var s)){states[item]=s=new();if(item.IsLoaded)ElementCompositionPreview.SetIsTranslationEnabled(item,true);}
        return s;
    }
    internal void Follow(FrameworkElement item,Vector3 value,float scale=1.045f)
    {var s=Get(item);s.Position=s.Target=value;s.Velocity=Vector3.Zero;s.TargetScale=Design.MotionEnabled?scale:1;Write(item,s);Wake();}
    internal void Target(FrameworkElement item,Vector3 value,float scale=1)
    {var s=Get(item);s.Target=value;s.TargetScale=Design.MotionEnabled?scale:1;Wake();}
    internal void Rebase(FrameworkElement item,Vector3 delta)
    {var s=Get(item);s.Position+=delta;s.Target=Vector3.Zero;s.TargetScale=1;Write(item,s);Wake();}
    private void Wake(){if(disposed||listening)return;previous=Stopwatch.GetTimestamp();listening=true;CompositionTarget.Rendering+=Tick;}
    private static void Spring(ref float x,ref float v,float target,float dt,float omega)
    {float z=x-target,c=v+omega*z,e=MathF.Exp(-omega*dt);x=target+(z+c*dt)*e;v=(v-omega*c*dt)*e;}
    private void Tick(object? sender,object args)
    {
        long now=Stopwatch.GetTimestamp();float dt=(float)Math.Min(.04,Stopwatch.GetElapsedTime(previous,now).TotalSeconds);previous=now;bool moving=false;
        foreach(var (item,s) in states)
        {
            if(!item.IsLoaded)continue;
            if(!Design.MotionEnabled){s.Position=s.Target;s.Scale=s.TargetScale;s.Velocity=Vector3.Zero;s.ScaleVelocity=0;}
            else{Spring(ref s.Position.X,ref s.Velocity.X,s.Target.X,dt,27);Spring(ref s.Position.Y,ref s.Velocity.Y,s.Target.Y,dt,27);Spring(ref s.Position.Z,ref s.Velocity.Z,s.Target.Z,dt,27);Spring(ref s.Scale,ref s.ScaleVelocity,s.TargetScale,dt,32);}
            bool active=Vector3.DistanceSquared(s.Position,s.Target)>.0025f||s.Velocity.LengthSquared()>.0025f||Math.Abs(s.Scale-s.TargetScale)+Math.Abs(s.ScaleVelocity)>.002;
            if(!active){s.Position=s.Target;s.Velocity=Vector3.Zero;s.Scale=s.TargetScale;s.ScaleVelocity=0;}moving|=active;Write(item,s);
        }
        if(!moving){CompositionTarget.Rendering-=Tick;listening=false;}
    }
    private void Write(FrameworkElement item,State s)
    {
        if(!item.IsLoaded||present?.Invoke(item,s.Position,s.Scale)==true)return;var visual=ElementCompositionPreview.GetElementVisual(item);
        visual.Properties.InsertVector3("Translation",s.Position);
        visual.CenterPoint=new((float)item.ActualWidth/2,(float)item.ActualHeight/2,0);visual.Scale=new(s.Scale,s.Scale,1);
    }
    internal void Reset()
    {if(listening)CompositionTarget.Rendering-=Tick;listening=false;foreach(var (item,s) in states){s.Position=s.Target=s.Velocity=Vector3.Zero;s.Scale=s.TargetScale=1;s.ScaleVelocity=0;Write(item,s);}states.Clear();}
    public void Dispose(){Reset();disposed=true;}
}

internal sealed record MobileDragBoard(FrameworkElement Surface,Func<IReadOnlyList<FrameworkElement>> Items,
    Func<FrameworkElement,Rect> Slot,Action<IReadOnlyList<FrameworkElement>> Reorder,bool? Horizontal);

// One controller per editing region; Mini and overview share this state machine.
internal sealed class MobileDragController : IDisposable
{
    private readonly MobileDragBoard board;
    private readonly FrameworkElement root;
    private readonly Window window;
    private readonly ScrollViewer scroll;
    private readonly Func<bool> canBegin;
    private readonly Func<FrameworkElement,bool> draggable;
    private readonly Action<IReadOnlyList<FrameworkElement>> commit;
    private readonly Action<FrameworkElement> choose;
    private readonly Action<string> announce;
    private readonly FrameworkElement[] subscribed;
    private readonly MobileDragMotion motion;
    private readonly MobileDragLayer layer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer landing,edge;
    private readonly XamlRoot? xamlRoot;
    private readonly double dpi;
    private MobileDragController? peer;
    private Action<FrameworkElement,FrameworkElement>? exchange;
    private FrameworkElement? source,crossTarget;
    private List<FrameworkElement>? original;
    private Rect[] frozen=[];
    private List<(FrameworkElement Item,Rect RootRect)> peerSlots=[];
    private readonly Dictionary<UIElement,int> lifted=[];
    private Point point,press,grab;
    private uint pointerId;
    private double interactionScale=1,scrollOffset;
    private long edgeTime;
    private int insertion,direction;
    private bool dragging,settling,releasing,disposed,accepted;
    private Action? afterLanding;
    internal bool ReorderWithin {get;set;}=true;
    internal bool Busy=>source!=null||settling||peer?.source!=null||peer?.settling==true;
    internal MobileDragController(MobileDragBoard board,FrameworkElement root,Window window,ScrollViewer scroll,
        Action<IReadOnlyList<FrameworkElement>> commit,Action<FrameworkElement> choose,Action<string> announce,
        Func<bool> canBegin,Func<FrameworkElement,bool>? draggable=null)
    {
        this.board=board;this.root=root;this.window=window;this.scroll=scroll;this.commit=commit;this.choose=choose;this.announce=announce;this.canBegin=canBegin;this.draggable=draggable??(_=>true);
        layer=new(root,Cancel);motion=new(layer.TryRender);
        subscribed=board.Items().ToArray();xamlRoot=root.XamlRoot;dpi=xamlRoot?.RasterizationScale??1;
        landing=board.Surface.DispatcherQueue.CreateTimer();landing.IsRepeating=false;landing.Tick+=(_,_)=>Complete();
        edge=board.Surface.DispatcherQueue.CreateTimer();edge.Interval=TimeSpan.FromMilliseconds(16);edge.Tick+=OnEdge;
        foreach(var item in subscribed){item.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler(Press),true);item.PointerMoved+=Move;item.PointerReleased+=Release;item.PointerCanceled+=PointerCancel;item.PointerCaptureLost+=Lost;}
        root.AddHandler(UIElement.KeyDownEvent,new KeyEventHandler(Key),true);window.Activated+=Activated;board.Surface.Unloaded+=Unloaded;board.Surface.SizeChanged+=Resized;scroll.ViewChanged+=Scrolled;if(xamlRoot!=null)xamlRoot.Changed+=DpiChanged;
    }
    internal void Link(MobileDragController other,Action<FrameworkElement,FrameworkElement> swap)
    {peer=other;other.peer=this;exchange=swap;other.exchange=swap;}
    private static bool Tool(DependencyObject? child,FrameworkElement item)
    {for(var n=child;n!=null&&n!=item;n=VisualTreeHelper.GetParent(n))if(n is PaintButton {IsHitTestVisible:true} or Button {IsHitTestVisible:true})return true;return false;}
    private void Press(object sender,PointerRoutedEventArgs e)
    {
        if(disposed||Busy||!canBegin()||sender is not FrameworkElement item||Tool(e.OriginalSource as DependencyObject,item)||!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)return;
        var tr=board.Surface.TransformToVisual(root);var a=tr.TransformPoint(new(0,0));var b=tr.TransformPoint(new(1,0));interactionScale=Math.Max(.01,Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2)));
        source=item;point=press=e.GetCurrentPoint(board.Surface).Position;grab=e.GetCurrentPoint(item).Position;pointerId=e.Pointer.PointerId;scrollOffset=scroll.VerticalOffset;
        if(!item.CapturePointer(e.Pointer)){source=null;return;}item.Focus(FocusState.Pointer);e.Handled=true;
    }
    private void Move(object sender,PointerRoutedEventArgs e)
    {
        if(source==null||settling||e.Pointer.PointerId!=pointerId)return;point=e.GetCurrentPoint(board.Surface).Position;
        if(!dragging&&Math.Sqrt(Math.Pow(point.X-press.X,2)+Math.Pow(point.Y-press.Y,2))*interactionScale<5)return;
        if(!dragging){if(!draggable(source))return;dragging=true;original=[..board.Items()];frozen=original.Select(board.Slot).ToArray();insertion=original.IndexOf(source);direction=0;
            peerSlots=peer?.board.Items().Select(i=>(i,RootRect(i,peer.board.Surface,peer.board.Slot(i)))).ToList()??[];
            for(DependencyObject? n=source;n is UIElement ui&&n!=root;n=VisualTreeHelper.GetParent(n)){lifted[ui]=Canvas.GetZIndex(ui);Canvas.SetZIndex(ui,100);}
            Lift(source,true);source.Shadow=Design.MotionEnabled?new ThemeShadow():null;announce("拖动靠近新位置，控件提前让位 · Esc 取消");}
        Follow();Consider();UpdateEdge();e.Handled=true;
    }
    private void Follow(){if(source==null)return;var r=board.Slot(source);motion.Follow(source,new((float)(point.X-grab.X-r.X),(float)(point.Y-grab.Y-r.Y),Design.MotionEnabled?12:0));}
    private bool Inside(double margin=0){double p=margin/interactionScale;return point.X>=-p&&point.Y>=-p&&point.X<=board.Surface.ActualWidth+p&&point.Y<=board.Surface.ActualHeight+p;}
    private static Rect RootRect(FrameworkElement item,FrameworkElement parent,Rect slot)
    {var tr=parent.TransformToVisual(null);var a=tr.TransformPoint(new(slot.X,slot.Y));var b=tr.TransformPoint(new(slot.Right,slot.Bottom));return new(a,new Size(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y)));}
    private Point RootPoint()=>board.Surface.TransformToVisual(null).TransformPoint(point);
    private void Lift(FrameworkElement item,bool held)=>layer.Lift(item,()=>
    {
        var slot=board.Slot(item);var tr=board.Surface.TransformToVisual(root);
        var a=tr.TransformPoint(new(slot.X,slot.Y));var b=tr.TransformPoint(new(slot.Right,slot.Bottom));
        return new Rect(a,new Size(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y)));
    },held);
    private void ClearCross(){if(crossTarget!=null&&peer!=null)peer.motion.Target(crossTarget,Vector3.Zero);crossTarget=null;}
    private void Consider()
    {
        if(source==null||!dragging||original==null)return;
        var p=RootPoint();var nearest=peerSlots.OrderBy(s=>Distance(p,new(s.RootRect.X+s.RootRect.Width/2,s.RootRect.Y+s.RootRect.Height/2))).FirstOrDefault();
        if(nearest.Item!=null&&peerSlots.Any(s=>Contains(s.RootRect,p,crossTarget!=null?14:5)))
        {
            if(crossTarget!=null&&nearest.Item!=crossTarget){var old=peerSlots.First(s=>s.Item==crossTarget);if(Distance(p,Center(old.RootRect))-Distance(p,Center(nearest.RootRect))<14)nearest=old;}
            if(!board.Items().SequenceEqual(original))Reorder(original);
            if(crossTarget!=nearest.Item){ClearCross();crossTarget=nearest.Item;if(peer!.draggable(crossTarget)){peer.Lift(crossTarget,false);peer.motion.Target(crossTarget,new(-7,-7,0),.94f);}announce("松手交换，两边控件将回到对方位置");}
            Follow();return;
        }
        ClearCross();if(!ReorderWithin||!Inside(16))return;
        var held=frozen[original.IndexOf(source)];var c=new Point(point.X-grab.X+held.Width/2,point.Y-grab.Y+held.Height/2);
        if(board.Horizontal is bool horizontal)
        {
            double axis=horizontal?c.X:c.Y;var centers=frozen.Select(r=>horizontal?r.X+r.Width/2:r.Y+r.Height/2).ToArray();
            for(int n=0;n<centers.Length;n++){
                if(insertion<centers.Length-1&&axis>(centers[insertion]+centers[insertion+1])/2+(direction==-1?.19:-.10)*(centers[insertion+1]-centers[insertion])){insertion++;direction=1;}
                else if(insertion>0&&axis<(centers[insertion-1]+centers[insertion])/2+(direction==1?-.19:.10)*(centers[insertion]-centers[insertion-1])){insertion--;direction=-1;}else break;
            }
        }
        else
        {
            int next=Enumerable.Range(0,frozen.Length).Where(i=>i!=insertion).OrderBy(i=>Distance(c,Center(frozen[i]))).DefaultIfEmpty(insertion).First();
            var current=Center(frozen[insertion]);var target=Center(frozen[next]);double length=Distance(current,target);
            double advantage=(Math.Pow(Distance(c,current),2)-Math.Pow(Distance(c,target),2))/Math.Max(1,length*length);
            int dir=Math.Sign(next-insertion);if(next!=insertion&&advantage>(direction!=0&&dir!=direction?.38:-.20)){insertion=next;direction=dir;}
        }
        var order=original.ToList();order.Remove(source);order.Insert(insertion,source);if(!board.Items().SequenceEqual(order)){Reorder(order);announce("已让出新位置 · 松手放置");Follow();}
    }
    private static Point Center(Rect r)=>new(r.X+r.Width/2,r.Y+r.Height/2);
    private static double Distance(Point a,Point b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static bool Contains(Rect r,Point p,double margin)=>p.X>=r.X-margin&&p.X<=r.Right+margin&&p.Y>=r.Y-margin&&p.Y<=r.Bottom+margin;
    private void Reorder(IReadOnlyList<FrameworkElement> order)
    {var before=board.Items().ToDictionary(i=>i,board.Slot);board.Reorder(order);foreach(var item in order){if(item==source)continue;var old=before[item];var next=board.Slot(item);motion.Rebase(item,new((float)(old.X-next.X),(float)(old.Y-next.Y),0));}}
    private void Release(object sender,PointerRoutedEventArgs e)
    {
        if(source==null||settling||e.Pointer.PointerId!=pointerId)return;point=e.GetCurrentPoint(board.Surface).Position;e.Handled=true;
        if(!dragging){var item=source;source=null;ReleaseCapture(item);if(Contains(board.Slot(item),point,0))choose(item);return;}
        Consider();edge.Stop();settling=true;accepted=Inside(16)&&ReorderWithin;
        if(crossTarget!=null&&peer!=null&&exchange!=null){var a=source;var b=crossTarget;var ar=RootRect(a,board.Surface,board.Slot(a));var br=peerSlots.First(s=>s.Item==b).RootRect;
            var from=board.Slot(a);var to=board.Surface.TransformToVisual(null).Inverse.TransformPoint(Center(br));
            motion.Target(a,new((float)(to.X-from.X-from.Width/2),(float)(to.Y-from.Y-from.Height/2),0),(float)Math.Min(br.Width/ar.Width,br.Height/ar.Height));
            if(peer.draggable(b)){var other=peer.board.Slot(b);var back=peer.board.Surface.TransformToVisual(null).Inverse.TransformPoint(Center(ar));peer.motion.Target(b,new((float)(back.X-other.X-other.Width/2),(float)(back.Y-other.Y-other.Height/2),0),(float)Math.Min(ar.Width/br.Width,ar.Height/br.Height));}
            afterLanding=()=>exchange(a,b);accepted=false;
        }else{if(!accepted&&original!=null)Reorder(original);Follow();motion.Target(source,Vector3.Zero);}
        dragging=false;ReleaseCapture(source);if(!Design.MotionEnabled)Complete();else{landing.Interval=TimeSpan.FromMilliseconds(afterLanding!=null?380:280);landing.Start();}
    }
    private void ReleaseCapture(FrameworkElement item){releasing=true;item.ReleasePointerCaptures();releasing=false;}
    private void Complete()
    {if(source==null||disposed)return;var order=board.Items().ToArray();bool changed=accepted&&original!=null&&!original.SequenceEqual(order);var action=afterLanding;afterLanding=null;
        Reset();if(action!=null)action();else if(changed)commit(order);else announce(accepted?"位置未改变":"已取消拖动 · 恢复原顺序");}
    private void Reset()
    {if(source!=null){source.Shadow=null;ReleaseCapture(source);}motion.Reset();peer?.motion.Reset();layer.Clear();peer?.layer.Clear();foreach(var (item,z) in lifted)Canvas.SetZIndex(item,z);lifted.Clear();source=null;crossTarget=null;original=null;dragging=settling=false;peerSlots.Clear();}
    internal void Cancel(){if(source==null){if(peer?.source!=null)peer.Cancel();return;}landing.Stop();edge.Stop();afterLanding=null;if(original!=null)board.Reorder(original);Reset();announce("已取消拖动 · 恢复原顺序");}
    private void PointerCancel(object sender,PointerRoutedEventArgs e)=>Cancel();
    private void Lost(object sender,PointerRoutedEventArgs e){if(!releasing&&!settling)Cancel();}
    private void Key(object sender,KeyRoutedEventArgs e){if(source!=null||settling){if(e.Key==VirtualKey.Escape)Cancel();e.Handled=true;}}
    private void Activated(object sender,WindowActivatedEventArgs e){if(e.WindowActivationState==WindowActivationState.Deactivated)Cancel();}
    private void Unloaded(object sender,RoutedEventArgs e)=>Dispose();
    private void Resized(object sender,SizeChangedEventArgs e){if(Math.Abs(e.NewSize.Width-e.PreviousSize.Width)>.5)Cancel();}
    private void DpiChanged(XamlRoot sender,XamlRootChangedEventArgs e){if(Math.Abs(sender.RasterizationScale-dpi)>.001)Cancel();}
    private double EdgeSpeed(){if(!dragging||source==null)return 0;var p=board.Surface.TransformToVisual(scroll).TransformPoint(point);return p.Y<54?-Math.Min(440,(54-p.Y)*7):p.Y>scroll.ActualHeight-54?Math.Min(440,(p.Y-scroll.ActualHeight+54)*7):0;}
    private void UpdateEdge(){if(EdgeSpeed()==0)edge.Stop();else if(!edge.IsRunning){edgeTime=Environment.TickCount64;edge.Start();}}
    private void Scrolled(object? sender,ScrollViewerViewChangedEventArgs args)
    {
        if(source==null)return;if(settling){Cancel();return;}double delta=scroll.VerticalOffset-scrollOffset;scrollOffset=scroll.VerticalOffset;if(!dragging||Math.Abs(delta)<.01)return;
        point=new(point.X,point.Y+delta/interactionScale);for(int i=0;i<peerSlots.Count;i++){var s=peerSlots[i];s.RootRect.Y-=delta;peerSlots[i]=s;}Follow();Consider();layer.Refresh();peer?.layer.Refresh();
    }
    private void OnEdge(Microsoft.UI.Dispatching.DispatcherQueueTimer sender,object args)
    {double speed=EdgeSpeed();if(disposed||speed==0){edge.Stop();return;}long now=Environment.TickCount64;double dt=Math.Min(32,now-edgeTime)/1000d;edgeTime=now;double before=scroll.VerticalOffset,after=Math.Clamp(before+speed*dt,0,scroll.ScrollableHeight);if(Math.Abs(after-before)<.1){edge.Stop();return;}scroll.ChangeView(null,after,null,true);}
    public void Dispose()
    {if(disposed)return;Cancel();disposed=true;landing.Stop();edge.Stop();motion.Dispose();layer.Dispose();foreach(var item in subscribed){item.RemoveHandler(UIElement.PointerPressedEvent,new PointerEventHandler(Press));item.PointerMoved-=Move;item.PointerReleased-=Release;item.PointerCanceled-=PointerCancel;item.PointerCaptureLost-=Lost;}root.RemoveHandler(UIElement.KeyDownEvent,new KeyEventHandler(Key));window.Activated-=Activated;board.Surface.Unloaded-=Unloaded;board.Surface.SizeChanged-=Resized;scroll.ViewChanged-=Scrolled;if(xamlRoot!=null)xamlRoot.Changed-=DpiChanged;if(peer!=null){peer.peer=null;peer=null;}}
}
