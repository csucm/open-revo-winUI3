//! Admission uses the pinned Core platform/manifest, never a local DMI allowlist.
use serde_json::{json,Value};
use openrevo_core::hal::traits::HardwareDriver;
use openrevo_core::{hal,driver::wmi_ec as ec};


pub(super) fn classic()->bool {
    hal::platform_probe::is_tongfang_family() && !ec::is_legacy_dual_mode_ec()
        && hal::declarative::load_matched_manifest().map(|m|m.channels.values().all(|c|matches!(c,hal::declarative::model_manifest::ChannelDef::AcpiIoctl{..}))).unwrap_or(true)
}
pub(super) fn project()->Option<u8>{if classic(){ec::wmi_read_ec(ec::ADDR_PROJECT_ID).filter(|p|*p!=0&&*p!=255)}else{None}}
fn declared_mode() -> Option<u8> {
    hal::get_hardware_driver().read_live_performance_mode().map(|mode| mode as u8)
}
pub(super) fn probe()->Value {
    let channel=hal::platform_probe::get_platform_channel();
    let manifest=hal::declarative::load_matched_manifest();
    let classic=classic();let pid=project();
    let standard=hal::driver_impl::MechrevoHALImpl::new();
    let driver:&dyn HardwareDriver=if classic{&standard}else{hal::get_hardware_driver()};
    let live_mode = if classic { None } else { declared_mode() };
    let fans = if !classic || pid.is_some() {
        (driver.kind() != hal::traits::DriverKind::Mock).then(|| driver.read_fan_rpm().ok()).flatten()
    } else { None };
    let physical = if classic { pid.is_some() } else { fans.is_some() || live_mode.is_some() };
    let ready=driver.kind()!=hal::traits::DriverKind::Mock&&physical;
    let flags=if classic&&ready{Some(ec::get_hardware_support_flags())}else{None};
    let caps=manifest.as_ref().map(|m|&m.capabilities);
    let battery_contract=classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(60),Some(55),Some(v))if v&0x31==0x21)
        ||classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(80),Some(75),Some(v))if v&0x31==0x11)
        ||classic&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),(Some(100),Some(95),Some(v))if v&0x31==1)
        // Verified Project 16 keeps its exact preset thresholds when firmware
        // resets the selector to LongLife (observed 60/55 with mode 0x09).
        // Do not infer other projects' write support from threshold bytes alone.
        ||classic&&pid==Some(16)&&matches!((ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP),ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE)),
            (Some(60),Some(55),Some(v))|(Some(80),Some(75),Some(v))if v&0x31==1);
    let mode=if classic {ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP).and_then(|clamp|ec::wmi_read_ec(ec::ADDR_CUSTOM_MODE).and_then(|custom|ec::wmi_read_ec(ec::ADDR_MODE_INDEX).map(|index|if custom&1!=0{4}else if clamp&0xb0==0xa0{1}else if index&0x1e==0x10{3}else{2})))}else if ready {live_mode}else{None};
    let declared_battery = !classic && manifest.as_ref().is_some_and(|m|
        m.capabilities.battery_charge_limit && matches!(m.actions.get("set_battery_limit"), Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. })));
    let declared_boost = !classic && manifest.as_ref().is_some_and(|m|
        m.capabilities.fan_boost_mode.as_deref() == Some("direct_rpm_clamp")
        || m.capabilities.fan_control_mode.as_deref() == Some("direct_rpm_clamp")
        || ["set_fan_boost_on","set_fan_boost_off"].iter().all(|key|
            matches!(m.actions.get(*key), Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. }))));
    let declared_fn = !classic && manifest.as_ref().is_some_and(|m|
        ["switch_fn_lock_on","switch_fn_lock_off"].iter().all(|key|
            matches!(m.actions.get(*key), Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. }))));
    let declared_rgb = !classic && manifest.as_ref().is_some_and(|m|
        ["set_keyboard_rgb","set_keyboard_mode_fixed","set_keyboard_mode_cyclic","set_keyboard_mode_off"].iter().all(|key|
            matches!(m.actions.get(*key), Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. }))));
    let tuning = if ready && !classic { manifest.as_ref().and_then(declared_tuning) } else { None };
    let keyboard = caps.and_then(|c| c.rgb_keyboard.as_ref());
    let monochrome_control = ready && !classic && keyboard.is_some_and(|k| k.supported && k.keyboard_type == "monochrome_stepped")
        && manifest.as_ref().is_some_and(|m| matches!(m.actions.get("set_keyboard_backlight"), Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. }))
            || m.channels.values().any(|c| matches!(c, hal::declarative::model_manifest::ChannelDef::WmiIp3)));
    let supported=channel!=hal::platform_probe::PlatformChannelKind::Unknown||manifest.is_some();
    let reason=if ready{"Core驱动通道已连接"}else if supported{"Core已识别平台，但硬件通道未响应；请检查驱动和管理员权限"}else{"原Core未识别到可用的硬件驱动通道"};
    json!({"supported":supported,"ready":ready,"classic_ec":classic,"channel":format!("{:?}",channel),"driver":driver.name(),"manifest":manifest.as_ref().map(|m|&m.id),"project":pid,"reason":reason,
      "mode":mode,"cpu_fan":fans.map(|v|v.0),"gpu_fan":fans.map(|v|v.1),"discrete_gpu":caps.map(|c|c.discrete_gpu),
      "fan_boost":if classic{Some(ec::is_fan_boost_active())}else{None},
      "fn_lock_via_ec":manifest.as_ref().is_some_and(|m|m.capabilities.fn_lock_via_ec),"fn_lock_readback":super::native_overview::declared_fn_readback(),"keyboard":keyboard,"declared_tuning":tuning,"power_mode_watts":caps.and_then(|c|c.power_mode_watts.as_ref()),"declared_rgb":ready&&declared_rgb,"monochrome_control":monochrome_control,
      "touchpad_control":ready&&!classic&&manifest.as_ref().map(|m|["switch_touchpad_on","switch_touchpad_off"].iter().all(|key|matches!(m.actions.get(*key),Some(hal::declarative::model_manifest::ActionDef::BufferInvoke{..})))).unwrap_or(false),
      "capabilities":{"performance":ready&&mode.is_some()&&(classic||manifest.as_ref().map(|m|m.actions.contains_key("set_power_mode")).unwrap_or(false)),"battery":declared_battery||classic&&(flags.map(|f|f.has_battery_limit).unwrap_or(false)||battery_contract),"fan_boost":declared_boost||classic&&flags.map(|f|f.has_fan_boost).unwrap_or(false),"fn_lock":declared_fn||manifest.as_ref().is_some_and(|m|m.capabilities.fn_lock_via_ec)||classic&&flags.map(|f|f.has_fn_lock).unwrap_or(false),"rgb":classic&&flags.map(|f|f.is_rgb_keyboard).unwrap_or(false),"custom_tuning":classic&&ready&&caps.map(|c|c.fan_custom_curve).unwrap_or(true)},
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

// Command acknowledgement only: never claim a cached setting is physical readback.
#[no_mangle]pub extern "C" fn or_native_monochrome_set(level:u32)->i32 {
    std::panic::catch_unwind(||{
        if classic()||level>255{return -5;}
        let admission = probe();
        if admission["monochrome_control"] != true { return -5; }
        if level != 0 && !admission["keyboard"]["steps"].as_array()
            .is_some_and(|steps| steps.iter().any(|step| step.as_u64() == Some(u64::from(level)))) {
            return -5;
        }
        if hal::get_hardware_driver().set_monochrome_backlight(level as u8).is_ok(){0}else{-4}
    }).unwrap_or(-2)
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
        release_declared_curve();
        if declared_mode().map(u32::from)==Some(target){0}else{-3}
    }).unwrap_or(-2)
}



