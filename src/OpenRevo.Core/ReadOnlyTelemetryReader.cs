using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenRevo.Core;

public interface IOverviewTelemetrySource
{
    OverviewTelemetry ReadOverviewTelemetry(bool sampleGpu, bool? discreteDeclared = null);
}

// Resident-only sampling has an independently releasable lifetime. It never owns
// a control session, a configuration store or a hardware command queue.
public interface IResidentTelemetrySource : IOverviewTelemetrySource, IDisposable
{
    int? ProcessId { get; }
    object? GpuTelemetryDiagnostics { get; }
    TemperatureReading ReadTemperature(bool sampleGpu, int? coreCpu);
    void ReleaseGpu();
    void ReleaseSamplingResources();
}

/// <summary>Only bridge query 10 and Windows sensors; no application initialization.</summary>
public sealed class ReadOnlyTelemetryReader : IOverviewTelemetrySource, IDisposable
{
    private readonly WindowsGpuTelemetry windowsGpu = new();
    private readonly TemperatureSensors temperatures = new();
    private nint library;
    private readonly Query query;
    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Query(uint operation, uint mode, nint buffer, nuint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResetMetrics();

    public ReadOnlyTelemetryReader(string absoluteDllPath)
    {
        if (!Path.IsPathFullyQualified(absoluteDllPath)) throw new ArgumentException("DLL path must be absolute.");
        library = NativeLibrary.Load(absoluteDllPath);
        try
        {
            if (Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(library, "or_native_abi_version"))() != 1)
                throw new NotSupportedException("Unsupported monitoring bridge ABI.");
            query = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(library, "or_native_query"));
        }
        catch { NativeLibrary.Free(library); library = 0; throw; }
    }

    public TemperatureReading ReadTemperature(bool sampleGpu, int? coreCpu) => temperatures.Read(sampleGpu, coreCpu);
    public void ReleaseGpu() => temperatures.ReleaseGpu();
    public object GpuTelemetryDiagnostics => windowsGpu.Diagnostics;

    public OverviewTelemetry ReadOverviewTelemetry(bool sampleGpu, bool? discreteDeclared = null)
    {
        ObjectDisposedException.ThrowIf(library == 0, this);
        int generation = DisplayTopology.GpuGeneration;
        // A fixed bounded buffer avoids executing an expensive query twice.
        const int capacity = 64 * 1024;
        nint buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            int length = query(10, sampleGpu && discreteDeclared != false ? 1u : 0u, buffer, capacity);
            if (length <= 1 || length > capacity || Marshal.ReadByte(buffer, length - 1) != 0)
                throw new IOException("Core monitoring response exceeds its bounds.");
            byte[] bytes = new byte[length - 1];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var result = JsonSerializer.Deserialize<OverviewTelemetry>(bytes, SnakeCase)
                ?? throw new IOException("Core monitoring response is unavailable.");
            return Project(windowsGpu, result, sampleGpu, discreteDeclared, generation);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static OverviewTelemetry Project(WindowsGpuTelemetry windowsGpu, OverviewTelemetry result,
        bool sampleGpu, bool? discreteDeclared, int generation)
    {
        result = result with { TopologyGeneration = generation };
        if (!sampleGpu) return result with { GpuSource = "GPU sampling inactive" };
        var gpu = result.Gpu;
        if (gpu.Usage != null || gpu.Temperature != null || gpu.CoreMhz != null ||
            gpu.MemoryMhz != null || gpu.MemoryTotalBytes != null || gpu.MemoryUsedBytes != null || gpu.PowerW != null)
        {
            // Keep NVML index 0 separate from Windows LUID identities.
            string machineKind = windowsGpu.InspectNvmlMachine(discreteDeclared);
            return result with
            {
                GpuSource = "NVIDIA NVML device index 0",
                Gpu = gpu with { MachineKind = machineKind, SampledAt = DateTimeOffset.UtcNow }
            };
        }
        var sample = windowsGpu.Read(discreteDeclared);
        if (sample == null)
            return result with { GpuSampled = true, GpuSource = "GPU adapter or telemetry unavailable" };
        return result with
        {
            GpuSampled = true,
            GpuSource = "Windows WDDM adapter counters, DXGI capacities and matched AMD ADLX sensors",
            Gpu = new(sample.Temperature, sample.CoreMhz, sample.MemoryMhz, sample.Usage,
                sample.Adapter.DedicatedBytes > 0 ? sample.Adapter.DedicatedBytes : null, sample.DedicatedUsedBytes, sample.PowerW)
            {
                AdapterName = sample.Adapter.Name, AdapterLuid = sample.Adapter.Luid,
                AdapterKind = sample.Kind.ToString(), MachineKind = sample.MachineKind, SampledAt = sample.At,
                SharedLimitBytes = sample.Adapter.SharedBytes > 0 ? sample.Adapter.SharedBytes : null,
                SharedUsedBytes = sample.SharedUsedBytes
            }
        };
    }

    public void Dispose()
    {
        if (library == 0) return;
        temperatures.Dispose();
        windowsGpu.Dispose();
        Marshal.GetDelegateForFunctionPointer<ResetMetrics>(NativeLibrary.GetExport(library, "or_native_metrics_reset"))();
        // This process has no control services to stop. The Rust metrics cache
        // and any driver-owned allocations end with the sampling process.
        NativeLibrary.Free(library);
        library = 0;
    }
}
