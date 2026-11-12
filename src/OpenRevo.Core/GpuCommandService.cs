using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed record GpuControlSnapshot(GpuModeState? Firmware = null, GpuBootState? Boot = null,
    GpuRuntimeCapabilities? Runtime = null, bool Fresh = false, bool Declared = false)
{
    public int RuntimeMode => Boot?.InitialMode == 3 ? 3 :
        Runtime is { HybridIgpuSupported: true, IgpuActive: bool active } ? (active ? 1 : 2) : Boot?.InitialMode ?? 0;
    public bool RestartPending => RuntimeMode is >= 1 and <= 3 && Firmware?.Known == true
        ? Firmware.Mode != RuntimeMode : Boot?.Pending == true;
}

public enum GpuCommandOutcome { Refreshed, NoChange, ReadbackConfirmed, CommandSent, Busy, Rejected, Unknown }

public sealed record GpuCommandResult(GpuCommandOutcome Outcome, GpuControlSnapshot State, Exception? Error = null);

public sealed class GpuCommandService
{
    private readonly ApplicationHardwareRuntime runtime;
    private GpuControlSnapshot snapshot = new();

    internal GpuCommandService(ApplicationHardwareRuntime runtime) => this.runtime = runtime;

    public GpuControlSnapshot Snapshot => Volatile.Read(ref snapshot);

    public Task<GpuCommandResult> RefreshAsync(bool persist = true) => RunAsync(async bridge =>
    {
        var state = await ReadAsync(bridge, Snapshot).ConfigureAwait(false);
        Publish(state);
        if (persist) Persist(state);
        return new(GpuCommandOutcome.Refreshed, state);
    });

    public Task<GpuCommandResult> SelectAsync(int target, int expectedFirmwareMode) => RunAsync(async bridge =>
    {
        var state = Snapshot;
        if (target is < 1 or > 3 || expectedFirmwareMode is < 1 or > 3 || !state.Fresh ||
            state.Firmware is not { } firmware || firmware.Mode != expectedFirmwareMode ||
            !firmware.Supports(target) || !runtime.Hardware.Active || state.Declared && target == 1)
            return Rejected("当前显卡状态、所选模式或硬件接管已变化，请重新读取。");
        if (!GpuModeControl.IsAdministrator())
            return Rejected("当前后台权限不足，未发送显卡切换命令。");

        runtime.Telemetry.Temperatures.ReleaseGpu();
        if (state.Declared)
        {
            var before = await Task.Run(bridge.ReadGpuMode).ConfigureAwait(false);
            if (!before.Known || before.Mode != expectedFirmwareMode || !before.Supports(target))
                return Rejected("声明式显卡模式已变化，请重新读取。");
            await Task.Run(() => runtime.Hardware.ApplyDeclaredGpuMux(target, bridge)).ConfigureAwait(false);
            return new(GpuCommandOutcome.CommandSent, Publish(state with { Fresh = false }));
        }

        var boot = state.Boot ?? ReadBoot(firmware.Mode);
        bool canSwitchRuntime = target is 1 or 2 && boot.InitialMode is 1 or 2 &&
            state.Runtime is { HybridIgpuSupported: true, IgpuActive: not null };
        bool runtimeWillChange = canSwitchRuntime && state.Runtime!.IgpuActive != (target == 1);
        if (firmware.Mode == target && !runtimeWillChange)
            return new(GpuCommandOutcome.NoChange, state);
        // Preserve the current-boot baseline before staging any next-boot mode.
        GpuBootStateStore.Save(boot);
        var result = await Task.Run(() => runtime.Hardware.ChangeGpuMode(bridge, target, expectedFirmwareMode))
            .ConfigureAwait(false);
        var next = state with { Firmware = result.After, Boot = boot.Observe(result.After.Mode), Fresh = true };
        Publish(next);
        if (runtimeWillChange)
        {
            var capabilities = await Task.Run(() => runtime.Hardware.SwitchHybridRuntime(state.Runtime!.IgpuActive!.Value, target == 1))
                .ConfigureAwait(false);
            next = Publish(next with { Runtime = capabilities });
        }
        Persist(next);
        return new(result.Changed || runtimeWillChange ? GpuCommandOutcome.ReadbackConfirmed : GpuCommandOutcome.NoChange, next);
    });

    private async Task<GpuCommandResult> RunAsync(Func<UpstreamCoreLibrary, Task<GpuCommandResult>> operation)
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false))
            return new(GpuCommandOutcome.Busy, Snapshot);
        try
        {
            if (runtime.Bridge is not { } bridge) return Rejected("设备未连接。");
            return await operation(bridge).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A failed runtime transition or save can follow a completed firmware
            // stage. Retain any known stage in the snapshot and require a reread.
            var state = Publish(Snapshot with { Fresh = false, Runtime = null });
            DiagnosticLog.Failure("gpu.command.failed", error, new { state.Firmware, state.Boot });
            return new(GpuCommandOutcome.Unknown, state, error);
        }
        finally { runtime.Operations.Release(); }
    }

    private async Task<GpuControlSnapshot> ReadAsync(UpstreamCoreLibrary bridge, GpuControlSnapshot previous)
    {
        bool declared = runtime.Hardware.CoreDelegated && CoreMachine.Current.Manifest is not (null or "");
        var firmware = await Task.Run(bridge.ReadGpuMode).ConfigureAwait(false);
        if (declared) return new(firmware, Fresh: firmware.Known, Declared: true);
        if (!firmware.Known)
            return previous with { Firmware = GpuModeCache.Read() ?? firmware, Fresh = false, Declared = false };
        var boot = (previous.Boot ?? ReadBoot(firmware.Mode)).Observe(firmware.Mode);
        GpuRuntimeCapabilities capabilities;
        try { capabilities = await Task.Run(GpuRuntimeCapabilities.Probe).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { capabilities = new(null, null, null, null, "ACPI capability query timed out"); }
        DiagnosticLog.Record("gpu.runtime.probe", new { capabilities, boot, firmware });
        return new(firmware, boot, capabilities, Fresh: true);
    }

    private static GpuBootState ReadBoot(int firmwareMode)
    {
        var saved = AppData.Load();
        return GpuBootStateStore.Open(firmwareMode, saved.RequestedGpu, saved.GpuSavedAtUnix);
    }

    private static void Persist(GpuControlSnapshot state)
    {
        if (state.Declared || !state.Fresh || state.Boot == null || state.Firmware is not { Known: true } firmware) return;
        GpuBootStateStore.Save(state.Boot);
        try { GpuModeCache.Save(firmware); }
        catch (Exception error) { DiagnosticLog.Failure("gpu.cache.save", error); }
        int pending = state.RestartPending ? firmware.Mode : 0;
        AppData.Update(saved => saved with
        {
            RequestedGpu = pending,
            GpuSavedAtUnix = pending == 0 ? 0 : saved.RequestedGpu == pending && saved.GpuSavedAtUnix > 0
                ? saved.GpuSavedAtUnix : DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    private GpuControlSnapshot Publish(GpuControlSnapshot value)
    {
        Volatile.Write(ref snapshot, value);
        return value;
    }

    private GpuCommandResult Rejected(string reason) => new(GpuCommandOutcome.Rejected, Snapshot, new InvalidOperationException(reason));
}
