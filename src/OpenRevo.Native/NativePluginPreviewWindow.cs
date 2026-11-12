using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.PluginSdk;
using OpenRevo.PluginHost;

namespace OpenRevo.Native;

// Readonly native design host: no MainWindow, worker, telemetry or HAL.
internal sealed class NativePluginPreviewWindow : Window
{
    internal NativePluginPreviewWindow()
    {
        Title = "Open-Revo · 插件管理只读预览";
        AppWindow.Resize(new(1080, 820));
        var content = Paint.Stack(22); content.Padding = new(32); content.MaxWidth = 1120;
        var root = new Grid(); root.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        void Theme() => root.Background = Paint.Brush(Paint.Bg(root));
        NativePluginThemePalette.RegisterRoot(root, "main"); NativePluginThemePalette.Bind(root, Theme);
        root.Loaded += (_, _) => Theme(); root.ActualThemeChanged += (_, _) => Theme(); Theme();
        var themes = Paint.Inline(8,
            new PaintButton("浅色", () => root.RequestedTheme = ElementTheme.Light),
            new PaintButton("深色", () => root.RequestedTheme = ElementTheme.Dark));
        content.Children.Add(Paint.Between(Paint.Stack(6, Paint.Text("插件与模块", 24, true),
            Paint.Text("独立只读预览。开关保留卡片，不连接硬件。", 12, muted: true)), themes));
        var feedback = Paint.Text("可检查右侧开关、关闭后卡片保留和浅深配色。", 12, muted: true);
        var list = Paint.Stack(8);
        bool themeEnabled = false, summaryEnabled = true, showMemory = true;
        int clicks = 0;
        PluginThemePalette? palette = null;
        string[] arguments = Environment.GetCommandLineArgs();
        int themeAt = Array.IndexOf(arguments, "--theme-file");
        if (themeAt >= 0 && themeAt + 1 < arguments.Length)
        {
            palette = PluginThemePalette.Compose([PluginPackage.Read(arguments[themeAt + 1])]);
            NativePluginThemePalette.Apply(palette); Theme(); themeEnabled = true;
        }
        void RenderCards()
        {
            list.Children.Clear();
            list.Children.Add(NativePluginCards.Create(new("theme-naiwa", "奶娃主题 · 奶黄", "1.0.0", "窗口主题",
                "暖黄与深褐的浅深配色，关闭后恢复原配色。", "NaiwaTheme", "Open-Revo 示例", themeEnabled,
                themeEnabled ? "预览 · 配色已应用" : "预览 · 已关闭，卡片保留"), palette != null, value =>
                {
                    themeEnabled = value;
                    NativePluginThemePalette.Apply(value ? palette! : PluginThemePalette.Empty);
                    feedback.Text = value ? "奶蛙配色已应用。" : "主题已关闭，卡片和入口继续保留。";
                    RenderCards();
                }));
            var surface = new NativePluginViewPresenter("状态摘要示例", showHeading: false);
            PluginView Document() => new("状态", [
                new("cpu", "text", "示例 CPU 使用率", "24.6%"),
                new("memory", "toggle", "显示内存", showMemory ? "true" : "false"),
                new("ping", "button", "检查按钮响应"),
                new("count", "text", "本次模拟操作", clicks.ToString())]);
            surface.Input += input =>
            {
                if (!summaryEnabled) return;
                if (input.ControlId == "memory") showMemory = input.Value == "true";
                clicks++; surface.Apply(Document(), summaryEnabled, null, preview: true);
            };
            surface.Apply(Document(), summaryEnabled, null, preview: true);
            list.Children.Add(NativePluginCards.Create(new("sample-system-summary", "系统状态示例", "1.0.0", "原生插件",
                "演示共用组件和插件功能区。关闭后保留只读页面。", "SystemSummary", "Open-Revo 示例", summaryEnabled,
                summaryEnabled ? "预览 · 已启用" : "预览 · 已关闭，功能区保持可见"), true, value =>
                {
                    summaryEnabled = value; RenderCards(); feedback.Text = value ? "模拟插件已启用。" : "模拟插件已关闭，卡片和功能区保留。";
                }, surface));
        }
        var folder = new PaintButton("打开插件目录", () => feedback.Text = "只读预览：此按钮不打开或修改真实插件目录。");
        var refresh = new PaintButton("刷新", () => { RenderCards(); feedback.Text = "模拟刷新完成。关闭的插件保持关闭。"; });
        RenderCards(); content.Children.Add(NativePluginCards.Section(Paint.Inline(8, folder, refresh), feedback, list));
    }
}
