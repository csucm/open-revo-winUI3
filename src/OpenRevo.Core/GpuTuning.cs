using System.Runtime.InteropServices;
using System.Text;

namespace OpenRevo.Core;

public sealed record ClockOffsets(int CoreMhz,int MemoryMhz);
public sealed record GpuTuningCapabilities(string Name,bool Supported,bool ConfigurableBase,int FloorWatts,int BaseMaxWatts,int BoostMaxWatts,int CoreOffsetMax=0,int MemoryOffsetMax=0)
{
    public static GpuTuningCapabilities Unknown=>new("能力待读取",false,false,0,0,0);
    // Same envelope calculation as the pinned Core, using physical NVML limits.
    // No Core global GPU object, background poller, config load or device rescan.
    public static GpuTuningCapabilities FromLimits(string name,int defaultWatts,int maxWatts)
    {
        bool modern=new[]{"RTX 30","RTX 40","RTX 50"}.Any(name.Contains);
        if(!modern||defaultWatts<=0||maxWatts<defaultWatts||maxWatts>255)return Unknown with{Name=name};
        int delta=maxWatts-defaultWatts;
        int boost=delta is >0 and <=25?delta:maxWatts<=115?15:25;
        int baseMax=maxWatts-boost;
        if(boost<5||baseMax<=0||defaultWatts>baseMax&&delta>25)return Unknown with{Name=name};
        return new(name,true,delta>25,delta>25?defaultWatts:baseMax,baseMax,boost);
    }
    internal static GpuTuningCapabilities Probe()
    {
        string path=Path.Combine(Environment.SystemDirectory,"nvml.dll");
        if(!File.Exists(path))return Unknown;
        nint dll=0;bool initialized=false;
        try
        {
            dll=NativeLibrary.Load(path);
            T Load<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll,name));
            if(Load<NoArgs>("nvmlInit_v2")()!=0)return Unknown;initialized=true;
            if(Load<Count>("nvmlDeviceGetCount_v2")(out uint count)!=0||count!=1)return Unknown;
            if(Load<Handle>("nvmlDeviceGetHandleByIndex_v2")(0,out nint device)!=0)return Unknown;
            var name=new StringBuilder(128);
            if(Load<NameQuery>("nvmlDeviceGetName")(device,name,128)!=0||
               Load<Limit>("nvmlDeviceGetPowerManagementDefaultLimit")(device,out uint def)!=0||
               Load<Constraints>("nvmlDeviceGetPowerManagementLimitConstraints")(device,out _,out uint max)!=0||def%1000!=0||max%1000!=0)return Unknown;
            return FromLimits(name.ToString(),(int)(def/1000),(int)(max/1000));
        }
        catch(Exception e)when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException){return Unknown;}
        finally
        {
            if(dll!=0){if(initialized)Marshal.GetDelegateForFunctionPointer<NoArgs>(NativeLibrary.GetExport(dll,"nvmlShutdown"))();NativeLibrary.Free(dll);}
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Handle(uint index,out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Ansi)]private delegate int NameQuery(nint device,StringBuilder name,uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Limit(nint device,out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Constraints(nint device,out uint min,out uint max);
}
public sealed record GpuTuningState(GpuTuningCapabilities Capabilities,ClockOffsets? Offsets);
public interface ITuningGpuCore
{
    GpuTuningState ReadGpuTuning();
    void SetClockOffsets(ClockOffsets expected,ClockOffsets target);
}
