using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Foundation;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunCapsUiReview()
    {
        var checks=new List<object>();var samples=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        using var osd=new NativeOsd(DispatcherQueue,"Open-Revo Caps OSD · 专项预览",true);
        try
        {
            Check("Isolated review data",AppData.IsIsolatedReviewData);
            if(!AppData.IsIsolatedReviewData)throw new InvalidOperationException("Isolated data required.");
            foreach(bool initial in new[]{false,true})
            {
                var transition=new CapsLockTransitions(initial);
                Check($"{initial} baseline and unrelated key do not notify",transition.Locked==initial&&transition.Accept(0x41,true)==null&&transition.Locked==initial);
                Check($"{initial} down toggles once",transition.Accept(0x14,true)==!initial);
                Check($"{initial} held key does not repeat",transition.Accept(0x14,true)==null&&transition.Locked==!initial);
                Check($"{initial} release then press restores baseline",transition.Accept(0x14,false)==null&&transition.Accept(0x14,true)==initial);
            }
            if(mini)ToggleMini();
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(int width in new[]{1180,840})
            {
                root.RequestedTheme=theme;ResizeLogical(width,900);
                foreach(string id in new[]{"overview","display","power","settings"})
                {
                    currentPage=id;Render();await Task.Delay(250);root.UpdateLayout();
                    var badge=DetailDescendants(page.Children[0]).OfType<Border>().Single(b=>b.Child is IconElement);
                    var icon=(FrameworkElement)badge.Child;var pos=icon.TransformToVisual(badge).TransformPoint(new Point());
                    Check($"{theme} {width} {id} header 44 with centered 20 icon",Math.Abs(badge.ActualWidth-44)<.1&&Math.Abs(badge.ActualHeight-44)<.1&&Math.Abs(icon.ActualWidth-20)<.1&&Math.Abs(icon.ActualHeight-20)<.1&&Math.Abs(pos.X-12)<.1&&Math.Abs(pos.Y-12)<.1);
                    samples.Add(new{theme=theme.ToString(),width,id,badgeWidth=badge.ActualWidth,iconWidth=icon.ActualWidth,pos});
                    if(id=="settings")
                    {
                        var group=DetailDescendants(page).OfType<StackPanel>().Single(s=>Equals(s.Tag,"power-plan-controls"));
                        var controls=group.Children.OfType<Control>().ToArray();
                        double[] top=controls.Select(c=>c.TransformToVisual(group).TransformPoint(new Point()).Y).ToArray();
                        Check($"{theme} {width} power controls share 36 DIP height and edges",controls.Length==3&&controls.All(c=>Math.Abs(c.ActualHeight-36)<.1)&&top.Max()-top.Min()<.1&&controls.OfType<ComboBox>().Single().ActualWidth==180);
                        group.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=.5});await Task.Delay(100);root.UpdateLayout();
                        samples.Add(new{theme=theme.ToString(),width,controlHeights=controls.Select(c=>c.ActualHeight).ToArray(),top});
                    }
                    if(theme==ElementTheme.Light&&width==1180&&id!="overview"||theme==ElementTheme.Dark&&width==840&&id=="settings")await CaptureDesktopReview($"CapsUi-{theme}-{id}-{width}");
                }
            }
            foreach(string theme in new[]{"pill","fade","cinema"})
            foreach(string value in new[]{"1","0"})
            {
                osd.Configure(systemPreferences with{OsdTheme=theme,OsdScale=100,OsdPosition="top-left"});osd.ShowFeature("caps_lock",value);await Task.Delay(280);osd.Surface.UpdateLayout();
                Check($"{theme} Caps {value} wording original SVG and no clipping",osd.Message.Text==(value=="1"?"Caps已锁定":"Caps已解锁")&&osd.Message.Icon=="caps"&&osd.OriginalIconLoaded&&osd.IconFits&&osd.TextFits);
                double capsSize=osd.TextSize;osd.ShowFeature("power_mode","3");Check($"{theme} Caps shares existing OSD text size",osd.TextSize==capsSize);
                if(theme=="pill"){osd.ShowFeature("caps_lock",value);await Task.Delay(260);await CaptureDesktopReview("CapsUi-OSD-"+value,osd.Surface);}
            }
            if(File.Exists(Path.Combine(AppContext.BaseDirectory,"live-caps-review")))
            {
                bool initial=CapsLockObserver.ReadState();var events=new List<bool>();
                using var observer=new CapsLockObserver(locked=>DispatcherQueue.TryEnqueue(()=>{events.Add(locked);osd.ShowFeature("caps_lock",locked?"1":"0");}));
                Check("Live observer installed",observer.Installed);
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"caps-live-ready.json"),JsonSerializer.Serialize(new{initial,observer.Installed}));
                var deadline=DateTimeOffset.Now.AddSeconds(55);
                while(events.Count<2&&DateTimeOffset.Now<deadline)await Task.Delay(100);
                await Task.Delay(200);
                Check("Live Caps roundtrip reports both states and restores baseline",events.Count==2&&events[0]==!initial&&events[1]==initial&&CapsLockObserver.ReadState()==initial);
                samples.Add(new{initial,liveEvents=events.ToArray(),restored=CapsLockObserver.ReadState()==initial});
                observer.Dispose();Check("Observer unhooked at exit",!observer.Installed);
            }
        }
        catch(Exception error){checks.Add(new{passed=false,error=error.ToString()});passed=false;}
        finally
        {
            exitRequested=true;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"caps-ui-review.json"),JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,fullUiSuiteRun=false,checks,samples},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;
        }
    }
}
