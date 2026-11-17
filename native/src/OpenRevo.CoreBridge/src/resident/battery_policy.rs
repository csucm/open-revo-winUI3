//! Battery policy owns the configured target and the last verified EC reading.
//! Other controls use their own snapshots and do not depend on these registers.
use super::{startup_factory_mode::Device, ControlState, HardwareSession, Outcome, Receipt};
use crate::{
    native_admission,
    resident::platform::{validate_directory, OperationLock},
};
use openrevo_core::{driver::wmi_ec as ec, system::config_io};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs::OpenOptions,
    io::{ErrorKind, Read},
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::Path,
};

const FILE: &str = "battery-limit.json";
#[derive(Deserialize, Serialize)]
struct Target {
    schema: u8,
    device: Device,
    support1: u8,
    support2: u8,
    percent: u8,
}
fn bounded(root: &Path, name: &str, max: u64) -> Result<Option<Vec<u8>>, String> {
    validate_directory(root)?;
    let file = match OpenOptions::new()
        .read(true)
        .custom_flags(0x00200000)
        .open(root.join(name))
    {
        Ok(file) => file,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.to_string()),
    };
    let meta = file.metadata().map_err(|e| e.to_string())?;
    if !meta.is_file() || meta.file_attributes() & 0x400 != 0 || meta.len() > max {
        return Err("限充记录不是大小受限的普通文件".into());
    }
    let mut bytes = Vec::new();
    file.take(max + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > max {
        return Err("限充记录超过大小限制".into());
    }
    Ok(Some(bytes))
}
impl Target {
    fn read(root: &Path) -> Result<Option<Self>, String> {
        let Some(bytes) = bounded(root, FILE, 4096)? else {
            return Ok(None);
        };
        let target: Self = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        if target.schema != 1 || !matches!(target.percent, 60 | 80 | 100) {
            return Err("限充目标记录无效".into());
        }
        Ok(Some(target))
    }
    fn for_device(control: &ControlState, percent: u8) -> Result<Self, String> {
        Ok(Self {
            schema: 1,
            device: Device::read(control).ok_or("无法确认限充设备身份")?,
            support1: control.support1,
            support2: control.support2,
            percent,
        })
    }
    fn matches_device(&self, control: &ControlState) -> bool {
        Device::read(control).as_ref() == Some(&self.device)
            && self.support1 == control.support1
            && self.support2 == control.support2
    }
    fn save(&self, root: &Path) -> Result<(), String> {
        validate_directory(root)?;
        config_io::atomic_write_with_retry(
            &root.join(FILE),
            &serde_json::to_vec(self).map_err(|e| e.to_string())?,
        )
    }
}
const SELECTOR_MASK: u8 = 0x31;
const TOUCHPAD_BIT: u8 = 0x08;

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "PascalCase")]
pub(super) struct BatteryState {
    upper: u8,
    lower: u8,
    battery_mode: u8,
    battery_limit_reached: bool,
}

impl BatteryState {
    fn read() -> Result<Self, String> {
        // The checked transport distinguishes an empty reply from a real zero.
        // This machine previously returned 60/55/mode 9 and later a complete
        // 0/0/mode 0 reply. The change's source and exact time are unknown.
        let bytes = ec::wmi_batch_read_ec_checked(&[
            ec::ADDR_BATTERY_LIMIT_UP,
            ec::ADDR_BATTERY_LIMIT_DOWN,
            ec::ADDR_BATTERY_PROTECTION_MODE,
        ])
        .map_err(|address| format!("限充读取失败：EC {address:#06x}"))?;
        let [upper, lower, battery_mode] = bytes[..] else {
            return Err("限充读取失败：控制快照长度无效".into());
        };
        Ok(Self {
            upper: upper & 127,
            lower,
            battery_mode,
            battery_limit_reached: upper & 128 != 0,
        })
    }

    fn raw_upper(&self) -> u8 {
        self.upper | if self.battery_limit_reached { 128 } else { 0 }
    }

    fn valid(&self) -> bool {
        (40..=100).contains(&self.upper) && self.lower <= self.upper
    }

    fn supported(&self, control: &ControlState) -> bool {
        self.valid()
            && (control.support2 & 64 != 0
                || self.lower.checked_add(5) == Some(self.upper)
                    && (matches!(
                        (self.upper, self.battery_mode & SELECTOR_MASK),
                        (60, 33) | (80, 17) | (100, 1)
                    ) || matches!(control.project, 16 | 26)
                        && matches!(self.upper, 60 | 80)
                        && self.battery_mode & SELECTOR_MASK == 1))
    }

