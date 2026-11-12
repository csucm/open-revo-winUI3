using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.ResidentProtocol;
using Windows.Graphics;

namespace OpenRevo.ResidentPanel;

internal sealed partial class ResidentPanelWindow : Window
{
    private readonly PanelClient client;
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock receipt = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch compact = new() { Header = "紧凑面板", OnContent = "", OffContent = "" };
    private readonly ComboBox limits = new() { Header = "充电上限草稿" };
    private readonly Button acquire, release, boost, applyLimit, keepAwake;
    private readonly Button[] modes;
    private readonly Button readGpu;
    private readonly Button[] gpuModes;
    private readonly Button readSwitches;
    private readonly (Button Button, string Label, Func<PanelSwitchSnapshot, bool?> Value)[] switches;
    private readonly Button readDisplay, applyBrightness, applyRefresh;
    private readonly Button loadPlugins, stopPlugins;
    private readonly Button osdToggle, osdTheme, osdScale, osdPosition, osdMotion;
    private readonly Slider brightness = new() { Header = "亮度", Minimum = 10, Maximum = 100, StepFrequency = 1 };
    private readonly ComboBox refresh = new() { Header = "内置屏刷新率" };
    private int[] displayedRates = [];
    private int? displayedBrightness;
    private int? brightnessDraft, refreshDraft;
    private bool syncingDisplay;
    private ContentDialog? refreshPrompt;
    private PanelSnapshot snapshot;
    private long observedWake;
    private bool busy, detaching, allowClose;
    private Task? commandSettled;

    internal ResidentPanelWindow(PanelClient client)
    {
        this.client = client;
        snapshot = client.Attached.Snapshot!;
        observedWake = client.Attached.Wake!.Value;
        var draft = client.Attached.Draft!;
        Title = "OpenRevo · 常驻候选面板";
        var surface = new StackPanel { Padding = new Thickness(20), Spacing = 12 };
        surface.Children.Add(new TextBlock { Text = Title, FontSize = 20 });
        surface.Children.Add(state);
        var takeover = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        takeover.Children.Add(acquire = Command("接管", PanelOperation.Takeover));
        takeover.Children.Add(release = Command("释放", PanelOperation.Release));
        surface.Children.Add(takeover);
        var performance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modes = [Command("办公", PanelOperation.SelectMode, 1), Command("均衡", PanelOperation.SelectMode, 2),
            Command("狂暴", PanelOperation.SelectMode, 3)];
        foreach (var button in modes) performance.Children.Add(button);
        surface.Children.Add(performance);
        surface.Children.Add(boost = Command("切换强冷", PanelOperation.ToggleFanBoost));
        foreach (int percent in new[] { 60, 80, 100 })
            limits.Items.Add(new ComboBoxItem { Content = percent + "%", Tag = percent });
        limits.SelectedIndex = draft.BatteryLimit switch { 60 => 0, 80 => 1, 100 => 2, _ => -1 };
        limits.SelectionChanged += (_, _) => UpdateControls();
        surface.Children.Add(limits);
        applyLimit = new() { Content = "应用充电上限" };
        applyLimit.Click += async (_, _) => await ExecuteAsync(PanelOperation.SetBatteryLimit, SelectedLimit);
        surface.Children.Add(applyLimit);
        surface.Children.Add(keepAwake = Command("切换防休眠", PanelOperation.ToggleKeepAwake));
        surface.Children.Add(readGpu = Command("读取显卡模式", PanelOperation.ReadGpu));
        var gpu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        gpuModes = [GpuMode(1), GpuMode(2), GpuMode(3)];
        foreach (var button in gpuModes) gpu.Children.Add(button);
        surface.Children.Add(gpu);
        surface.Children.Add(readSwitches = Command("读取设备开关", PanelOperation.ReadSwitches));
        switches =
        [
            (Command("相机", PanelOperation.ToggleCamera), "相机", s => s.Camera),
            (Command("麦克风", PanelOperation.ToggleMicrophone), "麦克风", s => s.Microphone),
            (Command("Win 锁", PanelOperation.ToggleWinLock), "Win 锁", s => s.WinLock),
            (Command("Wi-Fi", PanelOperation.ToggleWifi), "Wi-Fi", s => s.Wifi),
            (Command("蓝牙", PanelOperation.ToggleBluetooth), "蓝牙", s => s.Bluetooth),
            (Command("触控板", PanelOperation.ToggleTouchpad), "触控板", s => s.Touchpad),
            (Command("Fn 锁", PanelOperation.ToggleFnLock), "Fn 锁", s => s.FnLock)
        ];
        foreach (var item in switches) surface.Children.Add(item.Button);
        surface.Children.Add(readDisplay = Command("读取显示状态", PanelOperation.ReadDisplay));
        brightnessDraft = draft.Brightness;
        refreshDraft = draft.Refresh;
        surface.Children.Add(brightness);
        brightness.ValueChanged += (_, args) =>
        {
            if (!syncingDisplay && brightness.IsEnabled) brightnessDraft = (int)args.NewValue;
        };
        applyBrightness = new() { Content = "应用亮度" };
        applyBrightness.Click += async (_, _) => await ExecuteAsync(PanelOperation.SetBrightness, (int)brightness.Value);
        surface.Children.Add(applyBrightness);
        surface.Children.Add(refresh);
        applyRefresh = new() { Content = "试切刷新率" };
        refresh.SelectionChanged += (_, _) =>
        {
            if (!syncingDisplay && refresh.IsEnabled) refreshDraft = refresh.SelectedItem as int?;
            if (applyRefresh != null) applyRefresh.IsEnabled = !busy && !detaching &&
                refresh.SelectedItem is int selected && selected != snapshot.Display.Hertz;
        };
        applyRefresh.Click += async (_, _) =>
        {
            if (refresh.SelectedItem is int selected && snapshot.Display.Hertz is int expected)
                await ExecuteAsync(PanelOperation.BeginRefresh, selected, expected);
        };
        surface.Children.Add(applyRefresh);
        AddDesktopControls(surface, draft);
        osdToggle = new();
        osdToggle.Click += async (_, _) => await ExecuteAsync(PanelOperation.SetOsdEnabled, snapshot.Osd.Enabled ? 0 : 1);
        osdTheme = OsdChoice(value => value with { Theme = NextChoice(value.Theme, "cinema", "fade", "pill") });
        osdScale = OsdChoice(value => value with { Scale = NextChoice(value.Scale, 80, 100, 120, 140) });
        osdPosition = OsdChoice(value => value with { Position = NextChoice(value.Position, "top-left", "bottom-center", "center") });
        osdMotion = OsdChoice(value => value with { Motion = NextChoice(value.Motion, "fast", "normal", "slow", "off") });
        foreach (var button in new[] { osdToggle, osdTheme, osdScale, osdPosition, osdMotion })
            surface.Children.Add(button);
        surface.Children.Add(loadPlugins = Command("加载已授权后台插件", PanelOperation.LoadBackgroundPlugins));
        surface.Children.Add(stopPlugins = Command("停止本次会话的后台插件", PanelOperation.StopBackgroundPlugins));
        AddPluginControls(surface);
        AddPreferenceControls(surface);
        compact.IsOn = draft.Compact;
        compact.Toggled += (_, _) => Resize();
        surface.Children.Add(compact);
        var hide = new Button { Content = "隐藏到托盘" };
        hide.Click += (_, _) => Close();
        surface.Children.Add(hide);
        surface.Children.Add(receipt);
        Content = new ScrollViewer { Content = surface };
        Resize();
        UpdateControls();
        AppWindow.Closing += OnClosing;
        Activated += OnPanelActivationChanged;
        Closed += (_, _) =>
        {
            client.Dispose();
            Application.Current.Exit();
        };
        client.Notice += PresentNotice;
        client.Disconnected += () => DispatcherQueue.TryEnqueue(ExitPanel);
        client.StartReader();
    }

