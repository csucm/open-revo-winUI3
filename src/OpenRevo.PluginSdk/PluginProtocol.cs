using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRevo.PluginSdk;

/// <summary>Wire contract only. This library does not create a security sandbox.</summary>
public static class PluginProtocol
{
    public const int Major = 1;
    public const int Minor = 2;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumJsonDepth = 16;

    // These options are private to prevent a plugin from changing host parsing rules.
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = MaximumJsonDepth,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter<PluginMessageKind>(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public enum PluginMessageKind { Request, Response, Event }

/// <summary>
/// Request IDs correlate responses; events use zero. Identity and grants are bound
/// by the trusted host to the OS-verified connection, never supplied by this record.
/// </summary>
public sealed record PluginMessage(int ApiMajor, PluginMessageKind Kind, long RequestId,
    string Method, JsonElement Payload);

public sealed record PluginApiVersion(int Major, int Minor);
public sealed record PluginCapability(string Id, bool Available, string? UnavailableReason);
public sealed record PluginCapabilities(PluginApiVersion Api, PluginCapability[] Capabilities);

/// <summary>Null values are unknown; SampledAt belongs to the existing host sample.</summary>
[method: JsonConstructor]
public sealed record PluginSystemSummary(DateTimeOffset SampledAt, double? CpuPercent,
    double? UsedMemoryGiB, double? TotalMemoryGiB, int? BatteryPercent, bool? OnAc, bool? Charging = null)
{
    // Keep the original binary constructor/deconstruction for plugins compiled against API 1.0/1.1.
    public PluginSystemSummary(DateTimeOffset sampledAt, double? cpuPercent, double? usedMemoryGiB,
        double? totalMemoryGiB, int? batteryPercent, bool? onAc)
        : this(sampledAt, cpuPercent, usedMemoryGiB, totalMemoryGiB, batteryPercent, onAc, null) { }
    public void Deconstruct(out DateTimeOffset sampledAt, out double? cpuPercent, out double? usedMemoryGiB,
        out double? totalMemoryGiB, out int? batteryPercent, out bool? onAc)
        => (sampledAt, cpuPercent, usedMemoryGiB, totalMemoryGiB, batteryPercent, onAc) =
            (SampledAt, CpuPercent, UsedMemoryGiB, TotalMemoryGiB, BatteryPercent, OnAc);
}
public sealed record PluginPowerState(DateTimeOffset ObservedAt, bool? OnAc, int? BatteryPercent);

/// <summary>Hardware success must describe the existing action's actual confirmation level.</summary>
public sealed record PluginResult(bool Success, string Code, JsonElement? Data = null);

public static class PluginMethods
{
    public const string CapabilitiesQuery = "capabilities.query";
    public const string CatalogQuery = "capabilities.catalog";
    public const string ThemeSchemaQuery = "ui.theme.schema";
    public const string StateSubscribe = "state.subscribe";
    public const string StateUnsubscribe = "state.unsubscribe";
    public const string StateSummary = "state.system.summary";
    public const string StatePower = "state.system.power";
    public const string ConfigLoad = "config.load";
    public const string ConfigSave = "config.save";
    public const string ActionExecute = "action.execute";
}
