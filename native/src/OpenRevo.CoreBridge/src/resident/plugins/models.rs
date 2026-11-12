//! Existing Plugin SDK 1.2 / platform schema 1 data. No CLR types or plugin delegates.
use serde::{
    de::{self, MapAccess, SeqAccess, Visitor},
    Deserialize, Serialize,
};
use serde_json::{Map, Value};
use std::{collections::HashSet, fmt};
pub const SUMMARY: &str = "state.summary.read";
pub const POWER: &str = "state.power.read";
pub const CONFIG: &str = "config.own";
pub const VIEW: &str = "ui.view";
pub const NAVIGATE: &str = "ui.navigate";
pub const BACKGROUND: &str = "lifecycle.background";
pub const PERFORMANCE: &str = "hardware.performance.set";
pub const FAN: &str = "hardware.fanBoost.set";
pub const BATTERY: &str = "hardware.battery.limit.set";
pub const THEME: &str = "ui.theme";
pub const KNOWN: [&str; 14] = [
    SUMMARY,
    POWER,
    CONFIG,
    VIEW,
    NAVIGATE,
    BACKGROUND,
    PERFORMANCE,
    FAN,
    BATTERY,
    "ui.navigation.contribute",
    THEME,
    "ui.motion",
    "input.mapping",
    "control.policy",
];
pub const SURFACES: [&str; 5] = ["main", "mini", "osd", "menu", "dialog"];
pub const TOKENS: [&str; 8] = [
    "background",
    "surface",
    "border",
    "text",
    "muted",
    "accent",
    "tint",
    "subtle",
];
pub fn text(s: &str, min: usize, max: usize) -> bool {
    let n = s.encode_utf16().count();
    n >= min && n <= max
}
pub fn identifier(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 64
        && s.as_bytes()[0].is_ascii_alphanumeric()
        && s.bytes()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || matches!(c, b'-' | b'_'))
}
pub fn capability(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 96
        && s.bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'-' | b'_'))
}
pub fn hex(s: &str, len: usize) -> bool {
    s.len() == len && s.bytes().all(|c| c.is_ascii_hexdigit())
}
fn unique<'a>(values: impl IntoIterator<Item = &'a str>) -> bool {
    let mut seen = HashSet::new();
    values.into_iter().all(|s| seen.insert(s))
}
struct Strict(Value);
impl<'de> Deserialize<'de> for Strict {
    fn deserialize<D: de::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct V;
        impl<'de> Visitor<'de> for V {
            type Value = Strict;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("strict JSON")
            }
            fn visit_bool<E: de::Error>(self, v: bool) -> Result<Strict, E> {
                Ok(Strict(Value::Bool(v)))
            }
            fn visit_i64<E: de::Error>(self, v: i64) -> Result<Strict, E> {
                Ok(Strict(v.into()))
            }
            fn visit_u64<E: de::Error>(self, v: u64) -> Result<Strict, E> {
                Ok(Strict(v.into()))
            }
            fn visit_f64<E: de::Error>(self, v: f64) -> Result<Strict, E> {
                serde_json::Number::from_f64(v)
                    .map(|n| Strict(Value::Number(n)))
                    .ok_or_else(|| E::custom("nonfinite number"))
            }
            fn visit_str<E: de::Error>(self, v: &str) -> Result<Strict, E> {
                Ok(Strict(Value::String(v.into())))
            }
            fn visit_string<E: de::Error>(self, v: String) -> Result<Strict, E> {
                Ok(Strict(Value::String(v)))
            }
            fn visit_unit<E: de::Error>(self) -> Result<Strict, E> {
                Ok(Strict(Value::Null))
            }
            fn visit_none<E: de::Error>(self) -> Result<Strict, E> {
                Ok(Strict(Value::Null))
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut a: A) -> Result<Strict, A::Error> {
                let mut v = Vec::new();
                while let Some(item) = a.next_element::<Strict>()? {
                    v.push(item.0);
                }
                Ok(Strict(Value::Array(v)))
            }
            fn visit_map<A: MapAccess<'de>>(self, mut a: A) -> Result<Strict, A::Error> {
                let mut v = Map::new();
                while let Some(key) = a.next_key::<String>()? {
                    if v.contains_key(&key) {
                        return Err(de::Error::custom("duplicate property"));
                    }
                    v.insert(key, a.next_value::<Strict>()?.0);
                }
                Ok(Strict(Value::Object(v)))
            }
        }
        d.deserialize_any(V)
    }
}
pub fn parse<T: de::DeserializeOwned>(bytes: &[u8], maximum: usize) -> Result<T, String> {
    if bytes.len() > maximum {
        return Err("插件JSON超过边界".into());
    }
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes);
    let mut de = serde_json::Deserializer::from_slice(bytes);
    let value = Strict::deserialize(&mut de).map_err(|e| e.to_string())?.0;
    de.end().map_err(|e| e.to_string())?;
    fn depth(value: &Value, n: usize) -> bool {
        n <= 16
            && match value {
                Value::Object(v) => v.values().all(|v| depth(v, n + 1)),
                Value::Array(v) => v.iter().all(|v| depth(v, n + 1)),
                _ => true,
            }
    }
    if !depth(&value, 0) {
        return Err("插件JSON嵌套超过16层".into());
    }
    serde_json::from_value(value).map_err(|e| e.to_string())
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Manifest {
    pub api_major: i32,
    pub id: String,
    pub name: String,
    pub version: String,
    pub entry_assembly: String,
    pub entry_type: String,
    pub permissions: Vec<String>,
    pub background: bool,
    #[serde(default)]
    pub platform: Option<Platform>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub author: Option<String>,
    #[serde(default)]
    pub minimum_api_minor: i32,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Platform {
    pub schema_version: i32,
    pub runtime: String,
    pub required_capabilities: Vec<String>,
    pub optional_capabilities: Vec<String>,
    pub contributions: Contributions,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Contributions {
    pub pages: Vec<Page>,
    pub theme: Vec<Color>,
    pub motions: Vec<Motion>,
    pub keys: Vec<Key>,
    pub policies: Vec<Policy>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Page {
    pub id: String,
    pub title: String,
    pub show_in_navigation: bool,
    pub view: View,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Color {
    pub surface: String,
    pub mode: String,
    pub token: String,
    pub value: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Motion {
    pub surface: String,
    pub trigger: String,
    pub steps: Vec<Step>,
}
fn easing() -> String {
    "easeOut".into()
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Step {
    pub property: String,
    pub from: f64,
    pub to: f64,
    pub duration_ms: i32,
    #[serde(default)]
    pub delay_ms: i32,
    #[serde(default = "easing")]
    pub easing: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Key {
    pub event: String,
    pub action: Action,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Policy {
    pub id: String,
    pub resource: String,
    pub condition: String,
    pub action: Action,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Action {
    pub action: String,
    pub arguments: Value,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct View {
    pub title: String,
    pub controls: Vec<Control>,
}
fn yes() -> bool {
    true
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Control {
    pub id: String,
    pub kind: String,
    pub label: String,
    #[serde(default)]
    pub value: Option<String>,
    #[serde(default = "yes")]
    pub enabled: bool,
    #[serde(default)]
    pub choices: Option<Vec<Choice>>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Choice {
    pub value: String,
    pub label: String,
}
impl View {
    pub fn validate(&self) -> Result<(), String> {
        if !text(&self.title, 1, 120)
            || self.controls.len() > 32
            || !unique(self.controls.iter().map(|c| c.id.as_str()))
        {
            return Err("无效插件页面".into());
        }
        for c in &self.controls {
            if !identifier(&c.id)
                || !matches!(c.kind.as_str(), "text" | "button" | "toggle" | "choice")
                || !text(&c.label, 0, 160)
                || c.value.as_ref().is_some_and(|v| !text(v, 0, 1024))
            {
                return Err("无效插件控件".into());
            }
            if c.kind == "toggle" && !matches!(c.value.as_deref(), Some("true" | "false")) {
                return Err("无效开关值".into());
            }
            if c.kind == "choice" {
                let choices = c
                    .choices
                    .as_ref()
                    .filter(|v| (1..=12).contains(&v.len()))
                    .ok_or("无效选择项")?;
                if choices
                    .iter()
                    .any(|v| !identifier(&v.value) || !text(&v.label, 1, 80))
                    || !unique(choices.iter().map(|v| v.value.as_str()))
                    || !choices.iter().any(|v| Some(&v.value) == c.value.as_ref())
                {
                    return Err("无效选择项".into());
                }
            } else if c.choices.is_some() {
                return Err("非选择控件不能附带选项".into());
            }
        }
        Ok(())
    }
}
impl Action {
    pub fn permission(&self, navigate: bool) -> Option<&'static str> {
        let map = self.arguments.as_object()?;
        if map.len() != 1 {
            return None;
        }
        match self.action.as_str() {
            "performance.set"
                if map
                    .get("mode")
                    .and_then(Value::as_i64)
                    .is_some_and(|v| (1..=3).contains(&v)) =>
            {
                Some(PERFORMANCE)
            }
            "fanBoost.set" if map.get("enabled").is_some_and(Value::is_boolean) => Some(FAN),
            "battery.limit.set"
                if map
                    .get("percent")
                    .and_then(Value::as_i64)
                    .is_some_and(|v| matches!(v, 60 | 80 | 100)) =>
            {
                Some(BATTERY)
            }
            "ui.navigate"
                if navigate
                    && map.get("page").and_then(Value::as_str).is_some_and(|v| {
                        matches!(v, "overview" | "display" | "power" | "devices" | "settings")
                    }) =>
            {
                Some(NAVIGATE)
            }
            _ => None,
        }
    }
    pub fn resource(&self) -> &str {
        match self.action.as_str() {
            "performance.set" => "performance",
            "fanBoost.set" => "fanBoost",
            "battery.limit.set" => "battery.limit",
            _ => "",
        }
    }
}
impl Platform {
    pub fn validate(&self, permissions: &[String]) -> Result<(), String> {
        if self.schema_version != 1
            || !matches!(self.runtime.as_str(), "static" | "worker")
            || self.required_capabilities.len() > 64
            || self.optional_capabilities.len() > 64
            || self
                .required_capabilities
                .iter()
                .chain(&self.optional_capabilities)
                .any(|s| !capability(s))
            || !unique(
                self.required_capabilities
                    .iter()
                    .chain(&self.optional_capabilities)
                    .map(String::as_str),
            )
        {
            return Err("无效插件平台声明".into());
        }
        let v = &self.contributions;
        if v.pages.len() > 8
            || v.theme.len() > 80
            || v.motions.len() > 24
            || v.keys.len() > 16
            || v.policies.len() > 8
        {
            return Err("插件贡献超过预算".into());
        }
        let mut resources = HashSet::new();
        let mut claim = |key: String| {
            if resources.insert(key) {
                Ok(())
            } else {
                Err("插件资源重复声明".to_owned())
            }
        };
        let require = |key: &str| {
            if permissions.iter().any(|p| p == key) {
                Ok(())
            } else {
                Err(format!("插件声明缺少权限：{key}"))
            }
        };
        for p in &v.pages {
            if !identifier(&p.id) || !text(&p.title, 1, 80) {
                return Err("无效插件导航页".into());
            }
            p.view.validate()?;
            claim(format!("page.{}", p.id))?;
            require(VIEW)?;
            if p.show_in_navigation {
                require("ui.navigation.contribute")?;
            }
        }
        for c in &v.theme {
            if !SURFACES.contains(&c.surface.as_str())
                || !matches!(c.mode.as_str(), "light" | "dark")
                || !TOKENS.contains(&c.token.as_str())
                || !hex(&c.value, 6)
            {
                return Err("无效主题颜色".into());
            }
            claim(format!("theme.{}.{}.{}", c.surface, c.mode, c.token))?;
            require(THEME)?;
        }
        for motion in &v.motions {
            if !SURFACES.contains(&motion.surface.as_str())
                || !matches!(
                    motion.trigger.as_str(),
                    "enter" | "exit" | "press" | "release"
                )
                || !(1..=8).contains(&motion.steps.len())
            {
                return Err("无效插件动效".into());
            }
            claim(format!("motion.{}.{}", motion.surface, motion.trigger))?;
            let mut properties = HashSet::new();
            for s in &motion.steps {
                if !(50..=500).contains(&s.duration_ms)
                    || !(0..=250).contains(&s.delay_ms)
                    || s.delay_ms + s.duration_ms > 600
                    || !matches!(s.easing.as_str(), "linear" | "easeIn" | "easeOut")
                    || !s.from.is_finite()
                    || !s.to.is_finite()
                    || !properties.insert(&s.property)
                {
                    return Err("无效动效参数".into());
                }
                let valid = match s.property.as_str() {
                    "opacity" => (0.0..=1.0).contains(&s.from) && (0.0..=1.0).contains(&s.to),
                    "translateX" | "translateY" => s.from.abs() <= 16.0 && s.to.abs() <= 16.0,
                    "scale" => (0.96..=1.04).contains(&s.from) && (0.96..=1.04).contains(&s.to),
                    _ => false,
                };
                if !valid
                    || matches!(motion.trigger.as_str(), "enter" | "release")
                        && s.to
                            != if matches!(s.property.as_str(), "opacity" | "scale") {
                                1.0
                            } else {
                                0.0
                            }
                {
                    return Err("动效目标越界".into());
                }
            }
            require("ui.motion")?;
        }
        for key in &v.keys {
            if key.event != "input.mode-key" {
                return Err("未知按键事件".into());
            }
            let permission = key.action.permission(false).ok_or("无效按键动作")?;
            require(permission)?;
            require("input.mapping")?;
            claim(format!("key.{}", key.event))?;
        }
        for p in &v.policies {
            if !identifier(&p.id)
                || !matches!(p.condition.as_str(), "always" | "on-ac" | "on-battery")
                || p.resource.is_empty()
                || p.resource != p.action.resource()
            {
                return Err("无效策略声明".into());
            }
            let permission = p.action.permission(false).ok_or("无效策略动作")?;
            require(permission)?;
            require("control.policy")?;
            claim(format!("policy-id.{}", p.id))?;
            claim(format!("policy.{}", p.resource))?;
        }
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Approval {
    pub version: i32,
    pub plugin_id: String,
    pub package_digest: String,
    pub permissions: Vec<String>,
}
impl Approval {
    pub fn validate(&self) -> bool {
        self.version == 1
            && identifier(&self.plugin_id)
            && hex(&self.package_digest, 64)
            && self.permissions.len() <= 16
            && self.permissions.iter().all(|p| KNOWN.contains(&p.as_str()))
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Record {
    pub id: String,
    pub digest: String,
    pub enabled: bool,
    pub fault: Option<String>,
    pub paused_policies: Vec<String>,
    pub theme_edits: Vec<Color>,
    pub key_edits: Vec<Key>,
    #[serde(default)]
    pub active_run: Option<String>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Navigation {
    pub id: String,
    pub visible: bool,
    pub order: i32,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PendingRestore {
    pub plugin_id: String,
    pub resource: String,
    pub baseline: Action,
    pub reason: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Document {
    pub version: i32,
    pub plugins: Vec<Record>,
    pub navigation: Vec<Navigation>,
    pub pending_restores: Vec<PendingRestore>,
}
impl Default for Document {
    fn default() -> Self {
        Self {
            version: 1,
            plugins: Vec::new(),
            navigation: Vec::new(),
            pending_restores: Vec::new(),
        }
    }
}
impl Document {
    pub fn validate(&self) -> bool {
        self.version == 1
            && self.plugins.len() <= 128
            && self.navigation.len() <= 1032
            && self.pending_restores.len() <= 384
            && unique(self.plugins.iter().map(|p| p.id.as_str()))
            && unique(self.navigation.iter().map(|p| p.id.as_str()))
            && self.plugins.iter().all(|p| {
                identifier(&p.id)
                    && hex(&p.digest, 64)
                    && p.fault.as_ref().is_none_or(|v| text(v, 0, 512))
                    && p.active_run.as_ref().is_none_or(|v| text(v, 0, 64))
                    && p.paused_policies.len() <= 8
                    && p.paused_policies.iter().all(|v| identifier(v))
                    && p.theme_edits.len() <= 80
                    && p.key_edits.len() <= 16
            })
            && self
                .navigation
                .iter()
                .all(|p| text(&p.id, 1, 160) && (0..=1032).contains(&p.order))
            && self.pending_restores.iter().all(|p| {
                identifier(&p.plugin_id)
                    && text(&p.reason, 1, 512)
                    && p.baseline.permission(false).is_some()
                    && p.resource == p.baseline.resource()
            })
    }
}
