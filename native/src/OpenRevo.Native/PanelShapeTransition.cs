using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private bool panelShapeChanging;
    private PanelCompositionGuard? panelCompositionGuard;
    private Task panelShapeCompletion = Task.CompletedTask;
    private long panelCloakStarted;
    private double lastPanelCloakMs;
    private int panelTargetPixelWidth;
    private void PreparePanelLayout(RectInt32 target)
    {
        double scale=GetDpiForWindow(captionWindow)/96d;if(scale<=0)scale=1;
        GetPanelClientRect(captionWindow,out var client);
        int frameWidth=AppWindow.Size.Width-(client.Right-client.Left),frameHeight=AppWindow.Size.Height-(client.Bottom-client.Top);
        root.Width=Math.Max(1,(target.Width-frameWidth)/scale);
        root.Height=Math.Max(1,(target.Height-frameHeight)/scale);
        root.UpdateLayout();ArrangeDesktopViewport();root.UpdateLayout();
    }
    [StructLayout(LayoutKind.Sequential)]private struct PanelClientRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",EntryPoint="GetClientRect")]
    private static extern bool GetPanelClientRect(nint window,out PanelClientRect rect);

    private async Task FinishPanelShape(bool showFallback)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await NextPanelFrame();
            if (closed) return;
            root.UpdateLayout();
            var commit = ElementCompositionPreview.GetElementVisual(root).Compositor.RequestCommitAsync().AsTask();
            await Task.WhenAny(commit, Task.Delay(80));
            if (commit.IsCompleted) await commit;
            if (!closed) PanelCompositionGuard.Flush();
        }
        catch (Exception error) { DiagnosticLog.Failure("window.shape_commit", error); }
        finally
        {
            panelCompositionGuard?.Dispose(); panelCompositionGuard = null;
            lastPanelCloakMs=System.Diagnostics.Stopwatch.GetElapsedTime(panelCloakStarted).TotalMilliseconds;
            panelShapeChanging = false;
            panelTargetPixelWidth=0;
            if (showFallback && !closed && !trayHidden && !exitRequested) AppWindow.Show();
            DiagnosticLog.Record("window.shape_committed", new { mini, elapsedMs = watch.ElapsedMilliseconds, cloakMs=lastPanelCloakMs });
        }
    }
    private static async Task NextPanelFrame()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) => ready.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { await Task.WhenAny(ready.Task, Task.Delay(32)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}
