using System.Runtime.InteropServices;
namespace OpenRevo.Core;
public sealed partial class UpstreamCoreLibrary
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int ReadEc(uint address);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int WriteEc(uint address,uint expected,uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int CoreMode(uint expected,uint target);
    internal byte? ReadNativeEc(ushort address)
    {
        lock(gate){ObjectDisposedException.ThrowIf(library==0,this);int result=Marshal.GetDelegateForFunctionPointer<ReadEc>(NativeLibrary.GetExport(library,"or_native_ec_read"))(address);if(result is <0 or >255)DiagnosticLog.Event("ec.read.failed."+address.ToString("X4"),"error",new{address=$"0x{address:X4}",coreStatus=result},true);return result is >=0 and <=255?(byte)result:null;}
    }
    internal bool WriteNativeEc(ushort address,byte target)
    {
        lock(gate){byte? before=ReadNativeEc(address);if(!before.HasValue)return false;int result=Marshal.GetDelegateForFunctionPointer<WriteEc>(NativeLibrary.GetExport(library,"or_native_ec_write"))(address,before.Value,target);if(result!=0)DiagnosticLog.Event("ec.write.failed","error",new{address=$"0x{address:X4}",before,target,coreStatus=result});return result==0;}
    }
    internal void SendMonochromeLevel(int level)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            if(level is <0 or >255)throw new ArgumentOutOfRangeException(nameof(level));
            int result=Marshal.GetDelegateForFunctionPointer<ReadEc>(NativeLibrary.GetExport(library,"or_native_monochrome_set"))((uint)level);
            if(result!=0)throw new IOException($"白色背光命令未确认发送（{result}）；实际档位未知。");
        }
    }
    public void SetDeclarativeMode(int expected,int target)
    {
        lock(gate){ObjectDisposedException.ThrowIf(library==0,this);int result=Marshal.GetDelegateForFunctionPointer<CoreMode>(NativeLibrary.GetExport(library,"or_native_declarative_mode_set"))((uint)expected,(uint)target);if(result!=0)throw new IOException($"Core性能档位未确认（{result}）。");}
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WinLock(int value);

    // Keyboard filtering must not wait for EC reads or other hardware commands.
    // Disposal takes gate then winLockGate; this path only takes winLockGate.
    private readonly object winLockGate=new();
    public bool ReadWindowsWinKeyLocked()=>NativeWinLock();
    public bool SetWindowsWinKeyLocked(bool locked)=>NativeWinLock(locked);

    internal bool NativeWinLock(bool? locked = null)
    {
        lock(winLockGate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var action=Marshal.GetDelegateForFunctionPointer<WinLock>(NativeLibrary.GetExport(library,"or_native_win_lock"));
            int status=action(locked.HasValue?(locked.Value?1:0):-1);
            if(status is not (0 or 1))throw new IOException($"Core Win 键锁操作失败（{status}）。");
            return status==1;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeclaredControl(uint feature,uint target);

    internal void SendDeclaredControl(uint feature,uint target)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var send=Marshal.GetDelegateForFunctionPointer<DeclaredControl>(NativeLibrary.GetExport(library,"or_native_declared_control"));
            int result=send(feature,target);
            if(result!=0)throw new IOException($"Core 硬件命令未完成（{result}）。");
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClassicFanProtocol([In] byte[] snapshot,nuint length,uint factoryTarget);

    internal bool RecognizesClassicFanProtocol(Hal.FanProtocolState fans,bool factoryTarget)
    {
        int[][] rows=[fans.CpuUp,fans.CpuDown,fans.CpuDuty,fans.GpuUp,fans.GpuDown,fans.GpuDutyAndControl];
        if(rows.Any(row=>row.Length!=16||row.Any(value=>value is <0 or >255)))return false;
        byte[] snapshot=[..rows.SelectMany(row=>row.Select(value=>(byte)value)),fans.Custom,fans.Latch,fans.ApControl,fans.Isolated];
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            var classify=Marshal.GetDelegateForFunctionPointer<ClassicFanProtocol>(NativeLibrary.GetExport(library,"or_native_classic_fan_protocol"));
            int result=classify(snapshot,(nuint)snapshot.Length,factoryTarget?1u:0u);
            if(result is not (0 or 1))throw new IOException($"Core 风扇协议检查失败（{result}）。");
            return result==1;
        }
    }
}
