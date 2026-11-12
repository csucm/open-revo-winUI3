using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public enum PerformanceMode { Office = 1, Balanced = 2, Turbo = 3, Custom = 4 }

public enum PerformanceOutcome
{
    Skipped, Busy, Closing, Cancelled, NeedsPreset, Rejected, ReadbackConfirmed,
    CommandSent, ConfigurationSaved, ConfigurationSaveFailed, Unknown
}

public enum PerformanceDesktopPolicy { SleepOffice, PowerLink }

public sealed record PerformanceResult(
    PerformanceOutcome Outcome,
    PerformanceMode? Mode = null,
    PerformanceMode? PreviousMode = null,
    PerformanceReceipt? Classic = null,
    TuningReceipt? Tuning = null,
    string? IndicatorWarning = null,
    Exception? Error = null,
    Exception? FollowUpError = null,
    bool ReportFailure = true);

public sealed record DesktopPerformanceOperation(Task<bool> Started, Task<PerformanceResult> Completion);

public sealed class PerformanceCommandService
{
    private readonly HardwareSession hardware;
    private readonly HardwareOperationQueue operations;
    private readonly Func<UpstreamCoreLibrary?> getBridge;
    private readonly Lazy<DesktopServicePreferences> preferences = new(DesktopServicePreferences.Load);
    private readonly PerformanceOverlayService overlay = new();
    private readonly object powerGate = new();
    private PerformanceReceipt? temporaryOffice;
    private DateTimeOffset lastBatteryCheck;
    private bool suspended, restoredOnAc;

    internal PerformanceCommandService(HardwareSession hardware, HardwareOperationQueue operations,
        Func<UpstreamCoreLibrary?> getBridge)
    {
        this.hardware = hardware;
        this.operations = operations;
        this.getBridge = getBridge;
    }

    public bool SleepOfficeEnabled => preferences.Value.SleepOffice;
    public bool PowerLinkEnabled => preferences.Value.PerformanceLink;
    public bool HasTemporaryOffice => Volatile.Read(ref temporaryOffice) != null;

    internal void ResetTakeoverState()
    {
        temporaryOffice = null;
        lastBatteryCheck = default;
        lock (powerGate) restoredOnAc = false;
    }

    public Task<PerformanceResult> SelectModeAsync(PerformanceMode mode, bool originalDcConfirmed,
        string? fallbackPresetId = null) =>
        RunAsync(() => mode == PerformanceMode.Custom
            ? ApplySavedCustom(fallbackPresetId)
            : SetFactory(mode, originalDcConfirmed));

    public Task<PerformanceResult> SetPluginModeAsync(PerformanceMode mode, Func<bool> authorized,
        CancellationToken cancellationToken) =>
        RunAsync(() => SetFactory(mode, mode == PerformanceMode.Turbo), wait: false, authorized, cancellationToken);

    public Task<PerformanceResult> CyclePhysicalKeyAsync(int code) => RunAsync(() =>
    {
        if (!hardware.Active || hardware.CoreDelegated || !hardware.SupportsPerformance) return new(PerformanceOutcome.Skipped);
        var before = (PerformanceMode)hardware.ProbePerformance().Mode;
        bool limited = !hardware.AllowBatteryPerformance && Telemetry.ReadAc() != true;
        var target = before switch
        {
            PerformanceMode.Office => PerformanceMode.Balanced,
            PerformanceMode.Balanced when !limited => PerformanceMode.Turbo,
            _ => PerformanceMode.Office
        };
        DiagnosticLog.Record("modekey.intent", new { code, before = (int)before, target = (int)target, batteryLimited = limited });
        return SetFactory(target, target == PerformanceMode.Turbo);
    }, wait: false);

    public Task<PerformanceResult> ApplyTuningAsync(TuningSnapshot baseline, TuningProfile draft) =>
        RunAsync(() => ApplyClassicTuning(baseline, draft));

