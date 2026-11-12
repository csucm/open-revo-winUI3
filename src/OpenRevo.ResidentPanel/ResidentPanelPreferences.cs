using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.ResidentPanel;

internal sealed partial class ResidentPanelWindow
{
    private readonly TextBlock systemSummary = new() { TextWrapping = TextWrapping.Wrap };
    private Button themeChoice = null!, samplingChoice = null!, wakeChoice = null!, miniAutoHide = null!, batteryOverride = null!;
    private ContentDialog? settingsPrompt;
    private bool pickerOpen, panelActivated;

    private void AddPreferenceControls(StackPanel surface)
    {
        themeChoice = PreferenceButton(() =>
            new(PanelPreferenceKind.Theme, Number: NextChoice(snapshot.Preferences.Theme, 0, 1, 2)));
        samplingChoice = PreferenceButton(() =>
            new(PanelPreferenceKind.SampleSeconds, Number: NextChoice(snapshot.Preferences.SampleSeconds, 2, 5, 10)));
        wakeChoice = PreferenceButton(() =>
            new(PanelPreferenceKind.WakeMode, Text: NextChoice(snapshot.Preferences.WakeMode, "remember", "mini", "full")));
        miniAutoHide = PreferenceButton(() =>
            new(PanelPreferenceKind.MiniAutoHide, Enabled: !snapshot.Preferences.MiniAutoHide));
        batteryOverride = new();
        batteryOverride.Click += async (_, _) => await ChangeBatteryOverrideAsync();
        var group = new StackPanel { Spacing = 8 };
        foreach (var button in new[] { themeChoice, samplingChoice, wakeChoice, miniAutoHide, batteryOverride })
            group.Children.Add(button);
        surface.Children.Insert(2, systemSummary);
        surface.Children.Add(new Expander { Header = "面板与离电保护设置", Content = group });
    }

    private Button PreferenceButton(Func<PanelPreferenceRequest> build)
    {
        var button = new Button();
        button.Click += async (_, _) => await SendPreferenceAsync(build());
        return button;
    }

    private Task SendPreferenceAsync(PanelPreferenceRequest request) =>
        ExecuteAsync(PanelOperation.Preferences, null, preference: request);

    private async Task ChangeBatteryOverrideAsync()
    {
        if (busy || detaching || allowClose || settingsPrompt != null) return;
        if (snapshot.Preferences.BatteryOverride)
        {
            await SendPreferenceAsync(new(PanelPreferenceKind.BatteryOverride, Enabled: false));
            return;
        }
        if (Content is not FrameworkElement { XamlRoot: not null } surface) return;
        var prompt = settingsPrompt = new ContentDialog
        {
            XamlRoot = surface.XamlRoot, Title = "解除离电限制？",
            Content = "在电池或 Type-C 供电下允许狂暴与自定义模式，并停止拔电时自动降档。可能增加发热和耗电，供电不足时可能关机或丢失未保存内容。CPU、GPU 和风扇数值保护仍保留。",
            PrimaryButtonText = "解除限制", CloseButtonText = "取消"
        };
        try
        {
            if (await prompt.ShowAsync() == ContentDialogResult.Primary && !allowClose && !detaching)
                await SendPreferenceAsync(new(PanelPreferenceKind.BatteryOverride, Enabled: true, Confirmed: true));
        }
        catch (Exception error) { if (!allowClose) receipt.Text = error.Message; }
        finally { settingsPrompt = null; }
    }

    private void UpdatePreferenceControls(bool available)
    {
        var settings = snapshot.Preferences;
        if (Content is FrameworkElement surface && surface.RequestedTheme != (ElementTheme)settings.Theme)
            surface.RequestedTheme = (ElementTheme)settings.Theme;
        themeChoice.Content = "主题 · " + (settings.Theme switch { 1 => "浅色", 2 => "深色", _ => "跟随 Windows" });
        samplingChoice.Content = "摘要刷新 · " + settings.SampleSeconds + " 秒";
        wakeChoice.Content = "托盘唤醒 · " + (settings.WakeMode switch { "mini" => "紧凑", "full" => "完整", _ => "记忆上次形态" });
        miniAutoHide.Content = "Mini 失焦收起 · " + (settings.MiniAutoHide ? "开启" : "关闭");
        batteryOverride.Content = settings.BatteryOverride ? "恢复离电限制" : "解除离电限制";
        themeChoice.IsEnabled = samplingChoice.IsEnabled = wakeChoice.IsEnabled = miniAutoHide.IsEnabled =
            batteryOverride.IsEnabled = available;
        systemSummary.Text = snapshot.SystemSummary is { } sample
            ? $"CPU {sample.CpuPercent?.ToString("0") ?? "—"}% · 内存 {sample.UsedMemoryGiB?.ToString("0.0") ?? "—"} / {sample.TotalMemoryGiB?.ToString("0.0") ?? "—"} GiB\n" +
                $"电量 {sample.BatteryPercent?.ToString() ?? "—"}% · " +
                (sample.OnAc == true ? "接电" : sample.OnAc == false ? "电池供电" : "供电状态未知")
            : "等待后台系统摘要";
    }

    private void OnPanelActivationChanged(object sender, WindowActivatedEventArgs args)
    {
        panelActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!panelActivated && CanAutoHide())
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!panelActivated && CanAutoHide()) Close();
            });
    }

    private bool CanAutoHide() =>
        snapshot.Ready && compact.IsOn && snapshot.Preferences.MiniAutoHide && AppWindow.IsVisible &&
        !allowClose && !detaching && !pickerOpen &&
        refreshPrompt == null && desktopPrompt == null && pluginPrompt == null && settingsPrompt == null;
}
