//! Host-owned records preserve the existing camelCase schema and refuse unknown versions.
use super::{
    models::*,
    package::{bounded, Package},
    sandbox::protect_directory,
};
use crate::resident::{crypto, platform::validate_directory};
use openrevo_core::system::config_io;
use std::{
    fs,
    path::{Path, PathBuf},
};
pub struct Store {
    root: PathBuf,
    pub document: Document,
}
impl Store {
    pub fn open(root: PathBuf) -> Result<Self, String> {
        validate_directory(&root)?;
        let document = Self::read_document(&root)?.unwrap_or_default();
        Ok(Self { root, document })
    }
    fn read_document(root: &Path) -> Result<Option<Document>, String> {
        let Some(bytes) = bounded(&root.join("platform-state.json"), 1024 * 1024)? else {
            return Ok(None);
        };
        let document: Document = parse(&bytes, 1024 * 1024)?;
        if !document.validate() {
            return Err("不受支持的插件平台状态，保留原文件".into());
        }
        Ok(Some(document))
    }
    pub fn commit(&mut self, document: Document) -> Result<(), String> {
        if !document.validate() {
            return Err("无效插件平台状态".into());
        }
        let bytes = serde_json::to_vec(&document).map_err(|e| e.to_string())?;
        if bytes.len() > 1024 * 1024 {
            return Err("插件状态超过1MiB".into());
        }
        let _ = Self::read_document(&self.root)?;
        validate_directory(&self.root)?;
        fs::create_dir_all(&self.root).map_err(|e| e.to_string())?;
        protect_directory(&self.root, None)?;
        config_io::atomic_write_with_retry(&self.root.join("platform-state.json"), &bytes)?;
        self.document = document;
        Ok(())
    }
    pub fn record(&self, id: &str) -> Option<&Record> {
        self.document.plugins.iter().find(|p| p.id == id)
    }
    pub fn choice(
        &mut self,
        p: &Package,
        enabled: bool,
        fault: Option<String>,
    ) -> Result<(), String> {
        let mut next = self.document.clone();
        let mut record = next
            .plugins
            .iter()
            .find(|v| v.id == p.manifest.id)
            .cloned()
            .unwrap_or_else(|| Record {
                id: p.manifest.id.clone(),
                digest: p.digest.clone(),
                enabled: false,
                fault: None,
                paused_policies: Vec::new(),
                theme_edits: Vec::new(),
                key_edits: Vec::new(),
                active_run: None,
            });
        record.digest = p.digest.clone();
        record.enabled = enabled;
        record.fault = fault;
        record.active_run = None;
        next.plugins.retain(|v| v.id != record.id);
        next.plugins.push(record);
        self.commit(next)
    }
    fn approval_path(&self, id: &str) -> Result<PathBuf, String> {
        if !identifier(id) {
            return Err("插件标识无效".into());
        }
        Ok(self.root.join("approvals").join(format!("{id}.json")))
    }
    fn read_approval(&self, id: &str) -> Result<Option<Approval>, String> {
        let path = self.approval_path(id)?;
        let Some(bytes) = bounded(&path, 16384)? else {
            return Ok(None);
        };
        let value: Approval = parse(&bytes, 16384)?;
        if !value.validate() {
            return Err("不受支持的插件授权记录，保留原文件".into());
        }
        Ok(Some(value))
    }
    pub fn approval(&self, p: &Package) -> Result<Option<Approval>, String> {
        let Some(value) = self.read_approval(&p.manifest.id)? else {
            return Ok(None);
        };
        if value.plugin_id != p.manifest.id || value.package_digest != p.digest {
            return Ok(None);
        }
        p.approve(&value.permissions).map(Some)
    }
    pub fn save_approval(&self, a: &Approval) -> Result<(), String> {
        if !a.validate() {
            return Err("插件授权记录无效".into());
        }
        let path = self.approval_path(&a.plugin_id)?;
        let _ = self.read_approval(&a.plugin_id)?;
        let directory = path.parent().ok_or("授权目录无效")?;
        validate_directory(directory)?;
        fs::create_dir_all(directory).map_err(|e| e.to_string())?;
        protect_directory(directory, None)?;
        config_io::atomic_write_with_retry(
            &path,
            &serde_json::to_vec(a).map_err(|e| e.to_string())?,
        )
    }
    pub fn revoke(&self, id: &str) -> Result<(), String> {
        let path = self.approval_path(id)?;
        validate_directory(path.parent().ok_or("授权目录无效")?)?;
        if let Some(_) = bounded(&path, 16384)? {
            fs::remove_file(path).map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}
pub fn read_config(path: &Path) -> Result<Option<serde_json::Value>, String> {
    #[derive(serde::Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct Config {
        version: i32,
        data: serde_json::Value,
    }
    let Some(bytes) = bounded(path, 16384)? else {
        return Ok(None);
    };
    let config: Config = parse(&bytes, 16384)?;
    if config.version != 1 || !config.data.is_object() {
        return Err("插件配置版本或结构无法识别，保留原文件".into());
    }
    Ok(Some(config.data))
}
pub fn save_config(path: &Path, data: serde_json::Value) -> Result<(), String> {
    if !data.is_object() {
        return Err("插件配置必须为对象".into());
    }
    let _ = read_config(path)?;
    let bytes = serde_json::to_vec(&serde_json::json!({"version":1,"data":data}))
        .map_err(|e| e.to_string())?;
    if bytes.len() > 16384 {
        return Err("插件配置超过16KiB".into());
    }
    config_io::atomic_write_with_retry(path, &bytes)
}
/// Immutable trusted worker files and inspected package files are copied under a fresh session directory.
pub fn stage(p: &Package, root: &Path, worker: &Path, sid: &str) -> Result<PathBuf, String> {
    use std::io::{Read, Write};
    use std::os::windows::fs::OpenOptionsExt;
    let sessions = root.join("sessions");
    validate_directory(&sessions)?;
    fs::create_dir_all(&sessions).map_err(|e| e.to_string())?;
    protect_directory(&sessions, None)?;
    let id = unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?;
    let directory = sessions.join(format!("{id:?}").replace('-', ""));
    fs::create_dir(&directory).map_err(|e| e.to_string())?;
    protect_directory(&directory, None)?;
    let result = (|| -> Result<(), String> {
        validate_directory(worker)?;
        for name in [
            "OpenRevo.PluginWorker.exe",
            "OpenRevo.PluginWorker.dll",
            "OpenRevo.PluginWorker.deps.json",
            "OpenRevo.PluginWorker.runtimeconfig.json",
            "OpenRevo.PluginSdk.dll",
        ] {
            let mut input = super::package::open_regular(&worker.join(name))?;
            if input.metadata().map_err(|e| e.to_string())?.len() > 16 * 1024 * 1024 {
                return Err("可信Worker文件超过边界".into());
            }
            let mut output = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .share_mode(0)
                .open(directory.join(name))
                .map_err(|e| e.to_string())?;
            std::io::copy(&mut input, &mut output).map_err(|e| e.to_string())?;
            output.sync_all().map_err(|e| e.to_string())?;
        }
        let code = directory.join("plugin");
        fs::create_dir(&code).map_err(|e| e.to_string())?;
        validate_directory(&p.directory)?;
        for (name, expected) in &p.files {
            let mut input = super::package::open_regular(&p.directory.join(name))?;
            if input.metadata().map_err(|e| e.to_string())?.len() > 16 * 1024 * 1024 {
                return Err("插件文件在检查后增大".into());
            }
            let mut output = fs::OpenOptions::new()
                .read(true)
                .write(true)
                .create_new(true)
                .share_mode(0)
                .open(code.join(name))
                .map_err(|e| e.to_string())?;
            let mut total = 0;
            let mut buffer = [0u8; 65536];
            loop {
                let n = input.read(&mut buffer).map_err(|e| e.to_string())?;
                if n == 0 {
                    break;
                }
                total += n;
                if total > 16 * 1024 * 1024 {
                    return Err("插件文件在复制期间增大".into());
                }
                output.write_all(&buffer[..n]).map_err(|e| e.to_string())?;
            }
            output.sync_all().map_err(|e| e.to_string())?;
            use std::io::Seek;
            output.rewind().map_err(|e| e.to_string())?;
            if crypto::reader(&mut output, 16 * 1024 * 1024)?.0 != *expected {
                return Err("插件文件已变化，需要重新审核权限".into());
            }
        }
        protect_directory(&directory, Some(sid))?;
        Ok(())
    })();
    if let Err(error) = result {
        let _ = cleanup_stage(root, &directory);
        return Err(error);
    }
    Ok(directory)
}
pub fn cleanup_stage(root: &Path, stage: &Path) -> Result<(), String> {
    let sessions = root.join("sessions");
    let parent = stage.parent().ok_or("插件会话路径无效")?;
    if parent != sessions
        || stage == sessions
        || stage
            .file_name()
            .and_then(|s| s.to_str())
            .is_none_or(|s| s.len() != 32 || !s.bytes().all(|c| c.is_ascii_hexdigit()))
    {
        return Err("拒绝清理非会话目录".into());
    }
    validate_directory(stage)?;
    if !stage.exists() {
        return Ok(());
    }
    for entry in fs::read_dir(stage).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let meta = fs::symlink_metadata(entry.path()).map_err(|e| e.to_string())?;
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return Err("会话清理遇到重解析点".into());
        }
        if meta.is_dir() {
            if entry.file_name() != "plugin" {
                return Err("会话含意外子目录".into());
            }
            validate_directory(&entry.path())?;
            for child in fs::read_dir(entry.path()).map_err(|e| e.to_string())? {
                let child = child.map_err(|e| e.to_string())?;
                let meta = fs::symlink_metadata(child.path()).map_err(|e| e.to_string())?;
                if !meta.is_file() || meta.file_attributes() & 0x400 != 0 {
                    return Err("会话清理遇到非普通文件".into());
                }
                fs::remove_file(child.path()).map_err(|e| e.to_string())?;
            }
            fs::remove_dir(entry.path()).map_err(|e| e.to_string())?;
        } else if meta.is_file() {
            fs::remove_file(entry.path()).map_err(|e| e.to_string())?;
        } else {
            return Err("会话清理遇到未知对象".into());
        }
    }
    fs::remove_dir(stage).map_err(|e| e.to_string())
}
