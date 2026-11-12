//! Native resident entry. Installed starts use the existing protected installation validator.
use super::{
    platform,
    runtime::{Client, Command, Runtime},
};
use openrevo_core::system::{
    resident_entry::{ResidentEntry, ResidentKind},
    resident_window::{self, TrayMessages},
};
use std::{
    cell::RefCell,
    sync::{
        atomic::{AtomicIsize, AtomicU32, Ordering},
        OnceLock,
    },
};
use windows::{
    core::{GUID, PCWSTR},
    Win32::{
        Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM},
        System::{RemoteDesktop::ProcessIdToSessionId, Threading::GetCurrentProcessId},
        UI::{Shell::IsUserAnAdmin, WindowsAndMessaging::*},
    },
};
static CLIENT: OnceLock<Client> = OnceLock::new();
static WINDOW: AtomicIsize = AtomicIsize::new(0);
static WAKE: AtomicU32 = AtomicU32::new(0);
const TRAY: u32 = 0x8001;
const REFRESH: u32 = 0x8002;
const STATE_CHANGED: u32 = 0x8003;
const CAPS_CHANGED: u32 = 0x8004;
const SCREEN_OFF_REQUEST: u32 = 0x8005;
pub(super) fn request_exit() {
    let window = WINDOW.load(Ordering::Acquire);
    if window != 0 {
        let _ = unsafe { PostMessageW(HWND(window as *mut _), WM_CLOSE, WPARAM(0), LPARAM(0)) };
    }
}
pub(super) fn request_screen_off() -> Result<(), String> {
    let window = WINDOW.load(Ordering::Acquire);
    if window == 0 {
        return Err("后台窗口尚未就绪".into());
    }
    unsafe {
        PostMessageW(
            HWND(window as *mut _),
            SCREEN_OFF_REQUEST,
            WPARAM(0),
            LPARAM(0),
        )
    }
    .map_err(|e| e.to_string())
}
// Windows SDK 26100 winnt.h: do not treat AC/DC-source value 0 as a closed lid.
const LID: GUID = GUID::from_u128(0xba3e0f4d_b817_4094_a2d1_d56379e6a0f3);
const AC_DC: GUID = GUID::from_u128(0x5d3e9a59_e9d5_4b00_a6bd_ff34ff516548);
const BATTERY_PERCENT: GUID = GUID::from_u128(0xa7ad8041_b45a_4cae_87a3_eecbb468a9e1);
#[link(name = "user32")]
extern "system" {
    fn RegisterPowerSettingNotification(window: isize, setting: *const GUID, flags: u32) -> isize;
    fn UnregisterPowerSettingNotification(handle: isize) -> i32;
}
struct LidRegistration(isize);
impl Drop for LidRegistration {
    fn drop(&mut self) {
        if self.0 != 0 {
            unsafe {
                UnregisterPowerSettingNotification(self.0);
            }
        }
    }
}
pub fn run() -> i32 {
    let args: Vec<String> = std::env::args().skip(1).collect();

    let installed =
        matches!(args.as_slice(),[arg]if arg=="--installed-open"||arg=="--installed-logon");
    let paired = if installed || args == ["--isolated-candidate", "--native-panel"] {
        true
    } else if args == ["--isolated-candidate", "--native-runtime-development"] {
        false
    } else {
        return 2;
    };
    let mut session = 0;
    if unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) }.is_err() || session == 0
    {
        return 2;
    }
    if !unsafe { IsUserAnAdmin() }.as_bool() {
        return 3;
    }
    match start(
        paired,
        installed,
        !installed || args[0] == "--installed-open",
    ) {
        Ok(()) => 0,
        Err(error) => {
            let text: Vec<u16> = error.encode_utf16().chain(Some(0)).collect();
            unsafe {
                MessageBoxW(
                    None,
                    PCWSTR(text.as_ptr()),
                    windows::core::w!("Open-Revo"),
                    MB_OK | MB_ICONERROR,
                )
            };
            1
        }
    }
}
fn start(paired: bool, installed: bool, open: bool) -> Result<(), String> {
    let kind = if installed {
        ResidentKind::Installed
    } else {
        ResidentKind::Candidate
    };
    let lease = match kind.enter(open).map_err(|e| e.to_string())? {
        ResidentEntry::Acquired(lease) => lease,
        ResidentEntry::AlreadyRunning => return Ok(()),
        ResidentEntry::Blocked => return Err("另一后台或控制台已拥有硬件".into()),
    };
    let base = std::env::current_exe()
        .map_err(|e| e.to_string())?
        .parent()
        .ok_or("可执行目录无效")?
        .to_path_buf();
    let data = if installed {
        let context = super::tools::context()?;
        std::path::PathBuf::from(context["DataDirectory"].as_str().ok_or("缺少数据目录")?)
    } else {
        platform::candidate_data(&base)?
    };
    let runtime = RefCell::new(Runtime::start(data, installed, || {
        let value = WINDOW.load(Ordering::Acquire);
        if value != 0 {
            let _ = unsafe { PostMessageW(HWND(value as *mut _), REFRESH, WPARAM(0), LPARAM(0)) };
            let _ =
                unsafe { PostMessageW(HWND(value as *mut _), STATE_CHANGED, WPARAM(0), LPARAM(0)) };
        }
    })?);
    CLIENT
        .set(runtime.borrow().client.clone())
        .map_err(|_| "原生宿主不能在同一进程重复创建")?;
    if paired {
        PANEL.with(|p| {
            *p.borrow_mut() = Some(super::panel::Controller::new(
                base.clone(),
                runtime.borrow().client.clone(),
            ));
        });
    }
    let icon: Vec<u16> = base
        .join("App.ico")
        .as_os_str()
        .to_string_lossy()
        .encode_utf16()
        .chain(Some(0))
        .collect();
    let registration = RefCell::new(Vec::<LidRegistration>::new());
    let stopped = RefCell::new(Ok(()));
    let result = unsafe {
        resident_window::run(
            kind,
            PCWSTR(icon.as_ptr()),
            TrayMessages {
                callback: TRAY,
                refresh: REFRESH,
            },
            Some(procedure),
            |buffer| {
                buffer.fill(0);
                let snapshot = CLIENT.get().map(Client::read).unwrap_or_default();
                let mode = match snapshot["Mode"].as_u64() {
                    Some(1) => "办公",
                    Some(2) => "均衡",
                    Some(3) => "狂暴",
                    Some(4) => "自定义",
                    _ => "待读取",
                };
                let state = if snapshot["Active"] == true {
                    "已接管"
                } else {
                    "未接管"
                };
                let mut label = format!("Open-Revo · {state} · {mode}");
                if snapshot["PanelPreferences"]["TrayTelemetry"] == true {
                    let sample = &snapshot["TraySample"];
                    let temp = |key: &str| {
                        sample[key]
                            .as_i64()
                            .map(|v| format!("{v}°C"))
                            .unwrap_or_else(|| "—".into())
                    };
                    let percent = |key: &str| {
                        sample[key]
                            .as_f64()
                            .map(|v| format!("{v:.0}%"))
                            .unwrap_or_else(|| "—".into())
                    };
                    label.push_str(&format!(
                        "\nCPU {} · {}",
                        temp("Cpu"),
                        percent("CpuPercent")
                    ));
                    if sample["GpuRead"] == true {
                        label.push_str(&format!(
                            "\nGPU {} · {}",
                            temp("Gpu"),
                            percent("GpuPercent")
                        ));
                    }
                }
                let text: Vec<u16> = label.encode_utf16().collect();
                let length = text.len().min(buffer.len().saturating_sub(1));
                buffer[..length].copy_from_slice(&text[..length]);
            },
            |window, wake| {
                WAKE.store(wake, Ordering::Release);
                WINDOW.store(window.0 as isize, Ordering::Release);
                for setting in [LID, AC_DC, BATTERY_PERCENT] {
                    let handle = RegisterPowerSettingNotification(window.0 as isize, &setting, 0);
                    if handle != 0 {
                        registration.borrow_mut().push(LidRegistration(handle));
                    }
                }
                PRESENTATION.with(|p| p.borrow_mut().observe());
                if paired && open {
                    open_panel(window, 0);
                }
                true
            },
            || {
                PANEL.with(|p| {
                    if let Some(controller) = p.borrow_mut().as_mut() {
                        controller.begin_stop();
                    }
                });
                PRESENTATION.with(|p| {
                    let mut presentation = p.borrow_mut();
                    presentation.osd.take();
                    presentation.caps.take();
                });
                registration.borrow_mut().clear();
                let owner = runtime.borrow_mut().finish();
                let panel = PANEL.with(|p| {
                    p.borrow_mut()
                        .take()
                        .map(|mut controller| controller.finish())
                        .unwrap_or(Ok(()))
                });
                *stopped.borrow_mut() = owner.and(panel);
                WINDOW.store(0, Ordering::Release);
            },
        )
    };
    drop(lease);
    result.map_err(|error| format!("后台托盘窗口运行失败：{error}"))?;
    stopped.into_inner()
}
unsafe extern "system" fn procedure(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    // Never unwind across a Win32 callback.
    std::panic::catch_unwind(|| on_message(window, message, wparam, lparam)).unwrap_or(LRESULT(0))
}
fn post(command: Command) {
    if let Some(client) = CLIENT.get() {
        let _ = client.submit(command, true);
    }
}
unsafe fn on_message(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if message != 0 && message == WAKE.load(Ordering::Acquire) {
        open_panel(window, 0);
        return LRESULT(0);
    }
    match message {
        super::panel::CLOSED => {
            PANEL.with(|p| {
                if let Some(controller) = p.borrow_mut().as_mut() {
                    controller.closed(window.0 as isize, wparam.0 as u32);
                }
            });
            LRESULT(0)
        }
        super::panel::SHOW_MENU => {
            show_menu(window);
            LRESULT(0)
        }
        SCREEN_OFF_REQUEST => DefWindowProcW(
            window,
            WM_SYSCOMMAND,
            WPARAM(SC_MONITORPOWER as usize),
            LPARAM(2),
        ),
        CAPS_CHANGED => {
            PRESENTATION.with(|p| {
                p.borrow_mut()
                    .show_external(super::osd::Message::feature("caps_lock", wparam.0 != 0))
            });
            LRESULT(0)
        }
        STATE_CHANGED => {
            PRESENTATION.with(|p| p.borrow_mut().observe());
            PANEL.with(|p| {
                if let Some(controller) = p.borrow().as_ref() {
                    controller.signal_snapshot();
                }
            });
            LRESULT(0)
        }
        TRAY => {
            let event = lparam.0 as u32 & 0xffff;
            if matches!(event, 0x200 | 0x406) {
                if let Some(client) = CLIENT.get() {
                    client.request_tray_sample();
                }
            }
            if matches!(event, WM_LBUTTONUP | 0x400 | 0x401) && PANEL.with(|p| p.borrow().is_some())
            {
                open_panel(window, 0);
            } else if matches!(
                event,
                WM_CONTEXTMENU | WM_LBUTTONUP | WM_RBUTTONUP | 0x400 | 0x401
            ) {
                show_menu(window);
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            command(window, (wparam.0 & 0xffff) as u32);
            LRESULT(0)
        }
        WM_CLOSE => {
            PANEL.with(|p| {
                if let Some(controller) = p.borrow_mut().as_mut() {
                    controller.begin_stop();
                }
            });
            if let Some(client) = CLIENT.get() {
                client.stop_accepting();
            }
            PostQuitMessage(0);
            LRESULT(0)
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            LRESULT(0)
        }
        WM_QUERYENDSESSION => LRESULT(1),
        WM_ENDSESSION if wparam.0 != 0 => {
            PANEL.with(|p| {
                if let Some(controller) = p.borrow_mut().as_mut() {
                    controller.begin_stop();
                }
            });
            if let Some(client) = CLIENT.get() {
                client.stop_accepting();
            }
            PostQuitMessage(0);
            LRESULT(0)
        }
        WM_DISPLAYCHANGE => {
            post(Command::DisplayTopologyChanged);
            LRESULT(0)
        }
        WM_POWERBROADCAST => {
            let sleeping = match wparam.0 {
                4 => Some(true),
                7 | 18 => Some(false),
                10 => {
                    post(Command::PowerStatusChanged);
                    None
                }
                0x8013 if lparam.0 != 0 => {
                    let data = lparam.0 as *const u8;
                    let guid = std::ptr::read_unaligned(data.cast::<GUID>());
                    let length = std::ptr::read_unaligned(data.add(16).cast::<u32>());
                    if (guid == AC_DC || guid == BATTERY_PERCENT) && length == 4 {
                        post(Command::PowerStatusChanged);
                        None
                    } else if guid == LID && length == 4 {
                        Some(std::ptr::read_unaligned(data.add(20).cast::<u32>()) == 0)
                    } else {
                        None
                    }
                }
                _ => None,
            };
            if let Some(sleeping) = sleeping {
                post(Command::Suspension(sleeping));
            }
            LRESULT(1)
        }
        _ => DefWindowProcW(window, message, wparam, lparam),
    }
}
unsafe fn show_menu(window: HWND) {
    let Ok(menu) = CreatePopupMenu() else {
        return;
    };
    let paired = PANEL.with(|p| p.borrow().is_some());
    let controls: &[(usize, &str)] = if paired {
        &[
            (100, "打开面板"),
            (101, "打开小面板"),
            (102, "打开完整面板"),
            (103, "设置"),
            (104, "更新"),
            (99, "退出 OpenRevo"),
        ]
    } else {
        &[
            (1, "重新读取并接管"),
            (2, "释放接管"),
            (11, "办公"),
            (12, "均衡"),
            (13, "狂暴"),
            (21, "限充 60%"),
            (22, "限充 80%"),
            (23, "限充 100%"),
            (31, "切换强冷"),
            (32, "切换防休眠"),
            (99, "退出 OpenRevo"),
        ]
    };
    for (id, text) in controls {
        let text: Vec<u16> = text.encode_utf16().chain(Some(0)).collect();
        let _ = AppendMenuW(menu, MF_STRING, *id, PCWSTR(text.as_ptr()));
    }
    let mut point = POINT::default();
    let _ = GetCursorPos(&mut point);
    let _ = SetForegroundWindow(window);
    let selected = TrackPopupMenu(
        menu,
        TPM_RETURNCMD | TPM_NONOTIFY,
        point.x,
        point.y,
        0,
        window,
        None,
    )
    .0 as u32;
    let _ = DestroyMenu(menu);
    command(window, selected);
}
unsafe fn command(window: HWND, id: u32) {
    match id {
        100..=104 => open_panel(window, id - 100),
        1 => post(Command::Takeover),
        2 => post(Command::Release),
        11..=13 => post(Command::Factory {
            mode: (id - 10) as u8,
            dc_confirmed: true,
        }),
        21..=23 => post(Command::Battery(match id {
            21 => 60,
            22 => 80,
            _ => 100,
        })),
        31 => post(Command::ToggleBoost),
        32 => post(Command::ToggleAwake),
        99 => {
            let _ = PostMessageW(window, WM_CLOSE, WPARAM(0), LPARAM(0));
        }
        _ => {}
    }
}

thread_local! {static PRESENTATION:RefCell<Presentation>=RefCell::new(Presentation::default());}
#[derive(Default)]
struct Presentation {
    osd: Option<super::osd::Osd>,
    caps: Option<CapsHook>,
    mode: Option<u8>,
    boost: Option<bool>,
    awake: Option<bool>,
    trial: Option<String>,
    last_error: Option<String>,
    notice_sequence: u64,
}
impl Presentation {
    fn show_external(&mut self, message: super::osd::Message) {
        let Some(client) = CLIENT.get() else {
            return;
        };
        let snapshot = client.raw_read();
        let settings = &snapshot["Osd"];
        if settings["Enabled"] != true {
            return;
        }
        let result = (|| -> Result<(), String> {
            if self.osd.is_none() {
                self.osd = Some(super::osd::Osd::new()?);
            }
            self.osd.as_mut().unwrap().show(message, settings)
        })();
        if let Err(error) = result {
            if let Some(osd) = &mut self.osd {
                osd.hide();
            }
            self.last_error = Some(error);
        }
    }
    fn observe(&mut self) {
        let Some(client) = CLIENT.get() else {
            return;
        };
        let snapshot = client.raw_read();
        let settings = &snapshot["Osd"];
        let mode = if snapshot["Active"] == true {
            snapshot["Mode"]
                .as_u64()
                .and_then(|m| u8::try_from(m).ok())
                .filter(|m| (1..=4).contains(m))
        } else {
            None
        };
        let boost = if snapshot["Active"] == true {
            snapshot["Control"]["FanClamp"]
                .as_u64()
                .map(|v| v & 64 != 0)
        } else {
            None
        };
        let awake = snapshot["KeepAwake"].as_bool();
        let trial = snapshot["Display"]["LastTrial"]["Id"]
            .as_str()
            .map(str::to_owned);
        let mut message = None;
        if self.mode.is_some() && mode.is_some() && self.mode != mode {
            message = mode.map(super::osd::Message::mode);
        } else if self.boost.is_some() && boost.is_some() && self.boost != boost {
            message = boost.map(|on| super::osd::Message::feature("fan_boost", on));
        } else if self.awake.is_some() && awake.is_some() && self.awake != awake {
            message = awake.map(|on| super::osd::Message::feature("keep_awake", on));
        } else if trial.is_some() && self.trial != trial {
            if let Some(hertz) = snapshot["Display"]["LastTrial"]["ConfirmedHertz"].as_u64() {
                let kept = snapshot["Display"]["LastTrial"]["Kept"] == true;
                message = Some(super::osd::Message {
                    text: format!(
                        "{} · {hertz} Hz",
                        if kept {
                            "屏幕刷新率"
                        } else {
                            "已恢复刷新率"
                        }
                    ),
                    icon: super::osd::DISPLAY_ICON,
                    accent: 0xFFB992,
                    dot: None,
                    steps: None,
                });
            }
        }
        let notice = &snapshot["OsdNotice"];
        if let Some(sequence) = notice["Sequence"].as_u64() {
            if sequence != self.notice_sequence {
                self.notice_sequence = sequence;
                if let Some(feature) = notice["Feature"].as_str() {
                    if feature == "brightness" {
                        if let Some(level) = notice["Value"].as_u64() {
                            message = Some(super::osd::Message {
                                text: format!("屏幕亮度 · {level}%"),
                                icon: super::osd::DISPLAY_ICON,
                                accent: 0xFFB992,
                                dot: None,
                                steps: None,
                            });
                        }
                    } else if let Some(enabled) = notice["Value"].as_bool() {
                        message = Some(super::osd::Message::feature(feature, enabled));
                    }
                }
            }
        }
        self.mode = mode;
        self.boost = boost;
        self.awake = awake;
        self.trial = trial;
        if settings["Enabled"] != true {
            self.osd.take();
            self.caps.take();
            return;
        }
        if self.caps.is_none() {
            let (caps, error) = CapsHook::new();
            self.caps = Some(caps);
            if error.is_some() {
                self.last_error = error;
            }
        }
        if let Some(message) = message {
            let result = (|| -> Result<(), String> {
                if self.osd.is_none() {
                    self.osd = Some(super::osd::Osd::new()?);
                }
                self.osd.as_mut().unwrap().show(message, settings)
            })();
            if let Err(error) = result {
                if let Some(osd) = &mut self.osd {
                    osd.hide();
                }
                self.last_error = Some(error);
            }
        }
    }
}

struct CapsHook {
    handle: HHOOK,
}
impl CapsHook {
    fn new() -> (Self, Option<String>) {
        unsafe {
            let result = windows::Win32::System::LibraryLoader::GetModuleHandleW(PCWSTR::null())
                .and_then(|module| {
                    SetWindowsHookExW(
                        WH_KEYBOARD_LL,
                        Some(caps_procedure),
                        windows::Win32::Foundation::HINSTANCE(module.0),
                        0,
                    )
                });
            match result {
                Ok(handle) => (Self { handle }, None),
                Err(error) => (
                    Self {
                        handle: HHOOK::default(),
                    },
                    Some(error.to_string()),
                ),
            }
        }
    }
}
impl Drop for CapsHook {
    fn drop(&mut self) {
        if !self.handle.0.is_null() {
            let _ = unsafe { UnhookWindowsHookEx(self.handle) };
        }
    }
}
unsafe extern "system" fn caps_procedure(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code >= 0 && matches!(wparam.0 as u32, WM_KEYUP | WM_SYSKEYUP) && lparam.0 != 0 {
        let key = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
        if key.vkCode == 0x14 {
            let window = WINDOW.load(Ordering::Acquire);
            if window != 0 {
                let locked =
                    windows::Win32::UI::Input::KeyboardAndMouse::GetKeyState(0x14) & 1 != 0;
                let _ = PostMessageW(
                    HWND(window as *mut _),
                    CAPS_CHANGED,
                    WPARAM(usize::from(locked)),
                    LPARAM(0),
                );
            }
        }
    }
    CallNextHookEx(HHOOK::default(), code, wparam, lparam)
}

thread_local! {static PANEL:RefCell<Option<super::panel::Controller>>=RefCell::new(None);}
fn open_panel(window: HWND, target: u32) {
    PANEL.with(|p| {
        if let Some(controller) = p.borrow_mut().as_mut() {
            if let Err(error) = controller.open(window.0 as isize, target) {
                let text: Vec<u16> = format!("无法打开配对面板：{error}")
                    .encode_utf16()
                    .chain(Some(0))
                    .collect();
                unsafe {
                    MessageBoxW(
                        window,
                        PCWSTR(text.as_ptr()),
                        windows::core::w!("OpenRevo Core 测试"),
                        MB_OK | MB_ICONERROR,
                    );
                }
            }
        }
    });
}
