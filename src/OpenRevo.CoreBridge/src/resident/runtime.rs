use super::{
    hardware::{HardwareSession, Outcome, Receipt},
    policy::Policies,
    queue::{Admission, Denied, Ticket},
};
use openrevo_core::{
    driver::wmi_event_listener::{listen_mode_keys, ModeKeyEvent},
    system::{
        app_preferences::AppPreferences, native_services,
        resident_request::MAX_PRESENTATION_DEMAND, session_power::SessionPower,
        system_preferences::SystemPreferences,
    },
};
use serde_json::{json, Value};
use std::{
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, Sender, SyncSender},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub enum Command {
    PluginAction {
        lease: Arc<super::plugins::Lease>,
        action: super::plugins::Action,
    },
    Plugins(Value),
    Maintenance(Value),
    Application(Value),
    CancelPanelTools,
    LoadBackgroundPlugins,
    StopBackgroundPlugins,
    ClosePluginViews,
    Takeover,
    Release,
    Factory {
        mode: u8,
        dc_confirmed: bool,
    },
    Battery(u8),
    Boost(bool),
    ToggleBoost,
    ToggleAwake,
    Suspension(bool),
    PowerStatusChanged,
    PhysicalKey {
        generation: u64,
        code: u32,
    },
    DesktopPolicy {
        link: bool,
        enabled: bool,
    },
    DeclaredTuning(Value),
    ReadTuning,
    TuningEditor {
        request: Value,
        upload: Option<Vec<u8>>,
    },
    ApplyTuning {
        baseline: String,
        profile: super::hardware::TuningProfile,
    },
    BatteryPermission(bool),
    ReadState,
    Desktop {
        request: Value,
        upload: Option<Vec<u8>>,
    },
    ReadSwitches,
    ToggleMicrophone,
    ToggleCamera,
    ToggleWinLock,
    ToggleOverviewSwitch(u32),
    DeclaredFnLock(bool),
    ReadGpu,
    SelectGpu {
        target: i32,
        expected: i32,
    },
    CommitPanelPreference(Value),
    CommitPanelLayout(Value),
    RememberPanelShape(bool),
    PresentationDemand(u8),
    SetOsdEnabled(bool),
    SetOsdAppearance {
        theme: String,
        scale: i32,
        position: String,
        motion: String,
    },
    ReadDisplay,
    Brightness(u8),
    BeginRefresh {
        expected: u32,
        target: u32,
    },
    FinishRefresh {
        id: String,
        keep: bool,
    },
    LowRefresh(bool),
    DisplayTopologyChanged,
}
struct Request {
    ticket: Ticket,
    command: Command,
    reply: Sender<Receipt>,
}
enum ActorMessage {
    Request(Request),
    Wake,
}