#[no_mangle]
pub extern "C" fn or_native_declared_control(feature:u32, target:u32)->i32 {
    std::panic::catch_unwind(|| {
        if classic() { return -5; }
        let admission=probe();
        if admission["ready"]!=true { return -5; }
        let driver=hal::get_hardware_driver();
        if feature == 3 && target <= 1 && admission["fn_lock_readback"] == true {
            let Some(before) = driver.read_live_fn_lock() else { return -4; };
            return super::native_overview::or_native_switch_set(4, u32::from(before), target);
        }
        let result=match (feature,target) {
            (1,60|80|100) if admission["capabilities"]["battery"]==true => {
                let mode=match target {
                    60=>hal::BatteryHealthMode::Station,
                    80=>hal::BatteryHealthMode::Balance,
                    _=>hal::BatteryHealthMode::LongLife,
                };
                driver.set_battery_health(mode)
            }
            (2,0|1) if admission["capabilities"]["fan_boost"]==true => driver.set_fan_boost(target==1),
            (3,0|1) if admission["capabilities"]["fn_lock"]==true =>
                driver.set_device_switch(hal::DeviceSwitchType::FnLock,target==1),
            (4,2|3) if declared_mux_support(target==3) => driver.set_gpu_mux(target==3),
            (5, value) if admission["declared_rgb"]==true && (value>>24)<=2 =>
                driver.set_keyboard_effect((value>>24) as u8,(value>>16) as u8,(value>>8) as u8,value as u8),
            _=>return -5,
        };
        if result.is_ok(){0}else{-4}
    }).unwrap_or(-2)
}


