//! GPU firmware staging and current-boot runtime state. No automatic mode writes.
//! Preserves the managed GpuCommandService/BootState checks and existing executors.
use super::{HardwareSession, Outcome, Receipt};
use crate::resident::platform::{unix_ms, OperationLock};
use openrevo_core::system::{app_preferences::AppPreferences, config_io};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    io::Read,
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
};
use windows::Win32::System::SystemInformation::GetTickCount64;
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Boot {
    boot_unix: i64,
    initial_mode: i32,
    firmware_mode: i32,
    #[serde(default)]
    legacy_pending: bool,
}
#[derive(Clone, Default)]
struct Runtime {
    supported: Option<bool>,
    igpu: Option<bool>,
}
impl Runtime {
    fn decode(raw: Option<u64>) -> Option<bool> {
        match raw.map(|v| v & 255) {
            Some(0x55) => Some(true),
            Some(0xaa) => Some(false),
            _ => None,
        }
    }
    fn read() -> Self {
        use openrevo_core::driver::wmi_mulong::mulong_query_raw;
        Self {
            supported: Self::decode(mulong_query_raw(0x30000000003)),
            igpu: Self::decode(mulong_query_raw(0x30000000002)),
        }
    }
}
fn bounded(path: &Path, max: u64) -> Result<Option<Vec<u8>>, String> {
    let mut file = match fs::OpenOptions::new()
        .read(true)
        .custom_flags(0x00200000)
        .open(path)
    {
        Ok(file) => file,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    let meta = file.metadata().map_err(|e| e.to_string())?;
    if !meta.is_file() || meta.file_attributes() & 0x400 != 0 || meta.len() > max {
        return Err("显卡状态文件不是有界普通文件".into());
    }
    let mut bytes = Vec::new();
    file.by_ref()
        .take(max + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > max {
        return Err("显卡状态文件超过边界".into());
    }
    Ok(Some(bytes))
}
fn boot_unix() -> i64 {
    (unix_ms() / 1000) as i64 - unsafe { GetTickCount64() as i64 } / 1000
}
fn firmware() -> Result<Value, String> {
    let mut bytes = vec![0u8; 32768];
    let count = unsafe { crate::or_native_query(4, 0, bytes.as_mut_ptr(), bytes.len()) };
    if count <= 1 || count as usize > bytes.len() {
        return Err("显卡固件状态未读取".into());
    }
    let v: Value =
        serde_json::from_slice(&bytes[..count as usize - 1]).map_err(|e| e.to_string())?;
    let mode = match v["hardware"]["current_mode"].as_str() {
        Some("igpu") => 1,
        Some("hybrid") => 2,
        Some("dgpu") => 3,
        _ => 0,
    };
    Ok(
        json!({"Mode":mode,"HardwareRead":v["hardware"]["hardware_read"]==true,"Integrated":v["capabilities"]["supports_igpu_only"]==true,"Hybrid":v["capabilities"]["supports_hybrid"]==true,"Discrete":v["capabilities"]["supports_dgpu"]==true}),
    )
}
fn known(f: &Value) -> bool {
    f["HardwareRead"] == true && matches!(f["Mode"].as_i64(), Some(1..=3))
}
fn supports(f: &Value, mode: i32) -> bool {
    known(f)
        && f[match mode {
            1 => "Integrated",
            2 => "Hybrid",
            3 => "Discrete",
            _ => return false,
        }] == true
}
fn device_key() -> String {
    serde_json::to_string(
        &[
            "SystemManufacturer",
            "SystemProductName",
            "BaseBoardProduct",
            "BIOSVersion",
        ]
        .map(|key| crate::resident::platform::bios_string(key).unwrap_or_else(|| "未知".into())),
    )
    .unwrap_or_default()
}
fn same_device(a: &str, b: &str) -> bool {
    serde_json::from_str::<Vec<String>>(a)
        .ok()
        .zip(serde_json::from_str::<Vec<String>>(b).ok())
        .is_some_and(|(a, b)| a == b)
}
fn iso_unix(text: &str) -> Option<i64> {
    use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
    #[link(name = "kernel32")]
    extern "system" {
        fn SystemTimeToFileTime(system: *const SYSTEMTIME, file: *mut FILETIME) -> i32;
    }
    if text.len() < 19 || !text.is_ascii() {
        return None;
    }
    let number = |a, b| text.get(a..b)?.parse::<u16>().ok();
    let system = SYSTEMTIME {
        wYear: number(0, 4)?,
        wMonth: number(5, 7)?,
        wDay: number(8, 10)?,
        wHour: number(11, 13)?,
        wMinute: number(14, 16)?,
        wSecond: number(17, 19)?,
        ..Default::default()
    };
    let mut file = FILETIME::default();
    if unsafe { SystemTimeToFileTime(&system, &mut file) } == 0 {
        return None;
    }
    let ticks = (u64::from(file.dwHighDateTime) << 32) | u64::from(file.dwLowDateTime);
    let mut unix = i64::try_from(ticks / 10_000_000)
        .ok()?
        .checked_sub(11644473600)?;
    let tail = &text[19..];
    if tail.ends_with('Z') {
        return Some(unix);
    }
    let offset = tail.rfind(['+', '-'])?;
    let zone = &tail[offset..];
    if zone.len() != 6 || zone.as_bytes()[3] != b':' {
        return None;
    }
    let hours = zone[1..3].parse::<i64>().ok()?;
    let minutes = zone[4..6].parse::<i64>().ok()?;
    if hours > 14 || minutes >= 60 {
        return None;
    }
    let delta = hours * 3600 + minutes * 60;
    unix = if zone.starts_with('+') {
        unix.checked_sub(delta)?
    } else {
        unix.checked_add(delta)?
    };
    Some(unix)
}
fn recover_initial(root: &Path, current: i32, boot: i64) -> i32 {
    let result = (|| -> Option<i32> {
        let bytes = bounded(&root.join("hardware-audit.jsonl"), 4 * 1024 * 1024).ok()??;
        let text = std::str::from_utf8(&bytes).ok()?;
        let (mut initial, mut last, mut pending) = (0, 0, 0);
        for line in text.lines() {
            let v: Value = serde_json::from_str(line).ok()?;
            let op = v["operation"].as_str().unwrap_or("");
            if !matches!(op, "gpu-next-boot-intent" | "gpu-next-boot-confirmed") {
                continue;
            }
            let at = v["atUnixMs"]
                .as_i64()
                .map(|v| v / 1000)
                .or_else(|| v["at"].as_str().and_then(iso_unix))
                .or_else(|| v["state"]["atUnixMs"].as_i64().map(|v| v / 1000))?;
            if at < boot - 5 {
                continue;
            }
            let data = if v["state"].is_object() {
                &v["state"]
            } else {
                &v
            };
            if op == "gpu-next-boot-intent" {
                let before = data["before"]["Mode"].as_i64()? as i32;
                let target = data["target"].as_i64()? as i32;
                if pending != 0
                    || !(1..=3).contains(&before)
                    || !(1..=3).contains(&target)
                    || before == target
                    || last != 0 && last != before
                {
                    return None;
                }
                if initial == 0 {
                    initial = before;
                }
                last = before;
                pending = target;
            } else {
                let r = &data["receipt"];
                if pending == 0
                    || r["Before"]["Mode"] != last
                    || r["After"]["Mode"] != pending
                    || r["Changed"] != true
                {
                    return None;
                }
                last = pending;
                pending = 0;
            }
        }
        (pending == 0 && last == current).then_some(initial)
    })();
    result.unwrap_or(0)
}
pub struct GpuControl {
    root: PathBuf,
    firmware: Value,
    boot: Option<Boot>,
    runtime: Runtime,
    fresh: bool,
    declared: bool,
}
impl GpuControl {
    pub fn new(root: PathBuf) -> Self {
        Self {
            root,
            firmware: Value::Null,
            boot: None,
            runtime: Runtime::default(),
            fresh: false,
            declared: false,
        }
    }
    fn runtime_mode(&self) -> i32 {
        let initial = self.boot.as_ref().map_or(0, |b| b.initial_mode);
        if initial == 3 {
            3
        } else if self.runtime.supported == Some(true) {
            self.runtime
                .igpu
                .map_or(initial, |on| if on { 1 } else { 2 })
        } else {
            initial
        }
    }
    pub fn snapshot(&self) -> Value {
        let runtime = self.runtime_mode();
        let mode = self.firmware["Mode"].as_i64().unwrap_or(0);
        let pending = if (1..=3).contains(&runtime) && known(&self.firmware) {
            i64::from(runtime) != mode
        } else {
            self.boot
                .as_ref()
                .is_some_and(|b| b.initial_mode == 0 && b.legacy_pending)
        };
        json!({"Fresh":self.fresh,"Declared":self.declared,"FirmwareMode":mode,"RuntimeMode":runtime,"RestartPending":pending,"Integrated":self.firmware["Integrated"]==true,"Hybrid":self.firmware["Hybrid"]==true,"Discrete":self.firmware["Discrete"]==true})
    }
    fn save_boot(&self) -> Result<(), String> {
        if let Some(boot) = &self.boot {
            config_io::atomic_write_with_retry(
                &self.root.join("gpu-boot-state.json"),
                &serde_json::to_vec(boot).map_err(|e| e.to_string())?,
            )
        } else {
            Ok(())
        }
    }
    fn open_boot(&self, mode: i32, prefs: &Value) -> Boot {
        let now = boot_unix();
        if let Ok(Some(bytes)) = bounded(&self.root.join("gpu-boot-state.json"), 4096) {
            if let Ok(mut boot) = serde_json::from_slice::<Boot>(&bytes) {
                if (0..=3).contains(&boot.initial_mode) && boot.boot_unix.abs_diff(now) <= 5 {
                    boot.firmware_mode = mode;
                    return boot;
                }
            }
        }
        let legacy = matches!(prefs["RequestedGpu"].as_i64(), Some(1..=3))
            && prefs["GpuSavedAtUnix"]
                .as_i64()
                .is_some_and(|at| at >= now - 5);
        Boot {
            boot_unix: now,
            initial_mode: if legacy {
                recover_initial(&self.root, mode, now)
            } else {
                mode
            },
            firmware_mode: mode,
            legacy_pending: legacy,
        }
    }
    fn persist(&self, prefs: &mut Value) -> Result<(), String> {
        if self.declared || !self.fresh || self.boot.is_none() || !known(&self.firmware) {
            return Ok(());
        }
        self.save_boot()?;
        // The cache is display-only, bound to this machine and boot. Never admit a write from it.
        let cache = json!({"Device":device_key(),"BootUnix":boot_unix(),"CapturedUnix":(unix_ms()/1000) as i64,"State":self.firmware});
        let _ = config_io::atomic_write_with_retry(
            &self.root.join("gpu-read-cache.json"),
            &serde_json::to_vec(&cache).map_err(|e| e.to_string())?,
        );
        let pending = if self.snapshot()["RestartPending"] == true {
            self.firmware["Mode"].as_i64().unwrap_or(0)
        } else {
            0
        };
        let mut saved: Value = serde_json::from_slice(
            &AppPreferences::load_snapshot(&self.root, true).map_err(|_| "显卡设置读取失败")?,
        )
        .map_err(|e| e.to_string())?;
        let at = if pending == 0 {
            0
        } else if saved["RequestedGpu"] == pending
            && saved["GpuSavedAtUnix"].as_i64().is_some_and(|at| at > 0)
        {
            saved["GpuSavedAtUnix"].as_i64().unwrap()
        } else {
            (unix_ms() / 1000) as i64
        };
        saved["RequestedGpu"] = json!(pending);
        saved["GpuSavedAtUnix"] = json!(at);
        AppPreferences::save_snapshot(
            &self.root,
            &serde_json::to_vec(&saved).map_err(|e| e.to_string())?,
        )
        .map_err(|_| "显卡状态已改变，但设置保存失败")?;
        *prefs = saved;
        Ok(())
    }
    pub fn refresh(&mut self, hardware: &HardwareSession, prefs: &mut Value) -> Receipt {
        let result = (|| -> Result<(), String> {
            let f = firmware()?;
            self.declared = !hardware.classic()
                && hardware.admission["manifest"]
                    .as_str()
                    .is_some_and(|s| !s.is_empty());
            if !known(&f) {
                self.fresh = false;
                if let Ok(Some(bytes)) = bounded(&self.root.join("gpu-read-cache.json"), 4096) {
                    if let Ok(cache) = serde_json::from_slice::<Value>(&bytes) {
                        let now = (unix_ms() / 1000) as i64;
                        if cache["Device"]
                            .as_str()
                            .is_some_and(|k| same_device(k, &device_key()))
                            && cache["BootUnix"]
                                .as_i64()
                                .is_some_and(|b| b.abs_diff(boot_unix()) <= 5)
                            && cache["CapturedUnix"]
                                .as_i64()
                                .is_some_and(|at| at >= boot_unix() && at <= now + 5)
                            && known(&cache["State"])
                        {
                            self.firmware = cache["State"].clone();
                        }
                    }
                }
                return Err("显卡固件模式未知；显示缓存不能用于切换".into());
            }
            let mode = f["Mode"].as_i64().unwrap() as i32;
            self.firmware = f;
            self.fresh = true;
            if self.declared {
                self.boot = None;
                self.runtime = Runtime::default();
                return Ok(());
            }
            let mut boot = self
                .boot
                .take()
                .unwrap_or_else(|| self.open_boot(mode, prefs));
            boot.firmware_mode = mode;
            self.boot = Some(boot);
            self.runtime = Runtime::read();
            self.persist(prefs)
        })();
        match result {
            Ok(()) => Receipt::new(Outcome::ReadbackConfirmed),
            Err(error) => {
                self.fresh = false;
                self.runtime = Runtime::default();
                Receipt::error(Outcome::Unknown, error)
            }
        }
    }
}
impl HardwareSession {
    pub fn select_gpu(
        &mut self,
        control: &mut GpuControl,
        target: i32,
        expected: i32,
        prefs: &mut Value,
    ) -> Receipt {
        let mut attempted = false;
        let result = (|| -> Result<Outcome, String> {
            let _lock = OperationLock::enter()?;
            if !(1..=3).contains(&target)
                || !(1..=3).contains(&expected)
                || !control.fresh
                || !known(&control.firmware)
                || control.firmware["Mode"] != expected
                || !supports(&control.firmware, target)
                || !self.active()
                || control.declared && target == 1
            {
                return Err("当前显卡状态、所选模式或硬件接管已变化，请重新读取".into());
            }
            self.guard()?;
            let before = firmware()?;
            if !known(&before) || before["Mode"] != expected || !supports(&before, target) {
                return Err("显卡固件状态已变化，请重新读取".into());
            }
            if control.declared {
                self.audit(
                    "core-declared-control-intent",
                    json!({"feature":4,"target":target}),
                )?;
                self.guard()?;
                attempted = true;
                let code = crate::native_admission::or_native_declared_control(4, target as u32);
                if code != 0 {
                    return Err(format!("声明式显卡命令发送失败 ({code})"));
                }
                control.fresh = false;
                self.audit(
                    "core-declared-control-sent",
                    json!({"feature":4,"target":target,"hardwareReadback":false}),
                )?;
                return Ok(Outcome::CommandSent);
            }
            let initial = control.boot.as_ref().map_or(0, |b| b.initial_mode);
            let runtime_change = matches!(target, 1 | 2)
                && matches!(initial, 1 | 2)
                && control.runtime.supported == Some(true)
                && control
                    .runtime
                    .igpu
                    .is_some_and(|active| active != (target == 1));
            if expected == target && !runtime_change {
                return Ok(Outcome::Skipped);
            }
            control.save_boot()?;
            if expected != target {
                self.audit(
                    "gpu-next-boot-intent",
                    json!({"before":before,"target":target}),
                )?;
                self.guard()?;
                let root = super::ControlState::read()?;
                self.check_root(&root)?;
                attempted = true;
                let code = crate::or_native_gpu_stage(expected as u32, target as u32);
                let after = firmware()?;
                if code != 0 || !known(&after) || after["Mode"] != target {
                    control.fresh = false;
                    return Err(format!("显卡设置回读未确认 ({code})"));
                }
                self.audit(
                    "gpu-next-boot-confirmed",
                    json!({"receipt":{"Before":before,"After":after,"Changed":true}}),
                )?;
                control.firmware = after;
                if let Some(boot) = control.boot.as_mut() {
                    boot.firmware_mode = target;
                }
            }
            if runtime_change {
                let expected_runtime = control.runtime.igpu.unwrap();
                self.audit(
                    "gpu-runtime-intent",
                    json!({"expected":expected_runtime,"target":target==1}),
                )?;
                self.guard()?;
                let before = Runtime::read();
                if before.supported != Some(true) || before.igpu != Some(expected_runtime) {
                    return Err("热切换能力或当前状态未确认".into());
                }
                self.guard()?;
                attempted = true;
                let _ = openrevo_core::driver::wmi_mulong::set_acpi_igpu_mode(target == 1);
                let after = Runtime::read();
                if after.supported != Some(true) || after.igpu != Some(target == 1) {
                    return Err("显卡运行态回读未确认；停止后续写入".into());
                }
                control.runtime = after;
                self.audit("gpu-runtime-confirmed", json!({"igpu":target==1}))?;
            }
            control.fresh = true;
            control.persist(prefs)?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        if result.is_err() {
            control.fresh = false;
            control.runtime = Runtime::default();
        }
        self.finish(result, attempted)
    }
}