    fn reset(&self) -> bool {
        // A known preset selector can coexist with zero thresholds. A checked
        // sample still needs a saved target or a device capability before writing.
        self.raw_upper() == 0
            && self.lower == 0
            && self.battery_mode & !(SELECTOR_MASK | TOUCHPAD_BIT) == 0
            && matches!(self.battery_mode & SELECTOR_MASK, 0 | 1 | 17 | 33)
    }

    fn desired(&self, upper: u8) -> Self {
        Self {
            upper,
            lower: upper - 5,
            battery_mode: (self.battery_mode & !SELECTOR_MASK)
                | match upper {
                    60 => 33,
                    80 => 17,
                    _ => 1,
                },
            battery_limit_reached: false,
        }
    }

    fn matches(&self, other: &Self, selector_can_revert: bool) -> bool {
        self.upper == other.upper
            && self.lower == other.lower
            && native_admission::battery_mode_matches(
                selector_can_revert,
                self.upper,
                self.lower,
                self.battery_mode,
                other.battery_mode,
            )
    }
    fn audit_sample(&self) -> Value {
        json!({"rawUpper":self.raw_upper(),"upper":self.upper,"lower":self.lower,
            "mode":self.battery_mode,"limitReached":self.battery_limit_reached})
    }

    fn audit_control(&self, control: &ControlState) -> Value {
        let mut value = json!(control);
        value["Upper"] = json!(self.upper);
        value["Lower"] = json!(self.lower);
        value["BatteryMode"] = json!(self.battery_mode);
        value["BatteryLimitReached"] = json!(self.battery_limit_reached);
        value
    }
}
fn legacy_target_percent(bytes: &[u8], before: &ControlState) -> Option<u8> {
    let mut selected = None;
    for line in bytes.split(|b| *b == b'\n') {
        let Ok(event) = serde_json::from_slice::<Value>(line) else {
            continue;
        };
        if event["operation"] != "control-confirmed" {
            continue;
        }
        let old = &event["state"]["before"];
        let new = &event["state"]["after"];
        if old["Upper"] == new["Upper"] && old["Lower"] == new["Lower"] {
            continue;
        }
        let percent = new["Upper"].as_u64().and_then(|n| u8::try_from(n).ok());
        let mode = new["BatteryMode"]
            .as_u64()
            .and_then(|n| u8::try_from(n).ok());
        if new["Project"] != before.project
            || new["Platform"] != before.platform
            || new["Support1"] != before.support1
            || new["Support2"] != before.support2
            || old["Project"] != new["Project"]
            || old["Platform"] != new["Platform"]
        {
            continue;
        }
        let (Some(percent @ (60 | 80 | 100)), Some(mode)) = (percent, mode) else {
            continue;
        };
        if new["Lower"] != percent - 5
            || mode & 49
                != match percent {
                    60 => 33,
                    80 => 17,
                    _ => 1,
                }
        {
            continue;
        }
        selected = Some(percent);
    }
    selected
}
impl HardwareSession {
    pub fn battery_limit(&self) -> Option<u8> {
        self.battery_state
            .as_ref()
            .filter(|s| s.valid())
            .map(|s| s.upper)
    }

    pub(super) fn update_battery_touchpad_bit(&mut self, raw: u8) {
        if let Some(state) = &mut self.battery_state {
            if (state.battery_mode ^ raw) & !TOUCHPAD_BIT == 0 {
                state.battery_mode = raw;
            } else {
                self.battery_state = None;
            }
        }
    }

    fn configured_battery_target(&self, control: &ControlState) -> Result<Option<Target>, String> {
        if let Some(target) = Target::read(&self.root)? {
            if !target.matches_device(control) {
                return Err("限充目标与当前设备、BIOS 或能力位不匹配".into());
            }
            return Ok(Some(target));
        }
        // Only an explicit, previously confirmed command may migrate.
        if !self.factory_record_matches_device(control)? {
            return Ok(None);
        }
        let Some(bytes) = bounded(&self.root, "hardware-audit.jsonl", 4 * 1024 * 1024)? else {
            return Ok(None);
        };
        let Some(percent) = legacy_target_percent(&bytes, control) else {
            return Ok(None);
        };
        let target = Target::for_device(control, percent)?;
        target.save(&self.root)?;
        self.audit(
            "battery-target-migrated",
            json!({"percent":percent,"source":"control-confirmed"}),
        )?;
        Ok(Some(target))
    }

    pub fn restore_battery_on_resume(&mut self) -> Receipt {
        self.synchronize_battery(true)
    }

