use super::resident_entry::{ResidentKind, WAKE_MESSAGE};
use std::cell::Cell;
pub use windows::core::PCWSTR;
use windows::core::{w, Error, Result, HRESULT};
use windows::Win32::Foundation::{
    GetLastError, SetLastError, ERROR_SUCCESS, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIM_ADD, NIM_DELETE,
    NIM_MODIFY, NIM_SETVERSION, NOTIFYICONDATAW, NOTIFYICON_VERSION_4, NOTIFY_ICON_MESSAGE,
};
pub use windows::Win32::UI::WindowsAndMessaging::WNDPROC;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyWindow, DispatchMessageW, GetMessageW,
    GetWindowLongPtrW, IsWindow, LoadImageW, RegisterClassExW, RegisterWindowMessageW,
    SetWindowLongPtrW, TranslateMessage, UnregisterClassW, CREATESTRUCTW, GWLP_USERDATA, HICON,
    HMENU, IMAGE_ICON, LR_DEFAULTSIZE, LR_LOADFROMFILE, MSG, WM_CLOSE, WM_DESTROY, WM_NCCREATE,
    WM_NCDESTROY, WNDCLASSEXW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_POPUP,
};

pub struct TrayMessages {
    pub callback: u32,
    pub refresh: u32,
}

/// Owns the tray icon and window and pumps messages on the calling thread.
/// After readiness is attempted, stop runs before the window is destroyed.
///
/// # Safety
/// The procedure must accept all Win32 window messages, including those
/// delivered during creation and destruction. The procedure and tooltip
/// callbacks must never unwind. The icon path must point to a valid UTF-16
/// string for the duration of this call.
pub unsafe fn run(
    kind: ResidentKind,
    icon_path: PCWSTR,
    messages: TrayMessages,
    procedure: WNDPROC,
    tooltip: impl Fn(&mut [u16]),
    ready: impl FnOnce(HWND, u32) -> bool,
    stop: impl FnOnce(),
) -> Result<()> {
    let wake = register_message(WAKE_MESSAGE)?;
    let context = WindowContext {
        procedure,
        tooltip: &tooltip,
        taskbar: register_message(w!("TaskbarCreated"))?,
        refresh: messages.refresh,
        tray: TrayIcon::load(icon_path, messages.callback)?,
    };
    let window = ResidentWindow::create(kind.window_class(), &context)?;
    if !context.tray.add(window.handle, context.tooltip) {
        return Err(Error::from_hresult(HRESULT(0x80004005_u32 as i32))); // E_FAIL
    }
    let result = if ready(window.handle, wake) {
        pump_messages()
    } else {
        Err(Error::from_hresult(HRESULT(0x80004004_u32 as i32))) // E_ABORT
    };
    stop();
    result
}

struct WindowContext<'a> {
    procedure: WNDPROC,
    tooltip: &'a dyn Fn(&mut [u16]),
    taskbar: u32,
    refresh: u32,
    tray: TrayIcon,
}

unsafe extern "system" fn window_procedure(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    let mut data = GetWindowLongPtrW(window, GWLP_USERDATA);
    if message == WM_NCCREATE && data == 0 {
        if lparam.0 == 0 {
            return LRESULT(0);
        }
        let create = &*(lparam.0 as *const CREATESTRUCTW);
        data = create.lpCreateParams as isize;
        SetLastError(ERROR_SUCCESS);
        if SetWindowLongPtrW(window, GWLP_USERDATA, data) == 0 && GetLastError() != ERROR_SUCCESS {
            return LRESULT(0);
        }
    }
    let Some(context) = (data as *const WindowContext<'_>).as_ref() else {
        return DefWindowProcW(window, message, wparam, lparam);
    };
    let forward = |message, wparam, lparam| match context.procedure {
        Some(procedure) => procedure(window, message, wparam, lparam),
        None => DefWindowProcW(window, message, wparam, lparam),
    };
    match message {
        id if id == context.taskbar => {
            if context.tray.added.replace(false) && !context.tray.add(window, context.tooltip) {
                forward(WM_CLOSE, WPARAM(0), LPARAM(0));
            }
            return LRESULT(0);
        }
        id if id == context.refresh => {
            if context.tray.added.get() {
                context
                    .tray
                    .notify(window, NIM_MODIFY, Some(context.tooltip));
            }
            return LRESULT(0);
        }
        WM_DESTROY => context.tray.remove(window),
        WM_NCDESTROY => {
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        }
        _ => {}
    }
    forward(message, wparam, lparam)
}

