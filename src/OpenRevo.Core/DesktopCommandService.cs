using System.Security.Cryptography;
using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public sealed record ZoneLightingRequest(string Zone, bool Enabled, int Effect, int Brightness, int Speed, string? Color);
public sealed record KeyboardLightResult(bool CommandOnly, LightingReceipt? Receipt);
public sealed record MonochromeResult(int RequestedLevel, OverviewSwitches? Readback, string? ReadError);
public sealed record KeyboardLightingRead(KeyboardCapability Capability, bool CommandOnly,
    KeyboardLighting? Confirmed, int[] MonochromeLevels, OverviewSwitches? Switches, string? Error);

// Commands retain the existing HAL and Rust checks. The in-process desktop
// adapter holds Operations through its legacy action/refresh presentation path.
public sealed class DesktopCommandService
{
    private readonly ApplicationHardwareRuntime runtime;

    internal DesktopCommandService(ApplicationHardwareRuntime runtime) => this.runtime = runtime;

    public KeyboardLightingRead ReadKeyboardLighting(string board)
    {
        var hardware = runtime.Hardware;
        var bridge = Bridge();
        var caps = bridge.Read(5);
        string? hint = null;
        foreach (var model in bridge.Read(6).EnumerateArray())
        {
            if (model.GetProperty("model_id").GetString()?.Equals(board, StringComparison.OrdinalIgnoreCase) == true &&
                model.TryGetProperty("keyboard_type", out var modelHint))
                hint = modelHint.GetString();
        }
        var admission = CoreMachine.Current;
        bool singleZone = hardware.State is { } state && (state.Support2 & Vault.SingleZoneSupportMask) != 0 ||
            admission.Ready && admission.ClassicEc && admission.Rgb;
        var capability = KeyboardCapability.Detect(caps.GetProperty("per_key").GetBoolean(),
            caps.GetProperty("four_zone").GetBoolean(), singleZone, hint);
        if (hardware.CoreDelegated && admission.MonochromeLevels is { } levels)
        {
            var white = KeyboardCapability.Detect(false, false, false, "White");
            try { return new(white, true, null, levels, bridge.ReadOverviewSwitches(), null); }
            catch (Exception error) { return new(white, true, null, levels, null, error.Message); }
        }
        if (hardware.SupportsDeclaredRgb)
            return new(new("SingleZone", "单区 RGB", true, false), true, null, [], null, null);
        DiagnosticLog.Record("lighting.detected", new { capability.Kind, caps, modelHint = hint,
            support2 = hardware.State?.Support2, connected = hardware.Active });
        if (capability.Kind != "SingleZone") return new(capability, false, null, [], null, null);
        try { return new(capability, false, hardware.ProbeLighting(), [], null, null); }
        catch (Exception error)
        {
            DiagnosticLog.Failure("lighting.initial_read", error, new { capability.Kind });
            return new(capability, false, null, [], null, error.Message);
        }
    }

