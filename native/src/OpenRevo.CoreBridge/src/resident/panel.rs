//! Authenticated WinUI process lifetime. The native owner survives detach;
//! dropping a panel never cancels an admitted hardware transaction.
use super::{
    panel_wire as wire,
    runtime::{Client, Command},
};
use openrevo_core::system::{
    resident_panel, resident_pipe,
    resident_pipe_io::PanelPipeIo,
    resident_request::RequestRoute,
    resident_wake::{PanelWake, CLOSING, PENDING_REOPEN},
};
use serde_json::{json, Value};
use std::{
    os::windows::{
        ffi::OsStrExt,
        fs::MetadataExt,
        io::{AsHandle, AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::{HANDLE, HWND, LPARAM, WAIT_OBJECT_0, WPARAM},
        System::{
            Com::CoCreateGuid,
            RemoteDesktop::ProcessIdToSessionId,
            Threading::{
                CreateEventW, CreateWaitableTimerW, GetCurrentProcessId, SetEvent,
                SetWaitableTimer, TerminateProcess, WaitForSingleObject, INFINITE,
            },
        },
        UI::WindowsAndMessaging::{AllowSetForegroundWindow, PostMessageW, WM_CLOSE},
    },
};
pub const CLOSED: u32 = 0x8010;
pub const SHOW_MENU: u32 = 0x8011;
const GRACE: u32 = 5000;
fn handle(value: &OwnedHandle) -> HANDLE {
    HANDLE(value.as_raw_handle())
}
fn wait_exit(process: &OwnedHandle, ms: u32) -> bool {
    unsafe { WaitForSingleObject(handle(process), ms) == WAIT_OBJECT_0 }
}
fn signal(event: &OwnedHandle) {
    let _ = unsafe { SetEvent(handle(event)) };
}
fn event() -> Result<OwnedHandle, String> {
    unsafe {
        CreateEventW(None, true, false, PCWSTR::null())
            .map(|h| OwnedHandle::from_raw_handle(h.0))
            .map_err(|e| e.to_string())
    }
}
fn timer(milliseconds: u32) -> Result<OwnedHandle, String> {
    unsafe {
        let timer = OwnedHandle::from_raw_handle(
            CreateWaitableTimerW(None, true, PCWSTR::null())
                .map_err(|e| e.to_string())?
                .0,
        );
        let due = -i64::from(milliseconds) * 10_000;
        SetWaitableTimer(handle(&timer), &due, 0, None, None, false).map_err(|e| e.to_string())?;
        Ok(timer)
    }
}
fn notify(window: isize, message: u32, value: u32) {
    let _ = unsafe {
        PostMessageW(
            HWND(window as *mut _),
            message,
            WPARAM(value as usize),
            LPARAM(0),
        )
    };
}
struct Child {
    id: u32,
    process: Arc<OwnedHandle>,
    stop: Arc<OwnedHandle>,
    wake: Arc<PanelWake>,
    updates: SyncSender<()>,
    reader: JoinHandle<()>,
    watcher: JoinHandle<()>,
}
pub struct Controller {
    base: PathBuf,
    client: Client,
    draft: Arc<Mutex<Option<Value>>>,
    child: Option<Child>,
    stopping: bool,
}
impl Controller {
    pub fn new(base: PathBuf, client: Client) -> Self {
        Self {
            base,
            client,
            draft: Arc::new(Mutex::new(None)),
            child: None,
            stopping: false,
        }
    }
    pub fn open(&mut self, window: isize, target: u32) -> Result<(), String> {
        if self.stopping {
            return Err("后台正在退出".into());
        }
        if let Some(child) = &self.child {
            child.wake.raise(target).map_err(|_| "面板唤醒序号无效")?;
            let _ = child.updates.try_send(());
            return Ok(());
        }
        let folder = self.base.join("panel-native");
        super::platform::validate_directory(&folder)?;
        let executable = folder.join("Open Revo.exe");
        let metadata =
            std::fs::symlink_metadata(&executable).map_err(|_| "缺少配对的 WinUI 面板")?;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            return Err("面板路径不是普通文件".into());
        }
        let id = unsafe { CoCreateGuid() }.map_err(|e| e.to_string())?;
        let pipe_name = format!("OpenRevo.Resident.Panel.{id:?}").replace(['{', '}', '-'], "");
        let desktop = resident_panel::DesktopPanelLauncher::open()
            .map_err(|e| format!("验证当前桌面的普通权限：{e}"))?;
        let pipe = resident_pipe::create_local_server(&pipe_name, Some(desktop.token()))
            .map_err(|e| format!("建立面板连接：{e}"))?;
        let stop = Arc::new(event()?);
        let cancel = event()?;
        let wake = Arc::new(PanelWake::new(target).map_err(|_| "唤醒目标无效")?);
        let (updates, receiver) = mpsc::sync_channel(1);
        let exe: Vec<u16> = executable
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect();
        let directory: Vec<u16> = folder.as_os_str().encode_wide().chain(Some(0)).collect();
        let mut session = 0;
        unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) }
            .map_err(|e| e.to_string())?;
        let (id, process) =
            unsafe { desktop.start(PCWSTR(exe.as_ptr()), PCWSTR(directory.as_ptr()), &pipe_name) }
                .map_err(|e| format!("启动普通权限面板进程：{e}"))?;
        let process = Arc::new(process);
        let mut guard = LaunchGuard(Some(process.clone()));
        let _ = unsafe { AllowSetForegroundWindow(id) };
        let cleaned = Arc::new(AtomicBool::new(false));
        let marker = Cleanup {
            cleaned: cleaned.clone(),
            stop: stop.clone(),
        };
        let transport = Arc::new(Transport {
            pipe,
            stop: stop.clone(),
            write: Mutex::new(None),
        });
        let reader_process = process.clone();
        let reader_wake = wake.clone();
        let draft = self.draft.clone();
        let client = self.client.clone();
        let reader = thread::Builder::new()
            .name("OpenRevo.Panel.Reader".into())
            .spawn(move || {
                let _marker = marker;
                run(
                    transport,
                    cancel,
                    reader_process,
                    reader_wake,
                    client,
                    draft,
                    receiver,
                    id,
                    session,
                    window,
                );
            })
            .map_err(|e| e.to_string())?;
        let watched = process.clone();
        let watch_stop = stop.clone();
        let watcher = thread::Builder::new()
            .name("OpenRevo.Panel.Exit".into())
            .spawn(move || {
                if !wait_exit(&watched, INFINITE) {
                    return;
                }
                signal(&watch_stop);
                while !cleaned.load(Ordering::Acquire) {
                    thread::sleep(Duration::from_millis(10));
                }
                notify(window, CLOSED, id);
            })
            .map_err(|e| e.to_string())?;
        self.child = Some(Child {
            id,
            process,
            stop,
            wake,
            updates,
            reader,
            watcher,
        });
        guard.0.take();
        Ok(())
    }
    pub fn signal_snapshot(&self) {
        if let Some(child) = &self.child {
            let _ = child.updates.try_send(());
        }
    }
    pub fn closed(&mut self, window: isize, id: u32) {
        if self
            .child
            .as_ref()
            .is_none_or(|c| c.id != id || !wait_exit(&c.process, 0))
        {
            return;
        }
        let child = self.child.take().unwrap();
        let snapshot = child.wake.snapshot();
        let _ = child.reader.join();
        let _ = child.watcher.join();
        if !self.stopping && snapshot.flags & PENDING_REOPEN != 0 {
            let _ = self.open(window, snapshot.target);
        }
    }
    pub fn begin_stop(&mut self) {
        self.stopping = true;
        if let Some(child) = &self.child {
            child.wake.stop();
            signal(&child.stop);
        }
    }
    pub fn finish(&mut self) -> Result<(), String> {
        self.begin_stop();
        if let Some(child) = self.child.take() {
            if !wait_exit(&child.process, GRACE) {
                let _ = unsafe { TerminateProcess(handle(&child.process), 1) };
            }
            if !wait_exit(&child.process, GRACE) {
                return Err("未确认本后台启动的面板已经退出".into());
            }
            let _ = child.reader.join();
            let _ = child.watcher.join();
        }
        Ok(())
    }
}
struct LaunchGuard(Option<Arc<OwnedHandle>>);
impl Drop for LaunchGuard {
    fn drop(&mut self) {
        if let Some(process) = self.0.take() {
            let _ = unsafe { TerminateProcess(handle(&process), 1) };
            let _ = wait_exit(&process, GRACE);
        }
    }
}
struct Cleanup {
    cleaned: Arc<AtomicBool>,
    stop: Arc<OwnedHandle>,
}
impl Drop for Cleanup {
    fn drop(&mut self) {
        signal(&self.stop);
        self.cleaned.store(true, Ordering::Release);
    }
}
struct Transport {
    pipe: OwnedHandle,
    stop: Arc<OwnedHandle>,
    write: Mutex<Option<u64>>,
}
impl Transport {
    fn io<'a>(&'a self, cancel: &'a OwnedHandle) -> PanelPipeIo<'a> {
        PanelPipeIo::new(
            self.pipe.as_handle(),
            self.stop.as_handle(),
            cancel.as_handle(),
        )
    }
    fn send(&self, frame: &Value) -> Result<(), String> {
        let mut last_snapshot = self.write.lock().map_err(|_| "面板发送锁异常")?;
        self.write_frame(frame, &mut last_snapshot)
    }
    fn send_snapshot(&self, client: &Client) -> Result<(), String> {
        let mut last_snapshot = self.write.lock().map_err(|_| "面板发送锁异常")?;
        if last_snapshot.is_some_and(|sent| sent >= client.snapshot_revision()) {
            return Ok(());
        }
        let snapshot = wire::snapshot(client);
        if last_snapshot
            .is_some_and(|sent| snapshot["Revision"].as_u64().is_some_and(|v| sent >= v))
        {
            return Ok(());
        }
        self.write_frame(
            &json!({"Version":15,"Kind":2,"Snapshot":snapshot}),
            &mut last_snapshot,
        )
    }
    fn write_frame(&self, frame: &Value, last_snapshot: &mut Option<u64>) -> Result<(), String> {
        let bytes = serde_json::to_vec(frame).map_err(|e| e.to_string())?;
        let cancel = timer(5000)?;
        self.io(&cancel)
            .write_frame(&bytes)
            .map_err(|e| e.to_string())?;
        if let Some(revision) = frame["Snapshot"]["Revision"].as_u64() {
            *last_snapshot = Some(last_snapshot.map_or(revision, |sent| sent.max(revision)));
        }
        Ok(())
    }
}
#[allow(clippy::too_many_arguments)]
fn run(
    transport: Arc<Transport>,
    cancel: OwnedHandle,
    process: Arc<OwnedHandle>,
    wake: Arc<PanelWake>,
    client: Client,
    draft: Arc<Mutex<Option<Value>>>,
    updates: Receiver<()>,
    id: u32,
    session: u32,
    window: isize,
) {
    let mut publisher = None;
    let result = (|| -> Result<bool, String> {
        let connect_timeout = timer(15000)?;
        transport
            .io(&connect_timeout)
            .connect()
            .map_err(|e| e.to_string())?;
        if wait_exit(&process, 0)
            || !resident_pipe::is_client(transport.pipe.as_handle(), id, session)
        {
            return Err("面板进程或会话身份不匹配".into());
        }
        let initial = wake.snapshot();
        let mut initial_draft = draft
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
            .unwrap_or_else(|| json!({}));
        let remembered = initial_draft["Compact"].as_bool();
        initial_draft["Compact"] = json!(wire::wake_compact(&client, remembered));
        transport.send(&json!({"Version":15,"Kind":1,"Snapshot":wire::snapshot(&client),"Draft":initial_draft,"Wake":initial.sequence,"WakeTarget":initial.target}))?;
        wake.mark_delivered(initial.sequence)
            .map_err(|_| "初始唤醒序号无效")?;
        let outbound = transport.clone();
        let wake_out = wake.clone();
        let client_out = client.clone();
        publisher = Some(
            thread::Builder::new()
                .name("OpenRevo.Panel.Writer".into())
                .spawn(move || publish(outbound, wake_out, client_out, updates))
                .map_err(|e| e.to_string())?,
        );
        let mut previous = 0;
        loop {
            let bytes = transport
                .io(&cancel)
                .read_request_frame()
                .map_err(|e| e.to_string())?;
            let request =
                wire::Request::decode(&bytes, previous).ok_or("面板请求格式或序号无效")?;
            previous = request.admission.id;
            match request.admission.route {
                RequestRoute::Detach => {
                    let observed = request.document["ObservedWake"]
                        .as_i64()
                        .ok_or("缺少面板唤醒序号")?;
                    wake.begin_detach(observed)
                        .map_err(|_| "面板关闭序号无效")?;
                    release_panel_resources(&client);
                    let next = request.document["Draft"].clone();
                    let compact = next["Compact"].as_bool().unwrap_or(false);
                    // This is retained as editor data, never submitted as a device command.
                    *draft.lock().unwrap_or_else(|e| e.into_inner()) = Some(next);
                    if let Ok(pending) = client.submit(Command::RememberPanelShape(compact), true) {
                        let _ = pending.wait();
                    }
                    transport.send(&wire::result(previous, 7))?;
                    return Ok(true);
                }
                RequestRoute::Exit => {
                    let reply = transport.send(&wire::result(previous, 1));
                    notify(window, WM_CLOSE, 0);
                    reply?;
                    return Ok(false);
                }
                RequestRoute::Dispatch => {}
            }
            let payload = if request.upload == 0 {
                None
            } else {
                let mut bytes = vec![0; request.upload];
                let deadline = timer(10000)?;
                transport
                    .io(&deadline)
                    .read_upload(&mut bytes)
                    .map_err(|e| e.to_string())?;
                Some(bytes)
            };
            let reply = if request.admission.operation == 36
                && request.document["Application"]["Action"] == 7
            {
                let sent = unsafe {
                    PostMessageW(HWND(window as *mut _), SHOW_MENU, WPARAM(0), LPARAM(0))
                };
                wire::result(previous, if sent.is_ok() { 3 } else { 6 })
            } else {
                wire::dispatch(&client, &request, payload.as_deref())
            };
            transport.send(&reply)?;
        }
    })();
    let detached = matches!(result, Ok(true));
    if !detached {
        release_panel_resources(&client);
    }
    // Keep a successful detach acknowledgement readable until the child exits.
    if detached {
        let _ = wait_exit(&process, GRACE);
    }
    wake.stop();
    signal(&transport.stop);
    if let Some(publisher) = publisher {
        let _ = publisher.join();
    }
    drop(transport);
    if !wait_exit(&process, if detached { 0 } else { GRACE }) {
        let _ = unsafe { TerminateProcess(handle(&process), 1) };
        let _ = wait_exit(&process, GRACE);
    }
}
fn release_panel_resources(client: &Client) {
    stop_panel_demand(client);
    client.plugins.close_views();
    if let Ok(pending) = client.submit(Command::CancelPanelTools, true) {
        let _ = pending.wait();
    }
    if let Ok(pending) = client.submit(Command::ClosePluginViews, true) {
        let _ = pending.wait();
    }
}
fn stop_panel_demand(client: &Client) {
    client.stop_panel_sampling();
    if let Ok(pending) = client.submit(Command::PresentationDemand(0), true) {
        let _ = pending.wait();
    }
}

fn publish(transport: Arc<Transport>, wake: Arc<PanelWake>, client: Client, updates: Receiver<()>) {
    while !wait_exit(&transport.stop, 0) {
        if let Some(page) = client.plugins.navigation() {
            if transport
                .send(&json!({"Version":15,"Kind":5,"Destination":page}))
                .is_err()
            {
                signal(&transport.stop);
                break;
            }
        }
        match updates.recv_timeout(Duration::from_millis(100)) {
            Ok(()) => {}
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
        let state = wake.snapshot();
        if state.flags & CLOSING != 0 {
            continue;
        }
        if state.sequence != state.delivered {
            if transport
                .send(
                    &json!({"Version":15,"Kind":3,"Wake":state.sequence,"WakeTarget":state.target}),
                )
                .is_err()
            {
                signal(&transport.stop);
                break;
            }
            let _ = wake.mark_delivered(state.sequence);
        }
        if transport.send_snapshot(&client).is_err() {
            signal(&transport.stop);
            break;
        }
    }
}