fn register_message(name: PCWSTR) -> Result<u32> {
    let id = unsafe { RegisterWindowMessageW(name) };
    if id == 0 {
        Err(Error::from_win32())
    } else {
        Ok(id)
    }
}

fn pump_messages() -> Result<()> {
    let mut message = MSG::default();
    loop {
        unsafe {
            match GetMessageW(&mut message, HWND::default(), 0, 0).0 {
                0 => return Ok(()),
                -1 => return Err(Error::from_win32()),
                _ => {
                    let _ = TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
        }
    }
}

struct ResidentWindow<'a> {
    class: PCWSTR,
    instance: HINSTANCE,
    handle: HWND,
    context: &'a WindowContext<'a>,
}

impl<'a> ResidentWindow<'a> {
    fn create(class: PCWSTR, context: &'a WindowContext<'a>) -> Result<Self> {
        unsafe {
            let instance = HINSTANCE(GetModuleHandleW(PCWSTR::null())?.0);
            let definition = WNDCLASSEXW {
                cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
                hInstance: instance,
                lpfnWndProc: Some(window_procedure),
                lpszClassName: class,
                ..Default::default()
            };
            if RegisterClassExW(&definition) == 0 {
                return Err(Error::from_win32());
            }
            let mut window = Self {
                class,
                instance,
                handle: HWND::default(),
                context,
            };
            // Use a hidden top-level window to receive session and Explorer broadcasts.
            window.handle = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                class,
                w!(""),
                WS_POPUP,
                0,
                0,
                0,
                0,
                HWND::default(),
                HMENU::default(),
                instance,
                Some((context as *const WindowContext<'_>).cast()),
            )?;
            Ok(window)
        }
    }
}

impl Drop for ResidentWindow<'_> {
    fn drop(&mut self) {
        unsafe {
            if !self.handle.0.is_null() && IsWindow(self.handle).as_bool() {
                self.context.tray.remove(self.handle);
                SetWindowLongPtrW(self.handle, GWLP_USERDATA, 0);
                let _ = DestroyWindow(self.handle);
            }
            let _ = UnregisterClassW(self.class, self.instance);
        }
    }
}

struct TrayIcon {
    icon: HICON,
    callback: u32,
    added: Cell<bool>,
}

impl TrayIcon {
    unsafe fn load(path: PCWSTR, callback: u32) -> Result<Self> {
        let icon = LoadImageW(
            HINSTANCE::default(),
            path,
            IMAGE_ICON,
            0,
            0,
            LR_LOADFROMFILE | LR_DEFAULTSIZE,
        )?;
        Ok(Self {
            icon: HICON(icon.0),
            callback,
            added: Cell::new(false),
        })
    }

    fn add(&self, window: HWND, tooltip: &dyn Fn(&mut [u16])) -> bool {
        let added = self.notify(window, NIM_ADD, Some(tooltip));
        self.added.set(added);
        if added {
            self.notify(window, NIM_SETVERSION, None);
        }
        added
    }

    fn remove(&self, window: HWND) {
        if self.added.replace(false) {
            self.notify(window, NIM_DELETE, None);
        }
    }

    fn notify(
        &self,
        window: HWND,
        action: NOTIFY_ICON_MESSAGE,
        tooltip: Option<&dyn Fn(&mut [u16])>,
    ) -> bool {
        let mut data = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: window,
            uID: 1,
            ..Default::default()
        };
        if let Some(tooltip) = tooltip {
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = self.callback;
            data.hIcon = self.icon;
            tooltip(&mut data.szTip[..127]);
        }
        data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        unsafe { Shell_NotifyIconW(action, &data).as_bool() }
    }
}

impl Drop for TrayIcon {
    fn drop(&mut self) {
        let _ = unsafe { DestroyIcon(self.icon) };
    }
}
