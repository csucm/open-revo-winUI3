using System.Diagnostics;

namespace OpenRevo.Core;

public sealed record RefreshTrial(Guid Id, int Target, DateTimeOffset Deadline);
public sealed record RefreshTrialResult(Guid Id, bool Kept, int? ConfirmedHertz, Exception? Error = null);
public sealed record DisplayReadSnapshot(DisplayState? Panel, int? Brightness);

public sealed class DisplayCommandService
{
    private static readonly TimeSpan TrialDuration = TimeSpan.FromSeconds(15);
    // Display transactions are independent of EC commands. Hold this only
    // during a read/write, never while the user is deciding in the panel.
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AutomaticRefresh automatic = new();
    private PendingTrial? trial;
    private RefreshTrialResult? lastTrial;
    private bool closed;
    private volatile bool paused;
    private volatile bool lowRefreshOnBattery = AppData.Load().LowRefreshOnBattery;
    private bool? lastPower;
    private DateTimeOffset lastPolicyCheck;
    private DisplayState? lastPanel;
    private int topologyVersion, evaluatedTopology;
    private DisplayReadSnapshot readback = new(null, null);

    public DisplayReadSnapshot Snapshot => Volatile.Read(ref readback);
    public bool LowRefreshOnBattery => lowRefreshOnBattery;
    public event Action<RefreshTrialResult>? TrialFinished;

