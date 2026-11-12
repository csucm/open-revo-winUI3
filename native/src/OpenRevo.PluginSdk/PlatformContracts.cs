using System.Text.Json;

namespace OpenRevo.PluginSdk;

// Inline, versioned declarations. All arrays and descriptions cross the IPC as data only.
public sealed record PluginPlatformManifest(int SchemaVersion, string Runtime,
    string[] RequiredCapabilities, string[] OptionalCapabilities, PluginContributions Contributions);
public sealed record PluginContributions(PluginPage[] Pages, PluginThemeValue[] Theme,
    PluginMotion[] Motions, PluginKeyMapping[] Keys, PluginPolicy[] Policies);
public sealed record PluginPage(string Id, string Title, bool ShowInNavigation, PluginView View);
public sealed record PluginThemeValue(string Surface, string Mode, string Token, string Value);
public sealed record PluginMotion(string Surface, string Trigger, PluginMotionStep[] Steps);
public sealed record PluginMotionStep(string Property, double From, double To, int DurationMs,
    int DelayMs = 0, string Easing = "easeOut");
public sealed record PluginKeyMapping(string Event, PluginAction Action);
public sealed record PluginPolicy(string Id, string Resource, string Condition, PluginAction Action);
public sealed record PluginCapabilityInfo(string Id, string Kind, string Unit, string Availability,
    string? Reason, string[] Values, bool RequiresTakeover = false, bool FirmwareSuppression = false);
public sealed record PluginCapabilityCatalog(PluginApiVersion Api, PluginCapabilityInfo[] Entries);
public sealed record PluginThemeSchema(bool Available, int SchemaVersion, string Runtime,
    string[] Surfaces, string[] Modes, string[] ColorTokens, string ColorFormat,
    int MaximumValues, bool SupportsDynamicUpdates);
public sealed record PluginPlatformState(string Id, bool Enabled, string? Fault,
    string[] PausedPolicies, string[] UnavailableOptionalCapabilities);

public static class PluginPlatformPermissions
{
    public const string Navigation = "ui.navigation.contribute";
    public const string Theme = "ui.theme";
    public const string Motion = "ui.motion";
    public const string Keys = "input.mapping";
    public const string Policy = "control.policy";
    public static bool IsKnown(string id) => id is Navigation or Theme or Motion or Keys or Policy;
}
