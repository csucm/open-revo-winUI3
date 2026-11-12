using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Foundation;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static IEnumerable<DependencyObject> DetailDescendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in DetailDescendants(child))yield return nested;}
    }
    private async Task RunDetailReplicaChecks(Action<string,bool> check,bool capture)
    {
        var bounds=new Windows.Graphics.RectInt32(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
        bool pane=navigation.IsPaneOpen;var theme=root.RequestedTheme;var samples=new List<object>();
        async Task Measure(string name)
        {
            await Task.Delay(350);root.UpdateLayout();await Task.Delay(80);root.UpdateLayout();
            var origin=page.TransformToVisual(scroll).TransformPoint(new Point());
            var outside=DetailDescendants(page).OfType<Control>().Where(c=>c is Button or ToggleSwitch or NumberBox or Slider or ComboBox).Where(c=>c.ActualWidth>0&&c.ActualHeight>0).Select(c=>new{control=c.GetType().Name,name=Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(c),position=c.TransformToVisual(page).TransformPoint(new Point()),width=c.ActualWidth}).Where(c=>c.position.X<page.Padding.Left-2||c.position.X+c.width>page.ActualWidth-page.Padding.Right+2).ToArray();
            bool centered=page.ActualWidth*desktopSurface.ContentScale<=scroll.ActualWidth+1&&Math.Abs(origin.X-(scroll.ActualWidth-page.ActualWidth*desktopSurface.ContentScale)/2)<=2;
            check(name+" stays centered with all visible controls inside the page",centered&&outside.Length==0);
            if(currentPage=="display"&&!customTuningOpen&&!colorTuningOpen&&gpuButtons.Count==3)
            {
                var heights=gpuButtons.Values.Select(b=>b.ActualHeight).ToArray();
                check(name+" GPU cards share the same rendered height",heights.Min()>=175&&heights.Max()-heights.Min()<=1);
                check(name+" GPU cards stretch inside the shared row",gpuButtons.Values.All(b=>b.VerticalAlignment==VerticalAlignment.Stretch&&b.VerticalContentAlignment==VerticalAlignment.Stretch));
            }
            AuditDesktopAlignment(name,check);
            AuditFunctionOwnership(name,check);
            samples.Add(new{name,viewport=scroll.ActualWidth,pageWidth=page.ActualWidth,left=origin.X,centered,outside});
            if(capture)await CaptureDesktopReview(name);
        }
        try
        {
            foreach(var mode in new[]{ElementTheme.Light,ElementTheme.Dark})
            {
                root.RequestedTheme=mode;
                foreach(var(width,open)in new[]{(1440,false),(850,true)})
                {
                    ResizeLogical(width,960);navigation.IsPaneOpen=open;
                    foreach(string id in new[]{"overview","display","power","devices","plugins","settings"})
                    {
                        currentPage=id;customTuningOpen=false;Render();
                        if(id=="power")check($"{mode} {width} all power service bindings and battery presets are retained",desktopPowerSwitches.Count==11&&compactChargeButtons.Count==3&&desktopBatteryFill!=null);
                        if(id=="devices")check($"{mode} {width} lighting effects and palette use actual capability without a duplicate off effect",unavailableLightingTabs.All(b=>!b.IsEnabled)&&!desktopEffects.ContainsKey("关闭背光")&&(keyboard.Kind!="SingleZone"||desktopEffects.Count==2&&desktopColors.Count==8));
                        await Measure($"Replica-{mode}-{id}-{width}");
                        if(id=="display")
                        {
                            var icc=page.Children.OfType<Border>().Single(card=>DetailDescendants(card).OfType<TextBlock>().Any(text=>text.Text=="ICC 色彩校准"));
                            icc.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=.7});
                            await Measure($"Alignment-{mode}-display-color-{width}");
                        }
                        if(id=="devices")
                        {
                            var colorButton=DetailDescendants(page).OfType<Button>().SingleOrDefault(b=>b.Flyout is Flyout{Content:ColorPicker});
                            if(colorButton?.Flyout is Flyout colors)
                            {
                                colorButton.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=0});colors.ShowAt(colorButton);
                                await Measure($"Replica-{mode}-color-picker-{width}");colors.Hide();
                            }
                        }
                    }
                    OpenTuning();for(int i=0;i<80&&tuningLoading;i++)await Task.Delay(50);
                    for(int tab=0;tab<1;tab++)
                    {
                        RefreshTuningPanel();await Measure($"Replica-{mode}-custom-{tab}-{width}");
                        if(tab==0)
                        {
                            var customFans=DetailDescendants(page).OfType<ToggleSwitch>().Single(s=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(s)=="自定义风扇曲线");
                            bool savedFanEditing=customFans.IsOn;var hardwareBefore=hardware.TuningState;customFans.IsOn=true;
                            check($"{mode} {width} enabling fan editing only changes the draft",tuningDraft?.CustomFans==true&&ReferenceEquals(hardwareBefore,hardware.TuningState));
                            var nodes=DetailDescendants(page).OfType<FanCurveEditor>().ToArray();
                            check($"{mode} {width} fan curves expose visual node editors",nodes.Length==2&&nodes.All(node=>DetailDescendants(node).OfType<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>().Count()==16));
                            nodes.First().StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=0});
                            await Measure($"Replica-{mode}-fan-nodes-{width}");customFans.IsOn=savedFanEditing;
                        }
                    }
                }
            }
            if(AppWindow.Presenter is OverlappedPresenter presenter)
            {
                root.RequestedTheme=ElementTheme.Light;navigation.IsPaneOpen=false;presenter.Maximize();
                foreach(string id in new[]{"overview","display","power","devices","plugins","settings"})
                {currentPage=id;customTuningOpen=false;Render();await Measure($"Replica-Light-{id}-maximized");}
                OpenTuning();for(int tab=0;tab<1;tab++){RefreshTuningPanel();await Measure($"Replica-Light-custom-{tab}-maximized");}presenter.Restore();
            }
            RefreshTuningPanel();root.UpdateLayout();
            int saved=tuningDraft!.Pl1;var baseline=hardware.TuningState;
            var first=tuningNumbers[0];
            var row=(Grid)first.Parent;
            var slider=DetailDescendants((Microsoft.UI.Xaml.DependencyObject)row.Parent).OfType<Slider>().Single();
            int next=saved>15?saved-1:saved+1;slider.Value=next;
            check("CPU slider and number field update the same draft without a hardware apply",first.Value==next&&tuningDraft.Pl1==next&&ReferenceEquals(baseline,hardware.TuningState));
            first.Value=saved;check("Numeric edits synchronize the tuning slider",slider.Value==saved&&tuningDraft.Pl1==saved);
        }
        finally
        {
            AppWindow.MoveAndResize(bounds);navigation.IsPaneOpen=pane;root.RequestedTheme=theme;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"detail-replica-layout.json"),System.Text.Json.JsonSerializer.Serialize(samples,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
    }
}