#[derive(Clone)]
pub struct Client {
    sender: SyncSender<ActorMessage>,
    admission: Admission,
    snapshot: Arc<Mutex<Arc<Value>>>,
    stop: Arc<AtomicBool>,
    panel_sampling_stopped: Arc<AtomicBool>,
    tray_hover: Arc<AtomicBool>,
    generation: Arc<AtomicU64>,
    pub plugins: Arc<super::plugins::Shared>,
    updates: Arc<super::updates::Shared>,
}
pub struct Pending {
    reply: Receiver<Receipt>,
    cancellation: Arc<AtomicBool>,
}
impl Pending {
    pub fn wait(self) -> Receipt {
        self.reply
            .recv()
            .unwrap_or_else(|_| Receipt::error(Outcome::Unknown, "原生运行线程已结束"))
    }
    pub fn wait_active(self, active: &AtomicBool, timeout: Duration) -> Receipt {
        let started = Instant::now();
        loop {
            if !active.load(Ordering::Acquire) || started.elapsed() >= timeout {
                self.cancel_before_admission();
                return Receipt::new(Outcome::Cancelled);
            }
            match self.reply.recv_timeout(Duration::from_millis(50)) {
                Ok(r) => return r,
                Err(mpsc::RecvTimeoutError::Disconnected) => return Receipt::new(Outcome::Closing),
                Err(mpsc::RecvTimeoutError::Timeout) => {}
            }
        }
    }
    pub fn cancel_before_admission(&self) {
        self.cancellation.store(true, Ordering::Release);
    }
}
fn denied(reason: Denied) -> Receipt {
    Receipt::new(match reason {
        Denied::Busy => Outcome::Busy,
        Denied::Closing => Outcome::Closing,
        Denied::Cancelled => Outcome::Cancelled,
    })
}
impl Client {
    pub fn submit(&self, command: Command, wait: bool) -> Result<Pending, Receipt> {
        let ticket = self.admission.reserve(wait).map_err(denied)?;
        let cancellation = ticket.cancellation();
        let (reply, receiver) = mpsc::channel();
        match self.sender.try_send(ActorMessage::Request(Request {
            ticket,
            command,
            reply,
        })) {
            Ok(()) => Ok(Pending {
                reply: receiver,
                cancellation,
            }),
            Err(error) => {
                self.admission.abandon();
                Err(Receipt::error(Outcome::Busy, error.to_string()))
            }
        }
    }
    pub fn read(&self) -> Value {
        let mut snapshot = (*self.raw_read()).clone();
        self.plugins.project(&mut snapshot);
        snapshot["Update"] = self.updates.snapshot();
        snapshot["Revision"] = json!(snapshot["Revision"]
            .as_u64()
            .unwrap_or(0)
            .wrapping_add(self.updates.revision.load(Ordering::Acquire)));
        snapshot
    }
    pub fn update_revision(&self) -> u64 {
        self.updates.revision.load(Ordering::Acquire)
    }
    pub fn update_snapshot(&self) -> Value {
        self.updates.snapshot()
    }
    pub fn snapshot_revision(&self) -> u64 {
        self.raw_read()["Revision"]
            .as_u64()
            .unwrap_or(0)
            .wrapping_add(self.plugins.revision.load(Ordering::Acquire))
            .wrapping_add(self.update_revision())
    }
    pub fn raw_read(&self) -> Arc<Value> {
        self.snapshot
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }
    pub fn request_tray_sample(&self) {
        if self.raw_read()["PanelPreferences"]["TrayTelemetry"] == true
            && !self.tray_hover.swap(true, Ordering::AcqRel)
        {
            let _ = self.sender.try_send(ActorMessage::Wake);
        }
    }
    pub fn stop_panel_sampling(&self) {
        self.panel_sampling_stopped.store(true, Ordering::Release);
    }
    pub fn stop_accepting(&self) {
        self.plugins.stop();
        self.admission.stop();
        self.stop.store(true, Ordering::Release);
        // A full queue already guarantees the sleeping owner will be woken.
        let _ = self.sender.try_send(ActorMessage::Wake);
    }
}
pub struct Runtime {
    pub client: Client,
    worker: Option<JoinHandle<Result<(), String>>>,
}
impl Runtime {
    pub fn start(
        directory: PathBuf,
        installed: bool,
        changed: impl Fn() + Send + Sync + 'static,
    ) -> Result<Self, String> {
        // Fail startup before creating a live tray if the committed configuration
        // cannot be loaded. This setup does not create a hardware session.
        crate::or_native_abi_version();
        native_services::set_data_root(directory.to_str().ok_or("数据路径不是有效 Unicode")?)?;
        let sys = SystemPreferences::load(&directory);
        let app = read_preferences(&directory)?;
        let changed: Arc<dyn Fn() + Send + Sync> = Arc::new(changed);
        let plugins = Arc::new(super::plugins::Shared::new(changed.clone()));
        let updates = Arc::new(super::updates::Shared::new(installed, changed.clone()));
        let (sender, receiver) = mpsc::sync_channel(64);
        let client = Client {
            plugins: plugins.clone(),
            updates: updates.clone(),
            sender,
            admission: Admission::default(),
            snapshot: Arc::new(Mutex::new(Arc::new(Value::Null))),
            stop: Arc::new(AtomicBool::new(false)),
            panel_sampling_stopped: Arc::new(AtomicBool::new(true)),
            tray_hover: Arc::new(AtomicBool::new(false)),
            generation: Arc::new(AtomicU64::new(0)),
        };
        let background = client.clone();
        let worker = thread::Builder::new()
            .name("OpenRevo.Native.Owner".into())
            .spawn(move || {
                let mut actor = Actor {
                    installed,
                    updates: super::updates::Updates::new(updates, installed),
                    storage_exit_required: false,
                    install_on_exit: false,
                    hardware: HardwareSession::new(directory.clone(), sys.unlock_battery_limit),
                    policies: Policies::load(&directory),
                    power: SessionPower::default(),
                    display: super::display::Display::new(app["LowRefreshOnBattery"] == true),
                    plugins: super::plugins::Manager::new(
                        directory.clone(),
                        background.clone(),
                        plugins,
                    ),
                    client: background,
                    layouts: super::panel_settings::layouts(&directory),
                    presentation_demand: 0,
                    telemetry: super::telemetry::Telemetry::default(),
                    next_sample: Instant::now(),
                    switches: json!({}),
                    next_switch_poll: Instant::now(),
                    device_identity: None,
                    camera: super::hardware::Camera::default(),
                    desktop: super::hardware::DesktopState::default(),
                    editor: super::hardware::TuningEditor::new(directory.clone()),
                    gpu: super::hardware::GpuControl::new(directory.clone()),
                    osd_notice: None,
                    osd_sequence: 0,
                    directory,
                    preferences: app,
                    settings: sys,
                    listener: None,
                    last_key: None,
                    automatic_pending: false,
                    last_automatic: None,
                    revision: 0,
                    changed,
                };
                actor.run(receiver)
            })
            .map_err(|e| e.to_string())?;
        Ok(Self {
            client,
            worker: Some(worker),
        })
    }
    pub fn finish(&mut self) -> Result<(), String> {
        self.client.stop_accepting();
        self.worker
            .take()
            .map(|w| {
                w.join()
                    .map_err(|_| "原生运行线程异常终止".to_string())
                    .and_then(|v| v)
            })
            .unwrap_or(Ok(()))
    }
}
impl Drop for Runtime {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}
fn read_preferences(directory: &std::path::Path) -> Result<Value, String> {
    let bytes = AppPreferences::load_snapshot(directory, false).map_err(|_| "应用设置读取失败")?;
    serde_json::from_slice(&bytes).map_err(|e| e.to_string())
}
struct Listener {
    status: Arc<Mutex<Value>>,
    stop: Arc<AtomicBool>,
    thread: JoinHandle<()>,
}
struct Actor {
    hardware: HardwareSession,
    policies: Policies,
    power: SessionPower,
    display: super::display::Display,
    client: Client,
    directory: PathBuf,
    preferences: Value,
    settings: SystemPreferences,
    layouts: Value,
    presentation_demand: u8,
    telemetry: super::telemetry::Telemetry,
    next_sample: Instant,
    switches: Value,
    next_switch_poll: Instant,
    device_identity: Option<(String, String, String)>,
    camera: super::hardware::Camera,
    desktop: super::hardware::DesktopState,
    editor: super::hardware::TuningEditor,
    gpu: super::hardware::GpuControl,
    osd_notice: Option<Value>,
    osd_sequence: u64,
    listener: Option<Listener>,
    last_key: Option<Instant>,
    automatic_pending: bool,
    last_automatic: Option<Instant>,
    revision: u64,
    changed: Arc<dyn Fn() + Send + Sync>,
    plugins: super::plugins::Manager,
    installed: bool,
    updates: super::updates::Updates,
    storage_exit_required: bool,
    install_on_exit: bool,
}
impl Actor {
    fn run(&mut self, receiver: Receiver<ActorMessage>) -> Result<(), String> {
        if let Ok(ticket) = self.client.admission.reserve(true) {
            if let Ok(accepted) = self.client.admission.enter(ticket) {
                self.automatic_takeover();
                if let Err(error) = self.hardware.adopt_saved_color() {
                    let _ = self
                        .hardware
                        .audit("resident.saved-color.failed", json!({"error":error}));
                }
                drop(accepted);
            }
        }
        self.publish();
        let _ = self.plugins.load_background();
        self.publish();
        let mut next_policy = Instant::now() + Duration::from_secs(3);
        let mut last_tray_hover: Option<Instant> = None;
        while !self.client.stop.load(Ordering::Acquire) {
            if self.display.poll_brightness() {
                self.publish();
            }
            self.plugins.reap();
            self.updates.reap();
            if self.updates.shared.verified() {
                self.install_on_exit = true;
                self.client.admission.stop();
                super::window::request_exit();
                break;
            }
            if self.display.expire() {
                self.publish();
            }
            let now = Instant::now();
            let mut deadline = if self.client.admission.accepting() {
                let sample = next_policy.min(self.next_sample);
                if self.presentation_demand >= 2
                    && !self.client.panel_sampling_stopped.load(Ordering::Acquire)
                {
                    sample.min(self.next_switch_poll)
                } else {
                    sample
                }
            } else {
                now + Duration::from_secs(3)
            };
            if let Some(trial) = self.display.trial_deadline() {
                deadline = deadline.min(trial);
            }
            if self.plugins.has_sessions() || self.updates.has_worker() {
                deadline = deadline.min(now + Duration::from_millis(100));
            }
            match receiver.recv_timeout(deadline.saturating_duration_since(now)) {
                Ok(ActorMessage::Request(request)) => {
                    match self.client.admission.enter(request.ticket) {
                        Ok(accepted) => {
                            // The mutable session is owned here until the entire transaction settles.
                            // Closing a UI or dropping a reply never cancels this branch midway.
                            let result = self.execute(request.command);
                            drop(accepted);
                            self.synchronize_keys();
                            self.publish();
                            let _ = request.reply.send(result);
                        }
                        Err(reason) => {
                            let _ = request.reply.send(denied(reason));
                        }
                    }
                }
                Ok(ActorMessage::Wake) => {
                    if self.client.stop.load(Ordering::Acquire) {
                        continue;
                    }
                    let panel_hidden = self.presentation_demand == 0
                        || self.client.panel_sampling_stopped.load(Ordering::Acquire);
                    if panel_hidden
                        && self.settings.tray_telemetry
                        && self.client.tray_hover.load(Ordering::Acquire)
                        && last_tray_hover.is_none_or(|at| at.elapsed() >= Duration::from_secs(3))
                    {
                        self.next_sample = Instant::now();
                    }
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
                Err(mpsc::RecvTimeoutError::Timeout) => {}
            }
            let mut sampled = false;
            if Instant::now() >= self.next_sample && self.client.admission.accepting() {
                let tray_enabled = self.settings.tray_telemetry;
                let panel_stopped = &self.client.panel_sampling_stopped;
                let demand = if panel_stopped.load(Ordering::Acquire) {
                    0
                } else {
                    self.presentation_demand
                };

                if demand != 0 || tray_enabled || self.client.plugins.has_summary() {
                    if let Ok(ticket) = self.client.admission.reserve(false) {
                        if let Ok(accepted) = self.client.admission.enter(ticket) {
                            let tray_hover = self.client.tray_hover.swap(false, Ordering::AcqRel)
                                && tray_enabled;
                            self.telemetry.sample(
                                demand,
                                tray_enabled,
                                &mut self.hardware,
                                tray_hover,
                            );
                            if tray_hover {
                                last_tray_hover = Some(Instant::now());
                            }
                            self.client
                                .plugins
                                .publish_summary(&self.telemetry.latest_system);
                            if self.panel_needs_display(demand)
                                && !self.client.panel_sampling_stopped.load(Ordering::Acquire)
                            {
                                self.display.refresh_visible();
                            }
                            drop(accepted);
                            sampled = true;
                        }
                    }
                } else {
                    self.telemetry.idle();
                }
                let seconds = if demand != 0 {
                    self.preferences["SampleSeconds"].as_u64().unwrap_or(2)
                } else {
                    3
                };
                self.next_sample = Instant::now() + Duration::from_secs(seconds);
            }
            if self.presentation_demand >= 2
                && !self.client.panel_sampling_stopped.load(Ordering::Acquire)
                && self.client.admission.accepting()
                && Instant::now() >= self.next_switch_poll
            {
                self.next_switch_poll = Instant::now() + Duration::from_secs(6);
                if let Ok(ticket) = self.client.admission.reserve(false) {
                    if let Ok(accepted) = self.client.admission.enter(ticket) {
                        self.read_visible_switches(self.presentation_demand);
                        drop(accepted);
                        sampled = true;
                    }
                } else {
                    self.next_switch_poll = Instant::now() + Duration::from_millis(100);
                }
            }
            if Instant::now() >= next_policy && self.client.admission.accepting() {
                // Timer work enters the same admission gate; there is no second hardware queue.
                if let Ok(ticket) = self.client.admission.reserve(true) {
                    if let Ok(accepted) = self.client.admission.enter(ticket) {
                        self.evaluate();
                        drop(accepted);
                        self.synchronize_keys();
                        sampled = true;
                    }
                }
                next_policy = Instant::now() + Duration::from_secs(3);
            }
            if sampled {
                self.publish();
            }
        }
        self.client.admission.stop();
        while let Ok(message) = receiver.try_recv() {
            if let ActorMessage::Request(request) = message {
                self.client.admission.abandon();
                let _ = request.reply.send(Receipt::new(Outcome::Closing));
            }
        }
        self.telemetry.idle();
        openrevo_core::driver::nvapi::release_session();
        self.stop_keys();
        let plugins = self.plugins.stop_all();
        // Every accepted command has completed before any hardware resource is released.
        let display = self.display.shutdown();
        let desktop = crate::native_services::or_native_services_shutdown();
        let power = self.power.shutdown().map_err(|e| e.to_string());
        self.hardware.release();
        self.publish();
        if desktop != 0 {
            return Err(format!("桌面服务清理失败 ({desktop})"));
        }
        let settled = display.and(power).and(plugins);
        let update = self
            .updates
            .finish(self.install_on_exit && settled.is_ok() && !self.storage_exit_required);
        settled.and(update)
    }
    fn maintenance(&mut self, request: Value) -> Receipt {
        if !self.installed {
            return Receipt::error(Outcome::Rejected, "存储维护须从有效安装入口运行");
        }
        let action = request["Action"].as_u64().unwrap_or(0);
        let mutation = matches!(action, 5 | 6);
        if mutation {
            if self.display.snapshot()["Trial"].is_object()
                || matches!(
                    self.updates.shared.snapshot()["Phase"].as_u64(),
                    Some(1 | 4 | 5)
                )
            {
                return Receipt::new(Outcome::Busy);
            }
            if let Some(compact) = request["Compact"].as_bool() {
                if let Err(_) = SystemPreferences::apply_edit(
                    &self.directory,
                    openrevo_core::system::system_preferences::SystemPreferenceEdit::LastMini {
                        value: compact,
                    },
                ) {
                    return Receipt::error(Outcome::ConfigurationSaveFailed, "关闭形态保存失败");
                }
            }
            self.client.admission.stop();
            self.storage_exit_required = true;
            self.stop_keys();
            self.telemetry.idle();
            if let Err(e) = self.plugins.stop_all() {
                return Receipt::error(Outcome::Unknown, e);
            }
            if let Err(e) = self.display.shutdown() {
                return Receipt::error(Outcome::Unknown, e);
            }
            if crate::native_services::or_native_services_shutdown() != 0 {
                return Receipt::error(Outcome::Unknown, "桌面服务尚未确认释放");
            }
            if let Err(e) = self.power.shutdown() {
                return Receipt::error(Outcome::Unknown, e.to_string());
            }
            self.hardware.release();
        }
        let response = super::tools::call(
            "maintenance",
            json!({"Request":request,"Snapshot":self.client.read()}),
            if mutation {
                None
            } else {
                Some(Duration::from_secs(30))
            },
            None,
            |_| {},
        );
        let mut receipt = match response {
            Ok(v) => {
                let mut r = Receipt::new(if mutation {
                    Outcome::ConfigurationSaved
                } else {
                    Outcome::Applied
                });
                r.payload = Some(json!({"Maintenance":v["Maintenance"]}));
                r
            }
            Err(e) => {
                let mut r = Receipt::error(
                    if mutation {
                        Outcome::ConfigurationSaveFailed
                    } else {
                        Outcome::Rejected
                    },
                    e,
                );
                if mutation {
                    r.payload = Some(json!({"Maintenance":{"ExitRequired":true}}));
                }
                r
            }
        };
        if mutation {
            if let Some(v) = receipt.payload.as_mut() {
                v["Maintenance"]["ExitRequired"] = json!(true);
            }
        }
        receipt
    }
    fn execute(&mut self, command: Command) -> Receipt {
        match command {
            Command::Application(request) => self.updates.execute(request),
            Command::CancelPanelTools => {
                self.updates.cancel();
                Receipt::new(Outcome::Applied)
            }
            Command::Maintenance(request) => {
                let mut receipt = self.maintenance(request);
                if self.storage_exit_required {
                    if receipt.payload.is_none() {
                        receipt.payload = Some(json!({}));
                    }
                    if let Some(v) = receipt.payload.as_mut() {
                        v["Maintenance"]["ExitRequired"] = json!(true);
                    }
                }
                receipt
            }
            Command::Plugins(request) => self.plugins.execute(request),
            Command::LoadBackgroundPlugins => self.plugins.load_background(),
            Command::StopBackgroundPlugins => {
                desktop_result(self.plugins.stop_all(), Outcome::Applied)
            }
            Command::ClosePluginViews => {
                desktop_result(self.plugins.close_views(), Outcome::Applied)
            }
            Command::PluginAction { lease, action } => {
                let permission = action.permission(false);
                if permission.is_none_or(|p| !lease.allows(p)) {
                    return Receipt::new(Outcome::Cancelled);
                }
                self.hardware.plugin_action(lease.active.clone(), &action)
            }
            Command::Takeover => {
                self.stop_keys();
                self.automatic_pending = false;
                self.last_automatic = None;
                self.policies.reset_session();
                self.desktop = super::hardware::DesktopState::default();
                self.editor.reset();
                self.hardware.takeover()
            }
            Command::Release => {
                self.automatic_pending = false;
                self.stop_keys();
                self.hardware.release();
                self.desktop = super::hardware::DesktopState::default();
                self.editor.reset();
                self.policies.reset_session();
                Receipt::new(Outcome::Applied)
            }
            Command::Factory { mode, dc_confirmed } => {
                if mode == 4 {
                    self.hardware
                        .select_saved_custom(&mut self.editor, &mut self.preferences)
                } else {
                    self.hardware.select_factory(mode, dc_confirmed)
                }
            }
            Command::Battery(percent) => self.hardware.battery(percent),
            Command::Boost(enabled) => self.hardware.boost(enabled),
            Command::ToggleBoost => {
                let enabled = self
                    .hardware
                    .state
                    .as_ref()
                    .map(|s| s.fan_clamp & 64 != 0)
                    .or(self.hardware.requested_boost)
                    != Some(true);
                self.hardware.boost(enabled)
            }
            Command::ToggleAwake => match self.power.toggle_keep_awake() {
                Ok(_) => Receipt::new(Outcome::ReadbackConfirmed),
                Err(e) => Receipt::error(Outcome::Unknown, e.to_string()),
            },
            Command::PowerStatusChanged => {
                self.client.plugins.publish_power();
                self.evaluate();
                Receipt::new(Outcome::Applied)
            }
            Command::Suspension(sleeping) => {
                self.policies.observe_sleep(&mut self.hardware, sleeping)
            }
            Command::PhysicalKey { generation, code } => {
                if generation != self.client.generation.load(Ordering::Acquire)
                    || !self.hardware.active()
                    || !self.hardware.classic()
                {
                    return Receipt::new(Outcome::Skipped);
                }
                if self
                    .last_key
                    .is_some_and(|at| at.elapsed() < Duration::from_millis(500))
                {
                    return Receipt::new(Outcome::Skipped);
                }
                self.last_key = Some(Instant::now());
                // Refresh under the application transaction; do not cycle a stale cached mode.
                let before = match self.hardware.refresh_mode() {
                    Ok(mode) => mode,
                    Err(error) => return Receipt::error(Outcome::Unknown, error),
                };
                let limited =
                    !self.hardware.allow_battery && super::platform::ac_online() != Some(true);
                let mode = match before {
                    Some(1) => 2,
                    Some(2) if !limited => 3,
                    _ => 1,
                };
                let _ = self.hardware.audit(
                    "modekey.intent",
                    json!({"code":code,"before":before,"target":mode,"batteryLimited":limited}),
                );
                let receipt = self.hardware.select_factory(mode, mode == 3);
                let _ = self.hardware.audit("modekey.result", json!(receipt));
                receipt
            }
            Command::DesktopPolicy { link, enabled } => {
                self.policies
                    .edit(&mut self.hardware, &self.directory, link, enabled)
            }
            Command::TuningEditor { request, upload } => self.hardware.tuning_editor_command(
                &mut self.editor,
                &request,
                upload.as_deref(),
                &mut self.preferences,
            ),
            Command::DeclaredTuning(preset) => self.hardware.apply_declared(&preset),
            Command::ReadTuning => match self.hardware.probe_tuning() {
                Ok(_) => Receipt::new(Outcome::ReadbackConfirmed),
                Err(error) => Receipt::error(Outcome::Rejected, error),
            },
            Command::ApplyTuning { baseline, profile } => {
                self.hardware.apply_tuning(&baseline, profile)
            }
            Command::BatteryPermission(enabled) => {
                use openrevo_core::system::system_preferences::{
                    SystemPreferenceEdit, SystemPreferences,
                };
                match SystemPreferences::apply_edit(
                    &self.directory,
                    SystemPreferenceEdit::UnlockBatteryLimit { value: enabled },
                ) {
                    Ok(_) => {
                        self.hardware.allow_battery = enabled;
                        Receipt::new(Outcome::ConfigurationSaved)
                    }
                    Err(_) => Receipt::error(Outcome::ConfigurationSaveFailed, "离电设置保存失败"),
                }
            }
            Command::ReadState => Receipt::new(Outcome::Skipped),
            Command::Desktop { request, upload } => {
                self.desktop_command(&request, upload.as_deref())
            }
            Command::ReadSwitches => {
                self.read_switches();
                Receipt::new(Outcome::ReadbackConfirmed)
            }
            Command::ToggleCamera => {
                let result = self.hardware.toggle_camera(&mut self.camera);
                self.read_camera_switch();
                if matches!(result.outcome, Outcome::ReadbackConfirmed) {
                    if let Some(enabled) = self.switches["Camera"].as_bool() {
                        self.notify_osd("camera", enabled);
                    }
                }
                result
            }
            Command::ToggleMicrophone => {
                let code = crate::or_native_microphone_toggle();
                if matches!(code, 0 | 1) {
                    self.switches["Microphone"] = json!(code == 1);
                    self.notify_osd("mic", code == 1);
                    Receipt::new(Outcome::ReadbackConfirmed)
                } else {
                    self.switches["Microphone"] = Value::Null;
                    Receipt::error(Outcome::Unknown, format!("麦克风回读未确认 ({code})"))
                }
            }
            Command::ToggleWinLock => {
                let code = crate::native_admission::or_native_win_lock(2);
                if matches!(code, 0 | 1) {
                    self.switches["WinLock"] = json!(code == 1);
                    self.notify_osd("win_key", code == 1);
                    Receipt::new(Outcome::ReadbackConfirmed)
                } else {
                    self.switches["WinLock"] = Value::Null;
                    Receipt::error(Outcome::Unknown, format!("Win键软件锁未确认 ({code})"))
                }
            }
            Command::ToggleOverviewSwitch(id) => {
                let (key, feature) = match id {
                    1 => ("Wifi", "wifi"),
                    2 => ("Bluetooth", "bluetooth"),
                    3 => ("Touchpad", "touchpad"),
                    4 => ("FnLock", "fn_lock"),
                    _ => return Receipt::error(Outcome::Rejected, "无效开关"),
                };
                self.read_overview_switch(id, key);
                let Some(before) = self.switches[key].as_bool() else {
                    return Receipt::error(Outcome::Rejected, "设备当前状态未知");
                };
                let target = !before;
                let receipt = if id <= 2 {
                    let code = crate::native_overview::or_native_switch_set(
                        id,
                        u32::from(before),
                        u32::from(target),
                    );
                    if code == 0 {
                        Receipt::new(Outcome::ReadbackConfirmed)
                    } else {
                        Receipt::error(Outcome::Unknown, format!("设备回读未确认 ({code})"))
                    }
                } else {
                    self.hardware.overview_switch(id, before, target)
                };
                if matches!(receipt.outcome, Outcome::ReadbackConfirmed) {
                    self.switches[key] = json!(target);
                    self.notify_osd(feature, target);
                } else if matches!(receipt.outcome, Outcome::Unknown) {
                    self.switches[key] = Value::Null;
                }
                receipt
            }
            Command::DeclaredFnLock(enabled) => self.hardware.declared_fn_lock(enabled),
            Command::ReadGpu => self.gpu.refresh(&self.hardware, &mut self.preferences),
            Command::SelectGpu { target, expected } => {
                self.hardware
                    .select_gpu(&mut self.gpu, target, expected, &mut self.preferences)
            }
            Command::PresentationDemand(value) if i32::from(value) <= MAX_PRESENTATION_DEMAND => {
                if self.presentation_demand != value {
                    self.telemetry.stop_sampling();
                    self.next_sample = Instant::now();
                    self.next_switch_poll = self.next_sample;
                }
                if value == 0 {
                    openrevo_core::driver::nvapi::release_session();
                }
                self.presentation_demand = value;
                self.client
                    .panel_sampling_stopped
                    .store(value == 0, Ordering::Release);
                Receipt::new(Outcome::Applied)
            }
            Command::PresentationDemand(_) => Receipt::error(Outcome::Rejected, "无效显示需求"),
            Command::CommitPanelPreference(edit) => {
                let tray_was_enabled = self.settings.tray_telemetry;
                let outcome = super::panel_settings::commit_preference(
                    &self.directory,
                    &mut self.preferences,
                    &mut self.settings,
                    &edit,
                );
                if matches!(outcome.outcome, Outcome::ConfigurationSaved) {
                    self.hardware.allow_battery = self.settings.unlock_battery_limit;
                    if tray_was_enabled != self.settings.tray_telemetry {
                        self.next_sample = Instant::now();
                        if !self.settings.tray_telemetry {
                            self.client.tray_hover.store(false, Ordering::Release);
                            self.telemetry.clear_tray();
                        }
                    }
                    if self.presentation_demand <= 1 && !self.settings.tray_telemetry {
                        self.telemetry.stop_sampling();
                    }
                }
                outcome
            }
            Command::CommitPanelLayout(edit) => {
                match super::panel_settings::commit_layout(&self.directory, &edit) {
                    Ok(layouts) => {
                        self.layouts = layouts;
                        self.next_switch_poll = Instant::now();
                        Receipt::new(Outcome::ConfigurationSaved)
                    }
                    Err(error) => Receipt::error(Outcome::ConfigurationSaveFailed, error),
                }
            }
            Command::RememberPanelShape(value) => {
                if value == self.settings.last_mini {
                    return Receipt::new(Outcome::Skipped);
                }
                super::panel_settings::commit_system(
                    &self.directory,
                    &mut self.settings,
                    openrevo_core::system::system_preferences::SystemPreferenceEdit::LastMini {
                        value,
                    },
                )
            }
            Command::SetOsdEnabled(enabled) => {
                use openrevo_core::system::app_preferences::AppPreferenceEdit;
                match AppPreferences::apply_edit(
                    &self.directory,
                    AppPreferenceEdit::OsdEnabled { value: enabled },
                ) {
                    Ok(bytes) => match serde_json::from_slice(&bytes) {
                        Ok(settings) => {
                            self.preferences = settings;
                            Receipt::new(Outcome::ConfigurationSaved)
                        }
                        Err(error) => {
                            Receipt::error(Outcome::ConfigurationSaveFailed, error.to_string())
                        }
                    },
                    Err(_) => Receipt::error(Outcome::ConfigurationSaveFailed, "OSD 设置提交失败"),
                }
            }
            Command::SetOsdAppearance {
                theme,
                scale,
                position,
                motion,
            } => {
                use openrevo_core::system::system_preferences::SystemPreferenceEdit;
                match SystemPreferences::apply_edit(
                    &self.directory,
                    SystemPreferenceEdit::OsdAppearance {
                        theme,
                        scale,
                        position,
                        motion,
                    },
                ) {
                    Ok((settings, _)) => {
                        self.settings = settings;
                        Receipt::new(Outcome::ConfigurationSaved)
                    }
                    Err(_) => {
                        Receipt::error(Outcome::ConfigurationSaveFailed, "OSD 外观设置提交失败")
                    }
                }
            }
            Command::ReadDisplay => {
                desktop_result(self.display.read_for_panel(), Outcome::ReadbackConfirmed)
            }
            Command::Brightness(level) => {
                let result = self.display.set_brightness(level);
                if result.is_ok() {
                    self.notify_osd("brightness", level);
                }
                desktop_result(result, Outcome::ReadbackConfirmed)
            }
            Command::BeginRefresh { expected, target } => desktop_result(
                self.display.begin_trial(expected, target).map(|_| ()),
                Outcome::ReadbackConfirmed,
            ),
            Command::FinishRefresh { id, keep } => desktop_result(
                self.display.finish_trial(&id, keep),
                Outcome::ReadbackConfirmed,
            ),
            Command::LowRefresh(enabled) => desktop_result(
                self.display.set_policy(&self.directory, enabled),
                Outcome::ConfigurationSaved,
            ),
            Command::DisplayTopologyChanged => {
                self.display.invalidate();
                self.telemetry.invalidate();
                self.next_sample = Instant::now();
                Receipt::new(Outcome::Applied)
            }
        }
    }
    fn desktop_command(&mut self, request: &Value, upload: Option<&[u8]>) -> Receipt {
        let command = request["Command"].as_str().unwrap_or("");
        let group = match command {
            "read" => request["Group"].as_i64().unwrap_or(0) as i32,
            "keyboard-rgb" | "keyboard-undo" | "keyboard-white" | "zone" => 1,
            "color" | "icc-select" | "icc-import" => 2,
            "power-policy" | "power-plan" | "power-recovery" | "display-auto-refresh" => 3,
            _ => 0,
        };
        let mut receipt = match command {
            "power-policy" if matches!(request["Key"].as_str(), Some("office" | "link")) => {
                self.policies.edit(
                    &mut self.hardware,
                    &self.directory,
                    request["Key"] == "link",
                    request["Enabled"] == true,
                )
            }
            "display-auto-refresh" => desktop_result(
                self.display
                    .set_policy(&self.directory, request["Enabled"] == true),
                Outcome::ConfigurationSaved,
            ),
            "display-off" => self.screen_off(),
            _ => self
                .hardware
                .execute_desktop(&mut self.desktop, request, upload, &self.directory),
        };
        if group != 0 && receipt.payload.is_none() {
            let low = self.display.snapshot()["LowRefreshOnBattery"] == true;
            receipt.payload = Some(
                json!({"Desktop":self.hardware.desktop_snapshot(&self.desktop,group,&self.policies.settings,low)}),
            );
        }
        receipt
    }
    fn screen_off(&mut self) -> Receipt {
        let result = (|| -> Result<(), String> {
            let delay = self.power.begin_screen_off();
            if delay == 0 {
                return Err("息屏请求正在执行".into());
            }
            self.power.prepare_screen_off().map_err(|e| e.to_string())?;
            thread::sleep(Duration::from_millis(u64::from(delay)));
            self.power
                .capture_screen_off_input()
                .map_err(|e| e.to_string())?;
            super::window::request_screen_off()?;
            self.power.finish_screen_off();
            Ok(())
        })();
        if result.is_err() {
            let _ = self.power.cancel_screen_off();
        }
        desktop_result(result, Outcome::CommandSent)
    }
    fn read_camera_switch(&mut self) {
        let (camera, available) = self.camera.read(&self.hardware);
        self.switches["Camera"] = json!(camera);
        self.switches["CameraAvailable"] = json!(available);
    }
    fn read_overview_switch(&mut self, id: u32, key: &str) {
        self.switches[key] = json!(if id <= 2 || self.hardware.active() {
            crate::native_overview::switch_read(id)
        } else {
            None
        });
    }
    fn read_monochrome_switch(&mut self) {
        self.switches["MonochromeLevel"] = json!(crate::native_overview::read_monochrome());
        self.switches["MonochromeAt"] = json!(super::panel_wire::utc_iso(
            u64::try_from(super::platform::unix_ms()).unwrap_or(0)
        ));
    }
    fn read_shortcut_switch(&mut self, shortcut: &str) {
        match shortcut {
            "camera" => self.read_camera_switch(),
            "mic" => self.switches["Microphone"] = json!(crate::microphone_read()),
            "win" => {
                let win = crate::native_admission::or_native_win_lock(-1);
                self.switches["WinLock"] = json!(matches!(win, 0 | 1).then_some(win == 1));
            }
            "wifi" => self.read_overview_switch(1, "Wifi"),
            "bluetooth" => self.read_overview_switch(2, "Bluetooth"),
            "touchpad" => self.read_overview_switch(3, "Touchpad"),
            "fn" => self.read_overview_switch(4, "FnLock"),
            _ => {}
        }
    }
    fn panel_needs_display(&self, demand: u8) -> bool {
        let has = |list: &Value, names: &[&str]| {
            list.as_array().is_some_and(|items| {
                items.iter().filter_map(Value::as_str).any(|item| names.contains(&item))
            })
        };
        match demand {
            2 => has(&self.layouts["Overview"], &["display"]),
            3 | 4 => has(&self.layouts["Mini"]["Top"], &["refresh", "brightness"]),
            _ => false,
        }
    }
    fn read_visible_switches(&mut self, demand: u8) {
        match demand {
            2 => {
                for shortcut in [
                    "camera",
                    "mic",
                    "win",
                    "wifi",
                    "bluetooth",
                    "touchpad",
                    "fn",
                ] {
                    self.read_shortcut_switch(shortcut);
                }
            }
            3 | 4 => {
                let mini = &self.layouts["Mini"];
                let quick = mini["Quick"].as_array().cloned().unwrap_or_default();
                let keyboard = mini["Top"]
                    .as_array()
                    .is_some_and(|tiles| tiles.iter().any(|tile| tile == "keyboard"));
                for shortcut in quick.iter().filter_map(Value::as_str) {
                    self.read_shortcut_switch(shortcut);
                }
                if keyboard {
                    self.read_monochrome_switch();
                }
            }
            5 => self.read_monochrome_switch(),
            _ => {}
        }
    }
    fn read_switches(&mut self) {
        self.read_visible_switches(2);
        self.read_monochrome_switch();
    }
    fn notify_osd(&mut self, feature: &str, value: impl Into<Value>) {
        self.osd_sequence = self.osd_sequence.wrapping_add(1);
        let value: Value = value.into();
        self.osd_notice =
            Some(json!({"Sequence":self.osd_sequence,"Feature":feature,"Value":value}));
    }
    fn automatic_takeover(&mut self) {
        self.last_automatic = Some(Instant::now());
        let receipt = self.hardware.takeover();
        self.automatic_pending = receipt
            .message
            .as_deref()
            .is_some_and(|m| m.starts_with("请先正常退出其他硬件控制程序："));
        let _ = self.hardware.audit("resident.takeover", json!(receipt));
        if self.installed && matches!(receipt.outcome, Outcome::ReadbackConfirmed) {
            if let Some(restore) = self.hardware.restore_factory_mode() {
                let _ = self.hardware.audit("resident.factory-mode-restore", json!(restore));
            }
        }
        self.synchronize_keys();
    }
    fn evaluate(&mut self) {
        if self.automatic_pending
            && self
                .last_automatic
                .is_none_or(|at| at.elapsed() >= Duration::from_secs(6))
        {
            self.automatic_takeover();
        }
        if let Err(error) = self.power.restore_on_input() {
            let _ = self.hardware.audit(
                "resident.power-request.failed",
                json!({"error":error.to_string()}),
            );
        }
        if self.hardware.active() {
            if let Err(error) = self.display.evaluate() {
                let _ = self
                    .hardware
                    .audit("resident.display-policy.failed", json!({"error":error}));
            }
        }
        self.policies
            .evaluate(&mut self.hardware, &self.preferences);
    }
    fn synchronize_keys(&mut self) {
        if !self.hardware.active()
            || !self.hardware.classic()
            || !self.hardware.supports("performance")
            || !self.client.admission.accepting()
        {
            self.stop_keys();
            return;
        }
        if self.listener.is_some() {
            return;
        }
        let generation = self
            .client
            .generation
            .fetch_add(1, Ordering::AcqRel)
            .wrapping_add(1);
        let stop = Arc::new(AtomicBool::new(false));
        let stop_listener = stop.clone();
        let client = self.client.clone();
        let status = Arc::new(Mutex::new(
            json!({"Attempted":true,"Subscribed":false,"Failure":null}),
        ));
        let observed = status.clone();
        let thread = thread::spawn(move || {
            let result = listen_mode_keys(|event| {
                if stop_listener.load(Ordering::Acquire)
                    || client.stop.load(Ordering::Acquire)
                    || client.generation.load(Ordering::Acquire) != generation
                {
                    return false;
                }
                if matches!(event, ModeKeyEvent::Subscribed) {
                    observed.lock().unwrap_or_else(|e| e.into_inner())["Subscribed"] = json!(true);
                }
                if let ModeKeyEvent::Pressed(code) = event {
                    let _ = client.submit(Command::PhysicalKey { generation, code }, false);
                }
                true
            });
            if let Err(error) = result {
                let mut status = observed.lock().unwrap_or_else(|e| e.into_inner());
                status["Subscribed"] = json!(false);
                status["Failure"] = json!(error.to_string());
            }
        });
        self.listener = Some(Listener {
            stop,
            thread,
            status,
        });
    }
    fn stop_keys(&mut self) {
        self.client.generation.fetch_add(1, Ordering::AcqRel);
        if let Some(listener) = self.listener.take() {
            listener.stop.store(true, Ordering::Release);
            let _ = listener.thread.join();
        }
    }
    fn publish(&mut self) {
        self.revision = self.revision.wrapping_add(1);
        let mut snapshot = json!({"Revision":self.revision,
            "Ready":self.hardware.admission["ready"],"Initialized":true,"PanelPreferences":{"Theme":self.preferences["Theme"],"SampleSeconds":self.preferences["SampleSeconds"],"WakeMode":self.settings.wake_mode,"MiniAutoHide":self.settings.mini_auto_hide,"BatteryOverride":self.settings.unlock_battery_limit,"TrayTelemetry":self.settings.tray_telemetry},"LastMini":self.settings.last_mini,"Layouts":self.layouts,"Active":self.hardware.active(),"Mode":self.hardware.mode,
            "Osd":{"Enabled":self.preferences["OsdEnabled"],"Theme":self.settings.osd_theme,"Scale":self.settings.osd_scale,"Position":self.settings.osd_position,"Motion":self.settings.motion_speed},"Display":self.display.snapshot(),"Tuning":self.hardware.tuning_baseline,"PhysicalMode":self.listener.as_ref().map(|l|l.status.lock().unwrap_or_else(|e|e.into_inner()).clone()),"Control":self.hardware.state,"Admission":self.hardware.admission,"Faulted":self.hardware.faulted,
            "RequestedFanBoost":self.hardware.requested_boost,"KeepAwake":self.power.keep_awake_enabled(),
            "WaitingForTakeover":self.automatic_pending,"PhysicalModeAttempted":self.listener.is_some(),
            "Switches":self.switches,"Gpu":self.gpu.snapshot(),"OsdNotice":self.osd_notice,"PresentationDemand":self.presentation_demand,"Accepting":self.client.admission.accepting()});
        snapshot["SystemSummary"] = self.telemetry.summary.clone();
        snapshot["Details"] = self.telemetry.details.clone();
        snapshot["TraySample"] = self.telemetry.tray.clone();
        let a = &self.hardware.admission;
        snapshot["Device"] = if a["ready"] == true {
            let (board, product, processor) = self.device_identity.get_or_insert_with(|| {
                (
                    super::platform::bios_string("BaseBoardProduct")
                        .unwrap_or_else(|| "未知".into()),
                    super::platform::bios_string("SystemProductName")
                        .unwrap_or_else(|| "未知".into()),
                    openrevo_core::driver::uefi_nvram::get_cpu_info().0,
                )
            });
            let declared = !self.hardware.classic();
            let supports_fn = declared && self.hardware.supports("fn_lock");
            json!({"Board":board,"Product":product,"Processor":processor,"Declared":declared,
                "IntegratedOnly":a["discrete_gpu"]==false,
                "SupportsCustomTuning":self.hardware.supports("custom_tuning")||a["declared_tuning"].is_object(),
                "SupportsFnLock":supports_fn,
                "CanSendFnLock":supports_fn&&a["fn_lock_via_ec"]!=true&&a["fn_lock_readback"]!=true,
                "SupportsTouchpad":!declared||a["touchpad_control"]==true})
        } else {
            self.device_identity = None;
            Value::Null
        };
        snapshot["Theme"] = self.plugins.theme();
        *self
            .client
            .snapshot
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = Arc::new(snapshot);
        (self.changed)();
    }
}

fn desktop_result(result: Result<(), String>, outcome: Outcome) -> Receipt {
    match result {
        Ok(()) => Receipt::new(outcome),
        Err(error) => Receipt::error(Outcome::Unknown, error),
    }
}
