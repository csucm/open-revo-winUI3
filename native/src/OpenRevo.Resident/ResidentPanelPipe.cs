using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal enum PanelRequestRoute : uint { Dispatch, Detach, Exit }

internal sealed class ResidentPanelPipe(SafePipeHandle handle) : IDisposable
{
    private const string Bridge = "openrevo_native_bridge.dll";
    private readonly EventWaitHandle stop = new(false, EventResetMode.ManualReset);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    // The connection's single request reader advances this after field validation.
    private long lastAcceptedId;
    private int closed;

    private enum Operation : uint { Connect, ReadUpload, WriteFrame }

    [StructLayout(LayoutKind.Sequential)]
    private struct RequestAdmission
    {
        public long Id;
        public int Operation;
        public PanelRequestRoute Route;
    }

    internal static ResidentPanelPipe CreateLocalServer(string name, SafeAccessTokenHandle? desktopToken)
    {
        using var noDesktop = desktopToken == null ? new SafeAccessTokenHandle(IntPtr.Zero) : null;
        int result = CreateNative(name, desktopToken ?? noDesktop!, out var handle);
        try
        {
            Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
            if (handle.IsInvalid) throw new IOException("后台未取得有效的面板管道。");
            return new ResidentPanelPipe(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    internal bool IsClient(int processId, int sessionId) => IsClientNative(handle, processId, sessionId) == 1;

    internal Task WaitForConnectionAsync(CancellationToken token) =>
        Task.Run(() => Transfer(Operation.Connect, 0, 0, token), token);

    internal async Task<(PanelRequest Request, PanelRequestRoute Route)> ReadRequestAsync(CancellationToken token)
    {
        try { return await Task.Run(() => ReadRequest(token), token).ConfigureAwait(false); }
        catch { Dispose(); throw; }
    }

    internal async Task<byte[]> ReadUploadAsync(int length, CancellationToken token)
    {
        if (length is < 1 or > PanelProtocol.MaximumIccBytes)
            throw new InvalidDataException("面板文件上传参数无效。");
        try
        {
            byte[] data = new byte[length];
            await Task.Run(() => TransferBuffer(Operation.ReadUpload, data, token), token).ConfigureAwait(false);
            return data;
        }
        catch { Dispose(); throw; }
    }

    internal async Task WriteFrameAsync(PanelFrame message, CancellationToken token)
    {
        byte[] frame = PanelWire.EncodeFrame(message);
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => TransferBuffer(Operation.WriteFrame, frame, token), token).ConfigureAwait(false); }
        catch { Dispose(); throw; }
        finally { writeGate.Release(); }
    }

    private unsafe (PanelRequest Request, PanelRequestRoute Route) ReadRequest(CancellationToken token)
    {
        CheckOpen(token);
        int result = ReadRequestNative(handle, stop.SafeWaitHandle, token.WaitHandle.SafeWaitHandle,
            lastAcceptedId, out nint frame, out uint length, out var admission);
        try
        {
            CheckResult(result, token);
            if (frame == 0 || length is < 1 or > 1024 * 1024)
                throw new InvalidDataException("面板消息长度不合法。");
            var request = PanelWire.DecodeFrame<PanelRequest>(new ReadOnlySpan<byte>((void*)frame, (int)length));
            if (!request.ValidDetails || request.Id != admission.Id || (int)request.Operation != admission.Operation)
                throw new InvalidDataException("候选面板请求格式或序号无效。");
            lastAcceptedId = admission.Id;
            return (request, admission.Route);
        }
        finally { if (frame != 0) FreeFrame(frame, length); }
    }

    private unsafe void TransferBuffer(Operation operation, byte[] buffer, CancellationToken token)
    {
        fixed (byte* pointer = buffer) Transfer(operation, (nint)pointer, buffer.Length, token);
    }

    private void Transfer(Operation operation, nint buffer, int length, CancellationToken token)
    {
        CheckOpen(token);
        int result = TransferNative(operation, handle, stop.SafeWaitHandle, token.WaitHandle.SafeWaitHandle,
            buffer, length);
        CheckResult(result, token);
    }

    private void CheckOpen(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
    }

    private void CheckResult(int result, CancellationToken token)
    {
        if (result >= 0) return;
        CheckOpen(token);
        throw result switch
        {
            unchecked((int)0x80070026) => new EndOfStreamException(),
            unchecked((int)0x8007000D) => new InvalidDataException("面板消息长度、请求格式或序号无效。"),
            _ => new IOException("后台面板管道操作失败。", Marshal.GetExceptionForHR(result, new IntPtr(-1)))
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        // Pending native calls retain the safe handles through cancellation completion.
        stop.Set();
        handle.Dispose();
        stop.Dispose();
    }

    [DllImport(Bridge, EntryPoint = "or_native_resident_pipe_create", ExactSpelling = true,
        CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int CreateNative(string name, SafeAccessTokenHandle desktopToken, out SafePipeHandle pipe);

    [DllImport(Bridge, EntryPoint = "or_native_resident_pipe_is_client", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int IsClientNative(SafePipeHandle pipe, int processId, int sessionId);

    [DllImport(Bridge, EntryPoint = "or_native_resident_panel_io", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int TransferNative(Operation operation, SafePipeHandle pipe, SafeWaitHandle stop,
        SafeWaitHandle cancel, nint buffer, int length);

    [DllImport(Bridge, EntryPoint = "or_native_resident_panel_read_request", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int ReadRequestNative(SafePipeHandle pipe, SafeWaitHandle stop, SafeWaitHandle cancel,
        long previousId, out nint frame, out uint length, out RequestAdmission admission);

    [DllImport(Bridge, EntryPoint = "or_native_resident_panel_frame_free", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void FreeFrame(nint frame, uint length);
}