    public Task<PerformanceResult> ApplyDeclaredTuningAsync(DeclaredTuningPreset preset) =>
        RunAsync(() => SendDeclaredTuning(preset));

    public bool ShouldCheckBattery => hardware.Active && !hardware.AllowBatteryPerformance && hardware.CurrentMode is 3 or 4;

    public Task<PerformanceResult> EnforceBatteryGuardAsync() => RunAsync(() =>
    {
        if (!ShouldCheckBattery || getBridge() == null || DateTimeOffset.UtcNow - lastBatteryCheck < TimeSpan.FromSeconds(3))
            return new(PerformanceOutcome.Skipped);
        lastBatteryCheck = DateTimeOffset.UtcNow;
        bool? original = hardware.CoreDelegated ? Telemetry.ReadAc() : hardware.ProbeOriginalAdapter();
        if (original != false || hardware.AllowBatteryPerformance) return new(PerformanceOutcome.Skipped);
        if (hardware.CoreDelegated)
            return Confirmed(hardware.TryApplyDelegatedMode((int)PerformanceMode.Balanced,
                mode => mode is 3 or 4 && !hardware.AllowBatteryPerformance));
        return hardware.ProbePerformance().Mode is 3 or 4 && !hardware.AllowBatteryPerformance
            ? SetFactory(PerformanceMode.Balanced, false)
            : new(PerformanceOutcome.Skipped);
    });

    public void ObserveSuspension(bool sleeping)
    {
        lock (powerGate)
        {
            if (sleeping == suspended) return;
            suspended = sleeping;
            restoredOnAc = false;
        }
    }

    public bool ShouldRestoreDeclaredOnAc(bool? ac)
    {
        lock (powerGate)
        {
            if (!hardware.Active || !suspended && ac == false) restoredOnAc = false;
            return hardware.Active && hardware.DeclaredCpuTuning != null && !suspended && ac == true && !restoredOnAc;
        }
    }

    public Task<PerformanceResult> RestoreDeclaredOnAcAsync() => RunAsync(() =>
    {
        lock (powerGate)
        {
            if (!hardware.Active || hardware.DeclaredCpuTuning == null || suspended || restoredOnAc || Telemetry.ReadAc() != true)
                return new(PerformanceOutcome.Skipped);
            restoredOnAc = true;
        }
        return hardware.SavedDeclaredTuning is { RestoreOnAc: true } preset
            ? SendDeclaredTuning(preset)
            : new(PerformanceOutcome.Skipped);
    });

    public Task<PerformanceResult> ApplyAutomaticOfficeAsync(bool sleeping) => RunAsync(() =>
    {
        if (!hardware.Active || hardware.CoreDelegated) return new(PerformanceOutcome.Skipped);
        PerformanceReceipt receipt;
        if (sleeping)
        {
            if (!SleepOfficeEnabled || temporaryOffice != null || hardware.ProbePerformance().Mode == (int)PerformanceMode.Office)
                return new(PerformanceOutcome.Skipped);
            temporaryOffice = receipt = hardware.SetTemporaryOfficeMode();
        }
        else
        {
            if (temporaryOffice is not { } previous) return new(PerformanceOutcome.Skipped);
            temporaryOffice = null;
            receipt = hardware.RestoreAutomaticOffice(previous);
        }
        var result = Confirmed(receipt);
        if (PowerLinkEnabled && getBridge() is { } bridge)
        {
            try { overlay.Confirm(overlay.Apply(bridge, receipt.After.Mode)); }
            catch (Exception error) { return result with { FollowUpError = error }; }
        }
        return result;
    });

    public bool ShouldSynchronizePowerLink =>
        PowerLinkEnabled && hardware.Active && getBridge() != null &&
        overlay.ShouldSynchronize(hardware.CurrentMode ?? 0, DateTimeOffset.UtcNow);

