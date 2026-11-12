using System.Text.Json;

namespace OpenRevo.PluginSdk;

public enum PluginPerformanceMode { Office = 1, Balanced = 2, Performance = 3 }
public enum PluginBatteryLimit { Workstation = 60, Balanced = 80, Full = 100 }

/// <summary>Convenience API only. The trusted broker independently validates every argument and grant.</summary>
public static class PluginActions
{
    public static Task<PluginResult> SetPerformanceAsync(this IPluginContext context, PluginPerformanceMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return context.ExecuteAsync("performance.set", JsonSerializer.SerializeToElement(new { mode = (int)mode }), cancellationToken);
    }
    public static Task<PluginResult> SetFanBoostAsync(this IPluginContext context, bool enabled,
        CancellationToken cancellationToken = default) => context.ExecuteAsync("fanBoost.set",
            JsonSerializer.SerializeToElement(new { enabled }), cancellationToken);
    public static Task<PluginResult> SetBatteryLimitAsync(this IPluginContext context, PluginBatteryLimit limit,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(limit)) throw new ArgumentOutOfRangeException(nameof(limit));
        return context.ExecuteAsync("battery.limit.set", JsonSerializer.SerializeToElement(new { percent = (int)limit }), cancellationToken);
    }
}
