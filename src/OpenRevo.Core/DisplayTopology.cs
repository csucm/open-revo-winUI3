using System.Runtime.InteropServices;
namespace OpenRevo.Core;

public sealed record InternalDisplayCandidate(string DeviceName,string Panel,string Name,string Adapter,int Technology,bool Active,bool Available,bool SharedSource=false)
{
    public string? SourceAdapterLuid { get; init; }
    public string? TargetAdapterLuid { get; init; }
}
public static class DisplayTopology
{
    private static int gpuGeneration;
    public static int GpuGeneration => Volatile.Read(ref gpuGeneration);
    public static void InvalidateGpuIdentity() => Interlocked.Increment(ref gpuGeneration);
    internal static string AdapterKey(uint low, int high) => $"luid_0x{high:X8}_0x{low:X8}";
    internal static (string? Path, int Status) AdapterDevicePath(uint low, int high)
    {
        var packet = new AdapterName { Header = new() { Type = 4, Size = 276, Adapter = new() { Low = low, High = high } }, Path = "" };
        int status = GetAdapterName(ref packet);
        return (status == 0 ? packet.Path : null, status);
    }
    public static object? LastSnapshot{get;private set;}
    private static InternalDisplayCandidate[] lastCandidates=[];
    public static bool IsInternalTechnology(int technology)=>technology is 6 or 11 or 13 or int.MinValue;
    public static bool IsVirtual(string name)=>new[]{"virtual","remote","gameviewer","easy&light","indirect","miracast"}.Any(word=>name.Contains(word,StringComparison.OrdinalIgnoreCase));
    public static string PanelCode(string value)
    {
        var parts=value.Replace('#','\\').Split('\\',StringSplitOptions.RemoveEmptyEntries);
        int index=Array.FindIndex(parts,p=>p.Equals("DISPLAY",StringComparison.OrdinalIgnoreCase)||p.Equals("MONITOR",StringComparison.OrdinalIgnoreCase));
        return index>=0&&index+1<parts.Length?parts[index+1]:"未知";
    }
    public static InternalDisplayCandidate Select(IEnumerable<InternalDisplayCandidate> candidates)
    {
        var all=candidates.ToArray();
        var eligible=all.Where(c=>c.Active&&c.Available&&IsInternalTechnology(c.Technology)&&!IsVirtual(c.Adapter+" "+c.Name)).GroupBy(c=>new{c.DeviceName,c.Panel}).Select(g=>g.First()).ToArray();
        if(eligible.Length!=1)throw new IOException($"活动内置屏候选数量为{eligible.Length}，未选择外接或虚拟屏。");
        var selected=eligible[0];
        if(selected.SharedSource||all.Count(c=>c.Active&&c.DeviceName==selected.DeviceName)>1)throw new IOException("内置屏与其他目标共用克隆显示源，未开放控制。");
        return selected;
    }
    public static InternalDisplayCandidate Internal()
    {
        using var trace=DiagnosticLog.Begin("display.topology");
        try
        {
            if(Marshal.SizeOf<PathInfo>()!=72||Marshal.SizeOf<SourceName>()!=84||Marshal.SizeOf<TargetName>()!=420)throw new InvalidOperationException("显示API结构布局不匹配。");
            PathInfo[] paths=[];int result=0;uint count=0;
            for(int attempt=0;attempt<3;attempt++)
            {
                result=GetDisplayConfigBufferSizes(2,out uint pathCount,out uint modeCount);
                if(result!=0)throw new System.ComponentModel.Win32Exception(result,"无法枚举活动显示路径。");
                if(pathCount>256||modeCount>1024)throw new IOException("Windows显示路径数量超出诊断上限。");
                paths=new PathInfo[pathCount];byte[] modes=new byte[checked((int)modeCount*64)];count=pathCount;
                result=QueryDisplayConfig(2,ref count,paths,ref modeCount,modes,0);
                if(result==122)continue;if(result!=0)throw new System.ComponentModel.Win32Exception(result,"读取活动显示路径失败。");break;
            }
            if(result!=0)throw new System.ComponentModel.Win32Exception(result,"显示拓扑持续变化，请重试。");
            var list=new List<InternalDisplayCandidate>();
            foreach(var path in paths.Take((int)count))
            {
                var source=new SourceName{Header=new(){Type=1,Size=84,Adapter=path.Source.Adapter,Id=path.Source.Id},Name=""};
                int sourceResult=GetSourceName(ref source);
                var target=new TargetName{Header=new(){Type=2,Size=420,Adapter=path.Target.Adapter,Id=path.Target.Id},Name="",Path=""};
                int targetResult=GetTargetName(ref target);
                DiagnosticLog.Record("display.path",new{sourceResult,targetResult,device=source.Name,technology=path.Target.Technology,active=(path.Flags&1)!=0,available=path.Target.Available!=0,panel=PanelCode(target.Path),name=target.Name});
                if(sourceResult!=0||targetResult!=0)continue;
                bool shared=paths.Take((int)count).Count(p=>p.Source.Adapter.Low==path.Source.Adapter.Low&&p.Source.Adapter.High==path.Source.Adapter.High&&p.Source.Id==path.Source.Id)>1;
                list.Add(new(source.Name,PanelCode(target.Path),target.Name,DisplayControl.AdapterDescription(source.Name),path.Target.Technology,(path.Flags&1)!=0,path.Target.Available!=0,shared)
                {
                    SourceAdapterLuid = AdapterKey(path.Source.Adapter.Low, path.Source.Adapter.High),
                    TargetAdapterLuid = AdapterKey(path.Target.Adapter.Low, path.Target.Adapter.High)
                });
            }
            lastCandidates=list.ToArray();LastSnapshot=new{at=DateTimeOffset.UtcNow,result,candidates=lastCandidates};
            var selected=Select(list);trace.Complete(new{selected,candidates=list});return selected;
        }
        catch(Exception error){LastSnapshot=new{at=DateTimeOffset.UtcNow,error=error.Message,hresult=$"0x{error.HResult:X8}",candidates=lastCandidates};trace.Fail(error);throw;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct SourceInfo{public Luid Adapter;public uint Id,Mode,Status;}
    [StructLayout(LayoutKind.Sequential)]private struct Rational{public uint Numerator,Denominator;}
    [StructLayout(LayoutKind.Sequential)]private struct TargetInfo{public Luid Adapter;public uint Id,Mode;public int Technology;public uint Rotation,Scaling;public Rational Refresh;public uint Scanline;public int Available;public uint Status;}
    [StructLayout(LayoutKind.Sequential)]private struct PathInfo{public SourceInfo Source;public TargetInfo Target;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct Header{public uint Type,Size;public Luid Adapter;public uint Id;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct SourceName{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct TargetName{public Header Header;public uint Flags;public int Technology;public ushort Manufacturer,Product;public uint Connector;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct AdapterName{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path;}
    [DllImport("user32.dll")]private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]private static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out]PathInfo[] paths,ref uint modeCount,[Out]byte[] modes,nint topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo",CharSet=CharSet.Unicode)]private static extern int GetSourceName(ref SourceName packet);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo",CharSet=CharSet.Unicode)]private static extern int GetTargetName(ref TargetName packet);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo",ExactSpelling=true)]private static extern int GetAdapterName(ref AdapterName packet);
}
