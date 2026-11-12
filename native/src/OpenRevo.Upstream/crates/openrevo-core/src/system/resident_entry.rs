use std::{
    thread,
    time::{Duration, Instant},
};
use windows::core::{w, Result, HRESULT, PCWSTR};
use windows::Win32::Foundation::{
    CloseHandle, GetLastError, SetLastError, ERROR_ACCESS_DENIED, ERROR_ALREADY_EXISTS,
    ERROR_INVALID_HANDLE, ERROR_SUCCESS, HANDLE, LPARAM, WPARAM,
};
use windows::Win32::System::Threading::{CreateMutexExW, MUTEX_MODIFY_STATE};
use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, PostMessageW, RegisterWindowMessageW};

pub(super) const WAKE_MESSAGE: PCWSTR = w!("OpenRevo.Native.WakeWindow.v1");

#[derive(Clone, Copy)]
pub enum ResidentKind {
    Candidate,
    Installed,
}

pub enum ResidentEntry {
    Acquired(ResidentLease),
    AlreadyRunning,
    Blocked,
}

/// Holds object lifetimes only. The command queue and HAL locks serialize work.
pub struct ResidentLease {
    _hardware: NamedMutex,
    _legacy: NamedMutex,
    _instance: NamedMutex,
}

impl ResidentKind {
    pub fn enter(self, request_wake: bool) -> Result<ResidentEntry> {
        let name = match self {
            Self::Candidate => w!(r"Local\OpenRevo.RevoCore.ResidentCandidate"),
            Self::Installed => w!(r"Local\OpenRevo.RevoCore.Resident"),
        };
        let (instance, created) = NamedMutex::open(name)?;
        if !created {
            return Ok(if !request_wake || self.wake_existing() {
                ResidentEntry::AlreadyRunning
            } else {
                ResidentEntry::Blocked
            });
        }

        let (legacy, created) = NamedMutex::open(w!(r"Local\OpenRevo.Native.InstalledPreview"))?;
        if !created {
            return Ok(ResidentEntry::Blocked);
        }

        let hardware = match NamedMutex::open(w!(r"Global\OpenRevo.RevoCore.HardwareOwner")) {
            Ok((lease, true)) => lease,
            Ok((_, false)) => return Ok(ResidentEntry::Blocked),
            Err(error)
                if error.code() == HRESULT::from_win32(ERROR_ACCESS_DENIED.0)
                    || error.code() == HRESULT::from_win32(ERROR_INVALID_HANDLE.0) =>
            {
                return Ok(ResidentEntry::Blocked);
            }
            Err(error) => return Err(error),
        };
        Ok(ResidentEntry::Acquired(ResidentLease {
            _hardware: hardware,
            _legacy: legacy,
            _instance: instance,
        }))
    }

    pub(super) fn window_class(self) -> PCWSTR {
        match self {
            Self::Candidate => w!("OpenRevo.RevoCore.ResidentCandidate"),
            Self::Installed => w!("OpenRevo.RevoCore.Resident"),
        }
    }

    fn wake_existing(self) -> bool {
        let message = unsafe { RegisterWindowMessageW(WAKE_MESSAGE) };
        if message == 0 {
            return false;
        }
        let class = self.window_class();
        let started = Instant::now();
        loop {
            unsafe {
                if let Ok(window) = FindWindowW(class, PCWSTR::null()) {
                    if PostMessageW(window, message, WPARAM(0), LPARAM(0)).is_ok() {
                        return true;
                    }
                }
            }
            thread::sleep(Duration::from_millis(50));
            if started.elapsed() >= Duration::from_secs(5) {
                return false;
            }
        }
    }
}

struct NamedMutex(HANDLE);

impl NamedMutex {
    fn open(name: PCWSTR) -> Result<(Self, bool)> {
        // Match .NET Mutex's access mask and default, non-inheritable security.
        const MAXIMUM_ALLOWED: u32 = 0x0200_0000;
        const SYNCHRONIZE: u32 = 0x0010_0000;
        const ACCESS: u32 = MAXIMUM_ALLOWED | SYNCHRONIZE | MUTEX_MODIFY_STATE.0;
        unsafe {
            SetLastError(ERROR_SUCCESS);
            let handle = CreateMutexExW(None, name, 0, ACCESS)?;
            let created = GetLastError() != ERROR_ALREADY_EXISTS;
            Ok((Self(handle), created))
        }
    }
}

impl Drop for NamedMutex {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}
