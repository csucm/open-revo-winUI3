//! On-demand updates reuse the existing verifier/downloader in the brief Tools process.
use super::{
    hardware::{Outcome, Receipt},
    tools,
};
use serde_json::{json, Value};
use std::{
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::Duration,
};
pub struct Shared {
    pub state: Mutex<Value>,
    release: Mutex<Value>,
    download: Mutex<Value>,
    pub revision: AtomicU64,
    changed: Arc<dyn Fn() + Send + Sync>,
}
impl Shared {
    pub fn new(installed: bool, changed: Arc<dyn Fn() + Send + Sync>) -> Self {
        Self {
            state: Mutex::new(
                json!({"CurrentVersion":"0.7.0","CanInstall":installed,"Phase":0,"AvailableVersion":null,"Size":null,"Percent":null,"Message":null}),
            ),
            release: Mutex::new(Value::Null),
            download: Mutex::new(Value::Null),
            revision: AtomicU64::new(0),
            changed,
        }
    }
    fn signal(&self) {
        self.revision.fetch_add(1, Ordering::AcqRel);
        (self.changed)();
    }
    fn publish(&self, v: Value) {
        *self.state.lock().unwrap_or_else(|e| e.into_inner()) = v;
        self.signal();
    }
    pub fn snapshot(&self) -> Value {
        self.state.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }
    pub fn verified(&self) -> bool {
        self.download
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .is_object()
    }
}
pub struct Updates {
    pub shared: Arc<Shared>,
    cancel: Option<Arc<AtomicBool>>,
    worker: Option<JoinHandle<()>>,
    installed: bool,
}
impl Updates {
    pub fn new(shared: Arc<Shared>, installed: bool) -> Self {
        Self {
            shared,
            cancel: None,
            worker: None,
            installed,
        }
    }
    pub fn has_worker(&self) -> bool {
        self.worker.is_some()
    }
    pub fn reap(&mut self) {
        if self.worker.as_ref().is_some_and(|w| w.is_finished()) {
            if let Some(w) = self.worker.take() {
                let _ = w.join();
            }
            self.cancel = None;
        }
    }
    pub fn cancel(&mut self) {
        if let Some(c) = &self.cancel {
            c.store(true, Ordering::Release);
        }
        self.shared
            .download
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take();
    }
    pub fn execute(&mut self, request: Value) -> Receipt {
        self.reap();
        let action = request["Action"].as_u64().unwrap_or(0);
        if action == 3 {
            self.cancel();
            let mut state = self.shared.snapshot();
            state["Phase"] = json!(6);
            self.shared.publish(state);
            return Receipt::new(Outcome::Applied);
        }
        if !self.installed {
            return Receipt::error(Outcome::Rejected, "当前没有有效安装登记");
        }
        if matches!(action, 4..=6) {
            let operation = if action == 4 {
                "update-directory"
            } else {
                "startup"
            };
            return match tools::call(
                operation,
                request,
                Some(Duration::from_secs(30)),
                None,
                |_| {},
            ) {
                Ok(v) => {
                    let mut r = Receipt::new(if action == 6 {
                        Outcome::ReadbackConfirmed
                    } else {
                        Outcome::Applied
                    });
                    r.payload = Some(json!({"Application":v["Application"]}));
                    r
                }
                Err(e) => Receipt::error(Outcome::Rejected, e),
            };
        }
        if !matches!(action, 1 | 2) {
            return Receipt::new(Outcome::Rejected);
        }
        if self.worker.is_some() || self.shared.verified() {
            return Receipt::new(Outcome::Busy);
        }
        let payload = if action == 2 {
            let release = self
                .shared
                .release
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .clone();
            if !release.is_object() {
                return Receipt::new(Outcome::Rejected);
            }
            release
        } else {
            Value::Null
        };
        let shared = self.shared.clone();
        let cancel = Arc::new(AtomicBool::new(false));
        let child_cancel = cancel.clone();
        let mut state = self.shared.snapshot();
        state["Phase"] = json!(if action == 1 { 1 } else { 4 });
        state["Percent"] = if action == 2 { json!(0) } else { Value::Null };
        state["Message"] = Value::Null;
        self.shared.publish(state);
        let spawn = thread::Builder::new()
            .name("OpenRevo.Update.Tool".into())
            .spawn(move || {
                let result = tools::call(
                    if action == 1 {
                        "update-check"
                    } else {
                        "update-download"
                    },
                    payload,
                    Some(Duration::from_secs(if action == 1 { 30 } else { 1250 })),
                    Some(&child_cancel),
                    |v| {
                        if v["Update"].is_object() && !child_cancel.load(Ordering::Acquire) {
                            shared.publish(v["Update"].clone());
                        }
                    },
                );
                if child_cancel.load(Ordering::Acquire) {
                    let mut state = shared.snapshot();
                    state["Phase"] = json!(6);
                    shared.publish(state);
                    return;
                }
                match result {
                    Ok(v) => {
                        if action == 1 {
                            *shared.release.lock().unwrap_or_else(|e| e.into_inner()) =
                                v["Release"].clone();
                        } else {
                            let mut pending =
                                shared.download.lock().unwrap_or_else(|e| e.into_inner());
                            if !child_cancel.load(Ordering::Acquire) {
                                *pending = v["Download"].clone();
                            }
                        }
                        shared.signal();
                    }
                    Err(e) => {
                        let mut state = shared.snapshot();
                        state["Phase"] = json!(7);
                        state["Message"] = json!(e);
                        shared.publish(state);
                    }
                }
            });
        match spawn {
            Ok(w) => {
                self.worker = Some(w);
                self.cancel = Some(cancel);
                Receipt::new(Outcome::CommandSent)
            }
            Err(e) => Receipt::error(Outcome::Unknown, e.to_string()),
        }
    }
    pub fn finish(&mut self, install: bool) -> Result<(), String> {
        if !install {
            self.cancel();
        }
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
        if install {
            let download = self
                .shared
                .download
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .take();
            if download.is_object() {
                tools::call(
                    "update-install",
                    download,
                    Some(Duration::from_secs(30)),
                    None,
                    |_| {},
                )?;
            }
        }
        Ok(())
    }
}
impl Drop for Updates {
    fn drop(&mut self) {
        let _ = self.finish(false);
    }
}
