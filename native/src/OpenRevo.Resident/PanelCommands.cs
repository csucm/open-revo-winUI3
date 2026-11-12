using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;
using System.Diagnostics;

namespace OpenRevo.Resident;

internal sealed class PanelCommands(ApplicationHardwareRuntime runtime, ResidentBackgroundPlugins plugins,
    OsdSettings osd, PanelSettingsRuntime preferences, ResidentSystemSampler sampler,
    Action<OsdMessage> notify, Action configureOsd, Action screenOff, Action resetTrayTelemetry,
    Func<long, PanelMaintenanceRequest, Task<PanelFrame>> maintain, ResidentUpdates updates,
    Func<long, PanelApplicationRequest, Task<PanelFrame>> application)
{
    private readonly PanelDesktopCommands desktopCommands = new(runtime, notify, screenOff);
    private readonly PanelTuningCommands tuningCommands = new(runtime);
    private readonly object snapshotGate = new();
    private readonly object switchGate = new();
    private sealed record SwitchRead(DeviceShortcutRead? Shortcuts = null, OverviewSwitches? Overview = null,
        DateTimeOffset? OverviewAt = null);
    private readonly Lazy<DeviceIdentity> identity = new(DeviceIdentity.Read);
    private PanelLayouts? layouts;
    private long revision;
    private long lastSwitchRead, lastDisplayRead;
    private int presentationDemand;
    private volatile bool ready;
    private volatile SwitchRead switchRead = new();

    internal void MarkReady() => ready = true;
    internal PanelPresentationDemand PresentationDemand => (PanelPresentationDemand)Volatile.Read(ref presentationDemand);

    internal PanelSnapshot Read()
    {
        lock (snapshotGate) return ReadCore();
    }

    private PanelSnapshot ReadCore()
    {
        long next = ++revision;
        var hardware = runtime.Hardware;
        bool active = ready && hardware.Active;
        var confirmed = active ? hardware.State : null;
        var gpu = runtime.Gpu.Snapshot;
        var firmware = gpu.Firmware;
        var switches = switchRead;
        var currentShortcuts = switches.Shortcuts;
        var currentOverview = switches.Overview;
        var display = runtime.Display.Snapshot;
        var displayPanel = display.Panel;
        var settings = osd.Snapshot;
        var appearance = settings.Appearance;
        var panel = preferences.Snapshot;
        return new(next, ready, active, active ? hardware.CurrentMode : null,
            active && hardware.SupportsPerformance, active && hardware.SupportsBattery, active && hardware.SupportsFanBoost,
            confirmed?.Upper, confirmed?.FanBoost, active ? hardware.RequestedFanBoost : null,
            runtime.SessionPower.KeepAwakeEnabled, runtime.Devices.AutomaticTakeoverPending,
            new(gpu.Fresh, gpu.Declared, firmware?.Mode ?? 0, gpu.RuntimeMode, gpu.RestartPending,
                firmware?.Integrated == true, firmware?.Hybrid == true, firmware?.Discrete == true),
            new(active ? currentShortcuts?.Camera : null, currentShortcuts?.Microphone, currentShortcuts?.WinLocked,
                currentOverview?.Wifi, currentOverview?.Bluetooth, active ? currentOverview?.Touchpad : null,
                active ? currentOverview?.FnLock : null, active && currentShortcuts?.CameraAvailable == true,
                active ? currentOverview?.MonochromeLevel : null, active ? switches.OverviewAt : null),
            new(displayPanel?.Hertz, displayPanel?.Rates ?? [], display.Brightness, displayPanel?.Active == true,
                displayPanel?.Width ?? 0, displayPanel?.Height ?? 0, displayPanel?.DeviceName, displayPanel?.Panel), plugins.Running,
            new(settings.Enabled, new(appearance.OsdTheme, appearance.OsdScale, appearance.OsdPosition, appearance.MotionSpeed)),
            plugins.Revision, new(panel.Theme, panel.SampleSeconds, panel.WakeMode, panel.MiniAutoHide,
                panel.UnlockBatteryLimit, panel.TrayTelemetry),
            layouts ??= ReadLayouts(), runtime.Display.LowRefreshOnBattery, sampler.Snapshot, ready ? ReadDeviceInfo() : null,
            sampler.Details, plugins.Theme, plugins.CatalogRevision, updates.Snapshot);
    }

    private PanelDeviceInfo? ReadDeviceInfo()
    {
        try
        {
            var device = identity.Value;
            var hardware = runtime.Hardware;
            var admission = CoreMachine.Current;
            return new(device.Board, device.Product, device.Processor, hardware.CoreDelegated, admission.IntegratedOnly,
                hardware.SupportsCustomTuning, hardware.SupportsDeclaredFnLock,
                hardware.SupportsDeclaredFnLock && !admission.FnLockViaEc && !admission.FnLockReadback,
                SupportsTouchpad: admission.Ready ? hardware.SupportsTouchpad : null);
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.device.identity_failed", error, throttle: true); return null; }
    }

    private static PanelLayouts ReadLayouts()
    {
        var mini = MiniLayout.Load();
        return new(OverviewLayout.Load(), new(mini.Layout.Top, mini.Layout.Quick), SafeText(mini.Issue));
    }

    internal Task PanelDisconnectedAsync()
    {
        SetPresentationDemand(PanelPresentationDemand.Hidden);
        tuningCommands.Reset();
        return plugins.StopForegroundAsync();
    }

    private void SetPresentationDemand(PanelPresentationDemand demand)
    {
        if (Interlocked.Exchange(ref presentationDemand, (int)demand) != (int)demand) sampler.InvalidateDetails();
    }

    internal void DisplayTopologyChanged() => Interlocked.Exchange(ref lastDisplayRead, 0);

    internal async Task<bool> RefreshVisibleSourcesAsync()
    {
        if (!ready || !runtime.Operations.IsAccepting || PresentationDemand == PanelPresentationDemand.Hidden)
            return false;
        bool updated = await ReadSwitchesAsync(force: false).ConfigureAwait(false);
        var display = runtime.Display.Snapshot;
        if ((display.Panel == null || display.Panel.Active && display.Brightness == null) &&
            Due(Interlocked.Read(ref lastDisplayRead), 3))
        {
            await ReadDisplayAsync().ConfigureAwait(false);
            updated = true;
        }
        return updated;
    }

    private async Task ReadDisplayAsync()
    {
        Interlocked.Exchange(ref lastDisplayRead, Stopwatch.GetTimestamp());
        await runtime.Display.ReadAsync().ConfigureAwait(false);
    }

    private async Task<bool> ReadSwitchesAsync(bool force)
    {
        if (!force && !Due(Interlocked.Read(ref lastSwitchRead), 6)) return false;
        if (!await runtime.Operations.EnterAsync(wait: force).ConfigureAwait(false)) return false;
        try
        {
            if (!force && !Due(Interlocked.Read(ref lastSwitchRead), 6)) return false;
            Interlocked.Exchange(ref lastSwitchRead, Stopwatch.GetTimestamp());
            var before = switchRead;
            var update = await Task.Run(() =>
            {
                DeviceShortcutRead? shortcuts = null;
                OverviewSwitches? overview = null;
                try { shortcuts = runtime.Switches.ReadShortcuts(); }
                catch (Exception error) when (!force) { DiagnosticLog.Failure("resident.shortcuts.read_failed", error, throttle: true); }
                try { overview = runtime.Switches.ReadOverview(); }
                catch (Exception error) when (!force) { DiagnosticLog.Failure("resident.switches.read_failed", error, throttle: true); }
                return new SwitchRead(shortcuts, overview, DateTimeOffset.UtcNow);
            }).ConfigureAwait(false);
            lock (switchGate)
            {
                if (ReferenceEquals(before, switchRead)) switchRead = update;
                else Interlocked.Exchange(ref lastSwitchRead, 0);
            }
            return true;
        }
        finally { runtime.Operations.Release(); }
    }

    private void UpdateSwitches(Func<SwitchRead, SwitchRead> update)
    {
        lock (switchGate) switchRead = update(switchRead);
    }

    private static bool Due(long timestamp, int seconds) =>
        timestamp == 0 || Stopwatch.GetElapsedTime(timestamp) >= TimeSpan.FromSeconds(seconds);

    internal async Task<PanelFrame> ExecuteAsync(PanelRequest request, byte[]? payload = null)
    {
        if (!ready || !runtime.Operations.IsAccepting)
            return Reply(request.Id, PanelOutcome.Busy, "后台尚未就绪或正在退出。");
        try
        {
            switch (request.Operation)
            {
                case PanelOperation.PresentationDemand:
                    SetPresentationDemand(request.Demand!.Value);
                    return Reply(request.Id, PanelOutcome.Applied);
                case PanelOperation.Maintenance:
                    return await maintain(request.Id, request.Maintenance!).ConfigureAwait(false);
                case PanelOperation.Application:
                    return await application(request.Id, request.Application!).ConfigureAwait(false);
                case PanelOperation.Desktop:
                    var desktop = await desktopCommands.ExecuteAsync(request.Desktop!, payload).ConfigureAwait(false);
                    return new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id, Read(), desktop.Outcome,
                        SafeText(desktop.Message), Desktop: desktop.Snapshot);
                case PanelOperation.Tuning:
                    var tuning = await tuningCommands.ExecuteAsync(request.Tuning!, payload).ConfigureAwait(false);
                    if (tuning.Outcome is PanelOutcome.ReadbackConfirmed or PanelOutcome.CommandSent or PanelOutcome.Unknown)
                        sampler.InvalidateDetails();
                    return new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id, Read(), tuning.Outcome,
                        SafeText(tuning.Message), Tuning: tuning.Snapshot);
                case PanelOperation.Plugins:
                    var plugin = await plugins.ExecuteAsync(request.Plugin!).ConfigureAwait(false);
                    return new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id, Read(), plugin.Outcome,
                        SafeText(plugin.Message), Plugins: plugin.Snapshot);
                case PanelOperation.Preferences:
                    return await SetPreferenceAsync(request.Id, request.Preference!).ConfigureAwait(false);
                case PanelOperation.Layout:
                    return SaveLayout(request.Id, request.Layout!);
                case PanelOperation.Takeover:
                    tuningCommands.Reset();
                    var takeover = await runtime.Devices.TakeoverAsync().ConfigureAwait(false);
                    return takeover.Outcome switch
                    {
                        TakeoverOutcome.Active => Reply(request.Id, PanelOutcome.Applied, ErrorText(takeover.Error)),
                        TakeoverOutcome.Skipped => Reply(request.Id, PanelOutcome.Busy, "后台正在退出。"),
                        TakeoverOutcome.Blocked => Reply(request.Id, PanelOutcome.Rejected, takeover.Reason),
                        _ => Reply(request.Id, PanelOutcome.Unknown, ErrorText(takeover.Error))
                    };
                case PanelOperation.Release:
                    tuningCommands.Reset();
                    bool released = await runtime.ReleaseControlAsync().ConfigureAwait(false);
                    return Reply(request.Id, released ? PanelOutcome.Applied : PanelOutcome.Busy);
                case PanelOperation.SelectMode:
                    var mode = (PerformanceMode)request.Value!.Value;
                    var performance = await runtime.Performance.SelectModeAsync(mode, mode == PerformanceMode.Turbo).ConfigureAwait(false);
                    return Reply(request.Id, performance.Outcome switch
                    {
                        PerformanceOutcome.ReadbackConfirmed => PanelOutcome.ReadbackConfirmed,
                        PerformanceOutcome.CommandSent => PanelOutcome.CommandSent,
                        PerformanceOutcome.Busy or PerformanceOutcome.Closing or PerformanceOutcome.Cancelled => PanelOutcome.Busy,
                        PerformanceOutcome.Unknown => PanelOutcome.Unknown,
                        _ => PanelOutcome.Rejected
                    }, ErrorText(performance.Error) ?? performance.IndicatorWarning ??
                        (performance.Outcome == PerformanceOutcome.NeedsPreset ? "请先选择并保存自定义调校预设。" : null));
                case PanelOperation.SetBatteryLimit:
                case PanelOperation.ToggleFanBoost:
                    var device = request.Operation == PanelOperation.SetBatteryLimit
                        ? await runtime.Devices.SetChargeLimitAsync(request.Value!.Value).ConfigureAwait(false)
                        : await runtime.Devices.ToggleFanBoostAsync(null).ConfigureAwait(false);
                    if (request.Operation == PanelOperation.SetBatteryLimit &&
                        device is { Outcome: DeviceCommandOutcome.ReadbackConfirmed, Receipt: { } receipt } &&
                        receipt.Before.Upper != receipt.After.Upper)
                        ActionOsd("充电上限 · " + receipt.After.Upper + "%", "battery");
                    return Reply(request.Id, device.Outcome switch
                    {
                        DeviceCommandOutcome.ReadbackConfirmed => PanelOutcome.ReadbackConfirmed,
                        DeviceCommandOutcome.CommandSent => PanelOutcome.CommandSent,
                        DeviceCommandOutcome.Busy or DeviceCommandOutcome.Cancelled => PanelOutcome.Busy,
                        DeviceCommandOutcome.Unknown => PanelOutcome.Unknown,
                        _ => PanelOutcome.Rejected
                    }, ErrorText(device.Error));
                case PanelOperation.ToggleKeepAwake:
                    if (!await runtime.Operations.EnterAsync().ConfigureAwait(false))
                        return Reply(request.Id, PanelOutcome.Busy);
                    bool keepAwake;
                    try { keepAwake = runtime.SessionPower.ToggleKeepAwake(); }
                    finally { runtime.Operations.Release(); }
                    FeatureOsd("keep_awake", keepAwake);
                    return Reply(request.Id, PanelOutcome.Applied);
                case PanelOperation.ReadGpu:
                case PanelOperation.SelectGpu:
                    if (request.Operation == PanelOperation.SelectGpu) tuningCommands.Reset();
                    var gpu = request.Operation == PanelOperation.ReadGpu
                        ? await runtime.Gpu.RefreshAsync().ConfigureAwait(false)
                        : await runtime.Gpu.SelectAsync(request.Value!.Value, request.Expected!.Value).ConfigureAwait(false);
                    if (request.Operation == PanelOperation.SelectGpu) sampler.InvalidateDetails();
                    if (request.Operation == PanelOperation.SelectGpu && gpu.Outcome == GpuCommandOutcome.ReadbackConfirmed)
                        GpuOsd(request.Value!.Value, gpu.State);
                    return Reply(request.Id, gpu.Outcome switch
                    {
                        GpuCommandOutcome.Refreshed or GpuCommandOutcome.NoChange => PanelOutcome.Applied,
                        GpuCommandOutcome.ReadbackConfirmed => PanelOutcome.ReadbackConfirmed,
                        GpuCommandOutcome.CommandSent => PanelOutcome.CommandSent,
                        GpuCommandOutcome.Busy => PanelOutcome.Busy,
                        GpuCommandOutcome.Rejected => PanelOutcome.Rejected,
                        _ => PanelOutcome.Unknown
                    }, ErrorText(gpu.Error));
                case PanelOperation.ReadSwitches:
                    return Reply(request.Id, await ReadSwitchesAsync(force: true).ConfigureAwait(false) ? PanelOutcome.Applied : PanelOutcome.Busy);
                case PanelOperation.ToggleCamera:
                    var cameraRead = await runtime.Switches.ToggleCameraAsync(switchRead.Shortcuts?.WindowsDevice).ConfigureAwait(false);
                    UpdateSwitches(state => state with { Shortcuts = cameraRead });
                    if (cameraRead.Camera is not bool camera)
                        return Reply(request.Id, PanelOutcome.Unknown, "相机状态未确认。");
                    if (!runtime.Hardware.CoreDelegated) FeatureOsd("camera", camera);
                    return Reply(request.Id, PanelOutcome.ReadbackConfirmed);
                case PanelOperation.ToggleMicrophone:
                    bool microphone = await runtime.Switches.ToggleMicrophoneAsync().ConfigureAwait(false);
                    UpdateSwitches(state => state with { Shortcuts = (state.Shortcuts ?? new(null, false, null, null, null, null))
                        with { Microphone = microphone } });
                    FeatureOsd("mic", microphone);
                    return Reply(request.Id, PanelOutcome.ReadbackConfirmed);
                case PanelOperation.ToggleWinLock:
                    bool locked = await runtime.Switches.ToggleWinLockAsync().ConfigureAwait(false);
                    UpdateSwitches(state => state with { Shortcuts = (state.Shortcuts ?? new(null, false, null, null, null, null))
                        with { WinLocked = locked } });
                    FeatureOsd("win_key", locked);
                    return Reply(request.Id, PanelOutcome.ReadbackConfirmed);
                case PanelOperation.ToggleWifi:
                case PanelOperation.ToggleBluetooth:
                case PanelOperation.ToggleTouchpad:
                case PanelOperation.ToggleFnLock:
                    var (key, feature) = request.Operation switch
                    {
                        PanelOperation.ToggleWifi => (OverviewSwitch.Wifi, "wifi"),
                        PanelOperation.ToggleBluetooth => (OverviewSwitch.Bluetooth, "bluetooth"),
                        PanelOperation.ToggleTouchpad => (OverviewSwitch.Touchpad, "touchpad"),
                        _ => (OverviewSwitch.FnLock, "fn_lock")
                    };
                    var confirmedSwitch = await runtime.Switches.ToggleOverviewAsync(key).ConfigureAwait(false);
                    UpdateSwitches(state => state with { Overview = confirmedSwitch, OverviewAt = DateTimeOffset.UtcNow });
                    if (confirmedSwitch.Get(key) is bool enabled) FeatureOsd(feature, enabled);
                    return Reply(request.Id, PanelOutcome.ReadbackConfirmed);
                case PanelOperation.SetDeclaredFnLock:
                    if (ReadDeviceInfo() is not { CanSendFnLock: true })
                        return Reply(request.Id, PanelOutcome.Rejected, "当前设备未提供独立 Fn 锁发送入口。");
                    var fn = await runtime.Switches.SetDeclaredFnLockAsync(request.Value == 1).ConfigureAwait(false);
                    return Reply(request.Id, fn.Outcome == DeviceCommandOutcome.CommandSent
                        ? PanelOutcome.CommandSent : PanelOutcome.Unknown, ErrorText(fn.Error));
                case PanelOperation.ReadDisplay:
                    await ReadDisplayAsync().ConfigureAwait(false);
                    return Reply(request.Id, PanelOutcome.Applied);
                case PanelOperation.SetBrightness:
                    if (runtime.Display.Snapshot is not { Panel.Active: true, Brightness: not null })
                        return Reply(request.Id, PanelOutcome.Rejected, "活动内置屏亮度未读取，请先刷新显示。");
                    await runtime.Display.SetBrightnessAsync(request.Value!.Value).ConfigureAwait(false);
                    ActionOsd("屏幕亮度 · " + request.Value.Value + "%", "brightness");
                    return Reply(request.Id, PanelOutcome.ReadbackConfirmed);
                case PanelOperation.BeginRefresh:
                    if (runtime.Display.Snapshot.Panel is not { Active: true } display ||
                        display.Hertz != request.Expected || !display.Rates.Contains(request.Value!.Value))
                        return Reply(request.Id, PanelOutcome.Rejected, "内置屏或目标刷新率已变化，请重新读取。");
                    var trial = await runtime.Display.BeginRefreshTrialAsync(display, request.Value.Value).ConfigureAwait(false);
                    return new(PanelProtocol.Revision, PanelFrameKind.Result, request.Id, Read(),
                        PanelOutcome.TrialStarted, Trial: new(trial.Id, trial.Target, trial.Deadline));
                case PanelOperation.KeepRefresh:
                case PanelOperation.RestoreRefresh:
                    var refresh = await runtime.Display.FinishRefreshTrialAsync(request.TrialId!.Value,
                        request.Operation == PanelOperation.KeepRefresh).ConfigureAwait(false);
                    return Reply(request.Id, refresh.ConfirmedHertz.HasValue ? PanelOutcome.ReadbackConfirmed : PanelOutcome.Unknown,
                        ErrorText(refresh.Error) ?? (refresh.Kept ? "刷新率已保留。" : "刷新率已恢复。"));
                case PanelOperation.LoadBackgroundPlugins:
                    int running = await plugins.LoadApprovedAsync().ConfigureAwait(false);
                    return Reply(request.Id, PanelOutcome.Applied, $"当前运行 {running} 个已授权后台插件。");
                case PanelOperation.StopBackgroundPlugins:
                    await plugins.StopBackgroundAsync().ConfigureAwait(false);
                    return Reply(request.Id, PanelOutcome.Applied, "本次会话的后台插件已停止。");
                case PanelOperation.SetOsdEnabled:
                    bool enabledOsd = request.Value == 1;
                    osd.SetEnabled(enabledOsd);
                    configureOsd();
                    if (enabledOsd) ActionOsd("屏幕提示 · 已开启", "osd");
                    return Reply(request.Id, PanelOutcome.Applied);
                case PanelOperation.SetOsdAppearance:
                    var appearance = request.OsdAppearance!;
                    osd.SetAppearance(appearance.Theme, appearance.Scale, appearance.Position, appearance.Motion);
                    ActionOsd("Open-Revo · 屏幕提示预览", "osd");
                    return Reply(request.Id, PanelOutcome.Applied);
                default:
                    return Reply(request.Id, PanelOutcome.Rejected, "此操作不受支持。");
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.panel.command.failed", error, new { request.Operation });
            return Reply(request.Id, PanelOutcome.Unknown, ErrorText(error));
        }
    }

    private PanelFrame Reply(long id, PanelOutcome outcome, string? message = null) =>
        new(PanelProtocol.Revision, PanelFrameKind.Result, id, Read(), outcome, message);

    private PanelFrame SaveLayout(long id, PanelLayoutEdit edit)
    {
        try
        {
            if (edit.Overview is { } overview) OverviewLayout.Save(overview);
            else if (edit.Mini is { } mini)
                MiniLayout.Save(new(MiniLayout.CurrentVersion, mini.Top, mini.Quick), edit.ReplaceUnrecognized);
            lock (snapshotGate) layouts = ReadLayouts();
            return Reply(id, PanelOutcome.ConfigurationSaved);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.layout.save_failed", error);
            return Reply(id, PanelOutcome.ConfigurationSaveFailed, ErrorText(error));
        }
    }

    private async Task<PanelFrame> SetPreferenceAsync(long id, PanelPreferenceRequest request)
    {
        bool battery = request.Kind == PanelPreferenceKind.BatteryOverride;
        if (battery && !await runtime.Operations.EnterAsync().ConfigureAwait(false))
            return Reply(id, PanelOutcome.Busy);
        try
        {
            switch (request.Kind)
            {
                case PanelPreferenceKind.Theme: preferences.SetTheme(request.Number!.Value); break;
                case PanelPreferenceKind.SampleSeconds: preferences.SetSampleSeconds(request.Number!.Value); break;
                case PanelPreferenceKind.WakeMode: preferences.SetWakeMode(request.Text!); break;
                case PanelPreferenceKind.MiniAutoHide: preferences.SetMiniAutoHide(request.Enabled!.Value); break;
                case PanelPreferenceKind.TrayTelemetry:
                    preferences.SetTrayTelemetry(request.Enabled!.Value);
                    resetTrayTelemetry();
                    break;
                case PanelPreferenceKind.BatteryOverride:
                    preferences.SetBatteryOverride(request.Enabled!.Value);
                    runtime.Hardware.SetBatteryPerformancePermission(request.Enabled.Value);
                    break;
                default: return Reply(id, PanelOutcome.Rejected);
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.preference.save_failed", error, new { request.Kind });
            return Reply(id, PanelOutcome.ConfigurationSaveFailed, ErrorText(error));
        }
        finally { if (battery) runtime.Operations.Release(); }
        if (battery && request.Enabled == false)
        {
            var protection = await runtime.Performance.EnforceBatteryGuardAsync().ConfigureAwait(false);
            ResidentPerformanceLoop.Report("resident.preference.battery_guard", protection);
            if (protection.Outcome is not (PerformanceOutcome.Skipped or PerformanceOutcome.ReadbackConfirmed))
                return Reply(id, PanelOutcome.ConfigurationSaved, "离电保护已保存，后台会继续核对当前档位。");
        }
        return Reply(id, PanelOutcome.ConfigurationSaved);
    }

    private void FeatureOsd(string feature, bool enabled) => notify(OsdMessage.FromFeature(feature, enabled ? "1" : "0"));

    private void ActionOsd(string text, string icon) => notify(new(text, icon, "#92B9FF"));

    private void GpuOsd(int mode, GpuControlSnapshot state)
    {
        var (title, icon) = mode switch
        {
            1 => ("核显模式", "gpu-integrated"),
            2 => ("混合输出", "gpu-hybrid"),
            _ => ("独显直连", "gpu-discrete")
        };
        string status = state.RestartPending ? "待重启"
            : state.RuntimeMode == mode ? "已生效，无需重启"
            : "固件设置已确认";
        ActionOsd(title + " · " + status, icon);
    }

    private static string? ErrorText(Exception? error) => SafeText(error?.Message);

    private static string? SafeText(string? message)
    {
        if (message == null) return null;
        string text = DiagnosticJournal.Scrub(message);
        return text.Length <= 512 ? text : text[..512];
    }
}