pub(super) fn declared_mux_support(discrete: bool) -> bool {
    if classic() { return false; }
    let action = if discrete { "set_gpu_mux_discrete" } else { "set_gpu_mux_hybrid" };
    hal::declarative::load_matched_manifest().is_some_and(|m|
        m.capabilities.discrete_gpu && matches!(m.actions.get(action),
            Some(hal::declarative::model_manifest::ActionDef::BufferInvoke { .. })))
}


#[no_mangle]
pub extern "C" fn or_native_win_lock(value: i32) -> i32 {
    std::panic::catch_unwind(|| {
        use openrevo_core::system::win_key_hook;
        match value {
            -1 => i32::from(win_key_hook::is_locked()),
            0 | 1 => match win_key_hook::set_locked(value == 1) {
                Ok(()) => i32::from(win_key_hook::is_locked()),
                Err(_) => -4,
            },
            _ => -5,
        }
    }).unwrap_or(-2)
}

fn declared_tuning(manifest: &hal::declarative::model_manifest::ModelManifest) -> Option<Value> {
    use hal::declarative::model_manifest::ActionDef;
    if !manifest.actions.contains_key("set_power_mode") ||
        !["set_cpu_spl","set_cpu_sppt","set_cpu_temp_wall"].iter().all(|key|
            matches!(manifest.actions.get(*key), Some(ActionDef::BufferInvoke { .. }))) {
        return None;
    }
    let guardrails = manifest.guardrails.as_ref()?;
    Some(json!({"pl1":guardrails.pl1_watts_limit?, "pl2":guardrails.pl2_watts_limit?, "fan_curve": manifest.capabilities.fan_custom_curve &&
        manifest.capabilities.fan_control_mode.as_deref() == Some("direct_rpm_clamp")}))
}

static DECLARED_CURVE: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub(super) fn release_declared_curve() {
    if DECLARED_CURVE.swap(false, std::sync::atomic::Ordering::SeqCst) {
        openrevo_core::driver::bld_driver_mgr::restore_bld_fan_auto();
    }
}

pub(super) fn apply_declared_cpu_profile(request: Value) -> Result<(), String> {
    use openrevo_core::system::config::FanCurvePoint;
    if classic() || probe()["ready"] != true { return Err("Core driver is not ready".into()); }
    let manifest = hal::declarative::load_matched_manifest().ok_or("No matching Core manifest")?;
    let caps = declared_tuning(&manifest).ok_or("No declared CPU profile")?;
    let value = |key: &str| request[key].as_u64().and_then(|n| u32::try_from(n).ok()).ok_or("Invalid CPU profile parameter");
    let temperature = value("temperature")?;
    let curve = |key: &str| -> Result<Vec<FanCurvePoint>, String> {
        let points: Vec<FanCurvePoint> = serde_json::from_value(request[key].clone()).map_err(|e| e.to_string())?;
        if points.len() != 8 || points.iter().any(|p| !(30..=100).contains(&p.temp) || p.duty > 100)
            || points.windows(2).any(|p| p[0].temp >= p[1].temp || p[0].duty > p[1].duty)
            || points.last().is_none_or(|p| p.temp > 95 || p.duty != 100) {
            return Err("Invalid fan curve".into());
        }
        Ok(points)
    };
    let curves = if request["custom_fans"] == true {
        if caps["fan_curve"] != true { return Err("Fan curve is not declared".into()); }
        Some((curve("cpu_curve")?, curve("gpu_curve")?))
    } else { None };
    let driver = hal::declarative::driver::DeclarativeHardwareDriver::new(manifest);
    driver.set_cpu_profile(value("pl1")?, value("pl2")?, (temperature != 0).then_some(temperature))
        .map_err(|e| format!("CPU profile command incomplete, some parameters may have been sent: {e:?}"))?;
    if let Some((cpu, gpu)) = curves {
        openrevo_core::driver::bld_driver_mgr::set_bld_fan_curve(cpu, gpu, false, 100);
        DECLARED_CURVE.store(true, std::sync::atomic::Ordering::SeqCst);
    } else {
        release_declared_curve();
    }
    Ok(())
}

