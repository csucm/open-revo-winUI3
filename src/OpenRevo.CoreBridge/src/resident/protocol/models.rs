use serde::{Deserialize, Deserializer};
use std::collections::HashSet;
pub(super) trait Valid {
    fn valid(&self) -> bool;
}
fn text_length(value: &str) -> usize {
    value.encode_utf16().count()
}
fn bounded_text(value: &str, min: usize, max: usize) -> bool {
    let n = text_length(value);
    (min..=max).contains(&n)
}
fn unique<T: Eq + std::hash::Hash>(items: impl IntoIterator<Item = T>) -> bool {
    let mut seen = HashSet::new();
    items.into_iter().all(|v| seen.insert(v))
}
fn optional<T: Valid>(value: &Option<T>) -> bool {
    value.as_ref().is_none_or(Valid::valid)
}
fn required_option<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
#[serde(try_from = "String")]
pub(super) struct Guid(String);
impl Default for Guid {
    fn default() -> Self {
        Self("00000000-0000-0000-0000-000000000000".into())
    }
}
impl TryFrom<String> for Guid {
    type Error = &'static str;
    fn try_from(value: String) -> Result<Self, Self::Error> {
        if value.len() != 36
            || !value.bytes().enumerate().all(|(i, b)| {
                if [8, 13, 18, 23].contains(&i) {
                    b == b'-'
                } else {
                    b.is_ascii_hexdigit()
                }
            })
        {
            return Err("Invalid GUID");
        }
        Ok(Self(value.to_ascii_lowercase()))
    }
}
impl Guid {
    pub fn empty(&self) -> bool {
        self == &Self::default()
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct OsdAppearance {
    theme: String,
    scale: i32,
    position: String,
    motion: String,
}
impl Valid for OsdAppearance {
    fn valid(&self) -> bool {
        matches!(self.theme.as_str(), "cinema" | "fade" | "pill")
            && matches!(self.scale, 80 | 100 | 120 | 140)
            && matches!(
                self.position.as_str(),
                "top-left" | "bottom-center" | "center"
            )
            && matches!(self.motion.as_str(), "fast" | "normal" | "slow" | "off")
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct MiniLayout {
    top: Vec<String>,
    quick: Vec<Option<String>>,
}
impl Valid for MiniLayout {
    fn valid(&self) -> bool {
        self.top.len() <= 6
            && self.quick.len() == 6
            && self.top.iter().all(|s| {
                matches!(
                    s.as_str(),
                    "performance" | "gpu" | "refresh" | "brightness" | "keyboard" | "battery"
                )
            })
            && self.quick.iter().flatten().all(|s| {
                matches!(
                    s.as_str(),
                    "touchpad"
                        | "win"
                        | "fn"
                        | "camera"
                        | "mic"
                        | "osd"
                        | "boost"
                        | "awake"
                        | "screen"
                )
            })
            && unique(self.top.iter())
            && unique(self.quick.iter().flatten())
    }
}
fn overview(items: &[String]) -> bool {
    items.len() <= 7
        && items.iter().all(|s| {
            matches!(
                s.as_str(),
                "cpu" | "gpu" | "vram" | "fan" | "performance" | "battery" | "display"
            )
        })
        && unique(items.iter())
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct LayoutEdit {
    overview: Option<Vec<String>>,
    mini: Option<MiniLayout>,
    #[serde(default)]
    replace_unrecognized: bool,
}
impl Valid for LayoutEdit {
    fn valid(&self) -> bool {
        match &self.overview {
            None => self.mini.as_ref().is_some_and(Valid::valid),
            Some(items) => self.mini.is_none() && !self.replace_unrecognized && overview(items),
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Preference {
    kind: i32,
    number: Option<i32>,
    text: Option<String>,
    enabled: Option<bool>,
    #[serde(default)]
    confirmed: bool,
}
impl Valid for Preference {
    fn valid(&self) -> bool {
        match self.kind {
            1 => {
                matches!(self.number, Some(0..=2))
                    && self.text.is_none()
                    && self.enabled.is_none()
                    && !self.confirmed
            }
            2 => {
                matches!(self.number, Some(2 | 5 | 10))
                    && self.text.is_none()
                    && self.enabled.is_none()
                    && !self.confirmed
            }
            3 => {
                matches!(self.text.as_deref(), Some("remember" | "mini" | "full"))
                    && self.number.is_none()
                    && self.enabled.is_none()
                    && !self.confirmed
            }
            4 | 6 => {
                self.enabled.is_some()
                    && self.number.is_none()
                    && self.text.is_none()
                    && !self.confirmed
            }
            5 => {
                self.enabled.is_some()
                    && self.number.is_none()
                    && self.text.is_none()
                    && self.enabled == Some(self.confirmed)
            }
            _ => false,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Rgb {
    rgb: u32,
    level: i32,
    #[serde(rename = "Rainbow")]
    _rainbow: bool,
}
impl Valid for Rgb {
    fn valid(&self) -> bool {
        self.rgb <= 0xffffff && (0..=4).contains(&self.level)
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Zone {
    zone: String,
    #[serde(rename = "Enabled")]
    _enabled: bool,
    effect: i32,
    level: i32,
    speed: i32,
    rgb: u32,
}
impl Valid for Zone {
    fn valid(&self) -> bool {
        matches!(self.zone.as_str(), "front" | "hinge" | "logo")
            && (0..=4).contains(&self.level)
            && (0..=4).contains(&self.speed)
            && matches!(self.effect, 1 | 2 | 3 | 4 | 6 | 9 | 10 | 20)
            && self.rgb <= 0xffffff
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct ColorValues {
    temperature: Option<i32>,
    saturation: Option<f64>,
    contrast: Option<f64>,
    gamma: Option<f64>,
    red: Option<f64>,
    green: Option<f64>,
    blue: Option<f64>,
}
impl Valid for ColorValues {
    fn valid(&self) -> bool {
        self.temperature.is_none_or(|v| (4000..=9000).contains(&v))
            && self.saturation.is_none_or(|v| (0.0..=2.0).contains(&v))
            && self.contrast.is_none_or(|v| (0.5..=1.5).contains(&v))
            && self.gamma.is_none_or(|v| (0.7..=1.3).contains(&v))
            && [self.red, self.green, self.blue]
                .iter()
                .all(|v| v.is_none_or(|n| (0.5..=1.5).contains(&n)))
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Color {
    preset_id: String,
    #[serde(rename = "Enabled")]
    _enabled: bool,
    #[serde(default)]
    factory: bool,
    values: Option<ColorValues>,
}
impl Valid for Color {
    fn valid(&self) -> bool {
        bounded_text(&self.preset_id, 1, 100)
            && self
                .values
                .as_ref()
                .is_none_or(|v| !self.factory && v.valid())
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Icc {
    device: String,
    profile: String,
}
impl Valid for Icc {
    fn valid(&self) -> bool {
        bounded_text(&self.device, 1, 128) && bounded_text(&self.profile, 1, 512)
    }
}
#[derive(Deserialize)]
#[serde(tag = "Command", rename_all_fields = "PascalCase", deny_unknown_fields)]
pub(super) enum Desktop {
    #[serde(rename = "read")]
    Read { group: i32 },
    #[serde(rename = "keyboard-rgb")]
    KeyboardRgb(Rgb),
    #[serde(rename = "keyboard-white")]
    Monochrome { level: i32 },
    #[serde(rename = "keyboard-undo")]
    Undo {},
    #[serde(rename = "zone")]
    Zone(Zone),
    #[serde(rename = "color")]
    Color(Color),
    #[serde(rename = "icc-select")]
    Icc(Icc),
    #[serde(rename = "icc-import")]
    Import { file_name: String, bytes: i32 },
    #[serde(rename = "power-policy")]
    Power { key: String, enabled: bool },
    #[serde(rename = "power-plan")]
    Plan { scheme: Guid, expected: Guid },
    #[serde(rename = "power-recovery")]
    Recover { confirmed: bool },
    #[serde(rename = "display-auto-refresh")]
    AutomaticRefresh { enabled: bool },
    #[serde(rename = "display-off")]
    ScreenOff {},
}
impl Valid for Desktop {
    fn valid(&self) -> bool {
        match self {
            Self::Read { group } => (1..=3).contains(group),
            Self::KeyboardRgb(value) => value.valid(),
            Self::Monochrome { level } => (0..=255).contains(level),
            Self::Undo {} | Self::ScreenOff {} => true,
            Self::AutomaticRefresh { enabled } => {
                let _ = enabled;
                true
            }
            Self::Zone(value) => value.valid(),
            Self::Color(value) => value.valid(),
            Self::Icc(value) => value.valid(),
            Self::Import { file_name, bytes } => {
                (1..=4 * 1024 * 1024).contains(bytes)
                    && bounded_text(file_name, 5, 260)
                    && !file_name
                        .chars()
                        .any(|c| matches!(c, '/' | '\\' | ':') || c.is_control())
                    && [".icc", ".icm"]
                        .iter()
                        .any(|suffix| file_name.to_ascii_lowercase().ends_with(suffix))
            }
            Self::Power { key, enabled } => {
                let _ = enabled;
                matches!(
                    key.as_str(),
                    "office"
                        | "link"
                        | "master"
                        | "usb"
                        | "recovery"
                        | "disconnect_net"
                        | "block_away"
                        | "ignore_sysreq"
                        | "disable_wake_timers"
                        | "deep_sleep"
                        | "hibernate_30m"
                )
            }
            Self::Plan { scheme, expected } => !scheme.empty() && !expected.empty(),
            Self::Recover { confirmed } => *confirmed,
        }
    }
}
fn identifier(value: &str) -> bool {
    (1..=64).contains(&value.len())
        && value.as_bytes()[0].is_ascii_alphanumeric()
        && value
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || matches!(b, b'-' | b'_'))
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct PluginKey {
    id: String,
    digest: String,
}
impl Valid for PluginKey {
    fn valid(&self) -> bool {
        identifier(&self.id)
            && self.digest.len() == 64
            && self.digest.bytes().all(|b| b.is_ascii_hexdigit())
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Plugin {
    operation: i32,
    target: Option<PluginKey>,
    grants: Option<Vec<String>>,
    control_id: Option<String>,
    value: Option<String>,
}
impl Valid for Plugin {
    fn valid(&self) -> bool {
        (if self.operation == 6 {
            self.control_id.as_ref().is_some_and(|v| identifier(v))
                && self.value.as_ref().is_none_or(|v| identifier(v))
        } else {
            self.control_id.is_none() && self.value.is_none()
        }) && self.grants.as_ref().is_none_or(|grants| {
            self.operation == 2
                && grants.len() <= 16
                && grants.iter().all(|s| {
                    (1..=64).contains(&s.len())
                        && s.bytes()
                            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-'))
                })
                && unique(grants.iter())
        }) && match self.operation {
            1 | 7 | 8 | 9 | 10 => self.target.is_none(),
            2..=6 => self.target.as_ref().is_some_and(Valid::valid),
            _ => false,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Maintenance {
    action: i32,
    destination: Option<String>,
    #[serde(default)]
    confirmed: bool,
    compact: Option<bool>,
    log_level: Option<i32>,
    log_filter: Option<String>,
}
impl Valid for Maintenance {
    fn valid(&self) -> bool {
        if self.action == 8 {
            return self.destination.is_none()
                && !self.confirmed
                && self.compact.is_none()
                && matches!(self.log_level, Some(0..=2))
                && matches!(
                    self.log_filter.as_deref(),
                    Some("" | "cooler" | "custom" | "light" | "power" | "ec" | "device" | "ui")
                );
        }
        if self.log_level.is_some() || self.log_filter.is_some() {
            return false;
        }
        match self.action {
            1 | 2 | 3 | 4 | 7 => {
                self.destination.is_none() && !self.confirmed && self.compact.is_none()
            }
            5 => {
                self.confirmed
                    && self.compact.is_some()
                    && self.destination.as_ref().is_some_and(|s| {
                        bounded_text(s, 1, 1024) && !s.chars().any(char::is_control)
                    })
            }
            6 => self.confirmed && self.destination.is_none() && self.compact.is_none(),
            _ => false,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Application {
    action: i32,
    enabled: Option<bool>,
}
impl Valid for Application {
    fn valid(&self) -> bool {
        (1..=7).contains(&self.action) && (self.action == 6) == self.enabled.is_some()
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct FanPoint {
    celsius: i32,
    percent: i32,
}
fn curve_shape(curve: &[FanPoint]) -> bool {
    curve.len() == 8
        && curve
            .iter()
            .all(|p| (30..=100).contains(&p.celsius) && (0..=100).contains(&p.percent))
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct ClockOffsets {
    #[serde(rename = "CoreMhz")]
    _core_mhz: i32,
    #[serde(rename = "MemoryMhz")]
    _memory_mhz: i32,
}
// Numeric device limits are intentionally enforced at the hardware command
// boundary. Wire-valid editor drafts only require the original curve shape.
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct Profile {
    #[serde(rename = "Pl1")]
    _pl1: i32,
    #[serde(rename = "Pl2")]
    _pl2: i32,
    #[serde(rename = "Pl4")]
    _pl4: i32,
    #[serde(rename = "TemperatureEnabled")]
    _temperature_enabled: bool,
    #[serde(rename = "TemperatureC")]
    _temperature_c: i32,
    #[serde(rename = "GpuSuperPerformance")]
    _gpu_super_performance: bool,
    #[serde(rename = "GpuBoostEnabled")]
    _gpu_boost_enabled: bool,
    #[serde(rename = "GpuBoostWatts")]
    _gpu_boost_watts: i32,
    #[serde(rename = "GpuBaseWatts")]
    _gpu_base_watts: i32,
    #[serde(rename = "Offsets", deserialize_with = "required_option")]
    _offsets: Option<ClockOffsets>,
    #[serde(rename = "CustomFans")]
    _custom_fans: bool,
    cpu_curve: Vec<FanPoint>,
    gpu_curve: Vec<FanPoint>,
    #[serde(rename = "SmoothFans")]
    _smooth_fans: bool,
    #[serde(rename = "FanStepMs")]
    _fan_step_ms: i32,
}
impl Valid for Profile {
    fn valid(&self) -> bool {
        curve_shape(&self.cpu_curve) && curve_shape(&self.gpu_curve)
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct DeclaredProfile {
    #[serde(deserialize_with = "required_option")]
    pl1: Option<i32>,
    #[serde(deserialize_with = "required_option")]
    pl2: Option<i32>,
    temperature: i32,
    #[serde(rename = "CustomFans")]
    _custom_fans: bool,
    cpu_curve: Vec<FanPoint>,
    gpu_curve: Vec<FanPoint>,
    #[serde(rename = "RestoreOnAc")]
    _restore_on_ac: bool,
}
impl Valid for DeclaredProfile {
    fn valid(&self) -> bool {
        self.pl1.is_none_or(|v| v >= 0)
            && self.pl2.is_none_or(|v| v >= 0)
            && matches!(self.temperature, 0 | 70..=100)
            && curve_shape(&self.cpu_curve)
            && curve_shape(&self.gpu_curve)
    }
}
fn preset_id(value: &Option<String>) -> bool {
    value.as_ref().is_none_or(|s| {
        (1..=64).contains(&s.len()) && s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
    })
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Tuning {
    action: i32,
    #[serde(default)]
    baseline: Guid,
    preset_id: Option<String>,
    name: Option<String>,
    profile: Option<Profile>,
    declared: Option<DeclaredProfile>,
    limit: Option<i32>,
    watts: Option<i32>,
    bytes: Option<i32>,
}
impl Valid for Tuning {
    fn valid(&self) -> bool {
        if (self.action == 1) != self.baseline.empty()
            || !preset_id(&self.preset_id)
            || self
                .name
                .as_ref()
                .is_some_and(|s| !bounded_text(s, 1, 12) || s.chars().any(char::is_control))
            || !optional(&self.profile)
            || !optional(&self.declared)
            || self.limit.is_some() != self.watts.is_some()
            || self.limit.is_some() != (self.action == 3)
            || self.bytes.is_some() != (self.action == 9)
        {
            return false;
        }
        let (id, name, profile, declared) = (
            self.preset_id.is_some(),
            self.name.is_some(),
            self.profile.is_some(),
            self.declared.is_some(),
        );
        match self.action {
            1 | 14 => !id && !name && !profile && !declared,
            2 => !id && !name && profile && !declared,
            3 => {
                matches!(self.limit, Some(0..=2))
                    && matches!(self.watts, Some(1..=255))
                    && !id
                    && !name
                    && !profile
                    && !declared
            }
            4 | 7 | 11 => id && !name && !profile && !declared,
            5 => !id && name && !profile && !declared,
            6 => (!id) == name && profile && !declared,
            8 => id && name && profile && !declared,
            9 => !id && !name && !profile && !declared && matches!(self.bytes, Some(1..=1048576)),
            10 => !name && profile && !declared,
            12 | 13 => !id && !name && !profile && declared,
            _ => false,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct PresetEdit {
    #[serde(deserialize_with = "required_option")]
    preset_id: Option<String>,
    profile: Profile,
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct TuningDraft {
    #[serde(deserialize_with = "required_option")]
    selected_preset: Option<String>,
    classic: Vec<PresetEdit>,
    #[serde(deserialize_with = "required_option")]
    declared: Option<DeclaredProfile>,
    #[serde(deserialize_with = "required_option")]
    declared_model: Option<String>,
    #[serde(rename = "Open")]
    _open: bool,
    #[serde(rename = "HasClassicSelection")]
    _has_classic_selection: bool,
}
impl Valid for TuningDraft {
    fn valid(&self) -> bool {
        preset_id(&self.selected_preset)
            && self
                .classic
                .iter()
                .all(|p| preset_id(&p.preset_id) && p.profile.valid())
            && unique(self.classic.iter().map(|p| &p.preset_id))
            && optional(&self.declared)
            && if self.declared.is_none() {
                self.declared_model.is_none()
            } else {
                self.declared_model
                    .as_ref()
                    .is_some_and(|s| bounded_text(s, 1, 256))
            }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct ViewDraft {
    page: String,
    lighting_zone: String,
    tuning_return_page: String,
    #[serde(rename = "TuningReturnMini")]
    _tuning_return_mini: bool,
}
impl Valid for ViewDraft {
    fn valid(&self) -> bool {
        matches!(
            self.page.as_str(),
            "overview"
                | "display"
                | "power"
                | "devices"
                | "plugins"
                | "settings"
                | "minicustomization"
                | "sleep"
                | "lightingzone"
                | "tuning"
                | "color"
                | "osdsettings"
                | "storage"
                | "diagnostics"
                | "updates"
        ) && matches!(
            self.lighting_zone.as_str(),
            "keyboard" | "front" | "hinge" | "logo"
        ) && matches!(self.tuning_return_page.as_str(), "overview" | "display")
    }
}
#[derive(Deserialize, Default)]
#[serde(default, rename_all = "PascalCase", deny_unknown_fields)]
pub(super) struct Draft {
    #[serde(rename = "Compact")]
    _compact: bool,
    battery_limit: Option<i32>,
    keyboard: Option<Rgb>,
    zones: Option<Vec<Zone>>,
    color: Option<Color>,
    brightness: Option<i32>,
    refresh: Option<i32>,
    monochrome: Option<i32>,
    icc: Option<Icc>,
    power_plan: Option<Guid>,
    overview_draft: Option<Vec<String>>,
    mini_draft: Option<MiniLayout>,
    replace_mini_layout: bool,
    tuning: Option<TuningDraft>,
    view: Option<ViewDraft>,
}
impl Valid for Draft {
    fn valid(&self) -> bool {
        matches!(self.battery_limit, None | Some(60 | 80 | 100))
            && optional(&self.keyboard)
            && self.zones.as_ref().is_none_or(|zones| {
                zones.len() <= 3
                    && zones.iter().all(Valid::valid)
                    && unique(zones.iter().map(|z| &z.zone))
            })
            && self
                .color
                .as_ref()
                .is_none_or(|color| color.valid() && !color.factory)
            && self.brightness.is_none_or(|v| (10..=100).contains(&v))
            && self.refresh.is_none_or(|v| v > 1)
            && self.monochrome.is_none_or(|v| (0..=255).contains(&v))
            && optional(&self.icc)
            && self.power_plan.as_ref().is_none_or(|id| !id.empty())
            && self
                .overview_draft
                .as_ref()
                .is_none_or(|items| overview(items))
            && if self.mini_draft.is_none() {
                !self.replace_mini_layout && optional(&self.tuning) && optional(&self.view)
            } else {
                optional(&self.mini_draft) && optional(&self.tuning) && optional(&self.view)
            }
    }
}
