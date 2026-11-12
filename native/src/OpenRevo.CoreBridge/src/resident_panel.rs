use openrevo_core::system::resident_panel::{self, PCWSTR};
use std::{
    os::windows::io::{IntoRawHandle, RawHandle},
    panic::catch_unwind,
    ptr,
};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;

#[no_mangle]
pub unsafe extern "C" fn or_native_resident_desktop_token(output: *mut RawHandle) -> i32 {
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    catch_unwind(|| match resident_panel::open_desktop_token() {
        Ok(token) => {
            output.write(token.into_raw_handle());
            0
        }
        Err(error) => error.code().0,
    })
    .unwrap_or(E_FAIL)
}

#[no_mangle]
pub unsafe extern "C" fn or_native_resident_panel_start(
    token: RawHandle,
    executable: *const u16,
    directory: *const u16,
    pipe: *const u16,
    child: *mut RawHandle,
    pid: *mut u32,
) -> i32 {
    if child.is_null() || pid.is_null() {
        return E_INVALIDARG;
    }
    child.write(ptr::null_mut());
    pid.write(0);
    if token.is_null() || pipe.is_null() {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        let Ok(pipe) = PCWSTR::from_raw(pipe).to_string() else {
            return E_INVALIDARG;
        };
        match resident_panel::start_panel(
            token,
            PCWSTR::from_raw(executable),
            PCWSTR::from_raw(directory),
            &pipe,
        ) {
            Ok((process_id, process)) => {
                pid.write(process_id);
                child.write(process.into_raw_handle());
                0
            }
            Err(error) => error.code().0,
        }
    })
    .unwrap_or(E_FAIL)
}
