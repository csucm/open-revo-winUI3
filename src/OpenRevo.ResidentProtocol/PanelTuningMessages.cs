using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelTuningAction
{
    Read = 1, Apply, SetCeiling, SelectPreset, CreatePreset, SavePreset, RestorePreset, RenamePreset,
    ImportPreset, ExportPreset, DeletePreset, SaveDeclared, ApplyDeclared, OpenDirectory
}

public enum PanelCpuLimit { Pl1, Pl2, Pl4 }

public sealed record PanelTuningRequest(PanelTuningAction Action, Guid Baseline = default, string? PresetId = null,
    string? Name = null, PanelTuningProfile? Profile = null, PanelDeclaredProfile? Declared = null,
    PanelCpuLimit? Limit = null, int? Watts = null, int? Bytes = null)
{
    [JsonIgnore]
    public bool Valid
    {
        get
        {
            if ((Action == PanelTuningAction.Read) != (Baseline == Guid.Empty) || !ValidPresetId(PresetId) ||
                Name is not (null or { Length: > 0 and <= 12 }) || Name?.Any(char.IsControl) == true ||
                Profile is { Valid: false } || Declared is { Valid: false } ||
                (Limit.HasValue != Watts.HasValue) ||
                (Limit.HasValue != (Action == PanelTuningAction.SetCeiling)) ||
                (Bytes.HasValue != (Action == PanelTuningAction.ImportPreset))) return false;
            return Action switch
            {
                PanelTuningAction.Read or PanelTuningAction.OpenDirectory =>
                    PresetId == null && Name == null && Profile == null && Declared == null,
                PanelTuningAction.Apply => PresetId == null && Name == null && Profile != null && Declared == null,
                PanelTuningAction.SetCeiling => Limit is { } limit && Enum.IsDefined(limit) && Watts is > 0 and <= 255 &&
                    PresetId == null && Name == null && Profile == null && Declared == null,
                PanelTuningAction.SelectPreset or PanelTuningAction.RestorePreset or PanelTuningAction.DeletePreset =>
                    PresetId != null && Name == null && Profile == null && Declared == null,
                PanelTuningAction.CreatePreset => PresetId == null && Name != null && Profile == null && Declared == null,
                PanelTuningAction.SavePreset => (PresetId == null) == (Name != null) && Profile != null && Declared == null,
                PanelTuningAction.RenamePreset => PresetId != null && Name != null && Profile != null && Declared == null,
                PanelTuningAction.ImportPreset => PresetId == null && Name == null && Profile == null && Declared == null &&
                    Bytes is > 0 and <= PanelProtocol.MaximumTuningPresetBytes,
                PanelTuningAction.ExportPreset => Name == null && Profile != null && Declared == null,
                PanelTuningAction.SaveDeclared or PanelTuningAction.ApplyDeclared =>
                    PresetId == null && Name == null && Profile == null && Declared != null,
                _ => false
            };
        }
    }

    internal static bool ValidPresetId(string? id) =>
        id == null || id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

public sealed record PanelFanPoint(int Celsius, int Percent)
{
    internal static bool HasShape(PanelFanPoint[]? curve) => curve is { Length: 8 } &&
        curve.All(point => point is { Celsius: >= 30 and <= 100, Percent: >= 0 and <= 100 });
}

public sealed record PanelClockOffsets(int CoreMhz, int MemoryMhz);
public sealed record PanelTuningProfile(int Pl1, int Pl2, int Pl4, bool TemperatureEnabled, int TemperatureC,
    bool GpuSuperPerformance, bool GpuBoostEnabled, int GpuBoostWatts, int GpuBaseWatts, PanelClockOffsets? Offsets,
    bool CustomFans, PanelFanPoint[] CpuCurve, PanelFanPoint[] GpuCurve, bool SmoothFans, int FanStepMs)
{
    [JsonIgnore] public bool Valid => PanelFanPoint.HasShape(CpuCurve) && PanelFanPoint.HasShape(GpuCurve);
}

public sealed record PanelDeclaredProfile(int? Pl1, int? Pl2, int Temperature, bool CustomFans,
    PanelFanPoint[] CpuCurve, PanelFanPoint[] GpuCurve, bool RestoreOnAc)
{
    // An incomplete declared profile is allowed during handoff only. The
    // backend requires complete values and rechecks model limits on submit.
    [JsonIgnore] public bool Valid => Pl1 is null or >= 0 && Pl2 is null or >= 0 && Temperature is 0 or >= 70 and <= 100 &&
        PanelFanPoint.HasShape(CpuCurve) && PanelFanPoint.HasShape(GpuCurve);
}

public sealed record PanelTuningPresetEdit(string? PresetId, PanelTuningProfile Profile);
public sealed record PanelTuningEditorDraft(string? SelectedPreset, PanelTuningPresetEdit[] Classic,
    PanelDeclaredProfile? Declared, string? DeclaredModel, bool Open, bool HasClassicSelection)
{
    [JsonIgnore] public bool Valid => PanelTuningRequest.ValidPresetId(SelectedPreset) && Classic != null &&
        Classic.All(edit => edit is { Profile.Valid: true } && PanelTuningRequest.ValidPresetId(edit.PresetId)) &&
        Classic.Select(edit => edit.PresetId).Distinct(StringComparer.Ordinal).Count() == Classic.Length &&
        Declared is not { Valid: false } &&
        (Declared == null ? DeclaredModel == null : DeclaredModel is { Length: > 0 and <= 256 });
}

public sealed record PanelCpuCeiling(int Minimum, int Value, int Maximum, int Default);
public sealed record PanelCpuTuning(int Pl1Max, int Pl2Max, int Pl4Max, PanelCpuCeiling Pl1, PanelCpuCeiling Pl2, PanelCpuCeiling Pl4);
public sealed record PanelGpuTuning(bool Supported, bool ConfigurableBase, int FloorWatts, int BaseMaxWatts,
    int BoostMaxWatts, int CoreOffsetMax, int MemoryOffsetMax);
public sealed record PanelTuningPreset(string Id, string Name, bool BuiltIn, PanelTuningProfile Profile);
public sealed record PanelTuningClassic(PanelTuningProfile Current, PanelCpuTuning? Cpu, PanelGpuTuning Gpu,
    PanelTuningPreset[] Presets, bool CanCreatePreset, string? SelectedPreset, string Directory);
public sealed record PanelTuningDeclared(string Model, int Pl1Min, int Pl1Max, int Pl2Min, int Pl2Max, bool FanCurve,
    PanelDeclaredProfile? Saved);
public sealed record PanelFactoryPower(byte Pl1, byte Pl2, byte Pl4);
public sealed record PanelTuningFactory(PanelFactoryPower Office, PanelFactoryPower Balance, PanelFactoryPower Turbo, string[] Presets);
public sealed record PanelTuningSnapshot(Guid Baseline, PanelTuningClassic? Classic = null,
    PanelTuningDeclared? Declared = null, PanelTuningFactory? Unverified = null, string? Export = null, string? ExportName = null);
