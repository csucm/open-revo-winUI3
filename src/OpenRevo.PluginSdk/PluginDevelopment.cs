using System.Text.Json;

namespace OpenRevo.PluginSdk;

public interface IPlugin
{
    Task RunAsync(IPluginContext context, CancellationToken cancellationToken);
}

public interface IPluginContext
{
    Task<PluginCapabilities> QueryCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<PluginCapabilityCatalog> QueryCatalogAsync(CancellationToken cancellationToken = default)
        => Task.FromException<PluginCapabilityCatalog>(new NotSupportedException("Requires host API 1.2."));
    Task<PluginThemeSchema> QueryThemeSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromException<PluginThemeSchema>(new NotSupportedException("Requires host API 1.2."));
    Task<PluginResult> SubscribeSummaryAsync(CancellationToken cancellationToken = default);
    Task<PluginResult> SubscribePowerAsync(CancellationToken cancellationToken = default);
    Task<PluginResult> UnsubscribeSummaryAsync(CancellationToken cancellationToken = default)
        => Task.FromException<PluginResult>(new NotSupportedException("Requires host API 1.2 SDK methods."));
    Task<PluginResult> UnsubscribePowerAsync(CancellationToken cancellationToken = default)
        => Task.FromException<PluginResult>(new NotSupportedException("Requires host API 1.2 SDK methods."));
    Task<PluginResult> LoadConfigAsync(CancellationToken cancellationToken = default);
    Task<PluginResult> SaveConfigAsync<T>(T config, CancellationToken cancellationToken = default);
    Task<PluginResult> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken = default);
    Task<PluginResult> SetViewAsync(PluginView view, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PluginEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}

public sealed record PluginEvent(string Topic, JsonElement Data);
public sealed record PluginAction(string Action, JsonElement Arguments);
public sealed record PluginConfig(JsonElement Data);
public sealed record PluginTopic(string Topic);
public sealed record PluginUiEvent(string ControlId, string? Value);

// A closed vocabulary: no XAML, markup extensions, binding expressions, URIs or runtime type names.
// Strings are plain text. The host owns theme, fonts, sizing and event dispatch.
public sealed record PluginView(string Title, PluginControl[] Controls);
public sealed record PluginControl(string Id, string Kind, string Label, string? Value = null,
    bool Enabled = true, PluginChoice[]? Choices = null);
public sealed record PluginChoice(string Value, string Label);

public static class PluginPermissions
{
    public const string SummaryRead = "state.summary.read";
    public const string PowerRead = "state.power.read";
    public const string Config = "config.own";
    public const string View = "ui.view";
    public const string Navigate = "ui.navigate";
    public const string Background = "lifecycle.background";
    public const string Performance = "hardware.performance.set";
    public const string FanBoost = "hardware.fanBoost.set";
    public const string BatteryLimit = "hardware.battery.limit.set";

    public static bool IsKnown(string id) => id is SummaryRead or PowerRead or Config or View or Navigate or Background
        or Performance or FanBoost or BatteryLimit || PluginPlatformPermissions.IsKnown(id);
}
