using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

/// <summary>Lazy runtime. An ID remains reserved until startup and its worker have both stopped.</summary>
public sealed class PluginRuntime(IPluginHostServices services, string dataRoot, string workerDirectory) : IAsyncDisposable
{
    private readonly object sync = new();
    private sealed class Slot
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource StartupFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PluginSession? Session;
        internal Task? Stopping;
    }
    private readonly Dictionary<string, Slot> sessions = new(StringComparer.Ordinal);
    private bool disposed;
    private PluginSystemSummary? latest;
    private PluginPowerState? latestPower;
    public bool HasPowerSubscribers
    { get { lock (sync) return !disposed && sessions.Values.Any(s => s.Session?.HasPowerSubscription == true); } }
    public bool HasSummarySubscribers
    { get { lock (sync) return !disposed && sessions.Values.Any(s => s.Session?.HasSummarySubscription == true); } }

    public async Task<PluginSession> EnableAsync(PluginPackage package, PluginApproval approval,
        CancellationToken cancellationToken = default)
    {
        if (!approval.Matches(package)) throw new UnauthorizedAccessException("Plugin changed; renewed consent is required.");
        string[] grants = approval.Permissions;
        string id = package.Manifest.Id;
        Slot slot;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.ContainsKey(id)) throw new InvalidOperationException("Plugin is already enabled or starting.");
            if (sessions.Count >= 4) throw new InvalidOperationException("At most four plugin workers can be enabled.");
            slot = new(); sessions.Add(id, slot);
        }
        bool enabled = false;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, slot.Cancellation.Token);
            var created = await PluginSession.StartAsync(package, grants, services, dataRoot, workerDirectory,
                startup.Token, slot.Cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                slot.Session = created;
                startup.Token.ThrowIfCancellationRequested();
                if (disposed || slot.Stopping != null)
                    throw new OperationCanceledException("Plugin was disabled during startup.");
                created.Activate();
                if (latest != null) created.PublishSummary(latest);
                if (latestPower != null) created.PublishPower(latestPower);
                enabled = true;
            }
            return created;
        }
        finally
        {
            slot.StartupFinished.TrySetResult();
            if (!enabled) await StopSlotAsync(id, slot).ConfigureAwait(false);
        }
    }
    public void PublishSummary(PluginSystemSummary summary)
    {
        PluginSession[] active;
        lock (sync) { if (disposed) return; latest = summary; active = sessions.Values.Select(s => s.Session).OfType<PluginSession>().ToArray(); }
        foreach (var session in active) session.PublishSummary(summary);
        PublishPower(new(summary.SampledAt, summary.OnAc, summary.BatteryPercent));
    }
    public void PublishPower(PluginPowerState power)
    {
        PluginSession[] active;
        lock (sync)
        {
            if (disposed || latestPower != null && power.ObservedAt < latestPower.ObservedAt) return;
            latestPower = power; active = sessions.Values.Select(s => s.Session).OfType<PluginSession>().ToArray();
        }
        foreach (var session in active) session.PublishPower(power);
    }
    public Task DisableAsync(string id)
    {
        lock (sync) return sessions.TryGetValue(id, out var slot) ? StopSlotAsync(id, slot) : Task.CompletedTask;
    }
    private Task StopSlotAsync(string id, Slot slot)
    {
        lock (sync) return slot.Stopping ??= StopSlotCoreAsync(id, slot);
    }
    private async Task StopSlotCoreAsync(string id, Slot slot)
    {
        slot.Cancellation.Cancel(); // Revoke synchronously before waiting for startup/cleanup.
        await slot.StartupFinished.Task.ConfigureAwait(false);
        try { if (slot.Session != null) await slot.Session.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            slot.Cancellation.Dispose();
            lock (sync)
                if (sessions.TryGetValue(id, out var current) && ReferenceEquals(current, slot)) sessions.Remove(id);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] stopping;
        lock (sync)
        {
            disposed = true; latest = null; latestPower = null;
            stopping = sessions.ToArray().Select(p => StopSlotAsync(p.Key, p.Value)).ToArray();
        }
        await Task.WhenAll(stopping).ConfigureAwait(false);
    }
}
