using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRevo.Core;

/// <summary>One process snapshot. Never forces GC or trims its working set.</summary>
public sealed record ProcessResources(DateTimeOffset At,long UptimeMs,long WorkingSetBytes,long PeakWorkingSetBytes,
    long PrivateBytes,long VirtualBytes,long ManagedBytes,long GcHeapBytes,long GcCommittedBytes,long GcFragmentedBytes,
    int Gen0Collections,int Gen1Collections,int Gen2Collections,int Handles,int Threads,uint GdiObjects,uint UserObjects)
{
    public static ProcessResources Read()
    {
        using var process=Process.GetCurrentProcess();
        var gc=GC.GetGCMemoryInfo();
        return new(DateTimeOffset.UtcNow,(long)(DateTime.UtcNow-process.StartTime.ToUniversalTime()).TotalMilliseconds,
            process.WorkingSet64,process.PeakWorkingSet64,process.PrivateMemorySize64,process.VirtualMemorySize64,
            GC.GetTotalMemory(false),gc.HeapSizeBytes,gc.TotalCommittedBytes,gc.FragmentedBytes,
            GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),process.HandleCount,process.Threads.Count,
            GetGuiResources(process.Handle,0),GetGuiResources(process.Handle,1));
    }
    [DllImport("user32.dll")]private static extern uint GetGuiResources(nint process,uint flags);
}
