use std::sync::Mutex;

pub const MAX_WAKE_TARGET: u32 = 4;
pub const CLOSING: u32 = 1;
pub const PENDING_REOPEN: u32 = 2;

#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct WakeSnapshot {
    pub sequence: i64,
    pub delivered: i64,
    pub target: u32,
    pub flags: u32,
}

#[derive(Default)]
struct WakeState {
    sequence: i64,
    delivered: i64,
    target: u32,
    closing: bool,
    reopen: bool,
}

pub enum WakeError {
    Invalid,
    SequenceExhausted,
}

pub struct PanelWake {
    state: Mutex<WakeState>,
}

impl PanelWake {
    pub fn new(target: u32) -> Result<Self, WakeError> {
        if target > MAX_WAKE_TARGET {
            return Err(WakeError::Invalid);
        }
        Ok(Self {
            state: Mutex::new(WakeState {
                target,
                ..Default::default()
            }),
        })
    }

    pub fn snapshot(&self) -> WakeSnapshot {
        self.state
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .snapshot()
    }

    pub fn raise(&self, target: u32) -> Result<WakeSnapshot, WakeError> {
        if target > MAX_WAKE_TARGET {
            return Err(WakeError::Invalid);
        }
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.sequence = state
            .sequence
            .checked_add(1)
            .ok_or(WakeError::SequenceExhausted)?;
        state.target = target;
        if state.closing {
            state.reopen = true;
        }
        Ok(state.snapshot())
    }

    pub fn begin_detach(&self, observed: i64) -> Result<WakeSnapshot, WakeError> {
        if observed < 0 {
            return Err(WakeError::Invalid);
        }
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.closing = true;
        if observed < state.sequence {
            state.reopen = true;
        }
        Ok(state.snapshot())
    }

    pub fn mark_delivered(&self, sequence: i64) -> Result<WakeSnapshot, WakeError> {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        if sequence < state.delivered || sequence > state.sequence {
            return Err(WakeError::Invalid);
        }
        state.delivered = sequence;
        Ok(state.snapshot())
    }

    pub fn stop(&self) -> WakeSnapshot {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.closing = true;
        state.snapshot()
    }
}

impl WakeState {
    fn snapshot(&self) -> WakeSnapshot {
        let mut flags = 0;
        if self.closing {
            flags |= CLOSING;
        }
        if self.reopen || self.sequence > self.delivered {
            flags |= PENDING_REOPEN;
        }
        WakeSnapshot {
            sequence: self.sequence,
            delivered: self.delivered,
            target: self.target,
            flags,
        }
    }
}
