using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using System.Text.Json;
using System.Runtime.InteropServices;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint WindowReviewMessage(nint window,uint message,nuint wParam,nint lParam);
    private async Task RunWindowFixChecks()
    {
        var tests=new List<object>();var sizes=new List<object>();var rows=new List<object>();bool passed=true;
        void Check(string name,bool ok){tests.Add(new{name,passed=ok});passed&=ok;}
        async Task Settle(){await Task.Delay(130);root.UpdateLayout();await Task.Delay(60);root.UpdateLayout();}
        try
        {
            if(mini)ToggleMini();root.RequestedTheme=ElementTheme.Light;
            await ReadDesktopServices(true);while(desktopServicesReading)await Task.Delay(30);
            currentPage="power";Render();await Settle();
            while(desktopServicesReading)await Task.Delay(30);
            var firstStarted=new TaskCompletionSource();var releaseFirst=new TaskCompletionSource();var calls=new List<string>();
            desktopServiceReviewExecutor=async name=>{calls.Add(name);if(name=="power-office"){firstStarted.SetResult();await releaseFirst.Task;}};
            var before=desktopPowerSwitches.ToDictionary(k=>k.Key,k=>k.Value.IsEnabled);
            Task first=DesktopServiceAction("power-office",()=>throw new Exception("A review must not execute a setter"));
            await firstStarted.Task;
            Check("Only the office switch is busy; unrelated power switches keep their enabled state",!desktopPowerSwitches["office"].IsEnabled&&desktopPowerHints["office"].Text=="设置中"&&desktopPowerSwitches.Where(p=>p.Key!="office").All(p=>p.Value.IsEnabled==before[p.Key]));
            Task second=DesktopServiceAction("power-link",()=>throw new Exception("A review must not execute a setter"));
            Check("Another power switch accepts an intent while one is running",desktopBusyServices.Contains("power-link")&&desktopPowerHints["link"].Text=="等待中"&&!second.IsCompleted);
            Check("Unrelated sleep policies remain editable while two other operations are pending",desktopPowerSwitches.Where(p=>p.Key is not("office" or "link")).All(p=>p.Value.IsEnabled==before[p.Key]));
            releaseFirst.SetResult();await Task.WhenAll(first,second);
            Check("Accepted intents execute in order and clear their own busy state",calls.SequenceEqual(new[]{"power-office","power-link"})&&desktopBusyServices.Count==0&&desktopPowerSwitches.All(p=>p.Value.IsEnabled==before[p.Key]));
            desktopServiceReviewExecutor=null;
            desktopBusyServices.Add("power-master");desktopRunningService="power-master";SyncDesktopServices();
            Check("The master guard affects its six policies and leaves supply controls alone",OpenRevo.Core.DesktopPower.Policies.All(p=>!desktopPowerSwitches[p.Key].IsEnabled)&&new[]{"usb","recovery","office","link"}.All(key=>desktopPowerSwitches[key].IsEnabled==before[key]));
            desktopBusyServices.Clear();desktopRunningService=null;SyncDesktopServices();
            foreach(var(width,height)in new[]{(1500,960),(1180,850),(1000,720),(840,600),(1,1),(840,600),(1180,850),(1000,720),(840,600),(1500,960)})
            {
                ResizeLogical(width,height);
                foreach(string id in new[]{"overview","power","display","devices","plugins","settings"})
                {
                    currentPage=id;customTuningOpen=false;Render();await Settle();
                    double scale=desktopSurface.ContentScale;var origin=page.TransformToVisual(scroll).TransformPoint(new Point());
                    bool within=origin.X>=-1&&origin.X+page.ActualWidth*scale<=scroll.ActualWidth+1;
                    Check($"{id} at requested {width}x{height} keeps its rendered page inside the viewport",within);
                    var minimum=MinimumDesktopPixels();Check($"{id} respects desktop minimum at {width}x{height}",AppWindow.Size.Width>=minimum.Width&&AppWindow.Size.Height>=minimum.Height);
                    Check($"{id} uses a bounded, readable scale at {width}x{height}",scale>=.919&&scale<=1.001);
                    if(navigation.PaneDisplayMode==NavigationViewPaneDisplayMode.LeftMinimal)
                    {
                        var toggle=DetailDescendants(navigation).OfType<Button>().FirstOrDefault(b=>b.Name=="TogglePaneButton");
                        if(toggle!=null&&page.Children.FirstOrDefault() is FrameworkElement heading)
                        {
                            var navPosition=toggle.TransformToVisual(root).TransformPoint(new Point());var titlePosition=heading.TransformToVisual(root).TransformPoint(new Point());
                            Check($"{id} heading does not overlap the compact navigation button at {width}",titlePosition.Y>=navPosition.Y+toggle.ActualHeight-1||titlePosition.X>=navPosition.X+toggle.ActualWidth+1);
                        }
                    }
                    var clippedTitles=DetailDescendants(page).OfType<TextBlock>().Where(t=>t.TextWrapping==TextWrapping.NoWrap&&t.ActualWidth>0).Where(t=>
                    {
                        var probe=new TextBlock{Text=t.Text,FontFamily=t.FontFamily,FontSize=t.FontSize,FontWeight=t.FontWeight};probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                        return probe.DesiredSize.Width>Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(t).Width+1;
                    }).Select(t=>t.Text).ToArray();
                    Check($"{id} row titles fit without forced wrapping or clipping at {width}",clippedTitles.Length==0);
                    sizes.Add(new{id,requestedWidth=width,requestedHeight=height,actualWidth=AppWindow.Size.Width,actualHeight=AppWindow.Size.Height,viewport=scroll.ActualWidth,pageWidth=page.ActualWidth,scale,within,navigation=navigation.PaneDisplayMode.ToString()});
                    if(id=="power")
                    {
                        foreach(var(key,toggle)in desktopPowerSwitches.Where(p=>p.Key!="master"))
                        {
                            var parent=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(toggle) as FrameworkElement;
                            if(parent?.Tag is not DesktopAlignment alignment)continue;
                            var a=alignment.First.TransformToVisual(page).TransformPoint(new Point());var b=alignment.Second.TransformToVisual(page).TransformPoint(new Point());
                            double delta=Math.Abs(a.Y+alignment.First.ActualHeight/2-b.Y-alignment.Second.ActualHeight/2);
                            rows.Add(new{width,key,delta});Check($"Power {key} switch aligns with its text group at {width}",delta<=1);
                        }
                        if(width==840||width==1180)await CaptureDesktopReview($"WindowFix-power-{width}");
                    }
                    if(id=="overview"&&width==840){Check("Minimum overview retains a two-column telemetry layout",overviewMetrics?.Columns==2);await CaptureDesktopReview("WindowFix-overview-minimum");}
                }
            }
            int bytes=Marshal.SizeOf<WindowMinMax>();nint memory=Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.StructureToPtr(new WindowMinMax(),memory,false);
                WindowReviewMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0024,0,memory);
                var track=Marshal.PtrToStructure<WindowMinMax>(memory);var min=MinimumDesktopPixels();
                Check("Native WM_GETMINMAXINFO enforces DPI-aware minimum",track.MinTrack.X>=min.Width&&track.MinTrack.Y>=min.Height);
                ToggleMini();await Settle();
                var point=page.RenderTransform?.TransformPoint(new Point(1,1))??new Point(1,1);
                Check("Mini retains its unscaled content parent",mini&&ReferenceEquals(scroll.Content,page)&&Math.Abs(point.X-1)<.001&&Math.Abs(point.Y-1)<.001);
                Marshal.StructureToPtr(new WindowMinMax(),memory,false);WindowReviewMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0024,0,memory);
                var miniTrack=Marshal.PtrToStructure<WindowMinMax>(memory);Check("Full-panel minimum does not constrain mini",miniTrack.MinTrack.X<min.Width);
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        catch(Exception error){tests.Add(new{error=error.ToString(),passed=false});passed=false;}
        finally
        {
            desktopServiceReviewExecutor=null;desktopBusyServices.Clear();desktopRunningService=null;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"window-fix-review.json"),JsonSerializer.Serialize(new{allPassed=passed,tests,sizes,rows,hardwareSettersInvoked=false,fullUiSuiteRun=false},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=passed?0:1;
        }
    }
}
