using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private bool physicalModeChanging => hardwareRuntime.PerformanceInput.PhysicalModeBusy;

    private void SynchronizeModeKeyMonitor() => hardwareRuntime.PerformanceInput.SynchronizePhysicalMode(
        !uiReviewMode && !closed && !exitRequested && !sessionEnding && !settingsDataBusy &&
        !dataStorageExitPending && upstream != null);

    private void PresentPhysicalModeResult(PerformanceResult result)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (result.Outcome is PerformanceOutcome.Skipped or PerformanceOutcome.Busy or PerformanceOutcome.Closing)
                return;
            await RunPerformance(() => Task.FromResult(result), "机身按键切换档位已回读确认。");
        });
    }
}
