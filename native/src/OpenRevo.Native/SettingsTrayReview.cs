using OpenRevo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Foundation;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunSettingsTrayReview()
    {
        bool restore=Environment.GetCommandLineArgs().Contains("--ui-theme-restore-checks");
        var checks=new List<object>();bool passed=true;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});passed&=ok;}
        try
        {
            Check("Review data is isolated from user preferences",AppData.IsIsolatedReviewData);
            if(restore)
            {
                Check("A fresh process restores saved dark theme",selectedTheme==2&&root.RequestedTheme==ElementTheme.Dark&&AppData.Load().Theme==2);
                currentPage="settings";Render();await Task.Delay(180);root.UpdateLayout();await CaptureDesktopReview("Settings-restarted-dark");return;
            }
            if(mini)ToggleMini();currentPage="settings";Render();await Task.Delay(200);root.UpdateLayout();
            Check("Original navigation symbols restored with only Display and Power replacements",navigation.MenuItems.OfType<NavigationViewItem>().All(i=>i.Tag?.ToString() is "display" or "power"?i.Icon is ImageIcon:i.Icon is SymbolIcon));
            Check("Removed process memory and control method sections",!DetailDescendants(page).OfType<TextBlock>().Any(t=>t.Text is "当前进程内存" or "控制方式"));
            foreach(string label in new[]{"应用主题","OSD 屏幕悬浮提示","开机自动启动（最小化至托盘）","解除离电限制","唤醒面板形态","迷你面板点击外部自动收起","数据存储管理","软件版本与分发更新","Windows 基础电源方案","导出诊断","后台与托盘"})
                Check("Settings contains "+label,DetailDescendants(page).OfType<TextBlock>().Any(t=>t.Text==label));
            foreach(var(theme,width,height)in new[]{(ElementTheme.Light,1180,900),(ElementTheme.Light,840,600),(ElementTheme.Dark,1180,900)})
            {
                root.RequestedTheme=theme;ResizeLogical(width,height);Render();await Task.Delay(180);root.UpdateLayout();
                var outside=DetailDescendants(page).OfType<Control>().Where(c=>c is Button or ToggleSwitch or ComboBox).Where(c=>c.ActualWidth>0).Where(c=>{double x=c.TransformToVisual(page).TransformPoint(new Point()).X;return x<page.Padding.Left-2||x+c.ActualWidth>page.ActualWidth-page.Padding.Right+2;}).ToArray();
                Check($"Settings controls remain inside page at {theme} {width}",outside.Length==0);await CaptureDesktopReview($"Settings-{theme}-{width}");
                scroll.ChangeView(null,scroll.ScrollableHeight,null,true);await Task.Delay(80);root.UpdateLayout();await CaptureDesktopReview($"Settings-{theme}-{width}-bottom");
            }
            currentPage="settings";Render();await Task.Delay(100);root.UpdateLayout();
            var developerChoice=DetailDescendants(page).OfType<ComboBox>().Single(c=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(c)=="机型形态预览");developerChoice.SelectedIndex=1;
            currentPage="overview";Render();await Task.Delay(100);root.UpdateLayout();
            checks.Add(new{developerShape,DeveloperThinPreview,metrics=overviewMetrics?.Children.Count,enabled=DetailDescendants(page).OfType<Button>().Where(b=>b.IsEnabled).Select(b=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b)).ToArray()});
            Check("Developer thin layout crops GPU and VRAM without enabling hardware",DeveloperThinPreview&&overviewMetrics?.Children.Count==2&&DetailDescendants(page).OfType<Button>().All(b=>!b.IsEnabled));
            developerShape="real";currentPage="settings";Render();await Task.Delay(100);root.UpdateLayout();
            var baseline=hardware.TuningState;bool active=hardware.Active;
            Check("Temporary tray icon registered",trayAvailable);
            WindowReviewMessage(captionWindow,0x10,0,0);await Task.Delay(160);
            Check("Native close request hides the panel",trayHidden&&!AppWindow.IsVisible&&!closed);
            Check("Hidden panel retains hardware session",hardware.Active==active&&ReferenceEquals(baseline,hardware.TuningState));
            WindowReviewMessage(captionWindow,TrayCallback,0,(nint)0x400);await Task.Delay(160);
            Check("Tray selection wakes the same instance",AppWindow.IsVisible&&!closed);
            if(mini)ToggleMini();WindowReviewMessage(captionWindow,0x112,0xF020,0);await Task.Delay(160);
            Check("Large-panel minimize shows mini instead of hiding to tray",mini&&AppWindow.IsVisible&&!trayHidden&&!closed);
            WindowReviewMessage(captionWindow,0x112,0xF060,0);await Task.Delay(160);
            Check("Close button hides the panel to tray",!AppWindow.IsVisible&&trayHidden&&!closed);
            WindowReviewMessage(captionWindow,WakeMessage,0,0);await Task.Delay(160);
            Check("Wake message restores the existing window",AppWindow.IsVisible&&!closed);
            await ChangeApplicationTheme(2);Check("User theme action persists dark theme",AppData.Load().Theme==2);
            root.RequestedTheme=ElementTheme.Light;await SavePreferences();Check("Background preference save does not overwrite selected theme",AppData.Load().Theme==2);
        }
        catch(Exception error){checks.Add(new{error=error.ToString(),passed=false});passed=false;}
        finally
        {
            exitRequested=true;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,restore?"theme-restore-review.json":"settings-tray-review.json"),JsonSerializer.Serialize(new{allPassed=passed,hardwareSettersInvoked=false,fullUiSuiteRun=false,isolatedData=AppData.DirectoryPath,checks},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;
        }
    }
}
