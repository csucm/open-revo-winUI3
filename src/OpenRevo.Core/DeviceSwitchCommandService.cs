using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed record DeviceShortcutRead(bool? Camera, bool CameraAvailable, bool? Microphone, bool? WinLocked,
    CameraState? WindowsDevice, object? CameraDiagnostics);

public sealed class DeviceSwitchCommandService
{
    private readonly ApplicationHardwareRuntime runtime;

    internal DeviceSwitchCommandService(ApplicationHardwareRuntime runtime) => this.runtime = runtime;

    public DeviceShortcutRead ReadShortcuts()
    {
        var hardware = runtime.Hardware;
        var bridge = runtime.Bridge;
        bool? camera = null, mic = null, win = null;
        bool available = false, delegated = hardware.CoreDelegated;
        CameraState? pnp = null;
        object? diagnostic = null;
        try { win = bridge?.ReadWindowsWinKeyLocked(); } catch { }
        try
        {
            if (delegated)
            {
                var candidates = CameraControl.ReadWindowsCandidates();
                available = candidates.Count > 0;
                pnp = candidates.Count == 1 ? candidates[0] : null;
                diagnostic = new { source = "present Windows Camera-class devices; firmware power unknown",
                    candidateCount = candidates.Count, states = candidates.Select(d => d.Enabled).ToArray(),
                    selection = pnp == null ? "not selected" : "single Windows device" };
            }
            else { pnp = CameraControl.Read(); available = pnp != null; }
        }
        catch (Exception error) { diagnostic = new { error = DiagnosticJournal.Scrub(error.Message), hresult = $"0x{error.HResult:X8}" }; }
        try
        {
            if (hardware.Active)
                camera = delegated || hardware.ProbeSwitches().CameraPowered ? pnp?.Enabled : false;
        }
        catch { }
        try { mic = bridge?.ReadMicrophone(); } catch { }
        try { if (!delegated && camera == true && bridge?.IsCameraAccessBlocked() == true) camera = false; }
        catch { camera = null; }
        return new(camera, available, mic, win, delegated ? pnp : null, diagnostic);
    }

    public Task<DeviceShortcutRead> ToggleCameraAsync(CameraState? expected) => QueuedAsync(async bridge =>
    {
        var hardware = runtime.Hardware;
        if (!hardware.Active) throw new InvalidOperationException("请先连接硬件。");
        if (hardware.CoreDelegated)
        {
            if (hardware.CheckAvailability() is { } blocked) throw new InvalidOperationException(blocked);
            if (expected is not { Enabled: bool before }) throw new IOException("当前 Windows 相机设备状态未知。");
            await CameraControl.SetWindowsDeviceAsync(expected, !before).ConfigureAwait(false);
            return ReadShortcuts();
        }

        var current = ReadShortcuts();
        if (!current.Camera.HasValue) throw new IOException("无法确认相机状态，请检查设备管理器。");
        bool enable = !current.Camera.Value;
        bool beforePowered = hardware.ProbeSwitches().CameraPowered;
        bool confirmed = false;
        try
        {
            hardware.SetCameraPoweredViaCore(bridge, enable);
            if (enable)
            {
                var pnp = await WaitForCameraAsync().ConfigureAwait(false);
                if (pnp?.Enabled == false || bridge.IsCameraAccessBlocked())
                {
                    if (GpuModeControl.IsAdministrator()) CameraControl.ApplyLegacyState(hardware, bridge, true);
                    else await CameraControl.Request(true).ConfigureAwait(false);
                }
                else if (pnp?.Enabled != true) throw new IOException("相机尚未就绪，请稍后重试。");
            }
            var readback = ReadShortcuts();
            if (readback.Camera != enable) throw new IOException("相机状态未确认改变。");
            confirmed = true;
            return readback;
        }
        finally
        {
            if (!confirmed && !beforePowered && hardware.Active && hardware.ProbeSwitches().CameraPowered)
                hardware.SetCameraPoweredViaCore(bridge, false);
        }
    });

    public Task<bool> ToggleMicrophoneAsync() => QueuedAsync(bridge => Task.FromResult(bridge.ToggleMicrophone()));

    public OverviewSwitches ReadOverview() => Bridge().ReadOverviewSwitches();

    public Task<OverviewSwitches> ToggleOverviewAsync(OverviewSwitch key) => QueuedAsync(bridge =>
    {
        bool before = bridge.ReadOverviewSwitches().Get(key) ?? throw new IOException("设备当前状态不可用。");
        OverviewSwitches readback;
        if (key is OverviewSwitch.Touchpad or OverviewSwitch.FnLock)
            readback = runtime.Hardware.SetOverviewSwitchViaCore(bridge, key, before, !before);
        else
        {
            bridge.SetOverviewSwitch(key, before, !before);
            readback = bridge.ReadOverviewSwitches();
        }
        if (readback.Get(key) != !before) throw new IOException("设备回读未确认改变。");
        return Task.FromResult(readback);
    });

    public async Task<DeviceCommandResult> SetDeclaredFnLockAsync(bool enabled)
    {
        try
        {
            return await QueuedAsync(_ =>
            {
                runtime.Hardware.ApplyDeclaredFnLock(enabled);
                return Task.FromResult(new DeviceCommandResult(DeviceCommandOutcome.CommandSent));
            }).ConfigureAwait(false);
        }
        catch (Exception error) { return new(DeviceCommandOutcome.Unknown, Error: error); }
    }

    public bool ReadWinLock() => Bridge().ReadWindowsWinKeyLocked();

    public Task<bool> ToggleWinLockAsync() => Task.Run(() =>
    {
        if (!runtime.Operations.IsAccepting) throw new IOException("后台正在退出。");
        return Bridge().ToggleWindowsWinKeyLocked();
    });

    public Task<bool> SetWinLockAsync(bool enabled) => Task.Run(() =>
    {
        if (!runtime.Operations.IsAccepting) throw new IOException("后台正在退出。");
        bool locked = Bridge().SetWindowsWinKeyLocked(enabled);
        if (locked != enabled) throw new IOException("Windows Win 软件锁未确认改变。");
        return locked;
    });

    private UpstreamCoreLibrary Bridge() => runtime.Bridge ?? throw new IOException("设备未连接。");

    private async Task<T> QueuedAsync<T>(Func<UpstreamCoreLibrary, Task<T>> operation)
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) throw new IOException("后台正在退出。");
        try { return await Task.Run(() => operation(Bridge())).ConfigureAwait(false); }
        finally { runtime.Operations.Release(); }
    }

    private static async Task<CameraState?> WaitForCameraAsync()
    {
        CameraState? value = null;
        for (int i = 0; i < 30; i++)
        {
            value = CameraControl.Read();
            if (value?.Enabled.HasValue == true) break;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return value;
    }
}
