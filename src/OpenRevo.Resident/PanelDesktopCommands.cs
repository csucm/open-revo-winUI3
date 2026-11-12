using System.Globalization;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed record DesktopReply(PanelOutcome Outcome, string? Message = null, PanelDesktopSnapshot? Snapshot = null);

internal sealed class PanelDesktopCommands(ApplicationHardwareRuntime runtime, Action<OsdMessage> notify, Action screenOff)
{
    private static readonly int[] ZoneEffects = [1, 2, 20, 3, 4, 6, 9, 10];
    private static readonly int[] EcLogoEffects = [1, 2, 20];
    private readonly Lazy<string> board = new(() => DeviceIdentity.Read().Board);

    internal async Task<DesktopReply> ExecuteAsync(PanelDesktopRequest request, byte[]? icc)
    {
        var group = GroupOf(request);
        if (request is PanelSetPowerPolicy { Key: "office" or "link" } policy)
        {
            var kind = policy.Key == "office" ? PerformanceDesktopPolicy.SleepOffice : PerformanceDesktopPolicy.PowerLink;
            // This service owns its queue entry.
            var result = await runtime.Performance.SetDesktopPolicy(kind, policy.Enabled).Completion.ConfigureAwait(false);
            var outcome = result.Outcome switch
            {
                PerformanceOutcome.ConfigurationSaved => PanelOutcome.ConfigurationSaved,
                PerformanceOutcome.ConfigurationSaveFailed => PanelOutcome.ConfigurationSaveFailed,
                PerformanceOutcome.Busy or PerformanceOutcome.Closing or PerformanceOutcome.Skipped => PanelOutcome.Busy,
                PerformanceOutcome.Rejected => PanelOutcome.Rejected,
                _ => PanelOutcome.Unknown
            };
            var refresh = await ReadAsync(PanelDesktopGroup.Power).ConfigureAwait(false);
            return new(outcome, result.Error?.Message, refresh);
        }

        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) return new(PanelOutcome.Busy);
        try
        {
            DesktopReply result;
            try
            {
                if (request is PanelScreenOff)
                {
                    if (!await runtime.SessionPower.TurnOffAsync(screenOff).ConfigureAwait(false)) return new(PanelOutcome.Busy);
                    ActionOsd("息屏请求已发出", "display");
                    return new(PanelOutcome.CommandSent);
                }
                result = request is PanelSetLowRefresh low
                    ? await SetLowRefreshAsync(low.Enabled).ConfigureAwait(false)
                    : await Task.Run(() => Execute(request, icc)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                DiagnosticLog.Failure("resident.desktop.command_failed", error, new { command = request.GetType().Name });
                result = new(PanelOutcome.Unknown, error.Message);
            }
            return result with { Snapshot = await Task.Run(() => Read(group)).ConfigureAwait(false) };
        }
        finally { runtime.Operations.Release(); }
    }

