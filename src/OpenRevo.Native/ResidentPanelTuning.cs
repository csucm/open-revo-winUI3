using System.Diagnostics;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PanelTuningSnapshot? residentTuning;
    private readonly Dictionary<string, TuningProfile> residentTuningEdits = new(StringComparer.Ordinal);
    private PanelDeclaredProfile? residentDeclaredDraft;
    private string? residentDeclaredModel;
    private bool residentHasTuningSelection;

    private bool PaintClassicTuningReady => residentClient != null ? residentTuning?.Classic != null : tuningBaseline != null;
    private bool PaintClassicCpuTuning => residentClient != null ? residentTuning?.Classic?.Cpu != null : tuningBaseline?.CpuTuning == true;
    private bool PaintCanCreateTuningPreset => residentClient != null ? residentTuning?.Classic?.CanCreatePreset == true : FactoryBalancedTuningPreset != null;
    private bool PaintTuningCanApply => PaintClassicTuningReady && tuningDraft != null && !tuningLoading && !tuningApplying &&
        (residentClient != null ? PanelHardwareActive && !PanelTuningBusy : upstream != null && hardware.Active);
    private DeclaredCpuTuningLimits? PaintDeclaredLimits => residentClient == null ? hardware.DeclaredCpuTuning
        : residentTuning?.Declared is { } p ? new(p.Pl1Min, p.Pl1Max, p.Pl2Min, p.Pl2Max, p.FanCurve) : null;
    private PanelGpuTuning? PaintTuningGpu => residentClient != null ? residentTuning?.Classic?.Gpu
        : tuningBaseline?.Gpu.Capabilities is { } g ? new(g.Supported, g.ConfigurableBase, g.FloorWatts, g.BaseMaxWatts,
            g.BoostMaxWatts, g.CoreOffsetMax, g.MemoryOffsetMax) : null;
    private (int? Pl1, int? Pl2, int? Pl4) PaintTuningCpuMax => residentClient != null
        ? (residentTuning?.Classic?.Cpu?.Pl1Max, residentTuning?.Classic?.Cpu?.Pl2Max, residentTuning?.Classic?.Cpu?.Pl4Max)
        : (tuningBaseline?.Pl1Max, tuningBaseline?.Pl2Max, tuningBaseline?.Pl4Max);

    private void RestoreResidentTuningDraft(PanelTuningEditorDraft? draft)
    {
        if (draft == null) return;
        residentHasTuningSelection = draft.HasClassicSelection;
        tuningPresetId = draft.SelectedPreset;
        foreach (var edit in draft.Classic) residentTuningEdits[edit.PresetId ?? ""] = DecodeTuning(edit.Profile);
        residentDeclaredDraft = draft.Declared;
        residentDeclaredModel = draft.DeclaredModel;
    }

    private PanelTuningEditorDraft? CaptureResidentTuningDraft()
    {
        if (residentTuningEdits.Count == 0 && residentDeclaredDraft == null && !customTuningOpen) return null;
        return new(tuningPresetId, residentTuningEdits.Select(entry => new PanelTuningPresetEdit(
            entry.Key.Length == 0 ? null : entry.Key, EncodeTuning(entry.Value))).ToArray(),
            residentDeclaredDraft, residentDeclaredModel, customTuningOpen, residentHasTuningSelection);
    }

    private void TrackResidentTuningDraft()
    {
        if (residentClient != null && tuningDraft != null)
            residentTuningEdits[tuningPresetId ?? ""] = tuningDraft.Copy();
    }

    private void TrackResidentDeclaredDraft(PanelDeclaredProfile profile)
    {
        if (residentTuning?.Declared is not { } declared || !profile.Valid) return;
        residentDeclaredModel = declared.Model;
        residentDeclaredDraft = profile;
    }

    private TuningProfile FitPaintCpuDraft(TuningProfile draft)
    {
        if (residentClient == null) return tuningBaseline!.FitCpuDraft(draft);
        return residentTuning?.Classic?.Cpu is { } cpu
            ? TuningSnapshot.FitCpuDraft(draft, cpu.Pl1Max, cpu.Pl2Max, cpu.Pl4Max) : draft;
    }

    private TuningProfile EditPaintCpuDraft(TuningProfile draft, CpuPowerLimit limit, int value)
    {
        if (residentClient == null) return tuningBaseline!.EditCpuDraft(draft, limit, value);
        var cpu = residentTuning!.Classic!.Cpu!;
        return TuningSnapshot.EditCpuDraft(draft, limit, value, cpu.Pl1Max, cpu.Pl2Max, cpu.Pl4Max);
    }

    private void LoadResidentCatalog(PanelTuningSnapshot state)
    {
        residentTuning = state;
        tuningPresets.Clear();
        if (state.Classic is { } classic)
            tuningPresets.AddRange(classic.Presets.Select(p => new TuningPreset(p.Id, p.Name, "", p.BuiltIn, DecodeTuning(p.Profile), [])));
        if (state.Declared is { } declared && residentDeclaredModel != declared.Model)
        {
            residentDeclaredModel = null;
            residentDeclaredDraft = null;
        }
    }

    private void LoadResidentClassicDraft()
    {
        var classic = residentTuning!.Classic!;
        if (tuningPresetId != null && SelectedTuningPreset == null) tuningPresetId = null;
        if (residentTuningEdits.TryGetValue(tuningPresetId ?? "", out var edited))
        {
            tuningDraft = FitPaintCpuDraft(edited.Copy());
            tuningStatus = "已恢复调校草稿。";
        }
        else if (SelectedTuningPreset is { } preset) LoadPaintedPresetDraft(preset);
        else
        {
            tuningDraft = DecodeTuning(classic.Current);
            tuningStatus = "已读取当前硬件状态，编辑仅修改草稿。";
        }
    }

    private async Task ReadResidentTuningAsync(bool automatic)
    {
        if (tuningLoading || tuningApplying || closed || PanelTuningBusy || automatic && residentSnapshot?.Ready != true) return;
        tuningLoading = true;
        tuningStatus = "正在读取调校能力和预设…";
        RefreshPaintedState();
        try
        {
            if (automatic && panelShapeChanging) await panelShapeCompletion;
            if (closed || PanelTuningBusy || mini || !customTuningOpen) return;
            var result = await SendResidentAsync(PanelOperation.Tuning, tuning: new(PanelTuningAction.Read));
            RequireResidentResult(result, PanelOutcome.Applied);
            var snapshot = result.Tuning ?? throw new IOException("后台未返回调校读数。");
            LoadResidentCatalog(snapshot);
            if (snapshot.Classic is { } classic)
            {
                if (!automatic)
                {
                    residentTuningEdits.Remove(tuningPresetId ?? "");
                    residentTuningEdits.Remove("");
                    tuningPresetId = null;
                }
                else if (!residentHasTuningSelection) tuningPresetId = classic.SelectedPreset;
                residentHasTuningSelection = true;
                LoadResidentClassicDraft();
                if (result.Message != null) tuningStatus += result.Message;
            }
            else if (snapshot.Declared != null)
            {
                tuningDraft = null;
                if (!automatic) { residentDeclaredDraft = null; residentDeclaredModel = null; }
                tuningStatus = "已读取设备声明的调校范围。";
            }
            else
            {
                tuningDraft = null;
                tuningStatus = result.Message ?? "工厂功耗边界未通过校验。";
            }
        }
        catch (Exception error)
        {
            residentTuning = null;
            tuningDraft = null;
            tuningPresets.Clear();
            tuningStatus = "读取未完成，可重新读取。" + error.Message;
        }
        finally
        {
            tuningLoading = false;
            if (!closed && !residentDetaching && customTuningOpen && !mini) Render();
        }
    }

    private async Task SelectResidentTuningPresetAsync(string id)
    {
        if (residentTuning is not { Classic: not null } state || tuningLoading || tuningApplying || PanelTuningBusy) return;
        var result = await SendResidentAsync(PanelOperation.Tuning, tuning: new(PanelTuningAction.SelectPreset, state.Baseline, id));
        RequireResidentResult(result, PanelOutcome.ConfigurationSaved);
        LoadResidentCatalog(result.Tuning!);
        tuningPresetId = id;
        residentHasTuningSelection = true;
        LoadResidentClassicDraft();
        Render();
    }

    private async Task SaveResidentCpuCeilingAsync(PanelTuningSnapshot state, CpuPowerLimit limit, int watts)
    {
        if (!ReferenceEquals(state, residentTuning) || tuningDraft == null || PanelTuningBusy) return;
        var result = await SendResidentAsync(PanelOperation.Tuning,
            tuning: new(PanelTuningAction.SetCeiling, state.Baseline, Limit: (PanelCpuLimit)limit, Watts: watts));
        RequireResidentResult(result, PanelOutcome.ConfigurationSaved);
        LoadResidentCatalog(result.Tuning!);
        tuningDraft = FitPaintCpuDraft(tuningDraft);
        TrackResidentTuningDraft();
        tuningStatus = result.Message ?? "软件上限已保存。";
        RefreshPaintedState();
    }

    private async Task ApplyResidentClassicTuningAsync()
    {
        if (!PaintTuningCanApply || residentTuning is not { Classic: not null } state || tuningDraft == null) return;
        tuningApplying = true;
        tuningStatus = "正在应用并核验…";
        RefreshPaintedState();
        try
        {
            var reply = await SendResidentAsync(PanelOperation.Tuning,
                tuning: new(PanelTuningAction.Apply, state.Baseline, Profile: EncodeTuning(tuningDraft)));
            if (reply is { Outcome: PanelOutcome.ReadbackConfirmed, Tuning.Classic: { } classic })
            {
                LoadResidentCatalog(reply.Tuning);
                tuningDraft = DecodeTuning(classic.Current);
                TrackResidentTuningDraft();
                tuningStatus = "极客调校已应用并回读确认。" + reply.Message;
            }
            else
            {
                if (reply.Outcome == PanelOutcome.Unknown)
                {
                    TrackResidentTuningDraft();
                    residentTuning = null;
                    tuningDraft = null;
                    tuningPresets.Clear();
                }
                tuningStatus = "应用未确认。" + reply.Message;
            }
        }
        catch (Exception error) { tuningStatus = "应用未确认。" + error.Message; }
        finally
        {
            tuningApplying = false;
            if (!closed && !residentDetaching && customTuningOpen) Render();
        }
    }

    private async Task RunResidentPresetActionAsync(string command)
    {
        if (residentTuning is not { Classic: not null } state || tuningDraft == null || tuningLoading || tuningApplying || PanelTuningBusy)
            return;
        Guid baseline = state.Baseline;
        var selected = SelectedTuningPreset;
        var profile = EncodeTuning(tuningDraft);
        PanelTuningRequest request;
        byte[]? import = null;
        switch (command)
        {
            case "new":
                if (await PaintPresetName("新建手动方案", "自定义方案") is not { } name) return;
                request = new(PanelTuningAction.CreatePreset, baseline, Name: name);
                break;
            case "save":
                string? newName = selected == null ? await PaintPresetName("保存方案", "新方案") : null;
                if (selected == null && newName == null) return;
                request = new(PanelTuningAction.SavePreset, baseline, selected?.Id, newName, profile);
                break;
            case "restore":
                if (selected is not { BuiltIn: true }) return;
                request = new(PanelTuningAction.RestorePreset, baseline, selected.Id);
                break;
            case "rename":
                if (selected == null || await PaintPresetName("重命名方案", selected.Name) is not { } renamed) return;
                request = new(PanelTuningAction.RenamePreset, baseline, selected.Id, renamed, profile);
                break;
            case "import":
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".json");
                if (await picker.PickSingleFileAsync() is not { } file) return;
                await using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    if (stream.Length is < 1 or > PanelProtocol.MaximumTuningPresetBytes)
                        throw new IOException("方案文件不能为空且不能超过 1 MiB。");
                    import = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(import);
                }
                request = new(PanelTuningAction.ImportPreset, baseline, Bytes: import.Length);
                break;
            case "export":
                request = new(PanelTuningAction.ExportPreset, baseline, selected?.Id, Profile: profile);
                break;
            case "folder":
                request = new(PanelTuningAction.OpenDirectory, baseline);
                break;
            case "delete":
                if (selected is not { BuiltIn: false } ||
                    !await Confirm("删除“" + selected.Name + "”？", "方案文件会移到 trash 目录，当前硬件参数保持。")) return;
                request = new(PanelTuningAction.DeletePreset, baseline, selected.Id);
                break;
            default: return;
        }
        if (closed || !ReferenceEquals(state, residentTuning) || PanelTuningBusy) return;
        var reply = await SendResidentAsync(PanelOperation.Tuning, tuning: request, tuningImport: import);
        if (command is "folder" or "export")
        {
            RequireResidentResult(reply, PanelOutcome.Applied);
            if (command == "export" && reply.Tuning is { Export: { } content, ExportName: { } fileName })
                await SaveJsonFile(fileName + ".json", content);
            else if (command == "folder" && reply.Tuning?.Classic?.Directory is { } directory)
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return;
        }
        if (reply.Tuning is { } updated) LoadResidentCatalog(updated);
        RequireResidentResult(reply, PanelOutcome.ConfigurationSaved);
        var previousId = tuningPresetId;
        tuningPresetId = reply.Tuning!.Classic!.SelectedPreset;
        residentHasTuningSelection = true;
        if (command is "save" or "restore" or "rename" or "delete")
            residentTuningEdits.Remove(previousId ?? "");
        if (command != "delete") LoadResidentClassicDraft();
        else TrackResidentTuningDraft();
        tuningStatus = reply.Message ?? "方案设置已保存。";
        Render();
    }

    private async Task SubmitResidentDeclaredTuningAsync(PanelDeclaredProfile profile, bool save)
    {
        if (residentTuning is not { Declared: not null } state || PanelTuningBusy) return;
        var action = save ? PanelTuningAction.SaveDeclared : PanelTuningAction.ApplyDeclared;
        var reply = await SendResidentAsync(PanelOperation.Tuning, tuning: new(action, state.Baseline, Declared: profile));
        RequireResidentResult(reply, save ? PanelOutcome.ConfigurationSaved : PanelOutcome.CommandSent);
        if (save)
        {
            residentDeclaredDraft = null;
            residentDeclaredModel = null;
            if (reply.Tuning is { } updated) LoadResidentCatalog(updated);
            await ShowMessage("预设已保存", reply.Message ?? "可从自定义档位再次应用。");
        }
        else await ShowMessage("CPU 调校命令已发送", "设备未提供调校回读。");
    }

    private static PanelTuningProfile EncodeTuning(TuningProfile p) => new(p.Pl1, p.Pl2, p.Pl4, p.TemperatureEnabled,
        p.TemperatureC, p.GpuSuperPerformance, p.GpuBoostEnabled, p.GpuBoostWatts, p.GpuBaseWatts,
        p.Offsets is { } o ? new(o.CoreMhz, o.MemoryMhz) : null, p.CustomFans, EncodeCurve(p.CpuCurve),
        EncodeCurve(p.GpuCurve), p.SmoothFans, p.FanStepMs);
    private static TuningProfile DecodeTuning(PanelTuningProfile p) => new(p.Pl1, p.Pl2, p.Pl4, p.TemperatureEnabled,
        p.TemperatureC, p.GpuSuperPerformance, p.GpuBoostEnabled, p.GpuBoostWatts, p.GpuBaseWatts,
        p.Offsets is { } o ? new(o.CoreMhz, o.MemoryMhz) : null, p.CustomFans, DecodeCurve(p.CpuCurve),
        DecodeCurve(p.GpuCurve), p.SmoothFans, p.FanStepMs);
    private static PanelFanPoint[] EncodeCurve(FanPoint[] curve) => curve.Select(p => new PanelFanPoint(p.Celsius, p.Percent)).ToArray();
    private static FanPoint[] DecodeCurve(PanelFanPoint[] curve) => curve.Select(p => new FanPoint(p.Celsius, p.Percent)).ToArray();
}
