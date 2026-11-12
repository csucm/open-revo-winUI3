using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Core;

internal sealed class RustSessionPower : IDisposable
{
    private const string Bridge = "openrevo_native_bridge.dll";
    private enum Command : uint
    {
        State, Toggle, BeginScreenOff, PrepareScreenOff, CaptureInput, FinishScreenOff, CancelScreenOff,
        RestoreOnInput, Shutdown
    }

    private readonly object gate = new();
    private SessionHandle? session;
    private bool disposed;

    public bool KeepAwakeEnabled
    {
        get
        {
            lock (gate) return session != null && Apply(Command.State) == 1;
        }
    }

    public bool ToggleKeepAwake()
    {
        lock (gate)
        {
            EnsureSession();
            return Apply(Command.Toggle) == 1;
        }
    }

    public async Task<bool> TurnOffAsync(Action sendScreenOff)
    {
        bool began = false;
        try
        {
            int delay;
            lock (gate)
            {
                EnsureSession();
                delay = Apply(Command.BeginScreenOff);
                if (delay == 0) return false;
                began = true;
                Apply(Command.PrepareScreenOff);
            }
            await Task.Delay(delay).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed) return false;
                Apply(Command.CaptureInput);
                sendScreenOff();
                return true;
            }
        }
        catch
        {
            lock (gate)
            {
                if (began && !disposed)
                {
                    try { Apply(Command.CancelScreenOff); }
                    catch (Exception error) { DiagnosticLog.Failure("screen-off.restore.failed", error); }
                    began = false;
                }
            }
            throw;
        }
        finally
        {
            if (began)
                lock (gate)
                    if (!disposed) Apply(Command.FinishScreenOff);
        }
    }

    public void RestoreOnInput()
    {
        lock (gate)
            if (session != null) Apply(Command.RestoreOnInput);
    }

    private void EnsureSession()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (session != null) return;
        int result = Create(out var created);
        if (result < 0)
        {
            created.Dispose();
            ThrowFailure(result);
        }
        session = created;
    }

    private int Apply(Command command)
    {
        int result = Execute(session!, command);
        if (result < 0) ThrowFailure(result);
        return result;
    }

    private static void ThrowFailure(int result)
    {
        if (((uint)result & 0xffff0000) == 0x80070000) throw new Win32Exception(result & 0xffff);
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (session == null) return;
            try { Apply(Command.Shutdown); }
            finally
            {
                session.Dispose();
                session = null;
            }
        }
    }

    private sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SessionHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            CloseSession(handle);
            return true;
        }
    }

    [DllImport(Bridge, EntryPoint = "or_native_session_power_create", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int Create(out SessionHandle session);

    [DllImport(Bridge, EntryPoint = "or_native_session_power_apply", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int Execute(SessionHandle session, Command command);

    [DllImport(Bridge, EntryPoint = "or_native_session_power_close", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void CloseSession(nint session);
}
