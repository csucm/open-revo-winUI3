use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
use windows::Win32::Media::Audio::{
    eCapture, eConsole, IMMDevice, IMMDeviceEnumerator, MMDeviceEnumerator,
};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
    COINIT_MULTITHREADED,
};

/// Read real-time microphone active status (true = active/unmuted, false = muted)
pub fn get_microphone_state() -> Option<bool> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        let device = enumerator
            .GetDefaultAudioEndpoint(eCapture, eConsole)
            .ok()?;
        let endpoint_volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None).ok()?;
        let muted = endpoint_volume.GetMute().ok()?;
        Some(!muted.as_bool())
    }
}

/// Set microphone mute state (enable: true = unmute/active, false = mute)
pub fn set_microphone_state(enable: bool) -> Result<bool, String> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
                .map_err(|e| format!("Failed CoCreateInstance: {:?}", e))?;
        let device = enumerator
            .GetDefaultAudioEndpoint(eCapture, eConsole)
            .map_err(|e| format!("Failed GetDefaultAudioEndpoint: {:?}", e))?;
        let endpoint_volume: IAudioEndpointVolume = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| format!("Failed Activate endpoint: {:?}", e))?;
        let muted = !enable;
        endpoint_volume
            .SetMute(
                windows::Win32::Foundation::BOOL::from(muted),
                std::ptr::null(),
            )
            .map_err(|e| format!("Failed SetMute: {:?}", e))?;
        Ok(enable)
    }
}

pub enum MicrophoneToggleError {
    NoEndpoint,
    EndpointChanged,
    NotConfirmed,
}

/// Toggle one concrete default capture endpoint and confirm its resulting state.
pub fn toggle_microphone() -> Result<bool, MicrophoneToggleError> {
    use MicrophoneToggleError::{EndpointChanged, NoEndpoint, NotConfirmed};
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED)
            .ok()
            .map_err(|_| NoEndpoint)?;
        let _apartment = AudioApartment;
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).map_err(|_| NoEndpoint)?;
        let device = enumerator
            .GetDefaultAudioEndpoint(eCapture, eConsole)
            .map_err(|_| NoEndpoint)?;
        let id = endpoint_id(&device).map_err(|_| NotConfirmed)?;
        let volume: IAudioEndpointVolume = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|_| NotConfirmed)?;
        let target_muted = !volume.GetMute().map_err(|_| NotConfirmed)?.as_bool();
        let still_default = || {
            let current = enumerator
                .GetDefaultAudioEndpoint(eCapture, eConsole)
                .map_err(|_| EndpointChanged)?;
            let current_id = endpoint_id(&current).map_err(|_| NotConfirmed)?;
            if current_id == id {
                Ok(())
            } else {
                Err(EndpointChanged)
            }
        };
        still_default()?;
        volume
            .SetMute(target_muted, std::ptr::null())
            .map_err(|_| NotConfirmed)?;
        let muted = volume.GetMute().map_err(|_| NotConfirmed)?.as_bool();
        still_default()?;
        if muted != target_muted {
            return Err(NotConfirmed);
        }
        Ok(!muted)
    }
}

unsafe fn endpoint_id(device: &IMMDevice) -> windows::core::Result<Vec<u16>> {
    let id = device.GetId()?;
    let units = id.as_wide().to_vec();
    CoTaskMemFree(Some(id.0.cast()));
    Ok(units)
}

struct AudioApartment;

impl Drop for AudioApartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}
