using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

public static class PluginHardwareCommands
{
    public static async Task<PluginResult> ExecuteAsync(ApplicationHardwareRuntime runtime, PluginLease lease,
        PluginAction action, CancellationToken cancellationToken)
    {
        string? permission = action.Action switch
        {
            "performance.set" => PluginPermissions.Performance,
            "fanBoost.set" => PluginPermissions.FanBoost,
            "battery.limit.set" => PluginPermissions.BatteryLimit,
            _ => null
        };
        if (!lease.IsActive || cancellationToken.IsCancellationRequested || !runtime.Operations.IsAccepting)
            return new(false, "disabled");
        if (permission == null || !lease.Allows(permission)) return new(false, "permission_denied");
        var hardware = runtime.Hardware;
        if (!hardware.Active || permission == PluginPermissions.Performance && !hardware.SupportsPerformance ||
            permission == PluginPermissions.FanBoost && !hardware.SupportsFanBoost ||
            permission == PluginPermissions.BatteryLimit && !hardware.SupportsBattery)
            return new(false, "unavailable");
        using var trace = DiagnosticLog.Begin("plugin.hardware_action", new { lease.PluginId, action.Action });
        try
        {
            bool Authorized() => lease.Allows(permission);
            if (permission == PluginPermissions.Performance)
            {
                var mode = (PerformanceMode)action.Arguments.GetProperty("mode").GetInt32();
                var result = await runtime.Performance.SetPluginModeAsync(mode, Authorized, cancellationToken).ConfigureAwait(false);
                if (result.Error is { } error) trace.Fail(error, new { result.Outcome });
                else trace.Complete(new { result.Outcome, result.Mode, result.IndicatorWarning });
                return result.Outcome switch
                {
                    PerformanceOutcome.ReadbackConfirmed => Success("readback_confirmed"),
                    PerformanceOutcome.Busy => new(false, "busy"),
                    PerformanceOutcome.Closing or PerformanceOutcome.Cancelled => new(false, "disabled"),
                    _ => new(false, "hardware_action_failed")
                };
            }
            var device = permission == PluginPermissions.FanBoost
                ? await runtime.Devices.SetFanBoostAsync(action.Arguments.GetProperty("enabled").GetBoolean(), Authorized, cancellationToken)
                    .ConfigureAwait(false)
                : await runtime.Devices.SetChargeLimitAsync(action.Arguments.GetProperty("percent").GetInt32(), Authorized, cancellationToken)
                    .ConfigureAwait(false);
            if (device.Error is { } failure) trace.Fail(failure, new { device.Outcome });
            else trace.Complete(new { device.Outcome, device.Receipt });
            return device.Outcome switch
            {
                DeviceCommandOutcome.ReadbackConfirmed => Success("readback_confirmed"),
                DeviceCommandOutcome.CommandSent => Success("command_sent"),
                DeviceCommandOutcome.Busy => new(false, "busy"),
                DeviceCommandOutcome.Cancelled => new(false, "disabled"),
                DeviceCommandOutcome.Unavailable => new(false, runtime.Operations.IsAccepting ? "unavailable" : "disabled"),
                _ => new(false, "hardware_action_failed")
            };
        }
        catch (OperationCanceledException) { return new(false, "disabled"); }
        catch (Exception error)
        {
            trace.Fail(error);
            return new(false, "hardware_action_failed");
        }

        PluginResult Success(string code) => new(true, code, JsonSerializer.SerializeToElement(new
        { action = action.Action, requested = action.Arguments }));
    }
}

public static class PluginPowerSource
{
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public byte Ac, Flags, Percent, SystemFlags; public uint Remaining, Full; }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out Status status);

    public static PluginPowerState Read()
    {
        bool ok = GetSystemPowerStatus(out var value);
        return new(DateTimeOffset.Now, ok && value.Ac <= 1 ? value.Ac == 1 : null,
            ok && value.Percent <= 100 && (value.Flags & 128) == 0 ? value.Percent : null);
    }
}
