//! Windows power scheme reads and compare/readback transactions, matching PowerPlans.cs.
use openrevo_core::system::{config_io, native_services};
use serde_json::{json, Value};
use std::{
    ffi::c_void,
    fs,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant},
};
use windows::core::GUID;
#[link(name = "powrprof")]
extern "system" {
    fn PowerGetActiveScheme(root: *mut c_void, out: *mut *mut GUID) -> u32;
    fn PowerSetActiveScheme(root: *mut c_void, guid: *const GUID) -> u32;
    fn PowerEnumerate(
        root: *mut c_void,
        scheme: *const GUID,
        sub: *const GUID,
        access: u32,
        index: u32,
        buffer: *mut u8,
        size: *mut u32,
    ) -> u32;
    fn PowerReadFriendlyName(
        root: *mut c_void,
        scheme: *const GUID,
        sub: *const GUID,
        setting: *const GUID,
        buffer: *mut u8,
        size: *mut u32,
    ) -> u32;
    fn PowerReadACValueIndex(
        root: *mut c_void,
        scheme: *const GUID,
        sub: *const GUID,
        setting: *const GUID,
        value: *mut u32,
    ) -> u32;
    fn PowerReadDCValueIndex(
        root: *mut c_void,
        scheme: *const GUID,
        sub: *const GUID,
        setting: *const GUID,
        value: *mut u32,
    ) -> u32;
}
#[link(name = "kernel32")]
extern "system" {
    fn LocalFree(pointer: *mut c_void) -> *mut c_void;
    fn GetSystemDirectoryW(buffer: *mut u16, size: u32) -> u32;
}
fn guid(text: &str) -> Result<GUID, String> {
    let stripped = text.replace('-', "");
    if stripped.len() != 32 || !stripped.bytes().all(|c| c.is_ascii_hexdigit()) {
        return Err("电源方案标识无效".into());
    }
    Ok(GUID::from_u128(
        u128::from_str_radix(&stripped, 16).map_err(|e| e.to_string())?,
    ))
}
fn name(guid: &GUID) -> String {
    format!("{guid:?}")
        .trim_matches(['{', '}'])
        .to_ascii_lowercase()
}
pub fn active() -> Result<String, String> {
    unsafe {
        let mut pointer = std::ptr::null_mut();
        let code = PowerGetActiveScheme(std::ptr::null_mut(), &mut pointer);
        if code != 0 || pointer.is_null() {
            return Err(format!("当前电源方案未读取 ({code})"));
        }
        let value = name(&*pointer);
        LocalFree(pointer.cast());
        Ok(value)
    }
}
pub fn plans() -> Result<Vec<Value>, String> {
    let mut values = Vec::new();
    unsafe {
        for index in 0..256 {
            let mut value = GUID::zeroed();
            let mut size = 16;
            let code = PowerEnumerate(
                std::ptr::null_mut(),
                std::ptr::null(),
                std::ptr::null(),
                16,
                index,
                (&mut value as *mut GUID).cast(),
                &mut size,
            );
            if code == 259 {
                break;
            }
            if code != 0 || size != 16 {
                return Err(format!("电源方案枚举失败 ({code})"));
            }
            let id = name(&value);
            let mut size = 0;
            let code = PowerReadFriendlyName(
                std::ptr::null_mut(),
                &value,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null_mut(),
                &mut size,
            );
            let label = if matches!(code, 0 | 234) && size > 0 && size <= 65536 {
                let mut words = vec![0u16; (size as usize).div_ceil(2)];
                let cap = words.len() * 2;
                let code = PowerReadFriendlyName(
                    std::ptr::null_mut(),
                    &value,
                    std::ptr::null(),
                    std::ptr::null(),
                    words.as_mut_ptr().cast(),
                    &mut size,
                );
                if code == 0 && size as usize <= cap {
                    let end = words.iter().position(|v| *v == 0).unwrap_or(words.len());
                    String::from_utf16_lossy(&words[..end])
                        .chars()
                        .take(160)
                        .collect()
                } else {
                    id.clone()
                }
            } else {
                id.clone()
            };
            values.push(json!({"Id":id,"Name":label}));
        }
    }
    Ok(values)
}
const DEFINITIONS: [(&str, &str, &str, u32, bool); 6] = [
    (
        "disconnect_net",
        "fea3413e-7e05-4911-9a71-700331f1c294",
        "f15576e8-98b7-4186-b944-eafa664402d9",
        0,
        false,
    ),
    (
        "block_away",
        "238c9fa8-0aad-41ed-83f4-97be242c8f20",
        "25dfa149-5dd1-4736-b5ab-e8a37b5b8187",
        0,
        false,
    ),
    (
        "ignore_sysreq",
        "238c9fa8-0aad-41ed-83f4-97be242c8f20",
        "a4b195f5-8225-47d8-8012-9d41369786e2",
        0,
        false,
    ),
    (
        "disable_wake_timers",
        "238c9fa8-0aad-41ed-83f4-97be242c8f20",
        "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d",
        0,
        false,
    ),
    (
        "deep_sleep",
        "2e601130-5351-4d9d-8e04-252966bad054",
        "d502f7ee-1dc7-4efd-a55d-f04b6f5c0545",
        1,
        false,
    ),
    (
        "hibernate_30m",
        "238c9fa8-0aad-41ed-83f4-97be242c8f20",
        "9d7815a6-7ee4-497e-8888-515a05f02364",
        1800,
        true,
    ),
];
pub fn policies() -> Result<Vec<Value>, String> {
    let scheme = guid(&active()?)?;
    let mut output = Vec::new();
    for (key, group, setting, on, dc_only) in DEFINITIONS {
        let group = guid(group)?;
        let setting = guid(setting)?;
        let (mut ac, mut dc) = (0, 0);
        let ac = unsafe {
            (PowerReadACValueIndex(std::ptr::null_mut(), &scheme, &group, &setting, &mut ac) == 0)
                .then_some(ac)
        };
        let dc = unsafe {
            (PowerReadDCValueIndex(std::ptr::null_mut(), &scheme, &group, &setting, &mut dc) == 0)
                .then_some(dc)
        };
        let supported = dc.is_some() && (dc_only || ac.is_some());
        let enabled = if !supported {
            None
        } else if dc_only || ac == dc {
            Some(dc == Some(on))
        } else {
            None
        };
        output.push(json!({"Key":key,"Supported":supported,"Enabled":enabled}));
    }
    Ok(output)
}
fn activate(id: &str) -> Result<(), String> {
    let value = guid(id)?;
    let code = unsafe { PowerSetActiveScheme(std::ptr::null_mut(), &value) };
    if code != 0 {
        return Err(format!("Windows电源方案设置失败 ({code})"));
    }
    Ok(())
}
pub fn select(requested: &str, expected: &str) -> Result<(), String> {
    let expected = name(&guid(expected)?);
    let requested = name(&guid(requested)?);
    if active()? != expected {
        return Err("方案已被外部修改，请刷新".into());
    }
    if !plans()?.iter().any(|p| p["Id"] == requested) {
        return Err("目标电源方案已不存在".into());
    }
    if active()? != expected {
        return Err("方案已被外部修改，请刷新".into());
    }
    native_services::execute(11, json!({"scheme":requested}))?;
    if active()? != requested {
        return Err("电源方案回读未确认".into());
    }
    Ok(())
}
pub fn set_policy(key: &str, enabled: bool) -> Result<(), String> {
    let scheme = active()?;
    let available = policies()?;
    let targets: Vec<_> = available
        .iter()
        .filter(|p| p["Supported"] == true && (key == "master" || p["Key"] == key))
        .collect();
    if targets.is_empty() {
        return Err("Windows未提供可切换的睡眠策略".into());
    }
    for p in targets {
        if active()? != scheme {
            return Err("电源方案在设置期间变化，停止后续操作".into());
        }
        native_services::execute(1, json!({"key":p["Key"],"enabled":enabled,"scheme":scheme}))?;
        if active()? != scheme {
            return Err("电源方案在设置期间变化，未切回旧方案".into());
        }
        activate(&scheme)?;
        let values = policies()?;
        if !values
            .iter()
            .any(|v| v["Key"] == p["Key"] && v["Enabled"] == enabled)
        {
            return Err("睡眠策略回读不一致，停止后续操作".into());
        }
    }
    Ok(())
}
fn system_powercfg() -> Result<PathBuf, String> {
    let mut path = vec![0u16; 32768];
    let n = unsafe { GetSystemDirectoryW(path.as_mut_ptr(), path.len() as u32) };
    if n == 0 || n as usize >= path.len() {
        return Err("无法定位系统电源工具".into());
    }
    Ok(PathBuf::from(String::from_utf16_lossy(&path[..n as usize])).join("powercfg.exe"))
}
fn run(args: &[String]) -> Result<(), String> {
    let mut process = Command::new(system_powercfg()?)
        .args(args)
        .creation_flags(0x08000000)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| e.to_string())?;
    let start = Instant::now();
    loop {
        if let Some(exit) = process.try_wait().map_err(|e| e.to_string())? {
            return if exit.success() {
                Ok(())
            } else {
                Err("Windows电源工具返回失败".into())
            };
        }
        if start.elapsed() >= Duration::from_secs(30) {
            return Err("电源配置尚未完成，停止后续操作".into());
        }
        thread::sleep(Duration::from_millis(25));
    }
}
pub fn recover(root: &Path) -> Result<String, String> {
    let before = plans()?;
    let active = active()?;
    let backup = root.join("power-plan-backups").join(format!(
        "{}-{:?}",
        crate::resident::platform::unix_ms(),
        unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?
    ));
    crate::resident::platform::validate_directory(&backup)?;
    fs::create_dir_all(&backup).map_err(|e| e.to_string())?;
    for plan in &before {
        let id = plan["Id"].as_str().ok_or("方案标识缺失")?;
        let path = backup.join(format!("{id}.pow"));
        run(&[
            "/export".into(),
            path.to_str().ok_or("备份路径无效")?.into(),
            id.into(),
        ])?;
        if !fs::metadata(&path).is_ok_and(|m| m.is_file() && m.len() > 0) {
            return Err("电源方案备份不完整，未开始修复".into());
        }
    }
    config_io::atomic_write_with_retry(
        &backup.join("manifest.json"),
        &serde_json::to_vec_pretty(&json!({"active":active,"plans":before}))
            .map_err(|e| e.to_string())?,
    )?;
    let reset = native_services::execute(8, json!({"confirmed":true}));
    let builtins = [
        "381b4222-f694-41f0-9685-ff5bb260df2e",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        "a1841308-3541-4fab-bc81-f71556f20b4a",
        "e9a42b02-d5df-448d-aa00-03f14749eb61",
    ];
    for plan in &before {
        let id = plan["Id"].as_str().ok_or("方案标识缺失")?;
        if !builtins.contains(&id) && !plans()?.iter().any(|p| p["Id"] == id) {
            run(&[
                "/import".into(),
                backup
                    .join(format!("{id}.pow"))
                    .to_str()
                    .ok_or("备份路径无效")?
                    .into(),
                id.into(),
            ])?;
        }
    }
    if let Err(error) = reset {
        return Err(format!(
            "修复未完成；已检查保留自建方案；备份{}；{error}",
            backup.display()
        ));
    }
    if self::active()? != builtins[0] {
        return Err(format!("平衡方案回读未确认；备份{}", backup.display()));
    }
    Ok(format!("Windows平衡方案已恢复并回读；Core已执行OEM调度恢复流程，OEM包效果需实际确认。原方案备份：{}",backup.display()))
}
