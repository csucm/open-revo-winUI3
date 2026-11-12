using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;

internal sealed record NativePluginCardInfo(string Id, string Name, string Version, string Kind,
    string Description, string Folder, string? Author, bool Enabled, string Status);

// The same native card is used by the real manager and the independent readonly design host.
internal static class NativePluginCards
{
    internal static FrameworkElement Create(NativePluginCardInfo item, bool canToggle,
        Action<bool> toggle, FrameworkElement? content = null)
    {
        var icon = Paint.Card(Paint.Icon("plugins", 22, item.Enabled), 7, 10, inset: true);
        icon.Width = icon.Height = 36; icon.VerticalAlignment = VerticalAlignment.Center;
        var copy = Paint.Stack(4,
            Paint.Text(item.Name, 14, true),
            Paint.Text($"v{item.Version}  ·  {item.Kind}  ·  [{item.Id}]", 12, muted: true),
            Paint.Text(item.Description, 12, muted: true),
            Paint.Text((string.IsNullOrWhiteSpace(item.Author) ? "" : "作者：" + item.Author + "  ·  ") + "目录：" + item.Folder, 12, muted: true));
        // A Grid gives long names/descriptions a finite width while the switch keeps its hit target.
        var header = new Grid { ColumnSpacing = 18 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(icon); Grid.SetColumn(copy, 1); header.Children.Add(copy);
        var capsule = PaintSwitch.Create("启用“" + item.Name + "”", item.Enabled, toggle, "green");
        capsule.IsEnabled = canToggle;
        capsule.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(capsule, 2); header.Children.Add(capsule);
        // The header and saved view remain present when off. Only interactions are disabled.
        var body = Paint.Stack(12, header, Paint.Text(item.Status, 12, muted: true));
        if (content != null) body.Children.Add(content);
        return Paint.Card(body, 14, 12);
    }

    internal static FrameworkElement Section(FrameworkElement controls, FrameworkElement status, FrameworkElement list)
    {
        var title = Paint.Stack(4, Paint.Text("自定义插件", 16, true),
            Paint.Text("管理本机插件。关闭后保留插件与设置。", 12, muted: true));
        return Paint.Card(Paint.Stack(14, Paint.Between(title, controls), status, list), 20);
    }
}