pub(super) fn apply_classic_transaction(request: Value) -> Result<(), String> {
    let read = |address| ec::wmi_read_ec(address).ok_or_else(|| format!("EC read failed at {address:#06x}"));
    let project_id = request["project"].as_u64().and_then(|n| u8::try_from(n).ok())
        .filter(|n| *n != 0 && *n != 255).ok_or("Invalid EC project")?;
    if project() != Some(project_id) { return Err("EC project changed".into()); }

    let number = |key: &str| request[key].as_u64().and_then(|n| u8::try_from(n).ok())
        .ok_or_else(|| format!("Invalid transaction parameter: {key}"));
    if request["kind"] == "fan_controls" { return write_classic_fan_controls(&request); }
    if request["kind"] == "fan_tables" { return write_classic_fan_tables(&request); }
    let mut writes: Vec<(u16, u8, u8, u8)> = Vec::new();
    match request["kind"].as_str() {
        Some("battery") => {
            let (upper, lower, mode) = (number("upper")?, number("lower")?, number("mode")?);
            let (old_upper, old_lower, old_mode) = (number("oldUpper")?, number("oldLower")?, number("oldMode")?);
            if !(40..=100).contains(&upper) || lower > upper || (mode ^ old_mode) & !49 != 0 {
                return Err("Invalid battery preset".into());
            }
            let first = (ec::ADDR_BATTERY_LIMIT_UP, old_upper, upper, 0);
            let second = (ec::ADDR_BATTERY_LIMIT_DOWN, old_lower, lower, 0);
            writes.extend(if upper < old_lower { [second, first] } else { [first, second] });
            writes.push((ec::ADDR_BATTERY_PROTECTION_MODE, old_mode, mode, 0));
        }
        Some("boost") => {
            if !ec::get_hardware_support_flags().has_fan_boost { return Err("Fan boost is unavailable".into()); }
            let (old, target) = (number("before")?, number("after")?);
            if (old ^ target) & !64 != 0 { return Err("Invalid fan boost target".into()); }
            writes.push((ec::ADDR_FAN_BOOST_CLAMP, old, target, 0));
        }
        Some("lighting") => {
            if read(1894)? & 4 == 0 { return Err("Single-zone lighting is unavailable".into()); }
            if read(1989)? & 7 != 0 { return Err("EC fan isolation conflicts with lighting".into()); }
            for (address, field, ignored) in [(1897, "red", 0), (1898, "green", 0), (1899, "blue", 0),
                (1932, "backlight", 16), (1895, "trigger", 32)] {
                writes.push((address, number(&format!("old_{field}"))?, number(field)?, ignored));
            }
        }
        _ => return Err("Unknown classic EC transaction".into()),
    }

    for &(address, expected, _, ignore) in &writes {
        if read(address)? & !ignore != expected & !ignore {
            return Err(format!("EC state changed at {address:#06x}"));
        }
    }
    let attempts = if request["kind"] == "lighting" { 6 } else { 1 };
    for (address, expected, target, ignore) in writes {
        let mut conflict = false;
        let written = ec::wmi_modify_ec(address, |current| {
            conflict = current & !ignore != expected & !ignore;
            if conflict { current } else { target }
        });
        if conflict { return Err(format!("EC state changed at {address:#06x}")); }
        if written != Some(target) { return Err(format!("EC write failed at {address:#06x}")); }
        let mut confirmed = false;
        for attempt in 0..attempts {
            if attempt > 0 { std::thread::sleep(std::time::Duration::from_millis(20)); }
            if ec::wmi_read_ec(address).is_some_and(|value| value & !ignore == target & !ignore) {
                confirmed = true;
                break;
            }
        }
        if !confirmed { return Err(format!("EC readback failed at {address:#06x}")); }
    }
    Ok(())
}

