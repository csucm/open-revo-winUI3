using System.Collections.Concurrent;
using OpenRevo.Core;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed record ResidentPluginReply(PanelOutcome Outcome, PanelPluginSnapshot? Snapshot = null, string? Message = null);

internal sealed class ResidentBackgroundPlugins(ApplicationHardwareRuntime hardware,
    Func<PanelPluginDestination, PluginLease, bool>? navigate) : IPluginHostServices
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private sealed record ActivePlugin(PluginSession Session, bool Background);
    private readonly ConcurrentDictionary<string, ActivePlugin> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginPackage> themes = new(StringComparer.Ordinal);
    private PanelThemeSnapshot theme = new(0, []);
    private readonly CancellationTokenSource stopping = new();
    private readonly string root = Path.Combine(AppData.DirectoryPath, "native-plugins");
    private readonly PluginDiscovery discovery = new(AppContext.BaseDirectory);
    private PluginFolderCatalog? catalog;
    private PluginApprovalStore? approvalStore;
    private PluginFolderCatalog Catalog => catalog ??= new(root);
    private PluginApprovalStore Approvals => approvalStore ??= new(root);
    private PluginRuntime? runtime;
    private bool foregroundOpen;
    private long revision;
    private long catalogRevision;

    internal int Running => sessions.Values.Count(s => s.Background && s.Session.IsRunning);
    internal long Revision => Interlocked.Read(ref revision);
    internal long CatalogRevision => Interlocked.Read(ref catalogRevision);
    internal PanelThemeSnapshot Theme => Volatile.Read(ref theme);
    internal event Action? StateChanged;

    internal async Task<ResidentPluginReply> ExecuteAsync(PanelPluginRequest request)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopping.IsCancellationRequested) return new(PanelOutcome.Busy);
            if (request.Operation == PanelPluginOperation.Refresh)
            {
                await RefreshLockedAsync().ConfigureAwait(false);
                if (foregroundOpen) await RestoreForegroundLockedAsync().ConfigureAwait(false);
                return new(PanelOutcome.Applied, ReadCatalogLocked());
            }
            if (request.Operation == PanelPluginOperation.OpenDirectory)
                return new(PanelOutcome.Applied, new(Directory: discovery.EnsureRoot()));
            if (request.Operation == PanelPluginOperation.ReadState)
                return new(PanelOutcome.Applied, ReadCatalogLocked());
            if (request.Operation == PanelPluginOperation.OpenViews)
            {
                if (!Catalog.DiscoveryStarted) await RefreshLockedAsync().ConfigureAwait(false);
                await RestoreForegroundLockedAsync().ConfigureAwait(false);
                foregroundOpen = true;
                return new(PanelOutcome.Applied, ReadCatalogLocked());
            }
            if (request.Operation == PanelPluginOperation.CloseViews)
            {
                await StopForegroundLockedAsync().ConfigureAwait(false);
                return new(PanelOutcome.Applied, ReadCatalogLocked());
            }

            var key = request.Target!;
            if (request.Operation == PanelPluginOperation.ReadView)
                return new(PanelOutcome.Applied, new(Page: ReadView(key)));
            if (request.Operation == PanelPluginOperation.Input)
            {
                bool sent = (navigate == null || foregroundOpen) && MatchingSession(key) is { IsRunning: true } session &&
                    session.PostUiEvent(new(request.ControlId!, request.Value));
                return sent ? new(PanelOutcome.CommandSent) : new(PanelOutcome.Rejected, Message: "插件页面或控件已失效。");
            }

            var reviewed = Catalog.Packages.SingleOrDefault(p => p.Manifest.Id == key.Id && p.Digest == key.Digest);
            if (reviewed == null) return new(PanelOutcome.Rejected, Message: "插件包已变更，请刷新列表。");
            if (request.Operation == PanelPluginOperation.Enable)
            {
                if (navigate != null && !foregroundOpen)
                    return new(PanelOutcome.Rejected, Message: "请先打开插件页。");
                return await EnableLockedAsync(reviewed, request.Grants).ConfigureAwait(false);
            }

            Catalog.DisableInSession(key.Id);
            await StopLockedAsync(key.Id).ConfigureAwait(false);
            // Revoke first. If saving the preference fails the approval must still be gone.
            if (request.Operation == PanelPluginOperation.Revoke) Approvals.Revoke(key.Id);
            try { Catalog.SaveChoice(reviewed, false); }
            catch (Exception error)
            {
                DiagnosticLog.Failure("resident.plugin.choice_save_failed", error, new { key.Id });
                return new(PanelOutcome.ConfigurationSaveFailed, ReadCatalogLocked(),
                    "插件已停止，但关闭设置保存失败。");
            }
            Catalog.ClearError(key.Id);
            SignalCatalogChange();
            return new(PanelOutcome.ConfigurationSaved, ReadCatalogLocked(),
                request.Operation == PanelPluginOperation.Revoke ? "已停止并撤销授权。" : "已停止，保留授权和插件配置。");
        }
        finally { lifecycle.Release(); }
    }

    private async Task<ResidentPluginReply> EnableLockedAsync(PluginPackage reviewed, string[]? grants)
    {
        string id = reviewed.Manifest.Id;
        _ = Catalog.FindRecord(id);
        var current = await Task.Run(() => PluginPackage.Read(reviewed.DirectoryPath)).ConfigureAwait(false);
        if (current.Manifest.Id != id || current.Digest != reviewed.Digest)
            return new(PanelOutcome.Rejected, Message: "插件文件已变化，请刷新列表后重新审核。");
        if (MatchingSession(new(id, current.Digest)) is { IsRunning: true })
            return new(PanelOutcome.Applied, ReadCatalogLocked());
        var approval = grants == null ? Approvals.Load(current) : PluginApproval.FromUserSelection(current, grants);
        if (approval == null) return new(PanelOutcome.Rejected, ReadCatalogLocked(), "此版本需要重新审核权限。");
        if (current.Manifest.Platform != null)
            return EnableThemeLocked(current, approval, grants != null);
        if (grants != null) Approvals.Save(approval);
        if (sessions.ContainsKey(id)) await StopLockedAsync(id).ConfigureAwait(false);
        try { await StartLockedAsync(current, approval).ConfigureAwait(false); }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.plugin.enable_failed", error, new { id });
            return new(PanelOutcome.Unknown, ReadCatalogLocked(), error.Message);
        }
        try { Catalog.SaveChoice(current, true); }
        catch (Exception error)
        {
            Catalog.DisableInSession(id);
            await StopLockedAsync(id).ConfigureAwait(false);
            DiagnosticLog.Failure("resident.plugin.choice_save_failed", error, new { id });
            return new(PanelOutcome.ConfigurationSaveFailed, ReadCatalogLocked(), "启用设置保存失败，插件已停止。");
        }
        Catalog.MarkEnabled(id);
        SignalCatalogChange();
        return new(PanelOutcome.ConfigurationSaved, ReadCatalogLocked());
    }

    private ResidentPluginReply EnableThemeLocked(PluginPackage package, PluginApproval approval, bool saveApproval)
    {
        if (!approval.Permissions.Contains(PluginPlatformPermissions.Theme))
            return new(PanelOutcome.Rejected, ReadCatalogLocked(), "需要当前版本的静态主题授权。");
        string id = package.Manifest.Id;
        PluginThemePalette palette;
        try { palette = PluginThemePalette.Compose(themes.Values.Where(p => p.Manifest.Id != id).Append(package)); }
        catch (InvalidDataException error) { return new(PanelOutcome.Rejected, ReadCatalogLocked(), error.Message); }
        try
        {
            if (saveApproval) Approvals.Save(approval);
            Catalog.SaveChoice(package, true);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.theme.save_failed", error, new { id });
            return new(PanelOutcome.ConfigurationSaveFailed, ReadCatalogLocked(), "主题启用设置保存失败。");
        }
        themes[id] = package;
        Catalog.MarkEnabled(id);
        PublishTheme(palette);
        return new(PanelOutcome.ConfigurationSaved, ReadCatalogLocked());
    }

    private void RestoreThemesLocked(IEnumerable<PluginPackage> candidates)
    {
        var next = new Dictionary<string, PluginPackage>(StringComparer.Ordinal);
        var palette = PluginThemePalette.Empty;
        foreach (var package in candidates)
        {
            string id = package.Manifest.Id;
            try
            {
                palette = PluginThemePalette.Compose(next.Values.Append(package));
                next.Add(id, package);
                Catalog.ClearError(id);
            }
            catch (InvalidDataException error) { Catalog.SetError(id, error.Message); }
        }
        themes.Clear();
        foreach (var (id, package) in next) themes.Add(id, package);
        PublishTheme(palette);
    }

    private void PublishTheme(PluginThemePalette palette)
    {
        var current = Theme;
        var colors = palette.ReadColors().OrderBy(c => c.Surface, StringComparer.Ordinal).ThenBy(c => c.Mode, StringComparer.Ordinal)
            .ThenBy(c => c.Token, StringComparer.Ordinal).Select(c => new PanelThemeColor(c.Surface, c.Mode, c.Token, c.Value)).ToArray();
        if (!current.Colors.SequenceEqual(colors)) Volatile.Write(ref theme, new(current.Revision + 1, colors));
        SignalCatalogChange();
    }

    private PluginApproval? Approved(PluginPackage package)
    {
        var id = package.Manifest.Id;
        try
        {
            return !Catalog.DisabledThisSession(id) &&
                Catalog.FindRecord(id) is { Enabled: true, Fault: null } record && record.Digest == package.Digest
                ? Approvals.Load(package) : null;
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.plugin.approval.failed", error, new { id });
            return null;
        }
    }

    private async Task RestoreForegroundLockedAsync()
    {
        foreach (var package in Catalog.Packages)
        {
            if (sessions.Count >= 4) break;
            var manifest = package.Manifest;
            if (manifest.Background || manifest.Platform != null || sessions.ContainsKey(manifest.Id) || Approved(package) is not { } approval)
                continue;
            try { await StartLockedAsync(package, approval).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Catalog.SetError(manifest.Id, error.Message);
                SignalCatalogChange();
                DiagnosticLog.Failure("resident.plugin.start.failed", error, new { manifest.Id });
            }
        }
    }

    internal async Task<int> LoadApprovedAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            stopping.Token.ThrowIfCancellationRequested();
            await RefreshLockedAsync().ConfigureAwait(false);
            var approved = new Dictionary<string, (PluginPackage Package, PluginApproval Approval)>(StringComparer.Ordinal);
            foreach (var package in Catalog.Packages)
            {
                var manifest = package.Manifest;
                if (manifest.Background && manifest.Platform == null && Approved(package) is { } approval &&
                    approval.Permissions.Contains(PluginPermissions.Background))
                    approved.Add(manifest.Id, (package, approval));
            }
            foreach (var (id, active) in sessions.ToArray())
            {
                if (!active.Background) continue;
                if (active.Session.IsRunning && approved.TryGetValue(id, out var current) &&
                    current.Package.Digest == active.Session.Lease.PackageDigest)
                    continue;
                await StopLockedAsync(id).ConfigureAwait(false);
            }
            foreach (var (id, item) in approved)
            {
                stopping.Token.ThrowIfCancellationRequested();
                if (sessions.ContainsKey(id)) continue;
                if (sessions.Count >= 4) break;
                try { await StartLockedAsync(item.Package, item.Approval).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Catalog.SetError(id, error.Message);
                    SignalCatalogChange();
                    DiagnosticLog.Failure("resident.plugin.start.failed", error, new { id });
                }
            }
            return Running;
        }
        finally { lifecycle.Release(); }
    }

    private async Task RefreshLockedAsync()
    {
        var result = Directory.Exists(discovery.Root)
            ? await Task.Run(discovery.Refresh).ConfigureAwait(false) : new PluginDiscoveryResult([], null);
        if (result.RootError != null) throw new IOException(result.RootError);
        foreach (var (id, active) in sessions.ToArray())
            if (!result.Entries.Any(e => e.Package?.Manifest.Id == id && e.Package.Digest == active.Session.Lease.PackageDigest))
                await StopLockedAsync(id).ConfigureAwait(false);
        Catalog.AcceptDiscovery(result);
        var approvedThemes = Catalog.Packages.Where(package => package.Manifest.Platform is { Runtime: "static" } &&
            Approved(package)?.Permissions.Contains(PluginPlatformPermissions.Theme) == true);
        RestoreThemesLocked(approvedThemes.OrderBy(package => themes.ContainsKey(package.Manifest.Id) ? 0 : 1)
            .ThenBy(package => package.Manifest.Id, StringComparer.Ordinal).ToArray());
    }

    private async Task StartLockedAsync(PluginPackage package, PluginApproval approval)
    {
        var manifest = package.Manifest;
        string id = manifest.Id;
        runtime ??= new(this, root, Path.Combine(AppContext.BaseDirectory, "PluginWorker"));
        var session = await runtime.EnableAsync(package, approval, stopping.Token).ConfigureAwait(false);
        sessions[id] = new(session, manifest.Background);
        session.Changed += SignalChange;
        session.Stopped += reason =>
        {
            SignalCatalogChange();
            if (reason != null) _ = QuarantineAsync(package, session, reason);
        };
        if (!session.IsRunning)
        {
            string failure = session.FailureCode ?? "worker_stopped_during_startup";
            await StopLockedAsync(id).ConfigureAwait(false);
            Catalog.DisableInSession(id);
            Catalog.SaveChoice(package, false, failure);
            throw new IOException("插件在启用期间停止：" + failure);
        }
        Catalog.ClearError(id);
        SignalCatalogChange();
    }

    private async Task QuarantineAsync(PluginPackage package, PluginSession session, string failure)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            string id = package.Manifest.Id;
            if (!ReferenceEquals(sessions.GetValueOrDefault(id)?.Session, session)) return;
            Catalog.DisableInSession(id);
            Catalog.SetError(id, failure);
            try { Catalog.SaveChoice(package, false, Limit(failure, 512)); }
            finally { await StopLockedAsync(id).ConfigureAwait(false); }
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.plugin.quarantine.failed", error); }
        finally { lifecycle.Release(); }
    }

    internal async Task StopForegroundAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopForegroundLockedAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private Task StopForegroundLockedAsync()
    {
        foregroundOpen = false;
        return StopSessionsLockedAsync(background: false);
    }

    internal async Task StopBackgroundAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopSessionsLockedAsync(background: true).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopSessionsLockedAsync(bool background)
    {
        List<Exception> failures = [];
        foreach (var (id, active) in sessions.ToArray())
        {
            if (active.Background != background) continue;
            try { await StopLockedAsync(id).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("插件清理失败。", failures);
    }

    private async Task StopLockedAsync(string id)
    {
        if (themes.ContainsKey(id))
            RestoreThemesLocked(themes.Values.Where(package => package.Manifest.Id != id).ToArray());
        if (sessions.TryGetValue(id, out var active) && active.Session.View is { } view)
            Catalog.RememberView(id, view);
        try
        {
            if (runtime != null) await runtime.DisableAsync(id).ConfigureAwait(false);
        }
        finally
        {
            if (sessions.TryRemove(id, out _)) SignalCatalogChange();
        }
    }

    private PanelPluginSnapshot ReadCatalogLocked()
    {
        var entries = Catalog.Packages.Select(package =>
        {
            var manifest = package.Manifest;
            var key = new PanelPluginKey(manifest.Id, package.Digest);
            PluginRecord? record = null;
            string[]? granted = null;
            string? fault = Catalog.Error(manifest.Id);
            try
            {
                record = Catalog.FindRecord(manifest.Id);
                if (record?.Digest == package.Digest) fault ??= record.Fault;
                granted = Approvals.Load(package)?.Permissions;
            }
            catch (Exception error) { fault ??= error.Message; }
            bool staticTheme = manifest.Platform is { Runtime: "static" };
            bool supported = manifest.Platform == null;
            if (staticTheme)
            {
                try { PluginThemePalette.ValidatePackage(package); supported = true; }
                catch (InvalidDataException error) { fault ??= error.Message; }
            }
            return new PanelPluginEntry(key, manifest.Name, manifest.Version, manifest.Description, manifest.Author,
                manifest.Permissions, granted, manifest.Background, supported,
                record is { Enabled: true } && record.Digest == package.Digest && !Catalog.DisabledThisSession(manifest.Id),
                MatchingSession(key)?.IsRunning == true || themes.GetValueOrDefault(key.Id)?.Digest == key.Digest,
                fault == null ? null : Limit(fault, 512), Path.GetFileName(package.DirectoryPath), staticTheme,
                MatchingSession(key)?.View != null || Catalog.View(key.Id) != null);
        }).ToArray();
        var errors = Catalog.DiscoveryErrors.Select(e => Limit(e.FolderName + " · " + e.Error, 512)).ToArray();
        return new(entries, errors, Directory: discovery.Root);
    }

    private PluginSession? MatchingSession(PanelPluginKey key) =>
        sessions.TryGetValue(key.Id, out var active) && active.Session.Lease.PackageDigest == key.Digest ? active.Session : null;

    private PanelPluginPage ReadView(PanelPluginKey key)
    {
        var session = MatchingSession(key);
        bool current = Catalog.Packages.Any(package => package.Manifest.Id == key.Id && package.Digest == key.Digest);
        var view = session?.View ?? (current ? Catalog.View(key.Id) : null);
        return new(key, session?.IsRunning == true, session?.FailureCode ?? Catalog.Error(key.Id), view?.Title,
            view?.Controls.Select(c => new PanelPluginControl(c.Id, c.Kind, c.Label, c.Value, c.Enabled,
                c.Choices?.Select(choice => new PanelPluginChoice(choice.Value, choice.Label)).ToArray())).ToArray() ?? []);
    }

    private void SignalChange()
    {
        Interlocked.Increment(ref revision);
        StateChanged?.Invoke();
    }

    private void SignalCatalogChange()
    {
        Interlocked.Increment(ref catalogRevision);
        SignalChange();
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];

    internal void PublishPower()
    {
        if (Volatile.Read(ref runtime) is { HasPowerSubscribers: true } current)
            current.PublishPower(PluginPowerSource.Read());
    }

    internal bool HasSummarySubscribers => Volatile.Read(ref runtime)?.HasSummarySubscribers == true;

    internal void PublishSummary(SystemSnapshot sample)
    {
        if (Volatile.Read(ref runtime) is not { HasSummarySubscribers: true } current) return;
        current.PublishSummary(new(sample.At, sample.CpuPercent, sample.UsedMemoryGiB, sample.TotalMemoryGiB,
            sample.BatteryPercent, sample.OnAc, sample.Charging));
    }

    internal void BeginShutdown() => stopping.Cancel();

    internal async Task StopAllAsync(bool exiting = false)
    {
        if (exiting) BeginShutdown();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = Interlocked.Exchange(ref runtime, null);
            try { if (current != null) await current.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                sessions.Clear();
                foregroundOpen = false;
                themes.Clear();
                PublishTheme(PluginThemePalette.Empty);
            }
        }
        finally { lifecycle.Release(); }
    }

    public PluginCapability[] QueryCapabilities()
    {
        bool usable = hardware.Operations.IsAccepting && !stopping.IsCancellationRequested;
        bool active = usable && hardware.Hardware.Active;
        bool performance = active && hardware.Hardware.SupportsPerformance;
        bool fan = active && hardware.Hardware.SupportsFanBoost;
        bool battery = active && hardware.Hardware.SupportsBattery;
        return
        [
            new(PluginPermissions.SummaryRead, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.PowerRead, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Config, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Background, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.View, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Navigate, usable && navigate != null, usable && navigate != null ? null : "host_unavailable"),
            new(PluginPermissions.Performance, performance, performance ? null : "takeover_or_capability_unavailable"),
            new(PluginPermissions.FanBoost, fan, fan ? null : "takeover_or_capability_unavailable"),
            new(PluginPermissions.BatteryLimit, battery, battery ? null : "takeover_or_capability_unavailable")
        ];
    }

    public PluginPowerState? QueryPowerState() => PluginPowerSource.Read();

    public PluginCapabilityCatalog QueryCatalog() => new(new(PluginProtocol.Major, PluginProtocol.Minor),
        PluginCapabilityDescription.Build(QueryCapabilities(), restoreApprovedAtStartup: true));

    public PluginThemeSchema QueryThemeSchema() =>
        PluginCapabilityDescription.ThemeSchema(hardware.Operations.IsAccepting && !stopping.IsCancellationRequested);

    public async Task<PluginResult> ExecuteAsync(PluginLease lease, PluginAction action, CancellationToken cancellationToken)
    {
        if (action.Action == "ui.navigate")
        {
            if (!hardware.Operations.IsAccepting || stopping.IsCancellationRequested || cancellationToken.IsCancellationRequested ||
                !lease.Allows(PluginPermissions.Navigate)) return new(false, "navigation_unavailable");
            var destination = action.Arguments.GetProperty("page").GetString() switch
            {
                "overview" => PanelPluginDestination.Overview, "display" => PanelPluginDestination.Display,
                "power" => PanelPluginDestination.Power, "devices" => PanelPluginDestination.Devices,
                "settings" => PanelPluginDestination.Settings, _ => (PanelPluginDestination?)null
            };
            return destination is { } page && navigate?.Invoke(page, lease) == true
                ? new(true, "command_sent") : new(false, "navigation_unavailable");
        }
        var result = await PluginHardwareCommands.ExecuteAsync(hardware, lease, action, cancellationToken).ConfigureAwait(false);
        if (result.Success) SignalChange();
        return result;
    }
}
