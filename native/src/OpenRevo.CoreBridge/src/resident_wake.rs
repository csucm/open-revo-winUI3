use openrevo_core::system::resident_wake::{PanelWake, WakeError, WakeSnapshot};
use std::{panic::catch_unwind, ptr};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;
const E_OVERFLOW: i32 = 0x80070216_u32 as i32;

#[no_mangle]
pub unsafe extern "C" fn or_native_resident_wake_create(
    target: u32,
    output: *mut *mut PanelWake,
) -> i32 {
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    catch_unwind(|| match PanelWake::new(target) {
        Ok(state) => {
            output.write(Box::into_raw(Box::new(state)));
            0
        }
        Err(_) => E_INVALIDARG,
    })
    .unwrap_or(E_FAIL)
}

/// # Safety
/// The state must remain live and output must be writable until return.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_wake_apply(
    state: *const PanelWake,
    action: u32,
    value: i64,
    output: *mut WakeSnapshot,
) -> i32 {
    let Some(state) = state.as_ref() else {
        return E_INVALIDARG;
    };
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(WakeSnapshot::default());
    catch_unwind(|| {
        let result = match action {
            0 => Ok(state.snapshot()),
            1 => u32::try_from(value)
                .map_err(|_| WakeError::Invalid)
                .and_then(|target| state.raise(target)),
            2 => state.begin_detach(value),
            3 => state.mark_delivered(value),
            4 => Ok(state.stop()),
            _ => return E_INVALIDARG,
        };
        match result {
            Ok(snapshot) => {
                output.write(snapshot);
                0
            }
            Err(WakeError::Invalid) => E_INVALIDARG,
            Err(WakeError::SequenceExhausted) => E_OVERFLOW,
        }
    })
    .unwrap_or(E_FAIL)
}

/// # Safety
/// The state must be the sole owning pointer with no calls in flight.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_wake_close(state: *mut PanelWake) {
    if !state.is_null() {
        drop(Box::from_raw(state));
    }
}
