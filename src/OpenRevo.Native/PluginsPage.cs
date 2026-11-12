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
            var target = navigation.MenuItems.OfType<PaintNavItem>().First(i => Equals(i.Tag, destination));
            navigation.SelectedItem = target;
        });
        summaryExtension = extensionHost.Register("builtin-system-summary", 1);
    }
    private void RenderPlugins()
    {
#if NATIVE_PLUGIN_FOLDER_ENTRY
        RenderNativePluginFolders();
#else
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
        body.Children.Add(SuiteSettingRow("启用状态摘要","关闭后释放状态订阅。",toggle));body.Children.Add(SuiteSettingRow("状态摘要","",summary));body.Children.Add(SuiteSettingRow("打开状态总览","复用现有状态，不增加采样线程",open));body.Children.Add(feedback);body.Spacing=0;
        body.Loaded += (_, _) => { loaded = true; Subscribe(); };
        body.Unloaded += (_, _) => { loaded = false; subscription?.Dispose(); subscription = null; };
        page.Children.Add(DetailSection("内置状态摘要", body));
        page.Children.Add(DetailSection("原生扩展能力",SuiteVisuals.Stack(0,
            SuiteSettingRow("状态订阅","面板与扩展共享状态。",Ui.Text("已提供",12,muted:true)),
            SuiteSettingRow("页面跳转与配置保存","独立配置；失败保留旧文件。",Ui.Text("已提供",12,muted:true)),
            SuiteSettingRow("内置示例的硬件控制","此示例只读取状态。",Ui.Text("未开放",12,muted:true)))));
#endif
    }
}
