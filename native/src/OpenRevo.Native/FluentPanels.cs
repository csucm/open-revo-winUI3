using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core.Hal;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly Dictionary<int, Button> compactChargeButtons = [];
    private Button? compactBoostButton;
    private Button? compactStatus;
    private string rgbMode = "彩虹循环", rgbColor = "机械青";
    private int rgbLevel = 3;
    private static Grid Columns(params FrameworkElement[] items)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            items[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            items[i].VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(items[i], i); grid.Children.Add(items[i]);
        }
        return grid;
    }
    private Button Pending(string label, string reason)
    {
        var button = Ui.Button(label, (_, _) => { }); button.IsEnabled = false;
        button.Padding = new Thickness(mini ? 2 : 4); button.FontSize = mini ? 11 : 16;
        if (mini)
        {
            button.Height = label.Contains('\n') ? 42 : 32;
            if (button.Content is StackPanel content)
            {
                content.Spacing = 3;
                foreach (var item in content.Children.OfType<FrameworkElement>())
                {
                    if (item is TextBlock text) text.FontSize = 11;
                    else { item.Width = 16; item.Height = 16; }
                }
            }
        }
        ToolTipService.SetToolTip(button, reason); return button;
    }
    private Border Group(string glyph, string title, UIElement content, string? detail = null, string? titleKey = null, string? detailKey = null)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        label.Children.Add(titleKey=="lightingTitle"?OriginalDeviceGraphic("keyboard",mini?14:21):title=="电池健康"?OriginalDeviceGraphic("battery",mini?14:21):Ui.Icon(glyph, mini ? 14 : 21));
        var headingText = Ui.Text(title, mini ? 12 : 18, true);
        if (titleKey != null) live[titleKey] = headingText;
        if(titleKey=="gpuTitle")
        {
            gpuReadButton=Ui.Button("读取显卡状态",async(_,_)=>await RunGpuRequest(0));gpuReadButton.Content=headingText;
            gpuReadButton.Padding=new Thickness(0);gpuReadButton.MinHeight=0;gpuReadButton.MinWidth=0;
            gpuReadButton.BorderThickness=new Thickness(0);gpuReadButton.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            gpuReadButton.IsEnabled=OpenRevo.Core.Hal.EcReader.IsAllowed(identity)&&!gpuChanging;
            ToolTipService.SetToolTip(gpuReadButton,"显示本次开机最后读取的固件设置；点击刷新，需要 Windows 管理员授权");label.Children.Add(gpuReadButton);
        }
        else label.Children.Add(headingText);
        FrameworkElement info = mini && title == "电池健康" ? Value("miniLimit", 11) : Ui.Text(detail ?? "", 11, muted: true);
        if (detailKey != null && info is TextBlock block) live[detailKey] = block;
        var heading = Ui.Row(label, info);
        var stack = Ui.Stack(heading, content); stack.Spacing = mini ? 4 : 14;
        var card = Ui.Card(stack); card.Padding = new Thickness(mini ? 7 : 22); return card;
    }
    private Button MiniControl(string title, Func<object> operation, string explanation)
    {
        var button = Ui.Button(title, async (_, _) =>
        {
            if (!hardware.Active) return;
            await RunHardware(operation, "操作已回读确认。");
        });
        ToolTipService.SetToolTip(button, explanation);
        if (mini && button.Content is StackPanel content)
        {
            content.Spacing = 2;
            foreach (var child in content.Children.OfType<FrameworkElement>())
                if (child is TextBlock text) text.FontSize = 11;
                else { child.Width = 15; child.Height = 15; }
        }
        button.Padding = new Thickness(4); button.FontSize = mini ? 12 : 14; button.IsEnabled = !hardwareBusy && hardware.Active; return button;
    }
    private void RenderCompactPanel()
    {
        compactChargeButtons.Clear();
        page.Children.Add(Group("", "性能模式："+PerformanceName, PerformanceChoices(), "CPU：— · fans: — RPM", "performanceTitle", "miniCpu"));
        page.Children.Add(Group("", "显卡模式：" + gpuModeLabel, GpuChoices(), "GPU：— · fans: — RPM", "gpuTitle", "miniGpu"));
        page.Children.Add(Group("", displayState?.Active == true ? $"屏幕刷新率：{displayState.Hertz} Hz" : "屏幕刷新率：— Hz", MiniRefreshControls(), displayState?.Panel??"内置屏待识别", "refreshTitle"));
        page.Children.Add(MiniBrightnessCard());
        var lightingCard=Group("",MiniLightingTitle,LightingControls(),keyboard.Description,"lightingTitle");
        lightingCard.Padding=new Thickness(8,7,8,7);
        page.Children.Add(lightingCard);
        Button Charge(string label, int limit)
        {
            var button = MiniControl(label, () => hardware.SetBatteryPreset(limit), limit switch { 60=>"充至60%停止，低于55%恢复；不主动放电",80=>"充至80%停止，低于75%恢复；不主动放电",_=>"允许充满，适合外出使用" });
            compactChargeButtons[limit] = button; return button;
        }
        page.Children.Add(Group("", "电池健康", Columns(Charge("工作站", 60), Charge("平衡", 80), Charge("长效", 100))));
        compactBoostButton = Ui.Button("\n一键强冷", async (_, _) =>
        {
            bool target = (hardware.State?.FanBoost ?? ec.FanBoost) != true;
            if (!hardware.Active) return;
            await RunHardware(() => hardware.SetFanBoost(target), "强冷状态已回读确认。");
        });
        compactBoostButton.IsEnabled = !hardwareBusy && hardware.Active&&hardware.SupportsFanBoost;
        ToolTipService.SetToolTip(compactBoostButton,"点击提高风扇转速，再次点击恢复自动散热");
        var quickActions = Columns(compactBoostButton, QuickActionButton("camera"), QuickActionButton("mic"), QuickActionButton("win"), QuickActionButton("awake"), QuickActionButton("screen"));
        quickActions.ColumnSpacing = 4;
        foreach (var button in quickActions.Children.OfType<Button>())
        {
            button.Height = 50; button.Padding = new Thickness(1,4,1,4); button.FontSize = 12;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            if (button.Content is StackPanel content)
            {
                content.Spacing = 3;
                foreach (var text in content.Children.OfType<TextBlock>()) text.FontSize = 10;
                foreach (var icon in content.Children.OfType<FontIcon>()) icon.FontSize = 18;
            }
        }
        page.Children.Add(quickActions);
        var status = compactStatus = Ui.Button(hardware.Active ? "● 已接管" : automaticTakeoverPending ? "● 等待原版退出" : "● 未接管", (_, _) => { currentPage = "settings"; if (mini) ToggleMini(); else Render(); });
        status.Padding = new Thickness(4, 0, 4, 0); status.FontSize = 11; status.MinHeight = 22;
        status.VerticalAlignment=VerticalAlignment.Center;status.VerticalContentAlignment=VerticalAlignment.Center;
        ToolTipService.SetToolTip(status, hardware.Active ? "已连接，可以直接调整设置" : hardwareMessage);
        var model=Ui.Text(identity.Board,12,muted:true);model.VerticalAlignment=VerticalAlignment.Center;
        var footer=Ui.Row(model,status);footer.MinHeight=24;
        page.Children.Add(footer);
    }
    private void UpdateCompactValues()
    {
        int? current = hardware.State?.Upper ?? ec.BatteryLimit;
        Put("miniLimit", current is int currentLimit ? $"上限 {currentLimit}%" : "监测中");
        foreach (var (limit, button) in compactChargeButtons)
        {
            button.IsEnabled=hardware.Active&&!hardwareBusy&&hardware.SupportsBattery;
            SetChoiceVisual(button,current==limit);
            if(desktopBatteryChoices.TryGetValue(limit,out var radio))radio.Text=current==limit?"●":"○";
        }
        if (compactBoostButton != null)
        {
            compactBoostButton.IsEnabled=hardware.Active&&!hardwareBusy&&hardware.SupportsFanBoost;
            if ((hardware.State?.FanBoost ?? ec.FanBoost) == true) compactBoostButton.Style = (Style)Application.Current.Resources["SelectedChoiceStyle"];
            else compactBoostButton.ClearValue(FrameworkElement.StyleProperty);
        }
    }
    private FrameworkElement LightingControls()
    {
        return AdaptiveLightingControls();
    }
}
