//! Present Camera-class inventory is read-only. Mutations require one exact
//! device and the existing hardware owner; never bulk-enable a device class.
use super::{read, ControlState, HardwareSession, Outcome, Receipt};
use crate::resident::platform::OperationLock;
use openrevo_core::driver::wmi_ec as ec;
use serde::Serialize;
use serde_json::json;
use std::{
    ffi::c_void,
    mem::size_of,
    os::windows::process::CommandExt,
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant},
};
use windows::{
    core::{w, GUID, PCWSTR},
    Win32::{
        Foundation::{GetLastError, ERROR_FILE_NOT_FOUND, ERROR_NO_MORE_ITEMS},
        System::Registry::{
            RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_CURRENT_USER,
            HKEY_LOCAL_MACHINE, KEY_READ, REG_SZ,
        },
    },
};
#[repr(C)]
struct DeviceInfo {
    size: u32,
    class: GUID,
    instance: u32,
    reserved: usize,
}
#[link(name = "setupapi")]
extern "system" {
    fn SetupDiGetClassDevsW(
        class: *const GUID,
        enumerator: *const u16,
        parent: *mut c_void,
        flags: u32,
    ) -> *mut c_void;
    fn SetupDiEnumDeviceInfo(set: *mut c_void, index: u32, info: *mut DeviceInfo) -> i32;
    fn SetupDiGetDeviceInstanceIdW(
        set: *mut c_void,
        info: *mut DeviceInfo,
        id: *mut u16,
        size: u32,
        required: *mut u32,
    ) -> i32;
    fn SetupDiDestroyDeviceInfoList(set: *mut c_void) -> i32;
}
#[link(name = "cfgmgr32")]
extern "system" {
    fn CM_Get_DevNode_Status(status: *mut u32, problem: *mut u32, instance: u32, flags: u32)
        -> u32;
}
#[link(name = "kernel32")]
extern "system" {
    fn GetSystemDirectoryW(buffer: *mut u16, size: u32) -> u32;
}
struct Set(*mut c_void);
impl Drop for Set {
    fn drop(&mut self) {
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}
#[derive(Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Device {
    instance_id: String,
    enabled: Option<bool>,
}
fn inventory() -> Result<Vec<Device>, String> {
    let class = GUID::from_u128(0xca3e7ab9_b4c3_4ae6_8251_579ef933890f);
    let set =
        Set(unsafe { SetupDiGetClassDevsW(&class, std::ptr::null(), std::ptr::null_mut(), 2) });
    if set.0 as isize == -1 {
        return Err(format!("无法读取Windows相机设备：{}", unsafe {
            GetLastError().0
        }));
    }
    let mut devices = Vec::new();
    for index in 0..256 {
        let mut info = DeviceInfo {
            size: size_of::<DeviceInfo>() as u32,
            class: GUID::zeroed(),
            instance: 0,
            reserved: 0,
        };
        if unsafe { SetupDiEnumDeviceInfo(set.0, index, &mut info) } == 0 {
            let error = unsafe { GetLastError() };
            if error == ERROR_NO_MORE_ITEMS {
                return Ok(devices);
            }
            return Err(format!("Windows相机枚举失败 ({})", error.0));
        }
        let mut id = vec![0u16; 1024];
        let mut size = 0;
        if unsafe {
            SetupDiGetDeviceInstanceIdW(
                set.0,
                &mut info,
                id.as_mut_ptr(),
                id.len() as u32,
                &mut size,
            )
        } == 0
            || size as usize > id.len()
        {
            return Err("相机设备标识未读取".into());
        }
        let end = id
            .iter()
            .position(|v| *v == 0)
            .ok_or("相机设备标识未终止")?;
        let instance = String::from_utf16(&id[..end]).map_err(|e| e.to_string())?;
        if instance.is_empty() {
            return Err("相机设备标识为空".into());
        }
        let (mut status, mut problem) = (0, 0);
        let enabled =
            if unsafe { CM_Get_DevNode_Status(&mut status, &mut problem, info.instance, 0) } == 0 {
                if problem == 22 {
                    Some(false)
                } else if problem == 0 && status & 8 != 0 {
                    Some(true)
                } else {
                    None
                }
            } else {
                None
            };
        devices.push(Device {
            instance_id: instance,
            enabled,
        });
    }
    Err("摄像设备数量超出只读枚举上限".into())
}
fn single() -> Result<Option<Device>, String> {
    let mut devices = inventory()?;
    Ok(if devices.len() == 1 {
        devices.pop()
    } else {
        None
    })
}
fn deny(root: HKEY, path: &str) -> Result<bool, String> {
    let name: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    let mut key = HKEY::default();
    let status = unsafe { RegOpenKeyExW(root, PCWSTR(name.as_ptr()), 0, KEY_READ, &mut key) };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(false);
    }
    status.ok().map_err(|e| e.to_string())?;
    let result = (|| {
        let mut words = [0u16; 128];
        let mut size = 256;
        let mut kind = REG_SZ;
        let status = unsafe {
            RegQueryValueExW(
                key,
                w!("Value"),
                None,
                Some(&mut kind),
                Some(words.as_mut_ptr().cast()),
                Some(&mut size),
            )
        };
        if status == ERROR_FILE_NOT_FOUND {
            return Ok(false);
        }
        status.ok().map_err(|e| e.to_string())?;
        if kind != REG_SZ || size as usize > words.len() * 2 {
            return Err("相机访问权限文本无效".into());
        }
        let end = words.iter().position(|v| *v == 0).unwrap_or(words.len());
        Ok(String::from_utf16_lossy(&words[..end]).eq_ignore_ascii_case("Deny"))
    })();
    let _ = unsafe { RegCloseKey(key) };
    result
}
fn blocked() -> Result<bool, String> {
    use openrevo_core::system::switches::camera;
    let path =
        r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    Ok(camera::read_camera_policy_registry() == Some(false)
        || camera::read_camera_consent_registry() == Some(false)
        || deny(HKEY_LOCAL_MACHINE, path)?
        || deny(HKEY_LOCAL_MACHINE, &format!(r"{path}\NonPackaged"))?
        || deny(HKEY_CURRENT_USER, &format!(r"{path}\NonPackaged"))?)
}
fn pnp(expected: &Device, enabled: bool) -> Result<(), String> {
    let current = single()?.ok_or("无法唯一选定Windows相机设备")?;
    if &current != expected || current.enabled != Some(!enabled) {
        return Err("Windows相机设备或状态已变化，请刷新".into());
    }
    let mut path = vec![0u16; 32768];
    let size = unsafe { GetSystemDirectoryW(path.as_mut_ptr(), path.len() as u32) };
    if size == 0 || size as usize >= path.len() {
        return Err("无法定位Windows设备工具".into());
    }
    let executable = std::path::PathBuf::from(String::from_utf16_lossy(&path[..size as usize]))
        .join("pnputil.exe");
    let mut process = Command::new(executable)
        .arg(if enabled {
            "/enable-device"
        } else {
            "/disable-device"
        })
        .arg(&expected.instance_id)
        .creation_flags(0x08000000)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| e.to_string())?;
    let start = Instant::now();
    loop {
        if let Some(exit) = process.try_wait().map_err(|e| e.to_string())? {
            if !exit.success() {
                return Err(format!("Windows相机设置未完成 ({exit})"));
            }
            break;
        }
        if start.elapsed() >= Duration::from_secs(8) {
            return Err("Windows相机操作超时，请先重新读取".into());
        }
        thread::sleep(Duration::from_millis(25));
    }
    for _ in 0..10 {
        if single()?
            .is_some_and(|v| v.instance_id == expected.instance_id && v.enabled == Some(enabled))
        {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(150));
    }
    Err("Windows相机设备状态未回读确认".into())
}
#[derive(Default)]
pub struct Camera {
    last: Option<Device>,
}
impl Camera {
    pub fn read(&mut self, hardware: &HardwareSession) -> (Option<bool>, bool) {
        let delegated = !hardware.classic();
        let devices = inventory();
        let (mut camera, mut available) = (None, false);
        if let Ok(mut devices) = devices {
            available = if delegated {
                !devices.is_empty()
            } else {
                devices.len() == 1
            };
            self.last = if devices.len() == 1 {
                devices.pop()
            } else {
                None
            };
            if hardware.active() {
                camera = if delegated {
                    self.last.as_ref().and_then(|d| d.enabled)
                } else {
                    match read(ec::ADDR_CAMERA_CUTOFF) {
                        Ok(raw) if raw & ec::CAMERA_CUTOFF_BITMASK != 0 => Some(false),
                        Ok(_) => self.last.as_ref().and_then(|d| d.enabled),
                        Err(_) => None,
                    }
                };
            }
        } else {
            self.last = None;
        }
        if !delegated && camera == Some(true) {
            camera = match blocked() {
                Ok(true) => Some(false),
                Ok(false) => camera,
                Err(_) => None,
            };
        }
        (camera, available)
    }
}
impl HardwareSession {
    fn camera_power(&mut self, enabled: bool) -> Result<(), String> {
        if !self.active() {
            return Err("请先连接硬件".into());
        }
        self.guard()?;
        let root = ControlState::read()?;
        self.check_root(&root)?;
        let before = read(ec::ADDR_CAMERA_CUTOFF)?;
        let target = if enabled {
            before & !ec::CAMERA_CUTOFF_BITMASK
        } else {
            before | ec::CAMERA_CUTOFF_BITMASK
        };
        if target != before {
            self.audit(
                "core-camera-power-intent",
                json!({"before":before,"target":target}),
            )?;
            self.guard()?;
            let code = crate::or_native_camera_power_set(u32::from(enabled));
            if code != 0 || read(ec::ADDR_CAMERA_CUTOFF)? != target {
                self.faulted = true;
                return Err(format!("相机供电回读未确认 ({code})"));
            }
        }
        let _ = read(1896)?;
        let _ = read(ec::ADDR_CAMERA_CUTOFF)?;
        self.audit("core-camera-power-confirmed", json!({"powered":enabled}))?;
        Ok(())
    }
    pub fn toggle_camera(&mut self, camera: &mut Camera) -> Receipt {
        let mut attempted = false;
        let result = (|| -> Result<Outcome, String> {
            let _lock = OperationLock::enter()?;
            if !self.active() {
                return Err("请先连接硬件".into());
            }
            self.guard()?;
            if !self.classic() {
                let expected = camera
                    .last
                    .clone()
                    .filter(|v| v.enabled.is_some())
                    .ok_or("当前Windows相机设备状态未知")?;
                self.audit(
                    "windows-camera-intent",
                    json!({"device":expected,"target":!expected.enabled.unwrap()}),
                )?;
                self.guard()?;
                attempted = true;
                pnp(&expected, !expected.enabled.unwrap())?;
                self.audit(
                    "windows-camera-confirmed",
                    json!({"enabled":!expected.enabled.unwrap(),"firmwareReadback":false}),
                )?;
                return Ok(Outcome::ReadbackConfirmed);
            }
            let current = camera
                .read(self)
                .0
                .ok_or("无法确认相机状态，请检查设备管理器")?;
            let enable = !current;
            let before_powered = read(ec::ADDR_CAMERA_CUTOFF)? & ec::CAMERA_CUTOFF_BITMASK == 0;
            let action = (|| -> Result<(), String> {
                attempted = true;
                self.camera_power(enable)?;
                if enable {
                    let mut device = None;
                    for _ in 0..30 {
                        device = single()?;
                        if device.as_ref().is_some_and(|d| d.enabled.is_some()) {
                            break;
                        }
                        thread::sleep(Duration::from_millis(100));
                    }
                    if device.as_ref().is_some_and(|d| d.enabled == Some(false)) || blocked()? {
                        let baseline = single()?.ok_or("本机摄像头不匹配")?;
                        self.camera_power(true)?;
                        if baseline.enabled == Some(false) {
                            self.guard()?;
                            pnp(&baseline, true)?;
                        }
                        for _ in 0..40 {
                            if single()?.is_some_and(|d| d.enabled == Some(true)) {
                                break;
                            }
                            thread::sleep(Duration::from_millis(100));
                        }
                        if !single()?.is_some_and(|d| d.enabled == Some(true)) {
                            return Err("相机设备状态未确认".into());
                        }
                        self.guard()?;
                        if crate::or_native_camera_access_allow() != 0 || blocked()? {
                            return Err("Windows相机访问未确认开启，请检查系统相机权限".into());
                        }
                    } else if !device.is_some_and(|d| d.enabled == Some(true)) {
                        return Err("相机尚未就绪，请稍后重试".into());
                    }
                }
                if camera.read(self).0 != Some(enable) {
                    return Err("相机状态未确认改变".into());
                }
                Ok(())
            })();
            if action.is_err()
                && !before_powered
                && self.active()
                && read(ec::ADDR_CAMERA_CUTOFF).is_ok_and(|v| v & ec::CAMERA_CUTOFF_BITMASK == 0)
            {
                let _ = self.camera_power(false);
            }
            action?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        self.finish(result, attempted)
    }
}
