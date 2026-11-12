using System.Runtime.InteropServices;

namespace OpenRevo.Core;

internal sealed record AmdGpuSensors(int? CoreMhz, int? MemoryMhz, int? Temperature, double? PowerW);

// AMD ADLX C ABI, from SDK/Include/ADLX.h, ISystem.h and IPerformanceMonitoring.h.
// Only the driver-installed x64 library is loaded. The owning sampler serializes calls.
internal sealed class AmdGpuClocks : IDisposable
{
    private nint library, gpu, performance;
    private Terminate? terminate;
    private DateTimeOffset retryAt;
    private string state = "not-requested";
    private int? result, coreResult, memoryResult, temperatureResult;
    private int? temperatureSupportResult;
    private bool? temperatureSupported;
    private int? powerResult, powerSupportResult;
    private bool? powerSupported;

    internal object Diagnostics => new { source = "AMD ADLX current GPU metrics", state, result, coreResult, memoryResult,
        temperatureSupported, temperatureSupportResult, temperatureResult, powerSupported, powerSupportResult, powerResult };

    internal AmdGpuSensors Read(WindowsGpuAdapter adapter, WindowsGpuAdapter[] candidates, DateTimeOffset now)
    {
        if (now < retryAt) return new(null, null, null, null);
        if (gpu == 0 && !Connect(adapter, candidates))
        {
            Disconnect();
            retryAt = now.AddSeconds(30);
            return new(null, null, null, null);
        }

        result = Method<GetMetrics>(performance, 18)(performance, gpu, out nint metrics); // GetCurrentGPUMetrics
        if (result != 0 || metrics == 0)
        {
            state = "metrics-unavailable";
            Release(metrics);
            Disconnect();
            retryAt = now.AddSeconds(30);
            return new(null, null, null, null);
        }
        try
        {
            coreResult = Method<GetClock>(metrics, 5)(metrics, out int core);
            memoryResult = Method<GetClock>(metrics, 6)(metrics, out int memory);
            double temperature = 0;
            temperatureResult = temperatureSupported == true ? Method<GetMetric>(metrics, 7)(metrics, out temperature) : null;
            double power = 0;
            powerResult = powerSupported == true ? Method<GetMetric>(metrics, 9)(metrics, out power) : null;
            int? coreMhz = coreResult == 0 && core is > 0 and <= 20_000 ? core : null;
            int? memoryMhz = memoryResult == 0 && memory is > 0 and <= 20_000 ? memory : null;
            int? temperatureC = temperatureResult == 0 && temperature is > 0 and <= 125
                ? (int)Math.Round(temperature, MidpointRounding.AwayFromZero) : null;
            double? powerW = powerResult == 0 && power is >= 0 and <= 1_000 ? power : null;
            state = coreMhz.HasValue || memoryMhz.HasValue || temperatureC.HasValue || powerW.HasValue ? "sampled" : "sensor-fields-unavailable";
            if (coreMhz == null && memoryMhz == null && temperatureC == null && powerW == null) retryAt = now.AddSeconds(30);
            return new(coreMhz, memoryMhz, temperatureC, powerW);
        }
        finally { Release(metrics); }
    }

