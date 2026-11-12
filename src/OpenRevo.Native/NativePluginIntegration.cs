using OpenRevo.Core;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PluginRuntime? nativePluginRuntime;
    private NativePluginServices? nativePluginServices;
    private bool nativePluginPowerQueued;

    private async Task PauseNativePluginsForDataMaintenanceAsync()
    {
        var runtime = nativePluginRuntime;
        nativePluginRuntime = null; nativePluginServices = null;
        ClearFolderThemes();
        folderPlugins.Clear();
        folderPluginDigests.Clear();
        folderPluginCatalog = null;
        if (runtime != null) await runtime.DisposeAsync();
    }

    private void HandleNativePluginPowerMessage(uint message, nuint wParam)
    {
        // PBT_APMPOWERSTATUSCHANGE is delivered even while the application is tray-hidden.
        // No polling or CPU/memory refresh is added; coalesce one readonly OS query per notification batch.
        if (message != 0x0218 || wParam != 10 || nativePluginPowerQueued || nativePluginRuntime?.HasPowerSubscribers != true) return;
        nativePluginPowerQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            nativePluginPowerQueued = false;
            if (!closed && nativePluginRuntime?.HasPowerSubscribers == true)
                nativePluginRuntime.PublishPower(PluginPowerSource.Read());
        })) nativePluginPowerQueued = false;
    }

    private async void StopNativePluginsOnClose()
    {
        if (nativePluginRuntime == null) return;
        try { await nativePluginRuntime.DisposeAsync(); }
        catch (Exception error) { DiagnosticLog.Record("plugin.shutdown_failed", new { type = error.GetType().Name }); }
    }

    // Created only by explicit refresh/load. Consent remains bound to the exact package digest.
    private PluginRuntime EnsureNativePluginRuntime()
    {
        if (uiReviewMode || DeveloperLayoutPreview) throw new InvalidOperationException("Hardware plugins are unavailable in design review.");
        if (nativePluginRuntime != null) return nativePluginRuntime;
        nativePluginServices = new(this);
        RefreshNativePluginState();
        nativePluginRuntime = new(nativePluginServices, Path.Combine(AppData.DirectoryPath, "native-plugins"),
            Path.Combine(AppContext.BaseDirectory, "PluginWorker"));
        return nativePluginRuntime;
    }

    private void RefreshNativePluginState(bool publishSummary = false)
    {
        if (nativePluginServices == null) return;
        bool usable = !closed && !exitRequested && !sessionEnding && !settingsDataBusy && !settingsPowerBusy && !uiReviewMode && !DeveloperLayoutPreview;
        bool performance = usable && hardware.Active && hardware.SupportsPerformance;
        bool fan = usable && hardware.Active && hardware.SupportsFanBoost;
        bool battery = usable && hardware.Active && hardware.SupportsBattery;
        nativePluginServices.SetCapabilities([
            new(PluginPermissions.SummaryRead, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.PowerRead, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Config, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.View, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Navigate, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Background, usable, usable ? null : "host_unavailable"),
            new(PluginPermissions.Performance, performance, performance ? null : "takeover_or_capability_unavailable"),
            new(PluginPermissions.FanBoost, fan, fan ? null : "takeover_or_capability_unavailable"),
            new(PluginPermissions.BatteryLimit, battery, battery ? null : "takeover_or_capability_unavailable")]);
        if (publishSummary && latest is { } sample)
            nativePluginRuntime?.PublishSummary(new(sample.At, sample.CpuPercent, sample.UsedMemoryGiB,
                sample.TotalMemoryGiB, sample.BatteryPercent, sample.OnAc, sample.Charging));
    }

    private async Task<PluginResult> ExecuteNativePluginActionAsync(PluginLease lease, PluginAction action, CancellationToken cancellationToken)
    {
        if (!lease.IsActive || cancellationToken.IsCancellationRequested || closed || exitRequested || sessionEnding ||
            settingsDataBusy || settingsPowerBusy || uiReviewMode || DeveloperLayoutPreview)
            return new(false, "disabled");
        if (action.Action == "ui.navigate")
        {
            if (!lease.Allows(PluginPermissions.Navigate) || trayHidden) return new(false, "navigation_unavailable");
            string? destination = action.Arguments.GetProperty("page").GetString();
            var item = navigation.MenuItems.OfType<PaintNavItem>().FirstOrDefault(i => Equals(i.Tag, destination));
            if (item == null) return new(false, "unknown_page");
            navigation.SelectedItem = item; return new(true, "ok");
        }
        if (hardwareBusy || quickBusy || tuningApplying || takeoverChanging || gpuChanging || physicalModeChanging ||
            batteryGuardBusy || sleepOfficeBusy || desktopServiceBusy || operationQueue.IsBusy)
            return new(false, "busy");
        managedHardwareActions++;
        InvalidateOverviewPowerTargets();
        try
        {
            var result=await PluginHardwareCommands.ExecuteAsync(hardwareRuntime,lease,action,cancellationToken);
            if (result.Success)
            {
                hardwareMessage=result.Code=="command_sent"?"Core 已发送原生插件设置命令，实际效果待确认。":"原生插件设置已回读确认。";
                controlAt=DateTimeOffset.Now;
                if(hardware.State is { } state)ec=ec with{FanBoost=state.FanBoost,BatteryLimit=state.Upper};
            }
            else if(result.Code=="hardware_action_failed")
                hardwareMessage=hardware.Active?"插件设置未完成，硬件仍已接管。":"插件设置已停止，请重新读取并接管。";
            return result;
        }
        finally
        {
            managedHardwareActions--;
            if(!closed){UpdateControlValues();RefreshNativePluginState();}
        }
    }

    private sealed class NativePluginServices(MainWindow window) : IPluginHostServices
    {
        private PluginCapability[] capabilities = [];
        private PluginCapabilityInfo[] catalog = [];
        internal void SetCapabilities(PluginCapability[] value)
        {
            if (Volatile.Read(ref capabilities).SequenceEqual(value)) return;
            Volatile.Write(ref capabilities, value); Volatile.Write(ref catalog, PluginCapabilityDescription.Build(value));
        }
        public PluginCapability[] QueryCapabilities() => Volatile.Read(ref capabilities).ToArray();
        public PluginCapabilityCatalog QueryCatalog() => new(new(PluginProtocol.Major, PluginProtocol.Minor),
            Volatile.Read(ref catalog).Select(c => c with { Values = c.Values.ToArray() }).ToArray());
        public PluginThemeSchema QueryThemeSchema() =>
            PluginCapabilityDescription.ThemeSchema(QueryCapabilities().Any(c => c.Id == PluginPermissions.View && c.Available));
        public PluginPowerState? QueryPowerState() => PluginPowerSource.Read();
        public async Task<PluginResult> ExecuteAsync(PluginLease lease, PluginAction action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PluginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await window.ExecuteNativePluginActionAsync(lease, action, cancellationToken)); }
                catch (OperationCanceledException) { completion.TrySetResult(new(false, "disabled")); }
                catch (Exception error)
                {
                    DiagnosticLog.Record("plugin.dispatch_failed", new { lease.PluginId, type = error.GetType().Name });
                    completion.TrySetResult(new(false, "host_action_failed"));
                }
            })) return new(false, "host_unavailable");
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