    private async Task<DesktopReply> SetLowRefreshAsync(bool enabled)
    {
        try { await runtime.Display.SaveAutomaticPolicyAsync(enabled).ConfigureAwait(false); }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.display.policy_save_failed", error);
            return new(PanelOutcome.ConfigurationSaveFailed, error.Message);
        }
        ActionOsd("拔电自动低刷 · " + (enabled ? "已开启" : "已关闭"), "refresh");
        try
        {
            if (runtime.Hardware.Active)
                await runtime.Display.EvaluateAutomaticAsync(null, visible: false, force: true).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.display.policy_apply_failed", error);
            return new(PanelOutcome.ConfigurationSaved, "低刷设置已保存，刷新率本次未确认：" + error.Message);
        }
        return new(PanelOutcome.ConfigurationSaved);
    }

    private DesktopReply Execute(PanelDesktopRequest request, byte[]? icc)
    {
        var desktop = runtime.Desktop;
        var hardware = runtime.Hardware;
        switch (request)
        {
            case PanelReadDesktop:
                return new(PanelOutcome.Applied);
            case PanelSetKeyboardRgb rgb:
                if (ReadKeyboard() is not { CanSetRgb: true } keyboard || keyboard.CommandOnly && rgb.Level > 1)
                    return new(PanelOutcome.Rejected, "当前背光通道或所选亮度不可用。");
                var result = desktop.SetKeyboardRgb(rgb.Rgb, rgb.Level, rgb.Rainbow);
                if (result.CommandOnly) return new(PanelOutcome.CommandSent);
                ActionOsd(rgb.Level == 0 ? "关闭背光" : rgb.Rainbow ? "彩色循环" : "静态单色", "lighting");
                return new(PanelOutcome.ReadbackConfirmed);
            case PanelUndoKeyboardRgb:
                if (!hardware.CanUndoKeyboardLighting)
                    return new(PanelOutcome.Rejected, "没有本次接管可撤销的灯光修改。");
                desktop.UndoKeyboardLighting();
                ActionOsd("已撤销上次灯光更改", "lighting");
                return new(PanelOutcome.ReadbackConfirmed);
            case PanelSetMonochrome mono:
                if (!hardware.Active || !hardware.CoreDelegated || !CoreMachine.Current.MonochromeControl ||
                    CoreMachine.Current.MonochromeLevels?.Contains(mono.Level) != true)
                    return new(PanelOutcome.Rejected, "设备未提供此白色背光档位。");
                var monochrome = desktop.SetMonochrome(mono.Level);
                return monochrome.Readback?.MonochromeLevel == mono.Level
                    ? new(PanelOutcome.ReadbackConfirmed)
                    : new(PanelOutcome.CommandSent, monochrome.ReadError);
            case PanelSetLightingZone zone:
                var capability = ReadZones().FirstOrDefault(s => s.SavedCommand.Zone == zone.Zone);
                if (capability is not { CanSet: true } || !capability.Effects.Contains(zone.Effect))
                    return new(PanelOutcome.Rejected, "当前设备未提供此灯区或灯效。");
                int speed = capability.HasSpeed ? zone.Speed : capability.SavedCommand.Speed;
                hardware.ApplyDesktopService("lighting-" + zone.Zone,
                    () => desktop.SetLightingZone(new(zone.Zone, zone.Enabled, zone.Effect, zone.Level, speed, $"#{zone.Rgb:X6}")),
                    needsTakeover: true);
                return new(PanelOutcome.CommandSent, "Core 已确认灯区指令并保存设置。");
            case PanelSetColor color:
                hardware.ApplyDesktopService("display-color", () =>
                    desktop.ApplyColorSelection(color.Enabled, color.PresetId, color.Factory,
                        color.Values is { } values ? preset => SetColorValues(preset, values) : null));
                return new(color.Enabled ? PanelOutcome.ReadbackConfirmed : PanelOutcome.ConfigurationSaved);
            case PanelApplyIcc profile:
                hardware.ApplyDesktopService("icc-profile", () => desktop.ApplyIcc(profile.Profile, profile.Device));
                return new(PanelOutcome.ReadbackConfirmed);
            case PanelImportIcc import:
                if (icc?.Length != import.Bytes) return new(PanelOutcome.Rejected, "ICC 上传不完整。");
                hardware.ApplyDesktopService("icc-import", () => desktop.ImportIcc(import.FileName, icc));
                return new(PanelOutcome.ConfigurationSaved, "ICC 已导入。请选择配置文件以应用。");
            case PanelSetPowerPolicy power:
                bool ec = power.Key is "usb" or "recovery";
                if (power.Key == "master" && !DesktopPower.ReadPolicies().Any(p => p.Supported))
                    return new(PanelOutcome.Rejected, "Windows 未提供可切换的睡眠策略。");
                hardware.ApplyDesktopService("power-" + power.Key, () => desktop.SetPowerPolicy(power.Key, power.Enabled), ec);
                return new(PanelOutcome.ReadbackConfirmed);
            case PanelSelectPowerPlan plan:
                desktop.SelectPowerPlan(plan.Scheme, plan.Expected);
                return new(PanelOutcome.ReadbackConfirmed);
            case PanelRecoverPowerPlans:
                return new(PanelOutcome.Applied, desktop.RecoverPowerPlans());
            default:
                return new(PanelOutcome.Rejected);
        }
    }

    private async Task<PanelDesktopSnapshot?> ReadAsync(PanelDesktopGroup group)
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) return null;
        try { return await Task.Run(() => Read(group)).ConfigureAwait(false); }
        finally { runtime.Operations.Release(); }
    }

    private PanelDesktopSnapshot? Read(PanelDesktopGroup? group) => group switch
    {
        PanelDesktopGroup.Lighting => new(Lighting: new(TryRead("keyboard", ReadKeyboard), TryRead("zones", ReadZones) ?? [])),
        PanelDesktopGroup.Color => new(Color: ReadColor()),
        PanelDesktopGroup.Power => new(Power: ReadPower()),
        _ => null
    };

    private PanelKeyboardSnapshot ReadKeyboard()
    {
        var hardware = runtime.Hardware;
        var state = runtime.Desktop.ReadKeyboardLighting(board.Value);
        var light = state.Confirmed;
        PanelSetKeyboardRgb? confirmed = null;
        if (light != null)
        {
            uint Channel(int value) => (uint)Math.Clamp((int)Math.Round(value * 255d / 50), 0, 255);
            uint color = Channel(light.Red) << 16 | Channel(light.Green) << 8 | Channel(light.Blue);
            int level = (light.Backlight & 2) != 0 ? 0 : Math.Clamp(light.Backlight >> 5, 0, 4);
            confirmed = new(color, level, (light.Trigger & Vault.RainbowMask) != 0);
        }
        bool canRgb = hardware.Active && state.Capability.Kind == "SingleZone" && (confirmed != null || state.CommandOnly);
        int[] levels = hardware.Active && hardware.CoreDelegated && CoreMachine.Current.MonochromeControl
            ? state.MonochromeLevels : [];
        int? mono = state.Switches?.MonochromeLevel;
        return new(state.Capability.Kind, state.Capability.Description, canRgb, hardware.CanUndoKeyboardLighting,
            state.CommandOnly, confirmed, levels, mono is int actual && state.MonochromeLevels.Contains(actual) ? actual : null);
    }

    private PanelZoneSnapshot[] ReadZones()
    {
        var state = Bridge().Read(15);
        var saved = state.GetProperty("saved");
        return
        [
            Zone("front", "lb", !Flag(state, "ec_front"), ZoneEffects),
            Zone("hinge", "hinge", true, ZoneEffects),
            Zone("logo", "logo", false, Flag(state, "ec_logo") ? EcLogoEffects : ZoneEffects)
        ];

        PanelZoneSnapshot Zone(string key, string prefix, bool speedAvailable, int[] effects)
        {
            string color = saved.GetProperty(prefix + "_color").GetString() ?? "";
            uint.TryParse(color.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb);
            int speed = saved.TryGetProperty(prefix + "_speed", out var value) ? value.GetInt32() : 2;
            var command = new PanelSetLightingZone(key, Flag(saved, prefix + "_enabled"),
                saved.GetProperty(prefix + "_effect").GetInt32(), saved.GetProperty(prefix + "_brightness").GetInt32(), speed, rgb);
            return new(command, runtime.Hardware.Active && Flag(state, key), speedAvailable, effects);
        }
    }

    private PanelColorSnapshot ReadColor()
    {
        var state = TryRead("color", () => Bridge().ReadColorService());
        var icc = TryRead("icc", ReadIcc);
        if (state == null) return new(null, false, null, null, [], icc);
        PanelColorPreset? Preset(ColorPreset preset)
        {
            if (preset.Id is not { Length: > 0 and <= 100 } id) return null;
            return new(id, Label(preset.Name ?? id), GetColorValues(preset), state.FactoryPresets.Any(p => p.Id == id));
        }
        var active = Preset(state.ActivePreset);
        var choices = state.Presets.Select(Preset).OfType<PanelColorPreset>().ToList();
        if (active != null && choices.All(p => p.Id != active.Id)) choices.Add(active);
        return new(state.RuntimeEnabled, state.SavedEnabled, state.GammaApplied, active, choices.ToArray(), icc);
    }

    private PanelIccSnapshot? ReadIcc()
    {
        var state = Bridge().Read(14);
        if (state.ValueKind != JsonValueKind.Object ||
            state.GetProperty("current_device_name").GetString() is not { Length: > 0 and <= 128 } device)
            return null;
        var profiles = state.GetProperty("profiles").EnumerateArray().Select(profile =>
            new PanelDesktopChoice(profile.GetProperty("id").GetString()!, Label(profile.GetProperty("name").GetString() ?? "")))
            .Where(profile => profile.Id is { Length: > 0 and <= 512 }).ToArray();
        return new(device, state.GetProperty("active_profile_id").GetString() ?? "default_native", Flag(state, "enabled"),
            Flag(state, "hdr_active"), profiles);
    }

    private PanelPowerSnapshot ReadPower()
    {
        var plans = TryRead("power-plans", () =>
        {
            var api = new WindowsPowerPlans();
            return new PanelPowerSnapshot(api.List().Select(p => new PanelPowerPlan(p.Id, Label(p.Name))).ToArray(),
                api.Active(), [], runtime.Display.LowRefreshOnBattery);
        }) ?? new([], null, [], runtime.Display.LowRefreshOnBattery);
        var windows = TryRead("power-policies", DesktopPower.ReadPolicies) ?? [];
        var hardware = TryRead("power-options", () => Bridge().ReadDesktopPower());
        bool? usb = hardware?.Trigger is byte trigger ? (trigger & Vault.UsbChargeMask) != 0 : null;
        bool? recovery = hardware?.Recovery is byte ac ? (ac & Vault.AcRecoveryMask) != 0 : null;
        bool hasWindowsPolicies = windows.Any(p => p.Supported);
        return plans with
        {
            Policies =
            [
                new("office", runtime.Hardware.Active, runtime.Performance.SleepOfficeEnabled),
                new("link", true, runtime.Performance.PowerLinkEnabled),
                new("master", hasWindowsPolicies, hasWindowsPolicies ? windows.Where(p => p.Supported).All(p => p.Enabled == true) : null),
                new("usb", runtime.Hardware.Active && usb.HasValue, usb),
                new("recovery", runtime.Hardware.Active && recovery.HasValue, recovery),
                .. windows.Select(p => new PanelPowerPolicy(p.Definition.Key, p.Supported, p.Enabled))
            ]
        };
    }

    private static T? TryRead<T>(string group, Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.desktop.read_failed", error, new { group }, throttle: true);
            return null;
        }
    }

    private static PanelDesktopGroup? GroupOf(PanelDesktopRequest request) => request switch
    {
        PanelReadDesktop read => read.Group,
        PanelSetKeyboardRgb or PanelUndoKeyboardRgb or PanelSetMonochrome or PanelSetLightingZone => PanelDesktopGroup.Lighting,
        PanelSetColor or PanelApplyIcc or PanelImportIcc => PanelDesktopGroup.Color,
        PanelSetPowerPolicy or PanelSelectPowerPlan or PanelRecoverPowerPlans or PanelSetLowRefresh => PanelDesktopGroup.Power,
        _ => null
    };

    private static PanelColorValues? GetColorValues(ColorPreset preset)
    {
        if (preset.Get(ColorParameter.TemperatureKelvin) is not double temperature ||
            preset.Get(ColorParameter.Saturation) is not double saturation || preset.Get(ColorParameter.Contrast) is not double contrast ||
            preset.Get(ColorParameter.Gamma) is not double gamma || preset.Get(ColorParameter.RedGain) is not double red ||
            preset.Get(ColorParameter.GreenGain) is not double green || preset.Get(ColorParameter.BlueGain) is not double blue)
            return null;
        return new((int)temperature, saturation, contrast, gamma, red, green, blue);
    }

    private static void SetColorValues(ColorPreset preset, PanelColorValues values)
    {
        (ColorParameter Key, double? Value)[] changed =
        [
            (ColorParameter.TemperatureKelvin, values.Temperature), (ColorParameter.Saturation, values.Saturation),
            (ColorParameter.Contrast, values.Contrast), (ColorParameter.Gamma, values.Gamma),
            (ColorParameter.RedGain, values.Red), (ColorParameter.GreenGain, values.Green), (ColorParameter.BlueGain, values.Blue)
        ];
        foreach (var (key, value) in changed)
            if (value is double number) preset.Set(key, number);
    }

    private UpstreamCoreLibrary Bridge() => runtime.Bridge ?? throw new IOException("设备未连接。");
    private void ActionOsd(string text, string icon) => notify(new(text, icon, "#92B9FF"));
    private static bool Flag(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.True;
    private static string Label(string label) => label.Length <= 160 ? label : label[..160];
}