    private bool Connect(WindowsGpuAdapter adapter, WindowsGpuAdapter[] candidates)
    {
        result = coreResult = memoryResult = temperatureResult = null;
        temperatureSupportResult = null;
        temperatureSupported = null;
        powerResult = powerSupportResult = null;
        powerSupported = null;
        string? target = PciId(adapter.DevicePath);
        if (target == null) { state = "windows-pci-id-unavailable"; return false; }
        string hardwareId = HardwareId(target);
        bool uniqueHardwareId = candidates.Count(candidate => candidate.Kind != WindowsGpuKind.Virtual &&
            PciId(candidate.DevicePath) is { } id && HardwareId(id) == hardwareId) == 1 &&
            candidates.All(candidate => candidate.Kind == WindowsGpuKind.Virtual || candidate.Vendor != 0x1002 ||
                candidate.Device != adapter.Device || PciId(candidate.DevicePath) != null);

        state = "driver-library-unavailable";
        if (!NativeLibrary.TryLoad("amdadlx64.dll", typeof(AmdGpuClocks).Assembly, DllImportSearchPath.System32, out library))
            return false;
        state = "driver-exports-unavailable";
        if (!NativeLibrary.TryGetExport(library, "ADLXQueryFullVersion", out nint getVersion) ||
            !NativeLibrary.TryGetExport(library, "ADLXInitialize", out nint initialize) ||
            !NativeLibrary.TryGetExport(library, "ADLXTerminate", out nint end)) return false;

        state = "initialization-failed";
        result = Marshal.GetDelegateForFunctionPointer<QueryVersion>(getVersion)(out ulong version);
        if (result != 0 || version >> 48 == 0) return false;
        result = Marshal.GetDelegateForFunctionPointer<Initialize>(initialize)(version, out nint system);
        if (result != 0) return false;
        terminate = Marshal.GetDelegateForFunctionPointer<Terminate>(end);
        if (system == 0) return false;

        state = "gpu-list-unavailable";
        result = Method<GetInterface>(system, 1)(system, out nint list); // GetGPUs
        try
        {
            if (result != 0 || list == 0) return false;
            uint count = Method<ListSize>(list, 3)(list);
            if (count is 0 or > 32) return false;
            state = "gpu-pci-id-unmatched";
            for (uint i = 0; i < count; i++)
            {
                result = Method<GetGpuAt>(list, 11)(list, i, out nint item);
                try
                {
                    if (result != 0 || item == 0) { state = "gpu-enumeration-incomplete"; return false; }
                    result = Method<GetInterface>(item, 9)(item, out nint pnp);
                    string? id = result == 0 ? PciId(Marshal.PtrToStringAnsi(pnp)) : null;
                    if (id == null) { state = "gpu-pci-id-unavailable"; return false; }
                    if (id != target && !(uniqueHardwareId && id == hardwareId)) continue;
                    if (gpu != 0) { state = "gpu-pci-id-ambiguous"; return false; }
                    gpu = item;
                    item = 0; // Retain this reference until topology invalidation or disconnect.
                }
                finally { Release(item); }
            }
            if (gpu == 0) return false;
        }
        finally { Release(list); }

        state = "performance-service-unavailable";
        result = Method<GetInterface>(system, 9)(system, out performance); // GetPerformanceMonitoringServices
        if (result != 0 || performance == 0) return false;
        int supportResult = Method<GetMetrics>(performance, 21)(performance, gpu, out nint support); // GetSupportedGPUMetrics
        temperatureSupportResult = powerSupportResult = supportResult;
        try
        {
            if (supportResult == 0 && support != 0)
            {
                temperatureSupportResult = Method<GetSupported>(support, 6)(support, out byte supported);
                if (temperatureSupportResult == 0) temperatureSupported = supported != 0;
                powerSupportResult = Method<GetSupported>(support, 8)(support, out supported);
                if (powerSupportResult == 0) powerSupported = supported != 0;
            }
        }
        finally { Release(support); }
        return true;
    }

    private static string? PciId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string id = path.Trim();
        if (id.StartsWith(@"\\?\", StringComparison.Ordinal)) id = id[4..];
        string[] parts = id.Replace('#', '\\').ToUpperInvariant().Split('\\');
        if (parts.Length < 2 || parts[0] != "PCI" || !parts[1].StartsWith("VEN_1002&DEV_", StringComparison.Ordinal))
            return null;
        return string.Join("\\", parts.Take(3));
    }

    private static string HardwareId(string id)
    {
        int instance = id.IndexOf('\\', 4);
        return instance < 0 ? id : id[..instance];
    }

    private void Disconnect()
    {
        Release(performance);
        Release(gpu);
        performance = gpu = 0;
        terminate?.Invoke();
        terminate = null;
        if (library != 0) NativeLibrary.Free(library);
        library = 0;
    }

    public void Dispose()
    {
        Disconnect();
        retryAt = default;
        state = "not-requested";
        result = coreResult = memoryResult = temperatureResult = null;
        temperatureSupportResult = null;
        temperatureSupported = null;
        powerResult = powerSupportResult = null;
        powerSupported = null;
    }

    private static void Release(nint instance)
    {
        if (instance != 0) Method<ReleaseRef>(instance, 1)(instance);
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int QueryVersion(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize(ulong version, out nint system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Terminate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseRef(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ListSize(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGpuAt(nint self, uint index, out nint gpu);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetMetrics(nint self, nint gpu, out nint metrics);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetClock(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetMetric(nint self, out double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSupported(nint self, out byte supported);
}
