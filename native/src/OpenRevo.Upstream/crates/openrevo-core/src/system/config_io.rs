use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::ffi::OsStrExt;
use std::path::{Component, Path, Prefix};
use std::sync::atomic::{AtomicU64, Ordering};

pub static DATA_IO: parking_lot::ReentrantMutex<()> = parking_lot::ReentrantMutex::new(());

pub fn atomic_write(path: &Path, content: &[u8]) -> Result<(), String> {
    atomic_write_with(path, content, |source, destination| {
        fs::rename(source, destination)
    })
}

pub fn atomic_write_with_retry(path: &Path, content: &[u8]) -> Result<(), String> {
    atomic_write_with(path, content, |source, destination| {
        let source = wide_path(source);
        let destination = wide_path(destination);
        // Match the managed store's Windows replacement and sharing semantics.
        let replace = || {
            const MOVEFILE_REPLACE_EXISTING: u32 = 1;
            if unsafe {
                MoveFileExW(
                    source.as_ptr(),
                    destination.as_ptr(),
                    MOVEFILE_REPLACE_EXISTING,
                )
            } == 0
            {
                Err(io::Error::last_os_error())
            } else {
                Ok(())
            }
        };
        for attempt in 0..5 {
            match replace() {
                Err(error) if matches!(error.raw_os_error(), Some(5 | 32 | 33)) => {
                    std::thread::sleep(std::time::Duration::from_millis(20 << attempt));
                }
                result => return result,
            }
        }
        replace()
    })
}

fn wide_path(path: &Path) -> Vec<u16> {
    let (prefix, skip) = match path.components().next() {
        Some(Component::Prefix(prefix)) => match prefix.kind() {
            Prefix::Disk(_) => (r"\\?\", 0),
            Prefix::UNC(_, _) => (r"\\?\UNC\", 2),
            _ => ("", 0),
        },
        _ => ("", 0),
    };
    prefix
        .encode_utf16()
        .chain(path.as_os_str().encode_wide().skip(skip))
        .chain(std::iter::once(0))
        .collect()
}

#[link(name = "kernel32")]
extern "system" {
    fn MoveFileExW(source: *const u16, destination: *const u16, flags: u32) -> i32;
}

fn atomic_write_with(
    path: &Path,
    content: &[u8],
    replace: impl FnOnce(&Path, &Path) -> io::Result<()>,
) -> Result<(), String> {
    static NEXT_WRITE: AtomicU64 = AtomicU64::new(0);
    let mut name = path
        .file_name()
        .ok_or("Invalid storage file name")?
        .to_os_string();
    name.push(format!(
        ".tmp.{}.{}",
        std::process::id(),
        NEXT_WRITE.fetch_add(1, Ordering::Relaxed)
    ));
    let temporary = path.with_file_name(name);
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|e| format!("Cannot create temporary file: {e}"))?;
    let result = file.write_all(content).and_then(|_| file.sync_all());
    drop(file);
    result.and_then(|_| replace(&temporary, path)).map_err(|e| {
        let _ = fs::remove_file(&temporary);
        format!("Cannot save {}: {e}", path.display())
    })
}

pub fn merge_changes(current: &mut Value, baseline: &Value, updated: &Value) {
    if baseline == updated {
        return;
    }
    if let (Some(current), Some(baseline), Some(updated)) = (
        current.as_object_mut(),
        baseline.as_object(),
        updated.as_object(),
    ) {
        for key in baseline.keys().filter(|key| !updated.contains_key(*key)) {
            current.remove(key);
        }
        for (key, value) in updated {
            if let (Some(before), Some(latest)) = (baseline.get(key), current.get_mut(key)) {
                merge_changes(latest, before, value);
            } else if baseline.get(key) != Some(value) {
                current.insert(key.clone(), value.clone());
            }
        }
    } else {
        *current = updated.clone();
    }
}
