use super::hardware::{Outcome, Receipt};
use openrevo_core::system::{
    app_preferences::{AppPreferenceEdit, AppPreferences},
    config_io,
    system_preferences::{SystemPreferenceEdit, SystemPreferences},
};
use serde_json::{json, Value};
use std::{
    fs,
    io::Read,
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::Path,
};
const OVERVIEW: [&str; 7] = [
    "cpu",
    "gpu",
    "vram",
    "fan",
    "performance",
    "battery",
    "display",
];
const TOP: [&str; 6] = [
    "performance",
    "gpu",
    "refresh",
    "brightness",
    "keyboard",
    "battery",
];
const QUICK: [&str; 9] = [
    "touchpad", "win", "fn", "camera", "mic", "osd", "boost", "awake", "screen",
];
fn default_mini() -> Value {
    json!({"Top":TOP,"Quick":["boost","camera","mic","win","awake","screen"]})
}
fn read_layout(path: &Path) -> Result<Option<(Vec<u8>, Value)>, String> {
    let mut file = match fs::OpenOptions::new()
        .read(true)
        .custom_flags(0x00200000)
        .open(path)
    {
        Ok(f) => f,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    let metadata = file.metadata().map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
        return Err("布局路径不是普通文件".into());
    }
    if metadata.len() > 8192 {
        return Err("布局文件超过8192字节".into());
    }
    let mut bytes = Vec::new();
    file.by_ref()
        .take(8193)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > 8192 {
        return Err("布局文件超过8192字节".into());
    }
    let value = if bytes.starts_with(&[0xff, 0xfe]) || bytes.starts_with(&[0xfe, 0xff]) {
        if bytes.len() % 2 != 0 {
            return Err("布局文本编码无效".into());
        }
        let little = bytes[0] == 0xff;
        let words: Vec<u16> = bytes[2..]
            .chunks_exact(2)
            .map(|c| {
                if little {
                    u16::from_le_bytes([c[0], c[1]])
                } else {
                    u16::from_be_bytes([c[0], c[1]])
                }
            })
            .collect();
        let text = String::from_utf16(&words).map_err(|e| e.to_string())?;
        serde_json::from_str(&text)
    } else {
        serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes))
    }
    .map_err(|_| "布局格式无法识别")?;
    Ok(Some((bytes, value)))
}
fn normalize_overview(value: &Value) -> Vec<String> {
    let mut next = Vec::new();
    if let Some(items) = value.as_array() {
        for id in items.iter().filter_map(Value::as_str) {
            if OVERVIEW.contains(&id) && !next.iter().any(|s| s == id) {
                next.push(id.to_owned());
            }
        }
    } else {
        next = OVERVIEW.iter().map(|s| (*s).into()).collect();
    }
    next
}
fn valid_mini(value: &Value) -> bool {
    let Some(top) = value["Top"].as_array().filter(|a| a.len() <= 6) else {
        return false;
    };
    let Some(quick) = value["Quick"].as_array().filter(|a| a.len() == 6) else {
        return false;
    };
    let mut seen = std::collections::HashSet::new();
    if !top.iter().all(|v| {
        v.as_str()
            .is_some_and(|s| TOP.contains(&s) && seen.insert(s))
    }) {
        return false;
    }
    seen.clear();
    quick.iter().all(|v| {
        v.is_null()
            || v.as_str()
                .is_some_and(|s| QUICK.contains(&s) && seen.insert(s))
    })
}
pub fn layouts(directory: &Path) -> Value {
    let overview = read_layout(&directory.join("overview-layout.json"))
        .ok()
        .flatten()
        .map(|(_, v)| normalize_overview(&v))
        .unwrap_or_else(|| OVERVIEW.iter().map(|s| (*s).into()).collect());
    let (mini, issue) = match read_layout(&directory.join("mini-layout.json")) {
        Ok(Some((_, value))) if value["Version"] == 1 && valid_mini(&value) => {
            (json!({"Top":value["Top"],"Quick":value["Quick"]}), None)
        }
        Ok(None) => (default_mini(), None),
        Ok(Some(_)) => (
            default_mini(),
            Some("小面板布局无法识别，原文件已保留".to_owned()),
        ),
        Err(error) => (default_mini(), Some(error)),
    };
    json!({"Overview":overview,"Mini":mini,"MiniIssue":issue})
}
pub fn commit_layout(directory: &Path, edit: &Value) -> Result<Value, String> {
    super::platform::validate_directory(directory)?;
    if let Some(items) = edit["Overview"].as_array() {
        let normalized = normalize_overview(&edit["Overview"]);
        if normalized.len() != items.len()
            || !edit["Mini"].is_null()
            || edit["ReplaceUnrecognized"] == true
        {
            return Err("概览布局无效".into());
        }
        let path = directory.join("overview-layout.json");
        config_io::atomic_write_with_retry(
            &path,
            &serde_json::to_vec(&normalized).map_err(|e| e.to_string())?,
        )?;
    } else {
        let value = &edit["Mini"];
        if !valid_mini(value) {
            return Err("小面板布局无效".into());
        }
        let path = directory.join("mini-layout.json");
        let prior = read_layout(&path);
        let unreadable = match &prior {
            Ok(Some((_, v))) => v["Version"] != 1 || !valid_mini(v),
            Ok(None) => false,
            Err(_) => true,
        };
        if unreadable {
            if edit["ReplaceUnrecognized"] != true {
                return Err("原布局无法识别，请先明确恢复默认".into());
            }
            // Keep the original bytes, including unsupported encodings or formats.
            // Never follow a reparse point while preserving a file.
            let metadata = fs::symlink_metadata(&path).map_err(|e| e.to_string())?;
            if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
                return Err("原布局不是普通文件".into());
            }
            let id = unsafe { windows::Win32::System::Com::CoCreateGuid() }
                .map_err(|e| e.to_string())?;
            let backup = directory.join(format!("mini-layout.json.preserved-{id:?}"));
            fs::copy(&path, backup).map_err(|e| e.to_string())?;
        }
        config_io::atomic_write_with_retry(
            &path,
            &serde_json::to_vec(&json!({"Version":1,"Top":value["Top"],"Quick":value["Quick"]}))
                .map_err(|e| e.to_string())?,
        )?;
    }
    Ok(layouts(directory))
}
pub fn commit_preference(
    directory: &Path,
    app: &mut Value,
    system: &mut SystemPreferences,
    edit: &Value,
) -> Receipt {
    let enabled = edit["Enabled"].as_bool();
    let number = edit["Number"].as_i64();
    let application = match (edit["Kind"].as_i64(), number) {
        (Some(1), Some(value @ 0..=2)) => Some(AppPreferenceEdit::Theme {
            value: value as i32,
        }),
        (Some(2), Some(value @ (2 | 5 | 10))) => Some(AppPreferenceEdit::SampleSeconds {
            value: value as i32,
        }),
        _ => None,
    };
    if let Some(edit) = application {
        return match AppPreferences::apply_edit(directory, edit) {
            Ok(bytes) => match serde_json::from_slice(&bytes) {
                Ok(saved) => {
                    *app = saved;
                    Receipt::new(Outcome::ConfigurationSaved)
                }
                Err(error) => Receipt::error(Outcome::ConfigurationSaveFailed, error.to_string()),
            },
            Err(_) => Receipt::error(Outcome::ConfigurationSaveFailed, "应用偏好保存失败"),
        };
    }
    let system_edit = match (edit["Kind"].as_i64(), enabled) {
        (Some(3), None) => edit["Text"]
            .as_str()
            .filter(|s| matches!(*s, "remember" | "mini" | "full"))
            .map(|s| SystemPreferenceEdit::WakeMode { value: s.into() }),
        (Some(4), Some(value)) => Some(SystemPreferenceEdit::MiniAutoHide { value }),
        (Some(5), Some(value)) if edit["Confirmed"].as_bool().unwrap_or(false) == value => {
            Some(SystemPreferenceEdit::UnlockBatteryLimit { value })
        }
        (Some(6), Some(value)) => Some(SystemPreferenceEdit::TrayTelemetry { value }),
        _ => None,
    };
    let Some(edit) = system_edit else {
        return Receipt::error(Outcome::Rejected, "设置值无效或缺少确认");
    };
    commit_system(directory, system, edit)
}
pub fn commit_system(
    directory: &Path,
    system: &mut SystemPreferences,
    edit: SystemPreferenceEdit,
) -> Receipt {
    match SystemPreferences::apply_edit(directory, edit) {
        Ok((saved, _)) => {
            *system = saved;
            Receipt::new(Outcome::ConfigurationSaved)
        }
        Err(_) => Receipt::error(Outcome::ConfigurationSaveFailed, "系统偏好保存失败"),
    }
}
