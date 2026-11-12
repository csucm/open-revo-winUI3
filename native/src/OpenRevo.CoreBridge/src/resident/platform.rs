use std::{
    fs,
    io::Write,
    mem::size_of,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
    sync::OnceLock,
};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{
            CloseHandle, GetLastError, ERROR_NO_MORE_FILES, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0,
        },
        System::{
            Diagnostics::ToolHelp::{
                CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
                TH32CS_SNAPPROCESS,
            },
            Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS},
            Registry::{
                RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_LOCAL_MACHINE, KEY_READ,
                REG_SZ,
            },
            Threading::{CreateMutexW, ReleaseMutex, WaitForSingleObject},
        },
    },
};

pub fn unix_ms() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}
pub fn ac_online() -> Option<bool> {
    let mut status = SYSTEM_POWER_STATUS::default();
    if unsafe { GetSystemPowerStatus(&mut status) }.is_err() {
        return None;
    }
    match status.ACLineStatus {
        0 => Some(false),
        1 => Some(true),
        _ => None,
    }
}
struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}
pub struct OperationLock(Handle);
impl OperationLock {
    pub fn enter() -> Result<Self, String> {
        unsafe {
            let handle = Handle(
                CreateMutexW(None, false, w!(r"Local\OpenRevoNative.HardwareOperation"))
                    .map_err(|e| e.to_string())?,
            );
            match WaitForSingleObject(handle.0, 0) {
                WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Self(handle)),
                _ => Err("另一硬件操作尚未完成".into()),
            }
        }
    }
}
impl Drop for OperationLock {
    fn drop(&mut self) {
        let _ = unsafe { ReleaseMutex(self.0 .0) };
    }
}

