using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.ResidentProtocol;
using Windows.UI;

namespace OpenRevo.ResidentPanel;

internal sealed partial class ResidentPanelWindow
{
    private readonly List<Button> desktopReadButtons = [];
    private readonly TextBlock lightingStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ColorPicker keyboardColor = RgbPicker();
    private readonly Slider keyboardLevel = Range("键盘亮度", 0, 4, 1);
    private readonly CheckBox keyboardOn = new() { Content = "背光开启" };
    private readonly CheckBox rainbow = new() { Content = "彩色循环" };
    private readonly ComboBox monochrome = new() { Header = "白色背光档位" };
    private readonly ComboBox zoneChoice = new() { Header = "灯区" };
    private readonly CheckBox zoneOn = new() { Content = "灯区开启" };
    private readonly ComboBox zoneEffect = new() { Header = "灯效" };
    private readonly Slider zoneLevel = Range("灯区亮度", 0, 4, 1);
    private readonly Slider zoneSpeed = Range("灯效速度", 0, 4, 1);
    private readonly ColorPicker zoneColor = RgbPicker();
    private readonly TextBlock colorStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox presetChoice = new() { Header = "色彩预设" };
    private readonly CheckBox colorOn = new() { Content = "全局色彩增强" };
    private readonly Slider[] colorSliders =
    [
        Range("色温", 4000, 9000, 50), Range("饱和度", 0, 2, .01), Range("对比度", .5, 1.5, .01),
        Range("伽马", .7, 1.3, .01), Range("红色增益", .5, 1.5, .01),
        Range("绿色增益", .5, 1.5, .01), Range("蓝色增益", .5, 1.5, .01)
    ];
    private readonly ComboBox iccChoice = new() { Header = "物理内屏 ICC" };
    private readonly ComboBox planChoice = new() { Header = "Windows 电源方案" };
    private readonly ComboBox powerChoice = new() { Header = "电源策略" };
    private readonly TextBlock powerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private Button applyRgb = null!, undoRgb = null!, applyMono = null!, applyZone = null!, applyColor = null!, resetColor = null!,
        applyIcc = null!, importIcc = null!, applyPlan = null!, recoverPlans = null!, enablePower = null!, disablePower = null!,
        lowRefresh = null!, turnOffScreen = null!;
    private PanelLightingSnapshot? lighting;
    private PanelColorSnapshot? colors;
    private PanelPowerSnapshot? powerOptions;
    private PanelSetKeyboardRgb? keyboardDraft;
    private readonly Dictionary<string, PanelSetLightingZone> zoneDrafts = [];
    private PanelSetColor? colorDraft;
    private int? monochromeDraft;
    private PanelApplyIcc? iccDraft;
    private Guid? planDraft;
    private ContentDialog? desktopPrompt;
    private bool syncingDesktop;

