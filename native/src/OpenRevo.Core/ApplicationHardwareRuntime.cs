using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed class ApplicationHardwareRuntime
{
    private readonly object bridgeGate = new();
    private UpstreamCoreLibrary? bridge;
    private Task? releaseTask;

    public ApplicationHardwareRuntime(Func<string?>? runtimeBlock = null)
        : this(runtimeBlock, useNativeModeKeyEvents: false) { }

    public ApplicationHardwareRuntime(Func<string?>? runtimeBlock, bool useNativeModeKeyEvents)
        : this(runtimeBlock, useNativeModeKeyEvents, useNativePowerRequests: false) { }

    public ApplicationHardwareRuntime(Func<string?>? runtimeBlock, bool useNativeModeKeyEvents, bool useNativePowerRequests)
    {
        Hardware = HardwareSession.ForThisMachine(runtimeBlock);
        Devices = new(Hardware, Operations);
        Performance = new(Hardware, Operations, () => Bridge);
        PerformanceInput = new(Hardware, Operations, Performance, useNativeModeKeyEvents);
        Telemetry = new(this);
        Gpu = new(this);
        Switches = new(this);
        Desktop = new(this);
        SessionPower = new(useNativePowerRequests);
    }

    public HardwareSession Hardware { get; }
    public DeviceCommandService Devices { get; }
    public PerformanceCommandService Performance { get; }
    public PerformanceInputRuntime PerformanceInput { get; }
    public ApplicationTelemetryRuntime Telemetry { get; }
    public GpuCommandService Gpu { get; }
    public DeviceSwitchCommandService Switches { get; }
    public DesktopCommandService Desktop { get; }
    public SessionPowerRuntime SessionPower { get; }
    public DisplayCommandService Display { get; } = new();

    // Transitional access for existing non-performance callers. Do not acquire
    // this queue around a Performance command, which already acquires it.
    public HardwareOperationQueue Operations { get; } = new();
    public UpstreamCoreLibrary? Bridge => Volatile.Read(ref bridge);

    public UpstreamCoreLibrary OpenBridge()
    {
        lock (bridgeGate)
        {
            if (!Operations.IsAccepting) throw new InvalidOperationException("硬件运行正在关闭。");
            return bridge ??= new(Path.Combine(AppContext.BaseDirectory, "openrevo_native_bridge.dll"));
        }
    }

    public Task StopDesktopServicesAsync() => Operations.DrainAsync(() => Bridge?.StopDesktopServices());

    public async Task<bool> ReleaseControlAsync()
    {
        Devices.CancelAutomaticTakeover();
        if (!await Operations.EnterAsync().ConfigureAwait(false)) return false;
        try
        {
            await Task.Run(ReleaseControl).ConfigureAwait(false);
            return true;
        }
        finally { Operations.Release(); }
    }

    public Task ReleaseAsync()
    {
        lock (bridgeGate)
        {
            return releaseTask ??= ReleaseResourcesAsync();
        }
    }

    private async Task ReleaseResourcesAsync()
    {
        Operations.StopAccepting();
        try { await Display.StopAsync().ConfigureAwait(false); }
        finally
        {
            await Operations.DrainAsync(() =>
            {
                try { SessionPower.Dispose(); }
                finally
                {
                    try { Telemetry.Dispose(); }
                    finally
                    {
                        ReleaseControl();
                        bridge?.Dispose();
                    }
                }
            }).ConfigureAwait(false);
        }
    }

    private void ReleaseControl()
    {
        PerformanceInput.SynchronizePhysicalMode(false);
        Hardware.Release();
        Performance.ResetTakeoverState();
    }
}

public sealed class HardwareOperationQueue
{
    private readonly SemaphoreSlim queue = new(1, 1);
    // Even versions accept work. Advancing to the next even version ensures a
    // canceled exit never revives requests from before the stop.
    private int admissionVersion;

    public bool IsAccepting => (Volatile.Read(ref admissionVersion) & 1) == 0;
    public bool IsBusy => queue.CurrentCount == 0;

    public async Task<bool> EnterAsync(bool wait = true, CancellationToken cancellationToken = default)
    {
        int version = Volatile.Read(ref admissionVersion);
        if ((version & 1) != 0 || !await queue.WaitAsync(wait ? Timeout.Infinite : 0, cancellationToken).ConfigureAwait(false))
            return false;
        if (version == Volatile.Read(ref admissionVersion)) return true;
        queue.Release();
        return false;
    }

    public void Release() => queue.Release();
    public void StopAccepting() => Interlocked.Or(ref admissionVersion, 1);
    public void Resume()
    {
        int version = Volatile.Read(ref admissionVersion);
        if ((version & 1) != 0) Interlocked.CompareExchange(ref admissionVersion, unchecked(version + 1), version);
    }

    internal async Task DrainAsync(Action cleanup)
    {
        StopAccepting();
        await queue.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(cleanup).ConfigureAwait(false); }
        finally { queue.Release(); }
    }
}
