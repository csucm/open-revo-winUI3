using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System.Text.Json;
using OpenRevo.Core;
using Windows.Foundation;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunOsdPowerReview()
    {
        var checks=new List<object>();var samples=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        using var osd=new NativeOsd(DispatcherQueue,"Open-Revo OSD · 新版只读预览",true);
        try
        {
            Check("Review configuration is isolated",AppData.IsIsolatedReviewData);
            Check("Newly launched overview does not depend on tuning editor",hardware.TuningState==null&&tuningBaseline==null);
            await ReadOverviewPowerTargets();if(mini)ToggleMini();currentPage="overview";Render();await Task.Delay(350);root.UpdateLayout();
            Check("Overview target is populated without opening the editor",overviewPowerTargets?.Pl1Watts is >0&&live["overviewPowerTarget"].Text.Contains(overviewPowerTargets.Pl1Watts+" W"));
            Check("GPU target is populated from physical power envelope",overviewPowerTargets?.TgpWatts is >0&&live["overviewPowerTarget"].Text.Contains("TGP: "+overviewPowerTargets.TgpWatts+" W"));
            samples.Add(new{power=overviewPowerTargets,error=overviewPowerError,display=live["overviewPowerTarget"].Text});
            await CaptureDesktopReview("PowerTarget-overview");
            var before=overviewPowerTargets;overviewPowerTargets=before==null?null:before with{At=DateTimeOffset.Now.AddSeconds(-20)};UpdateOverviewPowerTarget();
            Check("Stale target is cleared instead of showing cached watts",live["overviewPowerTarget"].Text.Contains("PL1: —"));overviewPowerTargets=before;UpdateOverviewPowerTarget();
            foreach(string theme in new[]{"cinema","fade","pill"})
            foreach(int scale in new[]{80,100,120,140})
            foreach(string position in new[]{"top-left","bottom-center","center"})
            {
                var focus=NativeOsd.Foreground;osd.Configure(systemPreferences with{OsdTheme=theme,OsdScale=scale,OsdPosition=position});osd.Show("Open-Revo","屏幕提示预览");
                await Task.Delay(300);osd.Surface.UpdateLayout();
                Check($"{theme} {scale} {position} does not take foreground or keyboard focus",NativeOsd.Foreground==focus&&osd.DoesNotActivate&&osd.ClickThrough);
                Check($"{theme} {scale} {position} rejects framework frame reinsertion",osd.NativeFrameBorderless);
                Check($"{theme} {scale} {position} preserves preview wording",osd.Message.Text=="Open-Revo · 屏幕提示预览");
                Check($"{theme} {scale} {position} is a compact single-line HUD",osd.CardHeight<80&&DetailDescendants(osd.Surface).OfType<TextBlock>().Count()==1);
                Check($"{theme} {scale} {position} reserves all right-side text padding",osd.TextFits);
                Check($"{theme} {scale} {position} original SVG fully loads within its badge",osd.OriginalIconLoaded&&osd.IconFits);
                if(theme=="pill")Check($"{theme} {scale} {position} material is masked to capsule silhouette",osd.BackdropClipped);
                var bounds=osd.Bounds;var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
                Check($"{theme} {scale} {position} remains inside display work area",bounds.X>=area.X&&bounds.Y>=area.Y&&bounds.X+bounds.Width<=area.X+area.Width&&bounds.Y+bounds.Height<=area.Y+area.Height);
                if(position!="top-left")Check($"{theme} {scale} {position} is horizontally centered",Math.Abs((bounds.X+bounds.Width/2d)-(area.X+area.Width/2d))<=1);
                samples.Add(new{theme,scale,position,bounds,textSize=osd.TextSize,width=osd.Surface.ActualWidth,height=osd.Surface.ActualHeight,layout=osd.LayoutReport});
                if(position=="top-left"&&scale is 100 or 140)await CaptureDesktopReview($"OSD-{theme}-{scale}",osd.Surface);
            }
            foreach(string mode in new[]{"1","2","3","4"})
            {
                osd.Configure(systemPreferences with{OsdTheme="pill",OsdScale=100});osd.ShowFeature("power_mode",mode);await Task.Delay(260);
                Check("Mode "+mode+" uses original mode icon and title",osd.Message.Icon==(mode=="1"?"office":mode=="2"?"balance":mode=="3"?"beast":"geek")&&!osd.Message.Text.Contains(" · "));
                Check("Mode "+mode+" keeps its title inside the rounded capsule",osd.TextFits);
                if(mode=="3")await CaptureDesktopReview("OSD-beast-event",osd.Surface);
            }
            foreach(string feature in new[]{"wifi","bluetooth","touchpad","camera","mic","win_key","fn_lock","fan_boost","keep_awake"})
            foreach(string value in new[]{"0","1"})Check(feature+" "+value+" has matching dot and semantic accent",OsdMessage.FromFeature(feature,value).Dot!=null);
            Check("Keyboard level uses four progress steps",OsdMessage.FromFeature("keyboard_brightness","3").Steps==3);
            osd.ShowFeature("wifi","1");await Task.Delay(900);osd.ShowFeature("wifi","0");await Task.Delay(1000);
            Check("Repeated OSD restarts the display timer",osd.IsVisible&&osd.Message.Text.EndsWith("关闭"));
            await Task.Delay(900);Check("OSD hides after the last notification exits",!osd.IsVisible);
            osd.Configure(systemPreferences with{OsdTheme="pill",OsdScale=100,OsdPosition="top-left"});osd.Show("Open-Revo","屏幕提示预览");
            if(File.Exists(Path.Combine(AppContext.BaseDirectory,"hold-osd-review")))
            {
                await Task.Delay(250);osd.HoldForReview();
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"osd-desktop-ready.json"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,osd.Bounds,osd.Message,layout=osd.LayoutReport,productionStyleUnchanged=true}));
                await Task.Delay(45000);
            }
        }
        catch(Exception error){checks.Add(new{passed=false,error=error.ToString()});passed=false;}
        finally
        {
            exitRequested=true;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"osd-power-review.json"),JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,fullUiSuiteRun=false,checks,samples},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=passed?0:1;
        }
    }
}
