using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelDesktopGroup { Lighting = 1, Color, Power }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Command")]
[JsonDerivedType(typeof(PanelReadDesktop), "read")]
[JsonDerivedType(typeof(PanelSetKeyboardRgb), "keyboard-rgb")]
[JsonDerivedType(typeof(PanelSetMonochrome), "keyboard-white")]
[JsonDerivedType(typeof(PanelUndoKeyboardRgb), "keyboard-undo")]
[JsonDerivedType(typeof(PanelSetLightingZone), "zone")]
[JsonDerivedType(typeof(PanelSetColor), "color")]
[JsonDerivedType(typeof(PanelApplyIcc), "icc-select")]
[JsonDerivedType(typeof(PanelImportIcc), "icc-import")]
[JsonDerivedType(typeof(PanelSetPowerPolicy), "power-policy")]
[JsonDerivedType(typeof(PanelSelectPowerPlan), "power-plan")]
[JsonDerivedType(typeof(PanelRecoverPowerPlans), "power-recovery")]
[JsonDerivedType(typeof(PanelSetLowRefresh), "display-auto-refresh")]
[JsonDerivedType(typeof(PanelScreenOff), "display-off")]
public abstract record PanelDesktopRequest
{
    [JsonIgnore] public bool Valid => this switch
    {
        PanelReadDesktop read => Enum.IsDefined(read.Group),
        PanelSetKeyboardRgb rgb => rgb.Rgb <= 0xffffff && rgb.Level is >= 0 and <= 4,
        PanelSetMonochrome mono => mono.Level is >= 0 and <= 255,
        PanelUndoKeyboardRgb or PanelScreenOff or PanelSetLowRefresh => true,
        PanelSetLightingZone zone => zone.Zone is ("front" or "hinge" or "logo") &&
            zone.Level is >= 0 and <= 4 && zone.Speed is >= 0 and <= 4 &&
            zone.Effect is (1 or 2 or 3 or 4 or 6 or 9 or 10 or 20) && zone.Rgb <= 0xffffff,
        PanelSetColor color => color.PresetId is { Length: > 0 and <= 100 } &&
            (color.Values == null || !color.Factory && color.Values.Valid),
        PanelApplyIcc icc => icc.Profile is { Length: > 0 and <= 512 } && icc.Device is { Length: > 0 and <= 128 },
        PanelImportIcc upload => upload.Bytes is > 0 and <= PanelProtocol.MaximumIccBytes &&
            upload.FileName is { Length: > 4 and <= 260 } && upload.FileName.All(c => c is not ('/' or '\\' or ':') && !char.IsControl(c)) &&
            (upload.FileName.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) ||
                upload.FileName.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)),
        PanelSetPowerPolicy policy => policy.Key is ("office" or "link" or "master" or "usb" or "recovery" or "disconnect_net" or
            "block_away" or "ignore_sysreq" or "disable_wake_timers" or "deep_sleep" or "hibernate_30m"),
        PanelSelectPowerPlan plan => plan.Scheme != Guid.Empty && plan.Expected != Guid.Empty,
        PanelRecoverPowerPlans recover => recover.Confirmed,
        _ => false
    };
}

public sealed record PanelReadDesktop(PanelDesktopGroup Group) : PanelDesktopRequest;
public sealed record PanelSetKeyboardRgb(uint Rgb, int Level, bool Rainbow) : PanelDesktopRequest;
public sealed record PanelSetMonochrome(int Level) : PanelDesktopRequest;
public sealed record PanelUndoKeyboardRgb : PanelDesktopRequest;
public sealed record PanelSetLightingZone(string Zone, bool Enabled, int Effect, int Level, int Speed, uint Rgb) : PanelDesktopRequest;
public sealed record PanelSetColor(string PresetId, bool Enabled, bool Factory = false, PanelColorValues? Values = null) : PanelDesktopRequest;
public sealed record PanelApplyIcc(string Device, string Profile) : PanelDesktopRequest;
public sealed record PanelImportIcc(string FileName, int Bytes) : PanelDesktopRequest;
public sealed record PanelSetPowerPolicy(string Key, bool Enabled) : PanelDesktopRequest;
public sealed record PanelSelectPowerPlan(Guid Scheme, Guid Expected) : PanelDesktopRequest;
public sealed record PanelRecoverPowerPlans(bool Confirmed) : PanelDesktopRequest;
public sealed record PanelSetLowRefresh(bool Enabled) : PanelDesktopRequest;
public sealed record PanelScreenOff : PanelDesktopRequest;

public sealed record PanelColorValues(int? Temperature = null, double? Saturation = null, double? Contrast = null, double? Gamma = null,
    double? Red = null, double? Green = null, double? Blue = null)
{
    [JsonIgnore] public bool Valid => Temperature is (null or >= 4000 and <= 9000) && Saturation is (null or >= 0 and <= 2) &&
        Contrast is (null or >= .5 and <= 1.5) && Gamma is (null or >= .7 and <= 1.3) && Red is (null or >= .5 and <= 1.5) &&
        Green is (null or >= .5 and <= 1.5) && Blue is (null or >= .5 and <= 1.5);
}

public sealed record PanelDesktopSnapshot(PanelLightingSnapshot? Lighting = null, PanelColorSnapshot? Color = null,
    PanelPowerSnapshot? Power = null);
public sealed record PanelLightingSnapshot(PanelKeyboardSnapshot? Keyboard, PanelZoneSnapshot[] Zones);
public sealed record PanelKeyboardSnapshot(string Kind, string Name, bool CanSetRgb, bool CanUndoRgb, bool CommandOnly,
    PanelSetKeyboardRgb? ConfirmedRgb, int[] MonochromeLevels, int? MonochromeReadback);
public sealed record PanelZoneSnapshot(PanelSetLightingZone SavedCommand, bool CanSet, bool HasSpeed, int[] Effects)
{
    public override string ToString() => SavedCommand.Zone switch { "front" => "前灯带", "hinge" => "转轴灯", _ => "标志灯" };
}
public sealed record PanelDesktopChoice(string Id, string Name) { public override string ToString() => Name; }
public sealed record PanelColorPreset(string Id, string Name, PanelColorValues? Values, bool FactoryAvailable)
{
    public override string ToString() => Name;
}
public sealed record PanelIccSnapshot(string Device, string ActiveProfile, bool Enabled, bool HdrActive, PanelDesktopChoice[] Profiles);
public sealed record PanelColorSnapshot(bool? RuntimeEnabled, bool SavedEnabled, bool? GammaApplied,
    PanelColorPreset? ActivePreset, PanelColorPreset[] Presets, PanelIccSnapshot? Icc);
public sealed record PanelPowerPolicy(string Key, bool Supported, bool? Enabled)
{
    public override string ToString() => Key switch
    {
        "office" => "睡眠前切办公", "link" => "性能档位联动", "master" => "统一睡眠策略",
        "usb" => "关机 USB 供电", "recovery" => "交流电恢复",
        "disconnect_net" => "待机断网", "block_away" => "禁用离开模式",
        "ignore_sysreq" => "忽略系统电源请求", "disable_wake_timers" => "禁用唤醒计时器",
        "deep_sleep" => "深度睡眠", "hibernate_30m" => "拔电 30 分钟后休眠", _ => Key
    };
}
public sealed record PanelPowerPlan(Guid Id, string Name) { public override string ToString() => Name; }
public sealed record PanelPowerSnapshot(PanelPowerPlan[] Plans, Guid? ActivePlan, PanelPowerPolicy[] Policies, bool LowRefreshOnBattery);
