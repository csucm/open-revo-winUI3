//! Internal-panel control, with the same topology/clone rejection and expected
//! state comparisons as the managed reference. No virtual/external fallback.
use serde::Serialize;
use serde_json::{json, Value};
use std::{
    mem::size_of,
    sync::{
        atomic::{AtomicU8, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError},
        Arc,
    },
    thread,
    time::{Duration, Instant},
};
use windows::{
    core::{w, BSTR, HRESULT, PCWSTR, VARIANT},
    Win32::{
        Devices::{
            DeviceAndDriverInstallation::{
                SetupDiCreateDeviceInfoList, SetupDiDestroyDeviceInfoList,
                SetupDiGetDeviceInstanceIdW, SetupDiGetDeviceInterfaceDetailW,
                SetupDiOpenDeviceInterfaceW, SP_DEVICE_INTERFACE_DATA, SP_DEVINFO_DATA,
            },
            Display::*,
        },
        Foundation::{ERROR_INSUFFICIENT_BUFFER, HWND},
        Graphics::Gdi::*,
        System::{
            Com::{
                CoCreateInstance, CoInitializeEx, CoSetProxyBlanket, CoUninitialize,
                CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, EOAC_NONE, RPC_C_AUTHN_LEVEL_CALL,
                RPC_C_IMP_LEVEL_IMPERSONATE,
            },
            Wmi::{
                IWbemClassObject, IWbemLocator, IWbemServices, WbemLocator, WBEM_FLAG_FORWARD_ONLY,
                WBEM_FLAG_RETURN_IMMEDIATELY, WBEM_GENERIC_FLAG_TYPE,
            },
        },
    },
};
fn text(bytes: &[u16]) -> String {
    String::from_utf16_lossy(&bytes[..bytes.iter().position(|v| *v == 0).unwrap_or(bytes.len())])
}
pub fn panel_code(name: &str) -> Option<String> {
    let normalized = name.replace('#', "\\");
    let parts: Vec<_> = normalized.split('\\').filter(|s| !s.is_empty()).collect();
    parts
        .windows(2)
        .find(|p| p[0].eq_ignore_ascii_case("DISPLAY") || p[0].eq_ignore_ascii_case("MONITOR"))
        .map(|p| p[1].to_owned())
}
fn monitor_instance(path: &str) -> Option<String> {
    if path.is_empty() {
        return None;
    }
    unsafe {
        let set = SetupDiCreateDeviceInfoList(None, HWND::default()).ok()?;
        let instance = (|| {
            let path = wide(path);
            let mut interface = SP_DEVICE_INTERFACE_DATA {
                cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
                ..Default::default()
            };
            SetupDiOpenDeviceInterfaceW(set, PCWSTR(path.as_ptr()), 0, Some(&mut interface))
                .ok()?;
            let mut device = SP_DEVINFO_DATA {
                cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
                ..Default::default()
            };
            let error =
                SetupDiGetDeviceInterfaceDetailW(set, &interface, None, 0, None, Some(&mut device))
                    .err()?;
            if error.code() != HRESULT::from_win32(ERROR_INSUFFICIENT_BUFFER.0) {
                return None;
            }
            // MAX_DEVICE_ID_LEN includes the trailing null.
            let mut id = [0u16; 200];
            SetupDiGetDeviceInstanceIdW(set, &device, Some(&mut id), None).ok()?;
            let id = text(&id);
            (!id.is_empty()).then(|| id.to_ascii_uppercase())
        })();
        let _ = SetupDiDestroyDeviceInfoList(set);
        instance
    }
}
fn matches_wmi_instance(name: &str, expected: &str) -> bool {
    name.eq_ignore_ascii_case(expected)
        || name.rsplit_once('_').is_some_and(|(instance, index)| {
            instance.eq_ignore_ascii_case(expected)
                && !index.is_empty()
                && index.bytes().all(|byte| byte.is_ascii_digit())
        })
}
fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}
fn adapter_description(name: &str) -> String {
    unsafe {
        for index in 0..64 {
            let mut device = DISPLAY_DEVICEW {
                cb: size_of::<DISPLAY_DEVICEW>() as u32,
                ..Default::default()
            };
            if !EnumDisplayDevicesW(PCWSTR::null(), index, &mut device, 0).as_bool() {
                break;
            }
            if text(&device.DeviceName).eq_ignore_ascii_case(name) {
                return text(&device.DeviceString);
            }
        }
        String::new()
    }
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct PanelState {
    pub width: u32,
    pub height: u32,
    pub hertz: u32,
    pub rates: Vec<u32>,
    pub device: String,
    pub panel: String,
    #[serde(skip)]
    monitor_path: String,
    #[serde(skip)]
    instance: Option<String>,
    #[serde(skip)]
    pub source_adapter: windows::Win32::Foundation::LUID,
    #[serde(skip)]
    pub target_adapter: windows::Win32::Foundation::LUID,
}
impl PanelState {
    fn same_identity(&self, other: &Self) -> bool {
        self.device == other.device
            && !self.monitor_path.is_empty()
            && self.monitor_path.eq_ignore_ascii_case(&other.monitor_path)
            && self.source_adapter == other.source_adapter
            && self.target_adapter == other.target_adapter
            && self.width == other.width
            && self.height == other.height
    }
    fn check_current(&self) -> Result<(), String> {
        if !self.same_identity(&Self::read_current()?) {
            return Err("内屏状态已变化，请刷新后重试".into());
        }
        Ok(())
    }
    pub fn read() -> Result<Self, String> {
        Self::read_with_rates(true)
    }
    fn read_current() -> Result<Self, String> {
        Self::read_with_rates(false)
    }
    fn read_with_rates(include_rates: bool) -> Result<Self, String> {
        unsafe {
            let mut paths = Vec::new();
            let mut read_ok = false;
            for _ in 0..3 {
                let (mut path_count, mut mode_count) = (0, 0);
                let status = GetDisplayConfigBufferSizes(
                    QDC_ONLY_ACTIVE_PATHS,
                    &mut path_count,
                    &mut mode_count,
                );
                if status.0 != 0 {
                    return Err(format!("无法枚举活动显示路径 ({})", status.0));
                }
                if path_count > 256 || mode_count > 1024 {
                    return Err("显示路径超出边界".into());
                }
                paths = vec![DISPLAYCONFIG_PATH_INFO::default(); path_count as usize];
                let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); mode_count as usize];
                let status = QueryDisplayConfig(
                    QDC_ONLY_ACTIVE_PATHS,
                    &mut path_count,
                    paths.as_mut_ptr(),
                    &mut mode_count,
                    modes.as_mut_ptr(),
                    None,
                );
                if status.0 == 122 {
                    continue;
                }
                if status.0 != 0 {
                    return Err(format!("读取显示路径失败 ({})", status.0));
                }
                if path_count as usize > paths.len() {
                    return Err("显示路径返回边界无效".into());
                }
                paths.truncate(path_count as usize);
                read_ok = true;
                break;
            }
            if !read_ok {
                return Err("显示拓扑持续变化，请重试".into());
            }
            let mut candidates = Vec::new();
            for path in &paths {
                if path.flags & 1 == 0
                    || !path.targetInfo.targetAvailable.as_bool()
                    || !matches!(path.targetInfo.outputTechnology.0, 6 | 11 | 13 | i32::MIN)
                {
                    continue;
                }
                let mut source = DISPLAYCONFIG_SOURCE_DEVICE_NAME {
                    header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
                        r#type: DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size: size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>() as u32,
                        adapterId: path.sourceInfo.adapterId,
                        id: path.sourceInfo.id,
                    },
                    ..Default::default()
                };
                let mut target = DISPLAYCONFIG_TARGET_DEVICE_NAME {
                    header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
                        r#type: DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size: size_of::<DISPLAYCONFIG_TARGET_DEVICE_NAME>() as u32,
                        adapterId: path.targetInfo.adapterId,
                        id: path.targetInfo.id,
                    },
                    ..Default::default()
                };
                if DisplayConfigGetDeviceInfo(&mut source.header) != 0
                    || DisplayConfigGetDeviceInfo(&mut target.header) != 0
                {
                    continue;
                }
                let device = text(&source.viewGdiDeviceName);
                let name = text(&target.monitorFriendlyDeviceName);
                let description =
                    format!("{} {name}", adapter_description(&device)).to_ascii_lowercase();
                if [
                    "virtual",
                    "remote",
                    "gameviewer",
                    "easy&light",
                    "indirect",
                    "miracast",
                ]
                .iter()
                .any(|s| description.contains(s))
                {
                    continue;
                }
                let path_name = text(&target.monitorDevicePath);
                let panel = panel_code(&path_name).unwrap_or_else(|| "未知".into());
                let shared = paths
                    .iter()
                    .filter(|p| {
                        p.sourceInfo.adapterId == path.sourceInfo.adapterId
                            && p.sourceInfo.id == path.sourceInfo.id
                    })
                    .count()
                    > 1;
                candidates.push((
                    device,
                    panel,
                    path_name,
                    shared,
                    path.sourceInfo.adapterId,
                    path.targetInfo.adapterId,
                ));
            }
            if candidates.len() != 1 {
                return Err(format!(
                    "活动内置屏候选数量为{}，未选择外接或虚拟屏",
                    candidates.len()
                ));
            }
            let (device, panel, monitor_path, shared, source_adapter, target_adapter) =
                candidates.remove(0);
            if shared {
                return Err("内置屏共用克隆显示源，未开放控制".into());
            }
            let mode = read_mode(&device, ENUM_CURRENT_SETTINGS)?;
            if mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0 || mode.dmDisplayFrequency <= 1 {
                return Err("内屏当前模式无效".into());
            }
            let mut rates = std::collections::BTreeSet::new();
            if include_rates {
                for index in 0..1024 {
                    let Ok(candidate) = read_mode(&device, ENUM_DISPLAY_SETTINGS_MODE(index)) else {
                        break;
                    };
                    let resolution = candidate.dmPelsWidth == mode.dmPelsWidth
                        && candidate.dmPelsHeight == mode.dmPelsHeight
                        || candidate.dmPelsWidth == mode.dmPelsHeight
                            && candidate.dmPelsHeight == mode.dmPelsWidth;
                    if resolution
                        && candidate.dmBitsPerPel == mode.dmBitsPerPel
                        && candidate.dmDisplayFrequency > 1
                    {
                        rates.insert(candidate.dmDisplayFrequency);
                    }
                }
            }
            if rates.is_empty() {
                rates.insert(mode.dmDisplayFrequency);
            }
            Ok(Self {
                width: mode.dmPelsWidth,
                height: mode.dmPelsHeight,
                hertz: mode.dmDisplayFrequency,
                rates: rates.into_iter().collect(),
                device,
                panel,
                instance: include_rates.then(|| monitor_instance(&monitor_path)).flatten(),
                monitor_path,
                source_adapter,
                target_adapter,
            })
        }
    }
    fn change(&self, target: u32) -> Result<Self, String> {
        unsafe {
            let current = Self::read()?;
            if !self.same_identity(&current)
                || self.hertz != current.hertz
                || !current.rates.contains(&target)
            {
                return Err("内屏身份、显示模式或刷新率已变化，未写入".into());
            }
            let device = wide(&current.device);
            let mut mode = read_mode(&current.device, ENUM_CURRENT_SETTINGS)?;
            mode.dmFields = DM_DISPLAYFREQUENCY;
            mode.dmDisplayFrequency = target;
            let test = ChangeDisplaySettingsExW(
                PCWSTR(device.as_ptr()),
                Some(&mode),
                HWND::default(),
                CDS_TEST,
                None,
            );
            if test != DISP_CHANGE_SUCCESSFUL {
                return Err(format!("Windows 拒绝内屏模式 ({})", test.0));
            }
            let changed = ChangeDisplaySettingsExW(
                PCWSTR(device.as_ptr()),
                Some(&mode),
                HWND::default(),
                CDS_TYPE(0),
                None,
            );
            let after = Self::read()?;
            if changed != DISP_CHANGE_SUCCESSFUL
                || !current.same_identity(&after)
                || after.hertz != target
            {
                return Err(format!("内屏刷新率回读未确认 ({})", changed.0));
            }
            Ok(after)
        }
    }
}
unsafe fn read_mode(device: &str, index: ENUM_DISPLAY_SETTINGS_MODE) -> Result<DEVMODEW, String> {
    let name = wide(device);
    let mut mode = DEVMODEW {
        dmSize: size_of::<DEVMODEW>() as u16,
        ..Default::default()
    };
    if !EnumDisplaySettingsExW(PCWSTR(name.as_ptr()), index, &mut mode, EDS_ROTATEDMODE).as_bool() {
        mode = DEVMODEW {
            dmSize: size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        if !EnumDisplaySettingsW(PCWSTR(name.as_ptr()), index, &mut mode).as_bool() {
            return Err("无法读取内屏显示模式".into());
        }
    }
    Ok(mode)
}
struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}
struct BrightnessWmi {
    services: IWbemServices,
    _apartment: Apartment,
}
impl BrightnessWmi {
    fn open() -> Result<Self, String> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED)
                .ok()
                .map_err(|e| e.to_string())?;
            let apartment = Apartment;
            let locator: IWbemLocator = CoCreateInstance(&WbemLocator, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| e.to_string())?;
            let services = locator
                .ConnectServer(
                    &BSTR::from(r"root\WMI"),
                    &BSTR::new(),
                    &BSTR::new(),
                    &BSTR::new(),
                    0,
                    &BSTR::new(),
                    None,
                )
                .map_err(|e| e.to_string())?;
            CoSetProxyBlanket(
                &services,
                10,
                0,
                None,
                RPC_C_AUTHN_LEVEL_CALL,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                None,
                EOAC_NONE,
            )
            .map_err(|e| e.to_string())?;
            Ok(Self {
                services,
                _apartment: apartment,
            })
        }
    }
    fn candidate(&self, class: &str, instance: &str) -> Result<IWbemClassObject, String> {
        unsafe {
            let query = format!("SELECT * FROM {class} WHERE Active=TRUE");
            let rows = self
                .services
                .ExecQuery(
                    &BSTR::from("WQL"),
                    &BSTR::from(query),
                    WBEM_GENERIC_FLAG_TYPE(
                        (WBEM_FLAG_FORWARD_ONLY.0 | WBEM_FLAG_RETURN_IMMEDIATELY.0) as i32,
                    ),
                    None,
                )
                .map_err(|e| e.to_string())?;
            let mut matches = Vec::new();
            let mut ended = false;
            for _ in 0..256 {
                let mut items = [None];
                let mut count = 0;
                let status = rows.Next(1000, &mut items, &mut count);
                status.ok().map_err(|e| e.to_string())?;
                if count == 0 {
                    if status.0 == 1 {
                        ended = true;
                        break;
                    }
                    return Err("亮度通道读取超时".into());
                }
                let item = items[0].take().ok_or("亮度通道为空")?;
                let mut value = VARIANT::default();
                item.Get(w!("InstanceName"), 0, &mut value, None, None)
                    .map_err(|e| e.to_string())?;
                if matches_wmi_instance(&value.to_string(), instance) {
                    matches.push(item);
                }
            }
            if !ended || matches.len() != 1 {
                return Err(format!(
                    "活动内屏亮度通道数量为{}，未使用其他屏幕替代",
                    matches.len()
                ));
            }
            Ok(matches.remove(0))
        }
    }
    fn read(&self, instance: &str) -> Result<u8, String> {
        unsafe {
            let row = self.candidate("WmiMonitorBrightness", instance)?;
            let mut value = VARIANT::default();
            row.Get(w!("CurrentBrightness"), 0, &mut value, None, None)
                .map_err(|e| e.to_string())?;
            value
                .to_string()
                .parse::<u8>()
                .ok()
                .filter(|v| *v <= 100)
                .ok_or("亮度回读无效".into())
        }
    }
    fn write(&self, panel: &PanelState, instance: &str, level: u8, gate: &AtomicU8) -> Result<(), String> {
        unsafe {
            let row = self.candidate("WmiMonitorBrightnessMethods", instance)?;
            let mut path = VARIANT::default();
            row.Get(w!("__PATH"), 0, &mut path, None, None)
                .map_err(|e| e.to_string())?;
            let mut definition = None;
            self.services
                .GetObject(
                    &BSTR::from("WmiMonitorBrightnessMethods"),
                    WBEM_GENERIC_FLAG_TYPE(0),
                    None,
                    Some(&mut definition),
                    None,
                )
                .map_err(|e| e.to_string())?;
            let definition = definition.ok_or("亮度方法定义缺失")?;
            let mut signature = None;
            definition
                .GetMethod(
                    w!("WmiSetBrightness"),
                    0,
                    &mut signature,
                    std::ptr::null_mut(),
                )
                .map_err(|e| e.to_string())?;
            let parameters = signature
                .ok_or("亮度方法签名缺失")?
                .SpawnInstance(0)
                .map_err(|e| e.to_string())?;
            // WMI transports CIM_UINT32 method parameters in a VT_I4 variant.
            parameters
                .Put(w!("Timeout"), 0, &VARIANT::from(0i32), 0)
                .map_err(|e| format!("设置 WMI Timeout 失败 ({e})"))?;
            parameters
                .Put(w!("Brightness"), 0, &VARIANT::from(level), 0)
                .map_err(|e| format!("设置 WMI Brightness 失败 ({e})"))?;
            panel.check_current()?;
            gate.compare_exchange(PREPARING, ISSUED, Ordering::AcqRel, Ordering::Acquire)
                .map_err(|_| "亮度请求已过期，未执行写入")?;
            let mut result = None;
            self.services
                .ExecMethod(
                    &BSTR::from(path.to_string()),
                    &BSTR::from("WmiSetBrightness"),
                    WBEM_GENERIC_FLAG_TYPE(0),
                    None,
                    &parameters,
                    Some(&mut result),
                    None,
                )
                .map_err(|e| format!("执行 WmiSetBrightness 失败 ({e})"))?;
            let Some(result) = result else {
                // Some providers apply the method without an output object.
                // The caller still needs matching brightness readback.
                return Ok(());
            };
            let mut value = VARIANT::default();
            match result.Get(w!("ReturnValue"), 0, &mut value, None, None) {
                Err(error) if error.code().0 as u32 == 0x80041002 => return Ok(()),
                Err(error) => return Err(format!("读取 WmiSetBrightness 返回值失败 ({error})")),
                Ok(()) if value.is_empty() => return Ok(()),
                Ok(()) => {}
            }
            let status = u32::try_from(&value).map_err(|e| e.to_string())?;
            if status != 0 {
                return Err(format!("亮度驱动返回错误 ({status})"));
            }
            Ok(())
        }
    }
}
const PREPARING: u8 = 0;
const ISSUED: u8 = 1;
const CANCELLED: u8 = 2;

