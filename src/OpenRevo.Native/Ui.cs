using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace OpenRevo.Native;

internal static class Ui
{
    public static TextBlock Text(string text,double size=14,bool strong=false,bool muted=false)
    {
        var block=muted?(TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{ThemeResource OverviewMuted}'/>"):new TextBlock();
        block.Text=text;block.FontSize=Design.FontSize(size);block.LineHeight=block.FontSize==24?30:block.FontSize*1.5;block.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;block.FontFamily=new FontFamily("Segoe UI");block.TextWrapping=TextWrapping.Wrap;block.FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal;return block;
    }
    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    public static Border Card(UIElement child) => new() { Style = (Style)Application.Current.Resources["CardStyle"], Child = child };
    public static Button Button(string label, RoutedEventHandler action, bool accent = false)
    {
        var button = new Button { Content = label, FontSize=14, FontFamily = new FontFamily("Segoe UI"), Padding = new Thickness(12, 6, 12, 6), HorizontalContentAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, VerticalContentAlignment=VerticalAlignment.Center };
        Design.AttachPressMotion(button);
        var parts = label.Split('\n', 2);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, parts.Length == 2 ? parts[1] : label);
        if (parts.Length == 2 && parts[0].Any(c => c is >= '\uE000' and <= '\uF8FF'))
        {
            var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Icon(parts[0], 20));
            content.Children.Add(Text(parts[1], 12)); button.Content = content;
        }
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += action;
        ToolTipService.SetToolTip(button, label.Replace("\n", " ").Trim());
        return button;
    }
    public static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var row = new Grid { ColumnSpacing = 20 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        left.VerticalAlignment=right.VerticalAlignment=VerticalAlignment.Center;
        row.Children.Add(left); Grid.SetColumn(right, 1); row.Children.Add(right);
        return row;
    }
    public static Border Setting(string title, string detail, FrameworkElement control) => Card(Row(Stack(Text(title, 15, true), Text(detail, 12, muted: true)), control));
    public static InfoBar Notice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) => new()
    { IsOpen = true, IsClosable = false, Title = title, Message = message, Severity = severity };
    public static FrameworkElement Icon(string glyph, double size,bool accent=false)
    {
        if(ApprovedIcons.Glyph(glyph) is string approved)return ApprovedIcons.Create(approved,size);
        string? geometry = glyph switch
        {
            "\uE70F" => "M12,2 L20,5 L20,11 C20,16 17,20 12,22 C7,20 4,16 4,11 L4,5 Z",
            "\uE740" => "M5,5 L19,5 L19,19 L5,19 Z",
            "\uE73F" => "M3,10 L10,10 L10,3 M10,10 L3,3 M14,21 L14,14 L21,14 M14,14 L21,21",
            "\uEC4A" => "M13,2 C15,8 8,8 11,13 C7,12 7,9 7,9 C2,14 5,22 12,22 C20,22 23,13 16,8 C17,13 13,14 13,14 C15,9 10,8 13,2 Z",
            "\uE713" => "M5,2 L5,7 M5,11 L5,22 M2,7 L8,7 L8,11 L2,11 Z M12,2 L12,14 M12,18 L12,22 M9,14 L15,14 L15,18 L9,18 Z M19,2 L19,5 M19,9 L19,22 M16,5 L22,5 L22,9 L16,9 Z",
            "\uE945" => "M13,2 L4,14 L11,14 L10,22 L21,10 L14,10 Z",
            "\uE950" => "M6,6 L18,6 L18,18 L6,18 Z M9,9 L15,9 L15,15 L9,15 Z M8,2 L8,6 M12,2 L12,6 M16,2 L16,6 M8,18 L8,22 M12,18 L12,22 M16,18 L16,22 M2,8 L6,8 M2,12 L6,12 M2,16 L6,16 M18,8 L22,8 M18,12 L22,12 M18,16 L22,16",
            "\uE7F4" => "M3,4 L21,4 L21,17 L3,17 Z M8,21 L16,21 M12,17 L12,21",
            "\uE714" => "M7,2 L12,6 L17,2 M3,6 L21,6 L21,21 L3,21 Z",
            "\uE895" => "M3,7 L20,7 L16,3 M21,17 L4,17 L8,21",
            "\uE722" => "M3,7 L8,7 L10,4 L15,4 L17,7 L21,7 L21,20 L3,20 Z M16,13 A4,4 0 1 1 8,13 A4,4 0 1 1 16,13 M2,2 L22,22",
            "\uE706" => "M3,7 L8,7 L10,4 L15,4 L17,7 L21,7 L21,20 L3,20 Z M16,13 A4,4 0 1 1 8,13 A4,4 0 1 1 16,13",
            "\uE720" => "M9,5 A3,3 0 0 1 15,5 L15,12 A3,3 0 0 1 9,12 Z M5,11 L5,12 A7,7 0 0 0 19,12 L19,11 M12,19 L12,23 M8,23 L16,23",
            "\uE72E" => "M7,10 L7,7 A5,5 0 0 1 17,7 L17,10 M4,10 L20,10 L20,22 L4,22 Z M12,14 L12,18",
            "\uE708" => "M3,8 L17,8 L17,16 A4,4 0 0 1 13,20 L7,20 A4,4 0 0 1 3,16 Z M17,9 L20,9 A3,3 0 0 1 20,15 L17,15 M2,23 L20,23 M6,2 L6,5 M11,1 L11,5",
            "\uE728" => "M3,4 L21,4 L21,17 L3,17 Z M8,21 L16,21 M12,17 L12,21 M2,2 L22,22",
            "\uE8F1" => "M2,7 L19,7 L19,18 L2,18 Z M19,10 L22,10 L22,15 L19,15",
            "\uE765" => "M2,5 L22,5 L22,20 L2,20 Z M5,9 L6,9 M9,9 L10,9 M13,9 L14,9 M17,9 L18,9 M5,13 L6,13 M9,13 L10,13 M13,13 L14,13 M17,13 L18,13 M6,17 L18,17",
            _ => null
        };
        string iconStroke=accent?"{ThemeResource ChoiceText}":"{ThemeResource TextFillColorPrimaryBrush}";
        string path(string data, int angle = 0) => $"<Path Data='{data}' Stroke='{iconStroke}' StrokeThickness='1.6' StrokeLineJoin='Round'><Path.RenderTransform><RotateTransform Angle='{angle}' CenterX='12' CenterY='12'/></Path.RenderTransform></Path>";
        if (glyph == "\uE9D9")
        {
            const string blade = "M11,10 C7,9 5,7 6,4 C7,1 11,1 13,4 C14,6 14,8 13,10 Z";
            geometry = string.Concat(new[] { 0, 90, 180, 270 }.Select(angle => path(blade, angle))) + $"<Ellipse Width='4' Height='4' Canvas.Left='10' Canvas.Top='10' Stroke='{iconStroke}' StrokeThickness='1.6'/>";
        }
        else if (geometry != null) geometry = path(geometry);
        if (geometry == null) return new Viewbox { Width = size, Height = size, Child = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = glyph, FontSize = 24 } };
        var icon = (FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Canvas Width='24' Height='24'>{geometry}</Canvas></Viewbox>");
        icon.Width = size; icon.Height = size; return icon;
    }
}
