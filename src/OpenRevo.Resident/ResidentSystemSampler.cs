using System.Diagnostics;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

// One system sample feeds the attached panel and subscribed plugin workers.
internal sealed class ResidentSystemSampler(ApplicationTelemetryRuntime telemetry, ResidentBackgroundPlugins plugins)
{
    private sealed record DetailedSample(PanelDetailedTelemetry State, long Epoch, int GpuGeneration);
    private PanelSystemSummary? snapshot;
    private SystemSnapshot? system;
    private DetailedSample? details;
    private long detailEpoch;
    internal PanelSystemSummary? Snapshot => Volatile.Read(ref snapshot);
    internal SystemSnapshot? LatestSystem => Volatile.Read(ref system);
    internal PanelDetailedTelemetry? Details => Volatile.Read(ref details) is { } value &&
        value.Epoch == Interlocked.Read(ref detailEpoch) && value.GpuGeneration == DisplayTopology.GpuGeneration
        ? value.State : null;

    internal void InvalidateDetails() => Interlocked.Increment(ref detailEpoch);

    internal async Task RunAsync(Func<PanelPresentationDemand> panelDemand, Func<int> panelInterval,
        Func<Task<bool>> refreshControls, Action changed, CancellationToken cancellationToken)
    {
        SystemSnapshot? latest = null;
        long sampled = 0, panelPublished = 0, pluginPublished = 0;
        var previousDemand = PanelPresentationDemand.Hidden;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var demand = panelDemand();
                bool detailed = HasDetails(demand);
                bool demandChanged = previousDemand != demand;
                if (demandChanged)
                {
                    InvalidateDetails();
                    if (!detailed && HasDetails(previousDemand))
                    {
                        Volatile.Write(ref details, null);
                        await telemetry.EndResidentPanelReadsAsync().ConfigureAwait(false);
                    }
                    previousDemand = demand;
                }
                bool panel = demand != PanelPresentationDemand.Hidden, plugin = plugins.HasSummarySubscribers;
                int interval = panelInterval();
                long now = Stopwatch.GetTimestamp();
                if (!panel)
                {
                    panelPublished = 0;
                    Volatile.Write(ref snapshot, null);
                }
                if (!plugin) pluginPublished = 0;
                bool panelDue = panel && (demandChanged || Due(panelPublished, now, interval));
                bool pluginDue = plugin && Due(pluginPublished, now, 3);
                try
                {
                    if (!detailed) await telemetry.ReleaseIdleResidentSamplingAsync().ConfigureAwait(false);
                    if (!panel && !plugin)
                    {
                        if (latest != null) telemetry.Foreground.Reset();
                        latest = null;
                        Volatile.Write(ref system, null);
                        continue;
                    }
                    if (panelDue || pluginDue)
                    {
                        int samplingInterval = panel && plugin ? Math.Min(interval, 3) : panel ? interval : 3;
                        if (latest == null || Due(sampled, now, samplingInterval))
                        {
                            latest = telemetry.Foreground.Read();
                            Volatile.Write(ref system, latest);
                            sampled = now;
                        }
                        if (panelDue)
                        {
                            Volatile.Write(ref snapshot, new(latest.At, latest.CpuPercent, latest.UsedMemoryGiB, latest.TotalMemoryGiB,
                                latest.BatteryPercent, latest.OnAc, latest.Charging));
                            panelPublished = now;
                            changed();
                        }
                        if (pluginDue)
                        {
                            plugins.PublishSummary(latest);
                            pluginPublished = now;
                        }
                    }
                    if (panel && await refreshControls().ConfigureAwait(false)) changed();
                    if (detailed && (panelDue || Details == null))
                    {
                        long epoch = Interlocked.Read(ref detailEpoch);
                        var detail = await telemetry.ReadResidentPanelAsync(demand == PanelPresentationDemand.Overview,
                            demand == PanelPresentationDemand.MiniGpu).ConfigureAwait(false);
                        if (detail != null && panelDemand() == demand && epoch == Interlocked.Read(ref detailEpoch))
                        {
                            Volatile.Write(ref details, new(Project(detail), epoch, detail.GpuGeneration));
                            changed();
                        }
                    }
                }
                catch (Exception error) { DiagnosticLog.Failure("resident.system.sample_failed", error, throttle: true); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref snapshot, null);
            Volatile.Write(ref system, null);
            Volatile.Write(ref details, null);
            InvalidateDetails();
            telemetry.Foreground.Reset();
            if (HasDetails(previousDemand)) await telemetry.EndResidentPanelReadsAsync().ConfigureAwait(false);
        }
    }

    private static bool HasDetails(PanelPresentationDemand demand) => demand is
        PanelPresentationDemand.Overview or PanelPresentationDemand.Mini or PanelPresentationDemand.MiniGpu;

    private static PanelDetailedTelemetry Project(ResidentPanelRead reading)
    {
        PanelOverviewTelemetry? overview = null;
        var state = reading.Overview;
        if (state.Sample is { } sample && sample.Data.TopologyGeneration == reading.GpuGeneration)
        {
            var cpu = sample.Data.Cpu;
            var gpu = sample.Data.Gpu;
            overview = new(sample.At, state.IsFresh, state.IsReading, new(cpu.Mhz, cpu.Usage, cpu.PowerW, cpu.PowerEstimated),
                reading.AllowOverviewGpu && sample.Data.GpuSampled
                    ? new(gpu.Temperature, gpu.CoreMhz, gpu.MemoryMhz, gpu.Usage, gpu.MemoryTotalBytes, gpu.MemoryUsedBytes,
                        gpu.PowerW, gpu.AdapterName, gpu.AdapterKind, gpu.SharedLimitBytes, gpu.SharedUsedBytes) : null);
        }
        return new(reading.Ec is { } ec ? new(ec.CpuFan, ec.GpuFan, ec.BatteryLimit, ec.FanBoost, ec.Status) : null,
            reading.Temperature is { } temperature ? new(temperature.Cpu, temperature.Gpu, temperature.CpuSource) : null,
            overview, reading.PowerTargets is { } power
                ? new(power.Mode, power.Pl1Watts, power.TgpWatts, power.BaseTgpWatts, power.MaximumBoostWatts, power.Detail, power.At) : null);
    }

    private static bool Due(long previous, long now, int seconds) =>
        previous == 0 || Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromSeconds(seconds - .05);
}
