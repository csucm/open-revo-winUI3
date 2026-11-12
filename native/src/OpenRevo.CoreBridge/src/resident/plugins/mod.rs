//! Existing plugin workflows, owned by the native actor. No third-party CLR runs here.
mod capabilities;
mod models;
mod package;
mod sandbox;
mod session;
mod store;
mod theme;
use crate::resident::{
    hardware::{Outcome, Receipt},
    platform::validate_directory,
    runtime::Client,
};
pub use models::Action;
use models::*;
use package::Package;
use serde_json::{json, Value};
use session::Session;
pub use session::{Lease, Shared};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::PathBuf,
    sync::{atomic::Ordering, Arc},
};
pub struct Manager {
    base: PathBuf,
    root: PathBuf,
    store: Option<store::Store>,
    packages: BTreeMap<String, Package>,
    sessions: BTreeMap<String, Session>,
    themes: BTreeMap<String, Package>,
    errors: Vec<String>,
    faults: BTreeMap<String, String>,
    disabled: BTreeSet<String>,
    discovered: bool,
    theme_revision: u64,
    palette: Value,
    shared: Arc<Shared>,
    client: Client,
}
impl Manager {
    pub fn new(directory: PathBuf, client: Client, shared: Arc<Shared>) -> Self {
        let base = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_owned()))
            .unwrap_or_default();
        Self {
            base,
            root: directory.join("native-plugins"),
            store: None,
            packages: BTreeMap::new(),
            sessions: BTreeMap::new(),
            themes: BTreeMap::new(),
            errors: Vec::new(),
            faults: BTreeMap::new(),
            disabled: BTreeSet::new(),
            discovered: false,
            theme_revision: 0,
            palette: json!([]),
            shared,
            client,
        }
    }
    fn store(&mut self) -> Result<&mut store::Store, String> {
        if self.store.is_none() {
            self.store = Some(store::Store::open(self.root.clone())?)
        }
        self.store.as_mut().ok_or("插件状态不可用".into())
    }
    fn refresh(&mut self) -> Result<(), String> {
        let root = self.base.join("plugins");
        validate_directory(&root)?;
        let mut packages = BTreeMap::<String, Package>::new();
        let mut duplicates = BTreeSet::new();
        let mut errors = Vec::new();
        if root.try_exists().map_err(|e| e.to_string())? {
            let entries = std::fs::read_dir(&root)
                .map_err(|e| e.to_string())?
                .collect::<Result<Vec<_>, _>>()
                .map_err(|e| e.to_string())?;
            if entries.len() > 128 {
                return Err("插件目录超过128项".into());
            }
            for entry in entries {
                let path = entry.path();
                let kind = entry.file_type().map_err(|e| e.to_string())?;
                if !kind.is_dir() || kind.is_symlink() {
                    errors.push(format!(
                        "{} · 插件目录必须是普通目录",
                        entry.file_name().to_string_lossy()
                    ));
                    continue;
                }
                match Package::read(&path) {
                    Ok(p) => {
                        let id = p.manifest.id.clone();
                        if packages.contains_key(&id) || duplicates.contains(&id) {
                            duplicates.insert(id.clone());
                            packages.remove(&id);
                            errors.push(format!("{id} · 插件标识重复"));
                        } else {
                            packages.insert(id, p);
                        }
                    }
                    Err(e) => errors.push(format!(
                        "{} · {}",
                        entry.file_name().to_string_lossy(),
                        e.chars().take(400).collect::<String>()
                    )),
                }
            }
        }
        let stop = self
            .sessions
            .iter()
            .filter(|(id, s)| packages.get(*id).is_none_or(|p| p.digest != s.lease.digest))
            .map(|(id, _)| id.clone())
            .collect::<Vec<_>>();
        for id in stop {
            self.stop_one(&id)?;
        }
        self.themes
            .retain(|id, p| packages.get(id).is_some_and(|v| v.digest == p.digest));
        self.packages = packages;
        self.errors = errors;
        self.discovered = true;
        self.restore_themes();
        self.publish_theme()?;
        self.shared.signal();
        Ok(())
    }
    fn approved(&mut self, p: &Package) -> Result<Option<Approval>, String> {
        if self.disabled.contains(&p.manifest.id) {
            return Ok(None);
        }
        let store = self.store()?;
        if !store
            .record(&p.manifest.id)
            .is_some_and(|r| r.enabled && r.fault.is_none() && r.digest == p.digest)
        {
            return Ok(None);
        }
        store.approval(p)
    }
    fn restore_themes(&mut self) {
        let candidates = self
            .packages
            .values()
            .filter(|p| p.manifest.platform.is_some())
            .cloned()
            .collect::<Vec<_>>();
        let mut next = BTreeMap::new();
        for p in candidates {
            let id = p.manifest.id.clone();
            match self.approved(&p) {
                Ok(Some(a)) if a.permissions.iter().any(|v| v == THEME) => {
                    let mut values = next.values().cloned().collect::<Vec<_>>();
                    values.push(p.clone());
                    match theme::compose(values.iter()) {
                        Ok(_) => {
                            next.insert(id.clone(), p);
                            self.faults.remove(&id);
                        }
                        Err(e) => {
                            self.faults.insert(id, e);
                        }
                    }
                }
                Ok(_) => {}
                Err(e) => {
                    self.faults.insert(id, e);
                }
            }
        }
        self.themes = next;
    }
    fn publish_theme(&mut self) -> Result<(), String> {
        let colors = serde_json::to_value(theme::compose(self.themes.values())?)
            .map_err(|e| e.to_string())?;
        if colors != self.palette {
            self.palette = colors;
            self.theme_revision = self.theme_revision.wrapping_add(1);
            self.shared.signal();
        }
        Ok(())
    }
    pub fn theme(&self) -> Value {
        let colors=self.palette.as_array().map(|v|v.iter().map(|c|json!({"Surface":c["surface"],"Mode":c["mode"],"Token":c["token"],"Value":c["value"]})).collect::<Vec<_>>()).unwrap_or_default();
        json!({"Revision":self.theme_revision,"Colors":colors})
    }
    fn start(&mut self, p: &Package, a: Approval) -> Result<(), String> {
        if a.permissions
            .iter()
            .any(|g| !p.manifest.permissions.contains(g))
        {
            return Err("授权超出插件请求".into());
        }
        if p.manifest.background && !a.permissions.iter().any(|g| g == BACKGROUND) {
            return Err("后台运行未授权".into());
        }
        if self.sessions.len() >= 4 {
            return Err("最多同时运行4个插件".into());
        }
        if self.sessions.contains_key(&p.manifest.id) {
            return Err("旧插件进程尚未结束".into());
        }
        let worker = self.base.join("PluginWorker");
        let session = Session::start(
            p,
            a.permissions,
            &self.root,
            &worker,
            self.client.clone(),
            self.shared.clone(),
        )?;
        self.shared
            .leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(p.manifest.id.clone(), session.lease.clone());
        self.sessions.insert(p.manifest.id.clone(), session);
        self.faults.remove(&p.manifest.id);
        self.shared.signal();
        Ok(())
    }
    fn stop_one(&mut self, id: &str) -> Result<(), String> {
        if let Some(s) = self.sessions.get_mut(id) {
            s.lease.revoke(None);
            let result = s.stop();
            if !s.exited() {
                return result.and(Err("插件进程退出未确认".into()));
            }
            if let Err(e) = result {
                self.faults.insert(id.into(), e);
            }
            self.sessions.remove(id);
            self.shared
                .leases
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .remove(id);
        }
        self.themes.remove(id);
        self.publish_theme()?;
        self.shared.signal();
        Ok(())
    }
    pub fn has_sessions(&self) -> bool {
        !self.sessions.is_empty()
    }
    pub fn reap(&mut self) {
        let ids = self
            .sessions
            .iter()
            .filter(|(_, s)| s.stopped())
            .map(|(id, _)| id.clone())
            .collect::<Vec<_>>();
        for id in ids {
            if let Some(s) = self.sessions.get(&id) {
                if let Some(e) = s
                    .lease
                    .fault
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .clone()
                {
                    self.faults.insert(id.clone(), e);
                }
            }
            if let Err(e) = self.stop_one(&id) {
                self.faults.insert(id, e);
            }
        }
    }
    fn restore(&mut self, background: bool) -> Result<(), String> {
        if !self.discovered {
            self.refresh()?;
        }
        let packages = self
            .packages
            .values()
            .filter(|p| p.manifest.platform.is_none() && p.manifest.background == background)
            .cloned()
            .collect::<Vec<_>>();
        for p in packages {
            if self.sessions.contains_key(&p.manifest.id) {
                continue;
            }
            match self.approved(&p) {
                Ok(Some(a)) => {
                    if let Err(e) = self.start(&p, a) {
                        self.faults.insert(p.manifest.id, e);
                    }
                }
                Ok(None) => {}
                Err(e) => {
                    self.faults.insert(p.manifest.id, e);
                }
            }
        }
        self.shared.signal();
        Ok(())
    }
    pub fn load_background(&mut self) -> Receipt {
        self.reap();
        match self.refresh().and_then(|_| self.restore(true)) {
            Ok(()) => self.reply(Outcome::Applied, None, None),
            Err(e) => self.reply(Outcome::Unknown, None, Some(e)),
        }
    }
    pub fn stop_all(&mut self) -> Result<(), String> {
        self.shared.stop();
        let ids = self.sessions.keys().cloned().collect::<Vec<_>>();
        let mut failure = None;
        for id in ids {
            if let Err(e) = self.stop_one(&id) {
                failure = Some(e);
            }
        }
        self.themes.clear();
        self.publish_theme()?;
        failure.map_or(Ok(()), Err)
    }
    pub fn close_views(&mut self) -> Result<(), String> {
        self.shared.close_views();
        let ids = self
            .sessions
            .iter()
            .filter(|(_, s)| !s.lease.background)
            .map(|(id, _)| id.clone())
            .collect::<Vec<_>>();
        for id in ids {
            self.stop_one(&id)?;
        }
        Ok(())
    }
    fn catalog(&mut self) -> Value {
        let packages = self.packages.values().cloned().collect::<Vec<_>>();
        let entries=packages.iter().map(|p|{let id=&p.manifest.id;let record=self.store().ok().and_then(|s|s.record(id).cloned());let mut fault=self.faults.get(id).cloned().or_else(||record.as_ref().filter(|r|r.digest==p.digest).and_then(|r|r.fault.clone()));let granted=match self.store().and_then(|s|s.approval(p)){Ok(a)=>a.map(|a|a.permissions),Err(e)=>{fault.get_or_insert(e);None}};let supported=if p.manifest.platform.is_some(){match theme::validate(p){Ok(())=>true,Err(e)=>{fault.get_or_insert(e);false}}}else{true};let lease=self.shared.matching(id,&p.digest);let running=lease.as_ref().is_some_and(|l|l.active.load(Ordering::Acquire))||self.themes.get(id).is_some_and(|v|v.digest==p.digest);if let Some(l)=&lease{if let Some(e)=l.fault.lock().unwrap_or_else(|e|e.into_inner()).clone(){fault.get_or_insert(e);}}json!({"Key":{"Id":id,"Digest":p.digest},"Name":p.manifest.name,"Version":p.manifest.version,"Description":p.manifest.description,"Author":p.manifest.author,"Requested":p.manifest.permissions,"Granted":granted,"Background":p.manifest.background,"Supported":supported,"Enabled":record.is_some_and(|r|r.enabled&&r.digest==p.digest)&&!self.disabled.contains(id),"Running":running,"Fault":fault.map(|e|e.chars().take(512).collect::<String>()),"Folder":p.directory.file_name().map(|s|s.to_string_lossy().into_owned()).unwrap_or_default(),"Theme":p.manifest.platform.as_ref().is_some_and(|v|v.runtime=="static"),"HasView":lease.as_ref().is_some_and(|l|l.view.lock().unwrap_or_else(|e|e.into_inner()).is_some())})}).collect::<Vec<_>>();
        json!({"Entries":entries,"Errors":self.errors,"Page":null,"Directory":self.base.join("plugins").to_string_lossy()})
    }
    fn reply(&mut self, outcome: Outcome, page: Option<Value>, message: Option<String>) -> Receipt {
        let mut r = Receipt::new(outcome);
        r.message = message;
        r.payload = Some(json!({"Plugins":page.unwrap_or_else(||self.catalog())}));
        r
    }
    pub fn execute(&mut self, r: Value) -> Receipt {
        self.reap();
        match self.inner(&r) {
            Ok((outcome, page, message)) => self.reply(outcome, page, message),
            Err(e) => self.reply(Outcome::Rejected, None, Some(e)),
        }
    }
    fn inner(&mut self, r: &Value) -> Result<(Outcome, Option<Value>, Option<String>), String> {
        let operation = r["Operation"].as_u64().ok_or("缺少插件操作")?;
        match operation {
            1 => {
                self.refresh()?;
                if self.shared.foreground.load(Ordering::Acquire) {
                    self.restore(false)?;
                }
                return Ok((Outcome::Applied, None, None));
            }
            7 => {
                self.close_views()?;
                return Ok((Outcome::Applied, None, None));
            }
            8 => {
                self.shared.foreground.store(true, Ordering::Release);
                self.restore(false)?;
                return Ok((Outcome::Applied, None, None));
            }
            9 => return Ok((Outcome::Applied, None, None)),
            10 => {
                let root = self.base.join("plugins");
                validate_directory(&root)?;
                std::fs::create_dir_all(&root).map_err(|e| e.to_string())?;
                return Ok((
                    Outcome::Applied,
                    Some(json!({"Directory":root.to_string_lossy()})),
                    None,
                ));
            }
            _ => {}
        }
        let id = r["Target"]["Id"].as_str().ok_or("缺少插件标识")?;
        let digest = r["Target"]["Digest"].as_str().ok_or("缺少插件摘要")?;
        if operation == 5 {
            let lease = self.shared.matching(id, digest);
            let view = lease
                .as_ref()
                .and_then(|l| l.view.lock().unwrap_or_else(|e| e.into_inner()).clone());
            let controls=view.as_ref().map(|v|v.controls.iter().map(|c|json!({"Id":c.id,"Kind":c.kind,"Label":c.label,"Value":c.value,"Enabled":c.enabled,"Choices":c.choices.as_ref().map(|v|v.iter().map(|c|json!({"Value":c.value,"Label":c.label})).collect::<Vec<_>>())})).collect::<Vec<_>>()).unwrap_or_default();
            let fault = lease
                .as_ref()
                .and_then(|l| l.fault.lock().unwrap_or_else(|e| e.into_inner()).clone())
                .or_else(|| self.faults.get(id).cloned());
            return Ok((
                Outcome::Applied,
                Some(
                    json!({"Page":{"Key":r["Target"],"Running":lease.as_ref().is_some_and(|l|l.active.load(Ordering::Acquire)),"Fault":fault,"Title":view.map(|v|v.title),"Controls":controls}}),
                ),
                None,
            ));
        }
        if operation == 6 {
            if !self.shared.foreground.load(Ordering::Acquire) {
                return Err("插件页已关闭".into());
            }
            let sent = self.shared.matching(id, digest).is_some_and(|l| {
                l.input(r["ControlId"].as_str().unwrap_or(""), r["Value"].as_str())
            });
            return if sent {
                Ok((Outcome::CommandSent, None, None))
            } else {
                Err("插件控件已失效".into())
            };
        }
        let p = self
            .packages
            .get(id)
            .filter(|p| p.digest == digest)
            .cloned()
            .ok_or("插件包已变化，请刷新列表")?;
        if operation == 3 || operation == 4 {
            self.disabled.insert(id.into());
            self.stop_one(id)?;
            if operation == 4 {
                self.store()?.revoke(id)?;
            }
            let choice = self.store()?.choice(&p, false, None);
            return Ok((
                if choice.is_ok() {
                    Outcome::ConfigurationSaved
                } else {
                    Outcome::ConfigurationSaveFailed
                },
                None,
                choice.err(),
            ));
        }
        if operation != 2 {
            return Err("无效插件操作".into());
        }
        if !self.shared.foreground.load(Ordering::Acquire) {
            return Err("请先打开插件页".into());
        }
        let current = Package::read(&p.directory)?;
        if current.digest != p.digest || current.manifest.id != id {
            return Err("插件文件已变化，请重新审核".into());
        }
        if self
            .shared
            .matching(id, digest)
            .is_some_and(|l| l.active.load(Ordering::Acquire))
        {
            return Ok((Outcome::Applied, None, None));
        }
        let selected = r["Grants"]
            .as_array()
            .map(|v| {
                v.iter()
                    .map(|v| v.as_str().map(str::to_owned).ok_or("无效授权".to_string()))
                    .collect::<Result<Vec<_>, _>>()
            })
            .transpose()?;
        let a = match selected.as_ref() {
            Some(g) => p.approve(g)?,
            None => self
                .store()?
                .approval(&p)?
                .ok_or("此版本需要重新审核权限")?,
        };
        if p.manifest.platform.is_some() {
            theme::validate(&p)?;
            if !a.permissions.iter().any(|v| v == THEME) {
                return Err("需要静态主题授权".into());
            }
            let mut next = self.themes.clone();
            next.insert(id.into(), p.clone());
            theme::compose(next.values())?;
            if selected.is_some() {
                self.store()?.save_approval(&a)?;
            }
            if let Err(e) = self.store()?.choice(&p, true, None) {
                return Ok((Outcome::ConfigurationSaveFailed, None, Some(e)));
            }
            self.themes = next;
            self.disabled.remove(id);
            self.faults.remove(id);
            self.publish_theme()?;
            return Ok((Outcome::ConfigurationSaved, None, None));
        }
        if selected.is_some() {
            self.store()?.save_approval(&a)?;
        }
        self.stop_one(id)?;
        self.start(&p, a)?;
        if let Err(e) = self.store()?.choice(&p, true, None) {
            self.disabled.insert(id.into());
            self.stop_one(id)?;
            return Ok((Outcome::ConfigurationSaveFailed, None, Some(e)));
        }
        self.disabled.remove(id);
        self.shared.signal();
        Ok((Outcome::ConfigurationSaved, None, None))
    }
}
impl Drop for Manager {
    fn drop(&mut self) {
        let _ = self.stop_all();
    }
}
