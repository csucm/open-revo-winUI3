using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly PanelClient? residentClient;
    private PanelSnapshot? residentSnapshot;
    private (int Hertz, string Panel)? residentRefreshReadback;
    private int residentDisplayGeneration;
    private bool residentDisplayVisible, residentDisplayRead, residentDisplayReading;
    private int? DisplayedRefreshHertz => displayState is { Active: true } display
        ? display.Hertz : residentRefreshReadback?.Hertz;
    private string? DisplayedRefreshPanel => displayState?.Panel ?? residentRefreshReadback?.Panel;
    private PanelLightingSnapshot? residentLighting;
    private PanelPowerSnapshot? residentPower;
    private PanelDraft? residentDraft;
    private OverviewTelemetry? residentOverviewTelemetry;
    private PanelOverviewTelemetry? residentOverview;
    private ResidentSensorReader? residentSensors;
    private PanelSensorPlan? lastQueuedSensorPlan, residentReadingPlan;
    private ResidentSensorFields lastQueuedSensorFields, residentReadingFields;
    private ResidentSensorReading? residentReading;
    private ResidentSensorCapabilities? residentGpuCapabilities;
    private PanelPresentationDemand residentSentDemand;
    private bool residentSendingDemand;
    private long residentWake;
    private readonly Dictionary<ResidentActionKey, int> residentPending = [];
    private bool residentDetaching, residentAllowClose, residentActivated, residentRenderQueued, residentPowerReading;
    private bool residentGpuRead, residentLightingRead;
    private bool PanelHardwareActive => residentClient != null ? residentSnapshot?.Active == true : hardware.Active;
    private int? PanelMode => residentClient != null ? residentSnapshot?.Mode : hardware.CurrentMode;
    private int? PanelBatteryLimit => residentClient != null ? residentSnapshot?.BatteryLimit : hardware.State?.Upper;
    private bool? PanelFanBoost => residentClient != null ? residentSnapshot?.FanBoost ?? ec.FanBoost : hardware.State?.FanBoost ?? ec.FanBoost;
    private bool PanelSupportsBattery => residentClient != null ? residentSnapshot?.CanSetBatteryLimit == true : hardware.SupportsBattery;
    private bool PanelSupportsPerformance => residentClient != null ? residentSnapshot?.CanSelectMode == true : hardware.SupportsPerformance;
    private bool PanelSupportsCustom => residentClient != null ? residentSnapshot?.Device?.SupportsCustomTuning == true : hardware.SupportsCustomTuning;
    private bool PanelSupportsBoost => residentClient != null ? residentSnapshot?.CanToggleFanBoost == true : hardware.SupportsFanBoost;
    private bool PanelSupportsFn => residentClient != null ? residentSnapshot?.Device?.SupportsFnLock == true : hardware.SupportsDeclaredFnLock;
    private bool CanShowTouchpadControl => residentClient != null
        ? residentSnapshot?.Device?.SupportsTouchpad != false : hardware.SupportsTouchpad;
    private readonly record struct ResidentActionKey(PanelOperation Operation, object? Target = null);

    private bool ResidentSessionChanging => residentDetaching ||
        residentPending.ContainsKey(new(PanelOperation.ExitResident)) ||
        residentPending.ContainsKey(new(PanelOperation.Maintenance, PanelMaintenanceAction.Migrate));

    private bool PanelActionBusy(PanelOperation operation, object? target = null) => residentClient == null
        ? hardwareBusy : ResidentSessionChanging || residentPending.ContainsKey(new(operation, target));

    private bool PanelHardwareActionBusy(PanelOperation operation, object? target = null) =>
        PanelActionBusy(operation, target) || PanelActionBusy(PanelOperation.Takeover);

    private bool PanelTuningBusy => PanelHardwareActionBusy(PanelOperation.Tuning) || PanelActionBusy(PanelOperation.SelectMode);

    private bool PanelPowerPolicyBusy(string key) =>
        PanelActionBusy(PanelOperation.Desktop, (typeof(PanelSetPowerPolicy), key));

    private static ResidentActionKey ResidentActionFor(PanelOperation operation, PanelDesktopRequest? desktop,
        PanelPreferenceRequest? preference, PanelMaintenanceRequest? maintenance, PanelApplicationRequest? application) => operation switch
    {
        PanelOperation.Release => new(PanelOperation.Takeover),
        PanelOperation.SetDeclaredFnLock => new(PanelOperation.ToggleFnLock),
        PanelOperation.ReadGpu => new(PanelOperation.SelectGpu),
        PanelOperation.KeepRefresh or PanelOperation.RestoreRefresh => new(PanelOperation.BeginRefresh),
        PanelOperation.Preferences => new(operation, preference?.Kind),
        PanelOperation.Application => new(operation, application?.Action),
        PanelOperation.Maintenance => new(operation, maintenance?.Action == PanelMaintenanceAction.Reset
            ? PanelMaintenanceAction.Migrate : maintenance?.Action),
        PanelOperation.Desktop => new(operation, desktop switch
        {
            PanelSetPowerPolicy policy => (typeof(PanelSetPowerPolicy), policy.Key),
            PanelSetLightingZone zone => (typeof(PanelSetLightingZone), zone.Zone),
            PanelSetKeyboardRgb => typeof(PanelUndoKeyboardRgb),
            PanelImportIcc => typeof(PanelApplyIcc),
            PanelRecoverPowerPlans => typeof(PanelSelectPowerPlan),
            _ => desktop?.GetType()
        }),
        _ => new(operation)
    };

    private void InitializeResidentState(PanelClient client)
    {
        residentWake = client.Attached.Wake!.Value;
        var draft = residentDraft = client.Attached.Draft!;
        residentColorEdit = draft.Color;
        RestoreResidentTuningDraft(draft.Tuning);
        if (client.Attached.WakeTarget is not (PanelWakeTarget.Settings or PanelWakeTarget.Updates) &&
            draft.View is { } view)
        {
            selectedLightingZone = view.LightingZone;
            customTuningOpen = view.Page == "tuning";
            colorTuningOpen = view.Page == "color";
            currentPage = view.Page switch
            {
                "tuning" or "color" => "display",
                "lightingzone" => "devices",
                _ => view.Page
            };
            tuningReturnPage = view.TuningReturnPage;
            tuningReturnMini = view.TuningReturnMini;
        }
        if (draft.OverviewDraft != null) overviewDraft = [.. draft.OverviewDraft];
        if (draft.MiniDraft is { } miniEdit)
        {
            miniDraft = new(MiniLayout.CurrentVersion, miniEdit.Top, miniEdit.Quick);
            miniQuickPoolOrder = MiniLayout.QuickIds.Where(id => !miniEdit.Quick.Contains(id)).ToList();
            miniReplaceUnrecognized = draft.ReplaceMiniLayout;
        }
        ApplyResidentSnapshot(client.Attached.Snapshot!, updateUi: false);
    }

    private void AttachResidentPanel()
    {
        residentClient!.Notice += notice => DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || residentDetaching) return;
            if (notice.Kind == PanelFrameKind.Raise)
            {
                residentWake = notice.Wake!.Value;
                RaiseResidentPanel(notice.WakeTarget);
            }
            if (notice.Destination is { } destination) PresentResidentPluginNavigation(destination);
            if (notice.Snapshot is { } snapshot) ApplyResidentSnapshot(snapshot);
        });
        residentClient.Disconnected += () => DispatcherQueue.TryEnqueue(CloseResidentPanel);
        residentClient.StartReader();
        scroll.ViewChanged += (_, _) => QueueResidentSensors(residentSnapshot?.Details?.Sampling);
        Activated += (_, args) =>
        {
            residentActivated = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!residentActivated && CanAutoHideResident())
                DispatcherQueue.TryEnqueue(() => { if (!residentActivated && CanAutoHideResident()) HideToTray(); });
        };
    }

    private bool CanAutoHideResident() => mini && systemPreferences.MiniAutoHide && !closed && !dialogOpen &&
        !residentDetaching && !panelShapeChanging && !panelMinimizeQueued && AppWindow.IsVisible;

    private void InitializeResidentPresentation()
    {
        AlignCaption();
        root.XamlRoot.Changed += (_, _) =>
        {
            if (Math.Abs(root.XamlRoot.RasterizationScale - captionScale) > 0.001) AlignCaption();
        };
        var target = residentClient!.Attached.WakeTarget;
        if (target is PanelWakeTarget.Settings or PanelWakeTarget.Updates) RaiseResidentPanel(target);
        else
        {
            Render();
            if (customTuningOpen && !mini) _ = PaintReadTuning(automatic: true);
        }
        PrimeResidentReads();
    }

    private void RaiseResidentPanel(PanelWakeTarget target)
    {
        if (target is PanelWakeTarget.Settings or PanelWakeTarget.Updates)
            PaintNavigate(target == PanelWakeTarget.Settings ? "settings" : "updates");
        bool? preferred = target switch
        {
            PanelWakeTarget.Mini => true,
            PanelWakeTarget.Default when systemPreferences.WakeMode == "remember" => null,
            PanelWakeTarget.Default => systemPreferences.WakeMode == "mini",
            _ => false
        };
        if (preferred is bool compact)
        {
            if (panelShapeChanging) SetPanelGoal(compact);
            else if (mini != compact) ToggleMini();
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(captionWindow);
    }

    private PanelPresentationDemand ResidentDemand()
    {
        if (!initialized || residentDetaching || residentSnapshot?.Ready != true || !IsVisibleNow())
            return PanelPresentationDemand.Hidden;
        if (mini)
            return !PaintIntegratedOnly && MiniSaved.Top.Contains("gpu") ? PanelPresentationDemand.MiniGpu : PanelPresentationDemand.Mini;
#if NATIVE_RESIDENT_CORE
        if (PaintRoute == "devices") return PanelPresentationDemand.Lighting;
#endif
        return PaintRoute == "overview" ? PanelPresentationDemand.Overview : PanelPresentationDemand.Controls;
    }

    private async void PublishResidentDemand()
    {
        PrimeResidentReads();
        if (residentClient == null || closed || residentDetaching || residentSendingDemand) return;
        residentSendingDemand = true;
        try
        {
            while (!closed && !residentDetaching)
            {
                var demand = ResidentDemand();
                if (demand == residentSentDemand) break;
                var result = await residentClient.SendAsync(PanelOperation.PresentationDemand, demand: demand);
                if (result.Outcome != PanelOutcome.Applied) break;
                residentSentDemand = demand;
                if (!closed && !residentDetaching && result.Snapshot is { } snapshot) ApplyResidentSnapshot(snapshot);
            }
        }
        catch (Exception error) { if (!closed && !residentDetaching) actions.Add("后台读数订阅：" + error.Message); }
        finally { residentSendingDemand = false; }
    }

    private void ApplyResidentTelemetry(PanelDetailedTelemetry? detail)
    {
        ec = detail?.Ec is { } fans
            ? new(fans.CpuFan, fans.GpuFan, fans.BatteryLimit, fans.FanBoost, fans.Status)
            : new(null, null, null, null, "待读取");
        temperature = detail?.Temperature is { } thermal ? new(thermal.Cpu, thermal.Gpu, thermal.CpuSource) : new(null, null);
        var plan = detail?.Sampling;
        var reading = plan is not null && residentReadingPlan is { } priorPlan &&
            priorPlan.Generation == plan.Generation && priorPlan.Demand == plan.Demand &&
            priorPlan.AllowGpu == plan.AllowGpu && priorPlan.SampleGpu == plan.SampleGpu &&
            residentReadingFields == VisibleSensorFields(plan.Demand) &&
            DateTimeOffset.UtcNow - priorPlan.At <= TimeSpan.FromSeconds(Math.Max(interval * 2, 4))
            ? residentReading : null;
        if (reading != null && detail?.Temperature is { } baseline)
            temperature = new(baseline.Cpu ?? reading.CpuTemperature,
                plan?.SampleGpu == true ? reading.GpuTemperature : null,
                baseline.Cpu.HasValue ? baseline.CpuSource : "Windows 处理器温区");
        var shownPower = detail?.Power;
        if (shownPower?.GpuTarget is { } target && plan?.SampleGpu == true &&
            residentReadingPlan?.Generation == plan.Generation &&
            residentGpuCapabilities is { Supported: true, BaseMaxWatts: > 0, BoostMaxWatts: > 0 } caps)
        {
            int baseWatts = caps.BaseMaxWatts;
            int? customBase = caps.ConfigurableBase
                ? target.BaseOffset is int offset && caps.FloorWatts + offset <= baseWatts ? caps.FloorWatts + offset : null
                : baseWatts;
            int? tgp = shownPower.Mode switch
            {
                1 => baseWatts,
                2 or 3 => baseWatts + caps.BoostMaxWatts,
                4 when customBase is int custom && target.BoostOn == false => custom,
                4 when customBase is int custom && target.BoostOn == true &&
                    target.BoostWatts is int boost && boost <= caps.BoostMaxWatts => custom + boost,
                _ => null
            };
            shownPower = shownPower with { TgpWatts = tgp, BaseTgpWatts = baseWatts, MaximumBoostWatts = caps.BoostMaxWatts };
        }
        overviewPowerTargets = shownPower is { } power
            ? new(power.Mode, power.Pl1Watts, power.TgpWatts, power.BaseTgpWatts, power.MaximumBoostWatts, power.Detail, power.At)
            : null;
        overviewPowerError = shownPower?.Detail ?? "后台尚未取得功耗目标。";
        residentOverviewTelemetry = null;
        residentOverview = plan == null ? detail?.Overview
            : reading != null && (reading.Cpu != null || reading.Gpu != null)
                ? new(reading.At, true, false, reading.Cpu ?? new(null, null, null, false), plan.AllowGpu ? reading.Gpu : null)
                : null;
        if (residentOverview is not { } overview) return;
        var cpu = overview.Cpu;
        var gpu = overview.Gpu;
        OverviewGpu adapter = gpu == null ? new(null, null, null, null, null, null, null)
            : new(gpu.Temperature, gpu.CoreMhz, gpu.MemoryMhz, gpu.Usage, gpu.MemoryTotalBytes, gpu.MemoryUsedBytes, gpu.PowerW)
            {
                AdapterName = gpu.AdapterName, AdapterKind = gpu.AdapterKind, SharedLimitBytes = gpu.SharedLimitBytes,
                SharedUsedBytes = gpu.SharedUsedBytes
            };
        residentOverviewTelemetry = new(new(cpu.Mhz, cpu.Usage, cpu.PowerW, cpu.PowerEstimated), adapter, gpu != null, "", "");
    }

    private ResidentSensorFields VisibleSensorFields(PanelPresentationDemand demand)
    {
        if (demand is PanelPresentationDemand.Mini or PanelPresentationDemand.MiniGpu)
            return new(false, MiniSaved.Top.Contains("performance"), demand == PanelPresentationDemand.MiniGpu,
                false, false, false);
        if (demand != PanelPresentationDemand.Overview || overviewBoard?.XamlRoot == null || scroll.ActualHeight <= 0)
            return default;

        bool cpu = false, gpu = false, memory = false, target = false;
        foreach (var child in overviewBoard.Children)
        {
            if (child is not FrameworkElement { Tag: string id } tile || tile.ActualHeight <= 0 ||
                id is not ("cpu" or "gpu" or "vram" or "performance")) continue;
            var point = tile.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.Y >= scroll.ActualHeight || point.Y + tile.ActualHeight <= 0) continue;
            cpu |= id == "cpu";
            gpu |= id == "gpu";
            memory |= id == "vram";
            target |= id == "performance";
        }
        return new(cpu, cpu, gpu, gpu, memory, target);
    }

    private void QueueResidentSensors(PanelSensorPlan? plan)
    {
        if (closed || residentDetaching) return;
        if (plan == null || ResidentDemand() != plan.Demand)
        {
            if (residentSensors != null && lastQueuedSensorPlan is { } previous &&
                lastQueuedSensorFields.Any && residentSensors.TryQueue(previous, default, _ => { }))
            {
                lastQueuedSensorPlan = null;
                lastQueuedSensorFields = default;
            }
            return;
        }
        var fields = VisibleSensorFields(plan.Demand);
        if (lastQueuedSensorPlan == plan && lastQueuedSensorFields == fields) return;
        if (!fields.Any && residentSensors == null) return;
        residentSensors ??= new ResidentSensorReader();
        if (residentSensors.TryQueue(plan, fields, reading => DispatcherQueue.TryEnqueue(() =>
        {
            var current = residentSnapshot?.Details?.Sampling;
            if (closed || residentDetaching || current == null || ResidentDemand() != plan.Demand ||
                current.Generation != plan.Generation || current.Demand != plan.Demand ||
                current.AllowGpu != plan.AllowGpu || current.SampleGpu != plan.SampleGpu ||
                VisibleSensorFields(plan.Demand) != fields) return;
            if (residentReadingPlan?.Generation != plan.Generation || !plan.SampleGpu)
                residentGpuCapabilities = null;
            if (plan.ProbeCaps && fields.GpuTarget) residentGpuCapabilities = reading.Capabilities;
            residentReadingPlan = plan;
            residentReadingFields = fields;
            residentReading = reading;
            ApplyResidentTelemetry(residentSnapshot?.Details);
            UpdateOverviewValues();
        })))
        {
            lastQueuedSensorPlan = plan;
            lastQueuedSensorFields = fields;
        }
    }

    private void ApplyResidentSnapshot(PanelSnapshot next, bool updateUi = true)
    {
        var previous = residentSnapshot;
        if (previous != null && previous.Revision >= next.Revision) return;
        residentSnapshot = next;
        ApplyResidentTheme(next.Theme);
        var preferences = next.Preferences;
        selectedTheme = preferences.Theme;
        interval = preferences.SampleSeconds;
        lowRefreshOnBattery = next.LowRefreshOnBattery;
        osdEnabled = next.Osd.Enabled;
        systemPreferences = systemPreferences with
        {
            WakeMode = preferences.WakeMode, MiniAutoHide = preferences.MiniAutoHide, TrayTelemetry = preferences.TrayTelemetry,
            UnlockBatteryLimit = preferences.BatteryOverride, OsdTheme = next.Osd.Appearance.Theme,
            OsdScale = next.Osd.Appearance.Scale, OsdPosition = next.Osd.Appearance.Position,
            MotionSpeed = next.Osd.Appearance.Motion
        };
        var display = next.Display;
        if (previous?.Active != next.Active)
        {
            residentGpuRead = previous == null && next.Gpu.Fresh;
            residentLightingRead = false;
        }
        if (previous?.Display.Active == true && !display.Active)
            InvalidateResidentRefresh();
        displayState = display.Hertz is int hz && display.Device is { } device
            ? new(display.Width, display.Height, hz, display.Rates, device, display.Panel ?? "未知", display.Active)
            : null;
        screenBrightness = display.Brightness;
        if (displayState?.Active == true) residentRefreshReadback = null;
        if (next.Device is { } description)
            identity = new("未知", description.Product, description.Board, description.Processor, []);
        if (next.SystemSummary is { } sample)
            latest = new(sample.CpuPercent, sample.UsedMemoryGiB, sample.TotalMemoryGiB, sample.BatteryPercent,
                sample.OnAc, sample.Charging, 0, 0, sample.SampledAt);
        quickState = new(next.Switches.Camera, next.Switches.CameraAvailable, next.Switches.Microphone, next.Switches.WinLock);
        extraSwitches = new(next.Switches.Wifi, next.Switches.Bluetooth, next.Switches.Touchpad, next.Switches.FnLock)
        { MonochromeLevel = next.Switches.MonochromeLevel };
        if (next.Switches.MonochromeAt is { } monochromeAt && monochromeAt > monochromeReadAt)
        {
            UpdateMonochromeReadback(extraSwitches);
            if (keyboard.Kind == "White") monochromeReadAt = monochromeAt;
        }
        ApplyResidentTelemetry(next.Details);
        gpuModeLabel = !next.Gpu.Fresh ? "待读取" : next.Gpu.RestartPending ? "待重启" : OriginalGpuName(next.Gpu.FirmwareMode);
        if (previous == null || !previous.Layouts.Overview.SequenceEqual(next.Layouts.Overview)) overviewSaved = [.. next.Layouts.Overview];
        var miniLayout = next.Layouts.Mini;
        if (previous == null || !previous.Layouts.Mini.Top.SequenceEqual(miniLayout.Top) ||
            !previous.Layouts.Mini.Quick.SequenceEqual(miniLayout.Quick) || previous.Layouts.MiniIssue != next.Layouts.MiniIssue)
            miniLayoutRead = new(new(MiniLayout.CurrentVersion, miniLayout.Top, miniLayout.Quick), next.Layouts.MiniIssue);
        if (!updateUi || !initialized || closed) return;
        if (root.RequestedTheme != (ElementTheme)selectedTheme) root.RequestedTheme = (ElementTheme)selectedTheme;
        MotionDesign.Configure(systemPreferences.MotionSpeed);
        UpdateThemeToggle();
        UpdateBrightnessValues();
        UpdateOverviewValues();
        QueueResidentSensors(next.Details?.Sampling);
        if (next.Ready && PaintRoute is "power" or "sleep" or "settings") _ = ReadResidentPowerAsync();
        if (next.Ready && previous?.Ready != true && PaintRoute == "settings") _ = ReadResidentStartupAsync();
        if (next.Ready && previous?.Ready != true && customTuningOpen && !mini) _ = PaintReadTuning(automatic: true);
        if (!mini && PaintRoute is ("display" or "color")) _ = ReadResidentColorAsync();
        if (!mini && PaintRoute is ("storage" or "diagnostics") && previous?.Ready != true)
            _ = ReadResidentMaintenanceSettingsAsync();
        PublishResidentDemand();
        SyncResidentPluginPage();
    }

    private bool CanReadResidentRefresh() =>
        initialized && !closed && !residentDetaching && IsVisibleNow()
        && displayState?.Active != true
        && (mini ? MiniSaved.Top.Contains("refresh") :
            PaintRoute == "display" || PaintRoute == "overview" && overviewSaved.Contains("display"));

    private void InvalidateResidentRefresh()
    {
        residentDisplayGeneration++;
        residentDisplayRead = false;
        if (residentRefreshReadback.HasValue)
        {
            residentRefreshReadback = null;
            DispatcherQueue.TryEnqueue(() => { if (!closed && !residentDetaching) RefreshPaintedState(); });
        }
    }

    private async Task ReadResidentDisplayOnDemandAsync()
    {
        int generation = residentDisplayGeneration;
        residentDisplayRead = residentDisplayReading = true;
        try
        {
            await SendResidentAsync(PanelOperation.ReadDisplay, backgroundRead: true);
            if (generation != residentDisplayGeneration || !CanReadResidentRefresh()) return;
            var reading = await Task.Run(DisplayControl.ReadCurrentRefresh);
            if (generation != residentDisplayGeneration || !CanReadResidentRefresh()) return;
            residentRefreshReadback = reading;
            RefreshPaintedState();
            UpdateDisplayValues();
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.display.read_only", error, throttle: true); }
        finally
        {
            residentDisplayReading = false;
            if (generation != residentDisplayGeneration) PrimeResidentReads();
        }
    }

    private void PrimeResidentReads()
    {
        if (residentSnapshot?.Ready != true || residentDetaching || !initialized || closed || !IsVisibleNow()) return;
        string route = PaintRoute;
        bool miniEditor = !mini && route == "minicustomization";
        bool showGpu = mini ? MiniSaved.Top.Contains("gpu") :
            miniEditor || route == "display" || route == "overview" && overviewSaved.Contains("gpu");
        bool showLighting = mini ? MiniSaved.Top.Contains("keyboard") :
            miniEditor || route is "devices" or "lightingzone";
        bool showDisplay = mini ? MiniSaved.Top.Contains("refresh") || MiniSaved.Top.Contains("brightness") :
            route is "display" or "color" || route == "overview" && overviewSaved.Contains("display");
        if (showDisplay != residentDisplayVisible)
        {
            residentDisplayVisible = showDisplay;
            InvalidateResidentRefresh();
        }
        if (showGpu && !residentGpuRead)
        {
            residentGpuRead = true;
            _ = ReadResidentDeviceAsync(PanelOperation.ReadGpu);
        }
        if (showLighting && !residentLightingRead)
        {
            residentLightingRead = true;
            _ = ReadResidentDeviceAsync(PanelOperation.Desktop, new PanelReadDesktop(PanelDesktopGroup.Lighting));
        }
        if (showDisplay && !residentDisplayRead && !residentDisplayReading &&
            (displayState?.Active != true || !screenBrightness.HasValue))
            _ = ReadResidentDisplayOnDemandAsync();
    }

    private async Task ReadResidentDeviceAsync(PanelOperation operation, PanelDesktopRequest? desktop = null)
    {
        try
        {
            if (residentDetaching || closed) return;
            await SendResidentAsync(operation, desktop: desktop, backgroundRead: true);
        }
        catch (Exception error) { if (!closed) actions.Add("后台状态读取：" + error.Message); }
    }

    private void ApplyResidentKeyboardReadback(PanelSetKeyboardRgb? rgb)
    {
        if (rgb == null) return;
        rgbLevel = rgb.Level;
        if (rgb.Level > 0)
        {
            lastLightingLevel = rgb.Level;
            if (!rgb.Rainbow) customRgb = rgb.Rgb;
        }
        rgbMode = rgb.Level == 0 ? "关闭背光" : rgb.Rainbow ? "彩虹循环" : "静态单色";
    }

    private void ApplyResidentLighting(PanelLightingSnapshot lights, bool keyboardReply)
    {
        string priorKind = keyboard.Kind;
        bool firstLighting = residentLighting == null;
        if (keyboardReply && lights.Zones.Length == 0 && residentLighting is { } previous)
            lights = lights with { Zones = previous.Zones };
        residentLighting = lights;
        if (lights.Keyboard is { } model)
        {
            keyboard = new(model.Kind, model.Name, model.Kind is "SingleZone" or "4Zone" or "PerKey", model.Kind == "4Zone");
            lightingReadFresh = model.ConfirmedRgb != null;
            if (!ResidentKeyboardEditing) ApplyResidentKeyboardReadback(model.ConfirmedRgb);
            monochromeLevels = model.MonochromeLevels;
            monochromeReadback = model.MonochromeReadback;
            monochromeReadAt = DateTimeOffset.Now;
        }
        bool showingKeyboard = mini ? MiniSaved.Top.Contains("keyboard") : currentPage == "devices";
        if (initialized && !closed && showingKeyboard && (keyboard.Kind != priorKind || firstLighting))
            QueueResidentRender();
        else if (initialized && !ResidentKeyboardEditing) RefreshPaintedState();
    }

    private void QueueResidentRender()
    {
        if (residentRenderQueued || closed || residentDetaching) return;
        residentRenderQueued = true;
        _ = RenderResidentWhenReadyAsync();
    }

    private async Task RenderResidentWhenReadyAsync()
    {
        try
        {
            while (!closed && !residentDetaching && (panelShapeChanging || dialogOpen)) await Task.Delay(80);
            if (!closed && !residentDetaching) Render();
        }
        catch (Exception error) { if (!closed) actions.Add("后台状态绘制：" + error.Message); }
        finally { residentRenderQueued = false; }
    }

    private async Task ReadResidentPowerAsync(bool force = false)
    {
        if (residentPowerReading || residentDetaching || closed || residentSnapshot?.Ready != true ||
            !force && residentPower != null) return;
        residentPowerReading = true;
        try
        {
            await SendResidentAsync(PanelOperation.Desktop, desktop: new PanelReadDesktop(PanelDesktopGroup.Power),
                backgroundRead: true);
        }
        catch (Exception error) { if (!closed) actions.Add("后台电源状态：" + error.Message); }
        finally { residentPowerReading = false; }
    }

    private async Task<PanelFrame> SendResidentAsync(PanelOperation operation, int? value = null, int? expected = null,
        Guid? trial = null, PanelDesktopRequest? desktop = null, PanelPreferenceRequest? preference = null,
        PanelLayoutEdit? layout = null, PanelOsdAppearance? osdAppearance = null, bool backgroundRead = false, byte[]? icc = null,
        PanelTuningRequest? tuning = null, byte[]? tuningImport = null, PanelPluginRequest? plugin = null,
        PanelMaintenanceRequest? maintenance = null, PanelApplicationRequest? application = null)
    {
        bool keyboardEdit = operation == PanelOperation.Desktop && desktop is PanelSetKeyboardRgb;
        bool settlingBrightness = operation == PanelOperation.SetBrightness && brightnessApplying;
        if (residentClient == null || closed || residentDetaching && !keyboardEdit && !settlingBrightness)
            throw new IOException("面板正在关闭，请从托盘重新打开。");
        bool track = !backgroundRead && !keyboardEdit;
        var action = ResidentActionFor(operation, desktop, preference, maintenance, application);
        if (track)
        {
            int pending = residentPending.GetValueOrDefault(action);
            // Color sliders already coalesce edits and can send a newer draft while the previous one settles.
            if (ResidentSessionChanging && !settlingBrightness || pending != 0 && desktop is not PanelSetColor)
                return new(PanelProtocol.Revision, PanelFrameKind.Result, Outcome: PanelOutcome.Busy,
                    Message: "此操作仍在处理，请等待回执。");
            residentPending[action] = pending + 1;
        }
        try
        {
            if (track) RefreshPaintedState();
            var result = await residentClient.SendAsync(operation, value, expected: expected, trialId: trial, desktop: desktop,
                preference: preference, layout: layout, osdAppearance: osdAppearance, icc: icc,
                tuning: tuning, tuningImport: tuningImport, plugin: plugin, maintenance: maintenance, application: application);
            if (!closed && result.Snapshot is { } snapshot) ApplyResidentSnapshot(snapshot);
            if (!closed && result.Desktop?.Lighting is { } lights)
                ApplyResidentLighting(lights, desktop is PanelSetKeyboardRgb or PanelUndoKeyboardRgb);
            if (!closed && result.Desktop?.Color is { } color) ApplyResidentColor(color);
            if (!closed && result.Desktop?.Power is { } power)
            {
                residentPower = power;
                RefreshPaintedState();
            }
            return result;
        }
        finally
        {
            if (track)
            {
                if (residentPending[action] == 1) residentPending.Remove(action);
                else residentPending[action]--;
                RefreshPaintedState();
            }
        }
    }

    private async Task RunResidentActionAsync(PanelOperation operation, int? value = null, int? expected = null,
        PanelDesktopRequest? desktop = null)
    {
        var result = await SendResidentAsync(operation, value, expected, desktop: desktop);
        if (result.Outcome is PanelOutcome.Rejected or PanelOutcome.Busy or PanelOutcome.Unknown or PanelOutcome.ConfigurationSaveFailed)
            await ShowMessage("后台操作未完成", result.Message ?? "请刷新设备状态后重试。");
    }

    private async Task SetResidentPreferenceAsync(PanelPreferenceRequest preference) =>
        RequireResidentResult(await SendResidentAsync(PanelOperation.Preferences, preference: preference), PanelOutcome.ConfigurationSaved);

    private async Task SetResidentBrightnessAsync(int value) =>
        RequireResidentResult(await SendResidentAsync(PanelOperation.SetBrightness, value), PanelOutcome.ReadbackConfirmed);

    private async Task SaveResidentLayoutAsync(PanelLayoutEdit layout) =>
        RequireResidentResult(await SendResidentAsync(PanelOperation.Layout, layout: layout), PanelOutcome.ConfigurationSaved);

    private static void RequireResidentResult(PanelFrame result, PanelOutcome expected)
    {
        if (result.Outcome != expected) throw new IOException(result.Message ?? "后台未确认此操作。");
    }

    private async Task ToggleResidentLowRefreshAsync() =>
        RequireResidentResult(await SendResidentAsync(PanelOperation.Desktop, desktop: new PanelSetLowRefresh(!lowRefreshOnBattery)),
            PanelOutcome.ConfigurationSaved);

    private bool QueueResidentDetach()
    {
        if (residentDetaching || residentAllowClose || closed || dialogOpen || settingsDataBusy ||
            dataStorageExitPending || diagnosticExportBusy) return false;
        residentDetaching = true;
        colorDebounce?.Cancel();
        _ = DetachResidentPanelAsync();
        return true;
    }

    private async Task DetachResidentPanelAsync()
    {
        try
        {
            HidePanelHandoff();
            overviewDrag?.Cancel();
            CancelMiniDrags();
            await residentKeyboardWork;
            if (brightnessWork is { } brightness) await brightness;
            while (!closed && (residentPending.Count != 0 || miniSaving || panelShapeChanging)) await Task.Delay(20);
            if (closed) return;
            foreach (var field in DetailDescendants(page).OfType<PaintField>().ToArray()) field.SaveDraftOnHide();
            captureResidentDeclaredDraft?.Invoke();
            var draft = (residentDraft ?? new()) with
            {
                Compact = mini, OverviewDraft = overviewDraft?.ToArray(),
                Keyboard = null, Zones = null, Brightness = null, Refresh = null, Monochrome = null,
                Color = residentColorEdit,
                Tuning = CaptureResidentTuningDraft(),
                MiniDraft = miniDraft is { } edit ? new(edit.Top, edit.Quick) : null,
                ReplaceMiniLayout = miniDraft != null && miniReplaceUnrecognized,
                View = new(PaintRoute, selectedLightingZone, tuningReturnPage == "overview" ? "overview" : "display", tuningReturnMini)
            };
            var result = await residentClient!.SendAsync(PanelOperation.Detach, draft: draft, observedWake: residentWake);
            RequireResidentResult(result, PanelOutcome.Detached);
            CloseResidentPanel();
        }
        catch (Exception error)
        {
            residentDetaching = false;
            if (residentKeyboardWork.IsFaulted) residentKeyboardWork = Task.CompletedTask;
            if (!closed) await ShowMessage("藏盘交接未完成", error.Message);
        }
    }

    private void CloseResidentPanel()
    {
        if (residentAllowClose || closed) return;
        residentAllowClose = true;
        Close();
    }

    private async Task ExitResidentFromPanelAsync()
    {
        if (miniDraft != null && !await CanLeaveMiniEditor()) return;
        await residentKeyboardWork;
        if (brightnessWork is { } brightness) await brightness;
        RequireResidentResult(await SendResidentAsync(PanelOperation.ExitResident), PanelOutcome.Applied);
        CloseResidentPanel();
    }
}
