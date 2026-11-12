using System.Diagnostics;
using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public enum DeviceCommandOutcome { Busy, Unavailable, Rejected, Cancelled, ReadbackConfirmed, CommandSent, Unknown }

public sealed record DeviceCommandResult(DeviceCommandOutcome Outcome, HardwareReceipt? Receipt = null, Exception? Error = null);

public enum TakeoverOutcome { Skipped, Active, Blocked, Failed }

public sealed record TakeoverResult(TakeoverOutcome Outcome, ControlState? State = null, string? Reason = null,
    Exception? Error = null);

public sealed class DeviceCommandService
{
    private readonly HardwareSession hardware;
    private readonly HardwareOperationQueue operations;
    private readonly object automaticGate = new();
    private bool automaticPending;
    private long lastAutomaticAttempt;
    private int automaticVersion;

    internal DeviceCommandService(HardwareSession hardware, HardwareOperationQueue operations)
    {
        this.hardware = hardware;
        this.operations = operations;
    }

    public bool AutomaticTakeoverPending { get { lock (automaticGate) return automaticPending; } }

    public void CancelAutomaticTakeover()
    {
        lock (automaticGate)
        {
            automaticPending = false;
            automaticVersion++;
        }
    }

    public async Task<TakeoverResult> TakeoverAsync()
    {
        CancelAutomaticTakeover();
        if (!await operations.EnterAsync().ConfigureAwait(false)) return new(TakeoverOutcome.Skipped);
        try { return await Task.Run(Acquire).ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    public async Task<TakeoverResult> AutomaticTakeoverAsync(bool retryOnly = false)
    {
        int version;
        lock (automaticGate)
        {
            if (retryOnly && (!automaticPending || Stopwatch.GetElapsedTime(lastAutomaticAttempt) < TimeSpan.FromSeconds(6)))
                return new(TakeoverOutcome.Skipped);
            lastAutomaticAttempt = Stopwatch.GetTimestamp();
            version = automaticVersion;
        }
        if (!await operations.EnterAsync().ConfigureAwait(false)) return new(TakeoverOutcome.Skipped);
        try
        {
            return await Task.Run(() =>
            {
                lock (automaticGate)
                    if (version != automaticVersion) return new TakeoverResult(TakeoverOutcome.Skipped);
                string? blocked;
                try { blocked = hardware.CheckAvailability(); }
                catch (Exception error)
                {
                    lock (automaticGate)
                        if (version == automaticVersion) automaticPending = false;
                    return new TakeoverResult(TakeoverOutcome.Failed, Error: error);
                }
                lock (automaticGate)
                {
                    if (version != automaticVersion) return new TakeoverResult(TakeoverOutcome.Skipped);
                    automaticPending = blocked?.StartsWith("请先正常退出其他硬件控制程序：", StringComparison.Ordinal) == true;
                }
                return blocked == null ? Acquire() : new(TakeoverOutcome.Blocked, Reason: blocked);
            }).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    private TakeoverResult Acquire()
    {
        ControlState? state;
        try { state = hardware.Takeover(); }
        catch (Exception error) { return new(TakeoverOutcome.Failed, Error: error); }
        try
        {
            if (!hardware.CoreDelegated) hardware.ProbePerformance();
            return new(TakeoverOutcome.Active, state);
        }
        catch (Exception error) { return new(TakeoverOutcome.Active, state, Error: error); }
    }

    public Task<DeviceCommandResult> SetChargeLimitAsync(int percent, Func<bool>? authorized = null,
        CancellationToken cancellationToken = default)
    {
        if (percent is not (60 or 80 or 100))
            return Task.FromResult(new DeviceCommandResult(DeviceCommandOutcome.Rejected,
                Error: new ArgumentOutOfRangeException(nameof(percent))));
        return ExecuteAsync(() => hardware.SupportsBattery, () => hardware.ApplyBatteryCommand(percent), authorized, cancellationToken);
    }

    public Task<DeviceCommandResult> SetFanBoostAsync(bool enabled, Func<bool>? authorized = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => hardware.SupportsFanBoost, () => hardware.ApplyFanBoostCommand(enabled), authorized, cancellationToken);

    public Task<DeviceCommandResult> ToggleFanBoostAsync(bool? observedState) =>
        ExecuteAsync(() => hardware.SupportsFanBoost, () =>
        {
            bool enabled = (hardware.State?.FanBoost ?? observedState ?? hardware.RequestedFanBoost) != true;
            return hardware.ApplyFanBoostCommand(enabled);
        });

    private async Task<DeviceCommandResult> ExecuteAsync(Func<bool> supported, Func<object> apply,
        Func<bool>? authorized = null, CancellationToken cancellationToken = default)
    {
        bool entered = false;
        try
        {
            entered = await operations.EnterAsync(wait: authorized == null, cancellationToken).ConfigureAwait(false);
            if (!entered) return new(operations.IsAccepting ? DeviceCommandOutcome.Busy : DeviceCommandOutcome.Unavailable);
            return await Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested || authorized?.Invoke() == false)
                    return new DeviceCommandResult(DeviceCommandOutcome.Cancelled);
                if (!hardware.Active || !supported())
                    return new DeviceCommandResult(DeviceCommandOutcome.Unavailable,
                        Error: new InvalidOperationException("当前未接管硬件或设备不支持此控制。"));
                return apply() is HardwareReceipt receipt
                    ? new DeviceCommandResult(DeviceCommandOutcome.ReadbackConfirmed, receipt)
                    : new DeviceCommandResult(DeviceCommandOutcome.CommandSent);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!entered) { return new(DeviceCommandOutcome.Cancelled); }
        catch (Exception error) { return new(DeviceCommandOutcome.Unknown, Error: error); }
        finally { if (entered) operations.Release(); }
    }
}
