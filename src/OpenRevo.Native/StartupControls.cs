using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private CheckBox StartupCheckBox()
    {
        var box = new CheckBox { Content = "开机自启", FontSize = 12, MinWidth = 0 };
        try { box.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception error) { box.IsEnabled = false; ToolTipService.SetToolTip(box, error.Message); }
        box.Click += async (_, _) =>
        {
            try { StartupRegistration.SetEnabled(box.IsChecked == true); }
            catch (Exception error) { box.IsChecked = !box.IsChecked; await ShowMessage("开机启动设置失败", error.Message); }
        };
        return box;
    }
}