pub fn check_conflicts() -> Result<(), String> {
    unsafe {
        let snapshot = Handle(
            CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
                .map_err(|e| format!("无法检查控制程序：{e}"))?,
        );
        let mut entry = PROCESSENTRY32W {
            dwSize: size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        if let Err(error) = Process32FirstW(snapshot.0, &mut entry) {
            if GetLastError() == ERROR_NO_MORE_FILES {
                return Ok(());
            }
            return Err(format!("无法检查控制程序：{error}"));
        }
        loop {
            let end = entry
                .szExeFile
                .iter()
                .position(|c| *c == 0)
                .unwrap_or(entry.szExeFile.len());
            let name = String::from_utf16_lossy(&entry.szExeFile[..end]).to_ascii_lowercase();
            let name = name.strip_suffix(".exe").unwrap_or(&name);
            if matches!(name, "open-revo" | "openrevo" | "openrevo.native")
                || [
                    "controlcenter",
                    "gamingcenter",
                    "oemservice",
                    "uniwillservice",
                ]
                .iter()
                .any(|part| name.contains(part))
            {
                return Err(format!(
                    "请先正常退出其他硬件控制程序：{name}（PID {}）。",
                    entry.th32ProcessID
                ));
            }
            if let Err(error) = Process32NextW(snapshot.0, &mut entry) {
                if GetLastError() == ERROR_NO_MORE_FILES {
                    return Ok(());
                }
                return Err(format!("无法完成控制程序检查：{error}"));
            }
        }
    }
}
pub(super) fn bios_string(name: &str) -> Option<String> {
    unsafe {
        let mut key = HKEY::default();
        RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            w!(r"HARDWARE\DESCRIPTION\System\BIOS"),
            0,
            KEY_READ,
            &mut key,
        )
        .ok()
        .ok()?;
        let result = (|| {
            let name: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
            let mut kind = REG_SZ;
            let mut bytes = 0;
            RegQueryValueExW(
                key,
                PCWSTR(name.as_ptr()),
                None,
                Some(&mut kind),
                None,
                Some(&mut bytes),
            )
            .ok()
            .ok()?;
            if kind != REG_SZ || !(2..=4096).contains(&bytes) {
                return None;
            }
            let mut buffer = vec![0u16; (bytes as usize).div_ceil(2)];
            RegQueryValueExW(
                key,
                PCWSTR(name.as_ptr()),
                None,
                Some(&mut kind),
                Some(buffer.as_mut_ptr().cast()),
                Some(&mut bytes),
            )
            .ok()
            .ok()?;
            if kind != REG_SZ || bytes as usize > buffer.len() * 2 {
                return None;
            }
            let end = buffer.iter().position(|v| *v == 0).unwrap_or(buffer.len());
            Some(String::from_utf16_lossy(&buffer[..end]))
        })();
        let _ = RegCloseKey(key);
        result
    }
}
pub fn gm7tg0m_identity() -> bool {
    static MATCH: OnceLock<bool> = OnceLock::new();
    *MATCH.get_or_init(|| {
        bios_string("BaseBoardProduct").is_some_and(|s| s.eq_ignore_ascii_case("GM7TG0M"))
            && bios_string("BIOSVersion").is_some_and(|s| s.eq_ignore_ascii_case("N.1.07MRO06"))
    })
}
fn regular(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if !meta.is_file() || meta.file_attributes() & 0x400 != 0 {
        return Err("设置或日志不是普通文件".into());
    }
    Ok(())
}
pub fn validate_directory(path: &Path) -> Result<(), String> {
    if !path.is_absolute() || path.parent().is_none() {
        return Err("数据目录须为非根绝对路径".into());
    }
    // Reject relative components even when attached to an absolute drive prefix.
    if path.components().any(|c| {
        matches!(
            c,
            std::path::Component::ParentDir | std::path::Component::CurDir
        )
    }) {
        return Err("数据目录不得含相对分量".into());
    }
    for parent in path.ancestors() {
        match fs::symlink_metadata(parent) {
            Ok(meta) if !meta.is_dir() || meta.file_attributes() & 0x400 != 0 => {
                return Err("数据目录不能包含链接或重解析点".into())
            }
            Err(error) if error.kind() != std::io::ErrorKind::NotFound => {
                return Err(error.to_string())
            }
            _ => {}
        }
    }
    Ok(())
}
pub fn candidate_data(base: &Path) -> Result<PathBuf, String> {
    let default = base.join("resident-data");
    let pointer = base.join("resident-data.location.json");
    let directory = if pointer.exists() {
        regular(&pointer)?;
        let bytes = fs::read(&pointer).map_err(|e| e.to_string())?;
        if bytes.len() >= 8192 {
            return Err("候选数据指针超过边界".into());
        }
        let target: String = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        let target = PathBuf::from(target);
        validate_directory(&target)?;
        let storage = base.join("resident-storage");
        if !target.starts_with(&storage) || target == storage {
            return Err("候选数据指针超出 resident-storage".into());
        }
        target
    } else {
        default
    };
    validate_directory(&directory)?;
    fs::create_dir_all(&directory).map_err(|e| e.to_string())?;
    let marker = directory.join("openrevo-native-data.json");
    if !marker.exists() {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&marker)
            .map_err(|e| e.to_string())?;
        file.write_all(br#"{"application":"OpenRevo.Native","schema":1}"#)
            .map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
    }
    regular(&marker)?;
    if fs::metadata(&marker).map_err(|e| e.to_string())?.len() > 4096 {
        return Err("候选数据标记超过边界".into());
    }
    let value: serde_json::Value =
        serde_json::from_slice(&fs::read(marker).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
    if value["application"] != "OpenRevo.Native" || value["schema"] != 1 {
        return Err("数据目录标记不属于本版控制台".into());
    }
    for name in [
        "hardware-audit.jsonl",
        "desktop-services.json",
        "preferences.json",
        "system-settings.json",
    ] {
        let file = directory.join(name);
        if file.exists() {
            regular(&file)?;
        }
    }
    Ok(directory)
}

pub fn system_library(name: &str) -> Result<libloading::Library, String> {
    if name.contains(['/', '\\', ':']) {
        return Err("系统模块名无效".into());
    }
    let mut buffer = [0u16; 32768];
    let length = unsafe {
        windows::Win32::System::SystemInformation::GetSystemDirectoryW(Some(&mut buffer))
    } as usize;
    if length == 0 || length >= buffer.len() {
        return Err("无法取得 Windows 系统目录".into());
    }
    let root = PathBuf::from(String::from_utf16(&buffer[..length]).map_err(|e| e.to_string())?);
    unsafe { libloading::Library::new(root.join(name)) }.map_err(|e| e.to_string())
}
