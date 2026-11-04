use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};

pub static DATA_IO: parking_lot::ReentrantMutex<()> = parking_lot::ReentrantMutex::new(());

pub fn atomic_write(path: &Path, content: &[u8]) -> Result<(), String> {
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
    result
        .and_then(|_| fs::rename(&temporary, path))
        .map_err(|e| {
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
