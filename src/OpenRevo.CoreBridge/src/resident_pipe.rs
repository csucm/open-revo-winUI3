use openrevo_core::system::{
    resident_panel::PCWSTR,
    resident_pipe,
    resident_pipe_io::{PanelPipeIo, MAX_FRAME_BYTES, MAX_REQUEST_FRAME_BYTES},
    resident_request::RequestAdmission,
};
use std::{
    os::windows::io::{BorrowedHandle, IntoRawHandle, RawHandle},
    panic::catch_unwind,
    ptr, slice,
};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;
const E_INVALID_DATA: i32 = 0x8007000D_u32 as i32;

/// # Safety
/// The name and output must be valid for the duration of the call. A non-null
/// desktop handle must remain open until return.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_pipe_create(
    name: *const u16,
    desktop: RawHandle,
    output: *mut RawHandle,
) -> i32 {
    if output.is_null() {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    if name.is_null() || desktop as isize == -1 {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        let Ok(name) = PCWSTR::from_raw(name).to_string() else {
            return E_INVALIDARG;
        };
        let desktop = (!desktop.is_null()).then(|| BorrowedHandle::borrow_raw(desktop));
        match resident_pipe::create_local_server(&name, desktop) {
            Ok(pipe) => {
                output.write(pipe.into_raw_handle());
                0
            }
            Err(error) => error.code().0,
        }
    })
    .unwrap_or(E_FAIL)
}

/// # Safety
/// All handles must remain live until return. For upload reads and frame writes,
/// buffer is valid for count bytes.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_panel_io(
    operation: u32,
    pipe: RawHandle,
    stop: RawHandle,
    cancel: RawHandle,
    buffer: *mut u8,
    count: u32,
) -> i32 {
    if count as usize > MAX_FRAME_BYTES || (operation != 0 && (buffer.is_null() || count == 0)) {
        return E_INVALIDARG;
    }
    with_io(pipe, stop, cancel, |io| {
        let result = match operation {
            0 if count == 0 => io.connect(),
            1 => io.read_upload(slice::from_raw_parts_mut(buffer, count as usize)),
            2 => io.write_frame(slice::from_raw_parts(buffer, count as usize)),
            _ => return E_INVALIDARG,
        };
        match result {
            Ok(()) => 0,
            Err(error) => error.code().0,
        }
    })
}

/// # Safety
/// Handles must remain live and the output locations must be writable until
/// return. The caller must release a successful request frame exactly once.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_panel_read_request(
    pipe: RawHandle,
    stop: RawHandle,
    cancel: RawHandle,
    previous_id: i64,
    output: *mut *mut u8,
    count: *mut u32,
    admission: *mut RequestAdmission,
) -> i32 {
    if output.is_null() || count.is_null() || admission.is_null() || previous_id < 0 {
        return E_INVALIDARG;
    }
    output.write(ptr::null_mut());
    count.write(0);
    admission.write(RequestAdmission::default());
    with_io(pipe, stop, cancel, |io| match io.read_request_frame() {
        Ok(frame) => {
            let Some(header) = crate::resident::protocol::decode_after(&frame, previous_id) else {
                return E_INVALID_DATA;
            };
            admission.write(header);
            count.write(frame.len() as u32);
            output.write(Box::into_raw(frame).cast());
            0
        }
        Err(error) => error.code().0,
    })
}

/// # Safety
/// Buffer and count must be an unreleased result from read_request.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_panel_frame_free(buffer: *mut u8, count: u32) {
    if !buffer.is_null() && (1..=MAX_REQUEST_FRAME_BYTES).contains(&(count as usize)) {
        drop(Box::from_raw(ptr::slice_from_raw_parts_mut(
            buffer,
            count as usize,
        )));
    }
}

unsafe fn with_io(
    pipe: RawHandle,
    stop: RawHandle,
    cancel: RawHandle,
    call: impl FnOnce(PanelPipeIo<'_>) -> i32 + std::panic::UnwindSafe,
) -> i32 {
    if [pipe, stop, cancel]
        .iter()
        .any(|handle| handle.is_null() || *handle as isize == -1)
        || stop == cancel
    {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        call(PanelPipeIo::new(
            BorrowedHandle::borrow_raw(pipe),
            BorrowedHandle::borrow_raw(stop),
            BorrowedHandle::borrow_raw(cancel),
        ))
    })
    .unwrap_or(E_FAIL)
}

/// # Safety
/// The pipe handle must remain open until return.
#[no_mangle]
pub unsafe extern "C" fn or_native_resident_pipe_is_client(
    pipe: RawHandle,
    pid: u32,
    session: u32,
) -> i32 {
    if pipe.is_null() || pipe as isize == -1 {
        return 0;
    }
    catch_unwind(|| {
        i32::from(resident_pipe::is_client(
            BorrowedHandle::borrow_raw(pipe),
            pid,
            session,
        ))
    })
    .unwrap_or(0)
}
