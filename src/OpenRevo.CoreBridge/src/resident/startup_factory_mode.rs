use super::{ControlState, HardwareSession, Outcome, PerformanceState, Receipt};
use crate::resident::platform::{bios_string, validate_directory};
use openrevo_core::system::config_io;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{
    fs::OpenOptions,
    io::{ErrorKind, Read},
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::Path,
};

const FILE_NAME: &str = "factory-mode.json";
const MAX_BYTES: u64 = 4096;

#[derive(Deserialize, PartialEq, Serialize)]
struct Device {
    board: String,
    product: String,
    bios: String,
    project: u8,
    platform: u8,
}

impl Device {
    fn read(control: &ControlState) -> Option<Self> {
        Some(Self {
            board: bios_string("BaseBoardProduct").filter(|s| !s.trim().is_empty())?,
            product: bios_string("SystemProductName").filter(|s| !s.trim().is_empty())?,
            bios: bios_string("BIOSVersion").filter(|s| !s.trim().is_empty())?,
            project: control.project,
            platform: control.platform,
        })
    }
}

#[derive(Deserialize, Serialize)]
struct Selection {
    schema: u8,
    device: Device,
    mode: Option<u8>,
}

impl Selection {
    fn read(root: &Path) -> Result<Option<Self>, String> {
        validate_directory(root)?;
        let file = match OpenOptions::new()
            .read(true)
            .custom_flags(0x00200000) // FILE_FLAG_OPEN_REPARSE_POINT
            .open(root.join(FILE_NAME))
        {
            Ok(file) => file,
            Err(e) if e.kind() == ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e.to_string()),
        };
        let metadata = file.metadata().map_err(|e| e.to_string())?;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            return Err("档位记录不是普通文件".into());
        }
        let mut bytes = Vec::new();
        file.take(MAX_BYTES + 1)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        if bytes.len() as u64 > MAX_BYTES {
            return Err("档位记录超过大小限制".into());
        }
        let record: Self = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        if record.schema != 1 || record.mode.is_some_and(|mode| !(1..=3).contains(&mode)) {
            return Err("档位记录无效".into());
        }
        Ok(Some(record))
    }
}

impl HardwareSession {
    pub(super) fn remember_factory_mode(&self, mode: Option<u8>, receipt: &mut Receipt) {
        let Some(control) = self.state.as_ref() else {
            return;
        };
        let result = (|| {
            validate_directory(&self.root)?;
            let device = Device::read(control).ok_or("无法确认设备身份")?;
            let record = Selection {
                schema: 1,
                device,
                mode,
            };
            let bytes = serde_json::to_vec(&record).map_err(|e| e.to_string())?;
            config_io::atomic_write_with_retry(&self.root.join(FILE_NAME), &bytes)
        })();
        if let Err(error) = result {
            let _ = self.audit(
                "factory-mode-save-failed",
                json!({"mode":mode,"error":error}),
            );
            let warning = "档位已回读确认，但重启档位记录保存失败";
            receipt.warning = Some(match receipt.warning.take() {
                Some(existing) => format!("{existing}。{warning}"),
                None => warning.into(),
            });
        }
    }

    pub fn restore_factory_mode(&mut self) -> Option<Receipt> {
        if !self.supports("performance") {
            return None;
        }
        let (Some(control), Some(before)) = (self.state.as_ref(), self.performance.as_ref()) else {
            return None;
        };
        let record = match Selection::read(&self.root) {
            Ok(record) => record?,
            Err(error) => return Some(Receipt::error(Outcome::Rejected, error)),
        };
        let mode = record.mode?;
        if Device::read(control).as_ref() != Some(&record.device) {
            return Some(Receipt::error(
                Outcome::Rejected,
                "档位记录与接管设备不匹配",
            ));
        }
        let current = before.mode(Some(control));
        if current == 4 || (current == mode && before.recognized(true)) {
            return Some(Receipt::new(Outcome::Skipped));
        }
        let reset = boot_tables_cleared(before);
        if !reset && !before.recognized(false) {
            return Some(Receipt::error(Outcome::Rejected, "启动风扇表状态未识别"));
        }
        // An installed Core restores once at automatic takeover. The live write
        // rechecks the captured state, hardware admission, and the DC power gate.
        Some(self.change_performance(mode, true, Some(before.clone()), None, reset, false))
    }
}

fn boot_tables_cleared(before: &PerformanceState) -> bool {
    before.custom == 64
        && before.latch == 0
        && before.ap & 129 == 0
        && before.isolated == 0
        && before.tables.iter().all(|&value| value == 0)
}
