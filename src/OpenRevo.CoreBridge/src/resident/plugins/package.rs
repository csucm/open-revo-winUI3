use super::models::*;
use crate::resident::{crypto, platform::validate_directory};
use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::Read,
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
};
pub fn open_regular(path: &Path) -> Result<File, String> {
    validate_directory(path.parent().ok_or("无效插件路径")?)?;
    let file = OpenOptions::new()
        .read(true)
        .share_mode(1)
        .custom_flags(0x00200000)
        .open(path)
        .map_err(|e| e.to_string())?;
    let metadata = file.metadata().map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
        return Err("插件文件必须为普通文件".into());
    }
    Ok(file)
}
pub fn bounded(path: &Path, max: u64) -> Result<Option<Vec<u8>>, String> {
    let mut file = match open_regular(path) {
        Ok(f) => f,
        Err(_) if !path.try_exists().map_err(|e| e.to_string())? => return Ok(None),
        Err(e) => return Err(e),
    };
    if file.metadata().map_err(|e| e.to_string())?.len() > max {
        return Err("插件文件超过边界".into());
    }
    let mut bytes = Vec::new();
    file.by_ref()
        .take(max + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > max {
        return Err("插件文件超过边界".into());
    }
    Ok(Some(bytes))
}
fn filename(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 120
        && !name.starts_with('.')
        && name
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_' | b'-'))
}
fn version(s: &str) -> bool {
    s.len() <= 32
        && (2..=4).contains(&s.split('.').count())
        && s.split('.').all(|n| {
            !n.is_empty() && n.bytes().all(|c| c.is_ascii_digit()) && n.parse::<i32>().is_ok()
        })
}
#[derive(Clone)]
pub struct Package {
    pub directory: PathBuf,
    pub manifest: Manifest,
    pub digest: String,
    pub files: BTreeMap<String, String>,
}
impl Package {
    pub fn read(root: &Path) -> Result<Self, String> {
        validate_directory(root)?;
        let mut files = BTreeMap::new();
        let mut seen = std::collections::HashSet::new();
        let mut total = 0;
        for entry in std::fs::read_dir(root).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            let name = entry
                .file_name()
                .into_string()
                .map_err(|_| "插件文件名无效")?;
            if !filename(&name)
                || !(name.to_ascii_lowercase().ends_with(".dll") || name == "plugin.json")
                || !seen.insert(name.to_ascii_lowercase())
                || files.len() >= 64
            {
                return Err("插件包只能包含64个以内顶层DLL与plugin.json".into());
            }
            let mut file = open_regular(&entry.path())?;
            let (hash, size) = crypto::reader(&mut file, 16 * 1024 * 1024 - total)?;
            total += size;
            files.insert(name, hash);
        }
        let path = root.join("plugin.json");
        let bytes = bounded(&path, 16384)?.ok_or("插件清单缺失")?;
        let manifest: Manifest = parse(&bytes, 16384)?;
        if manifest.api_major != 1
            || !identifier(&manifest.id)
            || !text(&manifest.name, 1, 80)
            || manifest
                .description
                .as_ref()
                .is_some_and(|v| !text(v, 0, 512))
            || manifest.author.as_ref().is_some_and(|v| !text(v, 0, 80))
            || !version(&manifest.version)
            || !(0..=2).contains(&manifest.minimum_api_minor)
            || manifest.permissions.len() > 16
            || manifest
                .permissions
                .iter()
                .any(|p| !KNOWN.contains(&p.as_str()))
            || manifest
                .permissions
                .iter()
                .collect::<std::collections::HashSet<_>>()
                .len()
                != manifest.permissions.len()
            || manifest.background && !manifest.permissions.iter().any(|p| p == BACKGROUND)
        {
            return Err("无效或不受支持的插件清单".into());
        }
        if manifest
            .platform
            .as_ref()
            .is_some_and(|p| p.runtime == "static")
        {
            if !manifest.entry_assembly.is_empty()
                || !manifest.entry_type.is_empty()
                || manifest.background
            {
                return Err("静态插件不能声明执行入口或后台".into());
            }
        } else if !filename(&manifest.entry_assembly)
            || !manifest
                .entry_assembly
                .to_ascii_lowercase()
                .ends_with(".dll")
            || !files
                .keys()
                .any(|k| k.eq_ignore_ascii_case(&manifest.entry_assembly))
            || !text(&manifest.entry_type, 1, 160)
            || !manifest
                .entry_type
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_'))
        {
            return Err("插件入口无效".into());
        }
        if let Some(platform) = &manifest.platform {
            platform.validate(&manifest.permissions)?;
        }
        if files.get("plugin.json") != Some(&crypto::bytes(&bytes)?) {
            return Err("插件清单在检查期间变化".into());
        }
        let digest_text = files
            .iter()
            .map(|(name, hash)| format!("{name}:{hash}"))
            .collect::<Vec<_>>()
            .join("\n");
        let digest = crypto::bytes(digest_text.as_bytes())?;
        Ok(Self {
            directory: root.to_path_buf(),
            manifest,
            digest,
            files,
        })
    }
    pub fn approve(&self, grants: &[String]) -> Result<Approval, String> {
        let mut permissions = grants.to_vec();
        permissions.sort();
        permissions.dedup();
        if permissions
            .iter()
            .any(|p| !KNOWN.contains(&p.as_str()) || !self.manifest.permissions.contains(p))
            || self.manifest.background && !permissions.iter().any(|p| p == BACKGROUND)
        {
            return Err("只可授予已请求的已知权限；后台执行必须显式授权".into());
        }
        Ok(Approval {
            version: 1,
            plugin_id: self.manifest.id.clone(),
            package_digest: self.digest.clone(),
            permissions,
        })
    }
}
