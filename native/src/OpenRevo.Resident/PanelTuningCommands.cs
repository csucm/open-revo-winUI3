using System.Text;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed record PanelTuningResult(PanelOutcome Outcome, string? Message = null, PanelTuningSnapshot? Snapshot = null);

// The authenticated request reader owns this editor state. The live write
// baseline never leaves the resident process, even when a panel disconnects.
internal sealed class PanelTuningCommands(ApplicationHardwareRuntime runtime)
{
    private readonly Lazy<TuningPresetStore> store = new(() => new TuningPresetStore());
    private Guid baseline;
    private TuningSnapshot? classic;
    private TuningProfile? current;
    private DeclaredCpuTuningLimits? declared;
    private string? manifest, selected;
    private TuningPreset[] defaults = [];
    private readonly List<TuningPreset> presets = [];

    internal void Reset()
    {
        baseline = Guid.Empty;
        classic = null;
        current = null;
        declared = null;
        manifest = selected = null;
        defaults = [];
        presets.Clear();
    }

    internal async Task<PanelTuningResult> ExecuteAsync(PanelTuningRequest request, byte[]? payload)
    {
        if (request.Action != PanelTuningAction.Read && (baseline == Guid.Empty || request.Baseline != baseline))
            return new(PanelOutcome.Rejected, "调校读数已失效，请重新读取。");
        try
        {
            // The performance service itself takes the shared queue. An
            // accepted write is never cancelled by closure of its panel.
            if (request.Action == PanelTuningAction.Apply)
                return await ApplyClassicAsync(request.Profile!).ConfigureAwait(false);
            if (request.Action == PanelTuningAction.ApplyDeclared)
                return await ApplyDeclaredAsync(request.Declared!).ConfigureAwait(false);
            if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) return new(PanelOutcome.Busy);
            try { return await Task.Run(() => ExecuteQueued(request, payload)).ConfigureAwait(false); }
            finally { runtime.Operations.Release(); }
        }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException)
        {
            return new(PanelOutcome.Rejected, error.Message);
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.tuning.failed", error, new { request.Action });
            return new(PanelOutcome.Unknown, error.Message);
        }
    }

    private PanelTuningResult ExecuteQueued(PanelTuningRequest request, byte[]? payload)
    {
        if (request.Action == PanelTuningAction.Read) return Read();
        if (request.Action == PanelTuningAction.SaveDeclared)
        {
            var profile = PrepareDeclared(request.Declared!);
            return SaveConfiguration(() => AppData.Update(p => p with { DeclaredTuning = profile }),
                "预设已保存，可从自定义档位再次应用。");
        }
        if (classic is not { } state || !runtime.Hardware.Active || !runtime.Hardware.SupportsCustomTuning)
            return new(PanelOutcome.Rejected, "当前调校通道不可用，请重新读取。");
        if (request.Action == PanelTuningAction.SetCeiling)
        {
            if (!state.CpuTuning) return new(PanelOutcome.Rejected, "当前通道不支持调整 CPU 软件上限。");
            var limit = (CpuPowerLimit)request.Limit!.Value;
            var range = state.CpuCeilingRange(limit);
            int watts = request.Watts!.Value;
            if (watts < range.Min || watts > range.Max) return new(PanelOutcome.Rejected, "软件上限超出本机范围。");
            var previous = state.CpuCeilings ?? new();
            var ceilings = limit switch
            {
                CpuPowerLimit.Pl1 => previous with { Pl1 = watts },
                CpuPowerLimit.Pl2 => previous with { Pl2 = watts },
                _ => previous with { Pl4 = watts }
            };
            return SaveConfiguration(() =>
            {
                AppData.Update(p => p with { CpuPowerCeilings = ceilings });
                classic = state with { CpuCeilings = ceilings };
                baseline = Guid.NewGuid();
            }, "软件上限已保存，硬件参数保持原值。");
        }
        if (request.Action == PanelTuningAction.ExportPreset)
        {
            var export = PresetDraft(request);
            HardwareSession.ValidateTuningDraft(state, export.Profile);
            return new(PanelOutcome.Applied, Snapshot: Snapshot() with
            {
                Export = TuningPresetStore.Export(export), ExportName = export.Name
            });
        }
        if (request.Action == PanelTuningAction.OpenDirectory)
        {
            Directory.CreateDirectory(store.Value.DirectoryPath);
            return new(PanelOutcome.Applied, Snapshot: Snapshot());
        }
        return ChangePresets(request, payload, state);
    }

    private PanelTuningResult Read()
    {
        Reset();
        var hardware = runtime.Hardware;
        if (hardware.DeclaredCpuTuning is { } limits && CoreMachine.Current.Manifest is { Length: > 0 and <= 256 } matched)
        {
            declared = limits;
            manifest = matched;
            baseline = Guid.NewGuid();
            return new(PanelOutcome.Applied, Snapshot: Snapshot());
        }
        if (!hardware.Active || !hardware.SupportsCustomTuning || runtime.Bridge is not { } bridge)
            return new(PanelOutcome.Rejected, "请先接管支持调校的设备。");
        TuningSnapshot state;
        try { state = hardware.ProbeTuning(bridge); }
        catch (TuningFactoryLimitsException error)
        {
            string[] names = [];
            try
            {
                names = bridge.Read(18).EnumerateArray()
                    .Select(p => p.GetProperty("name").GetString()).OfType<string>().ToArray();
            }
            catch (Exception catalogError) { DiagnosticLog.Failure("resident.tuning.catalog_failed", catalogError); }
            var r = error.Registers;
            return new(PanelOutcome.Applied, "工厂功耗边界未通过校验。", new(Guid.Empty, Unverified: new(
                new(r.OfficePl1, r.OfficePl2, r.OfficePl4), new(r.BalancePl1, r.BalancePl2, r.BalancePl4),
                new(r.TurboPl1, r.TurboPl2, r.TurboPl4), names)));
        }
        current = hardware.TuningDraft(state);
        classic = state;
        baseline = Guid.NewGuid();
        string? message = null;
        try
        {
            defaults = bridge.Read(18).EnumerateArray().Select(p => TuningPresetStore.FromOriginal(p, state, true)).ToArray();
            presets.AddRange(store.Value.Load(defaults, state));
            selected = AppData.Load().SelectedTuningPreset;
        }
        catch (Exception error)
        {
            DiagnosticLog.Failure("resident.tuning.catalog_failed", error);
            presets.AddRange(defaults);
            message = "预设目录读取未完成。";
        }
        return new(PanelOutcome.Applied, message, Snapshot());
    }

    private PanelTuningResult ChangePresets(PanelTuningRequest request, byte[]? payload, TuningSnapshot state) =>
        SaveConfiguration(() =>
        {
            switch (request.Action)
            {
                case PanelTuningAction.SelectPreset:
                    SetSelection(FindPreset(request.PresetId!).Id);
                    break;
                case PanelTuningAction.CreatePreset:
                    var balanced = defaults.FirstOrDefault(p => p.Id == "preset_balance")
                        ?? throw new ArgumentException("未取得本机均衡方案。");
                    var manual = TuningPresetStore.CreateManualSource(balanced, state);
                    PublishPreset(store.Value.Duplicate(manual, request.Name!, state));
                    break;
                case PanelTuningAction.SavePreset:
                case PanelTuningAction.RenamePreset:
                    var preset = PresetDraft(request);
                    store.Value.Save(preset, state);
                    PublishPreset(preset);
                    break;
                case PanelTuningAction.RestorePreset:
                    var original = defaults.FirstOrDefault(p => p.Id == request.PresetId)
                        ?? throw new ArgumentException("没有可恢复的内置方案。");
                    var restored = original with { Profile = state.FitCpuDraft(original.Profile).Copy() };
                    store.Value.Save(restored, state);
                    PublishPreset(restored);
                    break;
                case PanelTuningAction.ImportPreset:
                    if (payload == null || payload.Length != request.Bytes) throw new ArgumentException("方案文件传输不完整。");
                    using (var reader = new StreamReader(new MemoryStream(payload), new UTF8Encoding(false, true), true))
                        PublishPreset(store.Value.Import(reader.ReadToEnd(), state));
                    break;
                case PanelTuningAction.DeletePreset:
                    var deleted = FindPreset(request.PresetId!);
                    if (deleted.BuiltIn) throw new ArgumentException("内置方案不可删除。");
                    // Do not leave the custom-mode entry pointing to a deleted file
                    // if writing preferences fails.
                    if (selected == deleted.Id) SetSelection(null);
                    store.Value.Delete(deleted);
                    presets.Remove(deleted);
                    break;
                default:
                    throw new ArgumentException("调校请求不适用于当前通道。");
            }
        }, "方案设置已保存，硬件参数保持原值。");

    private PanelTuningResult SaveConfiguration(Action update, string message)
    {
        try { update(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Failure("resident.tuning.configuration_failed", error);
            return new(PanelOutcome.ConfigurationSaveFailed, error.Message, Snapshot());
        }
        return new(PanelOutcome.ConfigurationSaved, message, Snapshot());
    }

    private TuningPreset FindPreset(string id) => presets.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("方案已变更，请重新读取。");

    private TuningPreset PresetDraft(PanelTuningRequest request)
    {
        var profile = Decode(request.Profile!);
        if (request.PresetId is not { } id)
            return new("user_" + Guid.NewGuid().ToString("N"), TuningPresetStore.ValidName(request.Name ?? "新方案"),
                "star", false, profile, []);
        var existing = FindPreset(id);
        return existing with { Profile = profile, Name = request.Name == null ? existing.Name : TuningPresetStore.ValidName(request.Name) };
    }

    private void PublishPreset(TuningPreset preset)
    {
        int index = presets.FindIndex(p => p.Id == preset.Id);
        if (index < 0) presets.Add(preset);
        else presets[index] = preset;
        SetSelection(preset.Id);
    }

    private void SetSelection(string? id)
    {
        AppData.Update(p => p with { SelectedTuningPreset = id });
        selected = id;
    }

    private async Task<PanelTuningResult> ApplyClassicAsync(PanelTuningProfile profile)
    {
        if (classic is not { } state)
            return new(PanelOutcome.Rejected, "请先读取经典调校参数。");
        var draft = Decode(profile);
        var result = await runtime.Performance.ApplyTuningAsync(state, draft).ConfigureAwait(false);
        if (result is { Outcome: PerformanceOutcome.ReadbackConfirmed, Tuning: { } receipt })
        {
            classic = receipt.After;
            current = receipt.Profile with { Offsets = draft.Offsets };
            baseline = Guid.NewGuid();
            return new(PanelOutcome.ReadbackConfirmed, result.IndicatorWarning, Snapshot());
        }
        if (result.Outcome == PerformanceOutcome.Unknown) Reset();
        return FromPerformance(result);
    }

    private async Task<PanelTuningResult> ApplyDeclaredAsync(PanelDeclaredProfile profile)
    {
        var preset = PrepareDeclared(profile);
        var result = await runtime.Performance.ApplyDeclaredTuningAsync(preset).ConfigureAwait(false);
        return FromPerformance(result);
    }

    private DeclaredTuningPreset PrepareDeclared(PanelDeclaredProfile profile)
    {
        if (declared == null || manifest == null || profile.Pl1 is not int pl1 || profile.Pl2 is not int pl2)
            throw new ArgumentException("请读取声明式调校能力并填完功耗值。");
        var preset = new DeclaredTuningPreset(manifest, pl1, pl2, profile.Temperature, profile.CustomFans,
            Decode(profile.CpuCurve), Decode(profile.GpuCurve), profile.RestoreOnAc);
        runtime.Hardware.ValidateDeclaredCpuTuning(preset);
        return preset;
    }

    private static PanelTuningResult FromPerformance(PerformanceResult result) => new(result.Outcome switch
    {
        PerformanceOutcome.CommandSent => PanelOutcome.CommandSent,
        PerformanceOutcome.Busy or PerformanceOutcome.Closing or PerformanceOutcome.Cancelled => PanelOutcome.Busy,
        PerformanceOutcome.Unknown => PanelOutcome.Unknown,
        _ => PanelOutcome.Rejected
    }, result.Error?.Message);

    private PanelTuningSnapshot Snapshot()
    {
        if (declared is { } limits)
        {
            var saved = runtime.Hardware.SavedDeclaredTuning;
            return new(baseline, Declared: new(manifest!, limits.Pl1Min, limits.Pl1Max, limits.Pl2Min, limits.Pl2Max, limits.FanCurve,
                saved == null ? null : new(saved.Pl1, saved.Pl2, saved.Temperature, saved.CustomFans,
                    EncodeSavedCurve(saved.CpuCurve), EncodeSavedCurve(saved.GpuCurve), saved.RestoreOnAc)));
        }
        var state = classic!;
        var caps = state.Gpu.Capabilities;
        PanelCpuCeiling Ceiling(CpuPowerLimit limit)
        {
            var range = state.CpuCeilingRange(limit);
            return new(range.Min, range.Value, range.Max, (state with { CpuCeilings = null }).CpuCeilingRange(limit).Value);
        }
        return new(baseline, Classic: new(Encode(current!),
            state.CpuTuning ? new(state.Pl1Max, state.Pl2Max, state.Pl4Max,
                Ceiling(CpuPowerLimit.Pl1), Ceiling(CpuPowerLimit.Pl2), Ceiling(CpuPowerLimit.Pl4)) : null,
            new(caps.Supported, caps.ConfigurableBase, caps.FloorWatts, caps.BaseMaxWatts, caps.BoostMaxWatts,
                caps.CoreOffsetMax, caps.MemoryOffsetMax),
            presets.Select(p => new PanelTuningPreset(p.Id, p.Name, p.BuiltIn, Encode(p.Profile))).ToArray(),
            defaults.Any(p => p.Id == "preset_balance"), selected, store.Value.DirectoryPath));
    }

    private static PanelTuningProfile Encode(TuningProfile p) => new(p.Pl1, p.Pl2, p.Pl4, p.TemperatureEnabled,
        p.TemperatureC, p.GpuSuperPerformance, p.GpuBoostEnabled, p.GpuBoostWatts, p.GpuBaseWatts,
        p.Offsets is { } o ? new(o.CoreMhz, o.MemoryMhz) : null, p.CustomFans, Encode(p.CpuCurve), Encode(p.GpuCurve), p.SmoothFans, p.FanStepMs);
    private static TuningProfile Decode(PanelTuningProfile p) => new(p.Pl1, p.Pl2, p.Pl4, p.TemperatureEnabled,
        p.TemperatureC, p.GpuSuperPerformance, p.GpuBoostEnabled, p.GpuBoostWatts, p.GpuBaseWatts,
        p.Offsets is { } o ? new(o.CoreMhz, o.MemoryMhz) : null, p.CustomFans, Decode(p.CpuCurve), Decode(p.GpuCurve), p.SmoothFans, p.FanStepMs);
    private static PanelFanPoint[] Encode(FanPoint[] curve) => curve.Select(p => new PanelFanPoint(p.Celsius, p.Percent)).ToArray();
    private static PanelFanPoint[] EncodeSavedCurve(FanPoint[]? curve) =>
        Encode(curve is { Length: 8 } && curve.All(p => p != null) ? curve : TuningProfile.DefaultCurve());
    private static FanPoint[] Decode(PanelFanPoint[] curve) => curve.Select(p => new FanPoint(p.Celsius, p.Percent)).ToArray();
}
