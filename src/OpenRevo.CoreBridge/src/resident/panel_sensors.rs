//! Foreground-only read-only sensor state. Owned by one WinUI sampling thread.
use super::{gpu::Capabilities, pdh::ThermalZones, windows_gpu::Gpu};
use openrevo_core::system::native_metrics::{CpuReader, GpuFields, GpuReader};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{panic::catch_unwind, ptr, slice};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct Request {
    cpu_metrics: bool,
    cpu_thermal: bool,
    gpu_thermal: bool,
    gpu_metrics: bool,
    gpu_memory: bool,
    sample_gpu: bool,
    allow_gpu: bool,
    discrete: Option<bool>,
    core_cpu: Option<i32>,
    generation: u64,
    probe_caps: bool,
}

#[derive(Default)]
struct Sensors {
    cpu: Option<CpuReader>,
    thermal: ThermalZones,
    nvml: GpuReader,
    gpu: Gpu,
    generation: Option<u64>,
}
impl Sensors {
    fn read(&mut self, request: &Request) -> Value {
        if self.generation != Some(request.generation) {
            self.gpu.reset();
            self.nvml = GpuReader::default();
            self.generation = Some(request.generation);
        }
        if !request.cpu_metrics {
            self.cpu = None;
        }
        if !request.cpu_thermal || request.core_cpu.is_some() {
            self.thermal = ThermalZones::default();
        }
        let cpu = if request.cpu_metrics {
            let cpu = self.cpu.get_or_insert_with(CpuReader::default).read();
            json!({"Mhz":cpu.mhz,"Usage":cpu.usage,"PowerW":cpu.power_w,"PowerEstimated":cpu.power_estimated})
        } else {
            Value::Null
        };
        let temperature = request
            .core_cpu
            .or_else(|| request.cpu_thermal.then(|| self.thermal.read()).flatten());
        let graphics = request.gpu_metrics || request.gpu_memory;
        let nvml = if request.sample_gpu
            && request.discrete != Some(false)
            && (request.gpu_thermal || graphics)
        {
            Some(self.nvml.read(GpuFields {
                temperature: request.gpu_thermal,
                utilization: request.gpu_metrics,
                clocks: graphics,
                memory: request.gpu_memory,
                power: request.gpu_metrics,
                name: graphics,
            }))
        } else {
            self.nvml = GpuReader::default();
            None
        };
        let temperature_gpu = nvml.as_ref().and_then(|v| v.temperature);
        let gpu = if graphics && request.allow_gpu {
            if let Some(g) = nvml.as_ref().filter(|g| {
                g.temperature.is_some()
                    || g.core_mhz.is_some()
                    || g.memory_mhz.is_some()
                    || g.usage.is_some()
                    || g.memory_total_bytes.is_some()
                    || g.memory_used_bytes.is_some()
                    || g.power_w.is_some()
            }) {
                self.gpu.close_counters();
                Some(
                    json!({"Temperature":g.temperature,"CoreMhz":g.core_mhz,"MemoryMhz":g.memory_mhz,"Usage":g.usage,"MemoryTotalBytes":g.memory_total_bytes,"MemoryUsedBytes":g.memory_used_bytes,"PowerW":g.power_w,"AdapterName":g.adapter_name,"AdapterKind":Value::Null,"SharedLimitBytes":Value::Null,"SharedUsedBytes":Value::Null}),
                )
            } else {
                self.gpu
                    .read(request.discrete, request.gpu_metrics, request.gpu_memory)
            }
        } else {
            self.gpu.close_counters();
            None
        };
        let capabilities =
            (request.probe_caps && request.allow_gpu && request.discrete != Some(false))
                .then(Capabilities::probe);
        json!({"Sampled":true,"At":super::panel_wire::utc_iso(u64::try_from(super::platform::unix_ms()).unwrap_or(0)),"Cpu":cpu,"CpuTemperature":temperature,"GpuTemperature":temperature_gpu,"Gpu":gpu,"Capabilities":capabilities,"GpuDiagnostics":self.gpu.diagnostic})
    }
}

#[no_mangle]
pub extern "C" fn or_native_panel_sensor_open() -> *mut std::ffi::c_void {
    catch_unwind(|| Box::into_raw(Box::new(Sensors::default())).cast()).unwrap_or(ptr::null_mut())
}

// The handle and buffers are private to one foreground thread. No file,
// process, configuration or control arguments cross this ABI.
#[no_mangle]
pub unsafe extern "C" fn or_native_panel_sensor_read(
    handle: *mut std::ffi::c_void,
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    if handle.is_null()
        || input.is_null()
        || output.is_null()
        || !(1..=2048).contains(&length)
        || !(1..=65536).contains(&capacity)
    {
        return -1;
    }
    catch_unwind(|| {
        let Ok(request) = serde_json::from_slice::<Request>(slice::from_raw_parts(input, length))
        else {
            return -1;
        };
        if request.sample_gpu && !request.allow_gpu
            || request.probe_caps && !request.sample_gpu
            || request.core_cpu.is_some_and(|v| !(1..125).contains(&v))
        {
            return -1;
        }
        let value = (&mut *handle.cast::<Sensors>()).read(&request);
        let Ok(bytes) = serde_json::to_vec(&value) else {
            return -2;
        };
        if bytes.len() > capacity {
            return -2;
        }
        ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
        bytes.len() as i32
    })
    .unwrap_or(-2)
}

#[no_mangle]
pub unsafe extern "C" fn or_native_panel_sensor_close(handle: *mut std::ffi::c_void) {
    if !handle.is_null() {
        let _ = catch_unwind(|| drop(Box::from_raw(handle.cast::<Sensors>())));
    }
}
