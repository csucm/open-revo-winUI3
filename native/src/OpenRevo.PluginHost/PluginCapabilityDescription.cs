using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

// Describes the capabilities the selected host exposes without sampling hardware.
public static class PluginCapabilityDescription
{
    public static PluginThemeSchema ThemeSchema(bool available) => new(available, 1, "static",
        PluginThemePalette.SupportedSurfaces.ToArray(), ["light", "dark"], PlatformValidation.ColorTokens.ToArray(), "RRGGBB",
        PluginThemePalette.SupportedSurfaces.Length * 2 * PlatformValidation.ColorTokens.Length, false);

    public static PluginCapabilityInfo[] Build(PluginCapability[] permissions, bool restoreApprovedAtStartup = false)
    {
        var entries = new List<PluginCapabilityInfo>();
        void Ready(string id, string permission, string kind, string unit, string[] values, bool takeover = false)
        {
            var capability = permissions.FirstOrDefault(c => c.Id == permission);
            bool available = capability?.Available == true;
            entries.Add(new(id, kind, unit, available ? "available" : "unavailable",
                available ? null : capability?.UnavailableReason ?? "host_unavailable", values, takeover));
        }
        void Missing(string id, string kind, string reason, string availability = "not_wired")
            => entries.Add(new(id, kind, "", availability, reason, []));
        Ready(PluginPermissions.SummaryRead, PluginPermissions.SummaryRead, "state", "% / GiB", ["CPU", "memory", "battery", "onAc", "charging", "sampledAt"]);
        Ready(PluginPermissions.PowerRead, PluginPermissions.PowerRead, "state", "%", ["onAc", "battery", "observedAt"]);
        Ready(PluginPermissions.Config, PluginPermissions.Config, "storage", "bytes", ["JSON object", "16384", "atomic-save"]);
        Ready(PluginPermissions.View, PluginPermissions.View, "native-ui", "controls", ["text", "button", "toggle", "choice", "maximum:32"]);
        Ready(PluginPermissions.Navigate, PluginPermissions.Navigate, "navigation", "", ["overview", "display", "power", "devices", "settings"]);
        Ready(PluginPermissions.Background, PluginPermissions.Background, "lifecycle", "", ["explicit-consent", "power-notifications"]);
        Ready(PluginPermissions.Performance, PluginPermissions.Performance, "action", "mode", ["1", "2", "3"], true);
        Ready(PluginPermissions.FanBoost, PluginPermissions.FanBoost, "action", "bool", ["true", "false"], true);
        Ready(PluginPermissions.BatteryLimit, PluginPermissions.BatteryLimit, "action", "%", ["60", "80", "100"], true);
        Ready(PluginPlatformPermissions.Theme, PluginPermissions.View, "static-contribution", "RGB", PluginThemePalette.SupportedSurfaces.ToArray());
        Missing("ui.theme.osd", "static-contribution", "NativeOsd有专用绘制路径，尚未接入主题颜色；现有四表面主题可继续使用。");
        Missing("ui.theme.system-menu", "system-ui", "Windows拥有的菜单未提供本软件的颜色契约。", "not_exposed");
        Missing("ui.theme.dynamic", "contribution", "目前使用静态清单；worker动态颜色更新尚未开放。");
        Missing("ui.view.slider", "native-ui", "已有PaintSlider，但插件视图校验和SDK尚未开放滑条类型。");
        Missing("ui.view.layout", "native-ui", "已有原生组合布局；插件当前只提供有序行，未开放嵌套布局和图表。");
        Missing("ui.navigation.contribute", "navigation", "已有PluginPage数据契约，Native尚未登记插件导航。");
        Missing("ui.motion", "contribution", "原生动效存在，但插件动效目标和撤销路径未接入。");
        Missing("input.mapping", "input", "classic实体性能键被动事件存在，插件事件和重映射尚未接入。");
        Missing("control.policy", "policy", "已有在制声明，Native未接入控制归属、暂停和恢复，不可执行。");
        if (restoreApprovedAtStartup)
            Ready("lifecycle.startup-restore", PluginPermissions.Background, "lifecycle", "", ["enabled", "matching-digest", "existing-grant"]);
        else
            Missing("lifecycle.startup-restore", "lifecycle", "管理页保留开关设置；正常启用插件的启动恢复尚未接入。");
        Missing("state.hardware.extended", "state", "原生已有GPU/风扇/温度等状态，尚未形成带单位、时间与未知原因的插件快照。");
        Missing("state.battery.cycles", "state", "当前Core状态未提供真实循环次数；不能推断机型不支持，也不补造数值。", "not_exposed");
        foreach (var (id, reason) in new[]
        {
            ("display.brightness", "已有ScreenBrightness及切屏刷新；插件缺少选屏、读值和写入契约。"),
            ("display.refresh", "已有DisplayControl；插件缺少模式范围及超时确认/恢复契约。"),
            ("lighting", "已有RGB/白色背光及灯区功能；插件缺少按机型过滤的颜色/档位契约。"),
            ("camera", "已有相机事务；插件尚未提供显式目标、Windows设备选择与回执。"),
            ("microphone", "已有默认麦克风控制；插件尚未提供授权与显式目标接口。"),
            ("windows-key", "已有软件锁；插件尚未提供会话归属和退出恢复。"),
            ("keep-awake", "已有SessionPower；插件尚未提供租约及异常恢复。"),
            ("screen-off", "已有熄屏与输入唤醒；插件尚未提供显式控制入口。"),
            ("gpu-mode", "已有切换及重启交互；插件尚未提供机型、忙碌和确认契约。"),
            ("gpu-tuning", "已有GPU功耗和频率调校；插件未开放按物理设备限制的调校事务。"),
            ("power-plan", "已有Windows方案读写及恢复；插件未开放带预期状态和失败回退的事务。"),
            ("overview-switches", "已有Wi-Fi/蓝牙/触控板/Fn锁状态和比较写入；插件尚未提供逐项契约。"),
            ("fan-curve", "已有曲线编辑及HAL校验；插件未开放曲线事务。"),
            ("cpu-tuning", "已有按机型声明的调校；插件未开放范围、预检和恢复事务。"),
            ("display-color", "已有屏幕调色及矩阵回读；插件尚未提供声明和撤销契约。")
        }) Missing("control." + id, "action", reason);
        Missing("ui.window", "native-ui", "独立插件窗口没有接入，本次窗口配色接口不表示提供独立窗口。");
        Missing("hardware.raw", "unsafe", "不开放原始EC/Core编号、驱动句柄或任意硬件命令。", "not_exposed");
        Missing("process.shell", "unsafe", "不开放Shell、外部进程、任意文件路径或动态库加载。", "not_exposed");
        Missing("host.preferences", "host-management", "不向插件开放接管、更新/安装、重置数据、自启和主程序基础配置。", "not_exposed");
        return entries.ToArray();
    }
}
