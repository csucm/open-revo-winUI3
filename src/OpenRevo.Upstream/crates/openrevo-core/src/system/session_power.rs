use windows::core::{w, Result, PWSTR};
use windows::Win32::Foundation::{CloseHandle, E_FAIL, E_UNEXPECTED, HANDLE};
use windows::Win32::System::Power::{
    PowerClearRequest, PowerCreateRequest, PowerRequestDisplayRequired, PowerRequestSystemRequired,
    PowerSetRequest, POWER_REQUEST_TYPE,
};
use windows::Win32::System::Threading::{
    POWER_REQUEST_CONTEXT_SIMPLE_STRING, REASON_CONTEXT, REASON_CONTEXT_0,
};
use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};

/// The host serializes calls and schedules the window message after the
/// returned settling delay. This state owns the requests between messages.
#[derive(Default)]
pub struct SessionPower {
    keep_awake: Option<SessionPowerRequest>,
    screen_off: Option<SessionPowerRequest>,
    last_input: u32,
    pending: bool,
}

impl SessionPower {
    pub fn keep_awake_enabled(&self) -> bool {
        self.keep_awake
            .as_ref()
            .is_some_and(SessionPowerRequest::enabled)
    }

    pub fn toggle_keep_awake(&mut self) -> Result<bool> {
        if self.keep_awake.is_none() {
            self.keep_awake = Some(SessionPowerRequest::new()?);
        }
        let request = self.keep_awake.as_mut().unwrap();
        request.set_keep_awake(!request.enabled())?;
        Ok(request.enabled())
    }

    pub fn begin_screen_off(&mut self) -> u32 {
        if self.pending {
            return 0;
        }
        self.pending = true;
        350
    }

    pub fn prepare_screen_off(&mut self) -> Result<()> {
        if !self.pending {
            return Err(E_UNEXPECTED.into());
        }
        if let Some(request) = &mut self.keep_awake {
            request.release_display()?;
        }
        if self.screen_off.is_none() {
            self.screen_off = Some(SessionPowerRequest::new()?);
        }
        self.screen_off.as_mut().unwrap().hold_system_only()
    }

    pub fn capture_screen_off_input(&mut self) -> Result<()> {
        if !self.pending || self.screen_off.is_none() {
            return Err(E_UNEXPECTED.into());
        }
        self.last_input = read_last_input()?;
        Ok(())
    }

    pub fn finish_screen_off(&mut self) {
        self.pending = false;
    }

    pub fn cancel_screen_off(&mut self) -> Result<()> {
        self.finish_screen_off();
        self.restore_display()
    }

    pub fn restore_on_input(&mut self) -> Result<()> {
        if !self.pending && self.screen_off.is_some() && read_last_input()? != self.last_input {
            self.restore_display()?;
        }
        Ok(())
    }

    fn restore_display(&mut self) -> Result<()> {
        let released = clear_request(&mut self.screen_off);
        if let Some(request) = self.keep_awake.as_mut().filter(|request| request.enabled()) {
            request.set_keep_awake(true)?;
        }
        released
    }

    pub fn shutdown(&mut self) -> Result<()> {
        self.pending = false;
        let awake = clear_request(&mut self.keep_awake);
        clear_request(&mut self.screen_off).and(awake)
    }
}

fn clear_request(slot: &mut Option<SessionPowerRequest>) -> Result<()> {
    if let Some(mut request) = slot.take() {
        request.set_keep_awake(false)?;
    }
    Ok(())
}

fn read_last_input() -> Result<u32> {
    let mut input = LASTINPUTINFO {
        cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32,
        dwTime: 0,
    };
    if unsafe { GetLastInputInfo(&mut input) }.as_bool() {
        Ok(input.dwTime)
    } else {
        Err(E_FAIL.into())
    }
}

/// One Windows power request, with one reference per active request type.
/// The session owner serializes calls and drops it when the request ends.
struct SessionPowerRequest {
    handle: HANDLE,
    system: bool,
    display: bool,
}

impl SessionPowerRequest {
    pub fn new() -> Result<Self> {
        let reason = REASON_CONTEXT {
            Version: 0,
            Flags: POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            Reason: REASON_CONTEXT_0 {
                SimpleReasonString: PWSTR(w!("Open-Revo 防休眠").as_ptr().cast_mut()),
            },
        };
        let handle = unsafe { PowerCreateRequest(&reason)? };
        Ok(Self {
            handle,
            system: false,
            display: false,
        })
    }

    pub fn enabled(&self) -> bool {
        self.system
    }

    pub fn set_keep_awake(&mut self, enabled: bool) -> Result<()> {
        if !enabled {
            self.release_display()?;
            return self.set_system(false);
        }

        let added_system = !self.system;
        self.set_system(true)?;
        if let Err(error) = Self::change(
            self.handle,
            PowerRequestDisplayRequired,
            &mut self.display,
            true,
        ) {
            if added_system {
                self.set_system(false)?;
            }
            return Err(error);
        }
        Ok(())
    }

    pub fn hold_system_only(&mut self) -> Result<()> {
        self.set_system(true)?;
        self.release_display()
    }

    pub fn release_display(&mut self) -> Result<()> {
        Self::change(
            self.handle,
            PowerRequestDisplayRequired,
            &mut self.display,
            false,
        )
    }

    fn set_system(&mut self, enabled: bool) -> Result<()> {
        Self::change(
            self.handle,
            PowerRequestSystemRequired,
            &mut self.system,
            enabled,
        )
    }

    fn change(
        handle: HANDLE,
        kind: POWER_REQUEST_TYPE,
        held: &mut bool,
        enabled: bool,
    ) -> Result<()> {
        if *held == enabled {
            return Ok(());
        }
        unsafe {
            if enabled {
                PowerSetRequest(handle, kind)?;
            } else {
                PowerClearRequest(handle, kind)?;
            }
        }
        *held = enabled;
        Ok(())
    }
}

impl Drop for SessionPowerRequest {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.handle) };
    }
}
