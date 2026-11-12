using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed partial class ResidentTray : IDisposable
{
    private const uint TrayCallback = 0x8001, RefreshTray = 0x8002, FinishShutdown = 0x8003;
    private const uint PanelCloseResult = 0x8004, PresentOsd = 0x8005, ConfigureOsd = 0x8006, ScreenOff = 0x8007;
    private const uint PresentTrayMenu = 0x8008, InstallUpdate = 0x8009;
    private readonly ApplicationHardwareRuntime runtime;
    private readonly bool nativePanel;
    private readonly bool memoryProfile;
    private readonly PanelCommands panelCommands;
    private readonly ResidentBackgroundPlugins plugins;
    private readonly WindowProcedure callback;
    private readonly CancellationTokenSource stop = new();
    private uint nativeWake;
    private readonly OsdSettings osdSettings = new();
    private readonly PanelSettingsRuntime panelSettings = new();
    private readonly ResidentSystemSampler sampler;
    private readonly object observedOsd = new();
    private nint window;
    private DesktopPowerNotification? power;
    private CapsLockObserver? caps;
    private ResidentOsd? osd;
    private OsdMessage? pendingOsd;
    private int? observedMode;
    private bool? observedFanBoost;
    private Task policies = Task.CompletedTask;
    private Task sampling = Task.CompletedTask;
    private Task pluginStartup = Task.CompletedTask;
    private Task memorySampling = Task.CompletedTask;
    private readonly TaskCompletionSource pluginReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? shutdown;
    private ResidentPanelProcess? panel;
    private PanelDraft? panelDraft;
    private bool menuOpen;
    private volatile bool stopping, shutdownReady;
    private string status = "正在检查设备";
    private readonly object trayTelemetryGate = new();
    private int trayTelemetryRevision;
    private TrayTelemetrySample? traySample;
    private Task? hoverRead;
    private int exitCode;

    internal ResidentTray(ApplicationHardwareRuntime runtime, bool nativePanel, string? debugArgument,
        string? installationDirectory, bool memoryProfile)
    {
        this.runtime = runtime;
        this.nativePanel = nativePanel;
        this.memoryProfile = memoryProfile;
        this.installationDirectory = installationDirectory;
        debugAvailable = debugArgument != null;
        if (debugArgument != null)
        {
            logLevel = debugArgument == "--log-errors" ? 1 : 2;
            logFilter = debugArgument.StartsWith("--debug=", StringComparison.Ordinal) ? debugArgument[8..] :
                SystemPreferences.Load().LogFilter;
        }
        plugins = new(runtime, nativePanel ? TryNavigatePlugin : null);
        plugins.StateChanged += OnPluginStateChanged;
        sampler = new(runtime.Telemetry, plugins);
        updates = new(installationDirectory, () => Volatile.Read(ref panel)?.SignalSnapshot(),
            () => PostMessage(window, InstallUpdate, 0, 0));
        panelCommands = new(runtime, plugins, osdSettings, panelSettings, sampler, QueueOsd, RequestOsdConfiguration,
            RequestScreenOff, ResetTrayTelemetry, RunMaintenanceRequestAsync, updates, RunApplicationRequestAsync);
        callback = OnMessage;
    }

    internal int Run(bool open)
    {
        ExceptionDispatchInfo? callbackError = null;
        int result = RunWindow(installationDirectory == null ? 0u : 1u,
            Path.Combine(AppContext.BaseDirectory, "App.ico"), TrayCallback, RefreshTray,
            callback, WriteTooltip, Ready, Stop);
        window = 0;
        callbackError?.Throw();
        Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
        return exitCode;

        int Ready(nint handle, uint wake)
        {
            window = handle;
            nativeWake = wake;
            try
            {
                Initialize(open);
                return 1;
            }
            catch (Exception error)
            {
                callbackError = ExceptionDispatchInfo.Capture(error);
                return 0;
            }
        }

        void Stop()
        {
            try
            {
                RequestShutdown();
                shutdown!.GetAwaiter().GetResult();
            }
            catch (Exception error) { callbackError ??= ExceptionDispatchInfo.Capture(error); }
        }
    }

    private void Initialize(bool open)
    {
        power = new DesktopPowerNotification(window);
        if (!power.LidRegistered) DiagnosticLog.Record("resident.lid.registration_failed");
        runtime.PerformanceInput.PhysicalModeCompleted += OnPhysicalModeCompleted;
        runtime.Display.TrialFinished += OnRefreshTrialFinished;
        ApplyOsdSettings();
        policies = Task.Run(RunPoliciesAsync);
        pluginStartup = Task.Run(RestorePluginsAsync);
        sampling = Task.Run(() => sampler.RunAsync(
            () => Volatile.Read(ref panel)?.IsAttached != true ? PanelPresentationDemand.Hidden :
                nativePanel ? panelCommands.PresentationDemand : PanelPresentationDemand.Controls,
            () => panelSettings.Snapshot.SampleSeconds,
            panelCommands.RefreshVisibleSourcesAsync,
            () => Volatile.Read(ref panel)?.SignalSnapshot(), stop.Token));
        if (memoryProfile) memorySampling = Task.Run(ObserveMemoryAsync);
        if (open) OpenPanel();
    }

    private async Task RunPoliciesAsync()
    {
        try { await new ResidentPerformanceLoop(runtime).RunAsync(Publish, stop.Token, ObserveHardwareOsd).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.policies.failed", error);
            SetStatus("性能后台未就绪");
            exitCode = 1;
        }
    }

    private void Publish(ResidentPerformanceStatus state)
    {
        if (stopping) return;
        ObserveHardwareOsd();
        panelCommands.MarkReady();
        pluginReady.TrySetResult();
        string mode = state.Mode switch { 1 => "办公", 2 => "均衡", 3 => "狂暴", 4 => "自定义", _ => "档位待读取" };
        SetStatus(state.Active ? "已接管 · " + mode : state.WaitingForTakeover ? "等待其他控制台退出" : "未接管");
        Volatile.Read(ref panel)?.SignalSnapshot();
    }

    private void OnPhysicalModeCompleted(PerformanceResult result)
    {
        if (result.Outcome != PerformanceOutcome.ReadbackConfirmed) return;
        Publish(new(runtime.Hardware.Active, runtime.Hardware.CurrentMode, runtime.Devices.AutomaticTakeoverPending));
    }

    private async Task OnSuspensionAsync(bool sleeping)
    {
        try
        {
            var result = await runtime.PerformanceInput.ObserveSuspensionAsync(sleeping).ConfigureAwait(false);
            ResidentPerformanceLoop.Report("resident.sleep-office", result);
            if (result.Outcome == PerformanceOutcome.ReadbackConfirmed)
                Publish(new(runtime.Hardware.Active, runtime.Hardware.CurrentMode, runtime.Devices.AutomaticTakeoverPending));
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.sleep-office.failed", error); }
    }

    private async Task RefreshHoverAsync()
    {
        try
        {
            int revision = Volatile.Read(ref trayTelemetryRevision);
            if (!panelSettings.Snapshot.TrayTelemetry) return;
            var trace = memoryProfile && Volatile.Read(ref traySample) == null ? CreateTrayMemoryTrace() : null;
            var sample = await runtime.Telemetry.ReadResidentTrayAsync(sampler.LatestSystem, trace).ConfigureAwait(false);
            if (sample == null) return;
            lock (trayTelemetryGate)
            {
                if (revision != trayTelemetryRevision || !panelSettings.Snapshot.TrayTelemetry) return;
                Volatile.Write(ref traySample, sample);
            }
            PostMessage(window, RefreshTray, 0, 0);
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.tray.telemetry_failed", error); }
    }

    private void ResetTrayTelemetry()
    {
        lock (trayTelemetryGate)
        {
            Interlocked.Increment(ref trayTelemetryRevision);
            Volatile.Write(ref traySample, null);
        }
        PostMessage(window, RefreshTray, 0, 0);
    }

    private void SetStatus(string value)
    {
        Volatile.Write(ref status, value);
        PostMessage(window, RefreshTray, 0, 0);
    }

    private void OnPluginStateChanged() => Volatile.Read(ref panel)?.SignalSnapshot();

    private bool TryNavigatePlugin(PanelPluginDestination destination, OpenRevo.PluginHost.PluginLease lease) =>
        !stopping && panelCommands.PresentationDemand is (PanelPresentationDemand.Controls or PanelPresentationDemand.Overview) &&
        Volatile.Read(ref panel)?.TryNavigate(destination, lease) == true;

    private async Task RestorePluginsAsync()
    {
        try
        {
            await pluginReady.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            await maintenanceGate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (stopping || dataExitRequired) return;
                if (debugAvailable)
                {
                    try { await ConfigureLoggingAsync(logLevel, logFilter).ConfigureAwait(false); }
                    catch (Exception error) { DiagnosticLog.Failure("resident.logging.start_failed", error); }
                }
                await plugins.LoadApprovedAsync().ConfigureAwait(false);
            }
            finally { maintenanceGate.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticLog.Failure("resident.plugin.restore.failed", error); }
    }

    private void ObserveHardwareOsd()
    {
        if (stopping) return;
        lock (observedOsd)
        {
            var hardware = runtime.Hardware;
            int? mode = hardware.Active ? hardware.CurrentMode : null;
            bool? fan = hardware.Active ? hardware.State?.FanBoost : null;
            bool modeChanged = observedMode.HasValue && mode is >= 1 and <= 4 && mode != observedMode;
            bool fanChanged = observedFanBoost.HasValue && fan.HasValue && fan != observedFanBoost;
            observedMode = mode is >= 1 and <= 4 ? mode : null;
            observedFanBoost = fan;
            if (modeChanged)
                QueueOsd(OsdMessage.FromFeature("power_mode", mode.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            else if (fanChanged)
                QueueOsd(OsdMessage.FromFeature("fan_boost", fan == true ? "1" : "0"));
        }
    }

    private void OnRefreshTrialFinished(RefreshTrialResult result)
    {
        if (result.ConfirmedHertz is int hertz)
            QueueOsd(new((result.Kept ? "屏幕刷新率 · " : "已恢复刷新率 · ") + hertz + " Hz", "refresh", "#92B9FF"));
        Volatile.Read(ref panel)?.SignalSnapshot();
    }

    private void QueueOsd(OsdMessage message)
    {
        if (!osdSettings.Snapshot.Enabled || stopping) return;
        if (Interlocked.Exchange(ref pendingOsd, message) == null && !PostMessage(window, PresentOsd, 0, 0))
            Interlocked.Exchange(ref pendingOsd, null);
    }

    private void ShowPendingOsd()
    {
        if (Interlocked.Exchange(ref pendingOsd, null) is not { } next || stopping) return;
        var settings = osdSettings.Snapshot;
        if (!settings.Enabled) return;
        try
        {
            osd ??= new ResidentOsd();
            osd.Show(next, settings.Appearance);
            DiagnosticLog.Record("resident.osd.presented", new { next.Icon });
        }
        catch (Exception error)
        {
            osd?.Hide();
            DiagnosticLog.Failure("resident.osd.failed", error, throttle: true);
        }
    }

    private void RequestOsdConfiguration() => PostMessage(window, ConfigureOsd, 0, 0);

    private void RequestScreenOff()
    {
        if (!PostMessage(window, ScreenOff, 0, 0)) throw new IOException("无法提交息屏请求。");
    }

    private void ApplyOsdSettings()
    {
        if (stopping || !osdSettings.Snapshot.Enabled)
        {
            osd?.Hide();
            caps?.Dispose();
            caps = null;
            return;
        }
        if (caps != null) return;
        caps = new(locked => QueueOsd(OsdMessage.FromFeature("caps_lock", locked ? "1" : "0")));
        if (!caps.Installed) DiagnosticLog.Record("resident.osd.caps.unavailable", new { caps.Failure });
    }

    private void RequestShutdown(DownloadedUpdate? installer = null)
    {
        if (stopping) return;
        stopping = true;
        ApplyOsdSettings();
        runtime.Operations.StopAccepting();
        panel?.Stop();
        stop.Cancel();
        plugins.BeginShutdown();
        SetStatus("正在退出");
        shutdown = CompleteShutdownAsync(updates.Stop(), installer);
    }

    private async Task CompleteShutdownAsync(Task updateStopped, DownloadedUpdate? installer)
    {
        await maintenanceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            runtime.Operations.StopAccepting();
            try { await plugins.StopAllAsync(exiting: true).ConfigureAwait(false); }
            catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.plugin.stop.failed", error); }
            try { await Task.WhenAll(policies, sampling, pluginStartup, updateStopped, memorySampling).ConfigureAwait(false); }
            catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.loop_stop.failed", error); }
            await runtime.StopDesktopServicesAsync().ConfigureAwait(false);
        }
        catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.stop.failed", error); }
        finally
        {
            try { await runtime.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.release.failed", error); }
            finally { maintenanceGate.Release(); }
            try
            {
                if (Volatile.Read(ref panel) is { } child && !await child.Completion.ConfigureAwait(false))
                    exitCode = 1;
            }
            catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.panel.stop.failed", error); }
            if (installer != null && exitCode == 0)
            {
                try { updates.StartInstallerAfterExit(installer); }
                catch (Exception error) { exitCode = 1; DiagnosticLog.Failure("resident.update.handoff_failed", error); }
            }
            shutdownReady = true;
            PostMessage(window, FinishShutdown, 0, 0);
        }
    }

    private nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == nativeWake && nativeWake != 0 && !stopping)
            {
                OpenPanel();
                return 0;
            }
            switch (message)
            {
                case TrayCallback when !stopping && ((uint)lParam & 0xffff) is 0x0400 or 0x0401 or 0x0203:
                    OpenPanel();
                    return 0;
                case TrayCallback when !stopping && ((uint)lParam & 0xffff) is 0x007b or 0x0205:
                    ShowMenu();
                    return 0;
                case TrayCallback when shutdown == null && ((uint)lParam & 0xffff) is 0x0200 or 0x0406:
                    if (panelSettings.Snapshot.TrayTelemetry && hoverRead is not { IsCompleted: false })
                        hoverRead = RefreshHoverAsync();
                    return 0;
                case PresentOsd:
                    ShowPendingOsd();
                    return 0;
                case ConfigureOsd:
                    ApplyOsdSettings();
                    return 0;
                case ScreenOff when !stopping:
                    SessionPower.TurnOffScreen(hwnd);
                    return 0;
                case PresentTrayMenu when !stopping && !dataExitRequired:
                    ShowMenu();
                    return 0;
                case InstallUpdate when !stopping && !dataExitRequired:
                    if (updates.ClaimInstaller() is { } installer) RequestShutdown(installer);
                    return 0;
                case PanelCloseResult:
                    if (wParam != 0 && panel is { ExitObserved: true } exited)
                    {
                        panel = null;
                        var target = exited.TakePendingWakeAfterExit();
                        if (dataExitRequired) RequestShutdown();
                        else if (target.HasValue && !stopping) OpenPanel(target.Value);
                    }
                    else if (wParam == 0 && dataExitRequired) RequestShutdown();
                    return 0;
                case FinishShutdown when shutdownReady:
                    PostQuitMessage(0);
                    return 0;
                case 0x0010: // WM_CLOSE
                    RequestShutdown();
                    return 0;
                case 0x0002: // WM_DESTROY
                    PostQuitMessage(0);
                    return 0;
                case 0x0011: // WM_QUERYENDSESSION
                    return 1;
                case 0x0016 when wParam != 0: // WM_ENDSESSION
                    RequestShutdown();
                    return 0;
                case 0x007e: // WM_DISPLAYCHANGE
                case 0x0219 when wParam == 7: // DBT_DEVNODES_CHANGED
                    if (message == 0x007e) osd?.Hide();
                    DisplayTopology.InvalidateGpuIdentity();
                    runtime.Display.InvalidateTopology();
                    panelCommands.DisplayTopologyChanged();
                    Volatile.Read(ref panel)?.SignalSnapshot();
                    break;
                case 0x0218 when shutdown == null: // WM_POWERBROADCAST
                    if (wParam == 10) // PBT_APMPOWERSTATUSCHANGE
                    {
                        sampler.InvalidateDetails();
                        plugins.PublishPower();
                        Volatile.Read(ref panel)?.SignalSnapshot();
                    }
                    if (DesktopPowerNotification.ReadSuspension(message, wParam, lParam) is bool sleeping)
                    {
                        sampler.InvalidateDetails();
                        Volatile.Read(ref panel)?.SignalSnapshot();
                        _ = OnSuspensionAsync(sleeping);
                    }
                    break;
            }
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.message.failed", error, new { message }); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (menuOpen) return;
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        menuOpen = true;
        try
        {
            AppendMenu(menu, 1, 0, Volatile.Read(ref status));
            AppendMenu(menu, 0, 2, "打开控制面板");
            if (nativePanel)
            {
                AppendMenu(menu, 0, 3, "打开迷你面板");
                AppendMenu(menu, 0, 4, "打开完整面板");
                AppendMenu(menu, 0, 5, "设置与诊断");
                AppendMenu(menu, 0, 6, "检查更新");
            }
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 1, "退出 RevoCore 候选后台");
            if (!GetCursorPos(out var cursor)) return;
            SetForegroundWindow(window);
            uint command = TrackPopupMenu(menu, 0x0102, cursor.X, cursor.Y, 0, window, 0);
            PostMessage(window, 0, 0, 0);
            if (command == 1) RequestShutdown();
            if (command == 2) OpenPanel();
            if (command is >= 3 and <= 6)
                OpenPanel((PanelWakeTarget)(command - 2));
        }
        finally { menuOpen = false; DestroyMenu(menu); }
    }

    private void OpenPanel(PanelWakeTarget target = PanelWakeTarget.Default)
    {
        if (stopping || dataExitRequired) return;
        if (panel is { } current)
        {
            if (!current.Closing)
                AllowSetForegroundWindow((uint)current.ProcessId);
            current.SignalRaise(target);
            return;
        }
        try
        {
            var preferences = panelSettings.Snapshot;
            var draft = Volatile.Read(ref panelDraft) ?? new PanelDraft(Compact: preferences.LastMini);
            bool compact = target == PanelWakeTarget.Mini || target == PanelWakeTarget.Default &&
                (preferences.WakeMode == "mini" || preferences.WakeMode == "remember" && draft.Compact);
            panel = ResidentPanelProcess.Start(panelCommands, draft with { Compact = compact },
                RememberPanelDraft, nativePanel, installationDirectory != null, target,
                () => PostMessage(window, 0x0010, 0, 0),
                confirmed => PostMessage(window, PanelCloseResult, confirmed ? 1u : 0u, 0));
            AllowSetForegroundWindow((uint)panel.ProcessId);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.panel.start.failed", error);
            SetStatus("候选面板未能启动");
        }
    }

    private void RememberPanelDraft(PanelDraft draft)
    {
        Volatile.Write(ref panelDraft, draft);
        try
        {
            if (runtime.Operations.IsAccepting && panelSettings.Snapshot.LastMini != draft.Compact)
                panelSettings.RememberPanelShape(draft.Compact);
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.panel.shape_save_failed", error); }
    }

    private void WriteTooltip(nint buffer, uint capacity)
    {
        try
        {
            string tooltip = "RevoCore 候选后台 · " + Volatile.Read(ref status);
            if (panelSettings.Snapshot.TrayTelemetry && Volatile.Read(ref traySample) is { } sample)
                tooltip += "\n" + sample.ToTooltip();
            int length = (int)Math.Min((uint)tooltip.Length, capacity);
            if (length == 0) return;
            if (length < tooltip.Length && char.IsHighSurrogate(tooltip[length - 1])) length--;
            Marshal.Copy(tooltip.ToCharArray(0, length), 0, buffer, length);
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.tray.tooltip_failed", error); }
    }

    public void Dispose()
    {
        plugins.StateChanged -= OnPluginStateChanged;
        runtime.PerformanceInput.PhysicalModeCompleted -= OnPhysicalModeCompleted;
        runtime.Display.TrialFinished -= OnRefreshTrialFinished;
        caps?.Dispose();
        osd?.Dispose();
        power?.Dispose();
        panel?.ReleaseWakeState();
        stop.Dispose();
    }

}
