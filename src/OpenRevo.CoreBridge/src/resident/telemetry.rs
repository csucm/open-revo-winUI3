//! System/EC readings and on-demand tray telemetry owned by the resident actor.
use super::{
    hardware::HardwareSession,
    pdh::ThermalZones,
    platform::{ac_online, unix_ms},
    windows_gpu::{TrayGpu, TrayReading},
};
use serde_json::{json, Value};
use std::{
    mem::size_of,
    time::{Duration, Instant},
};
use windows::Win32::{
    Foundation::FILETIME,
    System::{
        Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS},
        SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX},
        Threading::GetSystemTimes,
    },
};
#[derive(Default)]
struct System {
    last: Option<(u64, u64, u64)>,
}
fn ft(t: FILETIME) -> u64 {
    (u64::from(t.dwHighDateTime) << 32) | u64::from(t.dwLowDateTime)
}
impl System {
    fn read(&mut self, at: &Value) -> Value {
        let (mut idle, mut kernel, mut user) = (
            FILETIME::default(),
            FILETIME::default(),
            FILETIME::default(),
        );
        let mut cpu = None;
        if unsafe { GetSystemTimes(Some(&mut idle), Some(&mut kernel), Some(&mut user)) }.is_ok() {
            let current = (ft(idle), ft(kernel), ft(user));
            if let Some((a, b, c)) = self.last {
                if current.0 >= a && current.1 >= b && current.2 >= c {
                    let total = current.1 - b + current.2 - c;
                    if total > 0 {
                        cpu = Some(
                            (100.0 * (1.0 - (current.0 - a) as f64 / total as f64))
                                .clamp(0.0, 100.0),
                        );
                    }
                }
            }
            self.last = Some(current);
        }
        let mut memory = MEMORYSTATUSEX {
            dwLength: size_of::<MEMORYSTATUSEX>() as u32,
            ..Default::default()
        };
        let memory_ok = unsafe { GlobalMemoryStatusEx(&mut memory) }.is_ok();
        let mut power = SYSTEM_POWER_STATUS::default();
        let power_ok = unsafe { GetSystemPowerStatus(&mut power) }.is_ok();
        json!({"SampledAt":at,"CpuPercent":cpu,"UsedMemoryGiB":memory_ok.then(||(memory.ullTotalPhys-memory.ullAvailPhys) as f64/1073741824.0),"TotalMemoryGiB":memory_ok.then(||memory.ullTotalPhys as f64/1073741824.0),"BatteryPercent":(power_ok&&power.BatteryLifePercent<=100&&power.BatteryFlag&128==0).then_some(power.BatteryLifePercent),"OnAc":(power_ok&&power.ACLineStatus<=1).then_some(power.ACLineStatus==1),"Charging":(power_ok&&power.BatteryFlag!=255&&power.BatteryFlag&128==0).then_some(power.BatteryFlag&8!=0)})
    }
}
#[derive(Default)]
pub struct Telemetry {
    system: System,
    tray_gpu: TrayGpu,
    tray_thermal: ThermalZones,
    tray_cpu: Option<(Instant, i32)>,
    generation: u64,
    power_at: Option<Instant>,
    power_mode: Option<u8>,
    power: Value,

