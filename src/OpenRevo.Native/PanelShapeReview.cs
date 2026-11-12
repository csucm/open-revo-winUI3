using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PanelCompositionGuard? panelReviewGuard;
    internal void StartPanelShapeReview()
    {
        panelReviewGuard = new PanelCompositionGuard(captionWindow);
        if (!panelReviewGuard.Cloaked) throw new IOException("Cannot cloak the isolated review window.");
        AppWindow.Show(false);
    }
    private async Task RunPanelShapeReview()
    {
        var checks = new List<object>(); var samples = new List<object>();var layouts=new List<object>(); bool passed = true;
        void Check(string name, bool result) { checks.Add(new { name, passed = result }); passed &= result; }
        nint originalWindow = captionWindow;
        try
        {
            await NextPanelFrame();await Task.Delay(100);
            DiagnosticLog.Record("window.panel_review_started");
            Check("Review uses independent data and no hardware session", uiReviewMode && AppData.DirectoryPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) && !hardware.Active);
            if(!mini){ToggleMini();await panelShapeCompletion;}
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            {
                root.RequestedTheme=theme;
                keyboard=new("SingleZone","单区 RGB",true,false);lightingReadFresh=true;
                displayState=new(1920,1080,165,[60,165],"review","Review panel",true);
                Render();await NextPanelFrame();root.UpdateLayout();
                double knownHeight=panelMotionAnchors["lighting"].ActualHeight,knownPageHeight=page.ActualHeight;
                keyboard=new("Unknown","背光类型未识别",false,false);lightingReadFresh=false;displayState=null;
                Render();await NextPanelFrame();root.UpdateLayout();
                var lighting=panelMotionAnchors["lighting"];
                layouts.Add(new{theme=theme.ToString(),knownHeight,unknownHeight=lighting.ActualHeight,knownPageHeight,unknownPageHeight=page.ActualHeight,viewport=scroll.ActualHeight});
                var editors=DetailDescendants(lighting).OfType<Control>().Where(c=>c is Slider or ComboBox or Button).ToArray();
                Check($"{theme}: unknown lighting keeps slider, mode, color and more",editors.OfType<Slider>().Count()==1&&editors.OfType<ComboBox>().Count()==2&&editors.OfType<Button>().Any());
                Check($"{theme}: unknown lighting editors are disabled",editors.All(c=>!c.IsEnabled));
                Check($"{theme}: unknown lighting and page retain known layout height",Math.Abs(lighting.ActualHeight-knownHeight)<1&&Math.Abs(page.ActualHeight-knownPageHeight)<1);
                var rates=DetailDescendants(miniRefreshHost!).OfType<Button>().ToArray();
                Check($"{theme}: unknown refresh keeps three disabled slots",rates.Length==3&&rates.All(b=>!b.IsEnabled));
                Check($"{theme}: mini fits its unchanged viewport",page.ActualHeight<=scroll.ActualHeight+1&&page.Children.Count==8);
                await CaptureDesktopReview("Panel-unknown-mini-"+theme);
            }
            if(mini){ToggleMini();await panelShapeCompletion;}
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                for (int i = 0; i < 12; i++)
                {
                    foreach (bool targetMini in new[] { true, false })
                    {
                        if (mini == targetMini) continue;
                        int beforeRender = renderCount;
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        ToggleMini(); await panelShapeCompletion;
                        root.UpdateLayout();
                        GetPanelCloak(captionWindow, 14, out int cloaked, 4);
                        Check($"{theme}/{i}/{targetMini}: same window and completed transition", captionWindow == originalWindow && mini == targetMini && !panelShapeChanging && panelCompositionGuard == null);
                        Check($"{theme}/{i}/{targetMini}: one render and no page fade", renderCount == beforeRender + 1 && page.Opacity == 1);
                        Check($"{theme}/{i}/{targetMini}: input, transforms and layout restored",navigation.IsHitTestVisible&&ElementCompositionPreview.GetElementVisual(navigation).Scale==Vector3.One&&double.IsNaN(root.Width)&&double.IsNaN(root.Height));
                        Check($"{theme}/{i}/{targetMini}: motion follows system animation preference",!Design.MotionEnabled||lastPanelMotionFrames>0);
                        var expected=mini?MiniPanelBounds():normalBounds;
                        Check($"{theme}/{i}/{targetMini}: exact destination bounds",AppWindow.Position.X==expected.X&&AppWindow.Position.Y==expected.Y&&AppWindow.Size.Width==expected.Width&&AppWindow.Size.Height==expected.Height);
                        Check($"{theme}/{i}/{targetMini}: outer review cloak preserved", (cloaked & 1) != 0);
                        Check($"{theme}/{i}/{targetMini}: reveal has a bounded wait", watch.Elapsed < TimeSpan.FromSeconds(2));
                        samples.Add(new { theme = theme.ToString(), i, mini, elapsedMs = watch.ElapsedMilliseconds, cloakMs=lastPanelCloakMs, frames=lastPanelMotionFrames, width = AppWindow.Size.Width, height = AppWindow.Size.Height, renderCount, cloaked });
                    }
                }
            }
            Check("No production hardware takeover or user tray", !hardware.Active && !trayAvailable);
            AppWindow.Hide();panelReviewGuard?.Dispose();panelReviewGuard=null;
            GetPanelCloak(captionWindow,14,out int beforeCloak,4);
            GetPanelCloak(captionWindow,3,out int beforeTransitions,4);
            using(var guard=new PanelCompositionGuard(captionWindow))
            {
                GetPanelCloak(captionWindow,14,out int activeCloak,4);
                Check("Guard can acquire an app cloak",guard.Cloaked&&(activeCloak&1)!=0);
            }
            GetPanelCloak(captionWindow,14,out int afterCloak,4);
            GetPanelCloak(captionWindow,3,out int afterTransitions,4);
            Check("Guard restores previous cloak and DWM transition state",beforeCloak==afterCloak&&beforeTransitions==afterTransitions);
        }
        catch (Exception error) { checks.Add(new { passed = false, error = error.ToString() }); passed = false; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "panel-shape-review.json"), JsonSerializer.Serialize(new { allPassed = passed, hardwareWrites = 0, visibleReviewWindows = 0, checks, samples,layouts }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = passed ? 0 : 1;
            AppWindow.Hide(); panelReviewGuard?.Dispose(); panelReviewGuard = null;
            sessionEnding = true; desktopRuntimeClosed = true; exitRequested = true; Close();
        }
    }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetPanelCloak(nint window, uint attribute, out int value, int size);
}
