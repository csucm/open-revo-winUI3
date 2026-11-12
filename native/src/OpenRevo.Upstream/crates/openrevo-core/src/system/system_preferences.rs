use super::config_io::{atomic_write_with_retry, DATA_IO};
use serde::{Deserialize, Serialize};
use std::borrow::Cow;
use std::fs::{self, Metadata, OpenOptions};
use std::io::{self, Read};
use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
use std::path::Path;

pub const MAX_SYSTEM_PREFERENCES_BYTES: usize = 8192;
const FILE_NAME: &str = "system-settings.json";
const REPARSE_POINT: u32 = 0x400;
const OPEN_REPARSE_POINT: u32 = 0x00200000;

#[derive(Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct SystemPreferences {
    pub wake_mode: String,
    pub mini_auto_hide: bool,
    pub last_mini: bool,
    pub unlock_battery_limit: bool,
    pub osd_theme: String,
    pub osd_scale: i32,
    pub osd_position: String,
    pub log_level: i32,
    pub log_filter: String,
    pub motion_speed: String,
    pub tray_telemetry: bool,
}

impl Default for SystemPreferences {
    fn default() -> Self {
        Self {
            wake_mode: "remember".into(),
            mini_auto_hide: true,
            last_mini: false,
            unlock_battery_limit: false,
            osd_theme: "cinema".into(),
            osd_scale: 100,
            osd_position: "top-left".into(),
            log_level: 1,
            log_filter: String::new(),
            motion_speed: "normal".into(),
            tray_telemetry: false,
        }
    }
}

pub enum SystemPreferencesSaveError {
    Invalid,
    Io(String),
}

#[derive(Deserialize)]
#[serde(tag = "Kind", rename_all_fields = "PascalCase", deny_unknown_fields)]
pub enum SystemPreferenceEdit {
    WakeMode {
        value: String,
    },
    MiniAutoHide {
        value: bool,
    },
    LastMini {
        value: bool,
    },
    UnlockBatteryLimit {
        value: bool,
    },
    TrayTelemetry {
        value: bool,
    },
    OsdAppearance {
        theme: String,
        scale: i32,
        position: String,
        motion: String,
    },
    Logging {
        level: i32,
        filter: String,
    },
}

impl SystemPreferenceEdit {
    fn apply(self, settings: &mut SystemPreferences) {
        match self {
            Self::WakeMode { value } => settings.wake_mode = value,
            Self::MiniAutoHide { value } => settings.mini_auto_hide = value,
            Self::LastMini { value } => settings.last_mini = value,
            Self::UnlockBatteryLimit { value } => settings.unlock_battery_limit = value,
            Self::TrayTelemetry { value } => settings.tray_telemetry = value,
            Self::OsdAppearance {
                theme,
                scale,
                position,
                motion,
            } => {
                settings.osd_theme = theme;
                settings.osd_scale = scale;
                settings.osd_position = position;
                settings.motion_speed = motion;
            }
            Self::Logging { level, filter } => {
                settings.log_level = level;
                settings.log_filter = filter;
            }
        }
    }
}

impl SystemPreferences {
    /// The host passes its verified Open-Revo data directory.
    pub fn load(directory: &Path) -> Self {
        let _io = DATA_IO.lock();
        Self::read_locked(&directory.join(FILE_NAME))
            .ok()
            .flatten()
            .unwrap_or_default()
    }

    /// Validate and commit one edit against the latest file under the same lock.
    pub fn apply_edit(
        directory: &Path,
        edit: SystemPreferenceEdit,
    ) -> Result<(Self, Vec<u8>), SystemPreferencesSaveError> {
        let _io = DATA_IO.lock();
        let path = directory.join(FILE_NAME);
        let mut settings = Self::read_locked(&path)
            .map_err(|error| SystemPreferencesSaveError::Io(error.to_string()))?
            .unwrap_or_default();
        edit.apply(&mut settings);
        let bytes = settings.save_locked(directory, &path)?;
        Ok((settings, bytes))
    }

