using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
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
        var checks = new List<object>(); var samples = new List<object>(); bool passed = true;
        void Check(string name, bool result) { checks.Add(new { name, passed = result }); passed &= result; }
        nint originalWindow = captionWindow;
        try
        {
            Check("Review uses independent data and no hardware session", uiReviewMode && AppData.DirectoryPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) && !hardware.Active);
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
                        Check($"{theme}/{i}/{targetMini}: outer review cloak preserved", (cloaked & 1) != 0);
                        Check($"{theme}/{i}/{targetMini}: reveal has a bounded wait", watch.Elapsed < TimeSpan.FromSeconds(2));
                        samples.Add(new { theme = theme.ToString(), i, mini, elapsedMs = watch.ElapsedMilliseconds, cloakMs=lastPanelCloakMs, width = AppWindow.Size.Width, height = AppWindow.Size.Height, renderCount, cloaked });
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
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "panel-shape-review.json"), JsonSerializer.Serialize(new { allPassed = passed, hardwareWrites = 0, visibleReviewWindows = 0, checks, samples }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = passed ? 0 : 1;
            AppWindow.Hide(); panelReviewGuard?.Dispose(); panelReviewGuard = null;
            sessionEnding = true; desktopRuntimeClosed = true; exitRequested = true; Close();
        }
    }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetPanelCloak(nint window, uint attribute, out int value, int size);
}