    private void AddDesktopControls(StackPanel surface, PanelDraft draft)
    {
        keyboardDraft = draft.Keyboard;
        foreach (var zone in draft.Zones ?? []) zoneDrafts[zone.Zone] = zone;
        colorDraft = draft.Color;
        monochromeDraft = draft.Monochrome;
        iccDraft = draft.Icc;
        planDraft = draft.PowerPlan;

        applyRgb = DesktopButton("应用键盘灯光", () => RgbIntent());
        undoRgb = DesktopButton("撤销键盘灯光", () => new PanelUndoKeyboardRgb());
        applyMono = DesktopButton("应用白色背光", () =>
            monochrome.SelectedItem is int level ? new PanelSetMonochrome(level) : null);
        applyZone = DesktopButton("应用分区灯光", ZoneIntent);
        AddGroup(surface, "键盘及分区灯光", PanelDesktopGroup.Lighting, lightingStatus,
            keyboardOn, keyboardLevel, rainbow, keyboardColor, applyRgb, undoRgb, monochrome, applyMono,
            zoneChoice, zoneOn, zoneEffect, zoneLevel, zoneSpeed, zoneColor, applyZone);

        applyColor = DesktopButton("应用色彩", ColorIntent);
        resetColor = DesktopButton("恢复所选出厂色彩", () =>
            presetChoice.SelectedItem is PanelColorPreset { FactoryAvailable: true } preset
                ? new PanelSetColor(preset.Id, colorOn.IsChecked == true, Factory: true) : null);
        applyIcc = DesktopButton("应用 ICC", () =>
            colors?.Icc is { } icc && iccChoice.SelectedItem is PanelDesktopChoice selected ? new PanelApplyIcc(icc.Device, selected.Id) : null);
        importIcc = new() { Content = "导入 ICC 或 ICM" };
        importIcc.Click += async (_, _) => await PickIccAsync();
        AddGroup(surface, "屏幕色彩", PanelDesktopGroup.Color,
            [colorStatus, presetChoice, colorOn, .. colorSliders, applyColor, resetColor, iccChoice, applyIcc, importIcc]);

        applyPlan = DesktopButton("应用电源方案", () =>
            planChoice.SelectedItem is PanelPowerPlan selected && powerOptions?.ActivePlan is Guid expected
                ? new PanelSelectPowerPlan(selected.Id, expected) : null);
        recoverPlans = new() { Content = "备份并恢复 Windows 电源方案" };
        recoverPlans.Click += async (_, _) => await ConfirmRecoveryAsync();
        enablePower = DesktopButton("开启所选策略", () => PowerIntent(true));
        disablePower = DesktopButton("关闭所选策略", () => PowerIntent(false));
        lowRefresh = DesktopButton("拔电自动低刷", () =>
            powerOptions is { } state ? new PanelSetLowRefresh(!state.LowRefreshOnBattery) : null);
        turnOffScreen = DesktopButton("关闭屏幕", () => new PanelScreenOff());
        AddGroup(surface, "电源与息屏", PanelDesktopGroup.Power, powerStatus, planChoice, applyPlan,
            recoverPlans, powerChoice, enablePower, disablePower, lowRefresh, turnOffScreen);

        keyboardColor.ColorChanged += (_, _) => CaptureRgb();
        keyboardLevel.ValueChanged += (_, _) => CaptureRgb();
        keyboardOn.Checked += (_, _) => CaptureRgb();
        keyboardOn.Unchecked += (_, _) => CaptureRgb();
        rainbow.Checked += (_, _) => CaptureRgb();
        rainbow.Unchecked += (_, _) => CaptureRgb();
        zoneChoice.SelectionChanged += (_, _) => { if (!syncingDesktop) PopulateZone(); };
        zoneEffect.SelectionChanged += (_, _) => CaptureZone();
        zoneOn.Checked += (_, _) => CaptureZone();
        zoneOn.Unchecked += (_, _) => CaptureZone();
        zoneLevel.ValueChanged += (_, _) => CaptureZone();
        zoneSpeed.ValueChanged += (_, _) => CaptureZone();
        zoneColor.ColorChanged += (_, _) => CaptureZone();
        presetChoice.SelectionChanged += (_, _) =>
        {
            if (syncingDesktop || presetChoice.SelectedItem is not PanelColorPreset preset) return;
            SyncDesktop(() => LoadColorValues(preset.Values));
            colorDraft = new(preset.Id, colorOn.IsChecked == true);
            RefreshDesktopAvailability();
        };
        colorOn.Checked += (_, _) => CaptureColorSwitch();
        colorOn.Unchecked += (_, _) => CaptureColorSwitch();
        for (int i = 0; i < colorSliders.Length; i++)
        {
            int parameter = i;
            colorSliders[i].ValueChanged += (_, args) => CaptureColorValue(parameter, args.NewValue);
        }
        monochrome.SelectionChanged += (_, _) =>
        {
            if (!syncingDesktop) monochromeDraft = monochrome.SelectedItem as int?;
            RefreshDesktopAvailability();
        };
        iccChoice.SelectionChanged += (_, _) =>
        {
            if (!syncingDesktop)
                iccDraft = colors?.Icc is { } state && iccChoice.SelectedItem is PanelDesktopChoice selected
                    ? new(state.Device, selected.Id) : null;
            RefreshDesktopAvailability();
        };
        planChoice.SelectionChanged += (_, _) =>
        {
            if (!syncingDesktop) planDraft = (planChoice.SelectedItem as PanelPowerPlan)?.Id;
            RefreshDesktopAvailability();
        };
        powerChoice.SelectionChanged += (_, _) => RefreshDesktopAvailability();
    }

    private void AddGroup(StackPanel surface, string title, PanelDesktopGroup group, params UIElement[] controls)
    {
        var content = new StackPanel { Spacing = 8 };
        var read = DesktopButton("从后台读取", () => new PanelReadDesktop(group));
        desktopReadButtons.Add(read);
        content.Children.Add(read);
        foreach (var item in controls) content.Children.Add(item);
        surface.Children.Add(new Expander
        {
            Header = title, HorizontalAlignment = HorizontalAlignment.Stretch, Content = content
        });
    }

