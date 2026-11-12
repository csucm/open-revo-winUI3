using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenRevo.Core;
using System.Diagnostics;
using Windows.Graphics;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> panelMotionAnchors = [];
    private int lastPanelMotionFrames;
    private CancellationTokenSource? panelMotionStop;
    private PanelHandoffSurface? panelLargeSurface, panelMiniSurface;
    private bool panelGoalMini, panelTimelinePlaying;
    private double panelProgress;
    private long panelFrameTimestamp;
    private double panelPlaybackRate = 1;
    partial void ObservePanelHandoff(double progress, int frames, string? failure);
    private T PanelAnchor<T>(string key, T element) where T : FrameworkElement
    { panelMotionAnchors[key] = element; return element; }

    private void AdvancePanelTimeline()
    {
        long now = Stopwatch.GetTimestamp();
        if (panelTimelinePlaying)
        {
            double delta = Stopwatch.GetElapsedTime(panelFrameTimestamp, now).TotalMilliseconds * panelPlaybackRate / PanelHandoffGeometry.DurationMilliseconds;
            panelProgress = Math.Clamp(panelProgress + (panelGoalMini ? delta : -delta), 0, 1);
        }
        panelFrameTimestamp = now;
    }
    private void SetPanelGoal(bool targetMini)
    {
        AdvancePanelTimeline();
        panelGoalMini = targetMini;
    }
    private void HidePanelHandoff()
    {
        panelMotionStop?.Cancel();
        panelLargeSurface?.Hide(); panelMiniSurface?.Hide();
    }
    private void PanelHandoffCommand(uint command)
    {
        // A bitmap never replays a click against a device control. All caption
        // commands enter the owner's existing state machine on its UI thread.
        QueueCaptionAction(() =>
        {
            if (closed) return;
            if (command == 0xF030) ToggleMini();
            else if (command == 0xF020)
            {
                if (panelGoalMini) HideToTray();
                else SetPanelGoal(true);
            }
            else if (command == 0xF060) Close();
        });
    }
    private Task<PanelHandoffSurface> CapturePanelHandoff()
    {
        double scale = GetDpiForWindow(captionWindow) / 96d;
        RectInt32 toggle = default;
        if (panelToggle is { IsLoaded: true, Visibility: Visibility.Visible })
        {
            var rect = panelToggle.TransformToVisual(windowSurface).TransformBounds(new(0, 0, panelToggle.ActualWidth, panelToggle.ActualHeight));
            toggle = new((int)Math.Round(rect.X * scale), (int)Math.Round(rect.Y * scale), (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale));
        }
        bool snapshotMini = mini;
        return PanelHandoffSurface.Capture(windowSurface, captionWindow, AppWindow.TitleBar.RightInset,
            (int)Math.Round(root.RowDefinitions[0].ActualHeight * scale), toggle,
            command =>
            {
                if (command == 0xF020) QueueCaptionAction(() =>
                {
                    if (closed) return;
                    if (snapshotMini) HideToTray();
                    else if (panelShapeChanging) SetPanelGoal(true);
                    else QueuePanelMinimize();
                });
                else PanelHandoffCommand(command);
            },
            panelMotionStop?.Token ?? CancellationToken.None);
    }

    private async Task RunPanelHandoff(bool animate)
    {
        bool animationRequested = animate;
        bool sourceMini = mini, sourceMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        bool initialGoal = panelGoalMini;
        RectInt32 sourceBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        RectInt32 destinationBounds = sourceBounds;
        double sourceScale = GetDpiForWindow(captionWindow) / 96d;
        double previousWidth = root.Width, previousHeight = root.Height;
        bool previousHitTest = root.IsHitTestVisible;
        bool showFallback = false, restoreFocus = false, finishFailed = false;
        string? failure = null;
        nint foregroundAtStart = NativeOsd.Foreground;
        if (!sourceMini) normalBounds = sourceBounds;
        using var stop = new CancellationTokenSource(); panelMotionStop = stop;
        panelProgress = sourceMini ? 1 : 0; lastPanelMotionFrames = 0;
        try
        {
            root.IsHitTestVisible = false;
            ResetCaptionHover(); MotionDesign.Reset(page); page.Opacity = 1;
            if (animate)
            {
                var source = await CapturePanelHandoff();
                if (sourceMini) panelMiniSurface = source; else panelLargeSurface = source;
                stop.Token.ThrowIfCancellationRequested();
                source.Paint(1, 0, 0, true);
            }
            panelCloakStarted = Stopwatch.GetTimestamp();
            panelCompositionGuard = new PanelCompositionGuard(captionWindow);
            // Never expose a half-prepared target if cloaking is unavailable.
            // The immutable source surface remains visible during preparation.
            if (!panelCompositionGuard.Cloaked && AppWindow.IsVisible)
            {
                AppWindow.Hide(); showFallback = true; animate = false;
                panelLargeSurface?.Hide(); panelMiniSurface?.Hide();
            }
            if (!sourceMini)
            {
                if (AppWindow.Presenter is OverlappedPresenter restore && restore.State != OverlappedPresenterState.Restored) restore.Restore();
                normalBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            }
            ConfigurePanelMode(initialGoal);
            destinationBounds = initialGoal ? MiniPanelBounds() : normalBounds;
            PlacePanel(destinationBounds);
            if (animate)
            {
                await NextPanelFrame();
                stop.Token.ThrowIfCancellationRequested();
                var destination = await CapturePanelHandoff();
                if (initialGoal) panelMiniSurface = destination; else panelLargeSurface = destination;
                stop.Token.ThrowIfCancellationRequested();
                var large = sourceMini ? destinationBounds : sourceBounds;
                var small = sourceMini ? sourceBounds : destinationBounds;
                var travel = PanelHandoffGeometry.Travel(large, small, sourceScale > 0 ? sourceScale : 1);
                var (initialA, initialB) = PanelHandoffGeometry.Opacity(panelProgress);
                panelLargeSurface!.Paint(initialA, (1 - initialA) * travel.X, (1 - initialA) * travel.Y, !sourceMini);
                panelMiniSurface!.Paint(initialB, -(1 - initialB) * travel.X, -(1 - initialB) * travel.Y, sourceMini);
                panelMiniSurface.Raise(); // Match the preview's fixed layer order in both directions.
                panelLargeSurface.ConfirmPresentation("large");
                panelMiniSurface.ConfirmPresentation("mini");
                panelFrameTimestamp = Stopwatch.GetTimestamp(); panelTimelinePlaying = true;
                while (!stop.IsCancellationRequested && !closed && !exitRequested && !trayHidden)
                {
                    AdvancePanelTimeline();
                    if (!Design.MotionEnabled) panelProgress = panelGoalMini ? 1 : 0;
                    var (a, b) = PanelHandoffGeometry.Opacity(panelProgress);
                    bool miniInput = b >= a;
                    panelLargeSurface!.Paint(a, (1 - a) * travel.X, (1 - a) * travel.Y, !miniInput);
                    panelMiniSurface!.Paint(b, -(1 - b) * travel.X, -(1 - b) * travel.Y, miniInput);
                    lastPanelMotionFrames++;
                    ObservePanelHandoff(panelProgress, lastPanelMotionFrames, null);
                    if (panelProgress == (panelGoalMini ? 1 : 0)) break;
                    await NextPanelFrame();
                }
                // Stop advancing before awaiting the final composition commit.
                panelTimelinePlaying = false;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error.Message;
            DiagnosticLog.Failure("window.handoff", error);
            // Snapshot/driver failures use the same direct endpoint path as
            // reduced motion. No partially transparent live window survives.
        }
        finally
        {
            panelTimelinePlaying = false;
            if (!closed)
            {
                try
                {
                    if (panelCompositionGuard == null)
                    {
                        panelCloakStarted = Stopwatch.GetTimestamp();
                        panelCompositionGuard = new PanelCompositionGuard(captionWindow);
                        if (!panelCompositionGuard.Cloaked && AppWindow.IsVisible) { AppWindow.Hide(); showFallback = true; }
                    }
                    if (!mini && panelGoalMini && AppWindow.Presenter is OverlappedPresenter restore && restore.State != OverlappedPresenterState.Restored)
                    {
                        restore.Restore();
                        normalBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
                    }
                    // A reverse request uses the two retained snapshots. Only
                    // after playback do we rebuild the live owner if necessary.
                    if (mini != panelGoalMini) ConfigurePanelMode(panelGoalMini);
                    var finalBounds = panelGoalMini == sourceMini ? sourceBounds : destinationBounds;
                    // If capture failed before the target was prepared, derive
                    // the actual requested endpoint rather than reuse the source.
                    if (destinationBounds.Equals(sourceBounds) && panelGoalMini != sourceMini)
                        finalBounds = panelGoalMini ? MiniPanelBounds() : normalBounds;
                    // Preserve the original restore rectangle when a trip out
                    // of a maximized desktop reverses back to that desktop.
                    bool restoreMaximized = sourceMaximized && !panelGoalMini && panelGoalMini == sourceMini;
                    if (restoreMaximized) finalBounds = normalBounds;
                    PlacePanel(finalBounds);
                    if(panelGoalMini)PositionMiniAtBottomRight();
                    if (restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
                    { presenter.Maximize(); panelTargetPixelWidth = AppWindow.Size.Width; }
                    root.Width = previousWidth; root.Height = previousHeight;
                    AlignCaption(); ArrangeDesktopViewport(); root.UpdateLayout();
                    await NextPanelFrame();
                    var commit = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(root).Compositor.RequestCommitAsync().AsTask();
                    await Task.WhenAny(commit, Task.Delay(80));
                    if (commit.IsCompleted) await commit;
                    if (!closed)
                    {
                        if (trayHidden || exitRequested) AppWindow.Hide();
                        else
                        {
                            panelCompositionGuard?.Reveal();
                            if (showFallback) AppWindow.Show(false);
                            // Hand back to the system shadow in the same flush,
                            // rather than compose two shadows for a full frame.
                            panelLargeSurface?.Hide(); panelMiniSurface?.Hide();
                            PanelCompositionGuard.Flush();
                            restoreFocus = AppWindow.IsVisible && foregroundAtStart == captionWindow &&
                                (NativeOsd.Foreground == captionWindow || NativeOsd.Foreground == 0);
                        }
                    }
                }
                catch (Exception error) { finishFailed = true; DiagnosticLog.Failure("window.handoff_finish", error); }
            }
            // Always release native windows, DIBs and memory DCs, including on
            // hide, exit, reversal, capture failure and a destroyed owner.
            panelLargeSurface?.Dispose(); panelLargeSurface = null;
            panelMiniSurface?.Dispose(); panelMiniSurface = null;
            int activeLayersAfterCleanup = PanelHandoffSurface.ActiveCount;
            long activeBitmapBytesAfterCleanup = PanelHandoffSurface.ActiveBitmapBytes;
            panelCompositionGuard?.Dispose(); panelCompositionGuard = null;
            if (showFallback && !closed && !trayHidden && !exitRequested) AppWindow.Show(false);
            panelMotionStop = null; root.IsHitTestVisible = previousHitTest;
            root.Width = previousWidth; root.Height = previousHeight;
            lastPanelCloakMs = Stopwatch.GetElapsedTime(panelCloakStarted).TotalMilliseconds;
            panelTargetPixelWidth = 0; panelShapeChanging = false;
            if (!closed)
            {
                ResetCaptionHover();
                if (restoreFocus && !trayHidden && !exitRequested) Activate();
                if (mini && systemPreferences.MiniAutoHide && !dialogOpen && !exitRequested && !panelMinimizeQueued &&
                    foregroundAtStart == captionWindow && NativeOsd.Foreground != 0 && NativeOsd.Foreground != captionWindow && AppWindow.IsVisible)
                    HideToTray();
                UpdatePolling();
                if (residentClient != null && !mini && customTuningOpen && residentTuning == null)
                    _ = PaintReadTuning(automatic: true);
                // A command arriving during the endpoint commit starts the next
                // trip. Commands during playback reverse the retained timeline.
                if (!trayHidden && !exitRequested && mini != panelGoalMini) ToggleMini();
            }
            ObservePanelHandoff(panelProgress, lastPanelMotionFrames, failure);
            DiagnosticLog.Record("window.handoff_completed", new { mini, frames = lastPanelMotionFrames,
                durationMs = PanelHandoffGeometry.DurationMilliseconds / panelPlaybackRate, rate = panelPlaybackRate, cloakMs = lastPanelCloakMs,
                animationRequested, cancelled = stop.IsCancellationRequested, cloakFallback = showFallback,
                captureOrPresentationFailed = failure != null, finishFailed,
                activeLayers = activeLayersAfterCleanup, activeLayerBitmapBytes = activeBitmapBytesAfterCleanup });
        }
    }
    private void PlacePanel(RectInt32 bounds)
    {
        panelTargetPixelWidth = bounds.Width;
        AppWindow.MoveAndResize(bounds);
        PreparePanelLayout(bounds);
        AlignCaption(); ArrangeDesktopViewport(); root.UpdateLayout();
    }
    private void ConfigurePanelMode(bool targetMini)
    {
        mini = targetMini;
        if (mini)
        {
            developerShape = "real";
            navigation.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.LeftMinimal;
            navigation.IsPaneOpen = false; navigation.IsPaneVisible = false;
            page.Padding = new Thickness(12, 4, 12, 10); page.Spacing = 6;
            captionLabel.Text = "Open-Revo"; miniLabel.Visibility = Visibility.Visible; captionLabel.FontSize = 13;
        }
        else
        {
            navigation.IsPaneVisible = true; navigation.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Left; navigation.IsPaneOpen = true;
            captionLabel.Text = "Open-Revo"; miniLabel.Visibility = Visibility.Collapsed; captionLabel.FontSize = 14;
            page.Padding = new Thickness(28); page.Spacing = 18;
            var minimum = MinimumDesktopPixels();
            normalBounds.Width = Math.Max(normalBounds.Width, minimum.Width); normalBounds.Height = Math.Max(normalBounds.Height, minimum.Height);
        }
        UpdatePanelToggle(); Render(); Title = mini ? "Open-Revo Mini" : "Open-Revo";
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsMaximizable = !mini || captionModeAttached; presenter.IsResizable = !mini; }
    }
}
