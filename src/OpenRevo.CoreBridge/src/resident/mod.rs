//! Standalone native application runtime. This module does not construct a CLR
//! or forward hardware ownership to a managed resident. Deployment remains gated
//! on complete protocol/function pairing; see evidence/rc-031-native-runtime.
mod hardware;
mod platform;
mod policy;
mod queue;
pub mod runtime;
mod window;
pub fn run() -> i32 {
    window::run()
}

mod display;
mod gpu;

pub use hardware::TuningProfile;
mod osd;
mod panel;
mod panel_settings;
mod panel_wire;
pub(crate) mod protocol;

mod desktop_power;

mod pdh;
mod telemetry;

mod windows_gpu;

mod panel_sensors;

mod crypto;
mod plugins;

mod tools;
mod updates;