    public DesktopPerformanceOperation SynchronizePowerLink() => RunDesktopOperation(() =>
    {
        if (!PowerLinkEnabled || !hardware.Active || getBridge() is not { } bridge ||
            hardware.CurrentMode is not int mode || !overlay.ShouldSynchronize(mode, DateTimeOffset.UtcNow))
            return new(PerformanceOutcome.Skipped);
        PerformanceOverlayReceipt? receipt = null;
        try
        {
            hardware.ApplyDesktopService("power-overlay", () => receipt = overlay.Apply(bridge, mode));
            overlay.Confirm(receipt!);
            return new(PerformanceOutcome.ReadbackConfirmed, (PerformanceMode)mode);
        }
        catch (Exception error)
        {
            bool report = overlay.RecordFailure(mode, DateTimeOffset.UtcNow);
            return new(PerformanceOutcome.Unknown, (PerformanceMode)mode, Error: error, ReportFailure: report);
        }
    });

    public DesktopPerformanceOperation SetDesktopPolicy(PerformanceDesktopPolicy policy, bool enabled) => RunDesktopOperation(() =>
    {
        if (policy is not (PerformanceDesktopPolicy.SleepOffice or PerformanceDesktopPolicy.PowerLink))
            return Rejected(new ArgumentOutOfRangeException(nameof(policy)));
        if (getBridge() is not { } bridge) return new(PerformanceOutcome.Skipped);
        var settings = preferences.Value;
        bool link = policy == PerformanceDesktopPolicy.PowerLink;
        bool previous = link ? settings.PerformanceLink : settings.SleepOffice;
        PerformanceOverlayReceipt? receipt = null;
        Exception? saveError = null;
        try
        {
            hardware.ApplyDesktopService(link ? "power-link" : "power-office", () =>
            {
                if (link && enabled)
                    receipt = overlay.Apply(bridge, hardware.CurrentMode ?? throw new IOException("性能档位未读取。"));
                SetValue(enabled);
                try { settings.Save(); }
                catch (Exception error) { SetValue(previous); saveError = error; throw; }
            }, needsTakeover: !link);
        }
        catch (Exception) when (saveError != null)
        {
            return new(PerformanceOutcome.ConfigurationSaveFailed,
                receipt == null ? null : (PerformanceMode)receipt.Mode, Error: saveError);
        }
        if (receipt != null) overlay.Confirm(receipt);
        else if (link) overlay.Reset();
        return new(PerformanceOutcome.ConfigurationSaved, receipt == null ? null : (PerformanceMode)receipt.Mode);

        void SetValue(bool value)
        {
            if (link) settings.PerformanceLink = value;
            else settings.SleepOffice = value;
        }
    });

    private PerformanceResult SetFactory(PerformanceMode mode, bool originalDcConfirmed)
    {
        if (mode is < PerformanceMode.Office or > PerformanceMode.Turbo)
            return Rejected(new ArgumentOutOfRangeException(nameof(mode)));
        if (!hardware.Active || !hardware.SupportsPerformance)
            return Rejected(new InvalidOperationException("当前未接管硬件或设备不支持性能档位控制。"));
        return Confirmed(hardware.ApplyModeCommand(getBridge(), (int)mode, originalDcConfirmed));
    }

    private PerformanceResult ApplySavedCustom(string? fallbackPresetId)
    {
        if (!hardware.Active || !hardware.SupportsCustomTuning)
            return Rejected(new InvalidOperationException("当前未接管硬件或设备不支持自定义调校。"));
        if (hardware.DeclaredCpuTuning != null)
            return hardware.SavedDeclaredTuning is { } preset
                ? SendDeclaredTuning(preset) : new(PerformanceOutcome.NeedsPreset);
        var bridge = getBridge();
        if (bridge == null) return Rejected(new IOException("设备未连接。"));
        TuningSnapshot snapshot;
        TuningProfile profile;
        try
        {
            string? id = AppData.Load().SelectedTuningPreset ?? fallbackPresetId;
            if (string.IsNullOrEmpty(id)) throw new IOException("请通过齿轮选择并保存一个调校预设。");
            snapshot = hardware.ProbeTuning(bridge);
            var defaults = bridge.Read(18).EnumerateArray()
                .Select(p => TuningPresetStore.FromOriginal(p, snapshot, true)).ToArray();
            profile = new TuningPresetStore().LoadForApply(id, defaults, snapshot).Profile;
        }
        catch (Exception error) { return Rejected(error); }
        return ApplyClassicTuning(snapshot, profile);
    }