    private int? SelectedLimit => limits.SelectedItem is ComboBoxItem { Tag: int value } ? value : null;

    private Button Command(string title, PanelOperation operation, int? value = null)
    {
        var button = new Button { Content = title };
        button.Click += async (_, _) => await ExecuteAsync(operation, value);
        return button;
    }

    private void Resize() => AppWindow.Resize(compact.IsOn ? new SizeInt32(380, 410) : new SizeInt32(540, 650));

    private Button GpuMode(int target)
    {
        var button = new Button { Content = GpuName(target) };
        button.Click += async (_, _) => await ExecuteAsync(PanelOperation.SelectGpu, target, snapshot.Gpu.FirmwareMode);
        return button;
    }

    private static string GpuName(int mode) => mode switch { 1 => "纯集显", 2 => "标准混合", 3 => "独显直连", _ => "待读取" };

    private Button OsdChoice(Func<PanelOsdAppearance, PanelOsdAppearance> change)
    {
        var button = new Button();
        button.Click += async (_, _) => await ExecuteAsync(PanelOperation.SetOsdAppearance, null,
            appearance: change(snapshot.Osd.Appearance));
        return button;
    }

    private static T NextChoice<T>(T current, params T[] options) =>
        options[(Array.IndexOf(options, current) + 1) % options.Length];

