namespace OpenRevo.ResidentProtocol;

public enum PanelPresentationDemand { Hidden, Controls, Overview, Mini, MiniGpu, Lighting }

public sealed record PanelDetailedTelemetry(PanelEcTelemetry? Ec, PanelThermalTelemetry? Temperature,
    PanelOverviewTelemetry? Overview, PanelOverviewPower? Power, PanelSensorPlan? Sampling = null);

public sealed record PanelSensorPlan(DateTimeOffset At, PanelPresentationDemand Demand, ulong Generation,
    bool AllowGpu, bool SampleGpu, bool? Discrete, int? CoreCpu, bool ProbeCaps);

public sealed record PanelEcTelemetry(int? CpuFan, int? GpuFan, int? BatteryLimit, bool? FanBoost, string Status);
public sealed record PanelThermalTelemetry(int? Cpu, int? Gpu, string CpuSource);
public sealed record PanelOverviewTelemetry(DateTimeOffset At, bool Fresh, bool Reading,
    PanelOverviewCpu Cpu, PanelOverviewGpu? Gpu);
public sealed record PanelOverviewCpu(int? Mhz, int? Usage, double? PowerW, bool PowerEstimated);
public sealed record PanelOverviewGpu(int? Temperature, int? CoreMhz, int? MemoryMhz, int? Usage,
    ulong? MemoryTotalBytes, ulong? MemoryUsedBytes, double? PowerW, string? AdapterName,
    string? AdapterKind, ulong? SharedLimitBytes, ulong? SharedUsedBytes);
public sealed record PanelOverviewPower(int Mode, int? Pl1Watts, int? TgpWatts, int? BaseTgpWatts,
    int? MaximumBoostWatts, string Detail, DateTimeOffset At, PanelGpuTarget? GpuTarget = null);
public sealed record PanelGpuTarget(int? BaseOffset, bool? BoostOn, int? BoostWatts);
