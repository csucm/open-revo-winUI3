use super::system_preferences::data_directory;
use openrevo_core::system::app_preferences::{
    AppPreferenceEdit, AppPreferences, SaveError, MAX_FILE_BYTES, SNAPSHOT_CAPACITY,
};
use std::{panic::catch_unwind, ptr, slice};

const E_INVALIDARG: i32 = 0x80070057_u32 as i32;
const E_FAIL: i32 = 0x80004005_u32 as i32;
const E_INSUFFICIENT_BUFFER: i32 = 0x8007007A_u32 as i32;

fn failure(error: SaveError) -> i32 {
    match error {
        SaveError::Invalid => E_INVALIDARG,
        SaveError::Io(_) => E_FAIL,
    }
}

#[no_mangle]
pub unsafe extern "C" fn or_native_app_preferences_load(
    directory: *const u16,
    length: usize,
    strict: u32,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    if output.is_null() || strict > 1 {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        let snapshot = match AppPreferences::load_snapshot(&directory, strict == 1) {
            Ok(snapshot) => snapshot,
            Err(error) => return failure(error),
        };
        if snapshot.len() > capacity {
            return E_INSUFFICIENT_BUFFER;
        }
        ptr::copy_nonoverlapping(snapshot.as_ptr(), output, snapshot.len());
        snapshot.len() as i32
    })
    .unwrap_or(E_FAIL)
}

#[no_mangle]
pub unsafe extern "C" fn or_native_app_preferences_save(
    directory: *const u16,
    length: usize,
    input: *const u8,
    bytes: usize,
) -> i32 {
    if input.is_null() || !(1..=MAX_FILE_BYTES).contains(&bytes) {
        return E_INVALIDARG;
    }
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        AppPreferences::save_snapshot(&directory, slice::from_raw_parts(input, bytes))
            .map_or_else(failure, |()| 0)
    })
    .unwrap_or(E_FAIL)
}

#[no_mangle]
pub unsafe extern "C" fn or_native_app_preferences_edit(
    directory: *const u16,
    length: usize,
    input: *const u8,
    bytes: usize,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    if input.is_null() || !(1..=MAX_FILE_BYTES).contains(&bytes) {
        return E_INVALIDARG;
    }
    if output.is_null() || capacity < SNAPSHOT_CAPACITY {
        return E_INSUFFICIENT_BUFFER;
    }
    catch_unwind(|| {
        let Some(directory) = data_directory(directory, length) else {
            return E_INVALIDARG;
        };
        let Ok(edit) =
            serde_json::from_slice::<AppPreferenceEdit>(slice::from_raw_parts(input, bytes))
        else {
            return E_INVALIDARG;
        };
        let snapshot = match AppPreferences::apply_edit(&directory, edit) {
            Ok(snapshot) => snapshot,
            Err(error) => return failure(error),
        };
        ptr::copy_nonoverlapping(snapshot.as_ptr(), output, snapshot.len());
        snapshot.len() as i32
    })
    .unwrap_or(E_FAIL)
}
