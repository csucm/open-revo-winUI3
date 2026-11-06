use super::*;
use std::sync::{Mutex,OnceLock};
use std::collections::VecDeque;
use std::time::{Instant,Duration};
use openrevo_core::system::{native_metrics as metrics,switches};

struct MetricsState{cpu:metrics::CpuReader,last:Option<(Instant,bool,serde_json::Value)>}
static METRICS:OnceLock<Mutex<MetricsState>>=OnceLock::new();
pub(super) fn metrics_sample(gpu:bool)->serde_json::Value {
    let mut state=METRICS.get_or_init(||Mutex::new(MetricsState{cpu:metrics::CpuReader::default(),last:None})).lock().unwrap_or_else(|e|e.into_inner());
    if let Some((at,kind,value))=&state.last{if *kind==gpu&&at.elapsed()<Duration::from_millis(500){return value.clone();}}
    let value=json!({"cpu":state.cpu.read(),"gpu":if gpu{metrics::read_gpu()}else{metrics::GpuSample::default()},"gpu_sampled":gpu,"cpu_source":"Core CpuMonitor: Windows Energy Meter, original fallback model","gpu_source":"NVIDIA NVML driver: same units as Core nvidia-smi"});
    state.last=Some((Instant::now(),gpu,value.clone()));value
}
fn radio_read(id:u32)->Option<bool>{let _com=AudioCom::new()?;if id==1{switches::radio::get_wifi_radio_state_native()}else{switches::radio::get_native_bluetooth_radio_state()}}
fn declared_fn_ec() -> bool {
    !native_admission::classic() && openrevo_core::hal::declarative::load_matched_manifest()
        .is_some_and(|manifest| manifest.capabilities.fn_lock_via_ec)
}

const FN_LOCK_BIT: u8 = 0x10;

pub(super) fn declared_fn_readback() -> bool {
    !native_admission::classic() && openrevo_core::hal::declarative::load_matched_manifest()
        .is_some_and(|manifest| manifest.actions.contains_key("get_fn_lock"))
}

fn switch_read(id:u32)->Option<bool>{
    use openrevo_core::driver::wmi_ec as ec;
    match id {
        1|2=>radio_read(id),
        3=>switches::touchpad::read_ptp_registry_enabled(),
        4 if declared_fn_readback() => openrevo_core::hal::get_hardware_driver().read_live_fn_lock(),
        4 if native_admission::project().is_some() || declared_fn_ec() =>
            ec::wmi_read_ec(ec::ADDR_FN_LOCK).map(|value| value & FN_LOCK_BIT != 0),
        _=>None
    }
}
pub(super) fn native_switches()->serde_json::Value {
    let fn_lock = switch_read(4);
    let fn_lock_source = if declared_fn_readback() {
        if fn_lock.is_some() { "matched-manifest-wmi-readback" } else { "matched-manifest-wmi-read-unavailable" }
    } else if fn_lock.is_some() {
        "ec-bit-4"
    } else if declared_fn_ec() {
        "declared-ec-read-unavailable"
    } else {
        "unavailable"
    };
    let monochrome = openrevo_core::hal::get_hardware_driver().read_live_monochrome_backlight();
    json!({"wifi":switch_read(1),"bluetooth":switch_read(2),"touchpad":switch_read(3),"fn_lock":fn_lock,
        "fn_lock_source":fn_lock_source,"monochrome_level":monochrome})
}
#[no_mangle]
pub extern "C" fn or_native_switch_set(id:u32,expected:u32,target:u32)->i32 {
    catch_unwind(||{
        if !(1..=4).contains(&id)||expected>1||target>1{return -1;}
        if switch_read(id)!=Some(expected==1){return -8;}if expected==target{return 0;}
        use openrevo_core::driver::wmi_ec as ec;
        if id<=2 {
            let ok={let _com=match AudioCom::new(){Some(c)=>c,None=>return -4};
                if id==1{switches::radio::set_wifi_radio_state_native(target==1)}else{switches::radio::set_native_bluetooth_radio_state(target==1)}};
            if !ok{return -4;}
        } else if id==3&&!native_admission::classic() {
            // Use the matched Core HAL command, not a Tongfang EC register.
            // Only Windows PTP state is read back; mailbox hardware state is unknown.
            if native_admission::probe()["touchpad_control"]!=true{return -5;}
            if openrevo_core::hal::get_hardware_driver().set_device_switch(openrevo_core::hal::DeviceSwitchType::Touchpad,target==1).is_err(){return -4;}
            if switches::touchpad::set_native_touchpad_state(target==1).is_err(){return -3;}
        } else if id==4 && declared_fn_readback() {
            if native_admission::probe()["capabilities"]["fn_lock"] != true { return -5; }
            if openrevo_core::hal::get_hardware_driver()
                .set_device_switch(openrevo_core::hal::DeviceSwitchType::FnLock, target==1).is_err() { return -4; }
        } else if id==4 && declared_fn_ec() {
            // Match the upstream Fn Lock bit contract and preserve other firmware flags.
            let mut state_changed = false;
            let written = ec::wmi_modify_ec(ec::ADDR_FN_LOCK, |current| {
                if (current & FN_LOCK_BIT != 0) != (expected == 1) {
                    state_changed = true;
                    return current;
                }
                if target == 1 { current | FN_LOCK_BIT } else { current & !FN_LOCK_BIT }
            });
            if state_changed { return -8; }
            if written.is_none() { return -4; }
        } else {
            if native_admission::project().is_none(){return -5;}
            let(address,mask)=if id==3{(ec::ADDR_BATTERY_PROTECTION_MODE,ec::TOUCHPAD_BITMASK)}else{(ec::ADDR_FN_LOCK,0x10)};
            let before=match ec::wmi_read_ec(address){Some(v)=>v,None=>return -4};
            let after=if target==1{before|mask}else{before&!mask};
            if id==3&&switches::touchpad::set_native_touchpad_state(target==1).is_err(){return -4;}
            if ec::wmi_modify_ec_mask(address,mask,target==1)!=Some(after)||ec::wmi_read_ec(address)!=Some(after){return -3;}
        }
        for _ in 0..20 {if switch_read(id)==Some(target==1){return 0;}std::thread::sleep(Duration::from_millis(50));}
        -3
    }).unwrap_or(-2)
}

