//! On-demand tuning editor requests. Hardware applies use the existing session;
//! preset edits only persist drafts. No editor work or file polling when hidden.
use super::{FanPoint, HardwareSession, Outcome, Receipt, TuningProfile, TuningSnapshot};
use crate::resident::{
    gpu::Offsets,
    platform::{validate_directory, OperationLock},
};
use openrevo_core::system::{app_preferences::AppPreferences, config_io};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    io::Read,
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
};
const BUILTINS: [&str; 4] = [
    "preset_silent_beast",
    "preset_balance",
    "preset_quiet",
    "preset_extreme",
];
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Preset {
    id: String,
    name: String,
    icon: String,
    built_in: bool,
    profile: TuningProfile,
    #[serde(default)]
    adjustments: Vec<String>,
}
fn guid() -> Result<String, String> {
    Ok(format!(
        "{:?}",
        unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?
    )
    .to_ascii_lowercase())
}
fn user_id() -> Result<String, String> {
    Ok(format!("user_{}", guid()?.replace('-', "")))
}
fn name(value: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() || value.encode_utf16().count() > 12 || value.chars().any(char::is_control)
    {
        return Err("方案名需为1–12个字符".into());
    }
    Ok(value.into())
}
fn profile_file(root: &Path, id: &str) -> Result<PathBuf, String> {
    if !BUILTINS.contains(&id)
        && !(id.len() == 37
            && id.starts_with("user_")
            && id[5..]
                .bytes()
                .all(|c| c.is_ascii_digit() || matches!(c, b'a'..=b'f')))
    {
        return Err("方案标识无效".into());
    }
    Ok(root.join("profiles").join(format!("{id}.json")))
}
fn bytes(path: &Path) -> Result<Option<Vec<u8>>, String> {
    let mut file = match fs::OpenOptions::new()
        .read(true)
        .custom_flags(0x00200000)
        .open(path)
    {
        Ok(f) => f,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    let meta = file.metadata().map_err(|e| e.to_string())?;
    if !meta.is_file() || meta.file_attributes() & 0x400 != 0 || meta.len() > 1024 * 1024 {
        return Err("预设不是1MiB以内的普通文件".into());
    }
    let mut value = Vec::new();
    file.by_ref()
        .take(1024 * 1024 + 1)
        .read_to_end(&mut value)
        .map_err(|e| e.to_string())?;
    if value.len() > 1024 * 1024 {
        return Err("预设超过1MiB".into());
    }
    Ok(Some(value))
}
fn parse<T: serde::de::DeserializeOwned>(bytes: &[u8]) -> Result<T, String> {
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes);
    if bytes.starts_with(&[0xff, 0xfe]) || bytes.starts_with(&[0xfe, 0xff]) {
        if bytes.len() % 2 != 0 {
            return Err("预设编码无效".into());
        }
        let little = bytes[0] == 0xff;
        let words = bytes[2..]
            .chunks_exact(2)
            .map(|c| {
                if little {
                    u16::from_le_bytes([c[0], c[1]])
                } else {
                    u16::from_be_bytes([c[0], c[1]])
                }
            })
            .collect::<Vec<_>>();
        serde_json::from_str(&String::from_utf16(&words).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())
    } else {
        serde_json::from_slice(bytes).map_err(|e| e.to_string())
    }
}
fn default_curve() -> Vec<FanPoint> {
    [
        (40, 22),
        (50, 30),
        (60, 45),
        (68, 55),
        (75, 65),
        (82, 75),
        (88, 85),
        (95, 100),
    ]
    .into_iter()
    .map(|(celsius, percent)| FanPoint { celsius, percent })
    .collect()
}
impl TuningSnapshot {
    pub(super) fn fit_profile(&self, mut p: TuningProfile) -> TuningProfile {
        if self.cpu_tuning {
            let (a, b, c) = self.limits();
            p.pl2 = p.pl2.clamp(15, b);
            p.pl1 = p.pl1.clamp(15, a.min(p.pl2));
            p.pl4 = p.pl4.clamp(p.pl2 + 20, c);
        }
        p
    }
    pub(super) fn draft(&self, mode: u8) -> TuningProfile {
        let r = &self.registers;
        let c = &self.gpu.capabilities;
        let custom = mode == 4;
        let fans = custom && self.performance.isolated & 128 != 0;
        let curve = |offset: usize| {
            let mut points = default_curve();
            if fans {
                for point in &mut points {
                    let index = (1 + (point.celsius - 30) / 5).clamp(1, 15) as usize;
                    point.percent =
                        (i32::from(self.performance.tables[offset + index]) / 2).clamp(0, 100);
                }
                points[7].percent = 100;
            }
            points
        };
        let factory = match mode {
            1 => (r[12], r[13], r[14]),
            2 => (r[15], r[16], r[17]),
            _ => (r[8], r[9], r[10]),
        };
        let limits = if custom || !self.cpu_tuning {
            (r[0], r[1], r[2])
        } else {
            factory
        };
        let p = TuningProfile {
            pl1: i32::from(limits.0),
            pl2: i32::from(limits.1),
            pl4: i32::from(limits.2),
            temperature_enabled: custom && r[3] & 128 != 0,
            temperature_c: (if self.amd {
                i32::from(r[3] & 127)
            } else {
                self.tj_max - i32::from(r[3] & 127)
            })
            .clamp(80, 95),
            gpu_super_performance: self.performance.clamp & 16 != 0,
            gpu_boost_enabled: r[4] & 2 != 0,
            gpu_boost_watts: i32::from(r[6]).clamp(5, c.boost_max_watts.max(5)),
            gpu_base_watts: if c.configurable_base {
                (c.floor_watts + i32::from(r[7])).clamp(c.floor_watts, c.base_max_watts)
            } else {
                c.base_max_watts
            },
            offsets: self.gpu.offsets.clone(),
            custom_fans: fans,
            cpu_curve: curve(32),
            gpu_curve: curve(80),
            smooth_fans: true,
            fan_step_ms: (i32::from(self.performance.ramp & 127) * 100).clamp(100, 12700),
        };
        if custom || !self.cpu_tuning {
            p
        } else {
            self.fit_profile(p)
        }
    }
}
fn original(
    source: &Value,
    state: &TuningSnapshot,
    builtin: bool,
    mode: u8,
) -> Result<Preset, String> {
    let data = source.get("profile").unwrap_or(source);
    if !data.is_object() {
        return Err("方案参数必须为对象".into());
    }
    for key in [
        "cpu_pl1_watts",
        "cpu_pl2_watts",
        "cpu_pl4_watts",
        "cpu_fan_curve",
        "gpu_fan_curve",
    ] {
        if data.get(key).is_none() {
            return Err(format!("方案缺少参数：{key}"));
        }
    }
    let int = |key: &str, fallback: i32| -> Result<i32, String> {
        match data.get(key) {
            None => Ok(fallback),
            Some(v) => v
                .as_i64()
                .and_then(|n| i32::try_from(n).ok())
                .ok_or_else(|| format!("方案参数必须为整数：{key}")),
        }
    };
    let boolean = |key: &str, fallback: bool| -> Result<bool, String> {
        match data.get(key) {
            None => Ok(fallback),
            Some(v) => v
                .as_bool()
                .ok_or_else(|| format!("方案开关类型无效：{key}")),
        }
    };
    let mut notes = Vec::new();
    let r = &state.registers;
    let c = &state.gpu.capabilities;
    let draft = state.draft(mode);
    let id = if builtin {
        source["id"].as_str().ok_or("内置方案缺少标识")?.to_owned()
    } else {
        user_id()?
    };
    if builtin && !BUILTINS.contains(&id.as_str()) {
        return Err("未知内置方案".into());
    }
    let title = name(source["name"].as_str().unwrap_or("导入方案"))?;
    let icon = source["icon_key"].as_str().unwrap_or("star").to_owned();
    let (mut pl1, mut pl2, mut pl4) = (
        int("cpu_pl1_watts", draft.pl1)?,
        int("cpu_pl2_watts", draft.pl2)?,
        int("cpu_pl4_watts", draft.pl4)?,
    );
    if builtin && state.cpu_tuning {
        (pl1, pl2, pl4) = match id.as_str() {
            "preset_balance" => (r[15].into(), r[16].into(), r[17].into()),
            "preset_quiet" => (r[12].into(), r[13].into(), r[14].into()),
            "preset_extreme" => (r[8].into(), r[9].into(), r[10].into()),
            _ => {
                let peak = f64::from(r[8]);
                (
                    if r[8] > r[15] {
                        i32::from(r[15])
                            + ((i32::from(r[8]) - i32::from(r[15])) as f64 * 0.5).round_ties_even()
                                as i32
                    } else {
                        (peak * 0.85).round_ties_even() as i32
                    },
                    r[8].into(),
                    ((peak * 1.25).round_ties_even() as i32).min(r[10].into()),
                )
            }
        };
        notes.push("CPU参数按本机原厂功耗重新计算".into());
    }
    let mut p = TuningProfile {
        pl1,
        pl2,
        pl4,
        ..draft
    };
    if state.cpu_tuning {
        p = state.fit_profile(p);
    } else {
        notes.push("本机未开放CPU功耗和温控调校，应用时保留原值".into());
    }
    let limit = |v: i32, min: i32, max: i32| v.clamp(min, max);
    p.temperature_enabled = boolean("cpu_temp_target_enabled", true)?;
    p.temperature_c = limit(int("cpu_temp_target", 95)?, 80, 95);
    p.gpu_super_performance = boolean("gpu_super_perf", true)?;
    p.gpu_boost_enabled = boolean("gpu_db_enabled", true)?;
    if c.supported {
        p.gpu_base_watts = if c.configurable_base {
            if builtin {
                if id == "preset_quiet" {
                    (f64::from(c.base_max_watts) * 0.75).round_ties_even() as i32
                } else {
                    c.base_max_watts
                }
            } else {
                int("gpu_ctgp_watts", c.base_max_watts)?
            }
        } else {
            c.base_max_watts
        };
        p.gpu_base_watts = p.gpu_base_watts.clamp(c.floor_watts, c.base_max_watts);
        p.gpu_boost_watts = limit(
            if builtin {
                if id == "preset_quiet" {
                    10.min(c.boost_max_watts)
                } else {
                    c.boost_max_watts
                }
            } else {
                int("gpu_db_watts", c.boost_max_watts)?
            },
            5,
            c.boost_max_watts,
        );
        if state.gpu.offsets.is_some() {
            p.offsets = Some(Offsets {
                core_mhz: limit(int("gpu_core_offset_mhz", 0)?, 0, c.core_offset_max),
                memory_mhz: limit(int("gpu_mem_offset_mhz", 0)?, 0, c.memory_offset_max),
            });
        }
    }
    if !p.gpu_super_performance && p.offsets.is_some() {
        p.offsets = Some(Offsets {
            core_mhz: 0,
            memory_mhz: 0,
        });
    }
    let curve = |key: &str| -> Result<Vec<FanPoint>, String> {
        let list = data[key].as_array().ok_or("风扇曲线必须为数组")?;
        if list.len() != 8 {
            return Err("每条风扇曲线必须有8个节点".into());
        }
        let mut points = Vec::new();
        for point in list {
            points.push(FanPoint {
                celsius: point["temp"]
                    .as_i64()
                    .and_then(|n| i32::try_from(n).ok())
                    .ok_or("风扇温度无效")?,
                percent: point["duty"]
                    .as_i64()
                    .and_then(|n| i32::try_from(n).ok())
                    .ok_or("风扇占空比无效")?,
            });
        }
        if points[7].percent != 100 || points[7].celsius > 95 {
            points[7].celsius = points[7].celsius.min(95);
            points[7].percent = 100;
        }
        Ok(points)
    };
    p.custom_fans = boolean("fan_curve_enabled", false)?;
    p.cpu_curve = curve("cpu_fan_curve")?;
    p.gpu_curve = curve("gpu_fan_curve")?;
    p.smooth_fans = boolean("fan_ramp_rate", true)?;
    p.fan_step_ms = limit(
        (f64::from(int("fan_ramp_speed_ms", 300)?) / 100.0).round_ties_even() as i32 * 100,
        100,
        12700,
    );
    state.validate(&p, false)?;
    Ok(Preset {
        id,
        name: title,
        icon,
        built_in: builtin,
        profile: p,
        adjustments: notes,
    })
}
fn export(p: &Preset) -> Result<String, String> {
    let a = &p.profile;
    let curve = |list: &Vec<FanPoint>| {
        list.iter()
            .map(|p| json!({"temp":p.celsius,"duty":p.percent}))
            .collect::<Vec<_>>()
    };
    serde_json::to_string_pretty(&json!({"name":p.name,"id":p.id,"icon_key":p.icon,"is_builtin":false,"native_profile":a,"profile":{"cpu_pl1_watts":a.pl1,"cpu_pl2_watts":a.pl2,"cpu_pl4_watts":a.pl4,"cpu_temp_target_enabled":a.temperature_enabled,"cpu_temp_target":a.temperature_c,"bios_oc_enabled":false,"gpu_super_perf":a.gpu_super_performance,"gpu_ctgp_enabled":true,"gpu_ctgp_watts":a.gpu_base_watts,"gpu_db_enabled":a.gpu_boost_enabled,"gpu_db_watts":a.gpu_boost_watts,"gpu_core_offset_mhz":a.offsets.as_ref().map_or(0,|v|v.core_mhz),"gpu_mem_offset_mhz":a.offsets.as_ref().map_or(0,|v|v.memory_mhz),"fan_curve_enabled":a.custom_fans,"fan_ramp_rate":a.smooth_fans,"fan_ramp_speed_ms":a.fan_step_ms,"fan_isolated_output":true,"fan_curve_linked":false,"cpu_fan_curve":curve(&a.cpu_curve),"gpu_fan_curve":curve(&a.gpu_curve)}})).map_err(|e|e.to_string())
}
pub struct TuningEditor {
    root: PathBuf,
    baseline: Option<String>,
    classic: Option<TuningSnapshot>,
    current: Option<TuningProfile>,
    declared: Option<Value>,
    manifest: Option<String>,
    defaults: Vec<Preset>,
    presets: Vec<Preset>,
    selected: Option<String>,
}
impl TuningEditor {
    pub fn new(root: PathBuf) -> Self {
        Self {
            root,
            baseline: None,
            classic: None,
            current: None,
            declared: None,
            manifest: None,
            defaults: Vec::new(),
            presets: Vec::new(),
            selected: None,
        }
    }
    pub fn reset(&mut self) {
        *self = Self::new(self.root.clone());
    }
    fn save_settings(&self, prefs: &mut Value, key: &str, value: Value) -> Result<(), String> {
        let mut next: Value = parse(
            &AppPreferences::load_snapshot(&self.root, true).map_err(|_| "调校设置读取失败")?,
        )?;
        next[key] = value;
        AppPreferences::save_snapshot(
            &self.root,
            &serde_json::to_vec(&next).map_err(|e| e.to_string())?,
        )
        .map_err(|_| "调校设置保存失败")?;
        *prefs = next;
        Ok(())
    }
    fn select(&mut self, prefs: &mut Value, id: Option<String>) -> Result<(), String> {
        self.save_settings(prefs, "SelectedTuningPreset", json!(id))?;
        self.selected = id;
        Ok(())
    }
    fn save(&self, p: &Preset, state: &TuningSnapshot) -> Result<(), String> {
        state.validate(&p.profile, true)?;
        name(&p.name)?;
        let path = profile_file(&self.root, &p.id)?;
        validate_directory(path.parent().ok_or("无效预设目录")?)?;
        config_io::atomic_write_with_retry(
            &path,
            &serde_json::to_vec_pretty(p).map_err(|e| e.to_string())?,
        )
    }
    fn publish(&mut self, prefs: &mut Value, p: Preset) -> Result<(), String> {
        let id = p.id.clone();
        if let Some(old) = self.presets.iter_mut().find(|v| v.id == id) {
            *old = p;
        } else {
            self.presets.push(p);
        }
        self.select(prefs, Some(id))
    }
    fn find(&self, id: &str) -> Result<Preset, String> {
        self.presets
            .iter()
            .find(|p| p.id == id)
            .cloned()
            .ok_or_else(|| "方案已变更，请重新读取".into())
    }
    fn load(&mut self, state: &TuningSnapshot, mode: u8) -> Result<(), String> {
        let catalog = super::desktop::query(18)?;
        let list = catalog.as_array().ok_or("内置方案目录无效")?;
        self.defaults = list
            .iter()
            .map(|v| original(v, state, true, mode))
            .collect::<Result<Vec<_>, _>>()?;
        self.presets = self.defaults.clone();
        let dir = self.root.join("profiles");
        validate_directory(&dir)?;
        if !dir.exists() {
            return Ok(());
        }
        let mut files = fs::read_dir(&dir)
            .map_err(|e| e.to_string())?
            .filter_map(Result::ok)
            .filter(|f| f.path().extension().is_some_and(|v| v == "json"))
            .map(|f| f.path())
            .collect::<Vec<_>>();
        files.sort();
        files.truncate(100);
        let mut available = state.clone();
        available.cpu_ceilings = json!({"Pl1":255,"Pl2":255,"Pl4":255});
        for path in files {
            let item = (|| -> Result<Preset, String> {
                let b = bytes(&path)?.ok_or("方案不存在")?;
                let mut p: Preset = parse(&b)?;
                if profile_file(&self.root, &p.id)? != path {
                    return Err("方案标识与文件名不匹配".into());
                }
                p.name = name(&p.name)?;
                available.validate(&p.profile, true)?;
                p.built_in = BUILTINS.contains(&p.id.as_str());
                if p.built_in {
                    p.icon = self
                        .defaults
                        .iter()
                        .find(|v| v.id == p.id)
                        .ok_or("内置方案未就绪")?
                        .icon
                        .clone();
                }
                Ok(p)
            })();
            if let Ok(p) = item {
                if let Some(old) = self.presets.iter_mut().find(|v| v.id == p.id) {
                    *old = p;
                } else {
                    self.presets.push(p);
                }
            }
        }
        Ok(())
    }
    pub fn snapshot(&self, prefs: &Value) -> Option<Value> {
        let baseline = self.baseline.as_ref()?;
        if let (Some(caps), Some(manifest)) = (&self.declared, &self.manifest) {
            let saved = &prefs["DeclaredTuning"];
            let saved = if saved["Manifest"] == *manifest {
                let mut value = saved.clone();
                for key in ["CpuCurve", "GpuCurve"] {
                    if !value[key]
                        .as_array()
                        .is_some_and(|a| a.len() == 8 && a.iter().all(|p| p.is_object()))
                    {
                        value[key] = json!(default_curve());
                    }
                }
                Some(value)
            } else {
                None
            };
            return Some(
                json!({"Baseline":baseline,"Declared":{"Model":manifest,"Pl1Min":caps["pl1"]["min"],"Pl1Max":caps["pl1"]["max"],"Pl2Min":caps["pl2"]["min"],"Pl2Max":caps["pl2"]["max"],"FanCurve":caps["fan_curve"],"Saved":saved}}),
            );
        }
        let state = self.classic.as_ref()?;
        let caps = &state.gpu.capabilities;
        let ceiling = |index| {
            let (min, value, max) = state.ceiling_range(index);
            let mut default = state.clone();
            default.cpu_ceilings = Value::Null;
            json!({"Minimum":min,"Value":value,"Maximum":max,"Default":default.ceiling_range(index).1})
        };
        let (a, b, c) = state.limits();
        Some(
            json!({"Baseline":baseline,"Classic":{"Current":self.current,"Cpu":state.cpu_tuning.then(||json!({"Pl1Max":a,"Pl2Max":b,"Pl4Max":c,"Pl1":ceiling(0),"Pl2":ceiling(1),"Pl4":ceiling(2)})),"Gpu":{"Supported":caps.supported,"ConfigurableBase":caps.configurable_base,"FloorWatts":caps.floor_watts,"BaseMaxWatts":caps.base_max_watts,"BoostMaxWatts":caps.boost_max_watts,"CoreOffsetMax":caps.core_offset_max,"MemoryOffsetMax":caps.memory_offset_max},"Presets":self.presets.iter().map(|p|json!({"Id":p.id,"Name":p.name,"BuiltIn":p.built_in,"Profile":p.profile})).collect::<Vec<_>>(),"CanCreatePreset":self.defaults.iter().any(|p|p.id=="preset_balance"),"SelectedPreset":self.selected,"Directory":self.root.join("profiles").to_string_lossy()}}),
        )
    }
    fn declared_profile(&self, request: &Value) -> Result<Value, String> {
        let caps = self.declared.as_ref().ok_or("声明式调校能力未读取")?;
        let manifest = self.manifest.as_ref().ok_or("声明式机型未读取")?;
        let mut p = request.clone();
        let a = p["Pl1"].as_i64().ok_or("请填完功耗值")?;
        let b = p["Pl2"].as_i64().ok_or("请填完功耗值")?;
        for (n, key) in [(a, "pl1"), (b, "pl2")] {
            let min = caps[key]["min"].as_i64().ok_or("声明范围缺失")?;
            let max = caps[key]["max"].as_i64().ok_or("声明范围缺失")?;
            if !(min..=max).contains(&n) {
                return Err("功耗不在本机声明范围".into());
            }
        }
        if a > b || p["CustomFans"] == true && caps["fan_curve"] != true {
            return Err("功耗顺序或风扇能力无效".into());
        }
        if p["CustomFans"] == true {
            for key in ["CpuCurve", "GpuCurve"] {
                let points = p[key]
                    .as_array()
                    .filter(|a| a.len() == 8)
                    .ok_or("每条曲线需要8点")?;
                let mut previous = None;
                for point in points {
                    let t = point["Celsius"].as_i64().ok_or("曲线温度无效")?;
                    let duty = point["Percent"].as_i64().ok_or("曲线占空比无效")?;
                    if !(30..=100).contains(&t)
                        || !(0..=100).contains(&duty)
                        || previous.is_some_and(|(a, b)| t <= a || duty < b)
                    {
                        return Err("风扇曲线须递增并处于范围".into());
                    }
                    previous = Some((t, duty));
                }
                if previous.is_none_or(|(t, d)| t > 95 || d != 100) {
                    return Err("曲线末点须不高于95度且100%".into());
                }
            }
        }
        p["Manifest"] = json!(manifest);
        Ok(p)
    }
}
impl HardwareSession {
    pub fn tuning_editor_command(
        &mut self,
        editor: &mut TuningEditor,
        request: &Value,
        upload: Option<&[u8]>,
        prefs: &mut Value,
    ) -> Receipt {
        let action = request["Action"].as_i64().unwrap_or(0);
        if action != 1
            && !editor.baseline.as_ref().is_some_and(|id| {
                request["Baseline"]
                    .as_str()
                    .is_some_and(|v| v.eq_ignore_ascii_case(id))
            })
        {
            return Receipt::error(Outcome::Rejected, "调校读数已失效，请重新读取");
        }
        let mut exported = None;
        let mut exported_name = None;
        let mut unverified = None;
        let mut configuration = false;
        let mut apply_warning = None;
        let result = (|| -> Result<Outcome, String> {
            if action == 1 {
                editor.reset();
                self.tuning_baseline = None;
                if self.active() && self.admission["declared_tuning"].is_object() {
                    editor.declared = Some(self.admission["declared_tuning"].clone());
                    editor.manifest = self.admission["manifest"].as_str().map(str::to_owned);
                    editor.baseline = Some(guid()?);
                    return Ok(Outcome::Applied);
                }
                if !self.active() || !self.supports("custom_tuning") {
                    return Err("请先接管支持调校的设备".into());
                }
                let (id, state) = match self.probe_tuning() {
                    Ok(v) => v,
                    Err(error) if error.starts_with("工厂功耗边界无效") => {
                        let _lock = OperationLock::enter()?;
                        self.guard()?;
                        let r = [
                            super::read(1844)?,
                            super::read(1845)?,
                            super::read(1846)?,
                            super::read(1840)?,
                            super::read(1841)?,
                            super::read(1842)?,
                            super::read(1959)?,
                            super::read(1960)?,
                            super::read(1961)?,
                        ];
                        let names = super::desktop::query(18)
                            .ok()
                            .and_then(|v| {
                                v.as_array().map(|a| {
                                    a.iter()
                                        .filter_map(|p| p["name"].as_str().map(str::to_owned))
                                        .collect::<Vec<_>>()
                                })
                            })
                            .unwrap_or_default();
                        unverified = Some(
                            json!({"Baseline":"00000000-0000-0000-0000-000000000000","Unverified":{"Office":{"Pl1":r[0],"Pl2":r[1],"Pl4":r[2]},"Balance":{"Pl1":r[3],"Pl2":r[4],"Pl4":r[5]},"Turbo":{"Pl1":r[6],"Pl2":r[7],"Pl4":r[8]},"Presets":names}}),
                        );
                        return Ok(Outcome::Applied);
                    }
                    Err(error) => return Err(error),
                };
                let mode = state.performance.mode(self.state.as_ref());
                editor.current = Some(state.draft(mode));
                editor.baseline = Some(id);
                editor.classic = Some(state.clone());
                editor.selected = prefs["SelectedTuningPreset"].as_str().map(str::to_owned);
                if let Err(error) = editor.load(&state, mode) {
                    let _ = self.audit("resident.tuning.catalog-failed", json!({"error":error}));
                }
                return Ok(Outcome::Applied);
            }
            if matches!(action, 12 | 13) {
                let p = editor.declared_profile(&request["Declared"])?;
                if action == 12 {
                    configuration = true;
                    editor.save_settings(prefs, "DeclaredTuning", p)?;
                    return Ok(Outcome::ConfigurationSaved);
                }
                let receipt = self.apply_declared(&p);
                if matches!(receipt.outcome, Outcome::CommandSent) {
                    return Ok(Outcome::CommandSent);
                }
                return Err(receipt.message.unwrap_or_else(|| "声明式调校未完成".into()));
            }
            let state = editor
                .classic
                .clone()
                .filter(|_| self.active() && self.supports("custom_tuning"))
                .ok_or("当前调校通道不可用，请重新读取")?;
            if action == 2 {
                let profile: TuningProfile = serde_json::from_value(request["Profile"].clone())
                    .map_err(|e| e.to_string())?;
                let result = self.apply_tuning(
                    editor.baseline.as_deref().ok_or("缺少调校基线")?,
                    profile.clone(),
                );
                if matches!(result.outcome, Outcome::ReadbackConfirmed) {
                    let (id, after) = self.tuning_baseline.clone().ok_or("缺少调校确认基线")?;
                    editor.baseline = Some(id);
                    editor.classic = Some(after);
                    editor.current = Some(profile);
                    apply_warning = result.warning;
                    return Ok(Outcome::ReadbackConfirmed);
                }
                if matches!(result.outcome, Outcome::Unknown) {
                    editor.reset();
                }
                return Err(result.message.unwrap_or_else(|| "调校应用未完成".into()));
            }
            if action == 3 {
                if !state.cpu_tuning {
                    return Err("本机未开放CPU调校".into());
                }
                let index = request["Limit"]
                    .as_u64()
                    .filter(|v| *v <= 2)
                    .ok_or("无效功耗项")? as usize;
                let watts = request["Watts"]
                    .as_i64()
                    .and_then(|v| i32::try_from(v).ok())
                    .ok_or("无效功耗值")?;
                let (min, _, max) = state.ceiling_range(index);
                if !(min..=max).contains(&watts) {
                    return Err("软件上限超出本机范围".into());
                }
                let mut ceilings = if state.cpu_ceilings.is_object() {
                    state.cpu_ceilings.clone()
                } else {
                    json!({})
                };
                ceilings[["Pl1", "Pl2", "Pl4"][index]] = json!(watts);
                configuration = true;
                editor.save_settings(prefs, "CpuPowerCeilings", ceilings.clone())?;
                let mut next = state;
                next.cpu_ceilings = ceilings;
                let id = guid()?;
                self.tuning_baseline = Some((id.clone(), next.clone()));
                editor.baseline = Some(id);
                editor.classic = Some(next);
                return Ok(Outcome::ConfigurationSaved);
            }
            let id = request["PresetId"].as_str();
            let title = request["Name"].as_str();
            let preset_draft = |editor: &TuningEditor| -> Result<Preset, String> {
                let profile: TuningProfile = serde_json::from_value(request["Profile"].clone())
                    .map_err(|e| e.to_string())?;
                state.validate(&profile, true)?;
                if let Some(id) = id {
                    let mut p = editor.find(id)?;
                    p.profile = profile;
                    if let Some(title) = title {
                        p.name = name(title)?;
                    }
                    Ok(p)
                } else {
                    Ok(Preset {
                        id: user_id()?,
                        name: name(title.unwrap_or("新方案"))?,
                        icon: "star".into(),
                        built_in: false,
                        profile,
                        adjustments: Vec::new(),
                    })
                }
            };
            if action == 10 {
                let p = preset_draft(editor)?;
                exported = Some(export(&p)?);
                exported_name = Some(p.name);
                return Ok(Outcome::Applied);
            }
            if action == 14 {
                let path = editor.root.join("profiles");
                validate_directory(&path)?;
                fs::create_dir_all(path).map_err(|e| e.to_string())?;
                return Ok(Outcome::Applied);
            }
            match action {
                4 => {
                    let p = editor.find(id.ok_or("缺少预设标识")?)?;
                    configuration = true;
                    editor.select(prefs, Some(p.id))?;
                }
                5 => {
                    let mut p = editor
                        .defaults
                        .iter()
                        .find(|p| p.id == "preset_balance")
                        .cloned()
                        .ok_or("未取得本机均衡方案")?;
                    p.id = user_id()?;
                    p.name = name(title.ok_or("缺少方案名")?)?;
                    p.built_in = false;
                    p.icon = "star".into();
                    p.profile.temperature_enabled = state.cpu_tuning;
                    p.profile.gpu_super_performance = false;
                    p.profile.gpu_boost_enabled = state.gpu.capabilities.supported;
                    if state.gpu.capabilities.supported && state.gpu.offsets.is_some() {
                        p.profile.offsets = Some(Offsets {
                            core_mhz: 0,
                            memory_mhz: 0,
                        });
                    }
                    p.profile = state.fit_profile(p.profile);
                    state.validate(&p.profile, true)?;
                    configuration = true;
                    editor.save(&p, &state)?;
                    editor.publish(prefs, p)?;
                }
                6 | 8 => {
                    let p = preset_draft(editor)?;
                    state.validate(&p.profile, true)?;
                    configuration = true;
                    editor.save(&p, &state)?;
                    editor.publish(prefs, p)?;
                }
                7 => {
                    let mut p = editor
                        .defaults
                        .iter()
                        .find(|p| Some(p.id.as_str()) == id)
                        .cloned()
                        .ok_or("没有可恢复的内置方案")?;
                    p.profile = state.fit_profile(p.profile);
                    state.validate(&p.profile, true)?;
                    configuration = true;
                    editor.save(&p, &state)?;
                    editor.publish(prefs, p)?;
                }
                9 => {
                    let b = upload.ok_or("方案文件传输不完整")?;
                    if b.len() > 1024 * 1024 || request["Bytes"].as_u64() != Some(b.len() as u64) {
                        return Err("方案文件长度无效".into());
                    };
                    let v: Value = parse(b)?;
                    let p = if let Some(native) = v.get("native_profile") {
                        let profile: TuningProfile =
                            serde_json::from_value(native.clone()).map_err(|e| e.to_string())?;
                        state.validate(&profile, true)?;
                        Preset {
                            id: user_id()?,
                            name: name(v["name"].as_str().unwrap_or("导入方案"))?,
                            icon: "star".into(),
                            built_in: false,
                            profile,
                            adjustments: Vec::new(),
                        }
                    } else if v.get("Pl1").is_some() {
                        let profile: TuningProfile =
                            serde_json::from_value(v).map_err(|e| e.to_string())?;
                        state.validate(&profile, true)?;
                        Preset {
                            id: user_id()?,
                            name: "本机草稿".into(),
                            icon: "star".into(),
                            built_in: false,
                            profile,
                            adjustments: Vec::new(),
                        }
                    } else {
                        original(
                            &v,
                            &state,
                            false,
                            state.performance.mode(self.state.as_ref()),
                        )?
                    };
                    state.validate(&p.profile, true)?;
                    configuration = true;
                    editor.save(&p, &state)?;
                    editor.publish(prefs, p)?;
                }
                11 => {
                    let p = editor.find(id.ok_or("缺少预设标识")?)?;
                    if p.built_in || BUILTINS.contains(&p.id.as_str()) {
                        return Err("内置方案不可删除".into());
                    }
                    configuration = true;
                    if editor.selected.as_deref() == Some(&p.id) {
                        editor.select(prefs, None)?;
                    }
                    let path = profile_file(&editor.root, &p.id)?;
                    if bytes(&path)?.is_some() {
                        let trash = editor.root.join("profiles/trash");
                        validate_directory(&trash)?;
                        fs::create_dir_all(&trash).map_err(|e| e.to_string())?;
                        let target =
                            trash.join(format!("{}-{}.json", p.id, guid()?.replace('-', "")));
                        fs::rename(path, target).map_err(|e| e.to_string())?;
                    }
                    editor.presets.retain(|v| v.id != p.id);
                }
                _ => return Err("调校请求不适用于当前通道".into()),
            }
            Ok(Outcome::ConfigurationSaved)
        })();
        let mut receipt = match result {
            Ok(outcome) => Receipt::new(outcome),
            Err(error) => Receipt::error(
                if self.faulted {
                    Outcome::Unknown
                } else if configuration {
                    Outcome::ConfigurationSaveFailed
                } else {
                    Outcome::Rejected
                },
                error,
            ),
        };
        receipt.warning = apply_warning;
        let mut snapshot = unverified.or_else(|| editor.snapshot(prefs));
        if let Some(value) = snapshot.as_mut() {
            if let Some(text) = exported {
                value["Export"] = json!(text);
                value["ExportName"] = json!(exported_name);
            }
        }
        if let Some(value) = snapshot {
            receipt.payload = Some(json!({"Tuning":value}));
        }
        receipt
    }
    pub fn select_saved_custom(&mut self, editor: &mut TuningEditor, prefs: &mut Value) -> Receipt {
        let read = json!({"Action":1});
        let mut result = self.tuning_editor_command(editor, &read, None, prefs);
        if !matches!(result.outcome, Outcome::Applied) {
            return result;
        }
        if editor.declared.is_some() {
            if let Some(saved) = prefs.get("DeclaredTuning").filter(|v| {
                editor.manifest.as_deref().is_some_and(|id| v["Manifest"] == id)
            }).cloned() {
                return self.apply_declared(&saved);
            }
            result.outcome = Outcome::Rejected;
            result.message = Some("请先保存自定义调校方案".into());
            return result;
        }
        let result = (|| -> Result<(String, TuningProfile), String> {
            let state = editor.classic.as_ref().ok_or("调校通道不可用")?;
            let id = prefs["SelectedTuningPreset"]
                .as_str()
                .ok_or("请先选择已保存的自定义方案")?;
            let path = profile_file(&editor.root, id)?;
            let preset = if let Some(b) = bytes(&path)? {
                let p: Preset = parse(&b)?;
                if p.id != id {
                    return Err("方案标识不一致".into());
                }
                p
            } else {
                editor
                    .defaults
                    .iter()
                    .find(|p| p.id == id)
                    .cloned()
                    .ok_or("已保存方案不存在")?
            };
            name(&preset.name)?;
            state.validate(&preset.profile, true)?;
            Ok((
                editor.baseline.clone().ok_or("调校基线不可用")?,
                preset.profile,
            ))
        })();
        match result {
            Ok((id, profile)) => self.apply_tuning(&id, profile),
            Err(error) => Receipt::error(Outcome::Rejected, error),
        }
    }
}
