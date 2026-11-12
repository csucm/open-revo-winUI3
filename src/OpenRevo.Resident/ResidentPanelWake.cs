using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed class ResidentPanelWake : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Bridge = "openrevo_native_bridge.dll";
    private enum Action : uint { Read, Raise, BeginDetach, Delivered, Stop }

    [Flags]
    internal enum StateFlags : uint { Closing = 1, PendingReopen = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public long Sequence, Delivered;
        public PanelWakeTarget Target;
        public StateFlags Flags;
        internal bool Closing => (Flags & StateFlags.Closing) != 0;
        internal PanelWakeTarget? PendingTarget => (Flags & StateFlags.PendingReopen) != 0 ? Target : null;
    }

    public ResidentPanelWake() : base(ownsHandle: true) { }

    internal static ResidentPanelWake Create(PanelWakeTarget target)
    {
        int result = CreateNative((uint)target, out var state);
        if (result == 0 && !state.IsInvalid) return state;
        state.Dispose();
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        throw new IOException("无法创建面板唤醒状态。");
    }

    internal Snapshot Read() => Execute(Action.Read);
    internal void Raise(PanelWakeTarget target) => Execute(Action.Raise, (long)target);
    internal void BeginDetach(long observed) => Execute(Action.BeginDetach, observed);
    internal void MarkDelivered(long sequence) => Execute(Action.Delivered, sequence);
    internal void Stop() => Execute(Action.Stop);

    private Snapshot Execute(Action action, long value = 0)
    {
        int result = ApplyNative(this, action, value, out var snapshot);
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        return snapshot;
    }

    protected override bool ReleaseHandle()
    {
        CloseNative(handle);
        return true;
    }

    [DllImport(Bridge, EntryPoint = "or_native_resident_wake_create", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int CreateNative(uint target, out ResidentPanelWake state);

    [DllImport(Bridge, EntryPoint = "or_native_resident_wake_apply", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int ApplyNative(ResidentPanelWake state, Action action, long value, out Snapshot snapshot);

    [DllImport(Bridge, EntryPoint = "or_native_resident_wake_close", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void CloseNative(nint state);
}
