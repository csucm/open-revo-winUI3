//! Native owner of the application control session. No initialization on load.
//! Register writes use the existing admitted executors, not the upstream mode loop.
#[path = "tuning.rs"]
mod tuning;
#[path = "startup_factory_mode.rs"]
mod startup_factory_mode;
use super::platform::{ac_online, check_conflicts, gm7tg0m_identity, OperationLock};
use crate::native_admission as admission;
use openrevo_core::{driver::wmi_ec as ec, system::power};
use serde::Serialize;
use serde_json::{json, Value};
use std::{
    fs::OpenOptions,
    io::Write,
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::PathBuf,
};
pub use tuning::{FanPoint, TuningProfile, TuningSnapshot};
#[path = "tuning_editor.rs"]
mod tuning_editor;
pub use tuning_editor::TuningEditor;
#[path = "gpu_mode.rs"]
mod gpu_mode;
pub use gpu_mode::GpuControl;
#[path = "desktop.rs"]
mod desktop;
pub(super) use desktop::query as query_state;
pub use desktop::DesktopState;
#[path = "camera.rs"]
mod camera;
pub use camera::Camera;
#[path = "telemetry_hal.rs"]
mod telemetry_hal;

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct ControlState {
    pub project: u8,
    pub fan_clamp: u8,
    pub upper: u8,
    pub lower: u8,
    pub battery_mode: u8,
    pub platform: u8,
    pub support1: u8,
    pub support2: u8,
    pub power_status: u8,
    pub battery_limit_reached: bool,
}
impl ControlState {
    fn read() -> Result<Self, String> {
        let upper = read(1977)?;
        let state = Self {
            project: read(1856)?,
            fan_clamp: read(1873)?,
            upper: upper & 127,
            lower: read(2000)?,
            battery_mode: read(1958)?,
            platform: read(1987)?,
            support1: read(1893)?,
            support2: read(1894)?,
            power_status: read(1996)?,
            battery_limit_reached: upper & 128 != 0,
        };
        if matches!(state.project, 0 | 255)
            || !(40..=100).contains(&state.upper)
            || state.lower > state.upper
            || matches!(state.platform, 4 | 5)
        {
            return Err("当前电池阈值、项目或平台状态不满足普通控制条件".into());
        }
        Ok(state)
    }
    fn raw_upper(&self) -> u8 {
        self.upper | if self.battery_limit_reached { 128 } else { 0 }
    }
    fn battery_available(&self) -> bool {
        self.support2 & 64 != 0
            || self.lower.checked_add(5) == Some(self.upper)
                && (matches!(
                    (self.upper, self.battery_mode & 49),
                    (60, 33) | (80, 17) | (100, 1)
                ) || matches!(self.project, 16 | 26)
                    && matches!(self.upper, 60 | 80)
                    && self.battery_mode & 49 == 1)
    }
    fn battery_matches(&self, other: &Self) -> bool {
        let selector = self.battery_mode == other.battery_mode
            || self.project == 26
                && self.lower.checked_add(5) == Some(self.upper)
                && matches!(self.upper, 60 | 80)
                && (self.battery_mode ^ other.battery_mode) & !49 == 0
                && [1, if self.upper == 60 { 33 } else { 17 }].contains(&(self.battery_mode & 49))
                && [1, if self.upper == 60 { 33 } else { 17 }].contains(&(other.battery_mode & 49));
        self.project == other.project
            && self.upper == other.upper
            && self.lower == other.lower
            && selector
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct PerformanceState {
    tables: Vec<u8>,
    latch: u8,
    ap: u8,
    index: u8,
    silent: u8,
    custom: u8,
    isolated: u8,
    ramp: u8,
    clamp: u8,
    trigger: u8,
}
impl PerformanceState {
    fn read() -> Result<Self, String> {
        Ok(Self {
            tables: (3840..3936).map(read).collect::<Result<_, _>>()?,
            latch: read(1990)?,
            ap: read(1857)?,
            index: read(1963)?,
            silent: read(1115)?,
            custom: read(1798)?,
            isolated: read(1989)?,
            ramp: read(1927)?,
            clamp: read(1873)?,
            trigger: read(1895)?,
        })
    }
    pub fn mode(&self, root: Option<&ControlState>) -> u8 {
        Self::decode_mode(
            self.custom,
            self.clamp,
            self.index,
            self.isolated,
            root.map(|r| (r.project, r.platform)),
        )
    }
    fn decode_mode(
        custom: u8,
        clamp: u8,
        index: u8,
        isolated: u8,
        project: Option<(u8, u8)>,
    ) -> u8 {
        if gm7tg0m_identity() && project == Some((16, 7)) && custom == 0 && index == 16 {
            match (clamp & !64, isolated) {
                (160, 0) => return 1,
                (0, 0 | 128) => return 2,
                (16, 0) => return 3,
                _ => {}
            }
        }
        if custom & 1 != 0 {
            4
        } else if clamp & 176 == 160 {
            1
        } else if index & 30 == 16 {
            3
        } else {
            2
        }
    }
    fn matches(&self, other: &Self) -> bool {
        self.tables == other.tables
            && self.latch == other.latch
            && self.ap == other.ap
            && self.index == other.index
            && self.custom == other.custom
            && self.isolated == other.isolated
            && self.ramp == other.ramp
            && self.clamp == other.clamp
            && self.trigger & !40 == other.trigger & !40
    }
    fn recognized(&self, target: bool) -> bool {
        let mut bytes = self.tables.clone();
        bytes.extend([self.custom, self.latch, self.ap, self.isolated]);
        unsafe {
            admission::or_native_classic_fan_protocol(
                bytes.as_ptr(),
                bytes.len(),
                u32::from(target),
            ) == 1
        }
    }
    fn factory(&self, mode: u8) -> Result<Self, String> {
        let table =
            serde_json::to_value(power::get_mode_fan_table(mode)).map_err(|e| e.to_string())?;
        let mut tables = Vec::with_capacity(96);
        for key in [
            "cpu_upt",
            "cpu_downt",
            "cpu_duty",
            "gpu_upt",
            "gpu_downt",
            "gpu_duty",
        ] {
            let row = table[key]
                .as_array()
                .filter(|r| r.len() == 16)
                .ok_or("Core风扇表不完整")?;
            for value in row {
                tables.push(
                    value
                        .as_u64()
                        .and_then(|n| u8::try_from(n).ok())
                        .ok_or("Core风扇表字节无效")?,
                );
            }
        }
        tables[95] = tables[95].max(160);
        let desired = Self {
            tables,
            latch: 4,
            ap: self.ap | 129,
            index: (self.index & !30) | if mode == 3 { 16 } else { 0 },
            silent: u8::from(mode == 1),
            custom: 64,
            isolated: self.isolated & !128,
            ramp: 131,
            clamp: (self.clamp & !176)
                | match mode {
                    1 => 160,
                    3 => 16,
                    _ => 0,
                },
            trigger: (self.trigger & !8) | 32 | if mode == 1 { 8 } else { 0 },
        };
        if !desired.recognized(true) {
            return Err("目标不是允许的工厂档位协议".into());
        }
        Ok(desired)
    }
}
fn read(address: u16) -> Result<u8, String> {
    ec::wmi_read_ec(address).ok_or_else(|| format!("硬件读取失败 {address:#06x}"))
}

#[derive(Clone, Debug, Serialize)]
pub enum Outcome {
    Applied,
    Skipped,
    Busy,
    Closing,
    Cancelled,
    Rejected,
    ReadbackConfirmed,
    CommandSent,
    ConfigurationSaved,
    ConfigurationSaveFailed,
    Unknown,
}
#[derive(Clone, Debug, Serialize)]
pub struct Receipt {
    pub outcome: Outcome,
    pub mode: Option<u8>,
    pub message: Option<String>,
    pub warning: Option<String>,
    pub payload: Option<Value>,
}
impl Receipt {
    pub fn new(outcome: Outcome) -> Self {
        Self {
            outcome,
            mode: None,
            message: None,
            warning: None,
            payload: None,
        }
    }
    pub fn error(outcome: Outcome, message: impl Into<String>) -> Self {
        Self {
            message: Some(message.into()),
            ..Self::new(outcome)
        }
    }
}
pub struct HardwareSession {
    pub admission: Value,
    pub state: Option<ControlState>,
    pub performance: Option<PerformanceState>,
    pub tuning_baseline: Option<(String, TuningSnapshot)>,
    pub faulted: bool,
    delegated_active: bool,
    pub allow_battery: bool,
    pub requested_boost: Option<bool>,
    pub mode: Option<u8>,
    root: PathBuf,
    plugin_authorization: Option<std::sync::Arc<std::sync::atomic::AtomicBool>>,
    pub temporary_office: Option<(PerformanceState, PerformanceState)>,
    last_performance: Option<(PerformanceState, PerformanceState)>,
}
impl HardwareSession {
    pub fn new(root: PathBuf, allow_battery: bool) -> Self {
        Self {
            admission: Value::Null,
            state: None,
            performance: None,
            tuning_baseline: None,
            faulted: false,
            delegated_active: false,
            allow_battery,
            requested_boost: None,
            mode: None,
            root,
            plugin_authorization: None,
            temporary_office: None,
            last_performance: None,
        }
    }
    pub fn active(&self) -> bool {
        !self.faulted && (self.state.is_some() || self.delegated_active)
    }
    pub fn classic(&self) -> bool {
        self.admission["classic_ec"] == true
    }
    pub fn supports(&self, key: &str) -> bool {
        self.admission["ready"] == true && self.admission["capabilities"][key] == true
    }
    pub fn audit(&self, operation: &str, value: Value) -> Result<(), String> {
        super::platform::validate_directory(&self.root)?;
        let mut file = OpenOptions::new()
            .custom_flags(0x00200000)
            .create(true)
            .append(true)
            .open(self.root.join("hardware-audit.jsonl"))
            .map_err(|e| e.to_string())?;
        let metadata = file.metadata().map_err(|e| e.to_string())?;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            return Err("硬件审计目标不是普通文件".into());
        }
        writeln!(
            file,
            "{}",
            json!({"operation":operation,"atUnixMs":super::platform::unix_ms(),"state":value})
        )
        .map_err(|e| e.to_string())
    }
    fn guard(&self) -> Result<(), String> {
        check_conflicts()?;
        if self.admission["ready"] != true {
            return Err("Core驱动尚未就绪".into());
        }
        if self.classic()
            && admission::project().map(u64::from) != self.admission["project"].as_u64()
        {
            return Err("EC 项目与接管身份已变化".into());
        }
        Ok(())
    }
    pub fn takeover(&mut self) -> Receipt {
        let result: Result<(), String> = (|| {
            let _lock = OperationLock::enter()?;
            self.admission = admission::probe_with_mode_sample(gm7tg0m_identity());
            self.guard()?;
            if !self.classic() {
                self.delegated_active = true;
                self.tuning_baseline = None;
                self.state = None;
                self.mode = self.admission["mode"].as_u64().map(|v| v as u8);
            } else {
                let root = ControlState::read()?;
                if Some(u64::from(root.project)) != self.admission["project"].as_u64() {
                    return Err("接管项目不匹配".into());
                }
                // A new control snapshot must not retain receipts from an old session.
                let performance = PerformanceState::read()?;
                self.mode = Some(performance.mode(Some(&root)));
                self.state = Some(root);
                self.performance = Some(performance);
                self.delegated_active = false;
            }
            self.tuning_baseline = None;
            self.faulted = false;
            self.temporary_office = None;
            self.last_performance = None;
            self.requested_boost = None;
            self.audit("takeover-read-only",json!({"admission":self.admission,"control":self.state,"performance":self.performance}))?;
            Ok(())
        })();
        match result {
            Ok(()) => Receipt {
                mode: self.mode,
                ..Receipt::new(Outcome::ReadbackConfirmed)
            },
            Err(e) => {
                self.release();
                Receipt::error(Outcome::Rejected, e)
            }
        }
    }
    pub fn release(&mut self) {
        self.tuning_baseline = None;
        self.state = None;
        self.performance = None;
        self.delegated_active = false;
        self.faulted = false;
        self.mode = None;
        self.requested_boost = None;
        self.temporary_office = None;
        self.last_performance = None;
    }
    fn ready(&self, key: &str) -> Result<(), String> {
        if !self.active() || !self.supports(key) {
            return Err("当前未接管硬件或机型未声明此功能；错误后需重新读取接管".into());
        }
        self.guard()
    }
    fn check_root(&self, before: &ControlState) -> Result<(), String> {
        let expected = self.state.as_ref().ok_or("缺少接管快照")?;
        if expected.project != before.project || expected.platform != before.platform {
            return Err("设备或平台状态已变化，请重新接管".into());
        }
        Ok(())
    }
    fn authorized(&self) -> Result<(), String> {
        if self
            .plugin_authorization
            .as_ref()
            .is_some_and(|v| !v.load(std::sync::atomic::Ordering::Acquire))
        {
            Err("插件授权已撤销".into())
        } else {
            Ok(())
        }
    }
    pub fn plugin_action(
        &mut self,
        active: std::sync::Arc<std::sync::atomic::AtomicBool>,
        action: &super::plugins::Action,
    ) -> Receipt {
        self.plugin_authorization = Some(active);
        let receipt = match action.action.as_str() {
            "performance.set" => {
                self.select_factory(action.arguments["mode"].as_u64().unwrap_or(0) as u8, true)
            }
            "fanBoost.set" => self.boost(action.arguments["enabled"] == true),
            "battery.limit.set" => {
                self.battery(action.arguments["percent"].as_u64().unwrap_or(0) as u8)
            }
            _ => Receipt::new(Outcome::Rejected),
        };
        self.plugin_authorization = None;
        receipt
    }
    pub fn battery(&mut self, upper: u8) -> Receipt {
        if !matches!(upper, 60 | 80 | 100) {
            return Receipt::error(Outcome::Rejected, "仅支持 60 / 80 / 100 三档");
        }
        self.control_change(Some(upper), None)
    }
    pub fn boost(&mut self, enabled: bool) -> Receipt {
        self.control_change(None, Some(enabled))
    }
    fn control_change(&mut self, upper: Option<u8>, boost: Option<bool>) -> Receipt {
        let mut attempted = false;
        let result: Result<Outcome, String> = (|| {
            let _lock = OperationLock::enter()?;
            let key = if upper.is_some() {
                "battery"
            } else {
                "fan_boost"
            };
            self.ready(key)?;
            if !self.classic() {
                self.authorized()?;
                attempted = true;
                let code = admission::or_native_declared_control(
                    if upper.is_some() { 1 } else { 2 },
                    upper
                        .map(u32::from)
                        .unwrap_or(u32::from(boost == Some(true))),
                );
                if code != 0 {
                    return Err(format!("Core 声明式控制失败 ({code})"));
                }
                if boost.is_some() {
                    self.requested_boost = boost;
                }
                self.audit(
                    "declared-command-sent",
                    json!({"feature":key,"upper":upper,"boost":boost}),
                )?;
                return Ok(Outcome::CommandSent);
            }
            let before = ControlState::read()?;
            self.check_root(&before)?;
            let expected = self.state.as_ref().ok_or("缺少接管快照")?;
            if if upper.is_some() {
                !expected.battery_matches(&before)
            } else {
                expected.fan_clamp != before.fan_clamp
            } {
                return Err("硬件状态已变化，请重新读取接管".into());
            }
            let mut desired = before.clone();
            let request = if let Some(upper) = upper {
                if !before.battery_available() {
                    return Err("当前充电阈值不符合受支持的控制协议".into());
                }
                desired.upper = upper;
                desired.lower = upper - 5;
                desired.battery_limit_reached = false;
                desired.battery_mode = (before.battery_mode & !49)
                    | match upper {
                        60 => 33,
                        80 => 17,
                        _ => 1,
                    };
                json!({"kind":"battery","project":before.project,"oldUpper":before.raw_upper(),"oldLower":before.lower,
                    "oldMode":before.battery_mode,"upper":desired.upper,"lower":desired.lower,"mode":desired.battery_mode})
            } else {
                if before.support1 & 128 == 0 {
                    return Err("EC 未声明支持强冷".into());
                }
                desired.fan_clamp = if boost == Some(true) {
                    before.fan_clamp | 64
                } else {
                    before.fan_clamp & !64
                };
                json!({"kind":"boost","project":before.project,"before":before.fan_clamp,"after":desired.fan_clamp})
            };
            self.audit("control-intent", json!({"before":before,"desired":desired}))?;
            self.guard()?;
            self.authorized()?;
            attempted = true;
            admission::apply_classic_transaction(request)?;
            let after = ControlState::read()?;
            if if upper.is_some() {
                !desired.battery_matches(&after)
            } else {
                desired.fan_clamp != after.fan_clamp
            } {
                return Err("最终控制状态未保持目标值".into());
            }
            self.state = Some(after.clone());
            self.audit("control-confirmed", json!({"before":before,"after":after}))?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        self.finish(result, attempted)
    }
    pub fn select_factory(&mut self, mode: u8, dc_confirmed: bool) -> Receipt {
        let mut receipt = self.apply_factory(mode, dc_confirmed);
        if matches!(receipt.outcome, Outcome::ReadbackConfirmed) && receipt.mode == Some(mode) {
            self.remember_factory_mode(Some(mode), &mut receipt);
        }
        receipt
    }
    fn apply_factory(&mut self, mode: u8, dc_confirmed: bool) -> Receipt {
        let receipt = self.change_performance(mode, dc_confirmed, None, None, true, true);
        openrevo_core::driver::nvapi::release_session();
        receipt
    }
    pub fn sleep_office(&mut self, sleeping: bool, enabled: bool) -> Receipt {
        if !self.active() || !self.classic() {
            return Receipt::new(Outcome::Skipped);
        }
        if sleeping {
            if !enabled || self.temporary_office.is_some() {
                return Receipt::new(Outcome::Skipped);
            }
            match self.refresh_mode() {
                Ok(Some(1)) => return Receipt::new(Outcome::Skipped),
                Err(error) => return Receipt::error(Outcome::Unknown, error),
                _ => {}
            }
            let result = self.change_performance(1, false, None, None, false, false);
            if matches!(result.outcome, Outcome::ReadbackConfirmed) {
                self.temporary_office = self.last_performance.clone();
            }
            result
        } else if let Some((before, after)) = self.temporary_office.take() {
            let mode = before.mode(self.state.as_ref());
            self.change_performance(mode, true, Some(after), Some(before), false, false)
        } else {
            Receipt::new(Outcome::Skipped)
        }
    }
    fn check_power(&self, mode: u8, dc_confirmed: bool) -> Result<(), String> {
        if mode != 3 {
            return Ok(());
        }
        let power = read(1996)?;
        if !self.allow_battery
            && (!dc_confirmed || ac_online() != Some(true) || power & 62 != 0 && power & 1 == 0)
        {
            return Err("狂暴模式需要原装 DC 确认及交流供电；解除离电限制须在设置页确认".into());
        }
        Ok(())
    }
    fn controls(&self, project: u8, steps: &[(u16, u8, u8)]) -> Result<(), String> {
        self.guard()?;
        admission::apply_classic_transaction(json!({"kind":"fan_controls","project":project,
            "steps":steps.iter().map(|(address,value,ignore)|json!({"address":address,"value":value,"ignore":ignore})).collect::<Vec<_>>()}))
    }
    fn change_performance(
        &mut self,
        mode: u8,
        dc: bool,
        required: Option<PerformanceState>,
        target: Option<PerformanceState>,
        replace_oem: bool,
        reset_gpu: bool,
    ) -> Receipt {
        if !(1..=3).contains(&mode) {
            return Receipt::error(Outcome::Rejected, "原厂档位范围为 1..3");
        }
        let mut attempted = false;
        let mut warning = None;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.ready("performance")?;
            if !self.classic() {
                if mode == 3 && !self.allow_battery && ac_online() != Some(true) {
                    return Err("狂暴模式需要交流供电".into());
                }
                let live = admission::probe();
                let before = live["mode"].as_u64().ok_or("Core 未提供当前档位")?;
                self.audit("core-mode-intent", json!({"before":before,"target":mode}))?;
                self.guard()?;
                if !attempted {
                    self.authorized()?;
                }
                attempted = true;
                let code =
                    admission::or_native_declarative_mode_set(before as u32, u32::from(mode));
                if code != 0 {
                    return Err(format!("Core 档位控制未确认 ({code})"));
                }
                self.admission = admission::probe();
                self.mode = Some(mode);
                self.audit("core-mode-confirmed", json!({"before":before,"after":mode}))?;
                return Ok(Outcome::ReadbackConfirmed);
            }
            let root = ControlState::read()?;
            self.check_root(&root)?;
            let before = PerformanceState::read()?;
            if !replace_oem && !before.recognized(false) {
                return Err("当前不是已验证的风扇表协议，未修改性能".into());
            }
            if required.as_ref().is_some_and(|r| !r.matches(&before)) {
                return Err("性能状态已被外部修改，未继续恢复档位".into());
            }
            self.check_power(mode, dc)?;
            let desired = match target {
                Some(value) => value,
                None => before.factory(mode)?,
            };
            if !desired.recognized(true) {
                return Err("恢复目标不是允许的原厂协议".into());
            }
            let gpu = reset_gpu.then(super::gpu::GpuState::probe);
            let offsets = gpu
                .as_ref()
                .and_then(|g| g.offsets.as_ref())
                .map(|o| (o.core_mhz, o.memory_mhz));
            if before.mode(Some(&root)) == 4
                && gpu
                    .as_ref()
                    .is_some_and(|g| g.capabilities.supported && g.offsets.is_none())
            {
                return Err("无法确认显卡超频状态，请先重新读取".into());
            }
            self.audit(
                "performance-intent",
                json!({"before":before,"desired":desired,"replaceOem":replace_oem}),
            )?;
            self.guard()?;
            self.check_power(mode, dc)?;
            if let Some((core, memory)) = offsets.filter(|v| *v != (0, 0)) {
                if !attempted {
                    self.authorized()?;
                }
                attempted = true;
                let code = crate::or_native_clock_offsets_set(core, memory, 0, 0);
                if code != 0 {
                    return Err(format!("GPU 频偏复位未确认 ({code})"));
                }
                if !PerformanceState::read()?.matches(&before) {
                    return Err("显卡复位后档位已变化，未继续写入".into());
                }
            }
            self.guard()?;
            self.check_power(mode, dc)?;
            if !attempted {
                self.authorized()?;
            }
            attempted = true;
            self.controls(
                root.project,
                &[(1990, 4, 0), (1990, 0, 0), (1857, desired.ap, 0)],
            )?;
            self.guard()?;
            admission::apply_classic_transaction(
                json!({"kind":"fan_tables","project":root.project,"factory":true,"values":desired.tables}),
            )?;
            self.check_power(mode, dc)?;
            self.controls(
                root.project,
                &[
                    (1798, desired.custom, 0),
                    (1989, desired.isolated, 0),
                    (1927, desired.ramp, 0),
                    (1963, desired.index, 0),
                    (1115, desired.silent, 1),
                    (1895, desired.trigger, 40),
                    (1990, desired.latch, 0),
                    (1873, desired.clamp, 0),
                ],
            )?;
            let after = PerformanceState::read()?;
            if !desired.matches(&after) {
                return Err("最终档位或风扇表回读不一致".into());
            }
            self.state = Some(ControlState::read()?);
            self.mode = Some(after.mode(self.state.as_ref()));
            self.performance = Some(after.clone());
            self.last_performance = Some((before.clone(), after.clone()));
            self.tuning_baseline = None;
            self.audit(
                "performance-confirmed",
                json!({"before":before,"after":after}),
            )?;
            // Indicator failure must not invalidate a confirmed performance transaction.
            let code = self.synchronize_indicator();
            if code != 0 {
                warning = Some(format!("性能已确认，模式指示灯未同步 ({code})"));
            }
            Ok(Outcome::ReadbackConfirmed)
        })();
        let mut receipt = self.finish(result, attempted);
        receipt.warning = warning;
        receipt
    }
    fn synchronize_indicator(&self) -> i32 {
        let result = (|| -> Result<(), String> {
            self.guard()?;
            let steps = [(1830u16, 128u8), (1831u16, 64u8)]
                .into_iter()
                .map(|(address, mask)| {
                    let before = read(address)?;
                    let target = if self.mode == Some(4) {
                        before | mask
                    } else {
                        before & !mask
                    };
                    Ok((address, before, target))
                })
                .collect::<Result<Vec<_>, String>>()?;
            for (address, before, target) in steps {
                if before != target
                    && admission::or_native_ec_write(
                        u32::from(address),
                        u32::from(before),
                        u32::from(target),
                    ) != 0
                {
                    return Err("指示灯写入失败".into());
                }
                if read(address)? != target {
                    return Err("指示灯回读不一致".into());
                }
            }
            Ok(())
        })();
        if result.is_ok() {
            0
        } else {
            -4
        }
    }
    pub fn refresh_mode(&mut self) -> Result<Option<u8>, String> {
        let _lock = OperationLock::enter()?;
        self.guard()?;
        if self.classic() {
            let current = PerformanceState::read()?;
            self.mode = Some(current.mode(self.state.as_ref()));
            self.performance = Some(current);
        } else {
            self.admission = admission::probe();
            self.mode = self.admission["mode"].as_u64().map(|n| n as u8);
        }
        Ok(self.mode)
    }
    pub fn enforce_battery_guard(&mut self) -> Receipt {
        if self.allow_battery || !self.active() {
            return Receipt::new(Outcome::Skipped);
        }
        let lock = match OperationLock::enter() {
            Ok(lock) => lock,
            Err(e) => return Receipt::error(Outcome::Rejected, e),
        };
        let current = if self.classic() {
            PerformanceState::read()
                .ok()
                .map(|p| p.mode(self.state.as_ref()))
        } else {
            admission::probe()["mode"].as_u64().map(|m| m as u8)
        };
        let result = if matches!(current, Some(3 | 4)) {
            self.apply_factory(2, false)
        } else {
            Receipt::new(Outcome::Skipped)
        };
        drop(lock);
        result
    }
    pub fn apply_declared(&mut self, preset: &Value) -> Receipt {
        let mut attempted = false;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.guard()?;
            if !self.active()
                || self.classic()
                || !self.admission["declared_tuning"].is_object()
                || preset["Manifest"] != self.admission["manifest"]
            {
                return Err("Core 未声明此设备的 CPU 调校".into());
            }
            let caps = &self.admission["declared_tuning"];
            let number = |key: &str| {
                preset[key]
                    .as_u64()
                    .ok_or_else(|| format!("调校参数 {key} 无效"))
            };
            let (pl1, pl2, temp) = (number("Pl1")?, number("Pl2")?, number("Temperature")?);
            for (value, key) in [(pl1, "pl1"), (pl2, "pl2")] {
                let min = caps[key]["min"].as_u64().ok_or("机型调校范围缺失")?;
                let max = caps[key]["max"].as_u64().ok_or("机型调校范围缺失")?;
                if !(min..=max).contains(&value) {
                    return Err("功耗不在本机声明范围".into());
                }
            }
            if pl1 > pl2 || temp != 0 && !(70..=100).contains(&temp) {
                return Err("功耗顺序或温度范围无效".into());
            }
            let custom = preset["CustomFans"].as_bool().ok_or("缺少风扇控制声明")?;
            if custom && caps["fan_curve"] != true {
                return Err("Core 未声明风扇曲线控制".into());
            }
            let convert = |key: &str| -> Result<Value, String> {
                if !custom {
                    return Ok(json!([]));
                }
                let points = preset[key]
                    .as_array()
                    .filter(|p| p.len() == 8)
                    .ok_or("风扇曲线须有八点")?;
                let mut previous = None;
                let mut values = Vec::new();
                for point in points {
                    let t = point["Celsius"]
                        .as_u64()
                        .filter(|n| (30..=100).contains(n))
                        .ok_or("曲线温度无效")?;
                    let p = point["Percent"]
                        .as_u64()
                        .filter(|n| *n <= 100)
                        .ok_or("曲线转速无效")?;
                    if previous.is_some_and(|(pt, pp)| t <= pt || p < pp) {
                        return Err("曲线必须单调".into());
                    }
                    previous = Some((t, p));
                    values.push(json!({"temp":t,"duty":p}));
                }
                if previous.is_none_or(|(t, p)| t > 95 || p != 100) {
                    return Err("曲线末点须不高于95度且100%".into());
                }
                Ok(json!(values))
            };
            let request = json!({"pl1":pl1,"pl2":pl2,"temperature":temp,"custom_fans":custom,
                "cpu_curve":convert("CpuCurve")?,"gpu_curve":convert("GpuCurve")?});
            if !self.allow_battery && ac_online() != Some(true) {
                return Err("CPU 调校需要交流供电".into());
            }
            self.audit("core-cpu-profile-intent", request.clone())?;
            self.guard()?;
            attempted = true;
            admission::apply_declared_cpu_profile(request.clone())?;
            self.audit("core-cpu-profile-sent", request)?;
            Ok(Outcome::CommandSent)
        })();
        self.finish(result, attempted)
    }
    fn finish(&mut self, result: Result<Outcome, String>, attempted: bool) -> Receipt {
        match result {
            Ok(outcome) => Receipt {
                mode: self.mode,
                ..Receipt::new(outcome)
            },
            Err(error) => {
                if attempted {
                    self.faulted = true;
                    let _ = self.audit("failed-stop", json!({"error":error,"attemptedWrite":true}));
                }
                Receipt::error(
                    if attempted {
                        Outcome::Unknown
                    } else {
                        Outcome::Rejected
                    },
                    if attempted {
                        format!("操作已停收，可能部分生效；请重新读取并接管。{error}")
                    } else {
                        error
                    },
                )
            }
        }
    }
}