    pub(super) fn synchronize_battery(&mut self, restore: bool) -> Receipt {
        if !self.active() || !self.classic() || self.battery_faulted {
            return Receipt::new(Outcome::Skipped);
        }
        let mut observed = None;
        let mut attempted = false;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.guard()?;
            let control = ControlState::read()?;
            self.check_root(&control)?;
            let before = BatteryState::read()?;
            observed = Some(before.audit_sample());
            self.battery_state = before.valid().then(|| before.clone());
            self.admission["capabilities"]["battery"] = json!(before.supported(&control));
            if before.valid() {
                // A live, valid limit may have been deliberately changed in another
                // control center. Leave it alone, as required by the recovery policy.
                return Ok(Outcome::Skipped);
            }
            if !before.reset() {
                return Err("限充快照不可用：阈值或模式不是受支持的复位状态".into());
            }
            let target = self.configured_battery_target(&control)?;
            self.admission["capabilities"]["battery"] =
                json!(target.is_some() || control.support2 & 64 != 0);
            if !restore {
                return Ok(Outcome::Skipped);
            }
            let Some(target) = target else {
                if control.support2 & 64 != 0 {
                    let _ = self.audit("battery-no-saved-target", json!({"observed":observed}));
                    return Ok(Outcome::Skipped);
                }
                return Err("限充阈值已复位，缺少同设备已确认的限充设置".into());
            };
            self.apply_battery(&control, &before, target.percent, true, &mut attempted)?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        self.finish_battery(result, attempted, observed)
    }

    pub(super) fn set_classic_battery(&mut self, percent: u8) -> Receipt {
        let mut observed = None;
        let mut attempted = false;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.ready("battery")?;
            let control = ControlState::read()?;
            self.check_root(&control)?;
            let before = BatteryState::read()?;
            observed = Some(before.audit_sample());
            if self.battery_state.as_ref().is_some_and(|last| {
                !last.matches(
                    &before,
                    native_admission::battery_selector_can_revert(
                        control.project,
                        Some(control.platform),
                    ),
                )
            }) {
                return Err("限充状态已变化，请重新接管读取限充".into());
            }
            let supported = before.supported(&control)
                || before.reset()
                    && (control.support2 & 64 != 0
                        || self.configured_battery_target(&control)?.is_some());
            if !supported {
                return Err("当前限充状态不符合受支持的控制协议".into());
            }
            self.apply_battery(&control, &before, percent, false, &mut attempted)?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        let mut receipt = self.finish_battery(result, attempted, observed);
        if matches!(receipt.outcome, Outcome::ReadbackConfirmed) {
            if let Err(error) = self
                .state
                .as_ref()
                .ok_or_else(|| "缺少接管快照".to_string())
                .and_then(|control| Target::for_device(control, percent))
                .and_then(|target| target.save(&self.root))
            {
                let _ = self.audit("battery-target-save-failed", json!({"error":error}));
                receipt.warning = Some("限充已回读确认，但无法保存目标供下次恢复".into());
            }
        }
        receipt
    }

    // Caller owns the existing operation queue and operation lock.
    fn apply_battery(
        &mut self,
        control: &ControlState,
        before: &BatteryState,
        percent: u8,
        restoring: bool,
        attempted: &mut bool,
    ) -> Result<(), String> {
        let desired = before.desired(percent);
        let selector_can_revert =
            native_admission::battery_selector_can_revert(control.project, Some(control.platform));
        if !before.matches(&BatteryState::read()?, selector_can_revert) {
            return Err("限充写入前电池状态已变化".into());
        }
        let current = ControlState::read()?;
        self.check_root(&current)?;
        if (current.support1, current.support2) != (control.support1, control.support2) {
            return Err("限充写入前设备能力已变化".into());
        }
        let event = if restoring {
            "battery-reset-restore-intent"
        } else {
            "battery-control-intent"
        };
        self.audit(event, json!({"before":before.audit_control(control),"desired":desired.audit_control(control)}))?;
        self.guard()?;
        self.authorized()?;
        *attempted = true;
        native_admission::apply_classic_transaction(
            json!({"kind":"battery","project":control.project,
            "oldUpper":before.raw_upper(),"oldLower":before.lower,"oldMode":before.battery_mode,
            "upper":desired.upper,"lower":desired.lower,"mode":desired.battery_mode}),
        )?;
        let after = BatteryState::read()?;
        let after_control = ControlState::read()?;
        self.check_root(&after_control)?;
        if !desired.matches(&after, selector_can_revert)
            || (after_control.support1, after_control.support2)
                != (control.support1, control.support2)
        {
            return Err(format!("限充回读未确认：{}", after.audit_sample()));
        }
        self.state = Some(after_control.clone());
        self.battery_state = Some(after.clone());
        self.admission["capabilities"]["battery"] = json!(after.supported(&after_control));
        let event = if restoring {
            "battery-reset-restore-confirmed"
        } else {
            "control-confirmed"
        };
        self.audit(event, json!({"before":before.audit_control(control),"after":after.audit_control(&after_control)}))?;
        Ok(())
    }