    public async Task<DisplayReadSnapshot> ReadAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (paused) return Snapshot;
            int topology = Volatile.Read(ref topologyVersion);
            var result = await Task.Run(() =>
            {
                DisplayState? panel = null;
                int? brightness = null;
                try { panel = DisplayControl.Read(); }
                catch (Exception error) { DiagnosticLog.Failure("display.read", error, throttle: true); }
                try { brightness = ScreenBrightness.Read(); }
                catch (Exception error) { DiagnosticLog.Failure("brightness.read", error, throttle: true); }
                return new DisplayReadSnapshot(panel, brightness);
            }).ConfigureAwait(false);
            if (topology != Volatile.Read(ref topologyVersion)) return Snapshot;
            Volatile.Write(ref readback, result);
            return result;
        }
        finally { gate.Release(); }
    }

    private sealed record PendingTrial(RefreshTrial Ticket, DisplayState Previous, long Started, CancellationTokenSource Expiry);

    public async Task<RefreshTrial> BeginRefreshTrialAsync(DisplayState expected, int target)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (trial != null) throw new InvalidOperationException("上一项刷新率更改正在等待确认。");
            await Task.Run(() => DisplayControl.SetRefresh(expected, target)).ConfigureAwait(false);
            var ticket = new RefreshTrial(Guid.NewGuid(), target, DateTimeOffset.UtcNow.Add(TrialDuration));
            var pending = new PendingTrial(ticket, expected, Stopwatch.GetTimestamp(), new());
            trial = pending;
            Volatile.Write(ref readback, Snapshot with { Panel = expected with { Hertz = target } });
            _ = FinishOnTimeoutAsync(pending, pending.Expiry.Token);
            return ticket;
        }
        finally { gate.Release(); }
    }

    public async Task<RefreshTrialResult> FinishRefreshTrialAsync(Guid id, bool keep)
    {
        RefreshTrialResult finished;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (trial is { } current && current.Ticket.Id == id)
                finished = await FinishTrialAsync(current, keep).ConfigureAwait(false);
            else if (lastTrial is { } result && result.Id == id) return result;
            else return new(id, false, null, new InvalidOperationException("刷新率更改已失效，请重新读取。"));
        }
        finally { gate.Release(); }
        try { TrialFinished?.Invoke(finished); }
        catch (Exception error) { DiagnosticLog.Failure("display.refresh.notification_failed", error); }
        return finished;
    }

    private async Task FinishOnTimeoutAsync(PendingTrial pending, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TrialDuration, cancellationToken).ConfigureAwait(false);
            await FinishRefreshTrialAsync(pending.Ticket.Id, false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticLog.Failure("display.refresh.timeout", error); }
    }

    private async Task<RefreshTrialResult> FinishTrialAsync(PendingTrial current, bool keep)
    {
        bool retain = keep && Stopwatch.GetElapsedTime(current.Started) < TrialDuration;
        trial = null;
        current.Expiry.Cancel();
        current.Expiry.Dispose();
        try
        {
            var expected = current.Previous with { Hertz = current.Ticket.Target };
            if (!retain)
                await Task.Run(() => DisplayControl.SetRefresh(expected, current.Previous.Hertz)).ConfigureAwait(false);
            else
            {
                var readback = await Task.Run(DisplayControl.Read).ConfigureAwait(false);
                if (readback.DeviceName != expected.DeviceName || readback.Panel != expected.Panel ||
                    readback.Width != expected.Width || readback.Height != expected.Height || readback.Hertz != expected.Hertz)
                    throw new InvalidOperationException("内置屏状态已经变化，无法确认保留所选刷新率。");
            }
            int confirmed = retain ? current.Ticket.Target : current.Previous.Hertz;
            Volatile.Write(ref readback, Snapshot with { Panel = current.Previous with { Hertz = confirmed } });
            DiagnosticLog.Record("display.refresh.confirmed", new { current.Ticket.Id, kept = retain, confirmed });
            return lastTrial = new(current.Ticket.Id, retain, confirmed);
        }
        catch (Exception error)
        {
            Volatile.Write(ref readback, new(null, null));
            DiagnosticLog.Failure("display.refresh.finish", error, new { current.Ticket.Id, keep = retain });
            return lastTrial = new(current.Ticket.Id, retain, null, error);
        }
    }

    public void InvalidateTopology()
    {
        Interlocked.Increment(ref topologyVersion);
        Volatile.Write(ref readback, new(null, null));
    }

    public async Task SaveAutomaticPolicyAsync(bool enabled)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            AppData.Update(value => value with { LowRefreshOnBattery = enabled });
            lowRefreshOnBattery = enabled;
        }
        finally { gate.Release(); }
    }

    public async Task<DisplayState?> EvaluateAutomaticAsync(bool? enabled, bool visible, bool force = false, bool refreshAfterWake = false)
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            if (closed || paused || trial != null) return null;
            bool policyEnabled = enabled ?? lowRefreshOnBattery;
            if (enabled == null && !policyEnabled && !automatic.NeedsEvaluationWhenDisabled) return null;
            bool? ac = Telemetry.ReadAc();
            int topology = Volatile.Read(ref topologyVersion);
            if (topology != evaluatedTopology)
            {
                automatic.InvalidateTopology();
                evaluatedTopology = topology;
            }
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - lastPolicyCheck;
            if (!force && !automatic.NeedsEvaluation(policyEnabled, ac) && !refreshAfterWake &&
                (!visible || elapsed < TimeSpan.FromMinutes(1))) return null;
            if (!force && ac == lastPower && elapsed < TimeSpan.FromSeconds(10)) return null;
            lastPower = ac;
            lastPolicyCheck = now;
            var next = await Task.Run(() =>
            {
                var current = DisplayControl.Read();
                if (lastPanel is { } previous && (current.DeviceName != previous.DeviceName || current.Panel != previous.Panel))
                    automatic.InvalidateTopology();
                automatic.Evaluate(policyEnabled, ac, () => current, (_, target) =>
                {
                    DisplayControl.SetRefresh(current, target);
                    current = DisplayControl.Read();
                });
                lastPanel = current;
                return current;
            }).ConfigureAwait(false);
            if (topology == Volatile.Read(ref topologyVersion))
                Volatile.Write(ref readback, Snapshot with { Panel = next });
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task SetBrightnessAsync(int percent)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (trial != null) throw new InvalidOperationException("请先完成刷新率确认。");
            await Task.Run(() => ScreenBrightness.Set(percent)).ConfigureAwait(false);
            Volatile.Write(ref readback, Snapshot with { Brightness = percent });
        }
        finally { gate.Release(); }
    }

    internal async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            closed = true;
            if (trial is { } current) await FinishTrialAsync(current, false).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> TryPauseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed || trial != null) return false;
            paused = true;
            return true;
        }
        finally { gate.Release(); }
    }

    public void Resume() => paused = false;
}
