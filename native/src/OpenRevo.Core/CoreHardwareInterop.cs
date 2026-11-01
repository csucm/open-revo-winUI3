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
    public void SetDeclarativeMode(int expected,int target)
    {
        lock(gate){ObjectDisposedException.ThrowIf(library==0,this);int result=Marshal.GetDelegateForFunctionPointer<CoreMode>(NativeLibrary.GetExport(library,"or_native_declarative_mode_set"))((uint)expected,(uint)target);if(result!=0)throw new IOException($"Core性能档位未确认（{result}）。");}
    }
}
