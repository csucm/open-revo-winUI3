using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunPaintedUiReview()
    {
        bool passed=true;var results=new List<object>();
        void Check(string name,bool ok,object? detail=null){passed&=ok;results.Add(new{name,passed=ok,detail});}
        async Task Settle(){await Task.Delay(260);root.UpdateLayout();}
        try
        {
            if(!AppData.IsIsolatedReviewData)throw new IOException("自绘审阅需要隔离数据目录。");Check("No hardware takeover",!hardware.Active&&upstream==null);
            await Task.Delay(350);
            AppWindow.Hide();
            await Task.Delay(150);
            try
            {
                using var osd=new NativeOsd(DispatcherQueue);
                var foreground=NativeOsd.Foreground;
                foreach(string theme in new[]{"cinema","fade","pill"})
                {
                    osd.Configure(systemPreferences with{OsdTheme=theme});
                    osd.ShowFeature("caps_lock","1");
                    Check(theme+" starts with an invisible native window",osd.WindowOpacity==0);
                    await Task.Delay(320);
                    Check(theme+" shows a clipped translucent OSD while the main window is hidden",
                        !AppWindow.IsVisible&&osd.IsVisible&&osd.DoesNotActivate&&NativeOsd.Foreground==foreground&&
                        osd.CapsuleRegionApplied&&osd.WindowTranslucencyApplied&&osd.Message.Text=="Caps已锁定");
                    osd.ShowFeature("caps_lock","0");
                    await Task.Delay(280);
                    Check(theme+" replaces the visible message without clipping text or icon",
                        osd.Message.Text=="Caps已解锁"&&osd.TextFits&&osd.IconFits);
                    bool backingStayedPainted=true;
                    byte previous=osd.WindowOpacity;
                    bool nativeFaded=false;
                    var timeout=DateTimeOffset.UtcNow.AddMilliseconds(2100);
                    while(osd.IsVisible&&DateTimeOffset.UtcNow<timeout)
                    {
                        await Task.Delay(16);
                        backingStayedPainted&=Math.Abs(osd.CardOpacity-1)<.001;
                        byte alpha=osd.WindowOpacity;
                        nativeFaded|=alpha<previous;
                        previous=alpha;
                    }
                    Check(theme+" exits by fading the HWND with an opaque painted card",
                        backingStayedPainted&&nativeFaded&&!osd.IsVisible&&osd.WindowOpacity==0);
                }
            }
            finally{AppWindow.Show(false);}
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"painted-osd-review.json"),JsonSerializer.Serialize(new{passed,syntheticCapsEvents=true,results}));
            var fans=new FanProtocolState(new int[16],new int[16],new int[16],new int[16],new int[16],new int[16],4,129,16,0,65,128,10);tuningBaseline=new(new(fans,0,0),new(65,90,140,10,2,0,25,0,115,135,185,10,35,55,90,65,90,140),new(new("Isolated layout fixture",true,true,60,115,25,250,1000),new(0,0)),100);tuningDraft=tuningBaseline.CreateDraft();tuningStatus="隔离布局样本，未连接硬件。";
            colorDraft=ColorPreset.Parse("{\"id\":\"layout-fixture\",\"name\":\"隔离布局样本\",\"color_temp_k\":6500,\"saturation\":1,\"contrast\":1,\"gamma\":1,\"gain_r\":1,\"gain_g\":1,\"gain_b\":1}");
            root.RequestedTheme=ElementTheme.Light;PaintNavigate("overview");await Settle();
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(string speed in new[]{"fast","normal","slow"})
            {
                root.RequestedTheme=theme;MotionDesign.Configure(speed);await Settle();
                foreach(bool targetMini in new[]{true,false})
                {
                    if(mini==targetMini)continue;
                    bool visible=true,covered=true;int samples=0;
                    ToggleMini();
                    do
                    {
                        root.UpdateLayout();
                        GetPanelCloak(captionWindow,14,out int cloaked,4);
                        GetPanelClientRect(captionWindow,out var client);
                        double dpi=GetDpiForWindow(captionWindow)/96d;
                        visible&=AppWindow.IsVisible&&cloaked==0;
                        covered&=windowSurface.Background!=null&&
                            windowSurface.ActualWidth*dpi>=client.Right-client.Left-1&&
                            windowSurface.ActualHeight*dpi>=client.Bottom-client.Top-1;
                        samples++;await Task.Delay(16);
                    }while(!panelShapeCompletion.IsCompleted);
                    await panelShapeCompletion;
                    Check($"{theme} {speed} to {(targetMini?"mini":"desktop")} stays visible with full background",
                        visible&&covered,new{samples,visible,covered});
                }
            }
            MotionDesign.Configure(systemPreferences.MotionSpeed);
            if(!mini)ToggleMini();await panelShapeCompletion;await Settle();Check("Mini does not scroll",scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Disabled&&scroll.VerticalScrollMode==ScrollMode.Disabled);Check("Mini content fits",page.DesiredSize.Height<=scroll.ViewportHeight+1&&scroll.ViewportHeight-page.DesiredSize.Height<=20,new{page=page.DesiredSize.Height,viewport=scroll.ViewportHeight,height=AppWindow.Size.Height});
            var modes=DetailDescendants(page).OfType<PaintButton>().Where(b=>new[]{"办公","均衡","狂暴","自定义","核显模式","混合输出","独显直连"}.Contains(AutomationProperties.GetName(b))).ToArray();Check("Mini retains all mode labels",modes.Length==7&&modes.All(b=>b.ActualHeight>=56));
            await CaptureApprovedSurface("Painted-mini");
            if(mini)ToggleMini();await panelShapeCompletion;await Settle();
            ResizeLogical(1232,800);
            PaintNavigate("overview");
            await Settle();
            var iconsBefore=DetailDescendants(navigation).OfType<Image>().ToArray();
            var sourcesBefore=iconsBefore.Select(icon=>icon.Source).ToArray();
            var resizeWatch=System.Diagnostics.Stopwatch.StartNew();
            foreach(int width in new[]{1220,1200,1180,1150,1000,960,900,840,900,960,1000,1180,1232})
            {
                ResizeLogical(width,800);
                ArrangeDesktopViewport();
                await Task.Delay(30);
            }
            await Settle();
            var iconsAfter=DetailDescendants(navigation).OfType<Image>().ToArray();
            Check("Manual resize keeps navigation image elements and decoded sources",
                iconsBefore.SequenceEqual(iconsAfter)&&sourcesBefore.SequenceEqual(iconsAfter.Select(icon=>icon.Source)),
                new{images=iconsAfter.Length,elapsedMs=resizeWatch.ElapsedMilliseconds});
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})foreach(int width in new[]{1232,840})
            {
                root.RequestedTheme=theme;ResizeLogical(width,800);await Settle();
                foreach(string route in new[]{"overview","display","power","sleep","devices","lightingzone","plugins","settings","osdsettings","storage","diagnostics","tuning","color"})
                {
                    selectedLightingZone="keyboard";PaintNavigate(route);await Settle();UpdateOverviewValues();UpdateOverviewPowerTarget();RefreshPaintedState();await Settle();
                    var stock=DetailDescendants(page).Where(e=>e switch
                    {
                        PaintButton or PaintSlider => false,
                        ToggleSwitch toggle => !PaintSwitch.IsPainted(toggle),
                        Button or Slider or ComboBox or NumberBox or NavigationView or ColorPicker => true,
                        _ => false
                    }).Select(e=>e.GetType().Name).Distinct().ToArray();Check($"{theme} {width} {route} uses the approved control presentation",stock.Length==0,stock);
                    var text=DetailDescendants(page).OfType<TextBlock>().ToArray();Check($"{theme} {width} {route} typography",text.All(t=>t.FontSize is 12 or 14 or 16 or 24));
                    if(width==1232&&route=="overview")await CaptureApprovedSurface($"Painted-{theme}-{route}-{width}");
                }
            }
            PaintNavigate("overview");await Settle();var initial=overviewSaved.ToArray();PaintButton Find(string name)=>DetailDescendants(page).OfType<PaintButton>().First(b=>AutomationProperties.GetName(b)==name);Find("编辑磁贴").Invoke();await Settle();Check("Edit entry replaced by save and cancel icons",!DetailDescendants(page).OfType<PaintButton>().Any(b=>AutomationProperties.GetName(b)=="编辑磁贴")&&Find("保存磁贴布局").Content is Image&&Find("取消编辑磁贴").Content is Image);
            overviewDraft!.Clear();Render();await Settle();Check("Empty layout retains add",DetailDescendants(page).OfType<PaintSelect>().Any(b=>AutomationProperties.GetName(b)=="添加面板"));Find("取消编辑磁贴").Invoke();await Settle();Check("Cancel restores saved order",overviewSaved.SequenceEqual(initial)&&overviewDraft==null);
            var toggle=PaintSwitch.Create("原生开关输入",false);page.Children.Add(toggle);await Settle();
            var toggleInput=(IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
            toggleInput.Toggle();await Settle();Check("Styled switch toggles",toggle.IsOn);
            toggleInput.Toggle();await Settle();Check("Styled switch returns to off",!toggle.IsOn);
            Check("Read-only review did not acquire hardware",!hardware.Active&&upstream==null&&!trayAvailable);
        }
        catch(Exception error){Check("Review completed",false,error.ToString());}
        finally{exitRequested=true;await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"painted-ui-review.json"),JsonSerializer.Serialize(new{passed,hardwareWrites=false,syntheticLayoutFixture=true,browserPixelComparisonVerified=false,physicalDpiMatrixVerified=false,results},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=passed?0:1;}
    }
}