    private async Task ExecuteAsync(PanelOperation operation, int? value, int? expected = null,
        PanelOsdAppearance? appearance = null, PanelDesktopRequest? desktop = null, byte[]? icc = null,
        PanelPluginRequest? plugin = null, PanelPreferenceRequest? preference = null)
    {
        if (busy || detaching || allowClose) return;
        busy = true;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commandSettled = settled.Task;
        try
        {
            UpdateControls();
            var result = await client.SendAsync(operation, value, expected: expected, osdAppearance: appearance,
                desktop: desktop, icc: icc, plugin: plugin, preference: preference);
            UpdateSnapshot(result.Snapshot);
            if (desktop != null) PresentDesktopReply(desktop, result);
            if (plugin != null) PresentPluginReply(result.Plugins);
            if (result.Trial is { } trial)
            {
                var decision = await ConfirmTrialAsync(trial);
                if (decision == null) return;
                result = decision;
                UpdateSnapshot(result.Snapshot);
            }
            if (result.Outcome == PanelOutcome.ReadbackConfirmed)
            {
                if (operation == PanelOperation.SetBrightness) brightnessDraft = null;
                if (operation == PanelOperation.BeginRefresh && result.Snapshot?.Display.Hertz == value) refreshDraft = null;
            }
            receipt.Text = result.Message ?? (result.Outcome switch
            {
                PanelOutcome.ReadbackConfirmed => "后台已回读确认。",
                PanelOutcome.CommandSent => "后台已发送，实际状态尚未回读。",
                PanelOutcome.Applied => "后台已完成。",
                PanelOutcome.ConfigurationSaved => "后台已保存设置。",
                PanelOutcome.ConfigurationSaveFailed => "后台未能保存设置，请刷新确认当前状态。",
                PanelOutcome.Busy => "后台当前不可受理。",
                PanelOutcome.Rejected => "后台拒绝此操作。",
                _ => "结果未知，重开时请读取后台当前状态。"
            });
        }
        catch (Exception error) { receipt.Text = error.Message; }
        finally
        {
            busy = false;
            commandSettled = null;
            settled.TrySetResult();
            if (!allowClose) UpdateControls();
        }
    }

    private async Task<PanelFrame?> ConfirmTrialAsync(PanelRefreshTrial trial)
    {
        if (detaching || allowClose || Content is not FrameworkElement { XamlRoot: not null } surface) return null;
        var prompt = new ContentDialog
        {
            XamlRoot = surface.XamlRoot, Title = $"保留 {trial.Target} Hz？",
            Content = "15 秒内未确认时，后台会自动恢复。关闭面板也不会中断回退。",
            PrimaryButtonText = "保留", CloseButtonText = "恢复"
        };
        refreshPrompt = prompt;
        try
        {
            var choice = prompt.ShowAsync().AsTask();
            var remaining = trial.Deadline - DateTimeOffset.UtcNow;
            if (await Task.WhenAny(choice, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) != choice)
                prompt.Hide();
            bool keep = await choice == ContentDialogResult.Primary;
            if (detaching || allowClose) return null;
            return await client.SendAsync(keep ? PanelOperation.KeepRefresh : PanelOperation.RestoreRefresh, trialId: trial.Id);
        }
        finally { refreshPrompt = null; }
    }

    private void PresentNotice(PanelFrame notice) => DispatcherQueue.TryEnqueue(() =>
    {
        if (allowClose) return;
        if (notice.Kind == PanelFrameKind.Raise && !detaching)
        {
            observedWake = notice.Wake!.Value;
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            Activate();
        }
        UpdateSnapshot(notice.Snapshot);
        UpdateControls();
    });

    private void UpdateSnapshot(PanelSnapshot? value)
    {
        if (value != null && value.Revision > snapshot.Revision) snapshot = value;
    }

