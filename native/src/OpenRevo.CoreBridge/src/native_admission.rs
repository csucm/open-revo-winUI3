//! Admission uses the pinned Core platform/manifest, never a local DMI allowlist.
use serde_json::{json,Value};
use openrevo_core::hal::traits::HardwareDriver;
use openrevo_core::{hal,driver::wmi_ec as ec};


pub(super) fn classic()->bool {
    hal::platform_probe::is_tongfang_family() && !ec::is_legacy_dual_mode_ec()
        && hal::declarative::load_matched_manifest().map(|m|m.channels.values().all(|c|matches!(c,hal::declarative::model_manifest::ChannelDef::AcpiIoctl{..}))).unwrap_or(true)
}
pub(super) fn project()->Option<u8>{if classic(){ec::wmi_read_ec(ec::ADDR_PROJECT_ID).filter(|p|*p!=0&&*p!=255)}else{None}}
fn declared_mode()->Option<u8> {
    use hal::declarative::model_manifest::{ChannelDef,ActionDef};
    let manifest=hal::declarative::load_matched_manifest()?;
    if manifest.channels.values().any(|c|matches!(c,ChannelDef::WmiIp3)) {
        return openrevo_core::driver::wmi_ip3::ip3_get_power_mode().and_then(|raw|match raw{2=>Some(1),0=>Some(2),1=>Some(3),_=>None});
    }
    if let Some(ActionDef::BufferInvoke{input,parse,mapping,..})=manifest.actions.get("get_power_mode") {
        let bytes:Option<Vec<u8>>=input.iter().map(|v|v.as_u64().and_then(|n|u8::try_from(n).ok())).collect();
        let output=hal::declarative::invoke_raw_mailbox(&bytes?).ok()?;
        let rule=parse.as_ref()?.get("mode_code")?;
        let raw=hal::declarative::wmi_mailbox::extract_value_from_buffer(&output,rule.offset,&rule.value_type)?;
        return if let Some(map)=mapping {match map.get(&raw.to_string()).map(|v|v.as_str()){Some("office"|"silence")=>Some(1),Some("balance")=>Some(2),Some("turbo"|"rage")=>Some(3),_=>None}}else{match raw{1..=3=>Some(raw as u8),_=>None}};
    }
    None
}
pub(super) fn probe()->Value {
    let channel=hal::platform_probe::get_platform_channel();
    let manifest=hal::declarative::load_matched_manifest();
    let classic=classic();let pid=project();
    let standard=hal::driver_impl::MechrevoHALImpl::new();
    let driver:&dyn HardwareDriver=if classic{&standard}else{hal::get_hardware_driver()};
    let physical=if classic{pid.is_some()}else{driver.kind()!=hal::traits::DriverKind::Mock&&driver.read_fan_rpm().is_ok()||manifest.as_ref().map(|m|m.channels.values().any(|c|matches!(c,hal::declarative::model_manifest::ChannelDef::WmiIp3))).unwrap_or(false)&&openrevo_core::driver::wmi_ip3::ip3_get_power_mode().is_some()};
    let ready=driver.kind()!=hal::traits::DriverKind::Mock&&physical;
    let flags=if classic&&ready{Some(ec::get_hardware_support_flags())}else{None};
    let caps=manifest.as_ref().map(|m|&m.capabilities);
    let battery_contract=classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(60),Some(55),Some(v))if v&0x31==0x21)
        ||classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(80),Some(75),Some(v))if v&0x31==0x11)
        ||classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(100),Some(95),Some(v))if v&0x31==1);
    let mode=if classic {ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP).and_then(|clamp|ec::wmi_read_ec(ec::ADDR_CUSTOM_MODE).and_then(|custom|ec::wmi_read_ec(ec::ADDR_MODE_INDEX).map(|index|if custom&1!=0{4}else if clamp&0xb0==0xa0{1}else if index&0x1e==0x10{3}else{2})))}else if ready {declared_mode()}else{None};
    let fans=if ready{driver.read_fan_rpm().ok()}else{None};
    let supported=channel!=hal::platform_probe::PlatformChannelKind::Unknown||manifest.is_some();
    let reason=if ready{"Core驱动通道已连接"}else if supported{"Core已识别平台，但硬件通道未响应；请检查驱动和管理员权限"}else{"原Core未识别到可用的硬件驱动通道"};
    json!({"supported":supported,"ready":ready,"classic_ec":classic,"channel":format!("{:?}",channel),"driver":driver.name(),"manifest":manifest.as_ref().map(|m|&m.id),"project":pid,"reason":reason,
      "mode":mode,"cpu_fan":fans.map(|v|v.0),"gpu_fan":fans.map(|v|v.1),
      "fan_boost":if classic{Some(ec::is_fan_boost_active())}else{None},
      "capabilities":{"performance":ready&&mode.is_some()&&(classic||manifest.as_ref().map(|m|m.actions.contains_key("set_power_mode")).unwrap_or(false)),"battery":classic&&(flags.map(|f|f.has_battery_limit).unwrap_or(false)||battery_contract),"fan_boost":classic&&flags.map(|f|f.has_fan_boost).unwrap_or(false),"fn_lock":classic&&flags.map(|f|f.has_fn_lock).unwrap_or(false),"rgb":classic&&flags.map(|f|f.is_rgb_keyboard).unwrap_or(false),"custom_tuning":classic&&ready&&caps.map(|c|c.fan_custom_curve).unwrap_or(true)},
      "cpu_encoding":if openrevo_core::driver::uefi_nvram::get_cpu_info().1{"amd_celsius"}else{"intel_offset"},"tj_max":openrevo_core::system::cpu_monitor::get_cpu_native_tjmax()})
}

