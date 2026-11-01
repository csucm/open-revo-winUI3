//! Explicit C ABI: queries are read-only; mutations have separate exports.
//! Loading this library does not create EcBridge,
//! initialize a hardware backend, load plugins, install drivers or start workers.
use openrevo_core::system::power::{FAN_TABLE_GAMING, FAN_TABLE_OFFICE, FAN_TABLE_TURBO};
use serde_json::json;
use std::{panic::catch_unwind, ptr};
mod native_overview;
mod native_services;
mod native_admission;
use native_overview::{metrics_sample,native_switches,NativeEventSink};

const REVISION: &str = "72e98685b2c0538fa0a58409a6bcce9e7b6336f7";

#[no_mangle]
pub extern "C" fn or_native_abi_version() -> u32 {
    static INIT:std::sync::Once=std::sync::Once::new();
    INIT.call_once(|| {
        // Use our bounded C# audit, without upstream per-register console spam,
        // config loading, panic-hook installation or background initialization.
        openrevo_core::system::logger::set_log_level(openrevo_core::system::logger::LOG_LEVEL_OFF);
        openrevo_core::system::logger::set_log_filter(Some("OPENREVO_NATIVE"));
        openrevo_core::set_global_event_sink(std::sync::Arc::new(NativeEventSink));
    });
    1
}

/// Returns UTF-8 byte count including NUL, -1 for invalid query/mode, -2 for
/// an upstream panic. A null/short buffer is untouched and returns required size.
/// Caller owns its buffer; Rust allocations never cross the ABI.
/// Query 1: provenance; 2: factory fan table (mode 1..3); 3: OS power status.
/// Caller must provide a valid writable allocation of at least capacity bytes.
#[no_mangle]
pub unsafe extern "C" fn or_native_query(
    query: u32, mode: u32, output: *mut u8, capacity: usize,
) -> i32 {
    catch_unwind(|| {
        let value = match query {
            1 => json!({"abi":1,"upstream_version":"0.9.5","revision":REVISION,
                "hardware_writes":false,"background_workers":false}),
            2 => match mode {
                1 => json!(FAN_TABLE_OFFICE),
                2 => json!(FAN_TABLE_GAMING),
                3 => json!(FAN_TABLE_TURBO),
                _ => return -1,
            },
            3 => {
                let (percent, ac, charging) = openrevo_core::system::power::get_live_power_status();
                json!({"battery_percent":percent,"on_ac":ac,"charging":charging,
                    "source":"upstream GetSystemPowerStatus; upstream defaults on OS failure"})
            },
            4 => match openrevo_core::driver::uefi_nvram::read_active_nvram() {
                Ok(nv) => {
                    use openrevo_core::driver::uefi_nvram as fw;
                    let(cpu,amd)=fw::get_cpu_info();
                    let raw=nv.buf.get(fw::OFFSET_DISPLAY_MODE).copied().filter(|_|nv.bytes_read as usize>fw::OFFSET_DISPLAY_MODE);
                    let mode=raw.and_then(|r|decode_gpu(r,amd));
                    json!({"hardware":{"hardware_read":mode.is_some(),"current_mode":mode.map(gpu_name).unwrap_or("unknown"),
                        "raw_value":raw,"platform":if amd{"amd"}else{"intel"},"cpu_name":cpu,"deferred_only":true},
                        "capabilities":fw::probe_gpu_capabilities()})
                },
                Err(error) => json!({"hardware":{"hardware_read":false,"current_mode":"unknown","error":error},
                    "capabilities":{"supports_igpu_only":false,"supports_hybrid":false,"supports_dgpu":false,"permission_required":true}}),
            },
            5 => json!({"four_zone":openrevo_core::driver::lighting::IteLightingManager::probe_four_zone_keyboard_exists(),
                "per_key":openrevo_core::driver::ite_streamer::IteStreamerController::probe_exists()}),
            6 => json!(openrevo_core::system::model_engine::get_builtin_profiles()),
            7 => json!({"enabled":microphone_read()}),
            8 => {
                use openrevo_core::driver::wmi_ec as ec;
                let powered=if ec::probe_acpi_driver_available() {
                    ec::wmi_read_ec(ec::ADDR_CAMERA_CUTOFF).map(|v|v&ec::CAMERA_CUTOFF_BITMASK==0)
                } else {None};
                json!({"powered":powered,
                    "policy":openrevo_core::system::switches::camera::read_camera_policy_registry(),
                    "consent":openrevo_core::system::switches::camera::read_camera_consent_registry()})
            },
            9 => {
                let state=openrevo_core::driver::nvapi::get_gpu_clock_state();
                json!({"name":openrevo_core::driver::nvapi::get_gpu_full_name(),
                    "offsets":state.as_ref().map(|s|(s.core_mhz,s.memory_mhz)),"clock_state":state})
            },
            10 if mode<=1 => metrics_sample(mode==1),
            11 => native_switches(),
            12 => native_overview::peek_event(),
            13 => openrevo_core::system::native_services::power_state(),
            14 => openrevo_core::system::native_services::calibration_state(),
            15 => openrevo_core::system::native_services::lighting_state(),
            16 => {
                let mut state=json!(openrevo_core::system::display_tuning::get_current_tuning_state());
                state["runtime_enabled"]=json!(openrevo_core::system::display_tuning::native_active());
                state["gamma_applied"]=json!(openrevo_core::system::display_tuning::native_gamma_applied());
                state["engine_error"]=json!(openrevo_core::system::display_tuning::native_color_error());state
            },
            17 => json!(native_services::last_error()),
            18 => json!(openrevo_core::system::config::get_default_builtin_presets()),
            19 => json!({"level":openrevo_core::system::logger::get_log_level(),"filter":openrevo_core::system::logger::get_log_filter(),"path":openrevo_core::system::logger::get_log_path().to_string_lossy()}),
            20 => native_admission::probe(),
            21 if (1..=3).contains(&mode) => json!(openrevo_core::system::power::get_mode_fan_table(mode as u8)),
            22 => match openrevo_core::system::display_tuning::native_color_probe(){
                Ok(matrix)=>json!({"read_only":true,"matrix":matrix}),
                Err(error)=>json!({"read_only":true,"error":error})
            },
            _ => return -1,
        };
        let bytes = value.to_string().into_bytes();
        let required = bytes.len() + 1;
        if !output.is_null() && capacity >= required {
            ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
            output.add(bytes.len()).write(0);
        }
        required as i32
    }).unwrap_or(-2)
}

