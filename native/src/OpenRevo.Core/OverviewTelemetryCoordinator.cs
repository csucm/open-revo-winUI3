namespace OpenRevo.Core;

public sealed record OverviewSample(OverviewTelemetry Data, DateTimeOffset At);

public readonly record struct OverviewReadState(OverviewSample? Sample, bool IsReading, bool IsFresh);

// Demand comes from existing callers. Only an actual read schedules work.
public sealed class OverviewTelemetryCoordinator
{
    private static readonly TimeSpan ForegroundInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResumeFreshness = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TrayFreshness = TimeSpan.FromSeconds(4);
    private readonly object gate = new();
    private OverviewSample? sample;
    private ReadPlan sampledPlan, pendingPlan;
    private Task<OverviewSample?>? pending;
    private DateTimeOffset lastCompletion, resumeUntil;
    private int resumeGeneration;
    private bool foregroundActive;

    private readonly record struct ReadPlan(IOverviewTelemetrySource Source, bool SampleGpu,
        bool? DiscreteDeclared, int Generation);

    public OverviewReadState ReadState()
    {
        lock (gate)
        {
            return new(sample, pending != null, IsFresh(DisplayTopology.GpuGeneration, DateTimeOffset.UtcNow, ResumeFreshness));
        }
    }

    public void SetForegroundDemand(IOverviewTelemetrySource? source, bool? discreteDeclared = null,
        bool? onAc = null, int? gpuFanRpm = null) =>
        RequestForeground(source, discreteDeclared, onAc, gpuFanRpm);

    internal Task<OverviewSample?>? RequestForeground(IOverviewTelemetrySource? source, bool? discreteDeclared = null,
        bool? onAc = null, int? gpuFanRpm = null)
    {
        lock (gate)
        {
            if (source == null)
            {
                foregroundActive = false;
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var plan = new ReadPlan(source, AllowsGpu(discreteDeclared, onAc, gpuFanRpm), discreteDeclared,
                DisplayTopology.GpuGeneration);
            if (!foregroundActive)
            {
                foregroundActive = true;
                resumeGeneration = plan.Generation;
                resumeUntil = sampledPlan == plan && IsFresh(plan.Generation, now, ResumeFreshness)
                    ? sample!.At + ResumeFreshness : default;
            }

            if (resumeGeneration != plan.Generation || sampledPlan != plan) resumeUntil = default;
            if (pending == null && now >= resumeUntil && now - lastCompletion >= ForegroundInterval)
                return StartRead(plan);
            return pending;
        }
    }

    public async Task<(bool Allowed, OverviewSample? Sample)> ReadTrayGpuAsync(IOverviewTelemetrySource source,
        bool? discreteDeclared, bool readEc, int? gpuFanRpm)
    {
        // An incompatible in-flight sample must finish before another can start.
        // Recheck AC and topology after that wait, before admitting a GPU read.
        while (true)
        {
            bool? onAc = discreteDeclared != false && readEc && gpuFanRpm is > 0 ? Telemetry.ReadAc() : null;
            if (!AllowsGpu(discreteDeclared, onAc, gpuFanRpm)) return (false, null);

            var plan = new ReadPlan(source, true, discreteDeclared, DisplayTopology.GpuGeneration);
            Task<OverviewSample?> read;
            bool compatible;
            lock (gate)
            {
                if (sampledPlan == plan && sample?.Data.GpuSampled == true &&
                    IsFresh(plan.Generation, DateTimeOffset.UtcNow, TrayFreshness)) return (true, sample);

                read = pending ?? StartRead(plan);
                compatible = pendingPlan == plan;
            }

            var result = await read.ConfigureAwait(false);
            if (compatible)
                return (true, result?.Data.TopologyGeneration == DisplayTopology.GpuGeneration ? result : null);
        }
    }

    public async Task<OverviewSample> ReadForReviewAsync(IOverviewTelemetrySource source, bool sampleGpu)
    {
        var plan = new ReadPlan(source, sampleGpu, null, DisplayTopology.GpuGeneration);
        while (true)
        {
            Task<OverviewSample?> read;
            bool compatible;
            lock (gate)
            {
                read = pending ?? StartRead(plan);
                compatible = pendingPlan == plan;
            }

            var result = await read.ConfigureAwait(false);
            if (compatible) return result ?? throw new IOException("Core 监测读取不可用。");
        }
    }

    private static bool AllowsGpu(bool? discreteDeclared, bool? onAc, int? gpuFanRpm) =>
        discreteDeclared == false || onAc == true && gpuFanRpm is > 0;

    private bool IsFresh(int generation, DateTimeOffset now, TimeSpan freshness) =>
        sample is { } value && value.Data.TopologyGeneration == generation &&
        now - value.At >= TimeSpan.Zero && now - value.At < freshness;

    private Task<OverviewSample?> StartRead(ReadPlan plan)
    {
        // The caller holds gate. The worker can publish only after pending is assigned.
        pendingPlan = plan;
        return pending = Task.Run(() => ReadAndPublish(plan));
    }

    private OverviewSample? ReadAndPublish(ReadPlan plan)
    {
        OverviewTelemetry? data = null;
        OverviewSample? result = null;
        try
        {
            if (plan.Generation == DisplayTopology.GpuGeneration)
                data = plan.Source.ReadOverviewTelemetry(plan.SampleGpu, plan.DiscreteDeclared);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("overview.telemetry.read", error, throttle: true);
        }
        finally
        {
            lock (gate)
            {
                lastCompletion = DateTimeOffset.UtcNow;
                if (data != null && data.TopologyGeneration == plan.Generation && plan.Generation == DisplayTopology.GpuGeneration)
                {
                    result = sample = new(data, lastCompletion);
                    sampledPlan = plan;
                }
                pending = null;
                pendingPlan = default;
            }
        }

        return result;
    }
}
