using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.Foundation;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunDesignReview()
    {
        var checks=new List<object>();var samples=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        try
        {
            Check("Review preferences are isolated",AppData.IsIsolatedReviewData);
            if(!AppData.IsIsolatedReviewData)throw new InvalidOperationException("Isolated review data is required.");
            if(mini)ToggleMini();
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(int width in new[]{1180,840})
            {
                root.RequestedTheme=theme;ResizeLogical(width,width==840?600:900);
                foreach(string id in new[]{"overview","display","power","devices","plugins","settings"})
                {
                    currentPage=id;customTuningOpen=false;colorTuningOpen=false;Render();await Task.Delay(380);root.UpdateLayout();
                    Check($"{theme} {width} {id} navigation retains the icon rail",navigation.IsPaneVisible&&navigation.PaneDisplayMode!=NavigationViewPaneDisplayMode.LeftMinimal&&navigation.MenuItems.OfType<NavigationViewItem>().Count()==6);
                    var text=DetailDescendants(page).OfType<TextBlock>().Where(t=>t.ActualWidth>0&&!t.FontFamily.Source.Contains("Icons",StringComparison.OrdinalIgnoreCase)).ToArray();
                    double[] fonts=text.Select(t=>t.FontSize).Distinct().Order().ToArray();
                    Check($"{theme} {width} {id} four text sizes and 12 DIP floor",fonts.Length<=4&&fonts.All(f=>f is 12 or 14 or 24 or 28)&&desktopSurface.ContentScale==1);
                    var outside=DetailDescendants(page).OfType<Control>().Where(c=>c is Button or ToggleSwitch or ComboBox or NumberBox).Where(c=>c.ActualWidth>0&&c.ActualHeight>0).Where(c=>{double x=c.TransformToVisual(page).TransformPoint(new Point()).X;return x<page.Padding.Left-2||x+c.ActualWidth>page.ActualWidth-page.Padding.Right+2;}).Select(c=>new{type=c.GetType().Name,name=Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(c)}).ToArray();
                    Check($"{theme} {width} {id} controls fit page width",outside.Length==0);
                    if(id=="settings")
                    {
                        var toggles=DetailDescendants(page).OfType<ToggleSwitch>().Where(t=>t.ActualWidth>0).ToArray();
                        double[] right= toggles.Select(t=>t.TransformToVisual(page).TransformPoint(new Point()).X+t.ActualWidth).ToArray();
                        Check($"{theme} {width} settings switches share a right edge",right.Length>=4&&right.Max()-right.Min()<1);
                        Check($"{theme} {width} takeover status no longer displaces switch",takeoverSwitch?.OnContent?.ToString()==""&&takeoverSwitch.OffContent?.ToString()=="");
                        var header=DetailDescendants(page.Children[0]).OfType<SymbolIcon>().SingleOrDefault();
                        Check($"{theme} {width} settings header matches navigation gear",header?.Symbol==Symbol.Setting);
                    }
                    samples.Add(new{theme=theme.ToString(),width,id,fonts,outside,contentWidth=page.ActualWidth,contentHeight=page.ActualHeight});
                    if(id=="settings"||theme==ElementTheme.Light&&width==840&&id is "overview" or "display" or "power" or "devices")await CaptureDesktopReview($"Design-{theme}-{id}-{width}");
                }
            }
            ToggleMini();
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            {
                root.RequestedTheme=theme;Render();await Task.Delay(350);root.UpdateLayout();
                Check($"{theme} mini preserves eight existing content groups",page.Children.Count==8&&!navigation.IsPaneVisible);
                var texts=DetailDescendants(page).OfType<TextBlock>().Where(t=>t.ActualWidth>0&&!t.FontFamily.Source.Contains("Icons",StringComparison.OrdinalIgnoreCase)).ToArray();
                Check($"{theme} mini font floor",texts.All(t=>t.FontSize>=12));
                var clipped=texts.Where(t=>t.TextWrapping==TextWrapping.NoWrap&&t.ActualWidth+1<t.DesiredSize.Width).Select(t=>t.Text).ToArray();
                var outside=DetailDescendants(page).OfType<Button>().Where(b=>b.ActualWidth>0).Where(b=>{double x=b.TransformToVisual(page).TransformPoint(new Point()).X;return x<0||x+b.ActualWidth>page.ActualWidth+1;}).Select(b=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b)).ToArray();
                Check($"{theme} mini controls fit unchanged width",outside.Length==0);
                Check($"{theme} mini content fits its existing viewport",page.ActualHeight<=scroll.ActualHeight+1);
                var quickText=DetailDescendants(page.Children[6]).OfType<TextBlock>().Where(t=>!t.FontFamily.Source.Contains("Icons",StringComparison.OrdinalIgnoreCase)).ToArray();
                Check($"{theme} mini quick action labels fit at 12 DIP",quickText.All(t=>{var probe=new TextBlock{Text=t.Text,FontFamily=t.FontFamily,FontSize=t.FontSize};probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return probe.DesiredSize.Width<=t.ActualWidth+1;}));
                samples.Add(new{theme=theme.ToString(),id="mini",fonts=texts.Select(t=>t.FontSize).Distinct().ToArray(),clipped,outside,contentHeight=page.ActualHeight,viewport=scroll.ActualHeight});
                await CaptureDesktopReview($"Design-{theme}-mini");
            }
            Check("Review did not establish a user tray",!trayAvailable);
            Check("Reduced motion follows the Windows animation preference",Design.MotionEnabled==new Windows.UI.ViewManagement.UISettings().AnimationsEnabled);
        }
        catch(Exception error){checks.Add(new{error=error.ToString(),passed=false});passed=false;}
        finally
        {
            exitRequested=true;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"design-review.json"),JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,fullUiSuiteRun=false,checks,samples},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=passed?0:1;
        }
    }
}