// Side effects are separate from the size-query API and run only once per call.
#[no_mangle]
pub extern "C" fn or_native_clock_offsets_set(expected_core:i32,expected_memory:i32,core:i32,memory:i32)->i32 {
    catch_unwind(|| {
        if !(0..=250).contains(&core)||!(0..=1000).contains(&memory){return -1;}
        use openrevo_core::driver::nvapi;
        if nvapi::get_gpu_clock_offsets()!=Some((expected_core,expected_memory)){return -8;}
        if (expected_core,expected_memory)==(core,memory){return 0;}
        if nvapi::set_gpu_clock_offsets(core,memory).is_err(){return -4;}
        if nvapi::get_gpu_clock_offsets()==Some((core,memory)){0}else{-3}
    }).unwrap_or(-2)
}

// The C# privileged helper also validates machine, BIOS, EC project and conflicts.
#[no_mangle]
pub extern "C" fn or_native_gpu_set(mode: u32) -> i32 {
    // Legacy entry point intentionally cannot call upstream's live ACPI selector.
    let _=mode; -7
}

fn gpu_name(mode:u32)->&'static str {match mode{1=>"igpu",2=>"hybrid",3=>"dgpu",_=>"unknown"}}
fn decode_gpu(raw:u8,amd:bool)->Option<u32> {
    use openrevo_core::driver::uefi_nvram as fw;
    if amd {match raw{fw::AMD_IGPU_ONLY=>Some(1),fw::AMD_MSHYBRID=>Some(2),fw::AMD_DGPU_ONLY=>Some(3),_=>None}}
    else {match raw{fw::INTEL_IGPU_ONLY=>Some(1),fw::INTEL_DYNAMIC=>Some(2),fw::INTEL_DGPU_ONLY=>Some(3),_=>None}}
}
fn stage_gpu_buffer(bytes:&mut [u8],offset:usize,expected:u8,target:u8)->Result<bool,i32> {
    let current=bytes.get_mut(offset).ok_or(-5)?;
    if *current!=expected{return Err(-8);}
    if *current==target{return Ok(false);}
    *current=target;Ok(true)
}
// Stage ONLY the next-boot firmware byte. Never call write_gpu_mode(), which
// wakes/switches live ACPI graphics, nor any service/device/reboot operation.
#[no_mangle]
pub extern "C" fn or_native_gpu_stage(expected:u32,target:u32)->i32 {
    catch_unwind(|| {
        use openrevo_core::driver::uefi_nvram as fw;
        if !(1..=3).contains(&expected)||!(1..=3).contains(&target){return -1;}
        let caps=fw::probe_gpu_capabilities();
        if !matches!(caps.platform.as_str(),"intel"|"amd"){return -5;}
        if !(match target{1=>caps.supports_igpu_only,2=>caps.supports_hybrid,3=>caps.supports_dgpu,_=>false}){return -6;}
        let mut nv=match fw::read_active_nvram(){Ok(v)=>v,Err(_)=>return -4};
        if !matches!(nv.var_name.trim_end_matches('\0'),"UniWillVariable"|"OemMagicVariable") {return -5;}
        let count=nv.bytes_read as usize;
        if count>nv.buf.len(){return -5;}
        let raw=|mode|if caps.platform=="amd"{match mode{1=>fw::AMD_IGPU_ONLY,2=>fw::AMD_MSHYBRID,_=>fw::AMD_DGPU_ONLY}}else{match mode{1=>fw::INTEL_IGPU_ONLY,2=>fw::INTEL_DYNAMIC,_=>fw::INTEL_DGPU_ONLY}};
        match stage_gpu_buffer(&mut nv.buf[..count],fw::OFFSET_DISPLAY_MODE,raw(expected),raw(target)) {
            Ok(false)=>return 1,Err(error)=>return error,Ok(true)=>{}
        }
        if fw::write_active_nvram(nv.var_name,nv.is_50_series,&nv.buf[..count]).is_err(){return -4;}
        std::thread::sleep(std::time::Duration::from_millis(800));
        match fw::read_active_nvram() {
            Ok(after) if after.var_name==nv.var_name && after.bytes_read==nv.bytes_read && after.buf[..count]==nv.buf[..count]=>0,
            _=>-3,
        }
    }).unwrap_or(-2)
}

