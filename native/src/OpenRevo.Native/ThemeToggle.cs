using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Button? themeToggle;
    private void UpdateThemeToggle()
    {
        if (themeToggle == null) return;
        bool dark = root.RequestedTheme == ElementTheme.Dark || root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        themeToggle.Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = dark ? "\uE706" : "\uE708", FontSize = 12 };
        string label = dark ? "切换浅色模式" : "切换深色模式";
        AutomationProperties.SetName(themeToggle, label);
        ToolTipService.SetToolTip(themeToggle, label);
    }
}
