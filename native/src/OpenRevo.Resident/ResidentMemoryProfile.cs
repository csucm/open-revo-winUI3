using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using OpenRevo.Core;

namespace OpenRevo.Resident;

internal sealed partial class ResidentTray
{
    private async Task ObserveMemoryAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                var child = Volatile.Read(ref panel);
                DiagnosticLog.Record("resident.memory.sample", new
                {
                    PanelPid = child?.ProcessId,
                    SamplingPid = runtime.Telemetry.ResidentSamplingProcessId,
                    PanelAttached = child?.IsAttached == true,
                    Demand = panelCommands.PresentationDemand,
                    TraySampleAt = Volatile.Read(ref traySample)?.At,
                    GraphicsModules = new
                    {
                        Dxgi = GetModuleHandle("dxgi.dll") != 0,
                        DxCore = GetModuleHandle("dxcore.dll") != 0,
                        Nvml = GetModuleHandle("nvml.dll") != 0,
                        Pdh = GetModuleHandle("pdh.dll") != 0
                    },
                    Process = ProcessResources.Read(),
                    JitMethods = JitInfo.GetCompiledMethodCount(),
                    JitILBytes = JitInfo.GetCompiledILBytes(),
                    Regions = ReadCommittedRegions()
                });
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticLog.Failure("resident.memory.sample_failed", error); }
    }

    private static Action<string> CreateTrayMemoryTrace()
    {
        var seenModules = new HashSet<nint>();
        return stage =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var modules = process.Modules.Cast<ProcessModule>()
                    .Where(module => seenModules.Add(module.BaseAddress))
                    .Select(module => new { module.ModuleName, ImageBytes = module.ModuleMemorySize })
                    .ToArray();
                DiagnosticLog.Record("resident.memory.tray_stage", new
                {
                    Stage = stage,
                    Process = ProcessResources.Read(),
                    JitMethods = JitInfo.GetCompiledMethodCount(),
                    Regions = ReadCommittedRegions(),
                    AddedModules = stage == "begin" ? null : modules
                });
            }
            catch (Exception error) { DiagnosticLog.Failure("resident.memory.tray_stage_failed", error); }
        };
    }

    private sealed record CommittedRegions(long PrivateExecutableBytes, long PrivateNonExecutableBytes,
        long ImageBytes, long MappedBytes);

    private static CommittedRegions ReadCommittedRegions()
    {
        const uint committed = 0x1000, privateRegion = 0x20000, mapped = 0x40000, image = 0x1000000;
        const uint executeMask = 0xf0;
        nuint address = 0;
        nuint size = (nuint)Marshal.SizeOf<MemoryRegion>();
        long executable = 0, nonExecutable = 0, images = 0, mappings = 0;
        while (VirtualQuery(address, out var region, size) == size)
        {
            nuint next = region.BaseAddress + region.RegionSize;
            if (next <= address) break;
            address = next;
            if (region.State != committed) continue;
            long bytes = (long)region.RegionSize;
            switch (region.Type)
            {
                case privateRegion when (region.Protection & executeMask) != 0: executable += bytes; break;
                case privateRegion: nonExecutable += bytes; break;
                case image: images += bytes; break;
                case mapped: mappings += bytes; break;
            }
        }
        return new(executable, nonExecutable, images, mappings);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtection;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protection, Type;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(nuint address, out MemoryRegion region, nuint length);
}
