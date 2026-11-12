using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using OpenRevo.Core;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed class ResidentPanelProcess
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);
    private readonly ResidentPanelPipe pipe;
    private readonly ResidentPanelChild process;
    private readonly Task processExit;
    private readonly int processId;
    private readonly int sessionId;
    private readonly CancellationTokenSource stopping = new();
    private readonly object stopGate = new();
    private readonly Channel<byte> notifications = Channel.CreateBounded<byte>(1);
    private readonly ResidentPanelWake wake;
    private sealed record PluginNavigation(PanelPluginDestination Destination, PluginLease Lease);
    private PluginNavigation? pendingNavigation;
    private int disposed;
    private volatile bool attached, exitObserved;

    private ResidentPanelProcess(ResidentPanelPipe pipe, ResidentPanelChild process, int sessionId, PanelWakeTarget initialTarget)
    {
        this.pipe = pipe;
        this.process = process;
        processId = process.Id;
        this.sessionId = sessionId;
        processExit = process.Exit;
        wake = ResidentPanelWake.Create(initialTarget);
    }

    internal int ProcessId => processId;
    internal bool IsAttached => attached && !Closing;
    internal bool Closing => Volatile.Read(ref disposed) != 0 || wake.Read().Closing;
    internal bool ExitObserved => exitObserved;
    internal PanelWakeTarget? TakePendingWakeAfterExit()
    {
        try { return wake.Read().PendingTarget; }
        finally { wake.Dispose(); }
    }
    internal void ReleaseWakeState() => wake.Dispose();
    internal Task<bool> Completion { get; private set; } = Task.FromResult(false);

    internal static ResidentPanelProcess Start(PanelCommands commands, PanelDraft draft, Action<PanelDraft> saveDraft,
        bool nativePanel, bool installed, PanelWakeTarget initialTarget, Action exitRequested, Action<bool> closeResult)
    {
        string folder = UserDataStore.ValidateDirectory(Path.Combine(AppContext.BaseDirectory, nativePanel ? "panel-native" : "panel"));
        string executable = Path.Combine(folder, nativePanel ? "OpenRevo.ResidentNativePanel.exe" : "OpenRevo.ResidentPanel.exe");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("候选面板未在后台程序旁配对构建。");
        string pipeName = PanelProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        using var current = Process.GetCurrentProcess();
        using var desktopToken = installed ? ResidentPanelLaunch.OpenDesktopToken() : null;
        var pipe = ResidentPanelPipe.CreateLocalServer(pipeName, desktopToken);
        ResidentPanelChild? child = null;
        try
        {
            string parent = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            child = desktopToken == null ? StartCandidate(executable, folder, pipeName, parent) :
                ResidentPanelLaunch.Start(desktopToken, executable, folder, pipeName);
            var panel = new ResidentPanelProcess(pipe, child, current.SessionId, initialTarget);
            panel.Completion = panel.RunAsync(commands, draft, saveDraft, exitRequested, closeResult);
            return panel;
        }
        catch
        {
            pipe.Dispose();
            if (child != null)
            {
                try { child.Terminate(); }
                catch (Exception error) { DiagnosticLog.Failure("resident.panel.launch_abort_failed", error, new { processId = child.Id }); }
                finally { child.Dispose(); }
            }
            throw;
        }
    }

    private static ResidentPanelChild StartCandidate(string executable, string folder, string pipeName, string parent)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = folder, RedirectStandardError = true
        };
        start.ArgumentList.Add("--resident-panel");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(parent);
        var child = Process.Start(start) ?? throw new IOException("候选面板启动失败。");
        try
        {
            int childId = child.Id;
            child.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    DiagnosticLog.Event("resident.panel.stderr", "error", new { processId = childId, detail = args.Data });
            };
            child.BeginErrorReadLine();
            return new ResidentPanelChild(child);
        }
        catch
        {
            try { if (!child.HasExited) child.Kill(); }
            catch (Exception error) { DiagnosticLog.Failure("resident.panel.launch_abort_failed", error); }
            finally { child.Dispose(); }
            throw;
        }
    }

    internal void SignalSnapshot() => notifications.Writer.TryWrite(0);

    internal bool TryNavigate(PanelPluginDestination destination, PluginLease lease)
    {
        if (!IsAttached || Interlocked.CompareExchange(ref pendingNavigation, new(destination, lease), null) != null)
            return false;
        SignalSnapshot();
        return true;
    }

    internal void SignalRaise(PanelWakeTarget target)
    {
        wake.Raise(target);
        SignalSnapshot();
    }

    internal void Stop()
    {
        lock (stopGate)
        {
            if (disposed != 0) return;
            Volatile.Write(ref disposed, 1);
            wake.Stop();
            stopping.Cancel();
            notifications.Writer.TryComplete();
            pipe.Dispose();
        }
    }

    private async Task<bool> RunAsync(PanelCommands commands, PanelDraft draft, Action<PanelDraft> saveDraft,
        Action exitRequested, Action<bool> closeResult)
    {
        Task? publisher = null;
        bool graceExpired = false;
        bool confirmedExit;
        try
        {
            long initialWake;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var connection = pipe.WaitForConnectionAsync(connectTimeout.Token);
                if (await Task.WhenAny(connection, processExit).ConfigureAwait(false) == processExit)
                    connectTimeout.Cancel();
                await connection.ConfigureAwait(false);
                if (processExit.IsCompleted || !pipe.IsClient(processId, sessionId))
                    throw new UnauthorizedAccessException("候选面板进程或会话身份不匹配。");
                var initial = wake.Read();
                initialWake = initial.Sequence;
                await pipe.WriteFrameAsync(new PanelFrame(PanelProtocol.Revision, PanelFrameKind.Attached,
                    Snapshot: commands.Read(), Draft: draft, Wake: initialWake, WakeTarget: initial.Target),
                    connectTimeout.Token).ConfigureAwait(false);
                wake.MarkDelivered(initialWake);
                attached = true;
            }
            DiagnosticLog.Record("resident.panel.attached", new { processId = process.Id });
            publisher = PublishAsync(commands);
            while (!stopping.IsCancellationRequested)
            {
                var (request, route) = await pipe.ReadRequestAsync(stopping.Token).ConfigureAwait(false);
                if (route == PanelRequestRoute.Detach)
                {
                    wake.BeginDetach(request.ObservedWake!.Value);
                    saveDraft(request.Draft!);
                    await SendAsync(new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id,
                        Outcome: PanelOutcome.Detached)).ConfigureAwait(false);
                    DiagnosticLog.Record("resident.panel.detached", new { processId = process.Id });
                    // Keep the acknowledgement readable until the child has exited.
                    try { await processExit.WaitAsync(CloseGrace, stopping.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { graceExpired = true; }
                    break;
                }
                if (route == PanelRequestRoute.Exit)
                {
                    try
                    {
                        await SendAsync(new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id,
                            Outcome: PanelOutcome.Applied)).ConfigureAwait(false);
                    }
                    finally { exitRequested(); }
                    break;
                }
                // Only transport I/O is cancelled when the panel disappears. The
                // shared runtime owns the accepted command until its final outcome.
                byte[]? payload = null;
                int uploadBytes = PanelWire.PayloadBytes(request);
                if (uploadBytes != 0)
                {
                    using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    uploadTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    payload = await pipe.ReadUploadAsync(uploadBytes, uploadTimeout.Token).ConfigureAwait(false);
                }
                var reply = await commands.ExecuteAsync(request, payload).ConfigureAwait(false);
                await SendAsync(reply).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception error) { DiagnosticLog.Failure("resident.panel.channel.failed", error); }
        finally
        {
            Stop();
            var processClosed = ConfirmExitAsync(graceExpired);
            if (publisher != null) await publisher.ConfigureAwait(false);
            try { await commands.PanelDisconnectedAsync().ConfigureAwait(false); }
            catch (Exception error) { DiagnosticLog.Failure("resident.plugin.panel_close_failed", error); }
            confirmedExit = await processClosed.ConfigureAwait(false);
            stopping.Dispose();
            if (confirmedExit) ReportProcessExit(closeResult);
            else
            {
                closeResult(false);
                _ = ObserveLateExitAsync(closeResult);
            }
        }
        return confirmedExit;
    }

    private async Task<bool> ConfirmExitAsync(bool graceExpired)
    {
        try
        {
            if (!graceExpired)
            {
                try
                {
                    await processExit.WaitAsync(CloseGrace).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException) { }
            }
            if (!processExit.IsCompletedSuccessfully)
            {
                DiagnosticLog.Record("resident.panel.close_timeout", new { processId });
                process.Terminate();
            }
            await processExit.WaitAsync(CloseGrace).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.panel.exit_unconfirmed", error, new { processId });
            return false;
        }
    }

    private async Task ObserveLateExitAsync(Action<bool> closeResult)
    {
        try { await processExit.ConfigureAwait(false); }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.panel.exit_observer_failed", error, new { processId });
            process.Dispose();
            return;
        }
        ReportProcessExit(closeResult);
    }

    private void ReportProcessExit(Action<bool> closeResult)
    {
        try { DiagnosticLog.Record("resident.panel.exited", new { processId, process.ExitCode }); }
        finally
        {
            process.Dispose();
            exitObserved = true;
            closeResult(true);
        }
    }

    private async Task PublishAsync(PanelCommands commands)
    {
        try
        {
            await foreach (byte _ in notifications.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                var requested = wake.Read();
                if (requested.Closing) continue;
                if (requested.Sequence != requested.Delivered)
                {
                    await SendAsync(new(PanelProtocol.Revision, PanelFrameKind.Raise, Wake: requested.Sequence,
                        WakeTarget: requested.Target)).ConfigureAwait(false);
                    wake.MarkDelivered(requested.Sequence);
                }
                var navigation = Interlocked.Exchange(ref pendingNavigation, null);
                if (navigation != null && IsAttached &&
                    commands.PresentationDemand is (PanelPresentationDemand.Controls or PanelPresentationDemand.Overview) &&
                    navigation.Lease.Allows(PluginPermissions.Navigate))
                    await SendAsync(new(PanelProtocol.Revision, PanelFrameKind.PluginNavigate, Destination: navigation.Destination))
                        .ConfigureAwait(false);
                await SendAsync(new(PanelProtocol.Revision, PanelFrameKind.Snapshot, Snapshot: commands.Read()))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.panel.publish.failed", error);
            Stop();
        }
    }

    private async Task SendAsync(PanelFrame frame)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await pipe.WriteFrameAsync(frame, timeout.Token).ConfigureAwait(false);
    }
}