    private void UpdateControls()
    {
        string mode = snapshot.Mode switch { 1 => "办公", 2 => "均衡", 3 => "狂暴", 4 => "自定义", _ => "待读取" };
        string connection = !snapshot.Ready ? "后台启动中" : snapshot.Active ? "已接管" :
            snapshot.WaitingForTakeover ? "等待其他控制台退出" : "未接管";
        string fan = snapshot.FanBoost switch
        {
            true => "开启",
            false => "关闭",
            null when snapshot.RequestedFanBoost.HasValue => snapshot.RequestedFanBoost.Value ? "已请求开启" : "已请求关闭",
            _ => "未知"
        };
        state.Text = $"{connection} · {mode}\n充电上限 {snapshot.BatteryLimit?.ToString() ?? "—"}% · 强冷 {fan}\n防休眠 {(snapshot.KeepAwake ? "开启" : "关闭")}";
        var gpu = snapshot.Gpu;
        state.Text += !gpu.Fresh ? "\n显卡档位待读取" : gpu.Declared ? $"\n显卡 {GpuName(gpu.FirmwareMode)}" :
            $"\n显卡 {GpuName(gpu.RuntimeMode)}" + (gpu.RestartPending ? $" · 待重启 {GpuName(gpu.FirmwareMode)}" : "");
        bool available = snapshot.Ready && !busy && !detaching;
        acquire.IsEnabled = available && !snapshot.Active;
        release.IsEnabled = available && (snapshot.Active || snapshot.WaitingForTakeover);
        foreach (var button in modes) button.IsEnabled = available && snapshot.CanSelectMode;
        limits.IsEnabled = available && snapshot.CanSetBatteryLimit;
        applyLimit.IsEnabled = limits.IsEnabled && SelectedLimit.HasValue;
        boost.IsEnabled = available && snapshot.CanToggleFanBoost;
        keepAwake.IsEnabled = available;
        readGpu.IsEnabled = available;
        bool gpuAvailable = available && snapshot.Active && gpu.Fresh;
        gpuModes[0].IsEnabled = gpuAvailable && gpu.Integrated && !gpu.Declared;
        gpuModes[1].IsEnabled = gpuAvailable && gpu.Hybrid;
        gpuModes[2].IsEnabled = gpuAvailable && gpu.Discrete;
        readSwitches.IsEnabled = available;
        foreach (var item in switches)
        {
            bool? value = item.Value(snapshot.Switches);
            item.Button.Content = item.Label + (value == true ? " · 开启" : value == false ? " · 关闭" : " · 待读取");
            item.Button.IsEnabled = available && value.HasValue;
        }
        readDisplay.IsEnabled = available;
        var display = snapshot.Display;
        state.Text += $"\n内置屏 {display.Hertz?.ToString() ?? "—"} Hz · 亮度 {display.Brightness?.ToString() ?? "—"}%";
        syncingDisplay = true;
        try
        {
            if (!displayedRates.SequenceEqual(display.Rates))
            {
                displayedRates = display.Rates;
                refresh.ItemsSource = displayedRates;
                refresh.SelectedItem = refreshDraft is int target && displayedRates.Contains(target) ? target : display.Hertz;
            }
            else if (refreshDraft == null && display.Hertz is int hertz && displayedRates.Contains(hertz))
                refresh.SelectedItem = hertz;
            if (display.Brightness != displayedBrightness || brightnessDraft != null)
            {
                displayedBrightness = display.Brightness;
                if ((brightnessDraft ?? displayedBrightness) is int level) brightness.Value = Math.Clamp(level, 10, 100);
            }
        }
        finally { syncingDisplay = false; }
        brightness.IsEnabled = applyBrightness.IsEnabled = available && display.Brightness.HasValue && display.Hertz.HasValue;
        refresh.IsEnabled = available && display.Hertz.HasValue;
        applyRefresh.IsEnabled = refresh.IsEnabled && refresh.SelectedItem is int next && next != display.Hertz;
        var osd = snapshot.Osd;
        osdToggle.Content = "屏幕提示 · " + (osd.Enabled ? "开启" : "关闭");
        osdTheme.Content = "提示样式 · " + (osd.Appearance.Theme switch { "fade" => "渐隐", "pill" => "胶囊", _ => "影院" });
        osdScale.Content = "提示大小 · " + osd.Appearance.Scale + "%";
        osdPosition.Content = "提示位置 · " + (osd.Appearance.Position switch { "center" => "居中", "bottom-center" => "底部居中", _ => "左上" });
        osdMotion.Content = "动画速度 · " + (osd.Appearance.Motion switch { "fast" => "快", "slow" => "慢", "off" => "关闭", _ => "正常" });
        osdToggle.IsEnabled = osdTheme.IsEnabled = osdScale.IsEnabled = osdPosition.IsEnabled = osdMotion.IsEnabled = available;
        state.Text += $"\n后台插件 {snapshot.RunningBackgroundPlugins} 个";
        loadPlugins.IsEnabled = available;
        stopPlugins.IsEnabled = available && snapshot.RunningBackgroundPlugins > 0;
        UpdateDesktopAvailability(available);
        UpdatePluginAvailability(available);
        UpdatePreferenceControls(available);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (detaching) return;
        detaching = true;
        refreshPrompt?.Hide();
        desktopPrompt?.Hide();
        pluginPrompt?.Hide();
        settingsPrompt?.Hide();
        UpdateControls();
        try
        {
            if (commandSettled is { } pending) await pending;
            var draft = new PanelDraft(compact.IsOn, SelectedLimit, keyboardDraft, zoneDrafts.Values.ToArray(), colorDraft,
                brightnessDraft, refreshDraft, monochromeDraft, iccDraft, planDraft);
            var result = await client.SendAsync(PanelOperation.Detach, draft: draft,
                observedWake: observedWake);
            if (result.Outcome != PanelOutcome.Detached) throw new IOException("后台没有确认界面状态交接。");
            ExitPanel();
        }
        catch (Exception error)
        {
            receipt.Text = error.Message;
            detaching = false;
            if (!allowClose) UpdateControls();
        }
    }

    private void ExitPanel()
    {
        if (allowClose) return;
        allowClose = true;
        Close();
    }
}
