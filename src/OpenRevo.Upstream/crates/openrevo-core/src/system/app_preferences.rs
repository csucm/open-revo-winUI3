use super::{
    config_io::{atomic_write_with_retry, DATA_IO},
    system_preferences::decode_file,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    fs::{self, OpenOptions},
    io::{self, Read},
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::Path,
};

pub const MAX_FILE_BYTES: usize = 4096;
// BOM decoding can expand a bounded file when the snapshot is returned as UTF-8.
pub const SNAPSHOT_CAPACITY: usize = 16384;
const FILE_NAME: &str = "preferences.json";
const REPARSE_POINT: u32 = 0x400;
const OPEN_REPARSE_POINT: u32 = 0x00200000;

#[derive(Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct AppPreferences {
    pub theme: i32,
    pub sample_seconds: i32,
    pub start_mini: bool,
    pub low_refresh_on_battery: bool,
    pub requested_gpu: i32,
    pub gpu_saved_at_unix: i64,
    pub osd_enabled: bool,
    pub selected_tuning_preset: Option<String>,
    declared_tuning: Option<DeclaredTuningPreset>,
    cpu_power_ceilings: Option<CpuPowerCeilings>,
}

#[derive(Default, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
struct DeclaredTuningPreset {
    manifest: Option<String>,
    pl1: i32,
    pl2: i32,
    temperature: i32,
    custom_fans: bool,
    cpu_curve: Option<Vec<Option<FanPoint>>>,
    gpu_curve: Option<Vec<Option<FanPoint>>>,
    restore_on_ac: bool,
}

#[derive(Default, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
struct FanPoint {
    celsius: i32,
    percent: i32,
}

#[derive(Default, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
struct CpuPowerCeilings {
    pl1: Option<i32>,
    pl2: Option<i32>,
    pl4: Option<i32>,
}

impl Default for AppPreferences {
    fn default() -> Self {
        Self {
            theme: 1,
            sample_seconds: 2,
            start_mini: true,
            low_refresh_on_battery: false,
            requested_gpu: 0,
            gpu_saved_at_unix: 0,
            osd_enabled: true,
            selected_tuning_preset: None,
            declared_tuning: None,
            cpu_power_ceilings: None,
        }
    }
}

#[derive(Deserialize)]
#[serde(tag = "Kind", rename_all_fields = "PascalCase", deny_unknown_fields)]
pub enum AppPreferenceEdit {
    Theme { value: i32 },
    SampleSeconds { value: i32 },
    OsdEnabled { value: bool },
}

pub enum SaveError {
    Invalid,
    Io(String),
}

impl AppPreferences {
    pub fn load_snapshot(directory: &Path, strict: bool) -> Result<Vec<u8>, SaveError> {
        let _io = DATA_IO.lock();
        let document = match Self::read_locked(&directory.join(FILE_NAME)) {
            Ok(value) => value,
            Err(error) if strict => return Err(SaveError::Io(error.to_string())),
            Err(_) => None,
        };
        match document {
            Some((bytes, _)) => Ok(bytes),
            None => serde_json::to_vec(&Self::default()).map_err(|_| SaveError::Invalid),
        }
    }

    pub fn save_snapshot(directory: &Path, bytes: &[u8]) -> Result<(), SaveError> {
        let _io = DATA_IO.lock();
        if Self::parse_document(bytes).is_none() {
            return Err(SaveError::Invalid);
        }
        Self::save_locked(&directory.join(FILE_NAME), bytes)
    }

    pub fn apply_edit(directory: &Path, edit: AppPreferenceEdit) -> Result<Vec<u8>, SaveError> {
        let _io = DATA_IO.lock();
        let path = directory.join(FILE_NAME);
        let mut document =
            match Self::read_locked(&path).map_err(|error| SaveError::Io(error.to_string()))? {
                Some((_, document)) => document,
                None => serde_json::to_value(Self::default()).map_err(|_| SaveError::Invalid)?,
            };
        let (key, value) = match edit {
            AppPreferenceEdit::Theme { value } if (0..=2).contains(&value) => {
                ("Theme", Value::from(value))
            }
            AppPreferenceEdit::SampleSeconds { value } if matches!(value, 2 | 5 | 10) => {
                ("SampleSeconds", Value::from(value))
            }
            AppPreferenceEdit::OsdEnabled { value } => ("OsdEnabled", Value::from(value)),
            _ => return Err(SaveError::Invalid),
        };
        document
            .as_object_mut()
            .ok_or(SaveError::Invalid)?
            .insert(key.to_owned(), value);
        let bytes = serde_json::to_vec(&document).map_err(|_| SaveError::Invalid)?;
        Self::save_locked(&path, &bytes)?;
        Ok(bytes)
    }

    fn parse_document(bytes: &[u8]) -> Option<Value> {
        let raw: Value = serde_json::from_slice(bytes).ok()?;
        if !raw.is_object() || !supported_depth(&raw, 0) {
            return None;
        }
        let value = Self::deserialize(&raw).ok()?;
        value.valid().then_some(raw)
    }

    fn valid(&self) -> bool {
        (0..=2).contains(&self.theme)
            && matches!(self.sample_seconds, 2 | 5 | 10)
            && (0..=3).contains(&self.requested_gpu)
            && self.gpu_saved_at_unix >= 0
    }

    fn read_locked(path: &Path) -> io::Result<Option<(Vec<u8>, Value)>> {
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
        require_regular(&metadata)?;
        if metadata.len() > MAX_FILE_BYTES as u64 {
            return Ok(None);
        }
        let mut bytes = Vec::new();
        file.take(MAX_FILE_BYTES as u64 + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() > MAX_FILE_BYTES {
            return Ok(None);
        }
        Ok(decode_file(&bytes).and_then(|text| {
            let document = Self::parse_document(text.as_bytes())?;
            Some((text.into_owned().into_bytes(), document))
        }))
    }

    fn save_locked(path: &Path, bytes: &[u8]) -> Result<(), SaveError> {
        if bytes.len() > MAX_FILE_BYTES {
            return Err(SaveError::Invalid);
        }
        let directory = path.parent().ok_or(SaveError::Invalid)?;
        let io_error = |error: io::Error| SaveError::Io(error.to_string());
        fs::create_dir_all(directory).map_err(io_error)?;
        match fs::symlink_metadata(path) {
            Ok(metadata) => require_regular(&metadata).map_err(io_error)?,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(io_error(error)),
        }
        atomic_write_with_retry(path, bytes).map_err(SaveError::Io)
    }
}

fn supported_depth(value: &Value, depth: usize) -> bool {
    match value {
        Value::Array(items) => {
            depth < 64 && items.iter().all(|item| supported_depth(item, depth + 1))
        }
        Value::Object(fields) => {
            depth < 64 && fields.values().all(|item| supported_depth(item, depth + 1))
        }
        _ => true,
    }
}

fn require_regular(metadata: &fs::Metadata) -> io::Result<()> {
    if !metadata.is_file() || metadata.file_attributes() & REPARSE_POINT != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "应用设置路径不是普通文件。",
        ));
    }
    Ok(())
}
