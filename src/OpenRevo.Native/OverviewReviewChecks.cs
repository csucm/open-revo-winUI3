using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunOverviewLayoutChecks(Action<string,bool> check,bool capture)
    {
        var bounds=new Windows.Graphics.RectInt32(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
        bool pane=navigation.IsPaneOpen;var samples=new List<object>();
        async Task Measure(string name)
        {
            if(upstream is { } core)
            {
                await overviewSampling.ReadForReviewAsync(core, latest?.OnAc==true&&ec.GpuFan is >0);
                UpdateOverviewValues();
            }
            await Task.Delay(300);root.UpdateLayout();await Task.Delay(80);root.UpdateLayout();
            var origin=page.TransformToVisual(scroll).TransformPoint(new Point(0,0));
            double expected=(scroll.ActualWidth-page.ActualWidth*desktopSurface.ContentScale)/2;
            double expectedTop=Math.Max(0,(scroll.ActualHeight-page.ActualHeight*desktopSurface.ContentScale)/2);
            bool centered=page.ActualWidth*desktopSurface.ContentScale<=scroll.ActualWidth+1&&origin.X>=-1&&Math.Abs(origin.X-expected)<=2;
            bool metrics=overviewMetrics?.Children.OfType<FrameworkElement>().All(card=>
            {
                var p=card.TransformToVisual(page).TransformPoint(new Point(0,0));
                return p.X>=page.Padding.Left-1&&p.X+card.ActualWidth<=page.ActualWidth-page.Padding.Right+1;
            })==true;
            check($"{name} overview is centered and all four telemetry cards stay within the page",centered&&metrics);
            check($"{name} overview is vertically centered when it fits and never clips the top",origin.Y>=-1&&Math.Abs(origin.Y-expectedTop)<=2);
            var cardRows=overviewMetrics!.Children.OfType<Border>().Select(card=>
            {
                var body=(Grid)card.Child;var bar=(ProgressBar)body.Children[2];var footer=(FrameworkElement)body.Children[3];var number=(FrameworkElement)body.Children[1];
                var p=card.TransformToVisual(overviewMetrics).TransformPoint(new Point());
                return new{row=Grid.GetRow(card),cardTop=p.Y,barY=bar.TransformToVisual(overviewMetrics).TransformPoint(new Point()).Y,footerY=footer.TransformToVisual(overviewMetrics).TransformPoint(new Point()).Y,numberBottom=number.TransformToVisual(overviewMetrics).TransformPoint(new Point()).Y+number.ActualHeight};
            }).ToArray();
            bool barsAligned=cardRows.GroupBy(card=>card.row).All(row=>row.Max(card=>card.barY)-row.Min(card=>card.barY)<=1.5&&row.Max(card=>card.footerY)-row.Min(card=>card.footerY)<=1.5&&row.Max(card=>card.numberBottom)-row.Min(card=>card.numberBottom)<=1.5);
            check($"{name} telemetry bars, value bottoms and footers align across each visual row",barsAligned);
            samples.Add(new{name,viewportWidth=scroll.ActualWidth,pageWidth=page.ActualWidth,viewportHeight=scroll.ActualHeight,pageHeight=page.ActualHeight,left=origin.X,expected,top=origin.Y,expectedTop,centered,metrics,barsAligned,cardRows,metricColumns=overviewMetrics?.Columns,modeColumns=overviewModes?.Columns,switchColumns=overviewSwitches?.Columns});
            if(capture)await CaptureDesktopReview(name);
        }
        try
        {
            root.RequestedTheme=ElementTheme.Light;currentPage="overview";customTuningOpen=false;
            foreach(var(width,height,open)in new[]{(1440,960,false),(1180,850,true),(850,780,false),(1440,960,true),(1180,850,false)})
            {
                ResizeLogical(width,height);navigation.IsPaneOpen=open;Render();
                await Measure($"Overview-{width}-{(open?"expanded":"collapsed")}");
            }
            if(AppWindow.Presenter is OverlappedPresenter presenter)
            {
                navigation.IsPaneOpen=false;presenter.Maximize();Render();await Measure("Overview-maximized");
                check("Maximized overview matches the reference's four telemetry and four performance columns",overviewMetrics?.Columns==4&&overviewModes?.Columns==4);
                check("Overview has eleven wired shortcuts including strong cooling with no placeholder controls",overviewSwitches?.Children.Count==11&&extraSwitchButtons.Keys.Order().SequenceEqual(new[]{"bluetooth","fn","osd","touchpad","wifi"})&&overviewBoost!=null&&overviewSwitches.Children.Contains(overviewBoost)&&overviewTools?.Children.Count==2);
                root.RequestedTheme=ElementTheme.Dark;await Measure("Overview-maximized-dark");presenter.Restore();
            }
            foreach(string id in new[]{"display"})
            {
                currentPage=id;Render();await Task.Delay(150);root.UpdateLayout();
                var origin=page.TransformToVisual(scroll).TransformPoint(new Point(0,0));
                check($"{id} detail page is centered after repeated overview resize",Math.Abs(origin.X-(scroll.ActualWidth-page.ActualWidth*desktopSurface.ContentScale)/2)<=2&&page.ActualWidth*desktopSurface.ContentScale<=scroll.ActualWidth+1);
            }
        }
        finally
        {
            AppWindow.MoveAndResize(bounds);navigation.IsPaneOpen=pane;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"overview-layout.json"),System.Text.Json.JsonSerializer.Serialize(samples,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
    }
}
