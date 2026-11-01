using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    // In-process WinUI checks. Default is read-only; explicit per-device live
    // flags may write and must restore their baseline. No OS input injection.
    private async Task RunUiReviewChecks()
    {
        var results=new List<object>();var skipped=new List<object>();bool passed=true,hardwareSettersInvoked=false;
        void Check(string name,bool success){results.Add(new{name,passed=success});passed&=success;}
        static IEnumerable<DependencyObject> Tree(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Tree(child))yield return descendant;}
        }
        int? originalBrightness=screenBrightness;var originalTheme=root.RequestedTheme;
        try
        {
            if(!mini)ToggleMini();root.UpdateLayout();
            await RunMiniCaptionChecks(Check);
            RunMiniRefreshChecks(Check);
            Check("Preset color menu contains eight colors and no custom item",lightingColorBox?.Items.Count==8&&lightingColorBox.Items.OfType<ComboBoxItem>().All(i=>i.Tag?.ToString()!="custom"));
            foreach(int target in new[]{25,60,100})
            {
                screenBrightness=target;UpdateDisplayValues();
                Check($"Confirmed brightness {target}% synchronizes slider, text and preset",brightnessSlider?.Value==target&&live["brightnessTitle"].Text.Contains(target.ToString())&&brightnessButtons[target].Style==(Style)Application.Current.Resources["SelectedChoiceStyle"]);
            }
            screenBrightness=originalBrightness;UpdateDisplayValues();
            if(Environment.GetCommandLineArgs().Contains("--ui-gpu-checks"))
            {hardwareSettersInvoked=true;await RunLiveGpuChecks(Check);}
            if(displayState?.Active==true)await RunBrightnessInteractionChecks(Check);
            else skipped.Add(new{name="Brightness interaction timing",reason="Physical BOE panel is inactive; its controls remain disabled and no display was activated for this review."});
            if(Environment.GetCommandLineArgs().Contains("--ui-brightness-checks"))
            {hardwareSettersInvoked=true;await RunLiveBrightnessChecks(Check);}
            var selected=performanceButtons.Values.FirstOrDefault(b=>b.Style==(Style)Application.Current.Resources["SelectedChoiceStyle"])??throw new IOException("No selected performance button");
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            {
                root.RequestedTheme=theme;await Task.Delay(80);selected.ApplyTemplate();root.UpdateLayout();
                var surface=Tree(selected).OfType<Border>().Single(b=>b.Name=="ChoiceSurface");
                var expected=((SolidColorBrush)surface.BorderBrush).Color;
                foreach(string state in new[]{"PointerOver","Pressed","Normal"})
                {
                    bool found=VisualStateManager.GoToState(selected,state,false);root.UpdateLayout();
                    Check($"{theme} selected border survives {state}",found&&surface.BorderThickness.Left==1&&surface.BorderBrush is SolidColorBrush brush&&brush.Color==expected&&brush.Color.A>0);
                }
            }
            root.RequestedTheme=originalTheme;
            if(Environment.GetCommandLineArgs().Contains("--ui-camera-checks"))
            {
                bool admin=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if(admin||upstream==null||upstream.IsCameraAccessBlocked()||OpenRevo.Core.CameraControl.Read()?.Enabled!=true)throw new IOException("Camera UI check requires normal token and already-restored Windows access.");
                bool beforePowered=hardware.ProbeSwitches().CameraPowered;
                try
                {
                    hardwareSettersInvoked=true;
                    var on=await ToggleCameraThroughCore();var off=await ToggleCameraThroughCore();
                    Check("Actual camera button workflow roundtrips without admin token",on.Camera!=off.Camera&&off.Camera==beforePowered);
                }
                finally
                {
                    if(hardware.Active&&hardware.ProbeSwitches().CameraPowered!=beforePowered)
                        await Task.Run(()=>hardware.SetCameraPoweredViaCore(upstream,beforePowered));
                }
            }
            await RunWinQueueChecks(Check);
            if(Environment.GetCommandLineArgs().Contains("--ui-win-checks"))
            {hardwareSettersInvoked=true;await RunLiveWinQueueChecks(Check);}
            var savedQuick=quickState;quickState=quickState with{WinLocked=true};UpdateQuickActions();root.UpdateLayout();
            Check("Locked Win key uses the short label",quickButtons["win"].Tag?.ToString()=="已锁定");
            var selection=(Style)Application.Current.Resources["SelectedChoiceStyle"];
            quickState=quickState with{Microphone=true,WinLocked=false,Camera=true};UpdateQuickActions();
            Check("Microphone and camera availability are bright while the unlocked Win lock is off",new[]{"mic","camera"}.All(key=>quickButtons[key].Style==selection)&&quickButtons["win"].Style!=selection&&quickButtons["win"].Tag?.ToString()=="已解锁");
            quickState=quickState with{Microphone=false,WinLocked=true,Camera=false};UpdateQuickActions();
            Check("Win lock alone is bright when locked while muted microphone and closed camera stay off",new[]{"mic","camera"}.All(key=>quickButtons[key].Style!=selection)&&quickButtons["win"].Style==selection);
            quickState=quickState with{Microphone=null,WinLocked=null};UpdateQuickActions();
            Check("Unknown microphone and Win key states are not presented as enabled",new[]{"mic","win"}.All(key=>quickButtons[key].Style!=selection&&!quickButtons[key].IsEnabled));
            quickState=savedQuick;UpdateQuickActions();
            root.UpdateLayout();
            var custom=performanceButtons[4];var customContent=custom.Content as StackPanel;
            Check("Mini custom button shows its text within the fixed button height",customContent?.Children.OfType<TextBlock>().SingleOrDefault()?.Text=="自定义"&&customContent.ActualHeight>0&&customContent.DesiredSize.Height<=custom.ActualHeight-custom.Padding.Top-custom.Padding.Bottom);
            Check("Hardware takeover navigation is removed",navigation.MenuItems.OfType<NavigationViewItem>().All(i=>i.Tag?.ToString()!="hardware"));
            currentPage="settings";if(mini)ToggleMini();else Render();
            Check("Settings contains the takeover switch",takeoverSwitch!=null&&takeoverSwitch.IsOn==hardware.Active);
            if(takeoverSwitch!=null&&hardware.Active)
            {
                takeoverSwitch.IsOn=false;
                Check("Takeover off releases and cancels automatic retry",!hardware.Active&&!automaticTakeoverPending);
                takeoverSwitch.IsOn=true;
                for(int i=0;i<100&&takeoverChanging;i++)await Task.Delay(100);
                Check("Takeover on reacquires through fresh read",hardware.Active&&takeoverSwitch.IsOn&&!takeoverChanging);
            }
            int? modeBeforeCustomNavigation=hardware.CurrentMode;
            OpenTuning();
            for(int i=0;i<100&&tuningLoading;i++)await Task.Delay(100);
            root.UpdateLayout();
            Check("Mini custom navigates to the merged Display tuning subpage without changing performance mode",!mini&&currentPage=="display"&&tuningPanel!=null&&hardware.CurrentMode==modeBeforeCustomNavigation);
            Check("Custom subpage opens combined editors with the shared baseline",customTuningOpen&&tuningBaseline!=null&&tuningDraft!=null&&tuningNumbers.Count>=5&&DetailDescendants(page).OfType<FanCurveEditor>().Count()==2);
            if(tuningDraft!=null&&tuningNumbers.Count>0)
            {
                int before=tuningDraft.Pl1;int edited=before==15?16:before-1;
                tuningNumbers[0].Value=edited;
                Check("CPU editor updates only the draft",tuningDraft.Pl1==edited&&hardware.TuningState==tuningBaseline&&hardware.CurrentMode==tuningBaseline?.Performance.Mode);
            }
            await RunDesktopReviewChecks(Check);
        }
        catch(Exception error){passed=false;results.Add(new{name="runtime",passed=false,error=error.ToString()});}
        finally
        {
            screenBrightness=originalBrightness;root.RequestedTheme=originalTheme;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ui-review.json"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,version=typeof(App).Assembly.GetName().Version?.ToString(3),administratorToken=OpenRevo.Core.Hal.GpuModeControl.IsAdministrator(),allPassed=passed,hardwareSettersInvoked,display=displayState,gpuRuntime,gpuBoot,gpuUiTimings,winUiTimings,tests=results,skipped},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
}
