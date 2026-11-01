using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using System.Text.Json;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private NativeExtensionHost? extensionHost;
    private NativeExtensionClient? summaryExtension;
    private void EnsureExtensionHost()
    {
        if (extensionHost != null) return;
        extensionHost = new NativeExtensionHost(Path.Combine(AppData.DirectoryPath, "extensions"), destination =>
        {
            var target = navigation.MenuItems.OfType<NavigationViewItem>().First(i => Equals(i.Tag, destination));
            navigation.SelectedItem = target;
        });
        summaryExtension = extensionHost.Register("builtin-system-summary", 1);
    }
    private void RenderPlugins()
    {
        EnsureExtensionHost();
        var client = summaryExtension!;
        var capabilities = client.QueryCapabilities();
        var feedback = Ui.Text("配置保存在本机。", 12, muted: true);
        bool configValid = true, enabled = true;
        try
        {
            if (client.LoadConfig() is { } config) enabled = config.GetProperty("enabled").GetBoolean();
        }
        catch (Exception error) { configValid = false; enabled = false; feedback.Text = "配置读取失败，已保留原文件：" + error.Message; }
        client.SetEnabled(enabled);
        var toggle = new ToggleSwitch { Header = "启用状态摘要", IsOn = enabled, IsEnabled = configValid };
        var summary = Ui.Text("等待下一次采样…", 12, muted: true);
        var body = Ui.Stack(); body.Spacing = 12;
        IDisposable? subscription = null;
        bool loaded = false;
        void Subscribe()
        {
            subscription?.Dispose(); subscription = null;
            if (!client.Enabled) { summary.Text = "已禁用 · 状态订阅已释放。"; return; }
            if (!loaded) return;
            subscription = client.Subscribe("system.summary", state =>
            {
                string cpu = state.CpuPercent is double percent ? $"{percent:0.0}%" : "—";
                string battery = state.BatteryPercent is int charge ? $"{charge}%" : "—";
                summary.Text = $"CPU {cpu}   ·   电池 {battery}   ·   采样 {state.SampledAt.ToLocalTime():HH:mm:ss}";
            });
        }
        var open = Ui.Button("打开状态总览", (_, _) =>
        {
            var result = client.Execute("ui.open.overview");
            if (!result.Success) feedback.Text = "操作失败：" + result.Code;
        });
        open.IsEnabled = enabled;
        bool reverting = false;
        toggle.Toggled += (_, _) =>
        {
            if (reverting) return;
            bool desired = toggle.IsOn;
            try
            {
                using var config = JsonDocument.Parse(desired ? "{\"enabled\":true}" : "{\"enabled\":false}");
                client.SaveConfig(config.RootElement);
                client.SetEnabled(desired); open.IsEnabled = desired; Subscribe();
                feedback.Text = "已保存 · " + (desired ? "状态摘要已启用。" : "状态摘要已禁用。");
            }
            catch (Exception error)
            {
                // Keep the runtime and saved state consistent after a failed write.
                reverting = true; toggle.IsOn = client.Enabled; reverting = false;
                toggle.IsEnabled = false;
                feedback.Text = "保存失败，原配置保留。重新进入本页后重试：" + error.Message;
            }
        };
        body.Children.Add(toggle); body.Children.Add(summary); body.Children.Add(open); body.Children.Add(feedback);
        body.Loaded += (_, _) => { loaded = true; Subscribe(); };
        body.Unloaded += (_, _) => { loaded = false; subscription?.Dispose(); subscription = null; };
        page.Children.Add(DetailSection("原生扩展", body, "内置状态摘要 · 查看实时状态，快速进入总览。"));
        page.Children.Add(DetailSection("扩展能力", Ui.Stack(
            Ui.Text($"接口 {capabilities.Api.Major}.{capabilities.Api.Minor} · 原生组件", 14, true),
            Ui.Text("支持状态订阅、页面跳转和独立配置保存。硬件控制暂未向扩展开放。", 12, muted: true))));
        page.Children.Add(DetailSection("网页插件兼容", Ui.Text("有具体旧网页插件需求时，再增加可选兼容组件。旧版 HTML、CSS 和 JavaScript 插件目前无法直接运行；插件市场尚未接入。", 12, muted: true)));
    }
}