impl HardwareSession {
    pub fn overview_switch(&mut self, id: u32, expected: bool, target: bool) -> Receipt {
        let mut attempted = false;
        let result = (|| -> Result<Outcome, String> {
            let _lock = OperationLock::enter()?;
            if !self.active() || !matches!(id, 3 | 4) {
                return Err("当前未接管硬件或开关无效".into());
            }
            self.guard()?;
            let key = if id == 3 { "touchpad" } else { "fn_lock" };
            let before = if self.classic() {
                Some(ControlState::read()?)
            } else {
                None
            };
            if let Some(before) = &before {
                self.check_root(before)?;
            } else if id == 3 && self.admission["touchpad_control"] != true
                || id == 4 && !self.supports("fn_lock")
            {
                return Err("Core未声明此开关".into());
            }
            let (address, mask) = if id == 3 { (1958, 8) } else { (1870, 16) };
            let desired = if before.is_some() {
                let raw = read(address)?;
                if id == 4 && (raw & mask != 0) != expected {
                    return Err("Fn状态已变化，请重读".into());
                }
                Some(if target { raw | mask } else { raw & !mask })
            } else {
                None
            };
            if crate::native_overview::switch_read(id) != Some(expected) {
                return Err("设备状态已变化，请重读".into());
            }
            self.audit(
                "overview-switch-intent",
                json!({"key":key,"before":expected,"target":target}),
            )?;
            self.guard()?;
            attempted = true;
            let code = crate::native_overview::or_native_switch_set(
                id,
                u32::from(expected),
                u32::from(target),
            );
            if code != 0 || crate::native_overview::switch_read(id) != Some(target)
            {
                return Err(format!("设备回读未确认 ({code})"));
            }
            if let (Some(mut expected), Some(desired)) = (before, desired) {
                if id == 3 {
                    expected.battery_mode = desired;
                }
                let after = ControlState::read()?;
                if read(address)? != desired || after != expected {
                    return Err("开关或无关硬件位回读不一致".into());
                }
                self.state = Some(after);
            }
            self.audit(
                "overview-switch-confirmed",
                json!({"key":key,"target":target,"windowsReadback":id==3}),
            )?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        self.finish(result, attempted)
    }
    pub fn declared_fn_lock(&mut self, enabled: bool) -> Receipt {
        let mut attempted = false;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.ready("fn_lock")?;
            if self.classic()
                || self.admission["fn_lock_readback"] == true
                || self.admission["fn_lock_via_ec"] == true
            {
                return Err("Fn锁应走可回读入口".into());
            }
            self.audit("declared-fn-lock-intent", json!({"enabled":enabled}))?;
            self.guard()?;
            attempted = true;
            let code = admission::or_native_declared_control(3, u32::from(enabled));
            if code != 0 {
                return Err(format!("Fn锁发送失败 ({code})"));
            }
            self.audit("declared-fn-lock-sent", json!({"enabled":enabled}))?;
            Ok(Outcome::CommandSent)
        })();
        self.finish(result, attempted)
    }
    pub fn adopt_saved_color(&self) -> Result<(), String> {
        let _lock = OperationLock::enter()?;
        use openrevo_core::system::display_tuning;
        if !display_tuning::load_display_tuning_config().enabled {
            return Ok(());
        }
        let saved = display_tuning::get_current_tuning_state();
        if !saved.enabled || display_tuning::native_active() {
            return Ok(());
        }
        self.guard()?;
        // This process must own the configured desktop filter while the panel
        // is absent. Do not rewrite the preference file on adoption.
        openrevo_core::system::native_services::validate_color(&saved.active_preset)?;
        display_tuning::apply_color_preset(&saved.active_preset)
    }
}