fn write_classic_fan_tables(request: &Value) -> Result<(), String> {
    const START: u16 = 3840;
    const COUNT: usize = 96;
    let values = request["values"].as_array().filter(|values| values.len() == COUNT)
        .ok_or("Invalid EC fan table length")?;
    let targets: Vec<u8> = values.iter().map(|value| {
        value.as_u64().and_then(|n| u8::try_from(n).ok()).ok_or("Invalid EC fan table byte")
    }).collect::<Result<_,_>>()?;
    if ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP).is_none_or(|value| value & 64 == 0) {
        return Err("EC cooling clamp is not active".into());
    }
    for (offset, target) in targets.into_iter().enumerate() {
        let address = START + offset as u16;
        if ec::wmi_modify_ec(address, |_| target) != Some(target) {
            return Err(format!("EC fan table write failed at {address:#06x}"));
        }
        if ec::wmi_read_ec(address) != Some(target) {
            return Err(format!("EC fan table readback failed at {address:#06x}"));
        }
    }
    Ok(())
}

fn write_classic_fan_controls(request: &Value) -> Result<(), String> {
    let steps = request["steps"].as_array().filter(|list| !list.is_empty() && list.len() <= 24)
        .ok_or("Invalid EC control stage")?;
    let mut writes = Vec::with_capacity(steps.len());
    for step in steps {
        let address = step["address"].as_u64().and_then(|n| u16::try_from(n).ok()).ok_or("Invalid EC control address")?;
        let value = step["value"].as_u64().and_then(|n| u8::try_from(n).ok()).ok_or("Invalid EC control value")?;
        let ignore = step["ignore"].as_u64().and_then(|n| u8::try_from(n).ok()).ok_or("Invalid EC readback mask")?;
        let allowed_mask = match address {
            1115 => 1,
            1895 => 40,
            1798 | 1857 | 1859..=1862 | 1873 | 1923..=1927 | 1963 | 1989 | 1990 => 0,
            _ => return Err("Address is outside the performance control protocol".into()),
        };
        if ignore & !allowed_mask != 0 { return Err("Invalid EC control readback mask".into()); }
        writes.push((address, value, ignore));
    }
    for (address, value, ignore) in writes {
        if ec::wmi_modify_ec(address, |_| value) != Some(value) {
            return Err(format!("EC control write failed at {address:#06x}"));
        }
        if ec::wmi_read_ec(address).is_none_or(|actual| actual & !ignore != value & !ignore) {
            return Err(format!("EC control readback failed at {address:#06x}"));
        }
    }
    Ok(())
}

/// Classify a previously read classic EC snapshot without touching hardware.
#[no_mangle]
pub unsafe extern "C" fn or_native_classic_fan_protocol(
    snapshot: *const u8, length: usize, factory_target: u32,
) -> i32 {
    std::panic::catch_unwind(|| {
        if snapshot.is_null() || length != 100 || factory_target > 1 || !classic() {
            return -5;
        }
        let snapshot = unsafe { std::slice::from_raw_parts(snapshot, length) };
        let [cpu_up, cpu_down, cpu_duty, gpu_up, gpu_down, gpu_duty] =
            std::array::from_fn::<_, 6, _>(|i| &snapshot[i * 16..(i + 1) * 16]);
        let (custom, latch, ap, isolated) = (snapshot[96], snapshot[97], snapshot[98], snapshot[99]);
        let matches_factory = |mode| {
            let mut table = openrevo_core::system::power::get_mode_fan_table(mode);
            table.gpu_duty[15] = table.gpu_duty[15].max(160);
            cpu_up == table.cpu_upt && cpu_down == table.cpu_downt && cpu_duty == table.cpu_duty
                && gpu_up == table.gpu_upt && gpu_down == table.gpu_downt && gpu_duty == table.gpu_duty
        };
        let control_ready = latch == 4 && ap & 129 == 129;
        let valid = if factory_target == 1 {
            custom == 64 && control_ready && (1..=3).any(matches_factory)
        } else if custom == 64 {
            (1..=3).any(matches_factory)
        } else if custom != 65 || !control_ready || cpu_duty[0] != 0 || gpu_duty[0] != 0 {
            false
        } else if isolated & 128 == 0 {
            matches_factory(3)
        } else {
            let up: [u8; 16] = std::array::from_fn(|i| if i == 0 { 0 } else { 25 + 5 * i as u8 });
            let down = up.map(|t| if t == 0 { 0 } else if t <= 55 { t - 8 } else { t - 5 });
            cpu_up == up && gpu_up == up && cpu_down == down && gpu_down == down
                && [cpu_duty, gpu_duty].into_iter().all(|duty| duty[15] <= 200 && duty.windows(2).all(|p| p[0] <= p[1]))
        };
        i32::from(valid)
    }).unwrap_or(-2)
}