    fn read_locked(path: &Path) -> io::Result<Option<Self>> {
        let file = match OpenOptions::new()
            .read(true)
            .custom_flags(OPEN_REPARSE_POINT)
            .open(path)
        {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        let metadata = file.metadata()?;
        if !is_regular(&metadata) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "系统设置路径不是普通文件。",
            ));
        }
        if metadata.len() > MAX_SYSTEM_PREFERENCES_BYTES as u64 {
            return Ok(None);
        }
        let mut bytes = Vec::new();
        file.take(MAX_SYSTEM_PREFERENCES_BYTES as u64 + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() > MAX_SYSTEM_PREFERENCES_BYTES {
            return Ok(None);
        }
        Ok(decode_file(&bytes).and_then(|decoded| Self::from_json(decoded.as_bytes())))
    }

    pub fn from_json(bytes: &[u8]) -> Option<Self> {
        let value: serde_json::Value = serde_json::from_slice(bytes).ok()?;
        if !value.is_object() {
            return None;
        }
        let settings: Self = serde_json::from_value(value).ok()?;
        settings.valid().then_some(settings)
    }

    pub fn save(&self, directory: &Path) -> Result<(), SystemPreferencesSaveError> {
        let _io = DATA_IO.lock();
        self.save_locked(directory, &directory.join(FILE_NAME))
            .map(|_| ())
    }

    fn save_locked(
        &self,
        directory: &Path,
        path: &Path,
    ) -> Result<Vec<u8>, SystemPreferencesSaveError> {
        use SystemPreferencesSaveError::{Invalid, Io};
        if !self.valid() {
            return Err(Invalid);
        }
        let bytes = serde_json::to_vec(self).map_err(|_| Invalid)?;
        if bytes.len() > MAX_SYSTEM_PREFERENCES_BYTES {
            return Err(Invalid);
        }
        fs::create_dir_all(directory).map_err(|error| Io(error.to_string()))?;
        check_destination(path).map_err(|error| Io(error.to_string()))?;
        atomic_write_with_retry(path, &bytes).map_err(Io)?;
        Ok(bytes)
    }

    fn valid(&self) -> bool {
        matches!(self.wake_mode.as_str(), "remember" | "mini" | "full")
            && matches!(self.osd_theme.as_str(), "cinema" | "fade" | "pill")
            && matches!(self.osd_scale, 80 | 100 | 120 | 140)
            && matches!(
                self.osd_position.as_str(),
                "top-left" | "bottom-center" | "center"
            )
            && (0..=2).contains(&self.log_level)
            && self.log_filter.encode_utf16().count() <= 100
            && matches!(
                self.motion_speed.as_str(),
                "fast" | "normal" | "slow" | "off"
            )
    }
}

fn is_regular(metadata: &Metadata) -> bool {
    metadata.is_file() && metadata.file_attributes() & REPARSE_POINT == 0
}

fn check_destination(path: &Path) -> io::Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if is_regular(&metadata) => Ok(()),
        Ok(_) => Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "系统设置路径不是普通文件。",
        )),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
    }
}

// Existing readers accept the UTF BOMs recognized by File.ReadAllText.
pub(super) fn decode_file(bytes: &[u8]) -> Option<Cow<'_, str>> {
    let utf32 = bytes
        .strip_prefix(&[0xff, 0xfe, 0, 0])
        .map(|rest| (rest, true))
        .or_else(|| {
            bytes
                .strip_prefix(&[0, 0, 0xfe, 0xff])
                .map(|rest| (rest, false))
        });
    if let Some((data, little)) = utf32 {
        if data.len() % 4 != 0 {
            return None;
        }
        return Some(Cow::Owned(
            data.chunks_exact(4)
                .map(|chunk| {
                    let unit = [chunk[0], chunk[1], chunk[2], chunk[3]];
                    let code = if little {
                        u32::from_le_bytes(unit)
                    } else {
                        u32::from_be_bytes(unit)
                    };
                    char::from_u32(code).unwrap_or(char::REPLACEMENT_CHARACTER)
                })
                .collect(),
        ));
    }
    let utf16 = bytes
        .strip_prefix(&[0xff, 0xfe])
        .map(|rest| (rest, true))
        .or_else(|| bytes.strip_prefix(&[0xfe, 0xff]).map(|rest| (rest, false)));
    if let Some((data, little)) = utf16 {
        if data.len() % 2 != 0 {
            return None;
        }
        let units: Vec<_> = data
            .chunks_exact(2)
            .map(|chunk| {
                let unit = [chunk[0], chunk[1]];
                if little {
                    u16::from_le_bytes(unit)
                } else {
                    u16::from_be_bytes(unit)
                }
            })
            .collect();
        return Some(Cow::Owned(String::from_utf16_lossy(&units)));
    }
    Some(String::from_utf8_lossy(
        bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes),
    ))
}