    pub(super) fn finish_battery(
        &mut self,
        result: Result<Outcome, String>,
        attempted: bool,
        observed: Option<Value>,
    ) -> Receipt {
        match result {
            Ok(outcome) => Receipt::new(outcome),
            Err(error) => {
                self.battery_state = None;
                if attempted {
                    self.battery_faulted = true;
                }
                self.admission["capabilities"]["battery"] = json!(false);
                let _ = self.audit(
                    "battery-unavailable",
                    json!({"error":error,"observed":observed,"attemptedWrite":attempted}),
                );
                if attempted {
                    Receipt::error(
                        Outcome::Unknown,
                        format!("限充可能部分生效，请重新接管读取限充。{error}"),
                    )
                } else {
                    Receipt::error(Outcome::Rejected, error)
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn control() -> ControlState {
        ControlState {
            project: 16,
            fan_clamp: 0,
            platform: 0,
            support1: 161,
            support2: 148,
            power_status: 0,
        }
    }

    fn zero(mode: u8) -> BatteryState {
        BatteryState {
            upper: 0,
            lower: 0,
            battery_mode: mode,
            battery_limit_reached: false,
        }
    }

    #[test]
    fn reset_with_known_selector_does_not_change_root_admission() {
        for mode in [0, 1, 8, 9, 17, 25, 33, 41] {
            assert!(zero(mode).reset());
            assert!(!zero(mode).valid());
            assert!(!zero(mode).supported(&control()));
        }
        for mode in [2, 32, 49, 64, 255] {
            assert!(!zero(mode).reset());
        }
        let mut battery = zero(9);
        battery.battery_limit_reached = true;
        assert!(!battery.reset());
        battery.lower = 55;
        assert!(!battery.reset());
        assert!(control().validate().is_ok());
        let mut unsafe_platform = control();
        unsafe_platform.platform = 4;
        assert!(unsafe_platform.validate().is_err());
    }

    #[test]
    fn desired_preset_preserves_unrelated_bits_and_separates_reached_flag() {
        let mut battery = zero(9);
        for (upper, expected_mode) in [(60, 41), (80, 25), (100, 9)] {
            let desired = battery.desired(upper);
            assert_eq!(
                (desired.upper, desired.lower, desired.battery_mode),
                (upper, upper - 5, expected_mode)
            );
            let mut reached = desired.clone();
            reached.battery_limit_reached = true;
            assert!(desired.matches(&reached, false));
            assert_eq!(reached.raw_upper(), upper | 128);
            assert!(desired.supported(&control()));
        }
        battery.battery_mode = 0x89;
        assert_eq!(battery.desired(60).battery_mode, 0xa9);
        assert!(!battery.reset());
    }

    #[test]
    fn selector_reversion_keeps_thresholds_and_unrelated_bits_exact() {
        for upper in [60, 80] {
            let preset = zero(8).desired(upper);
            let mut fresh = preset.clone();
            fresh.battery_mode = 9;
            assert!(preset.matches(&fresh, true));
            assert!(fresh.matches(&preset, true));
            assert!(!preset.matches(&fresh, false));
            fresh.battery_mode = 1;
            assert!(!preset.matches(&fresh, true));
            fresh.battery_mode = 9;
            fresh.lower -= 1;
            assert!(!preset.matches(&fresh, true));
        }
    }

    fn event(old: u8, new: u8, project: u8, mode: u8) -> String {
        json!({"operation":"control-confirmed","state":{
            "before":{"Upper":old,"Lower":old.saturating_sub(5),"Project":project,"Platform":0},
            "after":{"Upper":new,"Lower":new.saturating_sub(5),"BatteryMode":mode,
                "Project":project,"Platform":0,"Support1":161,"Support2":148}}})
        .to_string()
    }

    #[test]
    fn legacy_migration_ignores_fan_commands_other_devices_and_invalid_targets() {
        let log = [
            event(80, 60, 16, 41),
            event(60, 60, 16, 41),
            event(60, 100, 26, 1),
            event(60, 0, 16, 0),
            event(60, 80, 16, 0),
        ]
        .join("\n");
        assert_eq!(legacy_target_percent(log.as_bytes(), &control()), Some(60));
        let log = format!("{log}\n{}", event(60, 80, 16, 25));
        assert_eq!(legacy_target_percent(log.as_bytes(), &control()), Some(80));
        assert_eq!(
            legacy_target_percent(b"incomplete log\n{}", &control()),
            None
        );
    }
}
