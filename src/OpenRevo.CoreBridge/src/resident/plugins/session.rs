//! SDK 1.2 transport. Untrusted code runs only in the verified AppContainer.
use super::{
    models::*,
    package::Package,
    sandbox::{self, Sandbox},
    store,
};
use crate::resident::{
    hardware::Outcome,
    runtime::{Client, Command},
};
use openrevo_core::system::resident_pipe_io::PanelPipeIo;
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    os::windows::io::{AsHandle, AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, SyncSender},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::HANDLE,
        System::{
            Pipes::GetNamedPipeClientProcessId,
            Threading::{
                CreateEventW, CreateWaitableTimerW, GetCurrentProcessId, SetEvent, SetWaitableTimer,
            },
        },
    },
};
fn event() -> Result<OwnedHandle, String> {
    unsafe {
        CreateEventW(None, true, false, PCWSTR::null())
            .map(|h| OwnedHandle::from_raw_handle(h.0))
            .map_err(|e| e.to_string())
    }
}
fn timer(ms: u32) -> Result<OwnedHandle, String> {
    unsafe {
        let h = OwnedHandle::from_raw_handle(
            CreateWaitableTimerW(None, true, PCWSTR::null())
                .map_err(|e| e.to_string())?
                .0,
        );
        SetWaitableTimer(
            HANDLE(h.as_raw_handle()),
            &(-i64::from(ms) * 10000),
            0,
            None,
            None,
            false,
        )
        .map_err(|e| e.to_string())?;
        Ok(h)
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Message {
    api_major: i32,
    kind: String,
    request_id: i64,
    method: String,
    payload: Value,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Topic {
    topic: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Config {
    data: Value,
}
pub fn result(success: bool, code: &str) -> Value {
    json!({"success":success,"code":code,"data":null})
}
pub struct Lease {
    pub id: String,
    pub digest: String,
    pub background: bool,
    pub active: Arc<AtomicBool>,
    pub grants: Vec<String>,
    pub requested: Vec<String>,
    pub summary: AtomicBool,
    pub power: AtomicBool,
    pub view: Mutex<Option<View>>,
    pub fault: Mutex<Option<String>>,
    outgoing: SyncSender<Value>,
    stop: Arc<OwnedHandle>,
}
impl Lease {
    pub fn allows(&self, p: &str) -> bool {
        self.active.load(Ordering::Acquire) && self.grants.iter().any(|g| g == p)
    }
    pub fn revoke(&self, reason: Option<&str>) {
        self.active.store(false, Ordering::Release);
        if let Some(r) = reason {
            *self.fault.lock().unwrap_or_else(|e| e.into_inner()) = Some(r.into());
        }
        let _ = unsafe { SetEvent(HANDLE(self.stop.as_raw_handle())) };
    }
    pub fn queue(&self, method: &str, id: i64, kind: &str, payload: Value) -> bool {
        if !self.active.load(Ordering::Acquire) {
            return false;
        }
        let v = json!({"apiMajor":1,"kind":kind,"requestId":id,"method":method,"payload":payload});
        if self.outgoing.try_send(v).is_err() {
            self.revoke(Some("outbound_queue_full"));
            false
        } else {
            true
        }
    }
    pub fn input(&self, id: &str, value: Option<&str>) -> bool {
        if !self.allows(VIEW) {
            return false;
        }
        let v = self.view.lock().unwrap_or_else(|e| e.into_inner());
        let Some(c) = v
            .as_ref()
            .and_then(|v| v.controls.iter().find(|c| c.id == id && c.enabled))
        else {
            return false;
        };
        let valid = match c.kind.as_str() {
            "button" => value.is_none(),
            "toggle" => matches!(value, Some("true" | "false")),
            "choice" => c
                .choices
                .as_ref()
                .is_some_and(|v| v.iter().any(|c| Some(c.value.as_str()) == value)),
            _ => false,
        };
        valid
            && self.queue(
                "ui.event",
                0,
                "Event",
                json!({"controlId":id,"value":value}),
            )
    }
}
pub struct Shared {
    pub leases: Mutex<BTreeMap<String, Arc<Lease>>>,
    pub revision: AtomicU64,
    pub foreground: AtomicBool,
    pub navigation: Mutex<Option<(String, String, u32)>>,
    latest_summary: Mutex<Value>,
    latest_power: Mutex<Value>,
    changed: Arc<dyn Fn() + Send + Sync>,
}
impl Shared {
    pub fn new(changed: Arc<dyn Fn() + Send + Sync>) -> Self {
        Self {
            leases: Mutex::default(),
            revision: AtomicU64::new(0),
            foreground: AtomicBool::new(false),
            navigation: Mutex::default(),
            latest_summary: Mutex::new(Value::Null),
            latest_power: Mutex::new(Value::Null),
            changed,
        }
    }
    pub fn signal(&self) {
        self.revision.fetch_add(1, Ordering::AcqRel);
        (self.changed)();
    }
    pub fn matching(&self, id: &str, digest: &str) -> Option<Arc<Lease>> {
        self.leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .get(id)
            .filter(|l| l.digest == digest)
            .cloned()
    }
    pub fn revoke(&self, id: &str, digest: &str) {
        if let Some(l) = self.matching(id, digest) {
            l.revoke(None);
            self.signal();
        }
    }
    pub fn close_views(&self) {
        self.foreground.store(false, Ordering::Release);
        self.navigation
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take();
        for l in self
            .leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values()
        {
            if !l.background {
                l.revoke(None);
            }
        }
        self.signal();
    }
    pub fn stop(&self) {
        self.close_views();
        for l in self
            .leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values()
        {
            l.revoke(None);
        }
        self.signal();
    }
    pub fn has_summary(&self) -> bool {
        self.leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values()
            .any(|l| l.allows(SUMMARY) && l.summary.load(Ordering::Acquire))
    }
    pub fn publish_summary(&self, v: &Value) {
        if !v.is_object() {
            return;
        }
        let fields = [
            ("sampledAt", "SampledAt"),
            ("cpuPercent", "CpuPercent"),
            ("usedMemoryGiB", "UsedMemoryGiB"),
            ("totalMemoryGiB", "TotalMemoryGiB"),
            ("batteryPercent", "BatteryPercent"),
            ("onAc", "OnAc"),
            ("charging", "Charging"),
        ];
        let mut v2 = json!({});
        for (a, b) in fields {
            v2[a] = v[b].clone();
        }
        *self
            .latest_summary
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = v2.clone();
        for l in self
            .leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values()
        {
            if l.allows(SUMMARY) && l.summary.load(Ordering::Acquire) {
                l.queue("state.system.summary", 0, "Event", v2.clone());
            }
        }
    }
    pub fn publish_power(&self) {
        let v = power();
        {
            let mut old = self.latest_power.lock().unwrap_or_else(|e| e.into_inner());
            if old["onAc"] == v["onAc"]
                && old["batteryPercent"] == v["batteryPercent"]
                && old.is_object()
            {
                return;
            }
            *old = v.clone();
        }
        for l in self
            .leases
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .values()
        {
            if l.allows(POWER) && l.power.load(Ordering::Acquire) {
                l.queue("state.system.power", 0, "Event", v.clone());
            }
        }
    }
    pub fn project(&self, v: &mut Value) {
        let leases = self.leases.lock().unwrap_or_else(|e| e.into_inner());
        let revision = self.revision.load(Ordering::Acquire);
        v["RunningBackgroundPlugins"] = json!(leases
            .values()
            .filter(|l| l.background && l.active.load(Ordering::Acquire))
            .count());
        v["PluginRevision"] = json!(revision);
        v["PluginCatalogRevision"] = json!(revision);
        v["Revision"] = json!(v["Revision"].as_u64().unwrap_or(0).wrapping_add(revision));
    }
    pub fn navigation(&self) -> Option<u32> {
        let (id, digest, page) = self
            .navigation
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()?;
        self.foreground
            .load(Ordering::Acquire)
            .then(|| self.matching(&id, &digest))
            .flatten()
            .filter(|l| l.allows(NAVIGATE))
            .map(|_| page)
    }
}
pub fn power() -> Value {
    use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
    let mut s = SYSTEM_POWER_STATUS::default();
    let ok = unsafe { GetSystemPowerStatus(&mut s) }.is_ok();
    json!({"observedAt":crate::resident::panel_wire::utc_iso(crate::resident::platform::unix_ms().max(0) as u64),"onAc":(ok&&s.ACLineStatus<=1).then_some(s.ACLineStatus==1),"batteryPercent":(ok&&s.BatteryLifePercent<=100&&s.BatteryFlag&128==0).then_some(s.BatteryLifePercent)})
}
struct Transport {
    pipe: OwnedHandle,
    stop: Arc<OwnedHandle>,
}
impl Transport {
    fn io<'a>(&'a self, cancel: &'a OwnedHandle) -> PanelPipeIo<'a> {
        PanelPipeIo::new(
            self.pipe.as_handle(),
            self.stop.as_handle(),
            cancel.as_handle(),
        )
    }
    fn read(&self, cancel: &OwnedHandle) -> Result<Message, String> {
        let io = self.io(cancel);
        let mut prefix = [0; 4];
        io.read_upload(&mut prefix).map_err(|e| e.to_string())?;
        let size = u32::from_le_bytes(prefix) as usize;
        if !(1..=65536).contains(&size) {
            return Err("插件帧超过边界".into());
        }
        let mut bytes = vec![0; size];
        io.read_upload(&mut bytes).map_err(|e| e.to_string())?;
        let m: Message = parse(&bytes, 65536)?;
        if m.api_major != 1
            || m.method.is_empty()
            || m.method.len() > 96
            || !m
                .method
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_' | b'-'))
            || !m.payload.is_object()
            || if m.kind == "Event" {
                m.request_id != 0
            } else {
                m.request_id <= 0
            }
            || m.request_id < 0
            || !matches!(m.kind.as_str(), "Request" | "Response" | "Event")
        {
            return Err("无效SDK消息".into());
        }
        Ok(m)
    }
    fn send(&self, v: &Value) -> Result<(), String> {
        let bytes = serde_json::to_vec(v).map_err(|e| e.to_string())?;
        if bytes.len() > 65536 {
            return Err("插件响应超过边界".into());
        }
        self.io(&timer(5000)?)
            .write_frame(&bytes)
            .map_err(|e| e.to_string())
    }
}
pub struct Session {
    pub lease: Arc<Lease>,
    sandbox: Sandbox,
    root: PathBuf,
    stage: PathBuf,
    reader: Option<JoinHandle<()>>,
    writer: Option<JoinHandle<()>>,
}
impl Session {
    pub fn start(
        p: &Package,
        grants: Vec<String>,
        root: &Path,
        worker: &Path,
        client: Client,
        shared: Arc<Shared>,
    ) -> Result<Self, String> {
        crate::resident::platform::validate_directory(root)?;
        std::fs::create_dir_all(root).map_err(|e| e.to_string())?;
        sandbox::protect_directory(root, None)?;
        let mut sandbox = Sandbox::new()?;
        let stage = store::stage(p, root, worker, &sandbox.sid_text)?;
        let result = (|| {
            let configs = root.join("config");
            crate::resident::platform::validate_directory(&configs)?;
            std::fs::create_dir_all(&configs).map_err(|e| e.to_string())?;
            sandbox::protect_directory(&configs, None)?;
            let guid = unsafe { windows::Win32::System::Com::CoCreateGuid() }
                .map_err(|e| e.to_string())?;
            let name = format!("OpenRevo.Plugin.{guid:?}").replace('-', "");
            let pipe = sandbox::create_pipe(&name)?;
            let stop = Arc::new(event()?);
            let transport = Arc::new(Transport {
                pipe,
                stop: stop.clone(),
            });
            let endpoint = sandbox::endpoint(&name)?;
            let deadline = timer(10000)?;
            transport
                .io(&deadline)
                .connect()
                .map_err(|e| e.to_string())?;
            let mut peer = 0;
            unsafe {
                GetNamedPipeClientProcessId(HANDLE(transport.pipe.as_raw_handle()), &mut peer)
            }
            .map_err(|e| e.to_string())?;
            if peer != unsafe { GetCurrentProcessId() } {
                return Err("插件端点并非宿主创建".into());
            }
            sandbox.start(
                &stage.join("OpenRevo.PluginWorker.exe"),
                &stage.join("plugin").join(&p.manifest.entry_assembly),
                &p.manifest.entry_type,
                &endpoint,
            )?;
            drop(endpoint);
            let hello = transport.read(&deadline)?;
            if hello.kind != "Event"
                || hello.request_id != 0
                || hello.method != "worker.ready"
                || !hello.payload.is_object()
                || hello.payload.as_object().is_none_or(|v| !v.is_empty())
            {
                return Err("插件Worker握手失败".into());
            }
            let (outgoing, incoming) = mpsc::sync_channel(16);
            let lease = Arc::new(Lease {
                id: p.manifest.id.clone(),
                digest: p.digest.clone(),
                background: p.manifest.background,
                active: Arc::new(AtomicBool::new(true)),
                grants,
                requested: p.manifest.permissions.clone(),
                summary: AtomicBool::new(false),
                power: AtomicBool::new(false),
                view: Mutex::default(),
                fault: Mutex::default(),
                outgoing,
                stop,
            });
            let sending = transport.clone();
            let wl = lease.clone();
            let ws = shared.clone();
            let writer = thread::Builder::new()
                .name("OpenRevo.Plugin.Writer".into())
                .spawn(move || {
                    while wl.active.load(Ordering::Acquire) {
                        match incoming.recv_timeout(Duration::from_millis(100)) {
                            Ok(v) => {
                                if sending.send(&v).is_err() {
                                    wl.revoke(Some("send_failed"));
                                    ws.signal();
                                    break;
                                }
                            }
                            Err(mpsc::RecvTimeoutError::Timeout) => {}
                            Err(_) => break,
                        }
                    }
                })
                .map_err(|e| e.to_string())?;
            let rl = lease.clone();
            let rs = shared.clone();
            let config = configs.join(format!("{}.json", p.manifest.id));
            let reader = match thread::Builder::new()
                .name("OpenRevo.Plugin.Reader".into())
                .spawn(move || {
                    let mut previous = 0;
                    let mut count = 0;
                    let mut window = Instant::now();
                    let (mut action, mut save, mut view) = (None, None, None);
                    let never = event();
                    let run = (|| -> Result<(), String> {
                        let never = never?;
                        while rl.active.load(Ordering::Acquire) {
                            let m = transport.read(&never)?;
                            if m.kind != "Request" || m.request_id <= previous {
                                return Err("无效请求方向或重复序号".into());
                            }
                            previous = m.request_id;
                            if window.elapsed() >= Duration::from_secs(1) {
                                window = Instant::now();
                                count = 0;
                            }
                            count += 1;
                            if count > 30 {
                                return Err("插件请求过快".into());
                            }
                            let response = dispatch(
                                &rl,
                                &client,
                                &rs,
                                &config,
                                &m,
                                &mut action,
                                &mut save,
                                &mut view,
                            )
                            .unwrap_or_else(|_| result(false, "invalid_request"));
                            if !rl.queue(&m.method, m.request_id, "Response", response) {
                                break;
                            }
                        }
                        Ok(())
                    })();
                    if run.is_err() && rl.active.load(Ordering::Acquire) {
                        rl.revoke(Some("session_failed"));
                    } else {
                        rl.revoke(None);
                    }
                    rs.signal();
                }) {
                Ok(t) => t,
                Err(e) => {
                    lease.revoke(Some("thread_failed"));
                    if writer.is_finished() {
                        let _ = writer.join();
                    }
                    return Err(e.to_string());
                }
            };
            Ok((lease, reader, writer))
        })();
        match result {
            Ok((lease, reader, writer)) => Ok(Self {
                lease,
                sandbox,
                root: root.into(),
                stage,
                reader: Some(reader),
                writer: Some(writer),
            }),
            Err(e) => {
                let e = if let Some(code) = sandbox.exit_code() {
                    format!("{e} (Worker退出码0x{code:08X})")
                } else {
                    e
                };
                if sandbox.stop().is_ok() {
                    let _ = store::cleanup_stage(root, &stage);
                }
                Err(e)
            }
        }
    }
    pub fn stopped(&self) -> bool {
        !self.lease.active.load(Ordering::Acquire) || self.sandbox.exited()
    }
    pub fn stop(&mut self) -> Result<(), String> {
        self.lease.revoke(None);
        self.sandbox.stop()?;
        for t in [&mut self.reader, &mut self.writer] {
            if t.as_ref().is_some_and(|t| t.is_finished()) {
                if let Some(t) = t.take() {
                    let _ = t.join();
                }
            }
        }
        store::cleanup_stage(&self.root, &self.stage)
    }
    pub fn exited(&self) -> bool {
        self.sandbox.exited()
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}
fn limited(last: &mut Option<Instant>, ms: u64) -> bool {
    if last.is_some_and(|t| t.elapsed() < Duration::from_millis(ms)) {
        true
    } else {
        *last = Some(Instant::now());
        false
    }
}
fn decode<T: serde::de::DeserializeOwned>(v: &Value) -> Result<T, String> {
    serde_json::from_value(v.clone()).map_err(|e| e.to_string())
}
fn dispatch(
    l: &Arc<Lease>,
    client: &Client,
    shared: &Shared,
    config: &Path,
    m: &Message,
    action: &mut Option<Instant>,
    save: &mut Option<Instant>,
    view: &mut Option<Instant>,
) -> Result<Value, String> {
    if !l.active.load(Ordering::Acquire) {
        return Ok(result(false, "disabled"));
    }
    Ok(match m.method.as_str() {
        "capabilities.query" => {
            json!({"api":{"major":1,"minor":2},"capabilities":l.requested.iter().map(|p|{let available=l.allows(p)&&super::capabilities::available(p,&client.raw_read());json!({"id":p,"available":available,"unavailableReason":if available{Value::Null}else{json!("not_granted_or_unavailable")}})}).collect::<Vec<_>>()})
        }
        "capabilities.catalog" => super::capabilities::catalog(&client.raw_read()),
        "ui.theme.schema" => super::capabilities::theme_schema(client.raw_read()["Accepting"] == true),
        "state.subscribe" | "state.unsubscribe" => {
            let topic: Topic = decode(&m.payload)?;
            let (on, permission, flag, method) = match topic.topic.as_str() {
                "system.summary" => (
                    m.method == "state.subscribe",
                    SUMMARY,
                    &l.summary,
                    "state.system.summary",
                ),
                "system.power" => (
                    m.method == "state.subscribe",
                    POWER,
                    &l.power,
                    "state.system.power",
                ),
                _ => return Ok(result(false, "unknown_topic")),
            };
            if on && !l.allows(permission) {
                return Ok(result(false, "permission_denied"));
            }
            let old = flag.swap(on, Ordering::AcqRel);
            if on && permission == SUMMARY {
                let latest = shared
                    .latest_summary
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .clone();
                if latest.is_object() {
                    l.queue(method, 0, "Event", latest);
                }
            }
            if on && !old && permission == POWER {
                l.queue(method, 0, "Event", power());
            }
            shared.signal();
            result(true, "ok")
        }
        "config.load" => {
            if !l.allows(CONFIG) {
                return Ok(result(false, "permission_denied"));
            }
            match store::read_config(config) {
                Ok(Some(data)) => json!({"success":true,"code":"ok","data":data}),
                Ok(None) => result(true, "not_found"),
                Err(_) => result(false, "storage_failed"),
            }
        }
        "config.save" => {
            if !l.allows(CONFIG) {
                return Ok(result(false, "permission_denied"));
            }
            if limited(save, 500) {
                return Ok(result(false, "rate_limited"));
            }
            let data: Config = decode(&m.payload)?;
            if !l.allows(CONFIG) {
                return Ok(result(false, "disabled"));
            }
            match store::save_config(config, data.data) {
                Ok(()) => result(true, "ok"),
                Err(_) => result(false, "storage_failed"),
            }
        }
        "ui.view.set" => {
            if !l.allows(VIEW) {
                return Ok(result(false, "permission_denied"));
            }
            if limited(view, 250) {
                return Ok(result(false, "rate_limited"));
            }
            let v: View = decode(&m.payload)?;
            v.validate()?;
            if !l.allows(VIEW) {
                return Ok(result(false, "disabled"));
            }
            *l.view.lock().unwrap_or_else(|e| e.into_inner()) = Some(v);
            shared.signal();
            result(true, "ok")
        }
        "action.execute" => {
            let a: Action = decode(&m.payload)?;
            let Some(permission) = a.permission(true) else {
                return Ok(result(false, "unsupported_action"));
            };
            if !l.allows(permission) {
                return Ok(result(false, "permission_denied"));
            }
            if !super::capabilities::available(permission, &client.raw_read()) {
                return Ok(result(false, "unavailable"));
            }
            if limited(action, 500) {
                return Ok(result(false, "rate_limited"));
            }
            if permission == NAVIGATE {
                if !shared.foreground.load(Ordering::Acquire) {
                    return Ok(result(false, "navigation_unavailable"));
                }
                let page = match a.arguments["page"].as_str() {
                    Some("overview") => 1,
                    Some("display") => 2,
                    Some("power") => 3,
                    Some("devices") => 4,
                    Some("settings") => 5,
                    _ => return Ok(result(false, "unsupported_action")),
                };
                *shared.navigation.lock().unwrap_or_else(|e| e.into_inner()) =
                    Some((l.id.clone(), l.digest.clone(), page));
                shared.signal();
                result(true, "command_sent")
            } else {
                let receipt = match client.submit(
                    Command::PluginAction {
                        lease: l.clone(),
                        action: a.clone(),
                    },
                    false,
                ) {
                    Ok(p) => p.wait_active(&l.active, Duration::from_secs(10)),
                    Err(r) => r,
                };
                let (success, code) = match receipt.outcome {
                    Outcome::ReadbackConfirmed => (true, "readback_confirmed"),
                    Outcome::CommandSent => (true, "command_sent"),
                    Outcome::Busy => (false, "busy"),
                    Outcome::Closing | Outcome::Cancelled => (false, "disabled"),
                    _ => (false, "hardware_action_failed"),
                };
                json!({"success":success,"code":code,"data":if success{json!({"action":a.action,"requested":a.arguments})}else{Value::Null}})
            }
        }
        _ => result(false, "unsupported_method"),
    })
}
