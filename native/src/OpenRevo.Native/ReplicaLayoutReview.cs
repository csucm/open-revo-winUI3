using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Foundation;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunReplicaLayoutReview()
    {
        var samples=new List<object>();bool passed=true;
        try
        {
            if(mini)ToggleMini();root.RequestedTheme=ElementTheme.Light;
            await ReadDesktopServices(true);while(desktopServicesReading)await Task.Delay(30);
            foreach(var(width,height)in new[]{(1180,900),(840,600)})
            {
                ResizeLogical(width,height);
                foreach(string view in new[]{"display","color","tuning"})
                {
                    currentPage="display";customTuningOpen=view=="tuning";colorTuningOpen=view=="color";Render();
                    while(tuningLoading||desktopServicesReading)await Task.Delay(30);
                    await Task.Delay(220);root.UpdateLayout();await Task.Delay(60);root.UpdateLayout();
                    var outside=DetailDescendants(page).OfType<Control>().Where(c=>c is Button or ToggleSwitch or NumberBox or Slider or ComboBox).Where(c=>c.ActualWidth>0&&c.ActualHeight>0).Select(c=>new{name=Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(c),x=c.TransformToVisual(page).TransformPoint(new Point()).X,width=c.ActualWidth}).Where(c=>c.x<page.Padding.Left-2||c.x+c.width>page.ActualWidth-page.Padding.Right+2).ToArray();
                    bool present=view switch{"tuning"=>tuningDraft!=null&&tuningNumbers.Count>=5&&DetailDescendants(page).OfType<FanCurveEditor>().Count()==2&&tuningPresets.Count>=4,"color"=>colorNumbers.Count==7&&colorPresetButtons.Count==6,_=>gpuButtons.Count==3&&brightnessButtons.Count==3};
                    bool ok=present&&outside.Length==0;passed&=ok;samples.Add(new{view,width,height,present,outside,passed=ok});
                    await CaptureDesktopReview($"Replica-{view}-{width}");
                    if(view=="tuning")
                    {
                        if(width==1180&&tuningDraft!=null)
                        {
                            var savedDraft=tuningDraft.Copy();string? savedId=tuningPresetId;var hardwareBefore=hardware.TuningState;
                            var choice=DetailDescendants(page).OfType<ComboBox>().Single();choice.SelectedItem=tuningPresets.Single(p=>p.Id=="preset_quiet");root.UpdateLayout();
                            bool selectionOnlyChangesDraft=tuningPresetId=="preset_quiet"&&tuningDraft.Pl1==tuningBaseline!.Registers.OfficePl1&&ReferenceEquals(hardwareBefore,hardware.TuningState);
                            passed&=selectionOnlyChangesDraft;samples.Add(new{selectionOnlyChangesDraft});await CaptureDesktopReview("Replica-preset-quiet");
                            int initial=tuningDraft.Pl1;var number=tuningNumbers[0];var slider=DetailDescendants(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(number.Parent)).OfType<Slider>().Single();
                            slider.Value=initial>15?initial-1:initial+1;bool sliderBindsDraft=number.Value==slider.Value&&tuningDraft.Pl1==(int)slider.Value&&ReferenceEquals(hardwareBefore,hardware.TuningState);
                            number.Value=initial;bool numberBindsSlider=slider.Value==initial;passed&=sliderBindsDraft&&numberBindsSlider;samples.Add(new{sliderBindsDraft,numberBindsSlider});
                            tuningDraft=savedDraft;tuningPresetId=savedId;RefreshTuningPanel();root.UpdateLayout();
                        }
                        var fan=DetailDescendants(page).OfType<FrameworkElement>().FirstOrDefault(c=>c is TextBlock{Text:"风扇曲线与响应"});
                        fan?.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=0});await Task.Delay(90);root.UpdateLayout();await CaptureDesktopReview($"Replica-fans-{width}");
                    }
                }
            }
            root.RequestedTheme=ElementTheme.Dark;ResizeLogical(1180,900);currentPage="display";customTuningOpen=false;colorTuningOpen=false;Render();await Task.Delay(150);root.UpdateLayout();await CaptureDesktopReview("Replica-display-dark");
        }
        catch(Exception error){passed=false;samples.Add(new{error=error.ToString()});}
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"replica-layout-review.json"),JsonSerializer.Serialize(new{allPassed=passed,fullUiSuiteRun=false,hardwareSettersInvoked=false,samples},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;
        }
    }
}
