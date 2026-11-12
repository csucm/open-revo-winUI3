using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.ResidentPanel;

internal sealed partial class ResidentPanelWindow
{
    private readonly Expander pluginGroup = new() { Header = "插件与权限", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox pluginChoice = new() { Header = "插件" };
    private readonly TextBlock pluginStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pluginErrors = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pluginPageTitle = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel pluginPage = new() { Spacing = 6 };
    private readonly Dictionary<string, Control> pluginInputs = new(StringComparer.Ordinal);
    private Button readPlugins = null!, enablePlugin = null!, disablePlugin = null!, revokePlugin = null!;
    private ContentDialog? pluginPrompt;
    private PanelPluginPage? currentPluginPage;
    private long pluginViewSeen = -1;
    private bool pluginViewReading, syncingPluginPage;

    private PanelPluginEntry? SelectedPlugin => pluginChoice.SelectedItem as PanelPluginEntry;

    private void AddPluginControls(StackPanel surface)
    {
        var content = new StackPanel { Spacing = 8 };
        readPlugins = PluginButton("刷新插件目录", () => new(PanelPluginOperation.Refresh));
        enablePlugin = new() { Content = "启用" };
        enablePlugin.Click += async (_, _) => await EnableSelectedPluginAsync();
        disablePlugin = PluginButton("停止并禁用", () =>
            SelectedPlugin is { } selected ? new(PanelPluginOperation.Disable, selected.Key) : null);
        revokePlugin = PluginButton("停止并撤销授权", () =>
            SelectedPlugin is { } selected ? new(PanelPluginOperation.Revoke, selected.Key) : null);
        foreach (var item in new UIElement[] { readPlugins, pluginChoice, pluginStatus, enablePlugin, disablePlugin,
                     revokePlugin, pluginPageTitle, pluginPage, pluginErrors })
            content.Children.Add(item);
        pluginGroup.Content = content;
        surface.Children.Add(pluginGroup);
        pluginChoice.SelectionChanged += (_, _) =>
        {
            pluginViewSeen = -1;
            ApplyPluginPage(null);
            UpdatePluginAvailability(snapshot.Ready && !busy && !detaching);
        };
        pluginGroup.Expanding += async (_, _) =>
        {
            if (pluginChoice.ItemsSource == null && snapshot.Ready) await SendPluginAsync(new(PanelPluginOperation.Refresh));
            else RefreshPluginViewIfNeeded();
        };
        pluginGroup.Collapsed += async (_, _) => await ClosePluginViewsAsync();
    }

    private Button PluginButton(string text, Func<PanelPluginRequest?> build)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            if (build() is { Valid: true } request) await SendPluginAsync(request);
        };
        return button;
    }

    private Task SendPluginAsync(PanelPluginRequest request) =>
        ExecuteAsync(PanelOperation.Plugins, null, plugin: request);

    private async Task EnableSelectedPluginAsync()
    {
        if (SelectedPlugin is not { Supported: true } selected || (currentPluginPage?.Running ?? selected.Running) ||
            busy || detaching || allowClose || pluginPrompt != null) return;
        if (selected.Granted != null)
        {
            await SendPluginAsync(new(PanelPluginOperation.Enable, selected.Key));
            return;
        }
        if (Content is not FrameworkElement { XamlRoot: not null } surface) return;
        var permissions = new StackPanel { Spacing = 6 };
        permissions.Children.Add(new TextBlock
        {
            Text = selected.Name + " " + selected.Version + "\n包摘要 " + selected.Key.Digest,
            TextWrapping = TextWrapping.Wrap
        });
        var choices = new List<(string Permission, CheckBox Control)>();
        foreach (string permission in selected.Requested)
        {
            bool required = selected.Background && permission == "lifecycle.background";
            var checkbox = new CheckBox { Content = PermissionLabel(permission), IsChecked = true, IsEnabled = !required };
            choices.Add((permission, checkbox));
            permissions.Children.Add(checkbox);
        }
        var dialog = pluginPrompt = new ContentDialog
        {
            XamlRoot = surface.XamlRoot, Title = "审核插件权限", Content = new ScrollViewer { Content = permissions, MaxHeight = 300 },
            PrimaryButtonText = "授权并启用", CloseButtonText = "取消"
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || allowClose || detaching || !pluginGroup.IsExpanded) return;
            string[] grants = choices.Where(c => c.Control.IsChecked == true).Select(c => c.Permission).ToArray();
            await SendPluginAsync(new(PanelPluginOperation.Enable, selected.Key, grants));
        }
        catch (Exception error) { if (!allowClose) receipt.Text = error.Message; }
        finally { pluginPrompt = null; }
    }

    private void PresentPluginReply(PanelPluginSnapshot? reply)
    {
        if (reply?.Entries is { } entries)
        {
            var selected = SelectedPlugin?.Key;
            pluginChoice.ItemsSource = entries;
            pluginChoice.SelectedItem = entries.FirstOrDefault(p => p.Key == selected) ?? entries.FirstOrDefault();
            pluginErrors.Text = string.Join("\n", reply.Errors ?? []);
        }
        if (reply?.Page is { } view && SelectedPlugin?.Key == view.Key) ApplyPluginPage(view);
    }

    private async Task ClosePluginViewsAsync()
    {
        pluginPrompt?.Hide();
        pluginViewSeen = -1;
        ApplyPluginPage(null);
        if (detaching || allowClose || !snapshot.Ready) return;
        try
        {
            var result = await client.SendAsync(PanelOperation.Plugins, plugin: new(PanelPluginOperation.CloseViews));
            if (allowClose || detaching) return;
            UpdateSnapshot(result.Snapshot);
            PresentPluginReply(result.Plugins);
            UpdateControls();
        }
        catch (Exception error) { if (!allowClose) receipt.Text = error.Message; }
    }

    private void RefreshPluginViewIfNeeded()
    {
        if (pluginViewReading || busy || detaching || allowClose || !pluginGroup.IsExpanded ||
            SelectedPlugin is not { } selected || snapshot.PluginRevision <= pluginViewSeen) return;
        pluginViewSeen = snapshot.PluginRevision;
        pluginViewReading = true;
        _ = ReadPluginViewAsync(selected.Key);
    }

    private async Task ReadPluginViewAsync(PanelPluginKey key)
    {
        try
        {
            var result = await client.SendAsync(PanelOperation.Plugins, plugin: new(PanelPluginOperation.ReadView, key));
            if (allowClose || detaching) return;
            UpdateSnapshot(result.Snapshot);
            if (pluginGroup.IsExpanded && SelectedPlugin?.Key == key) PresentPluginReply(result.Plugins);
        }
        catch (Exception error) { if (!allowClose && pluginGroup.IsExpanded) receipt.Text = error.Message; }
        finally
        {
            pluginViewReading = false;
            if (!allowClose && !detaching) UpdatePluginAvailability(snapshot.Ready && !busy);
        }
    }

    private void UpdatePluginAvailability(bool available)
    {
        var selected = SelectedPlugin;
        bool running = currentPluginPage?.Running ?? selected?.Running == true;
        readPlugins.IsEnabled = available;
        pluginChoice.IsEnabled = available && pluginChoice.Items.Count != 0;
        enablePlugin.IsEnabled = available && selected?.Supported == true && !running;
        enablePlugin.Content = selected?.Granted != null ? "使用已保存授权启用" : "审核权限并启用";
        disablePlugin.IsEnabled = available && selected?.Supported == true && (selected.Enabled || running);
        revokePlugin.IsEnabled = available && selected?.Supported == true && (selected.Granted != null || selected.Enabled || running);
        pluginStatus.Text = selected == null ? "从后台读取插件目录。" : string.Join("\n", new[]
        {
            selected.Version + (selected.Theme ? " · 静态主题 · 原版面板呈现" : selected.Background ? " · 可常驻" : " · 前台使用") +
                (running ? " · 运行中" : " · 已停止"),
            selected.Description, selected.Author,
            selected.Supported ? currentPluginPage?.Fault ?? selected.Fault : "此插件类型仍由原面板处理。",
            selected.Granted == null ? "尚无此版本授权" : "已授予 " + string.Join("、", selected.Granted.Select(PermissionLabel))
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
        foreach (var model in currentPluginPage?.Controls ?? [])
            if (pluginInputs.TryGetValue(model.Id, out var control)) control.IsEnabled = available && running && model.Enabled;
        RefreshPluginViewIfNeeded();
    }

    private void ApplyPluginPage(PanelPluginPage? page)
    {
        bool sameLayout = page != null && currentPluginPage?.Key == page.Key &&
            currentPluginPage.Controls.Length == page.Controls.Length &&
            currentPluginPage.Controls.Zip(page.Controls).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind &&
                pair.First.Label == pair.Second.Label && (pair.First.Choices ?? []).SequenceEqual(pair.Second.Choices ?? []));
        currentPluginPage = page;
        pluginPageTitle.Text = page?.Title ?? "";
        syncingPluginPage = true;
        try
        {
            if (!sameLayout)
            {
                pluginInputs.Clear();
                pluginPage.Children.Clear();
                foreach (var model in page?.Controls ?? []) AddPluginControl(model);
            }
            if (page == null) return;
            foreach (var (model, index) in page.Controls.Select((model, index) => (model, index)))
            {
                if (pluginPage.Children[index] is TextBlock text) text.Text = model.Label + "\n" + model.Value;
                else if (pluginInputs.GetValueOrDefault(model.Id) is ToggleSwitch toggle) toggle.IsOn = model.Value == "true";
                else if (pluginInputs.GetValueOrDefault(model.Id) is ComboBox select)
                    select.SelectedItem = (select.ItemsSource as PanelPluginChoice[])?.FirstOrDefault(c => c.Value == model.Value);
            }
        }
        finally { syncingPluginPage = false; }
    }

    private void AddPluginControl(PanelPluginControl model)
    {
        string id = model.Id;
        Control input;
        switch (model.Kind)
        {
            case "button":
                var button = new Button { Content = model.Label };
                button.Click += async (_, _) => await PluginInputAsync(id, null);
                input = button;
                break;
            case "toggle":
                var toggle = new ToggleSwitch { Header = model.Label };
                toggle.Toggled += async (_, _) => await PluginInputAsync(id, toggle.IsOn ? "true" : "false");
                input = toggle;
                break;
            case "choice":
                var choice = new ComboBox { Header = model.Label, ItemsSource = model.Choices };
                choice.SelectionChanged += async (_, _) =>
                {
                    if (choice.SelectedItem is PanelPluginChoice selected) await PluginInputAsync(id, selected.Value);
                };
                input = choice;
                break;
            default:
                pluginPage.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap });
                return;
        }
        pluginInputs.Add(id, input);
        pluginPage.Children.Add(input);
    }

    private async Task PluginInputAsync(string id, string? value)
    {
        if (!syncingPluginPage && currentPluginPage is { Running: true } page && pluginGroup.IsExpanded)
            await SendPluginAsync(new(PanelPluginOperation.Input, page.Key, ControlId: id, Value: value));
    }

    private static string PermissionLabel(string permission) => permission switch
    {
        "lifecycle.background" => "藏盘后继续运行",
        "hardware.performance.set" => "设置性能档位",
        "hardware.fanBoost.set" => "设置强冷",
        "hardware.battery.limit.set" => "设置充电上限",
        "state.summary.read" => "读取系统摘要",
        "state.power.read" => "读取供电状态",
        "config.own" => "读写自身配置",
        "ui.view" => "在面板中显示插件控件",
        "ui.navigate" => "请求页面导航",
        _ => permission
    };
}
