// Win-key filtering from the upstream desktop keyboard hook. Start the
// message pump only while Win lock is enabled.
use std::{sync::{Mutex, mpsc, atomic::{AtomicBool, Ordering}}, thread::{self, JoinHandle}};
use windows::Win32::{
    Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM},
    System::Threading::GetCurrentThreadId,
    UI::{Input::KeyboardAndMouse::{keybd_event, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, VK_LWIN, VK_RWIN}, WindowsAndMessaging::*},
};

static HOOK: Mutex<Option<(u32, JoinHandle<()>)>> = Mutex::new(None);

static WIN_DOWN: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn keyboard(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 {
        let event = &*(data.0 as *const KBDLLHOOKSTRUCT);
        let key = event.vkCode;
        if key == u32::from(VK_LWIN.0) || key == u32::from(VK_RWIN.0) {
            if event.flags.0 & 0x10 == 0 {
                let mask = match message.0 as u32 {
                    WM_KEYDOWN | WM_SYSKEYDOWN => !WIN_DOWN.swap(true, Ordering::SeqCst),
                    WM_KEYUP | WM_SYSKEYUP => {
                        WIN_DOWN.store(false, Ordering::SeqCst);
                        true
                    }
                    _ => false,
                };
                if mask {
                    keybd_event(0xE8, 0, KEYBD_EVENT_FLAGS(0), 0);
                    keybd_event(0xE8, 0, KEYEVENTF_KEYUP, 0);
                }
            }
            return LRESULT(1);
        }
    }
    CallNextHookEx(None, code, message, data)
}

pub fn is_locked() -> bool {
    HOOK.lock().unwrap().as_ref().is_some_and(|(_, thread)| !thread.is_finished())
}

pub fn set_locked(locked: bool) -> Result<(), String> {
    let mut worker = HOOK.lock().unwrap();
    if worker.as_ref().is_some_and(|(_, thread)| thread.is_finished()) {
        if let Some((_, thread)) = worker.take() { let _ = thread.join(); }
    }
    if !locked {
        if let Some((id, _)) = worker.as_ref() {
            unsafe { PostThreadMessageW(*id, WM_QUIT, WPARAM(0), LPARAM(0)) }
                .map_err(|e| e.to_string())?;
            if let Some((_, thread)) = worker.take() { let _ = thread.join(); }
        }
        return Ok(());
    }
    if worker.is_some() { return Ok(()); }
    WIN_DOWN.store(false, Ordering::SeqCst);
    let (ready, result) = mpsc::sync_channel(1);
    let thread = thread::Builder::new().name("OpenRevo Win lock".into()).spawn(move || unsafe {
        let mut message = MSG::default();
        let _ = PeekMessageW(&mut message, HWND::default(), 0, 0, PM_NOREMOVE);
        match SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard), HINSTANCE::default(), 0) {
            Ok(hook) => {
                if ready.send(Ok(GetCurrentThreadId())).is_ok() {
                    while GetMessageW(&mut message, HWND::default(), 0, 0).0 > 0 {}
                }
                let _ = UnhookWindowsHookEx(hook);
            }
            Err(error) => { let _ = ready.send(Err(error.to_string())); }
        }
    }).map_err(|e| e.to_string())?;
    let id = result.recv().map_err(|e| e.to_string())??;
    *worker = Some((id, thread));
    Ok(())
}
