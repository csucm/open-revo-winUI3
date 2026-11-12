//! One admission gate shared by every native command source. The actor is the
//! only consumer; cancellation before admission never aborts an accepted write.
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc, Mutex,
};
#[derive(Default)]
struct State {
    generation: u64,
    queued: usize,
    running: bool,
}
#[derive(Clone, Default)]
pub struct Admission(Arc<Mutex<State>>);
pub struct Ticket {
    generation: u64,
    cancelled: Arc<AtomicBool>,
}
pub struct Accepted(Admission);
#[derive(Debug, PartialEq, Eq)]
pub enum Denied {
    Closing,
    Busy,
    Cancelled,
}
impl Admission {
    pub fn reserve(&self, wait: bool) -> Result<Ticket, Denied> {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        if state.generation & 1 != 0 {
            return Err(Denied::Closing);
        }
        if state.queued >= 64 || !wait && (state.running || state.queued != 0) {
            return Err(Denied::Busy);
        }
        state.queued += 1;
        Ok(Ticket {
            generation: state.generation,
            cancelled: Arc::new(AtomicBool::new(false)),
        })
    }
    pub fn enter(&self, ticket: Ticket) -> Result<Accepted, Denied> {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        state.queued = state.queued.saturating_sub(1);
        if ticket.cancelled.load(Ordering::Acquire) {
            return Err(Denied::Cancelled);
        }
        if state.generation != ticket.generation || state.generation & 1 != 0 {
            return Err(Denied::Closing);
        }
        // Actor ownership makes this an invariant, not an additional queue lock.
        if state.running {
            return Err(Denied::Busy);
        }
        state.running = true;
        Ok(Accepted(self.clone()))
    }
    pub fn abandon(&self) {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        state.queued = state.queued.saturating_sub(1);
    }
    pub fn stop(&self) {
        self.0.lock().unwrap_or_else(|e| e.into_inner()).generation |= 1;
    }
    pub fn resume(&self) {
        let mut s = self.0.lock().unwrap_or_else(|e| e.into_inner());
        if s.generation & 1 != 0 {
            s.generation = s.generation.wrapping_add(1);
        }
    }
    pub fn accepting(&self) -> bool {
        self.0.lock().unwrap_or_else(|e| e.into_inner()).generation & 1 == 0
    }
    pub fn busy(&self) -> bool {
        let s = self.0.lock().unwrap_or_else(|e| e.into_inner());
        s.running || s.queued != 0
    }
}
impl Ticket {
    pub fn cancellation(&self) -> Arc<AtomicBool> {
        self.cancelled.clone()
    }
}
impl Drop for Accepted {
    fn drop(&mut self) {
        self.0 .0.lock().unwrap_or_else(|e| e.into_inner()).running = false;
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stopped_requests_do_not_resurrect_on_resume() {
        let gate = Admission::default();
        let old = gate.reserve(true).unwrap();
        gate.stop();
        gate.resume();
        assert!(matches!(gate.enter(old), Err(Denied::Closing)));
        let next = gate.reserve(true).unwrap();
        assert!(gate.enter(next).is_ok());
    }
    #[test]
    fn admitted_transaction_survives_stop() {
        let gate = Admission::default();
        let ticket = gate.reserve(true).unwrap();
        let active = gate.enter(ticket).unwrap();
        gate.stop();
        assert!(gate.busy());
        assert!(matches!(gate.reserve(true), Err(Denied::Closing)));
        drop(active);
        assert!(!gate.busy());
    }
    #[test]
    fn pre_admission_cancellation_does_not_hold_queue() {
        let gate = Admission::default();
        let ticket = gate.reserve(true).unwrap();
        ticket.cancellation().store(true, Ordering::Release);
        assert!(matches!(gate.enter(ticket), Err(Denied::Cancelled)));
        assert!(!gate.busy());
    }
    #[test]
    fn physical_key_never_waits_behind_hardware() {
        let gate = Admission::default();
        let ticket = gate.reserve(true).unwrap();
        assert!(matches!(gate.reserve(false), Err(Denied::Busy)));
        gate.abandon();
        assert!(gate.reserve(false).is_ok());
        drop(ticket);
    }
}
