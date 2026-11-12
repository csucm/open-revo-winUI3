using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PanelColorSnapshot? residentColor;
    private PanelSetColor? residentColorEdit;
    private (string? Device, bool Active)? residentColorDisplay;
    private bool residentColorReading;

    private bool? PaintColorEnabled => residentClient != null ? residentColor?.RuntimeEnabled : serviceColor?.RuntimeEnabled;
    private bool PaintColorAvailable => PaintColorEnabled.HasValue &&
        (residentClient != null ? colorDraft != null && !PanelActionBusy(PanelOperation.Desktop, typeof(PanelSetColor)) : !colorServicesBusy);
    private bool ResidentIccAvailable => residentColor?.Icc is { } icc && displayState is { Active: true } display &&
        icc.Device == display.DeviceName && !PanelActionBusy(PanelOperation.Desktop, typeof(PanelApplyIcc));

    private async Task ReadResidentColorAsync()
    {
        if (residentColorReading || residentDetaching || closed || residentSnapshot?.Ready != true) return;
        var display = residentSnapshot.Display;
        var key = (display.Device, display.Active);
        if (residentColorDisplay == key) return;
        residentColorReading = true;
        try
        {
            var result = await SendResidentAsync(PanelOperation.Desktop,
                desktop: new PanelReadDesktop(PanelDesktopGroup.Color), backgroundRead: true);
            if (result.Outcome == PanelOutcome.Applied && result.Desktop?.Color != null) residentColorDisplay = key;
        }
        catch (Exception error) { if (!closed) actions.Add("后台色彩读取：" + error.Message); }
        finally { residentColorReading = false; }
    }

    private void ApplyResidentColor(PanelColorSnapshot snapshot)
    {
        residentColor = snapshot;
        var edit = residentColorEdit;
        if (edit == null) colorDraft = CreateResidentColorDraft(snapshot.ActivePreset);
        else if (colorDraft == null)
        {
            var saved = snapshot.Presets.FirstOrDefault(preset => preset.Id == edit.PresetId);
            if (saved != null) colorDraft = CreateResidentColorDraft(saved, edit.Values);
        }
        if (initialized && !closed) RefreshPaintedState();
    }

    private static ColorPreset? CreateResidentColorDraft(PanelColorPreset? preset, PanelColorValues? edits = null)
    {
        if (preset?.Values is not { Valid: true } values || edits is { Valid: false }) return null;
        var draft = ColorPreset.CreateDraft(preset.Id, preset.Name);
        (ColorParameter Parameter, double? Value)[] parameters =
        [
            (ColorParameter.TemperatureKelvin, edits?.Temperature ?? values.Temperature),
            (ColorParameter.Saturation, edits?.Saturation ?? values.Saturation),
            (ColorParameter.Contrast, edits?.Contrast ?? values.Contrast),
            (ColorParameter.Gamma, edits?.Gamma ?? values.Gamma),
            (ColorParameter.RedGain, edits?.Red ?? values.Red),
            (ColorParameter.GreenGain, edits?.Green ?? values.Green),
            (ColorParameter.BlueGain, edits?.Blue ?? values.Blue)
        ];
        foreach (var (parameter, value) in parameters)
            if (value.HasValue) draft.Set(parameter, value.Value);
        return draft;
    }

    private static PanelSetColor ResidentColorIntent(ColorPreset preset, bool enabled)
    {
        double? temperature = preset.Get(ColorParameter.TemperatureKelvin);
        if (temperature is double kelvin && kelvin != Math.Truncate(kelvin))
            throw new IOException("色温必须是整数开尔文。");
        return new(preset.Id ?? throw new IOException("未取得色彩预设。"), enabled, Values: new(
            (int?)temperature, preset.Get(ColorParameter.Saturation), preset.Get(ColorParameter.Contrast),
            preset.Get(ColorParameter.Gamma), preset.Get(ColorParameter.RedGain), preset.Get(ColorParameter.GreenGain),
            preset.Get(ColorParameter.BlueGain)));
    }

    private async Task SendResidentColorAsync(PanelSetColor intent)
    {
        var result = await SendResidentAsync(PanelOperation.Desktop, desktop: intent);
        RequireResidentResult(result, intent.Enabled ? PanelOutcome.ReadbackConfirmed : PanelOutcome.ConfigurationSaved);
        if (intent.Factory || ReferenceEquals(residentColorEdit, intent))
        {
            residentColorEdit = null;
            colorDraft = CreateResidentColorDraft(residentColor?.ActivePreset);
        }
    }

    private async Task ResetResidentColorAsync()
    {
        if (!PaintColorAvailable || colorDraft?.Id is not { } id ||
            residentColor?.Presets.Any(preset => preset.Id == id && preset.FactoryAvailable) != true) return;
        colorDebounce?.Cancel();
        await SendResidentColorAsync(new(id, PaintColorEnabled == true, Factory: true));
    }

    private PaintSelect ResidentColorChoice(string name)
    {
        PanelColorPreset[] Presets() => residentColor?.Presets ?? [];
        var choice = new PaintSelect(name, [], -1, async index =>
        {
            var presets = Presets();
            if (!PaintColorAvailable || index < 0 || index >= presets.Length ||
                CreateResidentColorDraft(presets[index]) is not { } draft) return;
            colorDraft = draft;
            try { await SubmitColor(enabledOverride: true); }
            catch (Exception error) { if (!closed) await ShowMessage("设置未完成", error.Message); }
        }) { Width = 180 };
        paintBindings.Add(() =>
        {
            var presets = Presets();
            choice.Replace(presets.Select(preset => preset.Name).ToArray(), Array.FindIndex(presets, preset => preset.Id == colorDraft?.Id));
            choice.IsEnabled = PaintColorAvailable;
        });
        return choice;
    }

    private PaintSelect ResidentIccChoice(string name)
    {
        PanelIccSnapshot? presented = null;
        var choice = new PaintSelect(name, [], -1, async index =>
        {
            if (!ResidentIccAvailable || presented is not { } icc || index < 0 || index >= icc.Profiles.Length) return;
            try { await ApplyIcc(icc.Profiles[index].Id); }
            catch (Exception error) { if (!closed) await ShowMessage("设置未完成", error.Message); }
        }) { Width = 180 };
        paintBindings.Add(() =>
        {
            var icc = residentColor?.Icc;
            if (!ResidentIccAvailable || icc?.Device != presented?.Device ||
                icc != null && presented != null && !icc.Profiles.SequenceEqual(presented.Profiles))
                choice.DismissPopup();
            presented = icc;
            choice.Replace(icc?.Profiles.Select(profile => profile.Name).ToArray() ?? [],
                icc == null ? -1 : Array.FindIndex(icc.Profiles, profile => profile.Id == icc.ActiveProfile));
            choice.IsEnabled = ResidentIccAvailable;
        });
        return choice;
    }
}
