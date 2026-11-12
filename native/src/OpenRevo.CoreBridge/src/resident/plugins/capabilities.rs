use super::models::*;
use serde_json::{json, Value};
pub fn available(p: &str, s: &Value) -> bool {
    let usable = s["Accepting"] == true;
    let active = usable && s["Active"] == true;
    match p {
        SUMMARY | POWER | CONFIG | VIEW | BACKGROUND | NAVIGATE => usable,
        PERFORMANCE => active && s["Admission"]["capabilities"]["performance"] == true,
        FAN => active && s["Admission"]["capabilities"]["fan_boost"] == true,
        BATTERY => active && s["Admission"]["capabilities"]["battery"] == true,
        _ => false,
    }
}
pub fn theme_schema(available: bool) -> Value {
    json!({"available":available,"schemaVersion":1,"runtime":"static","surfaces":["main","mini","menu","dialog"],"modes":["light","dark"],"colorTokens":TOKENS,"colorFormat":"RRGGBB","maximumValues":64,"supportsDynamicUpdates":false})
}
pub fn catalog(s: &Value) -> Value {
    let mut entries = Vec::new();
    for (id, permission, kind, unit, values, takeover) in [
        (
            SUMMARY,
            SUMMARY,
            "state",
            "% / GiB",
            vec!["CPU", "memory", "battery", "onAc", "charging", "sampledAt"],
            false,
        ),
        (
            POWER,
            POWER,
            "state",
            "%",
            vec!["onAc", "battery", "observedAt"],
            false,
        ),
        (
            CONFIG,
            CONFIG,
            "storage",
            "bytes",
            vec!["JSON object", "16384", "atomic-save"],
            false,
        ),
        (
            VIEW,
            VIEW,
            "native-ui",
            "controls",
            vec!["text", "button", "toggle", "choice", "maximum:32"],
            false,
        ),
        (
            NAVIGATE,
            NAVIGATE,
            "navigation",
            "",
            vec!["overview", "display", "power", "devices", "settings"],
            false,
        ),
        (
            BACKGROUND,
            BACKGROUND,
            "lifecycle",
            "",
            vec!["explicit-consent", "power-notifications"],
            false,
        ),
        (
            PERFORMANCE,
            PERFORMANCE,
            "action",
            "mode",
            vec!["1", "2", "3"],
            true,
        ),
        (FAN, FAN, "action", "bool", vec!["true", "false"], true),
        (
            BATTERY,
            BATTERY,
            "action",
            "%",
            vec!["60", "80", "100"],
            true,
        ),
        (
            THEME,
            VIEW,
            "static-contribution",
            "RGB",
            vec!["main", "mini", "menu", "dialog"],
            false,
        ),
        (
            "lifecycle.startup-restore",
            BACKGROUND,
            "lifecycle",
            "",
            vec!["enabled", "matching-digest", "existing-grant"],
            false,
        ),
    ] {
        let ok = available(permission, s);
        entries.push(json!({"id":id,"kind":kind,"unit":unit,"availability":if ok{"available"}else{"unavailable"},"reason":if ok{Value::Null}else{json!("host_unavailable")},"values":values,"requiresTakeover":takeover,"firmwareSuppression":false}));
    }
    for (id, kind, reason, status) in [
        (
            "ui.theme.osd",
            "static-contribution",
            "NativeOsd有专用绘制路径，尚未接入主题颜色。",
            "not_wired",
        ),
        (
            "ui.theme.system-menu",
            "system-ui",
            "Windows拥有的菜单未提供本软件的颜色契约。",
            "not_exposed",
        ),
        (
            "ui.theme.dynamic",
            "contribution",
            "目前使用静态清单；动态颜色更新尚未开放。",
            "not_wired",
        ),
        (
            "ui.view.slider",
            "native-ui",
            "SDK尚未开放滑条类型。",
            "not_wired",
        ),
        (
            "ui.view.layout",
            "native-ui",
            "当前只提供有序行，未开放嵌套布局和图表。",
            "not_wired",
        ),
        (
            "ui.navigation.contribute",
            "navigation",
            "Native尚未登记插件导航。",
            "not_wired",
        ),
        (
            "ui.motion",
            "contribution",
            "插件动效目标和撤销路径未接入。",
            "not_wired",
        ),
        (
            "input.mapping",
            "input",
            "插件事件和重映射尚未接入。",
            "not_wired",
        ),
        (
            "control.policy",
            "policy",
            "未接入控制归属、暂停和恢复，不可执行。",
            "not_wired",
        ),
        (
            "state.hardware.extended",
            "state",
            "尚未形成带单位、时间与未知原因的插件快照。",
            "not_wired",
        ),
        (
            "state.battery.cycles",
            "state",
            "当前Core未提供真实循环次数。",
            "not_exposed",
        ),
        (
            "ui.window",
            "native-ui",
            "独立插件窗口没有接入。",
            "not_wired",
        ),
        (
            "hardware.raw",
            "unsafe",
            "不开放原始EC/Core编号、驱动句柄或任意硬件命令。",
            "not_exposed",
        ),
        (
            "process.shell",
            "unsafe",
            "不开放Shell、外部进程、任意文件路径或动态库加载。",
            "not_exposed",
        ),
        (
            "host.preferences",
            "host-management",
            "不向插件开放接管、更新、安装、数据维护、自启和主程序配置。",
            "not_exposed",
        ),
    ] {
        entries.push(json!({"id":id,"kind":kind,"unit":"","availability":status,"reason":reason,"values":[],"requiresTakeover":false,"firmwareSuppression":false}));
    }
    for id in [
        "display.brightness",
        "display.refresh",
        "lighting",
        "camera",
        "microphone",
        "windows-key",
        "keep-awake",
        "screen-off",
        "gpu-mode",
        "gpu-tuning",
        "power-plan",
        "overview-switches",
        "fan-curve",
        "cpu-tuning",
        "display-color",
    ] {
        entries.push(json!({"id":format!("control.{id}"),"kind":"action","unit":"","availability":"not_wired","reason":"现有主程序功能尚未开放插件事务契约。","values":[],"requiresTakeover":false,"firmwareSuppression":false}));
    }
    json!({"api":{"major":1,"minor":2},"entries":entries})
}
