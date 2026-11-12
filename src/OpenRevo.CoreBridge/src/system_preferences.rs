use openrevo_core::system::system_preferences::{
    SystemPreferenceEdit, SystemPreferences, SystemPreferencesSaveError,
    MAX_SYSTEM_PREFERENCES_BYTES,
};
use std::{
    ffi::OsString, os::windows::ffi::OsStringExt, panic::catch_unwind, path::PathBuf, ptr, slice,
};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;
const E_INSUFFICIENT_BUFFER: i32 = 0x8007007A_u32 as i32;

// The resident's verified data directory is passed by its own process. The
// bridge accepts no filenames or directory paths from panel commands.
pub(super) unsafe fn data_directory(directory: *const u16, length: usize) -> Option<PathBuf> {
    if directory.is_null() || !(1..=32767).contains(&length) {
        return None;
    }
    let units = slice::from_raw_parts(directory, length);
    if units.contains(&0) {
        return None;
    }
    let path = PathBuf::from(OsString::from_wide(units));
    (path.is_absolute() && path.parent().is_some()).then_some(path)
}

/// Caller owns both buffers. Returns the UTF-8 byte count, or a negative HRESULT.
#[no_mangle]
pub unsafe extern "C" fn or_native_system_preferences_load(
    directory: *const u16,
    length: usize,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        let Ok(bytes) = serde_json::to_vec(&SystemPreferences::load(&directory)) else {
            return E_FAIL;
        };
        if output.is_null() || capacity < bytes.len() {
            return E_INSUFFICIENT_BUFFER;
        }
        ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
        bytes.len() as i32
    })
    .unwrap_or(E_FAIL)
}

/// Validates and commits one complete snapshot. Callers serialize read-modify-write.
#[no_mangle]
pub unsafe extern "C" fn or_native_system_preferences_save(
    directory: *const u16,
    length: usize,
    input: *const u8,
    bytes: usize,
) -> i32 {
    if input.is_null() || !(1..=MAX_SYSTEM_PREFERENCES_BYTES).contains(&bytes) {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        let Some(settings) = SystemPreferences::from_json(slice::from_raw_parts(input, bytes))
        else {
            return E_INVALIDARG;
        };
        match settings.save(&directory) {
            Ok(()) => 0,
            Err(SystemPreferencesSaveError::Invalid) => E_INVALIDARG,
            Err(SystemPreferencesSaveError::Io(_)) => E_FAIL,
        }
    })
    .unwrap_or(E_FAIL)
}

/// Apply exactly one edit. Reserve the bounded result buffer before committing,
/// so the caller never needs a second mutation to fetch the new snapshot.
#[no_mangle]
pub unsafe extern "C" fn or_native_system_preferences_edit(
    directory: *const u16,
    length: usize,
    input: *const u8,
    bytes: usize,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    if input.is_null() || !(1..=MAX_SYSTEM_PREFERENCES_BYTES).contains(&bytes) {
        return E_INVALIDARG;
    }
    if output.is_null() || capacity < MAX_SYSTEM_PREFERENCES_BYTES {
        return E_INSUFFICIENT_BUFFER;
    }
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        let Ok(edit) =
            serde_json::from_slice::<SystemPreferenceEdit>(slice::from_raw_parts(input, bytes))
        else {
            return E_INVALIDARG;
        };
        let snapshot = match SystemPreferences::apply_edit(&directory, edit) {
            Ok((_, snapshot)) => snapshot,
            Err(SystemPreferencesSaveError::Invalid) => return E_INVALIDARG,
            Err(SystemPreferencesSaveError::Io(_)) => return E_FAIL,
        };
        ptr::copy_nonoverlapping(snapshot.as_ptr(), output, snapshot.len());
        snapshot.len() as i32
    })
    .unwrap_or(E_FAIL)
}
