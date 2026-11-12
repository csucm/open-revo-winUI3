use openrevo_core::system::session_power::SessionPower;
use std::{
    panic::{catch_unwind, AssertUnwindSafe},
    ptr,
};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;

/// `output` must point to writable storage. A successful result transfers one
/// owner to the caller, who must close it exactly once with the paired export.
#[no_mangle]
pub unsafe extern "C" fn or_native_session_power_create(output: *mut *mut SessionPower) -> i32 {
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    catch_unwind(|| {
        output.write(Box::into_raw(Box::new(SessionPower::default())));
        0
    })
    .unwrap_or(E_FAIL)
}

/// The session must be live and exclusively borrowed. A nonnegative result
/// is action-specific. A negative result is a failure HRESULT.
#[no_mangle]
pub unsafe extern "C" fn or_native_session_power_apply(
    session: *mut SessionPower,
    action: u32,
) -> i32 {
    let Some(session) = session.as_mut() else {
        return E_INVALIDARG;
    };
    catch_unwind(AssertUnwindSafe(|| {
        let result = match action {
            0 => return i32::from(session.keep_awake_enabled()),
            1 => session.toggle_keep_awake().map(i32::from),
            2 => return session.begin_screen_off() as i32,
            3 => session.prepare_screen_off().map(|()| 0),
            4 => session.capture_screen_off_input().map(|()| 0),
            5 => {
                session.finish_screen_off();
                return 0;
            }
            6 => session.cancel_screen_off().map(|()| 0),
            7 => session.restore_on_input().map(|()| 0),
            8 => session.shutdown().map(|()| 0),
            _ => return E_INVALIDARG,
        };
        match result {
            Ok(value) => value,
            Err(error) => error.code().0,
        }
    }))
    .unwrap_or(E_FAIL)
}

/// Releases the sole owning pointer returned by create.
#[no_mangle]
pub unsafe extern "C" fn or_native_session_power_close(session: *mut SessionPower) {
    if !session.is_null() {
        drop(Box::from_raw(session));
    }
}
