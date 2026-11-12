using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json.Nodes;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task CaptureApprovedSurface(string name)
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"layout-review");Directory.CreateDirectory(folder);
        var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();await bitmap.RenderAsync(root);
        byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();string path=Path.Combine(folder,name+".png");await File.WriteAllBytesAsync(path,[]);
        var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(path);using var stream=await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
    }
    // Explicit developer command. Runs before device discovery or hardware takeover.
    private async Task RunApprovedUiReview()
    {
        var results=new List<object>();bool passed=true;
        void Check(string name,bool ok){results.Add(new{name,passed=ok});passed&=ok;}
        async Task Settle(){await Task.Delay(180);root.UpdateLayout();}
        async Task Invoke(string name){var button=DetailDescendants(page).OfType<Button>().First(b=>AutomationProperties.GetName(b)==name);((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();await Settle();}
        try
        {
            if(!AppData.IsIsolatedReviewData)throw new IOException("需要隔离的审阅数据目录。");
            Check("No hardware controller acquired",!hardware.Active&&upstream==null);
            // Synthetic layout values exist only within this explicit isolated review.
            var fans=new FanProtocolState(new int[16],new int[16],new int[16],new int[16],new int[16],new int[16],4,129,16,0,65,128,10);
            tuningBaseline=new(new(fans,0,0),new(65,90,140,10,2,0,25,0,115,135,185,10,35,55,90,65,90,140),new(new("Review fixture GPU",true,true,60,115,25,250,1000),new(0,0)),100);
            tuningDraft=tuningBaseline.CreateDraft();tuningStatus="隔离布局样本；未连接硬件。";
            colorDraft=ColorPreset.Parse("{\"id\":\"layout-fixture\",\"name\":\"隔离布局样本\",\"color_temp_k\":6500,\"saturation\":1,\"contrast\":1,\"gamma\":1,\"gain_r\":1,\"gain_g\":1,\"gain_b\":1}");
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(int width in new[]{1180,840})
            {
                root.RequestedTheme=theme;ResizeLogical(width,900);
                foreach(string route in new[]{"overview","display","power","sleep","devices","plugins","settings","osdsettings","storage","diagnostics","lightingzone","tuning","color"})
                {
                    currentPage=route is "tuning" or "color"?"display":route=="lightingzone"?"devices":route;customTuningOpen=route=="tuning";colorTuningOpen=route=="color";selectedLightingZone=route=="lightingzone"?"front":"keyboard";Render();await Settle();
                    var texts=DetailDescendants(page).OfType<TextBlock>().Where(t=>!t.FontFamily.Source.Contains("Icons")).ToArray();
                    Check($"{theme} {width} {route} typography",texts.All(t=>t.FontSize is 12 or 14 or 16 or 24));
                    Check($"{theme} {width} {route} native content",page.Children.Count>1&&page.ActualWidth>0);
                    if(width==1180)await CaptureApprovedSurface($"Approved-{theme}-{route}-{width}");
                }
            }
            customTuningOpen=false;colorTuningOpen=false;selectedLightingZone="keyboard";
            var testSwitch=new ToggleSwitch{IsOn=false};page.Children.Add(testSwitch);await Settle();
            var peer=new ToggleSwitchAutomationPeer(testSwitch);var toggle=(IToggleProvider)peer.GetPattern(PatternInterface.Toggle);toggle.Toggle();await Settle();Check("Restyled native switch supports UIA input",testSwitch.IsOn&&toggle.ToggleState==ToggleState.On);toggle.Toggle();await Settle();Check("Restyled switch returns to off",!testSwitch.IsOn);
            currentPage="overview";Render();await Settle();
            Check("Nine shortcut rows",quickButtons.Count==5&&extraSwitchButtons.Count==3&&overviewBoost!=null);
            var original=overviewSaved.ToArray();await Invoke("编辑磁贴");
            Check("Edit entry replaced by two icon actions",!DetailDescendants(page).OfType<Button>().Any(b=>AutomationProperties.GetName(b)=="编辑磁贴")&&DetailDescendants(page).OfType<Button>().Count(b=>AutomationProperties.GetName(b) is "保存磁贴布局" or "取消编辑磁贴")==2);
            await CaptureApprovedSurface("Approved-editing");
            overviewDraft!.Remove("battery");await Invoke("取消编辑磁贴");Check("Cancel restores saved order",overviewSaved.SequenceEqual(original)&&overviewDraft==null);
            await Invoke("编辑磁贴");overviewDraft!.Clear();Render();await Settle();Check("Empty layout still allows add",DetailDescendants(page).OfType<Button>().Any(b=>AutomationProperties.GetName(b).Contains("添加面板")));await Invoke("取消编辑磁贴");
            customTuningOpen=true;currentPage="display";Render();await Settle();Check("Tuning keeps explicit editor boundary",!hardware.Active);customTuningOpen=false;colorTuningOpen=true;Render();await Settle();Check("Color editor renders without hardware writes",upstream==null);colorTuningOpen=false;
            if(!mini)ToggleMini();await panelShapeCompletion;await Settle();await CaptureApprovedSurface("Approved-mini");Check("Mini retains four performance buttons",performanceButtons.Count==4);Check("Review stays read only",!hardware.Active&&upstream==null&&!trayAvailable);
        }
        catch(Exception error){results.Add(new{passed=false,error=error.ToString()});passed=false;}
        finally
        {
            exitRequested=true;await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"approved-ui-review.json"),JsonSerializer.Serialize(new{passed,hardwareWrites=false,physicalDpiMatrixVerified=false,syntheticLayoutFixture=true,browserPixelComparisonVerified=false,results},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;
        }
    }
}