    private Button DesktopButton(string label, Func<PanelDesktopRequest?> request)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            if (request() is { Valid: true } command) await SendDesktopAsync(command);
        };
        return button;
    }

    private Task SendDesktopAsync(PanelDesktopRequest request, byte[]? icc = null) =>
        ExecuteAsync(PanelOperation.Desktop, null, desktop: request, icc: icc);

    private async Task PickIccAsync()
    {
        if (pickerOpen || busy || detaching || allowClose) return;
        pickerOpen = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".icc");
            picker.FileTypeFilter.Add(".icm");
            var file = await picker.PickSingleFileAsync();
            if (file == null || detaching || allowClose) return;
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            long length = stream.Length;
            if (length is < 1 or > PanelProtocol.MaximumIccBytes) throw new IOException("ICC 或 ICM 文件不能为空且不能超过 4 MiB。");
            var data = new byte[(int)length];
            await stream.ReadExactlyAsync(data);
            var command = new PanelImportIcc(Path.GetFileName(file.Path), data.Length);
            if (!command.Valid) throw new IOException("ICC 或 ICM 文件名无效。");
            await SendDesktopAsync(command, data);
        }
        catch (Exception error) { if (!allowClose) receipt.Text = error.Message; }
        finally { pickerOpen = false; }
    }

    private async Task ConfirmRecoveryAsync()
    {
        if (busy || detaching || allowClose || desktopPrompt != null ||
            Content is not FrameworkElement { XamlRoot: not null } surface) return;
        var dialog = desktopPrompt = new ContentDialog
        {
            XamlRoot = surface.XamlRoot, Title = "恢复 Windows 电源方案？",
            Content = "后台会先备份现有方案，再恢复 Windows 平衡方案和 OEM 调度设置。",
            PrimaryButtonText = "备份并恢复", CloseButtonText = "取消"
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !detaching && !allowClose)
                await SendDesktopAsync(new PanelRecoverPowerPlans(true));
        }
        catch (Exception error) { if (!allowClose) receipt.Text = error.Message; }
        finally { desktopPrompt = null; }
    }

    private void PresentDesktopReply(PanelDesktopRequest command, PanelFrame reply)
    {
        if (reply.Outcome is PanelOutcome.ReadbackConfirmed or PanelOutcome.CommandSent or PanelOutcome.ConfigurationSaved)
        {
            if (command is PanelSetKeyboardRgb or PanelUndoKeyboardRgb) keyboardDraft = null;
            if (command is PanelSetLightingZone zone) zoneDrafts.Remove(zone.Zone);
            if (command is PanelSetColor) colorDraft = null;
            if (command is PanelSetMonochrome) monochromeDraft = null;
            if (command is PanelApplyIcc) iccDraft = null;
            if (command is PanelSelectPowerPlan) planDraft = null;
        }
        if (reply.Desktop?.Lighting is { } updatedLighting)
        {
            lighting = updatedLighting;
            SyncDesktop(() =>
            {
                var keyboard = lighting.Keyboard;
                if ((keyboardDraft ?? keyboard?.ConfirmedRgb) is { } rgb)
                {
                    keyboardOn.IsChecked = rgb.Level > 0;
                    keyboardLevel.Value = rgb.Level;
                    rainbow.IsChecked = rgb.Rainbow;
                    keyboardColor.Color = AsColor(rgb.Rgb);
                }
                monochrome.ItemsSource = keyboard?.MonochromeLevels;
                monochrome.SelectedItem = monochromeDraft is int desired && keyboard?.MonochromeLevels.Contains(desired) == true
                    ? desired : keyboard?.MonochromeReadback;
                string? previous = (zoneChoice.SelectedItem as PanelZoneSnapshot)?.SavedCommand.Zone;
                zoneChoice.ItemsSource = lighting.Zones;
                zoneChoice.SelectedItem = lighting.Zones.FirstOrDefault(z => z.SavedCommand.Zone == previous)
                    ?? lighting.Zones.FirstOrDefault(z => z.CanSet);
            });
            PopulateZone();
        }
        if (reply.Desktop?.Color is { } updatedColor)
        {
            colors = updatedColor;
            SyncDesktop(() =>
            {
                var active = colors.ActivePreset;
                presetChoice.ItemsSource = colors.Presets;
                var selected = colors.Presets.FirstOrDefault(p => p.Id == (colorDraft?.PresetId ?? active?.Id));
                presetChoice.SelectedItem = selected;
                colorOn.IsChecked = colorDraft?.Enabled ?? colors.RuntimeEnabled ?? colors.SavedEnabled;
                LoadColorValues(selected?.Values ?? active?.Values);
                LoadColorValues(colorDraft?.Values);
                iccChoice.ItemsSource = colors.Icc?.Profiles;
                if (colors.Icc is { } icc)
                {
                    string profile = iccDraft is { } pending && pending.Device == icc.Device ? pending.Profile : icc.ActiveProfile;
                    iccChoice.SelectedItem = icc.Profiles.FirstOrDefault(p => p.Id == profile);
                }
            });
        }
        if (reply.Desktop?.Power is { } updatedPower)
        {
            powerOptions = updatedPower;
            SyncDesktop(() =>
            {
                string? key = (powerChoice.SelectedItem as PanelPowerPolicy)?.Key;
                planChoice.ItemsSource = powerOptions.Plans;
                planChoice.SelectedItem = powerOptions.Plans.FirstOrDefault(p => p.Id == planDraft)
                    ?? powerOptions.Plans.FirstOrDefault(p => p.Id == powerOptions.ActivePlan);
                powerChoice.ItemsSource = powerOptions.Policies;
                powerChoice.SelectedItem = powerOptions.Policies.FirstOrDefault(p => p.Key == key);
            });
        }
    }

    private void PopulateZone()
    {
        if (zoneChoice.SelectedItem is not PanelZoneSnapshot zone)
        {
            RefreshDesktopAvailability();
            return;
        }
        var values = zoneDrafts.GetValueOrDefault(zone.SavedCommand.Zone) ?? zone.SavedCommand;
        SyncDesktop(() =>
        {
            zoneOn.IsChecked = values.Enabled;
            zoneEffect.ItemsSource = zone.Effects;
            zoneEffect.SelectedItem = values.Effect;
            zoneLevel.Value = values.Level;
            zoneSpeed.Value = values.Speed;
            zoneColor.Color = AsColor(values.Rgb);
        });
        RefreshDesktopAvailability();
    }

    private PanelSetKeyboardRgb RgbIntent()
    {
        int level = keyboardOn.IsChecked != true ? 0 : lighting?.Keyboard?.CommandOnly == true
            ? 1 : Math.Max(1, (int)keyboardLevel.Value);
        return new(Rgb(keyboardColor.Color), level, rainbow.IsChecked == true);
    }

    private PanelSetLightingZone? ZoneIntent() =>
        zoneChoice.SelectedItem is PanelZoneSnapshot zone && zoneEffect.SelectedItem is int effect
            ? new(zone.SavedCommand.Zone, zoneOn.IsChecked == true, effect, (int)zoneLevel.Value, (int)zoneSpeed.Value, Rgb(zoneColor.Color))
            : null;

    private PanelSetColor? ColorIntent() =>
        presetChoice.SelectedItem is PanelColorPreset preset
            ? new(preset.Id, colorOn.IsChecked == true, Values: colorDraft?.Values) : null;

    private PanelDesktopRequest? PowerIntent(bool enabled) =>
        powerChoice.SelectedItem is PanelPowerPolicy { Supported: true } policy ? new PanelSetPowerPolicy(policy.Key, enabled) : null;

    private void CaptureRgb()
    {
        if (!syncingDesktop && lighting?.Keyboard?.CanSetRgb == true) keyboardDraft = RgbIntent();
    }

    private void CaptureZone()
    {
        if (syncingDesktop) return;
        if (ZoneIntent() is { Valid: true } draft) zoneDrafts[draft.Zone] = draft;
        RefreshDesktopAvailability();
    }

    private void CaptureColorSwitch()
    {
        if (!syncingDesktop && ColorIntent() is { } draft) colorDraft = draft;
    }

    private void CaptureColorValue(int parameter, double value)
    {
        if (syncingDesktop || ColorIntent() is not { } draft) return;
        var values = draft.Values ?? new();
        var changed = parameter switch
        {
            0 => values with { Temperature = (int)value },
            1 => values with { Saturation = value },
            2 => values with { Contrast = value },
            3 => values with { Gamma = value },
            4 => values with { Red = value },
            5 => values with { Green = value },
            _ => values with { Blue = value }
        };
        colorDraft = draft with { Values = changed };
    }

    private void LoadColorValues(PanelColorValues? values)
    {
        if (values == null) return;
        double?[] all = [values.Temperature, values.Saturation, values.Contrast, values.Gamma, values.Red, values.Green, values.Blue];
        for (int i = 0; i < all.Length; i++)
            if (all[i] is double number) colorSliders[i].Value = Math.Clamp(number, colorSliders[i].Minimum, colorSliders[i].Maximum);
    }

    private void RefreshDesktopAvailability()
    {
        if (!syncingDesktop && !allowClose)
            UpdateDesktopAvailability(snapshot.Ready && !busy && !detaching);
    }

    private void UpdateDesktopAvailability(bool available)
    {
        foreach (var button in desktopReadButtons) button.IsEnabled = available;
        var keyboard = lighting?.Keyboard;
        bool rgb = available && snapshot.Active && keyboard is { CanSetRgb: true };
        applyRgb.IsEnabled = keyboardOn.IsEnabled = keyboardColor.IsEnabled = rainbow.IsEnabled = rgb;
        keyboardLevel.IsEnabled = rgb && keyboard?.CommandOnly == false;
        undoRgb.IsEnabled = available && snapshot.Active && keyboard?.CanUndoRgb == true;
        monochrome.IsEnabled = available && snapshot.Active && keyboard?.MonochromeLevels.Length > 0;
        applyMono.IsEnabled = monochrome.IsEnabled && monochrome.SelectedItem is int;
        zoneChoice.IsEnabled = available && lighting != null;
        var zone = zoneChoice.SelectedItem as PanelZoneSnapshot;
        bool zoneEditable = available && snapshot.Active && zone is { CanSet: true };
        zoneOn.IsEnabled = zoneLevel.IsEnabled = zoneEffect.IsEnabled = zoneColor.IsEnabled = zoneEditable;
        zoneSpeed.IsEnabled = zoneEditable && zone?.HasSpeed == true;
        applyZone.IsEnabled = zoneEditable && zoneEffect.SelectedItem is int;
        lightingStatus.Text = (keyboard == null ? "键盘能力待读取" :
            keyboard.Name + (keyboard.ConfirmedRgb != null ? " · 已回读" : " · 当前状态未知")) +
            (zone is { CanSet: true } ? "\n分区灯光显示上次确认发送的指令" : "");
        presetChoice.IsEnabled = available && colors?.RuntimeEnabled.HasValue == true;
        bool colorEditable = presetChoice.IsEnabled && presetChoice.SelectedItem is PanelColorPreset;
        colorOn.IsEnabled = applyColor.IsEnabled = colorEditable;
        foreach (var slider in colorSliders) slider.IsEnabled = colorEditable;
        resetColor.IsEnabled = colorEditable && presetChoice.SelectedItem is PanelColorPreset { FactoryAvailable: true };
        iccChoice.IsEnabled = available && colors?.Icc != null;
        applyIcc.IsEnabled = iccChoice.IsEnabled && iccChoice.SelectedItem is PanelDesktopChoice;
        importIcc.IsEnabled = available;
        colorStatus.Text = colors?.RuntimeEnabled switch
        {
            true when colors.GammaApplied != true => "色彩矩阵已应用 · Gamma 未确认",
            true => "全局色彩已开启",
            false => "全局色彩已关闭",
            _ => "色彩状态待读取"
        };
        if (colors?.Icc?.HdrActive == true) colorStatus.Text += "\n内屏当前启用了 HDR";
        planChoice.IsEnabled = available && powerOptions?.ActivePlan.HasValue == true;
        applyPlan.IsEnabled = planChoice.IsEnabled && planChoice.SelectedItem is PanelPowerPlan selected && selected.Id != powerOptions?.ActivePlan;
        recoverPlans.IsEnabled = planChoice.IsEnabled;
        powerChoice.IsEnabled = available && powerOptions != null;
        var policy = powerChoice.SelectedItem as PanelPowerPolicy;
        enablePower.IsEnabled = disablePower.IsEnabled = powerChoice.IsEnabled && policy is { Supported: true } &&
            (policy.Key is not ("office" or "usb" or "recovery") || snapshot.Active);
        lowRefresh.IsEnabled = available && powerOptions != null;
        lowRefresh.Content = powerOptions == null ? "拔电自动低刷 · 待读取"
            : "拔电自动低刷 · " + (powerOptions.LowRefreshOnBattery ? "开启" : "关闭");
        turnOffScreen.IsEnabled = available;
        powerStatus.Text = policy switch
        {
            { Supported: false } => "当前设备或系统未提供该策略",
            { Enabled: true } => "所选策略已开启",
            { Enabled: false } => "所选策略已关闭",
            { Supported: true } => "接电与电池设置不同",
            _ => ""
        };
    }

    private void SyncDesktop(Action update)
    {
        syncingDesktop = true;
        try { update(); }
        finally { syncingDesktop = false; }
    }

    private static Slider Range(string label, double min, double max, double step) =>
        new() { Header = label, Minimum = min, Maximum = max, StepFrequency = step, IsThumbToolTipEnabled = true };

    private static ColorPicker RgbPicker() =>
        new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = true };

    private static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
    private static Color AsColor(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