    pub latest_system: Value,
    pub summary: Value,
    pub details: Value,
    pub tray: Value,
}
impl Telemetry {
    pub fn clear_details(&mut self) {
        self.details = Value::Null;
    }
    pub fn invalidate(&mut self) {
        self.clear_details();
        self.power_at = None;
        self.power = Value::Null;
        self.clear_tray();
        self.generation = self.generation.wrapping_add(1);
    }
    pub fn stop_sampling(&mut self) {
        self.clear_details();
        self.power_at = None;
        self.power_mode = None;
        self.power = Value::Null;
    }
    pub fn idle(&mut self) {
        self.stop_sampling();
        self.latest_system = Value::Null;
        self.summary = Value::Null;
        self.clear_tray();
        self.system.last = None;
    }
    pub fn clear_tray(&mut self) {
        self.tray = Value::Null;
        self.tray_gpu = TrayGpu::default();
        self.clear_tray_thermal();
    }
    fn clear_tray_thermal(&mut self) {
        self.tray_thermal = ThermalZones::default();
        self.tray_cpu = None;
    }
    pub fn sample(
        &mut self,
        demand: u8,
        tray_enabled: bool,
        hardware: &mut HardwareSession,
        tray_hover: bool,
    ) {
        let at = json!(super::panel_wire::utc_iso(
            u64::try_from(unix_ms()).unwrap_or(0)
        ));
        let summary = self.system.read(&at);
        self.latest_system = summary.clone();
        self.summary = if demand != 0 {
            summary.clone()
        } else {
            Value::Null
        };
        let details_requested = matches!(demand, 2..=4);
        if !details_requested && !tray_enabled {
            self.clear_tray();
            self.stop_sampling();
            return;
        }
        let (mut fan_cpu, mut fan_gpu, mut battery, mut boost) = (None, None, None, None);
        let ready = hardware.admission["ready"] == true;
        let discrete = hardware.admission["discrete_gpu"].as_bool();
        let gpu_on_ac = discrete != Some(false) && ac_online() == Some(true);
        if ready && (details_requested || demand == 0 && tray_enabled && gpu_on_ac) {
            let driver = openrevo_core::hal::get_hardware_driver();
            if let Ok((cpu, gpu)) = driver.read_fan_rpm() {
                fan_cpu = Some(cpu);
                fan_gpu = Some(gpu);
            }
            if details_requested && hardware.classic() {
                use openrevo_core::driver::wmi_ec as ec;
                battery = ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP).map(|v| v & 127);
                boost = ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP).map(|v| v & 64 != 0);
            }
        }
        let active_discrete = gpu_on_ac && fan_gpu.is_some_and(|v| v > 0);
        let allow_gpu = discrete == Some(false) || active_discrete;
        let sample_gpu = active_discrete && matches!(demand, 2 | 4);
        let core_cpu = if ready {
            super::hardware::query_state(24)
                .ok()
                .and_then(|v| v.as_i64())
                .and_then(|v| i32::try_from(v).ok())
                .filter(|v| *v > 0 && *v < 125)
        } else {
            None
        };
        if !tray_enabled || core_cpu.is_some() {
            self.clear_tray_thermal();
        } else if tray_hover {
            self.tray_cpu = self.tray_thermal.read().map(|value| (Instant::now(), value));
        } else if self.tray_cpu.is_none_or(|(at, _)| at.elapsed() >= Duration::from_secs(5)) {
            self.clear_tray_thermal();
        }
        let power_due = demand == 2
            && ready
            && (self
                .power_at
                .is_none_or(|t| t.elapsed() >= Duration::from_secs(6))
                || self.power_mode != hardware.mode);
        if power_due {
            self.power_at = Some(Instant::now());
            self.power_mode = hardware.mode;
            self.power = hardware.overview_power().unwrap_or(Value::Null);
        }
        let gpu = if tray_enabled && active_discrete && demand == 0 {
            self.tray_gpu.sample(tray_hover)
        } else {
            self.tray_gpu = TrayGpu::default();
            TrayReading::default()
        };
        self.tray = if tray_enabled {
            json!({"At":at,"Cpu":core_cpu.or_else(|| self.tray_cpu.map(|(_, value)| value)),"CpuPercent":summary["CpuPercent"],
                "Gpu":gpu.temperature,"GpuPercent":gpu.usage,
                "GpuRead":gpu.temperature.is_some() || gpu.usage.is_some()})
        } else {
            Value::Null
        };
        self.details = if details_requested {
            json!({"Ec":ready.then(||json!({"CpuFan":fan_cpu,"GpuFan":fan_gpu,"BatteryLimit":battery,"FanBoost":boost,"Status":"Core硬件读取"})),
                "Temperature":{"Cpu":core_cpu,"Gpu":Value::Null,"CpuSource":if core_cpu.is_some() {"Core 硬件温度"} else {"待前台读取"}},
                "Overview":Value::Null,"Power":if demand==2{self.power.clone()}else{Value::Null},
                "Sampling":{"At":at,"Demand":demand,"Generation":self.generation,"AllowGpu":allow_gpu,
                    "SampleGpu":sample_gpu,"Discrete":discrete,"CoreCpu":core_cpu,"ProbeCaps":power_due && sample_gpu}})
        } else {
            Value::Null
        };
    }
}
