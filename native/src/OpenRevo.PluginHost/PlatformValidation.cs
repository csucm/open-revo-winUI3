using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

public static class PlatformValidation
{
    public static readonly string[] Surfaces = ["main", "mini", "osd", "menu", "dialog"];
    public static readonly string[] ColorTokens = ["background", "surface", "border", "text", "muted", "accent", "tint", "subtle"];
    public static readonly string[] NativePages = ["overview", "display", "power", "devices", "plugins", "settings"];
    public static PluginContributions Empty => new([], [], [], [], []);

    public static void Validate(PluginPlatformManifest platform, string[] permissions)
    {
        if (platform.SchemaVersion != 1 || platform.Runtime is not ("static" or "worker") ||
            platform.RequiredCapabilities is not { Length: <= 64 } || platform.OptionalCapabilities is not { Length: <= 64 } ||
            platform.RequiredCapabilities.Concat(platform.OptionalCapabilities).Any(c => !ValidCapabilityId(c)) ||
            platform.RequiredCapabilities.Concat(platform.OptionalCapabilities).Distinct(StringComparer.Ordinal).Count() !=
                platform.RequiredCapabilities.Length + platform.OptionalCapabilities.Length)
            throw new InvalidDataException("Invalid platform schema or capability requirements.");
        Validate(platform.Contributions, permissions);
    }

    public static void Validate(PluginContributions value, IReadOnlyCollection<string> permissions)
    {
        if (value == null || value.Pages is not { Length: <= 8 } || value.Theme is not { Length: <= 80 } ||
            value.Motions is not { Length: <= 24 } || value.Keys is not { Length: <= 16 } || value.Policies is not { Length: <= 8 })
            throw new InvalidDataException("Plugin contribution budget exceeded.");
        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in value.Pages)
        {
            if (page == null || !PluginPackage.Identifier(page.Id) || page.Title is not { Length: > 0 and <= 80 }) Invalid();
            PluginViewPolicy.Validate(page!.View);
            Claim("page." + page.Id);
            Require(PluginPermissions.View);
            if (page.ShowInNavigation) Require(PluginPlatformPermissions.Navigation);
        }
        foreach (var token in value.Theme)
        {
            if (token == null || !Surfaces.Contains(token.Surface) || token.Mode is not ("light" or "dark") ||
                !ColorTokens.Contains(token.Token) || !Color(token.Value)) Invalid();
            Claim($"theme.{token!.Surface}.{token.Mode}.{token.Token}");
            Require(PluginPlatformPermissions.Theme);
        }
        foreach (var motion in value.Motions)
        {
            if (motion == null || !Surfaces.Contains(motion.Surface) || motion.Trigger is not ("enter" or "exit" or "press" or "release") ||
                motion.Steps is not { Length: >= 1 and <= 8 }) Invalid();
            Claim($"motion.{motion!.Surface}.{motion.Trigger}");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in motion.Steps)
            {
                if (step == null || step.DurationMs is < 50 or > 500 || step.DelayMs is < 0 or > 250 ||
                    step.DelayMs + step.DurationMs > 600 || step.Easing is not ("linear" or "easeIn" or "easeOut") ||
                    !double.IsFinite(step.From) || !double.IsFinite(step.To) || !properties.Add(step.Property)) Invalid();
                bool valid = step!.Property switch
                {
                    "opacity" => step.From is >= 0 and <= 1 && step.To is >= 0 and <= 1,
                    "translateX" or "translateY" => Math.Abs(step.From) <= 16 && Math.Abs(step.To) <= 16,
                    "scale" => step.From is >= .96 and <= 1.04 && step.To is >= .96 and <= 1.04,
                    _ => false
                };
                if (!valid || (motion.Trigger is "enter" or "release") &&
                    step.To != (step.Property is "opacity" or "scale" ? 1 : 0)) Invalid();
            }
            Require(PluginPlatformPermissions.Motion);
        }
        foreach (var key in value.Keys)
        {
            if (key == null || key.Event != "input.mode-key") Invalid();
            ValidateAction(key!.Action);
            Require(ActionPermission(key.Action)); Require(PluginPlatformPermissions.Keys);
            Claim("key." + key.Event);
        }
        foreach (var policy in value.Policies)
        {
            if (policy == null || !PluginPackage.Identifier(policy.Id) || policy.Condition is not ("always" or "on-ac" or "on-battery")) Invalid();
            ValidateAction(policy!.Action);
            if (policy.Resource != ActionResource(policy.Action) || policy.Resource.Length == 0) Invalid();
            Claim("policy-id." + policy.Id); Claim("policy." + policy.Resource);
            Require(ActionPermission(policy.Action)); Require(PluginPlatformPermissions.Policy);
        }
        return;

        void Claim(string key) { if (!resources.Add(key)) throw new InvalidDataException("Duplicate contribution resource: " + key); }
        void Require(string permission)
        { if (!permissions.Contains(permission)) throw new InvalidDataException("Contribution permission missing: " + permission); }
    }

    public static void ValidateAction(PluginAction action)
    {
        if (action == null || action.Arguments.ValueKind != JsonValueKind.Object) Invalid();
        var fields = action!.Arguments.EnumerateObject().ToArray();
        if (fields.Length != 1) Invalid();
        var field = fields[0];
        bool valid = action.Action switch
        {
            "performance.set" => field.Name == "mode" && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int mode) && mode is >= 1 and <= 3,
            "fanBoost.set" => field.Name == "enabled" && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "battery.limit.set" => field.Name == "percent" && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int cap) && cap is 60 or 80 or 100,
            _ => false
        };
        if (!valid) Invalid();
    }
    public static string ActionPermission(PluginAction action) => action.Action switch
    { "performance.set" => PluginPermissions.Performance, "fanBoost.set" => PluginPermissions.FanBoost, "battery.limit.set" => PluginPermissions.BatteryLimit, _ => "" };
    public static string ActionResource(PluginAction action) => action.Action switch
    { "performance.set" => "performance", "fanBoost.set" => "fanBoost", "battery.limit.set" => "battery.limit", _ => "" };
    public static bool ValidCapabilityId(string? id) => id is { Length: > 0 and <= 96 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool Color(string? value) => value is { Length: 6 } && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    [DoesNotReturn] private static void Invalid() => throw new InvalidDataException("Invalid plugin contribution.");
}
