using System.Runtime.InteropServices;
using System.Text.Json;
namespace OpenRevo.Core;
public enum OverviewSwitch:uint {Wifi=1,Bluetooth=2,Touchpad=3,FnLock=4}
public sealed record OverviewSwitches(bool? Wifi,bool? Bluetooth,bool? Touchpad,bool? FnLock)
{
    public bool? Get(OverviewSwitch key)=>key switch{OverviewSwitch.Wifi=>Wifi,OverviewSwitch.Bluetooth=>Bluetooth,OverviewSwitch.Touchpad=>Touchpad,OverviewSwitch.FnLock=>FnLock,_=>null};
}
public interface IOverviewSwitchCore {OverviewSwitches ReadOverviewSwitches();void SetOverviewSwitch(OverviewSwitch key,bool expected,bool target);}
public sealed record OverviewCpu(int? Mhz,int? Usage,double? PowerW,bool PowerEstimated);
public sealed record OverviewGpu(int? Temperature,int? CoreMhz,int? MemoryMhz,int? Usage,ulong? MemoryTotalBytes,ulong? MemoryUsedBytes,double? PowerW)
{
    public bool HasMemory=>MemoryTotalBytes is >0 and <1099511627776&&MemoryUsedBytes.HasValue&&MemoryUsedBytes<=MemoryTotalBytes;
    public double? TotalGiB=>HasMemory?MemoryTotalBytes/1073741824d:null;
    public double? UsedGiB=>HasMemory?MemoryUsedBytes/1073741824d:null;
    public double? MemoryPercent=>HasMemory?100d*MemoryUsedBytes/MemoryTotalBytes:null;
}
public sealed record OverviewTelemetry(OverviewCpu Cpu,OverviewGpu Gpu,bool GpuSampled,string CpuSource,string GpuSource);
public sealed record CoreOsdEvent(ulong Sequence,string Feature,string Value);
public sealed partial class UpstreamCoreLibrary
{
    private static readonly JsonSerializerOptions snakeCase=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int SwitchSet(uint key,uint expected,uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int NotifyOsd(uint feature,uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void AckOsd(ulong sequence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void ResetMetrics();
    public OverviewSwitches ReadOverviewSwitches()=>Read(11).Deserialize<OverviewSwitches>(snakeCase)??throw new IOException("Core开关读取失败。");
    public OverviewTelemetry ReadOverviewTelemetry(bool sampleGpu)=>Read(10,sampleGpu?1u:0u).Deserialize<OverviewTelemetry>(snakeCase)??throw new IOException("Core监测读取失败。");
    public void SetOverviewSwitch(OverviewSwitch key,bool expected,bool target)
    {
        if(!Enum.IsDefined(key))throw new ArgumentOutOfRangeException(nameof(key));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var write=Marshal.GetDelegateForFunctionPointer<SwitchSet>(NativeLibrary.GetExport(library,"or_native_switch_set"));
            int result=write((uint)key,expected?1u:0u,target?1u:0u);
            if(result!=0)throw new IOException(result==-8?"设备状态已变化，请重新读取后再试。":$"设备设置未确认成功（{result}）。");
        }
    }
    public CoreOsdEvent? SendOsd(uint feature,uint value)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var notify=Marshal.GetDelegateForFunctionPointer<NotifyOsd>(NativeLibrary.GetExport(library,"or_native_osd_notify"));
            if(notify(feature,value)!=0)throw new ArgumentOutOfRangeException(nameof(feature));
            CoreOsdEvent? latest=null;
            for(int i=0;i<16;i++)
            {
                var item=ReadLocked(12,0);if(item.ValueKind==JsonValueKind.Null)break;
                latest=item.Deserialize<CoreOsdEvent>(snakeCase);
                if(latest==null)break;
                Marshal.GetDelegateForFunctionPointer<AckOsd>(NativeLibrary.GetExport(library,"or_native_osd_ack"))(latest.Sequence);
            }
            return latest;
        }
    }
}
