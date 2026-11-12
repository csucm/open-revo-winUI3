use super::{
    hardware::{HardwareSession, Outcome, Receipt},
    platform::{ac_online, OperationLock},
};
use openrevo_core::system::{config_io, native_services};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    path::Path,
    time::{Duration, Instant},
};
use windows::core::GUID;
#[derive(Default, Clone, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct DesktopPreferences {
    pub sleep_office: bool,
    pub performance_link: bool,
}
impl DesktopPreferences {
    pub fn load(directory: &Path) -> Self {
        let path = directory.join("desktop-services.json");
        if std::fs::metadata(&path).is_ok_and(|m| m.len() <= 8192) {
            std::fs::read(path)
                .ok()
                .and_then(|b| serde_json::from_slice(&b).ok())
                .unwrap_or_default()
        } else {
            Self::default()
        }
    }
    pub fn save(&self, directory: &Path) -> Result<(), String> {
        config_io::atomic_write_with_retry(
            &directory.join("desktop-services.json"),
            &serde_json::to_vec(self).map_err(|e| e.to_string())?,
        )
    }
}
#[derive(Default)]
pub struct Overlay {
    confirmed: u8,
    failed: u8,
    failures: u8,
    retry_after: Option<Instant>,
}
impl Overlay {
    pub fn due(&self, mode: u8) -> bool {
        mode != 0
            && mode != self.confirmed
            && (mode != self.failed || self.retry_after.is_none_or(|at| Instant::now() >= at))
    }
    fn apply(&mut self, mode: u8) -> Result<(), String> {
        self.confirmed = 0;
        let _lock = OperationLock::enter()?;
        super::platform::check_conflicts()?;
        native_services::execute(3, json!({"mode":mode}))?;
        let expected = match mode {
            1 => GUID::from_u128(0x961cc777_2547_4f9d_8174_7d86181b8a7a),
            2 | 4 => GUID::zeroed(),
            3 => GUID::from_u128(0xded574b5_45a0_4f42_8737_46345c09c238),
            _ => return Err("无效档位".into()),
        };
        let mut actual = GUID::zeroed();
        let library = super::platform::system_library("powrprof.dll")?;
        type Query = unsafe extern "system" fn(*mut GUID) -> u32;
        let query = unsafe { library.get::<Query>(b"PowerGetEffectiveOverlayScheme\0") }
            .map_err(|e| e.to_string())?;
        let code = unsafe { query(&mut actual) };
        if code != 0 || actual != expected {
            return Err(format!("Windows 性能策略回读未确认 ({code})"));
        }
        self.confirmed = mode;
        self.failed = 0;
        self.failures = 0;
        self.retry_after = None;
        Ok(())
    }
    pub fn sync(&mut self, mode: u8) -> Receipt {
        if !self.due(mode) {
            return Receipt::new(Outcome::Skipped);
        }
        match self.apply(mode) {
            Ok(()) => Receipt {
                mode: Some(mode),
                ..Receipt::new(Outcome::ReadbackConfirmed)
            },
            Err(error) => {
                self.confirmed = 0;
                self.failures = if self.failed == mode {
                    (self.failures + 1).min(3)
                } else {
                    1
                };
                self.failed = mode;
                self.retry_after = Some(
                    Instant::now()
                        + Duration::from_secs(match self.failures {
                            1 => 3,
                            2 => 15,
                            _ => 60,
                        }),
                );
                Receipt::error(Outcome::Unknown, error)
            }
        }
    }
}
#[derive(Default)]
pub struct Policies {
    pub settings: DesktopPreferences,
    pub overlay: Overlay,
    suspended: bool,
    restored_on_ac: bool,
    last_battery: Option<Instant>,
}
impl Policies {
    pub fn load(directory: &Path) -> Self {
        Self {
            settings: DesktopPreferences::load(directory),
            ..Self::default()
        }
    }
    pub fn observe_sleep(&mut self, hardware: &mut HardwareSession, sleeping: bool) -> Receipt {
        if self.suspended != sleeping {
            self.suspended = sleeping;
            self.restored_on_ac = false;
        }
        let receipt = hardware.sleep_office(sleeping, self.settings.sleep_office);
        if matches!(receipt.outcome, Outcome::ReadbackConfirmed) && self.settings.performance_link {
            if let Some(mode) = hardware.mode {
                let _ = self.overlay.sync(mode);
            }
        }
        receipt
    }
    pub fn reset_session(&mut self) {
        self.restored_on_ac = false;
        self.last_battery = None;
    }
    pub fn evaluate(&mut self, hardware: &mut HardwareSession, preferences: &Value) {
        if !hardware.active() {
            self.restored_on_ac = false;
            return;
        }
        if self.settings.performance_link {
            if let Some(mode) = hardware.mode {
                let receipt = self.overlay.sync(mode);
                if !matches!(receipt.outcome, Outcome::Skipped) {
                    let _ = hardware.audit("resident.power-link", json!(receipt));
                }
            }
        }
        if !hardware.allow_battery
            && matches!(hardware.mode, Some(3 | 4))
            && self
                .last_battery
                .is_none_or(|at| at.elapsed() >= Duration::from_secs(3))
        {
            self.last_battery = Some(Instant::now());
            let original = if hardware.classic() {
                match ac_online() {
                    Some(true) => openrevo_core::driver::wmi_ec::wmi_read_ec(1996)
                        .map(|p| p & 62 == 0 || p & 1 != 0),
                    other => other,
                }
            } else {
                ac_online()
            };
            if original == Some(false) {
                let receipt = hardware.enforce_battery_guard();
                let _ = hardware.audit("resident.battery-guard", json!(receipt));
            }
        }
        let ac = ac_online();
        if !self.suspended && ac == Some(false) {
            self.restored_on_ac = false;
        }
        if !self.suspended
            && ac == Some(true)
            && !self.restored_on_ac
            && hardware.admission["declared_tuning"].is_object()
        {
            self.restored_on_ac = true;
            if preferences["DeclaredTuning"]["RestoreOnAc"] == true {
                let receipt = hardware.apply_declared(&preferences["DeclaredTuning"]);
                let _ = hardware.audit("resident.tuning-restore", json!(receipt));
            }
        }
    }
    pub fn edit(
        &mut self,
        hardware: &mut HardwareSession,
        directory: &Path,
        link: bool,
        enabled: bool,
    ) -> Receipt {
        if let Err(error) = super::platform::check_conflicts() {
            return Receipt::error(Outcome::Rejected, error);
        }
        if !link && !hardware.active() {
            return Receipt::error(Outcome::Rejected, "请先连接设备");
        }
        if link && enabled {
            let Some(mode) = hardware.mode else {
                return Receipt::error(Outcome::Rejected, "性能档位未读取");
            };
            if let Err(error) = self.overlay.apply(mode) {
                return Receipt::error(Outcome::Unknown, error);
            }
        }
        let mut next = self.settings.clone();
        if link {
            next.performance_link = enabled;
        } else {
            next.sleep_office = enabled;
        }
        match next.save(directory) {
            Ok(()) => {
                self.settings = next;
                if link && !enabled {
                    self.overlay = Overlay::default();
                }
                Receipt::new(Outcome::ConfigurationSaved)
            }
            Err(error) => Receipt::error(Outcome::ConfigurationSaveFailed, error),
        }
    }
}
