using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public sealed record PanelMiniLayout(string[] Top, string?[] Quick)
{
    [JsonIgnore] public bool Valid => Top is { Length: <= 6 } && Quick is { Length: 6 } &&
        Top.All(id => id is "performance" or "gpu" or "refresh" or "brightness" or "keyboard" or "battery") &&
        Quick.All(id => id is null or "touchpad" or "win" or "fn" or "camera" or "mic" or "osd" or "boost" or "awake" or "screen") &&
        Top.Distinct(StringComparer.Ordinal).Count() == Top.Length &&
        Quick.OfType<string>().Distinct(StringComparer.Ordinal).Count() == Quick.Count(id => id != null);
}

public sealed record PanelLayoutEdit(string[]? Overview = null, PanelMiniLayout? Mini = null, bool ReplaceUnrecognized = false)
{
    [JsonIgnore] public bool Valid => Overview == null
        ? Mini is { Valid: true }
        : Mini == null && !ReplaceUnrecognized && ValidOverview(Overview);

    internal static bool ValidOverview(string[] items) => items.Length <= 7 &&
        items.All(id => id is "cpu" or "gpu" or "vram" or "fan" or "performance" or "battery" or "display") &&
        items.Distinct(StringComparer.Ordinal).Count() == items.Length;
}

public sealed record PanelLayouts(string[] Overview, PanelMiniLayout Mini, string? MiniIssue);

public sealed record PanelDeviceInfo(string Board, string Product, string Processor, bool Declared, bool IntegratedOnly,
    bool SupportsCustomTuning, bool SupportsFnLock, bool CanSendFnLock, bool? SupportsTouchpad = null);