    public KeyboardLightResult SetKeyboardRgb(uint rgb, int level, bool rainbow)
    {
        if (rgb > 0xffffff || level is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(level));
        var hardware = runtime.Hardware;
        if (hardware.SupportsDeclaredRgb)
        {
            hardware.ApplyDeclaredRgb(rgb, level == 0 ? 0 : rainbow ? 2 : 1);
            return new(true, null);
        }
        return new(false, hardware.SetKeyboardLighting((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, level, rainbow));
    }

    public LightingReceipt UndoKeyboardLighting() => runtime.Hardware.UndoKeyboardLighting();

    public MonochromeResult SetMonochrome(int level)
    {
        runtime.Hardware.SendMonochromeLevel(level);
        try { return new(level, runtime.Bridge?.ReadOverviewSwitches(), null); }
        catch (Exception error) { return new(level, null, error.Message); }
    }

    public void SetLightingZone(ZoneLightingRequest request)
    {
        if (request.Zone is not ("front" or "hinge" or "logo") || request.Brightness is < 0 or > 4 ||
            request.Speed is < 0 or > 4 || request.Color is not { Length: 7 } || request.Color[0] != '#' ||
            !request.Color.Skip(1).All(char.IsAsciiHexDigit))
            throw new ArgumentException("灯区参数超出当前控制范围。", nameof(request));
        Bridge().ExecuteFeature(6, new { zone = request.Zone, enabled = request.Enabled, effect = request.Effect,
            brightness = request.Brightness, speed = request.Speed, color = request.Color });
    }

    public void ApplyColor(bool enabled, ColorPreset preset) => Bridge().ApplyColor(enabled, preset);

    public void ApplyColorSelection(bool enabled, string presetId, bool factory, Action<ColorPreset>? edit = null)
    {
        var state = Bridge().ReadColorService();
        var preset = (factory ? state.FactoryPresets : state.Presets).FirstOrDefault(p => p.Id == presetId)
            ?? (!factory && state.ActivePreset.Id == presetId ? state.ActivePreset : null)
            ?? throw new InvalidOperationException("色彩预设已变更，请刷新。");
        var next = preset.Copy();
        edit?.Invoke(next);
        ApplyColor(enabled, next);
    }

    public void ApplyIcc(string profile, string? expectedDevice)
    {
        var panel = DisplayControl.Read();
        if (!panel.Active || panel.DeviceName != expectedDevice)
            throw new IOException("物理内屏未激活或显示拓扑已改变。");
        Bridge().ExecuteFeature(4, new { device = panel.DeviceName, profile });
    }

    public void ImportIcc(string fileName, byte[] bytes)
    {
        const int maxIccBytes = 4 * 1024 * 1024;
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".icc" or ".icm") || bytes.Length is < 1 or > maxIccBytes)
            throw new IOException("ICC 或 ICM 文件不能为空且不能超过 4 MiB。");
        string stem = string.Concat(Path.GetFileNameWithoutExtension(fileName)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')).Trim('_');
        if (stem.Length > 40) stem = stem[..40];
        string name = "openrevo_" + stem + "_" + Convert.ToHexString(SHA256.HashData(bytes))[..12] + extension;
        Bridge().ExecuteFeature(5, new { name, bytes = bytes.Select(b => (int)b).ToArray() });
    }

    public void SetPowerPolicy(string key, bool enabled)
    {
        var bridge = Bridge();
        if (key is "usb" or "recovery") runtime.Hardware.SetDesktopPowerOption(bridge, key, enabled);
        else if (key == "master")
        {
            foreach (var policy in DesktopPower.ReadPolicies().Where(p => p.Supported))
                DesktopPower.SetPolicy(bridge, policy.Definition.Key, enabled);
        }
        else DesktopPower.SetPolicy(bridge, key, enabled);
    }

    public void SelectPowerPlan(Guid requested, Guid? expected = null)
    {
        var plans = new WindowsPowerPlans();
        Guid before = plans.Active();
        if (expected.HasValue && expected != before) throw new IOException("方案已被外部修改，请刷新。");
        if (!plans.List().Any(p => p.Id == requested)) throw new IOException("目标方案不存在。");
        if (plans.Active() != before) throw new IOException("方案已被外部修改，请刷新。");
        Bridge().ExecuteFeature(11, new { scheme = requested.ToString("D") });
        if (plans.Active() != requested) throw new IOException("电源方案回读不一致。");
    }

    public string RecoverPowerPlans() => PowerPlanRecovery.Restore(Bridge());

    public async Task RestoreSavedColorAsync()
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) return;
        try
        {
            await Task.Run(() =>
            {
                var bridge = Bridge();
                var saved = bridge.ReadColorService();
                if (saved.SavedEnabled)
                    runtime.Hardware.ApplyDesktopService("restore-saved-color", () => ApplyColor(true, saved.ActivePreset.Copy()));
            }).ConfigureAwait(false);
        }
        finally { runtime.Operations.Release(); }
    }

    private UpstreamCoreLibrary Bridge() => runtime.Bridge ?? throw new IOException("设备未连接。");
}
