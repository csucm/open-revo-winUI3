using OpenRevo.Core.Hal;
using System.Diagnostics;
using System.Text.Json;

namespace OpenRevo.Core;

public sealed record TrayTelemetryInputs(DeviceIdentity Identity, EcSnapshot Ec, Task<EcSnapshot>? PendingEc,
    DateTimeOffset EcAt, SystemSnapshot? System, TemperatureReading? Temperature, DateTimeOffset TemperatureAt);

public sealed record ResidentPanelRead(EcSnapshot? Ec, TemperatureReading? Temperature, OverviewReadState Overview,
    OverviewPowerTargets? PowerTargets, bool AllowOverviewGpu, int GpuGeneration);

public sealed class ApplicationTelemetryRuntime : IDisposable
{
    private static readonly TimeSpan ResidentSamplingIdle = TimeSpan.FromSeconds(10);
    private readonly ApplicationHardwareRuntime runtime;
    private readonly Telemetry trayCpu = new();
    private readonly TrayTelemetryCache tray = new();
    private readonly Lazy<DeviceIdentity> identity = new(DeviceIdentity.Read);
    private DateTimeOffset lastTrayRequest;
    private long lastResidentSampling;
    // Shared only by the resident panel and its tray. The application queue
    // serializes the sensor reads with each other and with hardware commands.
    private EcSnapshot? residentEc;
    private TemperatureReading? residentTemperature;
    private OverviewPowerTargets? residentPower;
    private DateTimeOffset residentEcAt, residentTemperatureAt, residentPowerAt;
    private bool residentMiniGpu, residentTemperatureIncludedGpu;
    private int? residentGpuGeneration;
    private int? residentPowerMode;
    private IResidentTelemetrySource? residentSampling;

