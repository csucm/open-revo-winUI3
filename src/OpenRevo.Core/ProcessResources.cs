using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRevo.Core;

/// <summary>One process snapshot. Never forces GC or trims its working set.</summary>
public sealed record ProcessResources(DateTimeOffset At,long UptimeMs,long WorkingSetBytes,long PeakWorkingSetBytes,
    long PrivateBytes,long VirtualBytes,long ManagedBytes,long GcHeapBytes,long GcCommittedBytes,long GcFragmentedBytes,
    int Gen0Collections,int Gen1Collections,int Gen2Collections,int Handles,int Threads,uint GdiObjects,uint UserObjects)
{
    public long? PrivateWorkingSetBytes { get; init; }

    public static ProcessResources Read()
    {
        using var process=Process.GetCurrentProcess();
        bool haveMemory=TryReadMemory(includePrivateWorkingSet:true,out var memory);
        var gc=GC.GetGCMemoryInfo();
        return new(DateTimeOffset.UtcNow,(long)(DateTime.UtcNow-process.StartTime.ToUniversalTime()).TotalMilliseconds,
            haveMemory?(long)memory.WorkingSet:process.WorkingSet64,haveMemory?(long)memory.PeakWorkingSet:process.PeakWorkingSet64,
            haveMemory?(long)memory.PrivateUsage:process.PrivateMemorySize64,process.VirtualMemorySize64,
            GC.GetTotalMemory(false),gc.HeapSizeBytes,gc.TotalCommittedBytes,gc.FragmentedBytes,
            GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),process.HandleCount,process.Threads.Count,
            GetGuiResources(CurrentProcess,0),GetGuiResources(CurrentProcess,1))
        {PrivateWorkingSetBytes=haveMemory&&memory.PrivateWorkingSet!=0?(long)memory.PrivateWorkingSet:null};
    }

    internal static (long WorkingSet, long PrivateUsage) ReadFootprint()
    {
        if(TryReadMemory(includePrivateWorkingSet:false,out var memory))
            return ((long)memory.WorkingSet,(long)memory.PrivateUsage);
        using var process=Process.GetCurrentProcess();
        return (process.WorkingSet64,process.PrivateMemorySize64);
    }

    private static readonly nint CurrentProcess = new(-1);
    private static readonly uint ExtendedSize = (uint)Marshal.OffsetOf<MemoryCounters>(nameof(MemoryCounters.PrivateWorkingSet)).ToInt32();
    private static readonly uint Extended2Size = (uint)Marshal.SizeOf<MemoryCounters>();

    private static bool TryReadMemory(bool includePrivateWorkingSet,out MemoryCounters counters)
    {
        // EX2 adds the private working set on supported Windows builds. EX is
        // sufficient for the high-frequency UI sample and the compatibility path.
        if(includePrivateWorkingSet)
        {
            counters=new(){Size=Extended2Size};
            if(K32GetProcessMemoryInfo(CurrentProcess,ref counters,Extended2Size))return true;
        }
        counters=new(){Size=ExtendedSize};
        return K32GetProcessMemoryInfo(CurrentProcess,ref counters,ExtendedSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size,PageFaultCount;
        public nuint PeakWorkingSet,WorkingSet,QuotaPeakPagedPool,QuotaPagedPool,QuotaPeakNonPagedPool,
            QuotaNonPagedPool,PagefileUsage,PeakPagefileUsage,PrivateUsage,PrivateWorkingSet;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint process,ref MemoryCounters counters,uint size);
    [DllImport("user32.dll")]private static extern uint GetGuiResources(nint process,uint flags);
}
