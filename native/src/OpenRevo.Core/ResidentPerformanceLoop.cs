using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed record ResidentPerformanceStatus(bool Active, int? Mode, bool WaitingForTakeover,
    DisplayReadSnapshot? Display = null);

// The tray host supplies its own lifetime and Windows event source. This loop
// performs only the existing performance policies, at the existing cadence.
public sealed class ResidentPerformanceLoop(ApplicationHardwareRuntime runtime)
{
    public async Task RunAsync(Action<ResidentPerformanceStatus> publish, CancellationToken cancellationToken, Action? onCycle = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Hardware.SetBatteryPerformancePermission(SystemPreferences.Load().UnlockBatteryLimit);
        runtime.OpenBridge();
        var takeover = await runtime.Devices.AutomaticTakeoverAsync().ConfigureAwait(false);
        ReportTakeover(takeover);
        try { await runtime.Desktop.RestoreSavedColorAsync().ConfigureAwait(false); }
        catch (Exception error) { DiagnosticLog.Failure("resident.saved-color.failed", error); }

        ResidentPerformanceStatus? previous = null;
        Publish();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!runtime.Operations.IsAccepting) continue;
            if (runtime.Devices.AutomaticTakeoverPending)
                ReportTakeover(await runtime.Devices.AutomaticTakeoverAsync(retryOnly: true).ConfigureAwait(false));
            try
            {
                runtime.SessionPower.RestoreOnInput();
                if (runtime.Hardware.Active)
                    await runtime.Display.EvaluateAutomaticAsync(null, visible: false).ConfigureAwait(false);
            }
            catch (Exception error) { DiagnosticLog.Failure("resident.display-policy.failed", error, throttle: true); }
            var performance = runtime.Performance;
            if (performance.ShouldSynchronizePowerLink)
                Report("resident.power-link", await performance.SynchronizePowerLink().Completion.ConfigureAwait(false));
            if (performance.ShouldCheckBattery)
                Report("resident.battery-guard", await performance.EnforceBatteryGuardAsync().ConfigureAwait(false));
            bool? ac = runtime.Hardware.Active && runtime.Hardware.DeclaredCpuTuning != null ? Telemetry.ReadAc() : null;
            if (performance.ShouldRestoreDeclaredOnAc(ac))
                Report("resident.tuning-restore", await performance.RestoreDeclaredOnAcAsync().ConfigureAwait(false));
            onCycle?.Invoke();
            if (runtime.Operations.IsAccepting) Publish();
        }

        void Publish()
        {
            runtime.PerformanceInput.SynchronizePhysicalMode(true);
            var next = new ResidentPerformanceStatus(runtime.Hardware.Active, runtime.Hardware.CurrentMode,
                runtime.Devices.AutomaticTakeoverPending, runtime.Display.Snapshot);
            if (next == previous) return;
            previous = next;
            publish(next);
        }
    }

    private static void ReportTakeover(TakeoverResult result)
    {
        if (result.Outcome == TakeoverOutcome.Skipped) return;
        DiagnosticLog.Record("resident.takeover", new { result.Outcome, result.Reason, state = result.State });
        if (result.Error is { } error) DiagnosticLog.Failure("resident.takeover.failed", error);
    }

    public static void Report(string eventName, PerformanceResult result)
    {
        if (result.Error is { } error) DiagnosticLog.Failure(eventName, error);
        else if (result.FollowUpError is { } followUp) DiagnosticLog.Failure(eventName, followUp);
        else if (result.Outcome is PerformanceOutcome.ReadbackConfirmed or PerformanceOutcome.CommandSent)
            DiagnosticLog.Record(eventName, new { result.Outcome, result.Mode, result.IndicatorWarning });
    }
}