// Only registers already used by the Native session are exposed. No arbitrary
// address/value surface or size-query mutation is added.
fn address_allowed(address:u16)->bool {
    matches!(address,1115|1124|1125|1131|1132|1798|1830|1831|1840..=1846|1856..=1862|1870|1873|1893..=1899|1923..=1927|1932|1958..=1961|1963|1977|1987|1989|1990|1996|2000|2010|3840..=3935)
}
#[no_mangle]pub extern "C" fn or_native_ec_read(address:u32)->i32 {
    std::panic::catch_unwind(||{if address>u16::MAX as u32||!address_allowed(address as u16)||project().is_none(){return -5;}ec::wmi_read_ec(address as u16).map(|v|v as i32).unwrap_or(-4)}).unwrap_or(-2)
}
#[no_mangle]pub extern "C" fn or_native_ec_write(address:u32,expected:u32,target:u32)->i32 {
    std::panic::catch_unwind(||{if address>u16::MAX as u32||!address_allowed(address as u16)||expected>255||target>255||project().is_none(){return -5;}
        if ec::wmi_read_ec(address as u16)!=Some(expected as u8){return -8;}
        if expected==target{return 0;}
        if !ec::wmi_write_ec(address as u16,target as u8){return -4;}0
    }).unwrap_or(-2)
}

#[no_mangle]pub extern "C" fn or_native_declarative_mode_set(expected:u32,target:u32)->i32 {
    std::panic::catch_unwind(||{
        if !(1..=3).contains(&target)||!(1..=4).contains(&expected)||classic(){return -5;}
        let p=probe();if p["ready"]!=true||p["capabilities"]["performance"]!=true{return -5;}
        let driver=hal::get_hardware_driver();if declared_mode().map(u32::from)!=Some(expected){return -8;}
        let mode=match target{1=>hal::traits::HardwarePowerMode::Office,2=>hal::traits::HardwarePowerMode::Balance,_=>hal::traits::HardwarePowerMode::Turbo};
        if driver.set_performance_mode(mode).is_err(){return -4;}
        if declared_mode().map(u32::from)==Some(target){0}else{-3}
    }).unwrap_or(-2)
}