#[cfg(test)]mod gpu_tests {
    use super::*;
    #[test]fn changes_one_byte_and_preserves_everything_else(){let before:Vec<u8>=(0..140).collect();let mut after=before.clone();assert_eq!(stage_gpu_buffer(&mut after,98,before[98],4),Ok(true));for i in 0..140{assert_eq!(after[i],if i==98{4}else{before[i]});}}
    #[test]fn stale_short_and_noop_never_mutate(){let mut data=vec![2;140];let before=data.clone();assert_eq!(stage_gpu_buffer(&mut data,98,4,1),Err(-8));assert_eq!(data,before);assert_eq!(stage_gpu_buffer(&mut data[..50],98,2,1),Err(-5));assert_eq!(stage_gpu_buffer(&mut data,98,2,2),Ok(false));assert_eq!(data,before);}
    #[test]fn platform_values_are_not_interchangeable(){assert_eq!(decode_gpu(4,false),Some(2));assert_eq!(decode_gpu(2,false),Some(3));assert_eq!(decode_gpu(2,true),Some(1));assert_eq!(decode_gpu(255,false),None);}
    #[test]fn nvapi_p0_and_domains_can_be_reordered_without_swapping_offsets(){
        use openrevo_core::driver::nvapi as nv;
        let mut words=vec![0u32;nv::PSTATES20_SIZE_V1/4];words[0]=nv::PSTATES20_VER1;words[2]=2;words[3]=2;
        words[5]=8;words[119]=0;
        for (start,domain,value,max) in [(121,4,200000,1000000),(132,0,100000,250000)] {
            words[start]=domain;words[start+2]=1;words[start+3]=value;words[start+4]=(-500000i32)as u32;words[start+5]=max;
        }
        let state=nv::parse_native_clock_state(&words).unwrap();
        assert_eq!((state.core_mhz,state.memory_mhz),(100,200));assert_eq!((state.core_max_mhz,state.memory_max_mhz),(250,1000));
        words[132]=4;assert!(nv::parse_native_clock_state(&words).is_none());
        words[3]=9;assert!(nv::parse_native_clock_state(&words).is_none());
        assert!(nv::parse_native_clock_state(&words[..50]).is_none());
    }
}

