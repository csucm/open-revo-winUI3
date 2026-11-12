using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelPreferenceKind { Theme = 1, SampleSeconds, WakeMode, MiniAutoHide, BatteryOverride, TrayTelemetry }

public sealed record PanelPreferenceRequest(PanelPreferenceKind Kind, int? Number = null, string? Text = null,
    bool? Enabled = null, bool Confirmed = false)
{
    [JsonIgnore] public bool Valid => Kind switch
    {
        PanelPreferenceKind.Theme => Number is >= 0 and <= 2 && Text == null && Enabled == null && !Confirmed,
        PanelPreferenceKind.SampleSeconds => Number is (2 or 5 or 10) && Text == null && Enabled == null && !Confirmed,
        PanelPreferenceKind.WakeMode => Text is ("remember" or "mini" or "full") && Number == null && Enabled == null && !Confirmed,
        PanelPreferenceKind.MiniAutoHide or PanelPreferenceKind.TrayTelemetry =>
            Enabled.HasValue && Number == null && Text == null && !Confirmed,
        PanelPreferenceKind.BatteryOverride => Enabled.HasValue && Number == null && Text == null && Confirmed == Enabled,
        _ => false
    };
}

public sealed record PanelPreferenceSnapshot(int Theme, int SampleSeconds, string WakeMode, bool MiniAutoHide,
    bool BatteryOverride, bool TrayTelemetry = false)
{
    [JsonIgnore] public bool Valid => Theme is >= 0 and <= 2 && SampleSeconds is (2 or 5 or 10) &&
        WakeMode is ("remember" or "mini" or "full");
}

public sealed record PanelSystemSummary(DateTimeOffset SampledAt, double? CpuPercent, double? UsedMemoryGiB,
    double? TotalMemoryGiB, int? BatteryPercent, bool? OnAc, bool? Charging);