static EVENTS:OnceLock<Mutex<VecDeque<serde_json::Value>>>=OnceLock::new();
fn events()->&'static Mutex<VecDeque<serde_json::Value>>{EVENTS.get_or_init(||Mutex::new(VecDeque::new()))}
pub(super) fn peek_event()->serde_json::Value{events().lock().unwrap_or_else(|e|e.into_inner()).front().cloned().unwrap_or(serde_json::Value::Null)}
pub(super) struct NativeEventSink;
impl openrevo_core::CoreEventSink for NativeEventSink {
    fn emit(&self,event:&str,payload:serde_json::Value){
        if event!="osd-notify"{return;}
        static NEXT:std::sync::atomic::AtomicU64=std::sync::atomic::AtomicU64::new(1);
        let seq=NEXT.fetch_add(1,std::sync::atomic::Ordering::Relaxed);
        let mut queue=events().lock().unwrap_or_else(|e|e.into_inner());if queue.len()>=16{queue.pop_front();}
        queue.push_back(json!({"sequence":seq,"feature":payload["feature"],"value":payload["value"]}));
    }
}
#[no_mangle]
pub extern "C" fn or_native_osd_notify(feature:u32,value:u32)->i32 {
    let name=match feature{1=>"power_mode",2=>"fan_boost",3=>"wifi",4=>"bluetooth",5=>"touchpad",6=>"camera",7=>"mic",8=>"win_key",9=>"fn_lock",10=>"keep_awake",_=>return -1};
    if (feature==1&&!(1..=4).contains(&value))||(feature!=1&&value>1){return -1;}
    openrevo_core::osd_notify_global(name,&value.to_string());0
}
#[no_mangle]
pub extern "C" fn or_native_osd_ack(sequence:u64){let mut queue=events().lock().unwrap_or_else(|e|e.into_inner());while queue.front().and_then(|v|v["sequence"].as_u64()).map_or(false,|n|n<=sequence){queue.pop_front();}}
#[no_mangle]
pub extern "C" fn or_native_metrics_reset(){if let Some(state)=METRICS.get(){let mut state=state.lock().unwrap_or_else(|e|e.into_inner());state.cpu=metrics::CpuReader::default();state.last=None;}}

#[cfg(test)]mod tests{
    use super::*;
    #[test]fn invalid_switch_requests_never_enter_device_read_or_write(){
        for args in [(0,0,1),(5,0,1),(1,2,0),(4,0,2)]{assert_eq!(or_native_switch_set(args.0,args.1,args.2),-1);}
    }
    #[test]fn invalid_osd_payload_never_enters_the_event_sink(){assert_eq!(or_native_osd_notify(0,1),-1);assert_eq!(or_native_osd_notify(1,5),-1);assert_eq!(or_native_osd_notify(2,2),-1);}
    #[test]fn core_event_sink_preserves_sequence_until_explicit_ack(){
        super::super::or_native_abi_version();assert_eq!(or_native_osd_notify(3,1),0);
        let first=peek_event();assert_eq!(first["feature"],"wifi");assert_eq!(first["value"],"1");assert_eq!(peek_event(),first);
        or_native_osd_ack(first["sequence"].as_u64().unwrap());assert!(peek_event().is_null());
    }
}
