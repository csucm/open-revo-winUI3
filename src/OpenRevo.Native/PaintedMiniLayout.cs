using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private MiniLayoutRead? miniLayoutRead;
    private MiniLayout MiniSaved => (miniLayoutRead ??= MiniLayout.Load()).Layout;
    private static string MiniName(string id) => id switch
    {
        "performance" => "性能模式", "gpu" => "显卡模式", "refresh" => "屏幕刷新率", "brightness" => "屏幕亮度",
        "keyboard" => "键盘背光", "battery" => "电池健康", "touchpad" => "触摸板", "win" => "Win 键锁",
        "fn" => "F1–F12 模式", "camera" => "相机", "mic" => "麦克风", "osd" => "OSD 提示",
        "boost" => "一键强冷", "awake" => "防休眠", "screen" => "关闭屏幕", _ => id
    };
    private static string MiniIcon(string id) => id switch
    { "performance" => "performance", "refresh" => "display", "win" => "win-lock", "fn" => "fn-lock", "boost" => "fan", "screen" => "display", _ => id };

    // Availability for the catalogue is deliberately independent of action readiness.
    // Only an identified, ready declaration can exclude a hardware feature.
    private bool MiniSupported(string id)
    {
        if (uiReviewMode) return true;
        if (residentClient != null) return ResidentMiniSupported(id);
        if (id == "keyboard") return keyboard.Kind != "None";
        if (id is not ("performance" or "battery" or "boost" or "fn" or "touchpad")) return true;
        if (!hardware.CoreDelegated || !CoreMachine.Current.Ready) return true;
        return id switch
        {
            "performance" => hardware.SupportsPerformance, "battery" => hardware.SupportsBattery,
            "boost" => hardware.SupportsFanBoost, "fn" => hardware.SupportsDeclaredFnLock,
            "touchpad" => hardware.SupportsTouchpad, _ => true
        };
    }
    private string MiniAvailability(string id)
    {
        if (!MiniSupported(id)) return "当前机型不支持 · 布局记录保留";
        if (MiniLayout.QuickIds.Contains(id)) return PaintQuickEnabled(id) ? "" : "暂不可用或状态未知 · 位置保留";
        return id switch
        {
            "performance" or "battery" when !PanelHardwareActive => "未接管硬件 · 位置保留",
            "gpu" when !gpuReadFresh && !PaintIntegratedOnly => "显卡状态未知 · 位置保留",
            "refresh" or "brightness" when displayState?.Active != true => "内置屏暂不可用 · 位置保留",
            "refresh" when displayState?.Rates.Length is not > 0 => "刷新率档位未知 · 位置保留",
            "brightness" when !screenBrightness.HasValue => "亮度状态未知 · 位置保留",
            "battery" when PanelBatteryLimit == null => "充电上限未知 · 位置保留",
            "keyboard" when keyboard.Kind == "White" => CurrentMonochromeLevel.HasValue ? "" : MonochromeStatus,
            "keyboard" when !lightingReadFresh => "背光状态未知 · 位置保留", _ => ""
        };
    }
    private FrameworkElement MiniGroup(string icon, Func<string> title, Func<string> meta, FrameworkElement content)
    {
        var heading = Paint.Between(Paint.Inline(6, Paint.Icon(icon, 24, true, true), PT(title, 12, true)), PT(meta, 12, muted: true), 7);
        return Paint.Card(Paint.Stack(4, heading, content), 7, 12);
    }
    private PaintButton MiniButton(string name, Func<Task> action, bool readOnly, bool hardwareAction = true) =>
        readOnly ? new PaintButton(name) : PB(name, action, hardwareAction);

    private FrameworkElement PaintMiniTile(string id, bool readOnly = false)
    {
        switch (id)
        {
            case "performance":
                return MiniGroup("performance", () => "性能模式：" + PerformanceName,
                    () => "CPU " + (temperature.Cpu?.ToString() ?? "—") + "°C · fans " + (ec.CpuFan?.ToString() ?? "—") + " RPM", PaintModes(true, readOnly: readOnly));
            case "gpu":
                return MiniGroup("gpu", () => PaintIntegratedOnly ? "显卡模式：集成显卡" : "显卡模式：" + gpuModeLabel,
                    () => PaintIntegratedOnly ? "" : "GPU " + (temperature.Gpu?.ToString() ?? "—") + "°C · fans " + (ec.GpuFan?.ToString() ?? "—") + " RPM", PaintModes(true, true, readOnly));
            case "refresh":
                var rates = new List<FrameworkElement>();
                var low = MiniButton("拔电低刷", async () =>
                {
                    if(residentClient!=null){await ToggleResidentLowRefreshAsync();return;}
                    lowRefreshOnBattery = !lowRefreshOnBattery; await SavePreferences();
                    if (!uiReviewMode) await CheckAutomaticRefresh(true);
                    ConfirmedActionOsd("拔电自动低刷 · " + (lowRefreshOnBattery ? "已开启" : "已关闭"), "refresh");
                }, readOnly, false);
                low.Padding = new(3); low.MinHeight = 30; rates.Add(low); paintBindings.Add(() =>
                {
                    low.Selected = lowRefreshOnBattery;
                    if (residentClient != null) low.IsEnabled = !PanelActionBusy(PanelOperation.Desktop, typeof(PanelSetLowRefresh));
                });
                foreach (bool high in new[] { false, true })
                {
                    int hz = 0;
                    string fallback = high ? "最高刷新率" : "最低刷新率";
                    var label = Paint.Text(fallback);
                    var b = MiniButton(fallback, () => ChangeRefresh(hz), readOnly);
                    b.Content = label;
                    b.MinHeight = 30; b.Padding = new(3);
                    paintBindings.Add(() =>
                    {
                        hz = displayState?.Rates is { Length: > 0 } values ? (high ? values.Max() : values.Min()) : 0;
                        string text = hz > 0 ? $"{hz} Hz" : fallback;
                        if (label.Text != text) { label.Text = text; AutomationProperties.SetName(b, text); }
                        b.IsEnabled = hz > 0 && displayState?.Active == true && !displayBusy;
                        b.Selected = displayState?.Hertz == hz;
                    });
                    rates.Add(b);
                }
                return MiniGroup("display", () => "屏幕刷新率：" + (DisplayedRefreshHertz?.ToString() ?? "—") + " Hz", () => DisplayedRefreshPanel ?? "内置屏待识别", PaintColumns(rates.ToArray()));
            case "brightness":
                var presets = Paint.Inline(4);
                foreach (int n in new[] { 25, 60, 100 })
                {
                    string label = n == 25 ? "暗光25%" : n == 60 ? "室内60%" : "户外100%";
                    var b = MiniButton(label, () => SetCompactBrightness(n), readOnly); b.MinHeight = 23; b.Padding = new(4, 1, 4, 1); b.Content = Paint.Text(label, 12);
                    paintBindings.Add(() => b.IsEnabled = displayState?.Active == true && screenBrightness.HasValue && !displayBusy); presets.Children.Add(b);
                }
                var brightness = new PaintSlider("屏幕亮度", 10, 100, screenBrightness ?? 100, 1, readOnly ? null : async v => { if (!uiReviewMode) await SetCompactBrightness((int)v, coalesce: true); });
                paintBindings.Add(() => { brightness.IsEnabled = displayState?.Active == true && screenBrightness.HasValue && !displayBusy; brightness.Value = DisplayedScreenBrightness ?? 100; });
                return Paint.Card(Paint.Stack(0, Paint.Between(Paint.Inline(6, Paint.Icon("brightness", 24), PT(() => "屏幕亮度：" + (DisplayedScreenBrightness?.ToString() ?? "—") + "%", 12, true)), presets, 4), brightness), 7, 12);
            case "keyboard": return PaintMiniLighting(readOnly);
            case "battery":
                var charge = new List<FrameworkElement>();
                foreach (var (name, cap) in new[] { ("工作站", 60), ("平衡", 80), ("长效", 100) })
                {
                    var b = MiniButton(name, () => SetChargeLimit(cap), readOnly);
                    b.MinHeight = 30; b.Padding = new(4); b.Content = Paint.Text(name, 12);
                    paintBindings.Add(() =>
                    {
                        b.IsEnabled = PanelHardwareActive && PanelSupportsBattery && !PanelHardwareActionBusy(PanelOperation.SetBatteryLimit);
                        b.Selected = PanelBatteryLimit == cap;
                    });
                    charge.Add(b);
                }
                return MiniGroup("battery", () => "电池健康", () => PanelBatteryLimit is int n ? "上限 " + n + "%" : "上限 —", PaintColumns(charge.ToArray()));
            default: throw new ArgumentOutOfRangeException(nameof(id));
        }
    }
    private void RenderPaintedMini()
    {
        page.Spacing = 6; page.Padding = new(12, 4, 12, 6);
        foreach (string id in MiniSaved.Top)
        {
            var tile = PaintMiniTile(id);
            paintBindings.Add(() => tile.Visibility = MiniSupported(id) ? Visibility.Visible : Visibility.Collapsed);
            paintBindings.Add(() => ToolTipService.SetToolTip(tile, MiniAvailability(id)));
            page.Children.Add(tile);
        }
        var shortcuts = MiniSaved.Quick.Select(id =>
        {
            // A fixed cell survives exclusion. The saved ID is never removed by a failed read.
            var cell = new Grid { MinHeight = 50 };
            if (id != null)
            {
                var button = PaintQuickRow(id, true); cell.Children.Add(button);
                paintBindings.Add(() => button.Visibility = MiniSupported(id) ? Visibility.Visible : Visibility.Collapsed);
            }
            return (FrameworkElement)cell;
        }).ToArray();
        page.Children.Add(PaintColumns(shortcuts, 4));
        var status = PB("硬件接管状态", () => { PaintNavigate("settings"); if (mini) ToggleMini(); return Task.CompletedTask; });
        status.MinHeight = 22; status.Padding = new(6, 1, 6, 1); status.CornerRadius = new(5);
        status.Content = PT(() => PanelHardwareActive ? "● 已接管" : "● 未接管", 12, muted: true);
        var footer = Paint.Between(PT(() => identity.Board == "未知" ? "机型待读取" : identity.Board, 12, muted: true), status);
        footer.Margin = new(0, 12, 0, 0); page.Children.Add(footer);
    }
}
