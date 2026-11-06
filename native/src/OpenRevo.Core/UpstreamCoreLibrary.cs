using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenRevo.Core;

/// <summary>Explicit core interoperability. Loading never starts upstream workers; mutations use separate exports.</summary>
public interface ICameraPowerCore { void SetCameraPowered(bool enabled); }
public sealed partial class UpstreamCoreLibrary : IDisposable,ICameraPowerCore,IGpuModeCore,ITuningGpuCore,IOverviewSwitchCore
{
    private nint library;
    private readonly Query query;
    private readonly object gate = new();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetGpu(uint mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StageGpu(uint expected,uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetClocks(int expectedCore,int expectedMemory,int core,int memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CameraAccess();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Query(uint operation, uint mode, nint buffer, nuint capacity);

    public UpstreamCoreLibrary(string absoluteDllPath)
    {
        if (!Path.IsPathFullyQualified(absoluteDllPath)) throw new ArgumentException("DLL path must be absolute.");
        library = NativeLibrary.Load(absoluteDllPath);
        try
        {
            var version = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(library, "or_native_abi_version"));
            if (version() != 1) throw new NotSupportedException("Unsupported OpenRevo bridge ABI.");
            query = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(library, "or_native_query"));
            ExecuteFeature(10,new{path=AppData.DirectoryPath});
        }
        catch { NativeLibrary.Free(library); library = 0; throw; }
    }

    public JsonElement Read(uint operation, uint mode = 0)
    { lock (gate) return ReadLocked(operation, mode); }
    private JsonElement ReadLocked(uint operation, uint mode)
    {
        ObjectDisposedException.ThrowIf(library == 0, this);
        int size = query(operation, mode, 0, 0);
        if (size < 1 || size > 1024 * 1024) throw new InvalidOperationException($"Upstream query rejected: {size}.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                int actual = query(operation, mode, buffer, (nuint)size);
                if (actual < 1 || actual > 1024 * 1024) throw new InvalidOperationException($"Upstream query failed: {actual}.");
                if (actual > size) { size = actual; continue; }
                byte[] bytes = new byte[actual - 1]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
                return document.RootElement.Clone();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Unstable upstream response size.");
    }

    public void Dispose()
    {

        lock(gate) lock(winLockGate)
        {
            if(library==0)return;
            // Never unload code while its global keyboard callback can run.
            try{NativeWinLock(false);}
            catch(Exception error){DiagnosticLog.Failure("win-lock.dispose",error);return;}
            if(NativeLibrary.TryGetExport(library,"or_native_services_shutdown",out var cleanup))Marshal.GetDelegateForFunctionPointer<CameraAccess>(cleanup)();
            if(NativeLibrary.TryGetExport(library,"or_native_metrics_reset",out var reset))Marshal.GetDelegateForFunctionPointer<ResetMetrics>(reset)();
            windowsGpu.Dispose();NativeLibrary.Free(library);library=0;
        }
    }
    public GpuModeState ReadGpuMode()=>GpuModeState.Parse(Read(4));
    public GpuTuningState ReadGpuTuning()
    {
        var caps=GpuTuningCapabilities.Probe();
        if(!caps.Supported)return new(caps,null);
        try
        {
            var state=Read(9);
            string? name=state.GetProperty("name").GetString();
            if(name!=caps.Name)return new(GpuTuningCapabilities.Unknown with{Name=caps.Name},null);
            var offsets=state.GetProperty("offsets");
            var clocks=state.GetProperty("clock_state");
            if(clocks.ValueKind==JsonValueKind.Object)
                caps=caps with{CoreOffsetMax=clocks.GetProperty("core_editable").GetBoolean()?Math.Clamp(clocks.GetProperty("core_max_mhz").GetInt32(),0,250):0,
                    MemoryOffsetMax=clocks.GetProperty("memory_editable").GetBoolean()?Math.Clamp(clocks.GetProperty("memory_max_mhz").GetInt32(),0,1000):0};
            return new(caps,offsets.ValueKind==JsonValueKind.Array?new(offsets[0].GetInt32(),offsets[1].GetInt32()):null);
        }
        catch(InvalidOperationException){return new(caps,null);}
    }
    public void SetClockOffsets(ClockOffsets expected,ClockOffsets target)
    {
        if(target.CoreMhz is <0 or >250||target.MemoryMhz is <0 or >1000)throw new ArgumentOutOfRangeException(nameof(target));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var write=Marshal.GetDelegateForFunctionPointer<SetClocks>(NativeLibrary.GetExport(library,"or_native_clock_offsets_set"));
            int result=write(expected.CoreMhz,expected.MemoryMhz,target.CoreMhz,target.MemoryMhz);
            if(result!=0)throw new IOException($"显卡频率偏移未确认（{result}）；请重新读取当前状态。");
        }
    }
    public bool StageGpuMode(int expected,int target)
    {
        lock (gate)
        {
            if(expected is <1 or >3 || target is <1 or >3)throw new ArgumentOutOfRangeException(nameof(target));
            ObjectDisposedException.ThrowIf(library == 0, this);
            var write=Marshal.GetDelegateForFunctionPointer<StageGpu>(NativeLibrary.GetExport(library,"or_native_gpu_stage"));
            int result=write((uint)expected,(uint)target);
            if(result is not(0 or 1))throw new IOException(result==-8?"显卡设置已变化，请重新读取后再选择。":"显卡保存结果未确认，请重新读取设置。");
            return result==0;
        }
    }
    public bool? ReadMicrophone()
    {
        var value=Read(7).GetProperty("enabled");
        return value.ValueKind==JsonValueKind.Null?null:value.GetBoolean();
    }
    public void SetMicrophone(bool enabled)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var write=Marshal.GetDelegateForFunctionPointer<SetGpu>(NativeLibrary.GetExport(library,"or_native_microphone_set"));
            if(write(enabled?1u:0u)!=0)throw new IOException("麦克风设置未确认成功，请检查默认输入设备。");
        }
    }
    public void SetCameraPowered(bool enabled)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var write=Marshal.GetDelegateForFunctionPointer<SetGpu>(NativeLibrary.GetExport(library,"or_native_camera_power_set"));
            int result=write(enabled?1u:0u);
            if(result!=0)throw new IOException($"相机供电设置未确认成功（{result}）。");
        }
    }
    public bool IsCameraAccessBlocked()
    {
        var state=Read(8);
        return state.GetProperty("policy").ValueKind==JsonValueKind.False ||
            state.GetProperty("consent").ValueKind==JsonValueKind.False || CameraControl.MachineAccessBlocked();
    }
    public void AllowCameraAccess()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var allow=Marshal.GetDelegateForFunctionPointer<CameraAccess>(NativeLibrary.GetExport(library,"or_native_camera_access_allow"));
            if(allow()!=0 || IsCameraAccessBlocked())throw new IOException("Windows 相机访问未确认开启，请检查系统相机权限。");
        }
    }
}