    private PerformanceResult ApplyClassicTuning(TuningSnapshot baseline, TuningProfile draft)
    {
        if (!hardware.Active || !hardware.SupportsCustomTuning || getBridge() is not { } bridge)
            return Rejected(new InvalidOperationException("当前未接管硬件或设备不支持自定义调校。"));
        TuningProfile requested;
        try { requested = HardwareSession.PrepareTuningForApply(baseline, draft); }
        catch (Exception error) { return Rejected(error); }
        var receipt = hardware.ApplyTuning(bridge, baseline, requested);
        return new(PerformanceOutcome.ReadbackConfirmed, (PerformanceMode)receipt.After.Performance.Mode,
            (PerformanceMode)receipt.Before.Performance.Mode, Tuning: receipt, IndicatorWarning: hardware.ModeIndicatorWarning);
    }

    private PerformanceResult SendDeclaredTuning(DeclaredTuningPreset preset)
    {
        if (!hardware.Active || hardware.DeclaredCpuTuning == null)
            return Rejected(new InvalidOperationException("Core 未声明 CPU 调校。"));
        hardware.ApplyDeclaredCpuTuning(preset);
        return new(PerformanceOutcome.CommandSent);
    }

    private PerformanceResult Confirmed(PerformanceReceipt receipt) =>
        new(PerformanceOutcome.ReadbackConfirmed, (PerformanceMode)receipt.After.Mode,
            (PerformanceMode)receipt.Before.Mode, Classic: receipt, IndicatorWarning: hardware.ModeIndicatorWarning);

    private PerformanceResult Confirmed(ModeCommandReceipt? receipt)
    {
        if (receipt == null) return new(PerformanceOutcome.Skipped);
        if (receipt.Classic is { } classic) return Confirmed(classic);
        return new(PerformanceOutcome.ReadbackConfirmed, (PerformanceMode)receipt.After, (PerformanceMode)receipt.Before);
    }

    private static PerformanceResult Rejected(Exception error) => new(PerformanceOutcome.Rejected, Error: error);

    private DesktopPerformanceOperation RunDesktopOperation(Func<PerformanceResult> command)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = RunAsync(command, started: started);
        return new(started.Task, completion);
    }

    private async Task<PerformanceResult> RunAsync(Func<PerformanceResult> command, bool wait = true,
        Func<bool>? authorized = null, CancellationToken cancellationToken = default,
        TaskCompletionSource<bool>? started = null)
    {
        bool entered = false;
        try
        {
            entered = await operations.EnterAsync(wait, cancellationToken).ConfigureAwait(false);
            if (!entered)
                return new(operations.IsAccepting ? PerformanceOutcome.Busy : PerformanceOutcome.Closing);
            return await Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested || authorized?.Invoke() == false)
                    return new PerformanceResult(PerformanceOutcome.Cancelled);
                started?.TrySetResult(true);
                return command();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!entered) { return new(PerformanceOutcome.Cancelled); }
        catch (AcPowerRequiredException error) { return Rejected(error); }
        // Some lower-level failures can occur after writes or while publishing
        // an audit. Only known preflight rejections are reported as rejected.
        catch (Exception error) { return new(PerformanceOutcome.Unknown, Error: error); }
        finally
        {
            if (entered) operations.Release();
            started?.TrySetResult(false);
        }
    }
}
