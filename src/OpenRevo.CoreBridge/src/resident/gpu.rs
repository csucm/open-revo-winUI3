//! Bounded NVML capability probe, matching the managed reference. No global
//! GpuBridge, background polling or nvidia-smi child process is constructed.
use openrevo_core::driver::nvapi;
use serde::{Deserialize, Serialize};
use std::ffi::{c_char, c_void, CStr};
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub struct Offsets {
    pub core_mhz: i32,
    pub memory_mhz: i32,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Capabilities {
    pub name: String,
    pub supported: bool,
    pub configurable_base: bool,
    pub floor_watts: i32,
    pub base_max_watts: i32,
    pub boost_max_watts: i32,
    pub core_offset_max: i32,
    pub memory_offset_max: i32,
}
impl Capabilities {
    pub fn unknown(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            supported: false,
            configurable_base: false,
            floor_watts: 0,
            base_max_watts: 0,
            boost_max_watts: 0,
            core_offset_max: 0,
            memory_offset_max: 0,
        }
    }
    pub fn from_limits(name: String, default: i32, max: i32) -> Self {
        let unknown = || Self::unknown(name.clone());
        if !["RTX 30", "RTX 40", "RTX 50"]
            .iter()
            .any(|s| name.contains(s))
            || default <= 0
            || max < default
            || max > 255
        {
            return unknown();
        }
        let delta = max - default;
        let boost = if (1..=25).contains(&delta) {
            delta
        } else if max <= 115 {
            15
        } else {
            25
        };
        let base = max - boost;
        if boost < 5 || base <= 0 || default > base && delta > 25 {
            return unknown();
        }
        Self {
            name,
            supported: true,
            configurable_base: delta > 25,
            floor_watts: if delta > 25 { default } else { base },
            base_max_watts: base,
            boost_max_watts: boost,
            core_offset_max: 0,
            memory_offset_max: 0,
        }
    }
    pub(super) fn probe() -> Self {
        unsafe {
            let Ok(library) = super::platform::system_library("nvml.dll") else {
                return Self::unknown("能力待读取");
            };
            type NoArgs = unsafe extern "C" fn() -> i32;
            type Count = unsafe extern "C" fn(*mut u32) -> i32;
            type Device = unsafe extern "C" fn(u32, *mut *mut c_void) -> i32;
            type Name = unsafe extern "C" fn(*mut c_void, *mut c_char, u32) -> i32;
            type Limit = unsafe extern "C" fn(*mut c_void, *mut u32) -> i32;
            type Constraints = unsafe extern "C" fn(*mut c_void, *mut u32, *mut u32) -> i32;
            let probe = (|| -> Option<Self> {
                // Resolve the cleanup export before initialization so every successful
                // initialization has a matching shutdown before unloading the module.
                let shutdown = *library.get::<NoArgs>(b"nvmlShutdown\0").ok()?;
                let initialize = *library.get::<NoArgs>(b"nvmlInit_v2\0").ok()?;
                if initialize() != 0 {
                    return None;
                }
                struct Session(NoArgs);
                impl Drop for Session {
                    fn drop(&mut self) {
                        unsafe {
                            (self.0)();
                        }
                    }
                }
                let _session = Session(shutdown);
                let mut count = 0;
                if library.get::<Count>(b"nvmlDeviceGetCount_v2\0").ok()?(&mut count) != 0
                    || count != 1
                {
                    return None;
                }
                let mut device = std::ptr::null_mut();
                if library
                    .get::<Device>(b"nvmlDeviceGetHandleByIndex_v2\0")
                    .ok()?(0, &mut device)
                    != 0
                    || device.is_null()
                {
                    return None;
                }
                let mut bytes = [0u8; 128];
                if library.get::<Name>(b"nvmlDeviceGetName\0").ok()?(
                    device,
                    bytes.as_mut_ptr().cast(),
                    128,
                ) != 0
                {
                    return None;
                }
                let name = CStr::from_bytes_until_nul(&bytes)
                    .ok()?
                    .to_str()
                    .ok()?
                    .to_owned();
                let (mut default, mut minimum, mut maximum) = (0, 0, 0);
                if library
                    .get::<Limit>(b"nvmlDeviceGetPowerManagementDefaultLimit\0")
                    .ok()?(device, &mut default)
                    != 0
                    || library
                        .get::<Constraints>(b"nvmlDeviceGetPowerManagementLimitConstraints\0")
                        .ok()?(device, &mut minimum, &mut maximum)
                        != 0
                    || default % 1000 != 0
                    || maximum % 1000 != 0
                {
                    return None;
                }
                Some(Self::from_limits(
                    name,
                    i32::try_from(default / 1000).ok()?,
                    i32::try_from(maximum / 1000).ok()?,
                ))
            })();
            probe.unwrap_or_else(|| Self::unknown("能力待读取"))
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GpuState {
    pub capabilities: Capabilities,
    pub offsets: Option<Offsets>,
}
impl GpuState {
    pub fn integrated() -> Self {
        Self {
            capabilities: Capabilities::unknown("集成显卡"),
            offsets: None,
        }
    }
    pub fn probe() -> Self {
        let mut capabilities = Capabilities::probe();
        if !capabilities.supported {
            return Self {
                capabilities,
                offsets: None,
            };
        }
        if nvapi::get_gpu_full_name().as_deref() != Some(capabilities.name.as_str()) {
            return Self {
                capabilities: Capabilities::unknown(capabilities.name),
                offsets: None,
            };
        }
        let state = nvapi::get_gpu_clock_state();
        if let Some(state) = state.as_ref() {
            capabilities.core_offset_max = if state.core_editable {
                state.core_max_mhz.clamp(0, 250)
            } else {
                0
            };
            capabilities.memory_offset_max = if state.memory_editable {
                state.memory_max_mhz.clamp(0, 1000)
            } else {
                0
            };
        }
        Self {
            capabilities,
            offsets: state.map(|s| Offsets {
                core_mhz: s.core_mhz,
                memory_mhz: s.memory_mhz,
            }),
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn limits_require_supported_name_and_physical_envelope() {
        assert!(!Capabilities::from_limits("RTX 2060".into(), 80, 115).supported);
        assert!(!Capabilities::from_limits("RTX 4060".into(), 100, 90).supported);
        let caps = Capabilities::from_limits("RTX 4060".into(), 80, 140);
        assert!(caps.supported && caps.configurable_base);
        assert_eq!(
            (caps.floor_watts, caps.base_max_watts, caps.boost_max_watts),
            (80, 115, 25)
        );
    }
}