#[link(name="ole32")]
extern "system" {
    fn CoInitializeEx(reserved: *mut std::ffi::c_void, flags: u32) -> i32;
    fn CoUninitialize();
}
// The pinned upstream audio functions initialize COM once but do not uninitialize.
// Establish MTA first, then balance both successful initializations on every exit.
struct AudioCom;
impl AudioCom {
    fn new() -> Option<Self> {
        if unsafe { CoInitializeEx(ptr::null_mut(),0) } < 0 { None } else { Some(Self) }
    }
}
impl Drop for AudioCom {
    fn drop(&mut self) { unsafe {CoUninitialize();CoUninitialize();} }
}
fn microphone_read() -> Option<bool> {
    let _com=AudioCom::new()?;
    openrevo_core::system::switches::audio::get_microphone_state()
}
fn microphone_write(enabled: bool) -> Result<(),()> {
    {
        let _com=AudioCom::new().ok_or(())?;
        openrevo_core::system::switches::audio::set_microphone_state(enabled).map_err(|_|())?;
    }
    if microphone_read()==Some(enabled) {Ok(())} else {Err(())}
}
#[no_mangle]
pub extern "C" fn or_native_microphone_set(enabled: u32) -> i32 {
    catch_unwind(|| {
        if enabled>1{return -1;}
        match microphone_write(enabled==1) {Ok(())=>0,Err(())=>-4}
    }).unwrap_or(-2)
}

// Only the camera power bit is exposed. No arbitrary EC address, global camera
// policy, bulk PnP class switch, driver initialization or worker startup.
#[no_mangle]
pub extern "C" fn or_native_camera_power_set(enabled: u32) -> i32 {
    catch_unwind(|| {
        if enabled>1{return -1;}
        use openrevo_core::driver::wmi_ec as ec;
        if !ec::probe_acpi_driver_available() {return -4;}
        if native_admission::project().is_none() {return -5;}
        let before=match ec::wmi_read_ec(ec::ADDR_CAMERA_CUTOFF){Some(v)=>v,None=>return -4};
        let target=if enabled==1 {before&!ec::CAMERA_CUTOFF_BITMASK}else{before|ec::CAMERA_CUTOFF_BITMASK};
        let requested=ec::wmi_modify_ec_mask(ec::ADDR_CAMERA_CUTOFF,ec::CAMERA_CUTOFF_BITMASK,enabled==0);
        if requested!=Some(target) || ec::wmi_read_ec(ec::ADDR_CAMERA_CUTOFF)!=Some(target){return -3;}
        0
    }).unwrap_or(-2)
}

#[link(name="shell32")]
extern "system" { fn IsUserAnAdmin() -> i32; }
// One-time recovery of Windows camera access, only in the elevated helper after
// explicit camera-enable. Routine ON/OFF never changes privacy registry values.
#[no_mangle]
pub extern "C" fn or_native_camera_access_allow() -> i32 {
    catch_unwind(|| {
        if unsafe {IsUserAnAdmin()}==0{return -6;}
        use openrevo_core::system::switches::camera as camera;
        camera::write_camera_policy_registry(true);
        camera::write_camera_consent_registry(true);
        if camera::read_camera_policy_registry()!=Some(true) || camera::read_camera_consent_registry()!=Some(true){return -3;}
        0
    }).unwrap_or(-2)
}
