using System.Diagnostics;
using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed record PhysicalModeStatus(bool Attempted, bool Subscribed, string? Failure);

public sealed class PerformanceInputRuntime
{
    private readonly HardwareSession hardware;
    private readonly HardwareOperationQueue operations;
    private readonly PerformanceCommandService performance;
    private readonly bool useNativeModeKeyEvents;
    private readonly object gate = new();
    private AcpiModeKeyObserver? observer;
    private bool attempted, physicalModeBusy;
    private int generation, sleepActions;
    private long lastKey;
    private string? startFailure;

    internal PerformanceInputRuntime(HardwareSession hardware, HardwareOperationQueue operations,
        PerformanceCommandService performance, bool useNativeModeKeyEvents)
    {
        this.hardware = hardware;
        this.operations = operations;
        this.performance = performance;
        this.useNativeModeKeyEvents = useNativeModeKeyEvents;
    }

    public event Action<PerformanceResult>? PhysicalModeCompleted;
    public bool PhysicalModeBusy { get { lock (gate) return physicalModeBusy; } }
    public bool SleepOfficeBusy => Volatile.Read(ref sleepActions) > 0;
    public PhysicalModeStatus Status
    {
        get { lock (gate) return new(attempted, observer?.Installed == true, observer?.Failure ?? startFailure); }
    }

    public void SynchronizePhysicalMode(bool enabled)
    {
        lock (gate)
        {
            if (!enabled || !operations.IsAccepting || !hardware.Active || !CoreMachine.Current.Ready ||
                hardware.CoreDelegated || !hardware.SupportsPerformance)
            {
                generation++;
                observer?.Dispose();
                observer = null;
                attempted = false;
                startFailure = null;
                return;
            }
            if (attempted) return;
            attempted = true;
            int subscription = ++generation;
            try { observer = new(code => _ = HandleModeKeyAsync(subscription, code), useNativeModeKeyEvents); }
            catch (Exception error)
            {
                startFailure = error.Message;
                DiagnosticLog.Failure("modekey.subscription.failed", error);
            }
        }
    }

    private async Task HandleModeKeyAsync(int subscription, int code)
    {
        lock (gate)
        {
            if (subscription != generation || !operations.IsAccepting || !hardware.Active) return;
            long now = Stopwatch.GetTimestamp();
            if (lastKey != 0 && Stopwatch.GetElapsedTime(lastKey, now) < TimeSpan.FromMilliseconds(500)) return;
            if (physicalModeBusy || operations.IsBusy)
            {
                DiagnosticLog.Record("modekey.ignored", new { code, reason = "hardware-busy" });
                return;
            }
            lastKey = now;
            physicalModeBusy = true;
        }
        try
        {
            var result = await performance.CyclePhysicalKeyAsync(code).ConfigureAwait(false);
            if (result.Error is { } error) DiagnosticLog.Failure("modekey.operation.failed", error);
            DiagnosticLog.Record("modekey.result", new { code, result.Outcome, result.Mode, result.IndicatorWarning });
            PhysicalModeCompleted?.Invoke(result);
        }
        catch (Exception error) { DiagnosticLog.Failure("modekey.result.consumer_failed", error); }
        finally { lock (gate) physicalModeBusy = false; }
    }

    public async Task<PerformanceResult> ObserveSuspensionAsync(bool sleeping)
    {
        performance.ObserveSuspension(sleeping);
        if (!operations.IsAccepting || !hardware.Active || hardware.CoreDelegated ||
            !(performance.SleepOfficeEnabled || performance.HasTemporaryOffice || SleepOfficeBusy))
            return new(PerformanceOutcome.Skipped);
        Interlocked.Increment(ref sleepActions);
        try { return await performance.ApplyAutomaticOfficeAsync(sleeping).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref sleepActions); }
    }
}