    // Set once by the resident entry, before starting any callers. Legacy UI
    // continues to use its existing in-process source.
    public void UseResidentSamplingSource(IResidentTelemetrySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref residentSampling, source, null) != null)
            throw new InvalidOperationException("Resident sampling source is already assigned.");
    }

    private IOverviewTelemetrySource? ResidentOverview => residentSampling ?? (IOverviewTelemetrySource?)runtime.Bridge;
    public int? ResidentSamplingProcessId => residentSampling?.ProcessId;
    public object? ResidentGpuTelemetryDiagnostics => residentSampling != null ?
        residentSampling.GpuTelemetryDiagnostics : runtime.Bridge?.GpuTelemetryDiagnostics;

    internal ApplicationTelemetryRuntime(ApplicationHardwareRuntime runtime) => this.runtime = runtime;

    public Telemetry Foreground { get; } = new();
    public TemperatureSensors Temperatures { get; } = new();
    public OverviewTelemetryCoordinator Overview { get; } = new();

    public Task<TrayTelemetrySample?> ReadTrayAsync(bool ecEnabled, TrayTelemetryInputs? inputs = null) =>
        ReadTrayAsync(ecEnabled, inputs, resident: false, null);

    public Task<TrayTelemetrySample?> ReadResidentTrayAsync(SystemSnapshot? system, Action<string>? traceStage = null) =>
        ReadTrayAsync(ecEnabled: true, null, resident: true, system, traceStage);

    private async Task<TrayTelemetrySample?> ReadTrayAsync(bool ecEnabled, TrayTelemetryInputs? inputs,
        bool resident, SystemSnapshot? system, Action<string>? traceStage = null)
    {
        if (!await runtime.Operations.EnterAsync(wait: false).ConfigureAwait(false)) return null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastTrayRequest is { TotalSeconds: >= 0 and < 5 }) return null;
            lastTrayRequest = now;
            try { return await tray.RequestAsync(() => Task.Run(() => ReadTrayCoreAsync(ecEnabled, inputs, now, resident, system, traceStage))).ConfigureAwait(false); }
            finally { if (resident) Volatile.Write(ref lastResidentSampling, Stopwatch.GetTimestamp()); }
        }
        finally { runtime.Operations.Release(); }
    }

    private async Task<TrayTelemetrySample> ReadTrayCoreAsync(bool ecEnabled, TrayTelemetryInputs? inputs, DateTimeOffset now,
        bool resident, SystemSnapshot? system, Action<string>? traceStage)
    {
        traceStage?.Invoke("begin");
        bool readEc = ecEnabled && runtime.Hardware.Active;
        if (resident) RefreshResidentTopology(DisplayTopology.GpuGeneration);
        var cachedCpu = resident ? system : inputs?.System;
        bool cpuFresh = cachedCpu?.CpuPercent != null && now - cachedCpu.At < TimeSpan.FromSeconds(4);
        if (!cpuFresh) trayCpu.ReadCpuPercent();
        traceStage?.Invoke("cpu-topology");
        var fans = inputs?.Ec;
        var pending = inputs?.PendingEc;
        bool freshEc = inputs != null && pending == null && now - inputs.EcAt < TimeSpan.FromSeconds(6);
        if (resident) fans = readEc ? ReadResidentEc(now) : null;
        else if (readEc && !freshEc)
        {
            try { fans = pending != null ? await pending.ConfigureAwait(false) : EcReader.Read(inputs?.Identity ?? identity.Value); }
            catch { fans = null; }
        }
        traceStage?.Invoke("ec");
        var cachedTemperature = resident ? residentTemperature : inputs?.Temperature;
        DateTimeOffset temperatureAt = resident ? residentTemperatureAt : inputs?.TemperatureAt ?? default;
        bool tempFresh = cachedTemperature != null && Fresh(temperatureAt, now, resident ? 6 : 4);
        var temps = tempFresh ? cachedTemperature! : resident
            ? CaptureResidentTemperature(miniGpu: false, readCore: readEc, traceStage) : Temperatures.Read(false);
        traceStage?.Invoke("temperature");
        var (readGpu, gpuSample) = (resident ? ResidentOverview : runtime.Bridge) is not { } bridge
            ? (false, null)
            : await Overview.ReadTrayGpuAsync(bridge, CoreMachine.Current.DiscreteGpuDeclared, readEc, fans?.GpuFan).ConfigureAwait(false);
        traceStage?.Invoke("overview-gpu");
        if (!cpuFresh) await Task.Delay(350).ConfigureAwait(false);
        if (gpuSample?.Data.TopologyGeneration != DisplayTopology.GpuGeneration) gpuSample = null;
        var at = cpuFresh ? cachedCpu!.At : DateTimeOffset.UtcNow;
        if (tempFresh && temperatureAt < at) at = temperatureAt;
        if (gpuSample is { } gpu && gpu.At < at) at = gpu.At;
        var result = new TrayTelemetrySample(at, temps.Cpu, cpuFresh ? cachedCpu!.CpuPercent : trayCpu.ReadCpuPercent(),
            gpuSample?.Data.Gpu.Temperature, gpuSample?.Data.Gpu.Usage, readGpu);
        traceStage?.Invoke("complete");
        return result;
    }

    public async Task<ResidentPanelRead?> ReadResidentPanelAsync(bool overview, bool miniGpu)
    {
        if (!await runtime.Operations.EnterAsync(wait: false).ConfigureAwait(false)) return null;
        try
        {
            int generation = DisplayTopology.GpuGeneration;
            RefreshResidentTopology(generation);
            var now = DateTimeOffset.UtcNow;
            var admission = CoreMachine.Current;
            EcSnapshot? ec = admission.Ready ? ReadResidentEc(now) : null;
            bool onAc = Telemetry.ReadAc() == true;
            bool activeDiscrete = admission.DiscreteGpuDeclared != false && onAc && ec?.GpuFan is > 0;
            var temperature = ReadResidentTemperature(now, miniGpu && activeDiscrete);
            bool allowOverviewGpu = admission.DiscreteGpuDeclared == false || activeDiscrete;
            OverviewReadState state = default;
            OverviewPowerTargets? power = null;
            if (overview)
            {
                var pending = Overview.RequestForeground(ResidentOverview, admission.DiscreteGpuDeclared, onAc, ec?.GpuFan);
                if (pending != null) await pending.ConfigureAwait(false);
                state = Overview.ReadState();
                int? mode = runtime.Hardware.CurrentMode;
                if (admission.Ready && (!Fresh(residentPowerAt, now, 6) || residentPowerMode != mode))
                {
                    residentPowerAt = now;
                    residentPowerMode = mode;
                    try { residentPower = runtime.Hardware.ProbeOverviewPowerTargets(); }
                    catch (Exception error)
                    {
                        residentPower = null;
                        DiagnosticLog.Failure("resident.overview.power_targets_failed", error, throttle: true);
                    }
                }
                if (admission.Ready && residentPower?.Mode == runtime.Hardware.CurrentMode) power = residentPower;
            }
            else Overview.SetForegroundDemand(null);
            if (generation != DisplayTopology.GpuGeneration) return null;
            return new(ec, temperature, state, power, allowOverviewGpu, generation);
        }
        finally
        {
            Volatile.Write(ref lastResidentSampling, Stopwatch.GetTimestamp());
            runtime.Operations.Release();
        }
    }

    public async Task EndResidentPanelReadsAsync()
    {
        Overview.SetForegroundDemand(null);
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) return;
        try
        {
            Temperatures.ReleaseGpu();
            residentSampling?.ReleaseGpu();
            if (residentTemperature is { Gpu: not null } reading) residentTemperature = reading with { Gpu = null };
            residentTemperatureIncludedGpu = residentMiniGpu = false;
        }
        finally { runtime.Operations.Release(); }
    }

    public async Task ReleaseIdleResidentSamplingAsync()
    {
        if (!ResidentSamplingExpired() || !await runtime.Operations.EnterAsync(wait: false).ConfigureAwait(false)) return;
        try
        {
            if (!ResidentSamplingExpired()) return;
            Temperatures.ReleaseSamplingResources();
            if (residentSampling != null) residentSampling.ReleaseSamplingResources();
            else runtime.Bridge?.ReleaseOverviewSamplingResources();
            Volatile.Write(ref lastResidentSampling, 0);
            residentTemperature = null;
            residentTemperatureIncludedGpu = residentMiniGpu = false;
            DiagnosticLog.Record("resident.telemetry.idle_released");
        }
        finally { runtime.Operations.Release(); }
    }

    private bool ResidentSamplingExpired()
    {
        long last = Volatile.Read(ref lastResidentSampling);
        return last != 0 && Stopwatch.GetElapsedTime(last) >= ResidentSamplingIdle;
    }

    private EcSnapshot ReadResidentEc(DateTimeOffset now)
    {
        if (residentEc != null && Fresh(residentEcAt, now, 6)) return residentEc;
        residentEc = EcReader.Read(identity.Value);
        residentEcAt = DateTimeOffset.UtcNow;
        return residentEc;
    }

    private void RefreshResidentTopology(int generation)
    {
        if (residentGpuGeneration == generation) return;
        residentGpuGeneration = generation;
        if (residentMiniGpu)
        {
            Temperatures.ReleaseGpu();
            residentSampling?.ReleaseGpu();
        }
        residentTemperatureIncludedGpu = residentMiniGpu = false;
        residentEcAt = residentTemperatureAt = residentPowerAt = default;
    }

    private TemperatureReading ReadResidentTemperature(DateTimeOffset now, bool miniGpu)
    {
        bool changed = residentMiniGpu != miniGpu;
        if (!miniGpu && residentMiniGpu)
        {
            Temperatures.ReleaseGpu();
            residentSampling?.ReleaseGpu();
        }
        residentMiniGpu = miniGpu;
        if (!changed && residentTemperature is { } cached && Fresh(residentTemperatureAt, now, 6) &&
            (!miniGpu || residentTemperatureIncludedGpu)) return cached;
        return CaptureResidentTemperature(miniGpu);
    }

    private TemperatureReading CaptureResidentTemperature(bool miniGpu, bool readCore = true, Action<string>? traceStage = null)
    {
        int? coreCpu = null;
        try
        {
            if (readCore && runtime.Bridge?.Read(24) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int cpu))
                coreCpu = cpu;
        }
        catch (Exception error) { DiagnosticLog.Failure("temperature.core", error, throttle: true); }
        traceStage?.Invoke("core-temperature");
        var reading = residentSampling != null ?
            // Hardware CPU temperature requires no Windows sampling process.
            coreCpu.HasValue && !miniGpu ? new TemperatureReading(coreCpu, null, "Core 硬件温度") :
                residentSampling.ReadTemperature(miniGpu, coreCpu) : Temperatures.Read(miniGpu, coreCpu);
        if (!coreCpu.HasValue) traceStage?.Invoke("windows-temperature");
        residentTemperature = reading;
        residentTemperatureAt = DateTimeOffset.UtcNow;
        residentTemperatureIncludedGpu = miniGpu;
        return reading;
    }

    private static bool Fresh(DateTimeOffset at, DateTimeOffset now, int seconds) =>
        now - at is { TotalSeconds: >= 0 } age && age < TimeSpan.FromSeconds(seconds);

    public void Dispose()
    {
        Overview.SetForegroundDemand(null);
        Temperatures.Dispose();
        residentSampling?.Dispose();
    }
}
