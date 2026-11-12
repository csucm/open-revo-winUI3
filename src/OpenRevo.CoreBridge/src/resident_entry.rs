use openrevo_core::system::resident_entry::{ResidentEntry, ResidentKind, ResidentLease};
use openrevo_core::system::resident_window::{self, TrayMessages, PCWSTR, WNDPROC};
use std::{panic::catch_unwind, ptr};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;

fn resident_kind(value: u32) -> Option<ResidentKind> {
    match value {
        0 => Some(ResidentKind::Candidate),
        1 => Some(ResidentKind::Installed),
        _ => None,
    }
}

/// The caller owns writable storage for the result. A lease is returned only
/// when all reservations succeed. The owning pointer must be closed once.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_enter(
    kind: u32,
    request_wake: u32,
    output: *mut *mut ResidentLease,
) -> i32 {
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    let Some(kind) = resident_kind(kind) else {
        return E_INVALIDARG;
    };
    if request_wake > 1 {
        return E_INVALIDARG;
    }
    catch_unwind(|| match kind.enter(request_wake != 0) {
        Ok(ResidentEntry::Acquired(lease)) => {
            output.write(Box::into_raw(Box::new(lease)));
            0
        }
        Ok(ResidentEntry::AlreadyRunning) => 1,
        Ok(ResidentEntry::Blocked) => 4,
        Err(error) => error.code().0,
    })
    .unwrap_or(E_FAIL)
}

#[no_mangle]
pub unsafe extern "C" fn or_native_resident_lease_close(lease: *mut ResidentLease) {
    if !lease.is_null() {
        drop(Box::from_raw(lease));
    }
}

/// # Safety
/// The callbacks must remain valid for the duration of this synchronous call
/// and must not unwind into Rust. The icon path is a NUL-terminated UTF-16
/// string. The tooltip callback can write up to the supplied length.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_tray_run(
    kind: u32,
    icon_path: *const u16,
    tray_callback: u32,
    refresh_tray: u32,
    procedure: WNDPROC,
    tooltip: Option<extern "system" fn(*mut u16, u32)>,
    ready: Option<extern "system" fn(isize, u32) -> i32>,
    stop: Option<extern "system" fn()>,
) -> i32 {
    let (Some(kind), Some(_), Some(tooltip), Some(ready), Some(stop)) =
        (resident_kind(kind), procedure, tooltip, ready, stop)
    else {
        return E_INVALIDARG;
    };
    if icon_path.is_null() {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        match resident_window::run(
            kind,
            PCWSTR::from_raw(icon_path),
            TrayMessages {
                callback: tray_callback,
                refresh: refresh_tray,
            },
            procedure,
            |buffer| tooltip(buffer.as_mut_ptr(), buffer.len() as u32),
            |window, wake| ready(window.0 as isize, wake) != 0,
            || stop(),
        ) {
            Ok(()) => 0,
            Err(error) => error.code().0,
        }
    })
    .unwrap_or(E_FAIL)
}
