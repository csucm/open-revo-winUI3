using System.Runtime.InteropServices;
using System.Text.Json;
namespace OpenRevo.Core;
public enum OverviewSwitch:uint {Wifi=1,Bluetooth=2,Touchpad=3,FnLock=4}
public sealed record OverviewSwitches(bool? Wifi,bool? Bluetooth,bool? Touchpad,bool? FnLock)
{
    public string? FnLockSource { get; init; }
    public int? MonochromeLevel { get; init; }
    public bool? Get(OverviewSwitch key)=>key switch{OverviewSwitch.Wifi=>Wifi,OverviewSwitch.Bluetooth=>Bluetooth,OverviewSwitch.Touchpad=>Touchpad,OverviewSwitch.FnLock=>FnLock,_=>null};
}
public interface IOverviewSwitchCore {OverviewSwitches ReadOverviewSwitches();void SetOverviewSwitch(OverviewSwitch key,bool expected,bool target);}
public sealed record OverviewCpu(int? Mhz,int? Usage,double? PowerW,bool PowerEstimated);
public sealed record OverviewGpu(int? Temperature,int? CoreMhz,int? MemoryMhz,int? Usage,ulong? MemoryTotalBytes,ulong? MemoryUsedBytes,double? PowerW)
{
    public string? AdapterName { get; init; }
    public string? AdapterLuid { get; init; }
    public string? AdapterKind { get; init; }
    public string? MachineKind { get; init; }
    public DateTimeOffset? SampledAt { get; init; }
    public ulong? SharedLimitBytes { get; init; }
    public ulong? SharedUsedBytes { get; init; }
    private bool HasCapacity => MemoryTotalBytes is >0 and <1099511627776;
    public bool HasMemory=>HasCapacity&&MemoryUsedBytes.HasValue&&MemoryUsedBytes<=MemoryTotalBytes;
    public double? TotalGiB=>HasCapacity?MemoryTotalBytes/1073741824d:null;
    public double? UsedGiB=>HasMemory?MemoryUsedBytes/1073741824d:null;
    public double? MemoryPercent=>HasMemory?100d*MemoryUsedBytes/MemoryTotalBytes:null;
    public bool ShowsSharedMemory => AdapterKind == "Integrated";
    private bool HasSharedUsage => SharedLimitBytes is >0 && SharedUsedBytes.HasValue && SharedUsedBytes <= SharedLimitBytes;
    public double? DisplayMemoryTotalGiB => ShowsSharedMemory ? SharedLimitBytes / 1073741824d : TotalGiB;
    public double? DisplayMemoryUsedGiB => ShowsSharedMemory ? (HasSharedUsage ? SharedUsedBytes / 1073741824d : null) : UsedGiB;
    public double? DisplayMemoryPercent => ShowsSharedMemory ? (HasSharedUsage ? 100d * SharedUsedBytes / SharedLimitBytes : null) : MemoryPercent;
}
public sealed record OverviewTelemetry(OverviewCpu Cpu,OverviewGpu Gpu,bool GpuSampled,string CpuSource,string GpuSource)
{
    public int TopologyGeneration { get; init; }
}
public sealed record CoreOsdEvent(ulong Sequence,string Feature,string Value);
public sealed partial class UpstreamCoreLibrary
{
    private static readonly JsonSerializerOptions snakeCase=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int SwitchSet(uint key,uint expected,uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int NotifyOsd(uint feature,uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void AckOsd(ulong sequence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void ResetMetrics();
    public OverviewSwitches ReadOverviewSwitches()=>Read(11).Deserialize<OverviewSwitches>(snakeCase)??throw new IOException("Core开关读取失败。");
    private readonly WindowsGpuTelemetry windowsGpu=new();
    public object GpuTelemetryDiagnostics { get { lock (gate) return windowsGpu.Diagnostics; } }
    public OverviewTelemetry ReadOverviewTelemetry(bool sampleGpu, bool? discreteDeclared = null)
    {
        lock(gate)
        {
            int generation = DisplayTopology.GpuGeneration;
            bool useNvml = sampleGpu && discreteDeclared != false;
            var result=Read(10,useNvml?1u:0u).Deserialize<OverviewTelemetry>(snakeCase)??throw new IOException("Core监测读取失败。");
            result = result with { TopologyGeneration = generation };
            if (!sampleGpu) return result with { GpuSource = "GPU sampling inactive" };

            var gpu = result.Gpu;
            if (gpu.Usage != null || gpu.Temperature != null || gpu.CoreMhz != null ||
                gpu.MemoryMhz != null || gpu.MemoryTotalBytes != null || gpu.MemoryUsedBytes != null || gpu.PowerW != null)
            {
                // The bridge's display name and sensors use NVML device index 0.
                // A name is not a Windows LUID: do not merge Windows fields.
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
                    AdapterName = sample.Adapter.Name,
                    AdapterLuid = sample.Adapter.Luid,
                    AdapterKind = sample.Kind.ToString(),
                    MachineKind = sample.MachineKind,
                    SampledAt = sample.At,
                    SharedLimitBytes = sample.Adapter.SharedBytes > 0 ? sample.Adapter.SharedBytes : null,
                    SharedUsedBytes = sample.SharedUsedBytes
                }
            };
        }
    }
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
