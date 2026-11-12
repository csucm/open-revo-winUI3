using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.ResidentProtocol;

public sealed class PanelClient : IDisposable
{
    private readonly PanelWire wire;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object pendingGate = new();
    private TaskCompletionSource<PanelFrame>? pending;
    private long pendingId, nextId;
    private int disposed, readerStarted;

    private PanelClient(PanelWire wire, PanelFrame attached)
    {
        this.wire = wire;
        Attached = attached;
    }

    public PanelFrame Attached { get; }
    public event Action<PanelFrame>? Notice;
    public event Action? Disconnected;

    public static async Task<PanelClient> ConnectAsync(string name, int parentId, bool nativeCore = false)
    {
        if (!PanelProtocol.ValidPipeName(name)) throw new ArgumentException("候选面板地址无效。");
        const uint queryLimitedInformation = 0x1000;
        using var parent = OpenProcess(queryLimitedInformation, false, parentId);
        using var current = Process.GetCurrentProcess();
        string host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", nativeCore ? "RevoCore.exe" : "OpenRevo.Resident.exe"));
        var image = new StringBuilder(32768);
        int length = image.Capacity;
        if (parent.IsInvalid || !QueryFullProcessImageName(parent, 0, image, ref length) ||
            !string.Equals(image.ToString(), host, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("候选后台身份不匹配。");
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!PanelPipe.IsServer(pipe, parentId, current.SessionId))
                throw new UnauthorizedAccessException("候选后台的实际管道身份不匹配。");
            // The server sets its owner to the account SID. An elevated token's
            // default owner can be Administrators, which CurrentUserOnly compares.
            using var identity = WindowsIdentity.GetCurrent();
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (identity.User == null || owner != identity.User)
                throw new UnauthorizedAccessException("候选后台的管道所有者与当前账户不匹配。");
            var wire = new PanelWire(pipe);
            var attached = await wire.ReadAsync<PanelFrame>(timeout.Token).ConfigureAwait(false);
            if (!attached.Valid || attached.Kind != PanelFrameKind.Attached)
                throw new InvalidDataException("候选后台与面板版本不配对。");
            return new(wire, attached);
        }
        catch { pipe.Dispose(); throw; }
    }

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);

    public void StartReader()
    {
        if (Interlocked.Exchange(ref readerStarted, 1) == 0) _ = Task.Run(ReadNoticesAsync);
    }

    public async Task<PanelFrame> SendAsync(PanelOperation operation, int? value = null, PanelDraft? draft = null,
        int? expected = null, Guid? trialId = null, long? observedWake = null, PanelOsdAppearance? osdAppearance = null,
        PanelDesktopRequest? desktop = null, byte[]? icc = null, PanelPluginRequest? plugin = null,
        PanelPreferenceRequest? preference = null, PanelLayoutEdit? layout = null, PanelPresentationDemand? demand = null,
        PanelTuningRequest? tuning = null, byte[]? tuningImport = null, PanelMaintenanceRequest? maintenance = null,
        PanelApplicationRequest? application = null)
    {
        if (Volatile.Read(ref readerStarted) == 0) throw new InvalidOperationException("面板接收器尚未启动。");
        if (icc != null && tuningImport != null) throw new ArgumentException("面板请求只接受一个文件。");
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var reply = new TaskCompletionSource<PanelFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            long id = ++nextId;
            var request = new PanelRequest(PanelProtocol.Revision, id, operation, value, draft, expected, trialId,
                observedWake, osdAppearance, desktop, plugin, preference, layout, demand, tuning, maintenance, application);
            if (!request.Valid) throw new ArgumentException("候选面板请求参数不合法。", nameof(operation));
            lock (pendingGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                pending = reply;
                pendingId = id;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wire.WriteAsync(request, timeout.Token, icc ?? tuningImport).ConfigureAwait(false);
            return await reply.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (pendingGate) pending = null;
            requestGate.Release();
        }
    }

    private async Task ReadNoticesAsync()
    {
        try
        {
            while (Volatile.Read(ref disposed) == 0)
            {
                var frame = await wire.ReadAsync<PanelFrame>(CancellationToken.None).ConfigureAwait(false);
                if (!frame.Valid || frame.Kind == PanelFrameKind.Attached)
                    throw new InvalidDataException("候选后台消息无效。");
                if (frame.Kind == PanelFrameKind.Result)
                {
                    lock (pendingGate)
                    {
                        if (pending == null || frame.RequestId != pendingId || !pending.TrySetResult(frame))
                            throw new InvalidDataException("候选后台回执序号无效。");
                    }
                }
                else Notice?.Invoke(frame);
            }
        }
        catch (Exception) when (Volatile.Read(ref disposed) != 0) { }
        catch
        {
            Dispose();
            Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        wire.Dispose();
        lock (pendingGate) pending?.TrySetException(new IOException("后台已断开。请在重开后读取状态。"));
    }
}
