using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly List<object> desktopAlignmentSamples=[];
    private void AuditDesktopAlignment(string name,Action<string,bool> check)
    {
        var pairs=DetailDescendants(page).OfType<FrameworkElement>().SelectMany(element=>element.Tag switch
        {DesktopAlignment pair=>new[]{pair},DesktopAlignment[] group=>group,_=>Array.Empty<DesktopAlignment>()})
            .Where(pair=>pair.First.ActualWidth>0&&pair.First.ActualHeight>0&&pair.Second.ActualWidth>0&&pair.Second.ActualHeight>0)
            .Select(pair=>
            {
                var first=pair.First.TransformToVisual(page).TransformPoint(new Point());
                var second=pair.Second.TransformToVisual(page).TransformPoint(new Point());
                // A stretched, centered TextBlock reports its text width in
                // ActualWidth; its text is positioned in the full layout slot.
                double Width(FrameworkElement element)=>element is TextBlock{TextAlignment:TextAlignment.Center,HorizontalAlignment:HorizontalAlignment.Stretch}?LayoutInformation.GetLayoutSlot(element).Width:element.ActualWidth;
                double a=pair.Horizontal?first.X+Width(pair.First)/2:first.Y+pair.First.ActualHeight/2;
                double b=pair.Horizontal?second.X+Width(pair.Second)/2:second.Y+pair.Second.ActualHeight/2;
                return new{pair.Role,axis=pair.Horizontal?"horizontal":"vertical",firstCenter=a,secondCenter=b,delta=Math.Abs(a-b)};
            }).ToArray();
        check(name+" icon/text and row controls share their intended centers",pairs.All(pair=>pair.delta<=1.5));
        desktopAlignmentSamples.Add(new{name,currentPage,customTuningOpen,colorTuningOpen,width=page.ActualWidth,pairCount=pairs.Length,pairs});
    }
}