struct BrightnessReport {
    actual: Option<u8>,
    result: Result<(), String>,
}

// The actor owns this single slot. WMI can stall even during connection or
// method discovery, so COM and the entire transaction stay on its worker.
struct BrightnessJob {
    panel: PanelState,
    write: bool,
    gate: Arc<AtomicU8>,
    reply: Receiver<BrightnessReport>,
}
impl BrightnessJob {
    fn cancel_before_write(&self) {
        let _ = self.gate.compare_exchange(
            PREPARING,
            CANCELLED,
            Ordering::AcqRel,
            Ordering::Acquire,
        );
    }
    fn may_write(&self) -> bool {
        self.write && self.gate.load(Ordering::Acquire) != CANCELLED
    }
}
impl Drop for BrightnessJob {
    fn drop(&mut self) {
        self.cancel_before_write();
    }
}

fn run_brightness(panel: &PanelState, target: Option<u8>, gate: &AtomicU8) -> BrightnessReport {
    let mut actual = None;
    let result = (|| {
        let instance = panel.instance.as_deref().ok_or("无法确认内屏实例")?;
        let wmi = BrightnessWmi::open()?;
        let Some(level) = target else {
            let current = wmi.read(instance)?;
            panel.check_current()?;
            actual = Some(current);
            return Ok(());
        };
        let write = wmi.write(panel, instance, level, gate);
        if gate.load(Ordering::Acquire) == CANCELLED {
            return write;
        }
        let mut last_error = None;
        for _ in 0..if write.is_ok() { 10 } else { 1 } {
            if write.is_ok() {
                thread::sleep(Duration::from_millis(100));
            }
            let reading = wmi.read(instance);
            if let Err(error) = panel.check_current() {
                actual = None;
                return Err(error);
            }
            match reading {
                Ok(current) => {
                    actual = Some(current);
                    if current == level && write.is_ok() {
                        return Ok(());
                    }
                }
                Err(error) => last_error = Some(error),
            }
        }
        write?;
        Err(last_error.unwrap_or_else(|| "亮度回读未确认成功".into()))
    })();
    BrightnessReport { actual, result }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct TrialTicket {
    pub id: String,
    pub target: u32,
    pub deadline_unix_ms: u128,
}
struct Trial {
    ticket: TrialTicket,
    previous: PanelState,
    started: Instant,
}
#[derive(Default)]
pub struct Display {
    pub panel: Option<PanelState>,
    pub brightness: Option<u8>,
    last_read_attempt: Option<Instant>,
    panel_read_error: Option<String>,
    brightness_read_error: Option<String>,
    brightness_job: Option<BrightnessJob>,
    trial: Option<Trial>,
    pub last_trial: Option<Value>,
    low_on_battery: bool,
    applied_ac: Option<bool>,
    was_enabled: bool,
    restore_rate: Option<(String, u32)>,
    pending: bool,
    last_policy: Option<Instant>,
    last_power: Option<bool>,
}
impl Display {
    pub fn new(low: bool) -> Self {
        Self {
            low_on_battery: low,
            ..Self::default()
        }
    }
    fn visible_read_due(&self, now: Instant) -> bool {
        self.trial.is_none()
            && self.brightness_job.is_none()
            && (self.panel.is_none() || self.brightness.is_none())
            && self
                .last_read_attempt
                .is_none_or(|at| now.saturating_duration_since(at) >= Duration::from_secs(3))
    }
    pub fn refresh_visible(&mut self) {
        let now = Instant::now();
        if !self.visible_read_due(now) {
            return;
        }
        if let Some(panel) = self.panel.clone() {
            self.last_read_attempt = Some(now);
            match panel.check_current() {
                Ok(()) => self.read_brightness(&panel),
                Err(error) => {
                    self.invalidate();
                    self.panel_read_error = Some(error);
                }
            }
        } else {
            let _ = self.read();
        }
    }
    pub fn read(&mut self) -> Result<(), String> {
        self.last_read_attempt = Some(Instant::now());
        let panel = match PanelState::read() {
            Ok(panel) => panel,
            Err(error) => {
                self.panel = None;
                self.brightness = None;
                self.panel_read_error = Some(error.clone());
                self.brightness_read_error = None;
                return Err(error);
            }
        };
        if !self.panel.as_ref().is_some_and(|prior| prior.same_identity(&panel)) {
            self.brightness = None;
            self.brightness_read_error = None;
        }
        self.panel_read_error = None;
        self.panel = Some(panel.clone());
        if self.brightness.is_none() {
            self.read_brightness(&panel);
        }
        Ok(())
    }
    pub fn read_for_panel(&mut self) -> Result<(), String> {
        if self.trial.is_some() {
            return Err("刷新率仍在确认".into());
        }
        if self
            .last_read_attempt
            .is_some_and(|at| at.elapsed() < Duration::from_secs(3))
        {
            return if self.panel.is_some() {
                Ok(())
            } else {
                Err(self.panel_read_error.clone().unwrap_or_else(|| "内屏待读取".into()))
            };
        }
        self.read()
    }
    fn read_brightness(&mut self, panel: &PanelState) {
        if self.brightness_job.is_some() {
            return;
        }
        if let Err(error) = self.start_brightness(panel.clone(), None) {
            self.brightness_read_error = Some(error);
            return;
        }
        let _ = self.receive_brightness(Duration::from_millis(200));
    }
    fn start_brightness(&mut self, panel: PanelState, target: Option<u8>) -> Result<(), String> {
        if self.brightness_job.is_some() {
            return Err("上一次亮度读取或设置仍在等待驱动返回".into());
        }
        if panel.instance.is_none() {
            return Err("无法确认内屏实例，亮度通道不可用".into());
        }
        let gate = Arc::new(AtomicU8::new(PREPARING));
        let worker_gate = gate.clone();
        let worker_panel = panel.clone();
        let (sender, reply) = mpsc::channel();
        thread::Builder::new()
            .name("OpenRevo.BrightnessWmi".into())
            .spawn(move || {
                let report = run_brightness(&worker_panel, target, &worker_gate);
                let _ = sender.send(report);
            })
            .map_err(|error| error.to_string())?;
        self.brightness_job = Some(BrightnessJob {
            panel,
            write: target.is_some(),
            gate,
            reply,
        });
        Ok(())
    }
    fn receive_brightness(&mut self, wait: Duration) -> Option<Result<(), String>> {
        let job = self.brightness_job.as_ref()?;
        let report = match job.reply.recv_timeout(wait) {
            Ok(report) => report,
            Err(RecvTimeoutError::Timeout) => return None,
            Err(RecvTimeoutError::Disconnected) => BrightnessReport {
                actual: None,
                result: Err("亮度读取线程未返回".into()),
            },
        };
        let job = self.brightness_job.take()?;
        if !self.panel.as_ref().is_some_and(|panel| panel.same_identity(&job.panel)) {
            return Some(Err("内屏已变化，未采用旧亮度回读".into()));
        }
        self.brightness = report.actual;
        self.brightness_read_error = if report.actual.is_none() {
            report.result.as_ref().err().cloned()
        } else {
            None
        };
        Some(report.result)
    }
    pub fn poll_brightness(&mut self) -> bool {
        self.receive_brightness(Duration::ZERO).is_some()
    }
    pub fn snapshot(&self) -> Value {
        json!({
            "Panel":self.panel, "Brightness":self.brightness,
            "ReadAttempted":self.last_read_attempt.is_some(),
            "PanelReadError":self.panel.is_none().then_some(self.panel_read_error.as_deref()).flatten(),
            "BrightnessReadError":self.brightness.is_none().then_some(self.brightness_read_error.as_deref()).flatten(),
            "Trial":self.trial.as_ref().map(|t|&t.ticket), "LastTrial":self.last_trial,
            "LowRefreshOnBattery":self.low_on_battery
        })
    }
    pub fn set_brightness(&mut self, level: u8) -> Result<(), String> {
        if !(10..=100).contains(&level) || self.trial.is_some() {
            return Err("亮度超出范围或刷新率仍在确认".into());
        }
        self.poll_brightness();
        let expected = self.panel.clone().ok_or("缺少有效内屏快照，请先读取")?;
        self.start_brightness(expected, Some(level))?;
        let result = self.receive_brightness(Duration::from_millis(1600));
        self.last_read_attempt = Some(Instant::now());
        if let Some(result) = result {
            return result;
        }
        if let Some(job) = &self.brightness_job {
            job.cancel_before_write();
        }
        self.brightness = None;
        let error = "亮度驱动响应超时，等待实际状态回读".to_string();
        self.brightness_read_error = Some(error.clone());
        Err(error)
    }
    fn brightness_write_pending(&self) -> bool {
        self.brightness_job.as_ref().is_some_and(BrightnessJob::may_write)
    }
    pub fn begin_trial(&mut self, expected: u32, target: u32) -> Result<TrialTicket, String> {
        if self.brightness_write_pending() {
            return Err("亮度设置仍在等待驱动返回".into());
        }
        if self.trial.is_some() {
            return Err("上一项刷新率更改仍在等待确认".into());
        }
        let previous = self
            .panel
            .as_ref()
            .filter(|p| p.hertz == expected)
            .ok_or("缺少有效内屏快照，请先读取")?
            .clone();
        let id =
            unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?;
        let next = previous.change(target)?;
        let ticket = TrialTicket {
            id: format!("{id:?}"),
            target,
            deadline_unix_ms: super::platform::unix_ms() + 15000,
        };
        self.panel = Some(next);
        self.trial = Some(Trial {
            ticket: ticket.clone(),
            previous,
            started: Instant::now(),
        });
        Ok(ticket)
    }
    pub fn finish_trial(&mut self, id: &str, keep: bool) -> Result<(), String> {
        if !self
            .trial
            .as_ref()
            .is_some_and(|t| t.ticket.id.eq_ignore_ascii_case(id))
        {
            if let Some(last) = self
                .last_trial
                .as_ref()
                .filter(|v| v["Id"].as_str().is_some_and(|s| s.eq_ignore_ascii_case(id)))
            {
                return match last["Error"].as_str() {
                    Some(error) => Err(error.into()),
                    None if keep && last["Kept"] == false => {
                        Err("刷新率试切已恢复，无法再保留".into())
                    }
                    None if !keep && last["Kept"] == true => {
                        Err("刷新率试切已确认保留".into())
                    }
                    None => Ok(()),
                };
            }
            return Err("刷新率更改已失效，请重新读取".into());
        }
        let trial = self.trial.take().unwrap();
        let retain = keep && trial.started.elapsed() < Duration::from_secs(15);
        let result: Result<(), String> = (|| {
            let mut expected = trial.previous.clone();
            expected.hertz = trial.ticket.target;
            let panel = if retain {
                let panel = PanelState::read()?;
                if !expected.same_identity(&panel) || expected.hertz != panel.hertz {
                    return Err("内屏状态已变化，无法确认保留".into());
                }
                panel
            } else {
                expected.change(trial.previous.hertz)?
            };
            self.panel = Some(panel);
            Ok(())
        })();
        if result.is_err() {
            self.panel = None;
            self.brightness = None;
        }
        self.last_trial = Some(
            json!({"Id":trial.ticket.id,"Kept":retain,"ConfirmedHertz":self.panel.as_ref().map(|p|p.hertz),"Error":result.as_ref().err()}),
        );
        result?;
        if keep && !retain {
            return Err("确认期限已过，原刷新率已恢复".into());
        }
        Ok(())
    }
    pub fn trial_deadline(&self) -> Option<Instant> {
        self.trial
            .as_ref()
            .map(|trial| trial.started + Duration::from_secs(15))
    }
    pub fn expire(&mut self) -> bool {
        if let Some(id) = self
            .trial
            .as_ref()
            .filter(|t| t.started.elapsed() >= Duration::from_secs(15))
            .map(|t| t.ticket.id.clone())
        {
            let _ = self.finish_trial(&id, false);
            return true;
        }
        false
    }
    pub fn shutdown(&mut self) -> Result<(), String> {
        if let Some(job) = &self.brightness_job {
            job.cancel_before_write();
        }
        if let Some(id) = self.trial.as_ref().map(|t| t.ticket.id.clone()) {
            self.finish_trial(&id, false)
        } else {
            Ok(())
        }
    }
    pub fn invalidate(&mut self) {
        if let Some(job) = &self.brightness_job {
            job.cancel_before_write();
        }
        self.panel = None;
        self.brightness = None;
        self.last_read_attempt = None;
        self.panel_read_error = None;
        self.brightness_read_error = None;
        if self.was_enabled || self.restore_rate.is_some() {
            self.pending = true;
        }
    }
    pub fn set_policy(&mut self, directory: &std::path::Path, enabled: bool) -> Result<(), String> {
        use openrevo_core::system::app_preferences::AppPreferences;
        let bytes = AppPreferences::load_snapshot(directory, true).map_err(|_| "设置读取失败")?;
        let mut value: Value = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        value["LowRefreshOnBattery"] = json!(enabled);
        AppPreferences::save_snapshot(
            directory,
            &serde_json::to_vec(&value).map_err(|e| e.to_string())?,
        )
        .map_err(|_| "显示策略保存失败")?;
        self.low_on_battery = enabled;
        Ok(())
    }
    pub fn evaluate(&mut self) -> Result<(), String> {
        if self.trial.is_some() || self.brightness_write_pending() {
            return Ok(());
        }
        let ac = super::platform::ac_online();
        let enabled = self.low_on_battery;
        let needs = ac.is_some()
            && if enabled {
                !self.was_enabled || self.applied_ac != ac || self.pending
            } else {
                self.was_enabled || self.pending
            };
        if !needs
            || ac == self.last_power
                && self
                    .last_policy
                    .is_some_and(|at| at.elapsed() < Duration::from_secs(10))
        {
            return Ok(());
        }
        self.last_power = ac;
        self.last_policy = Some(Instant::now());
        let current = PanelState::read()?;
        let instance = current.instance.clone().ok_or("无法确认内屏实例")?;
        if self.restore_rate.as_ref().is_some_and(|(saved, _)| saved != &instance) {
            self.restore_rate = None;
        }
        let mut target = current.hertz;
        if enabled && ac == Some(false) {
            let min = *current.rates.iter().min().ok_or("缺少内屏刷新率")?;
            if current.hertz != min {
                self.restore_rate.get_or_insert((instance, current.hertz));
            }
            target = min;
        } else if let Some(restore) = self
            .restore_rate.as_ref()
            .map(|(_, rate)| *rate)
            .filter(|rate| current.rates.contains(rate))
        {
            target = restore;
        }
        let next = if target != current.hertz {
            current.change(target)?
        } else {
            current
        };
        if !self.panel.as_ref().is_some_and(|prior| prior.same_identity(&next)) {
            self.brightness = None;
            self.brightness_read_error = None;
        }
        self.panel = Some(next);
        if !enabled || ac == Some(true) {
            self.restore_rate = None;
        }
        self.applied_ac = ac;
        self.was_enabled = enabled;
        self.pending = false;
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn visible_read_recovers_missing_fields_with_backoff() {
        let now = Instant::now();
        let retry = now + Duration::from_secs(3);
        let mut display = Display::default();
        assert!(display.visible_read_due(now));

        display.last_read_attempt = Some(now);
        assert!(!display.visible_read_due(now + Duration::from_secs(2)));
        assert!(display.visible_read_due(retry));

        let panel = PanelState {
            width: 1920,
            height: 1080,
            hertz: 60,
            rates: vec![60, 120],
            device: r"\\.\DISPLAY1".into(),
            panel: "BOE0A1F".into(),
            monitor_path: r"\\?\DISPLAY#BOE0A1F#sample".into(),
            instance: Some(r"DISPLAY\BOE0A1F\sample".into()),
            source_adapter: Default::default(),
            target_adapter: Default::default(),
        };
        display.panel = Some(panel.clone());
        assert!(display.visible_read_due(retry));

        display.brightness = Some(0);
        assert!(!display.visible_read_due(retry));
        display.invalidate();
        assert!(display.visible_read_due(retry));

        display.trial = Some(Trial {
            ticket: TrialTicket {
                id: "test".into(),
                target: 120,
                deadline_unix_ms: 0,
            },
            previous: panel,
            started: now,
        });
        assert!(!display.visible_read_due(retry));
    }

    #[test]
    fn panel_code_preserves_exact_hardware_identity() {
        assert_eq!(
            panel_code(r"\\?\DISPLAY#BOE0A1F#sample"),
            Some("BOE0A1F".into())
        );
        assert_eq!(panel_code("virtual"), None);
    }

    #[test]
    fn brightness_matches_an_instance_not_a_monitor_model() {
        let internal = r"DISPLAY\BOE0A1F\4&abc&0&UID1";
        assert!(matches_wmi_instance(r"display\boe0a1f\4&abc&0&uid1_0", internal));
        assert!(matches_wmi_instance(internal, internal));
        assert!(!matches_wmi_instance(r"DISPLAY\BOE0A1F\4&abc&0&UID2_0", internal));
        assert!(!matches_wmi_instance(r"DISPLAY\BOE0A1F\4&abc&0&UID1_other", internal));
    }
}
