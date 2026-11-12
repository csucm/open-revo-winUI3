using System.Runtime.InteropServices;

namespace OpenRevo.Core;

internal sealed record WindowsGpuSample(WindowsGpuAdapter Adapter, WindowsGpuKind Kind,
    string MachineKind, int? Usage, ulong? SharedUsedBytes, ulong? DedicatedUsedBytes, DateTimeOffset At)
{
    internal int? CoreMhz { get; init; }
    internal int? MemoryMhz { get; init; }
    internal int? Temperature { get; init; }
    internal double? PowerW { get; init; }
    internal string? CoreClockSource { get; init; }
    internal string? MemoryClockSource { get; init; }
}

internal sealed class WindowsGpuTelemetry : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDoubleNoCap = 0x8200;
    private const uint FormatBytes = FormatDoubleNoCap | 0x1000; // PDH_FMT_NOSCALE
    private readonly WindowsGpuIdentity identity = new();
    private readonly AmdGpuClocks amdClocks = new();
    private WindowsGpuAdapter[] candidates = [];
    private WindowsGpuAdapter? selected;
    private WindowsGpuKind selectedKind;
    private string machineKind = "unknown";
    private bool observedHybrid;
    private string selection = "not-requested";
    private string? displayLuid, displayTargetLuid;
    private string samplingState = "not-requested";
    private PerformanceCounterQuery? query;
    private nint engine, shared, dedicated;
    private uint? queryStatus, engineStatus, sharedStatus, dedicatedStatus;
    private int generation = -1, emptySamples;
    private bool? declaration;
    private bool lastWasNvml;
    private DateTimeOffset retryAt, lastCollection;
    private WindowsGpuSample? last;
    private uint? graphicsNode;
    private bool inspectedClockNodes;
    private int? clockStatus;
    private DateTimeOffset retryClocksAt;

    // The owning Core instance serializes sampling, diagnostics and disposal.
    internal object Diagnostics => new
    {
        candidates = candidates.Select(a => new
        {
            a.Name, a.Luid, a.Vendor, a.Device, a.Subsystem, a.Flags,
            kind = a.Kind.ToString(), a.Physical, a.AdapterType, a.TypeStatus, a.DxCoreHardware,
            a.DxCoreIntegrated, a.DevicePathStatus, a.Evidence, a.DedicatedBytes, a.DedicatedSystemBytes, a.SharedBytes,
            pnpHardwareId = a.DevicePath?.Split('#') is { Length: >= 2 } parts ? string.Join("#", parts.Take(2)) : null
        }).ToArray(),
        machineKind, declaration, selection, samplingState, displayLuid, displayTargetLuid, adapter = selected?.Name, luid = selected?.Luid,
        enumerationStatus = identity.EnumerationStatus, queryStatus,
        engine = new { source = "PDH GPU Engine / busiest engine", status = engineStatus, usage = last?.Usage },
        shared = new { source = "PDH GPU Adapter Memory / Shared Usage", scope = "adapter", status = sharedStatus, used = last?.SharedUsedBytes, limit = selected?.SharedBytes },
        dedicated = new { source = "PDH GPU Adapter Memory / Dedicated Usage", scope = "adapter", status = dedicatedStatus, used = last?.DedicatedUsedBytes, capacity = selected?.DedicatedBytes },
        clocks = new { d3dkmt = new { node = graphicsNode, status = clockStatus }, adlx = amdClocks.Diagnostics,
            coreMhz = last?.CoreMhz, memoryMhz = last?.MemoryMhz, coreSource = last?.CoreClockSource, memorySource = last?.MemoryClockSource },
        temperature = new { source = last?.Temperature != null ? "AMD ADLX / GPUTemperature / Celsius" : null, value = last?.Temperature },
        power = new { source = last?.PowerW != null ? "AMD ADLX / GPUPower / watts" : null, value = last?.PowerW },
        capacitySource = "DXGI adapter description", sampledAt = last?.At
    };

    internal string InspectNvmlMachine(bool? discreteDeclared)
    {
        RefreshIdentity(discreteDeclared, nvml: true, DateTimeOffset.UtcNow);
        samplingState = "nvml-primary";
        return machineKind;
    }

    private void RefreshIdentity(bool? discreteDeclared, bool nvml, DateTimeOffset now)
    {
        int currentGeneration = DisplayTopology.GpuGeneration;
        if (generation != currentGeneration || declaration != discreteDeclared || lastWasNvml != nvml ||
            selected != null && !identity.IsCurrent)
        {
            Reset();
            retryAt = default;
            generation = currentGeneration;
            declaration = discreteDeclared;
            lastWasNvml = nvml;
        }
        if (selected == null && now >= retryAt && (!nvml || !identity.IsCurrent))
        {
            retryAt = now.AddSeconds(30);
            Discover(discreteDeclared, nvml);
        }
    }

    internal WindowsGpuSample? Read(bool? discreteDeclared)
    {
        var now = DateTimeOffset.UtcNow;
        RefreshIdentity(discreteDeclared, nvml: false, now);
        if (selected == null)
        {
            samplingState = "no-selected-adapter";
            return null;
        }

        if (query != null && now - lastCollection > TimeSpan.FromSeconds(10)) CloseQuery();
        if (query == null && (last == null || now >= retryAt))
        {
            retryAt = now.AddSeconds(30);
            OpenQuery();
        }

        int? usage = null;
        ulong? sharedUsed = null, dedicatedUsed = null;
        samplingState = "query-unavailable";
        if (query is { } activeQuery)
        {
            bool warmed = lastCollection != default;
            queryStatus = activeQuery.Collect();
            if (queryStatus == 0)
            {
                lastCollection = now;
                if (engine != 0 && warmed) usage = ReadEngine(activeQuery, selected.Luid);
                if (shared != 0) sharedUsed = ReadMemory(activeQuery, shared, selected.Luid, selected.SharedBytes, ref sharedStatus);
                if (dedicated != 0) dedicatedUsed = ReadMemory(activeQuery, dedicated, selected.Luid, selected.DedicatedBytes, ref dedicatedStatus);
                samplingState = !warmed ? "first-observation" :
                    usage.HasValue || sharedUsed.HasValue || dedicatedUsed.HasValue ? "sampled" : "no-matching-valid-counters";
                if (warmed && usage == null && sharedUsed == null && dedicatedUsed == null)
                {
                    if (++emptySamples >= 3)
                    {
                        CloseQuery();
                        retryAt = now.AddSeconds(30);
                        emptySamples = 0;
                    }
                }
                else emptySamples = 0;
            }
            else
            {
                CloseQuery();
                samplingState = "collect-failed";
                retryAt = now.AddSeconds(30);
            }
        }
        last = new(selected, selectedKind, machineKind, usage, sharedUsed, dedicatedUsed, now);
        if (selectedKind == WindowsGpuKind.Integrated && now >= retryClocksAt)
        {
            var (core, memory, status) = identity.ReadIntegratedClocks(selected, ref graphicsNode, ref inspectedClockNodes);
            clockStatus = status;
            last = last with
            {
                CoreMhz = core, MemoryMhz = memory,
                CoreClockSource = core.HasValue ? "D3DKMT 3D node" : null,
                MemoryClockSource = memory.HasValue ? "D3DKMT adapter performance data" : null
            };
            if (core == null && memory == null)
            {
                retryClocksAt = now.AddSeconds(30);
                if (graphicsNode == null) inspectedClockNodes = false;
            }
        }
        if (selectedKind == WindowsGpuKind.Integrated && selected.Vendor == 0x1002)
        {
            var sensors = amdClocks.Read(selected, candidates, now);
            last = last with
            {
                CoreMhz = last.CoreMhz ?? sensors.CoreMhz,
                MemoryMhz = last.MemoryMhz ?? sensors.MemoryMhz,
                Temperature = sensors.Temperature,
                PowerW = sensors.PowerW,
                CoreClockSource = last.CoreClockSource ?? (sensors.CoreMhz.HasValue ? "AMD ADLX" : null),
                MemoryClockSource = last.MemoryClockSource ?? (sensors.MemoryMhz.HasValue ? "AMD ADLX" : null)
            };
        }
        return last;
    }

    private void Discover(bool? discreteDeclared, bool nvml)
    {
        candidates = identity.Enumerate();
        var visible = candidates.Where(a => a.Kind != WindowsGpuKind.Virtual).ToArray();
        var physical = visible.Where(a => a.Physical).ToArray();
        bool discrete = physical.Any(a => a.Kind == WindowsGpuKind.Discrete);
        bool integrated = physical.Any(a => a.Kind == WindowsGpuKind.Integrated);
        observedHybrid |= discrete && integrated;
        machineKind = observedHybrid ? "hybrid" : discrete ? "discrete-visible" :
            discreteDeclared == true && integrated ? "discrete-declared-integrated-visible" : "unknown";

        if (nvml || visible.Any(a => a.Vendor == 0x10de))
        {
            selection = "nvidia-visible-use-existing-nvml-policy";
            return;
        }

        if (discreteDeclared == false && discrete)
        {
            selection = "discrete-adapter-conflicts-with-integrated-only-declaration";
            return;
        }
        if ((discreteDeclared == true || observedHybrid) && !discrete)
        {
            selection = "discrete-telemetry-target-not-confirmed";
            return;
        }

        // A display path can identify a rendering adapter on a pure iGPU
        // machine. On a hybrid machine it would identify the output side.
        if (discreteDeclared == false)
        {
            var eligible = physical.Where(a => a.Kind == WindowsGpuKind.Integrated).ToArray();
            if (eligible.Length == 1 && visible.Length == 1)
            {
                Select(eligible[0], WindowsGpuKind.Integrated, "single-confirmed-integrated-adapter");
                return;
            }
            try
            {
                var panel = DisplayTopology.Internal();
                displayLuid = panel.SourceAdapterLuid;
                displayTargetLuid = panel.TargetAdapterLuid;
                if (displayLuid != null && displayLuid == displayTargetLuid)
                {
                    var match = physical.SingleOrDefault(a => a.Luid == displayLuid);
                    if (match != null && (match.Kind == WindowsGpuKind.Integrated ||
                        match.Kind == WindowsGpuKind.Unknown && !match.ArchitectureConflict))
                    {
                        Select(match, WindowsGpuKind.Integrated, "matched-pure-integrated-manifest-and-internal-display-luid");
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                selection = "internal-display-path-unavailable";
                return;
            }
            selection = "integrated-adapter-identity-ambiguous";
            return;
        }

        var discreteAdapters = physical.Where(a => a.Kind == WindowsGpuKind.Discrete).ToArray();
        if (discreteAdapters.Length == 1 &&
            visible.All(a => a.Kind is WindowsGpuKind.Integrated or WindowsGpuKind.Discrete))
        {
            Select(discreteAdapters[0], WindowsGpuKind.Discrete, "single-confirmed-discrete-telemetry-target");
            return;
        }
        if (visible.Length == 1 && physical.Length == 1 && physical[0].Kind == WindowsGpuKind.Integrated)
        {
            Select(physical[0], physical[0].Kind, "single-confirmed-hardware-adapter");
            return;
        }
        selection = "windows-adapter-identity-ambiguous";
    }

    private void Select(WindowsGpuAdapter adapter, WindowsGpuKind kind, string reason)
    {
        selected = adapter;
        selectedKind = kind;
        selection = reason;
        if (kind == WindowsGpuKind.Integrated && declaration == false) machineKind = "integrated-only";
    }

    private void OpenQuery()
    {
        queryStatus = PerformanceCounterQuery.Open(out query);
        if (query == null) return;
        engine = AddCounter(query, @"\GPU Engine(*)\Utilization Percentage", out engineStatus);
        shared = AddCounter(query, @"\GPU Adapter Memory(*)\Shared Usage", out sharedStatus);
        dedicated = AddCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", out dedicatedStatus);
        if (engine == 0 && shared == 0 && dedicated == 0) CloseQuery();
    }

    private static nint AddCounter(PerformanceCounterQuery query, string path, out uint? status)
    {
        status = query.Add(path, out nint counter);
        return status == 0 ? counter : 0;
    }

    private int? ReadEngine(PerformanceCounterQuery query, string luid)
    {
        var entries = ReadCounter(query, engine, ref engineStatus);
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            int start = name.IndexOf("_" + luid + "_phys_", StringComparison.OrdinalIgnoreCase);
            if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) || start < 0) continue;
            string engineName = name[(start + 1 + luid.Length)..];
            engines[engineName] = engines.GetValueOrDefault(engineName) + value;
        }
        return engines.Count == 0 ? null : (int)Math.Round(Math.Clamp(engines.Values.Max(), 0, 100));
    }

    private static ulong? ReadMemory(PerformanceCounterQuery query, nint counter, string luid, ulong capacity, ref uint? status)
    {
        if (capacity == 0) return null;
        double? result = null;
        foreach (var (name, value) in ReadCounter(query, counter, ref status, FormatBytes))
        {
            string prefix = luid + "_phys_";
            bool matches = name.Equals(luid, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, null, out _);
            if (!matches) continue;
            if (result.HasValue || value > capacity || value != Math.Truncate(value)) return null;
            result = value;
        }
        return result.HasValue ? (ulong)result.Value : null;
    }

    private static List<(string Name, double Value)> ReadCounter(PerformanceCounterQuery query,
        nint counter, ref uint? status, uint format = FormatDoubleNoCap)
    {
        var entries = new List<(string, double)>();
        uint bytes = 0;
        status = query.Read(counter, format, ref bytes, out _, 0);
        if (status != MoreData || bytes == 0 || bytes > 4 * 1024 * 1024) return entries;
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            uint allocated = bytes;
            status = query.Read(counter, format, ref bytes, out uint count, buffer);
            if (status != 0 || bytes > allocated) return entries;
            int size = Marshal.SizeOf<CounterItem>();
            if ((long)count * size > bytes) return entries;
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Number) || item.Value.Number < 0) continue;
                long offset = item.Name.ToInt64() - buffer.ToInt64();
                if (offset < (long)count * size || offset > bytes - sizeof(char) || offset % sizeof(char) != 0) continue;
                int maxChars = Math.Min(256, (int)((bytes - offset) / sizeof(char)));
                int length = 0;
                while (length < maxChars && Marshal.ReadInt16(item.Name, length * sizeof(char)) != 0) length++;
                if (length < maxChars && Marshal.PtrToStringUni(item.Name, length) is string name)
                    entries.Add((name, item.Value.Number));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return entries;
    }

    private void CloseQuery()
    {
        query?.Dispose();
        query = null;
        engine = shared = dedicated = 0;
        lastCollection = default;
    }

    private void Reset()
    {
        CloseQuery();
        amdClocks.Dispose();
        identity.Dispose();
        selected = null;
        displayLuid = displayTargetLuid = null;
        queryStatus = engineStatus = sharedStatus = dedicatedStatus = null;
        last = null;
        emptySamples = 0;
        graphicsNode = null;
        inspectedClockNodes = false;
        clockStatus = null;
        retryClocksAt = default;
    }

    public void Dispose() => Reset();

    internal void ReleaseSamplingResources()
    {
        Reset();
        generation = -1;
        candidates = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Number; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public CounterValue Value; }

}
