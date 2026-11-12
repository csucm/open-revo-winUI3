using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Resident;

internal sealed class ResidentStartupLease : SafeHandleZeroOrMinusOneIsInvalid
{
    internal enum Entry { Acquired = 0, AlreadyRunning = 1, Blocked = 4 }

    private const string Bridge = "openrevo_native_bridge.dll";

    public ResidentStartupLease() : base(ownsHandle: true) { }

    internal static Entry Enter(bool installed, bool requestWake, out ResidentStartupLease lease)
    {
        int result = EnterNative(installed ? 1u : 0u, requestWake ? 1u : 0u, out lease);
        if ((Entry)result is Entry.Acquired && !lease.IsInvalid ||
            (Entry)result is Entry.AlreadyRunning or Entry.Blocked && lease.IsInvalid)
            return (Entry)result;

        lease.Dispose();
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        throw new IOException("后台启动租约返回无效。");
    }

    protected override bool ReleaseHandle()
    {
        CloseLease(handle);
        return true;
    }

    [DllImport(Bridge, EntryPoint = "or_native_resident_enter", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int EnterNative(uint installed, uint requestWake, out ResidentStartupLease lease);

    [DllImport(Bridge, EntryPoint = "or_native_resident_lease_close", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void CloseLease(nint lease);
}
