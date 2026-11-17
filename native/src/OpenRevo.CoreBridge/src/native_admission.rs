//! Admission uses the pinned Core platform/manifest, never a local DMI allowlist.
use serde_json::{json,Value};
use openrevo_core::hal::declarative::{matched_manifest, ModelManifest};
use openrevo_core::hal::declarative::model_manifest::{ActionDef, ChannelDef};
use openrevo_core::hal::traits::HardwareDriver;
use openrevo_core::{hal,driver::wmi_ec as ec};

// 0x07B9 bit 7 is a volatile reached flag, not part of the percentage.
const BATTERY_PERCENT_MASK: u8 = 0x7f;
const BATTERY_REACHED_MASK: u8 = 0x80;

pub(super) fn battery_selector_can_revert(project: u8, platform: Option<u8>) -> bool {
    project == 26 || project == 16 && platform == Some(0) && crate::resident::gm6iq0c_battery_identity()
}

pub(super) fn battery_mode_matches(selector_can_revert: bool, upper: u8, lower: u8, expected: u8, actual: u8) -> bool {
    if expected == actual { return true; }
    let preset = match (upper & BATTERY_PERCENT_MASK, lower) {
        (60, 55) => 0x21,
        (80, 75) => 0x11,
        _ => return false,
    };
    // Project 26 and the observed GM6IQ0C BIOS retain their thresholds when
    // the selector reverts. Unrelated bits must still match exactly.
    selector_can_revert && (expected ^ actual) & !0x31 == 0
        && matches!(expected & 0x31, v if v == 1 || v == preset)
        && matches!(actual & 0x31, v if v == 1 || v == preset)
}

pub(super) fn classic()->bool {
    static CLASSIC: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *CLASSIC.get_or_init(|| hal::platform_probe::is_tongfang_family()
        && !ec::is_legacy_dual_mode_ec()
        && matched_manifest().is_none_or(|m| m.channels.values().all(|channel| matches!(channel, ChannelDef::AcpiIoctl { .. }))))
}

fn strict_ec_byte(address: u16) -> Option<u8> {
    ec::wmi_batch_read_ec_checked(&[address]).ok()?.first().copied()
}

pub(super) fn project() -> Option<u8> {
    if !classic() { return None; }
    strict_ec_byte(ec::ADDR_PROJECT_ID).filter(|p| !matches!(p, 0 | 255))
}

pub(super) fn declared_mode() -> Option<u8> {
    hal::get_hardware_driver().read_live_performance_mode().map(|mode| mode as u8)
}

#[derive(Default)]
struct DeclaredCapabilities {
    performance: bool,
    battery: bool,
    fan_boost: bool,
    fn_lock: bool,
    fn_readback: bool,
    rgb: bool,
    monochrome: bool,
    touchpad: bool,
    mux_hybrid: bool,
    mux_discrete: bool,
}

fn declared_capabilities() -> &'static DeclaredCapabilities {
    static CAPABILITIES: std::sync::OnceLock<DeclaredCapabilities> = std::sync::OnceLock::new();
    CAPABILITIES.get_or_init(|| {
        let Some(m) = matched_manifest().filter(|_| !classic()) else {
            return DeclaredCapabilities::default();
        };
        let has = |action| matches!(m.actions.get(action), Some(ActionDef::BufferInvoke { .. }));
        let caps = &m.capabilities;
        DeclaredCapabilities {
            performance: m.actions.contains_key("set_power_mode"),
            battery: caps.battery_charge_limit && has("set_battery_limit"),
            fan_boost: caps.fan_boost_mode.as_deref() == Some("direct_rpm_clamp")
                || caps.fan_control_mode.as_deref() == Some("direct_rpm_clamp")
                || has("set_fan_boost_on") && has("set_fan_boost_off"),
            fn_lock: caps.fn_lock_via_ec || has("switch_fn_lock_on") && has("switch_fn_lock_off"),
            fn_readback: m.actions.contains_key("get_fn_lock"),
            rgb: ["set_keyboard_rgb", "set_keyboard_mode_fixed", "set_keyboard_mode_cyclic", "set_keyboard_mode_off"]
                .into_iter().all(has),
            monochrome: caps.rgb_keyboard.as_ref().is_some_and(|k| k.supported && k.keyboard_type == "monochrome_stepped")
                && (has("set_keyboard_backlight") || m.channels.values().any(|c| matches!(c, ChannelDef::WmiIp3))),
            touchpad: has("switch_touchpad_on") && has("switch_touchpad_off"),
            mux_hybrid: caps.discrete_gpu && has("set_gpu_mux_hybrid"),
            mux_discrete: caps.discrete_gpu && has("set_gpu_mux_discrete"),
        }
    })
}

pub(super) fn declared_touchpad_supported() -> bool {
    declared_capabilities().touchpad
}

pub(super) fn declared_fn_readback() -> bool { declared_capabilities().fn_readback }
pub(super) fn declared_fn_lock() -> bool { declared_capabilities().fn_lock }

#[derive(Default)]
struct ClassicCapabilities {
    fan_boost: bool,
    battery: bool,
    fn_lock: bool,
    rgb: bool,
}

fn classic_capabilities(manifest: Option<&ModelManifest>) -> ClassicCapabilities {
    if manifest.is_some() {
        let flags = ec::get_hardware_support_flags();
        return ClassicCapabilities { fan_boost: flags.has_fan_boost, battery: flags.has_battery_limit,
            fn_lock: flags.has_fn_lock, rgb: flags.is_rgb_keyboard };
    }
    // Support bytes are live evidence. Zero or a transport failure must not
    // become the upstream all-enabled fallback for the life of this process.
    let first = strict_ec_byte(ec::ADDR_SUPPORT_BYTE1);
    let second = strict_ec_byte(ec::ADDR_SUPPORT_BYTE2);
    ClassicCapabilities {
        fan_boost: first.is_some_and(|value| value & 0x80 != 0),
        battery: second.is_some_and(|value| value & 0x40 != 0),
        fn_lock: [first, second].into_iter().flatten().any(|value| value != 0)
            && strict_ec_byte(ec::ADDR_FN_LOCK).is_some(),
        rgb: second.is_some_and(|value| value & 0x04 != 0)
            || openrevo_core::driver::ite_lighting::IteLightingManager::probe_keyboard_device_exists(),
    }
}

pub(super) fn decode_classic_mode(custom: u8, clamp: u8, index: u8) -> u8 {
    if custom & 1 != 0 { 4 }
    else if clamp & 0xb0 == 0xa0 { 1 }
    else if index & 0x1e == 0x10 { 3 }
    else { 2 }
}
fn classic_cpu_tuning_allowed(caps: Option<&hal::declarative::model_manifest::ModelCapabilities>) -> bool {
    caps.and_then(|c|c.cpu_tuning_type.as_deref())!=Some("none")
}
pub(super) fn probe_with_mode_sample(include_oem_mode: bool) -> Value {
    probe_impl(include_oem_mode, true)
}

pub(super) fn probe_for_resident(include_oem_mode: bool) -> Value {
    probe_impl(include_oem_mode, false)
}

fn probe_impl(include_oem_mode: bool, include_legacy_details: bool) -> Value {
    let channel=hal::platform_probe::get_platform_channel();
    let manifest=matched_manifest();
    let classic=classic();let pid=project();
    let standard=hal::driver_impl::MechrevoHALImpl::new();
    let driver:&dyn HardwareDriver=if classic{&standard}else{hal::get_hardware_driver()};
    let live_mode = if classic { None } else { declared_mode() };
    // The resident reads fan telemetry on presentation demand. Its admission
    // only needs RPM if a declared driver has no live mode response.
    let sample_fans = if classic { include_legacy_details && pid.is_some() }
        else { include_legacy_details || live_mode.is_none() };
    let fans = if sample_fans {
        (driver.kind() != hal::traits::DriverKind::Mock).then(|| driver.read_fan_rpm().ok()).flatten()
    } else { None };
    let physical = if classic { pid.is_some() } else { fans.is_some() || live_mode.is_some() };
    let ready=driver.kind()!=hal::traits::DriverKind::Mock&&physical;
    let flags=if classic && ready { classic_capabilities(manifest) } else { ClassicCapabilities::default() };
    let caps=manifest.map(|m|&m.capabilities);
    let classic_cpu_tuning=classic&&ready&&classic_cpu_tuning_allowed(caps);
    let battery_contract = classic && include_legacy_details
        && match (
            ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_UP).map(|v| v & BATTERY_PERCENT_MASK),
            ec::wmi_read_ec(ec::ADDR_BATTERY_LIMIT_DOWN),
            ec::wmi_read_ec(ec::ADDR_BATTERY_PROTECTION_MODE).map(|v| v & 0x31),
        ) {
            (Some(60), Some(55), Some(0x21))
            | (Some(80), Some(75), Some(0x11))
            | (Some(100), Some(95), Some(1)) => true,
            // Projects 16 and 26 can retain their exact preset thresholds
            // after firmware resets the selector to LongLife.
            (Some(60), Some(55), Some(1)) | (Some(80), Some(75), Some(1)) => {
                matches!(pid, Some(16 | 26))
            }
            _ => false,
        };
    let classic_mode = if classic && ready {
        ec::wmi_batch_read_ec_checked(&[ec::ADDR_FAN_BOOST_CLAMP, ec::ADDR_CUSTOM_MODE, ec::ADDR_MODE_INDEX])
            .ok().and_then(|bytes| <[u8; 3]>::try_from(bytes).ok())
            .map(|[clamp, custom, index]| (clamp, custom, index))
    } else {
        None
    };
    let mode = if classic {
        classic_mode.map(|(clamp, custom, index)| decode_classic_mode(custom, clamp, index))
    } else if ready { live_mode } else { None };
    let fan_boost = if classic && ready && include_legacy_details {
        classic_mode.map(|(clamp, _, _)| clamp)
            .or_else(|| strict_ec_byte(ec::ADDR_FAN_BOOST_CLAMP))
            .map(|clamp| clamp & ec::FAN_BOOST_CLAMP_BIT != 0)
    } else { None };
    let declared = declared_capabilities();
    let tuning = if ready && !classic { manifest.and_then(declared_tuning) } else { None };
    // A missing manifest does not assert a fan table protocol. Use the live
    // classic mode byte, and retain the known OEM exception. Actual writes
    // still require the existing full tuning snapshot and staging checks.
    let custom_tuning = classic && ready && caps.map(|c| c.fan_custom_curve)
        .unwrap_or_else(|| classic_mode.is_some_and(|(_, custom, _)| {
            matches!(custom, 64 | 65)
                || custom == 0 && pid == Some(16) && crate::resident::gm7tg0m_identity()
        }));
    let keyboard = caps.and_then(|c| c.rgb_keyboard.as_ref());
    let supported=channel!=hal::platform_probe::PlatformChannelKind::Unknown||manifest.is_some();
    let reason=if ready{"Core驱动通道已连接"}else if supported{"Core已识别平台，但硬件通道未响应；请检查驱动和管理员权限"}else{"原Core未识别到可用的硬件驱动通道"};
    let mut result = json!({"supported":supported,"ready":ready,"classic_ec":classic,"channel":format!("{:?}",channel),"driver":driver.name(),"manifest":manifest.as_ref().map(|m|&m.id),"project":pid,"reason":reason,
      "mode":mode,"cpu_fan":fans.map(|v|v.0),"gpu_fan":fans.map(|v|v.1),"discrete_gpu":caps.map(|c|c.discrete_gpu),"classic_cpu_tuning":classic_cpu_tuning,
      "fan_boost":fan_boost,
      "fn_lock_via_ec":caps.is_some_and(|c| c.fn_lock_via_ec),"fn_lock_readback":declared.fn_readback,"keyboard":keyboard,"declared_tuning":tuning,"power_mode_watts":caps.and_then(|c|c.power_mode_watts.as_ref()),"declared_rgb":ready&&declared.rgb,"monochrome_control":ready&&declared.monochrome,
      "touchpad_control":ready&&declared.touchpad,
      "capabilities":{"performance":ready&&mode.is_some()&&(classic||declared.performance),"battery":declared.battery||flags.battery||battery_contract,"fan_boost":declared.fan_boost||flags.fan_boost,"fn_lock":declared.fn_lock||caps.is_some_and(|c|c.fn_lock_via_ec)||flags.fn_lock,"rgb":flags.rgb,"custom_tuning":custom_tuning},
      "cpu_encoding":if openrevo_core::driver::uefi_nvram::get_cpu_info().1{"amd_celsius"}else{"intel_offset"},"tj_max":openrevo_core::system::cpu_monitor::get_cpu_native_tjmax()});
    if let (true, Some(16), Some((clamp, 0, index))) = (include_oem_mode && ready, pid, classic_mode) {
        result["oem_mode_clamp"] = json!(clamp);
        result["oem_mode_custom"] = json!(0);
        result["oem_mode_index"] = json!(index);
        result["oem_mode_platform"] = json!(ec::wmi_read_ec(ec::ADDR_PLATFORM_REAL_ID));
        result["oem_mode_isolated"] = json!(ec::wmi_read_ec(ec::ADDR_FAN_ISOLATED));
    }
    result
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
        let Ok(level) = u8::try_from(level) else { return -5; };
        if !declared_capabilities().monochrome { return -5; }
        if level != 0 && !matched_manifest().and_then(|m| m.capabilities.rgb_keyboard.as_ref())
            .is_some_and(|keyboard| keyboard.steps.contains(&level)) {
            return -5;
        }
        let driver = hal::get_hardware_driver();
        if driver.set_monochrome_backlight(level).is_ok(){0}else{-4}
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
        if !declared_capabilities().performance { return -5; }
        let driver = hal::get_hardware_driver();
        if driver.kind() == hal::DriverKind::Mock { return -5; }
        let Some(before) = declared_mode() else { return -5; };
        if u32::from(before) != expected { return -8; }
        let mode=match target{1=>hal::traits::HardwarePowerMode::Office,2=>hal::traits::HardwarePowerMode::Balance,_=>hal::traits::HardwarePowerMode::Turbo};
        if driver.set_performance_mode(mode).is_err(){return -4;}
        release_declared_curve();
        if declared_mode().map(u32::from)==Some(target){0}else{-3}
    }).unwrap_or(-2)
}



#[no_mangle]
pub extern "C" fn or_native_declared_control(feature:u32, target:u32)->i32 {
    std::panic::catch_unwind(|| {
        if classic() || matched_manifest().is_none() { return -5; }
        let caps = declared_capabilities();
        let driver=hal::get_hardware_driver();
        if feature == 3 && target <= 1 && caps.fn_readback {
            if !caps.fn_lock { return -5; }
            return super::native_overview::set_switch(4, None, target == 1);
        }
        let result=match (feature,target) {
            (1,60|80|100) if caps.battery => {
                let mode=match target {
                    60=>hal::BatteryHealthMode::Station,
                    80=>hal::BatteryHealthMode::Balance,
                    _=>hal::BatteryHealthMode::LongLife,
                };
                driver.set_battery_health(mode)
            }
            (2,0|1) if caps.fan_boost => driver.set_fan_boost(target==1),
            (3,0|1) if caps.fn_lock =>
                driver.set_device_switch(hal::DeviceSwitchType::FnLock,target==1),
            (4,2|3) if declared_mux_support(target==3) => driver.set_gpu_mux(target==3),
            (5, value) if caps.rgb && (value>>24)<=2 =>
                driver.set_keyboard_effect((value>>24) as u8,(value>>16) as u8,(value>>8) as u8,value as u8),
            _=>return -5,
        };
        if result.is_ok(){0}else{-4}
    }).unwrap_or(-2)
}


pub(super) fn declared_mux_support(discrete: bool) -> bool {
    let caps = declared_capabilities();
    if discrete { caps.mux_discrete } else { caps.mux_hybrid }
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
            2 => win_key_hook::toggle_locked().map(i32::from).unwrap_or(-4),
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
    if classic() { return Err("Core driver is not ready".into()); }
    let manifest = matched_manifest().ok_or("No matching Core manifest")?;
    let caps = declared_tuning(manifest).ok_or("No declared CPU profile")?;
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
    hal::get_hardware_driver()
        .set_cpu_profile(value("pl1")?, value("pl2")?, (temperature != 0).then_some(temperature))
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
    let battery = request["kind"] == "battery";
    let read = |address| {
        let value = if battery {
            ec::wmi_batch_read_ec_checked(&[address]).ok().and_then(|bytes| bytes.first().copied())
        } else {
            ec::wmi_read_ec(address)
        };
        value.ok_or_else(|| format!("EC read failed at {address:#06x}"))
    };
    let project_id = request["project"].as_u64().and_then(|n| u8::try_from(n).ok())
        .filter(|n| *n != 0 && *n != 255).ok_or("Invalid EC project")?;
    if project() != Some(project_id) { return Err("EC project changed".into()); }

    let number = |key: &str| request[key].as_u64().and_then(|n| u8::try_from(n).ok())
        .ok_or_else(|| format!("Invalid transaction parameter: {key}"));
    if request["kind"] == "fan_controls" { return write_classic_fan_controls(&request); }
    if request["kind"] == "fan_tables" { return write_classic_fan_tables(&request); }
    let mut writes: Vec<(u16, u8, u8, u8)> = Vec::new();
    let mut battery_thresholds = None;
    let selector_can_revert = battery && battery_selector_can_revert(project_id,
        if project_id == 16 { read(ec::ADDR_PLATFORM_REAL_ID).ok() } else { None });
    match request["kind"].as_str() {
        Some("battery") => {
            let (upper, lower, mode) = (number("upper")?, number("lower")?, number("mode")?);
            let (old_upper, old_lower, old_mode) = (number("oldUpper")?, number("oldLower")?, number("oldMode")?);
            if !(40..=100).contains(&upper) || lower > upper || (mode ^ old_mode) & !49 != 0 {
                return Err("Invalid battery preset".into());
            }
            battery_thresholds = Some((old_upper, old_lower, upper, lower));
            let first = (ec::ADDR_BATTERY_LIMIT_UP, old_upper, upper, BATTERY_REACHED_MASK);
            let second = (ec::ADDR_BATTERY_LIMIT_DOWN, old_lower, lower, 0);
            writes.extend(if upper < old_lower { [second, first] } else { [first, second] });
            writes.push((ec::ADDR_BATTERY_PROTECTION_MODE, old_mode, mode, 0));
        }
        Some("boost") => {
            if matched_manifest().is_some() && !ec::get_hardware_support_flags().has_fan_boost
                || !strict_ec_byte(ec::ADDR_SUPPORT_BYTE1).is_some_and(|value| value & 0x80 != 0) {
                return Err("Fan boost is unavailable".into());
            }
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

    let matches = |address, actual: u8, expected: u8, ignore: u8, readback| {
        if address == ec::ADDR_BATTERY_PROTECTION_MODE {
            if let Some((old_upper, old_lower, upper, lower)) = battery_thresholds {
                let (limit, start) = if readback { (upper, lower) } else { (old_upper, old_lower) };
                return battery_mode_matches(selector_can_revert, limit, start, expected, actual);
            }
        }
        actual & !ignore == expected & !ignore
    };
    for &(address, expected, _, ignore) in &writes {
        if !matches(address, read(address)?, expected, ignore, false) {
            return Err(format!("EC state changed at {address:#06x}"));
        }
    }
    let attempts = if request["kind"] == "lighting" { 6 } else { 1 };
    for (address, expected, target, ignore) in writes {
        let mut conflict = false;
        let compare_and_update = |current| {
            conflict = !matches(address, current, expected, ignore, false);
            if conflict { current } else { target }
        };
        let written = if battery {
            ec::wmi_modify_ec_checked(address, compare_and_update)
        } else {
            ec::wmi_modify_ec(address, compare_and_update)
        };
        if conflict { return Err(format!("EC state changed at {address:#06x}")); }
        if written != Some(target) { return Err(format!("EC write failed at {address:#06x}")); }
        let mut confirmed = false;
        for attempt in 0..attempts {
            if attempt > 0 { std::thread::sleep(std::time::Duration::from_millis(20)); }
            if read(address).is_ok_and(|value| matches(address, value, target, ignore, true)) {
                confirmed = true;
                break;
            }
        }
        if !confirmed { return Err(format!("EC readback failed at {address:#06x}")); }
    }
    Ok(())
}

pub(super) fn classic_factory_table(mode: u8) -> [[u8; 16]; 6] {
    let mut table = openrevo_core::system::power::get_mode_fan_table(mode);
    table.gpu_duty[15] = table.gpu_duty[15].max(160);
    [table.cpu_upt, table.cpu_downt, table.cpu_duty,
        table.gpu_upt, table.gpu_downt, table.gpu_duty]
}

fn matches_classic_factory_table(values: &[u8], mode: u8) -> bool {
    let rows = classic_factory_table(mode);
    values.chunks(16).eq(rows.iter().map(|row| row.as_slice()))
}

const FAN_TABLE_BYTE_COUNT: usize = 96;

fn fan_table_is_staged() -> bool {
    ec::wmi_read_ec(ec::ADDR_FAN_TABLE_LATCH) == Some(0)
        && ec::wmi_read_ec(ec::ADDR_AP_OEM_BYTE).is_some_and(|value| value & 0x81 == 0x81)
}

fn write_classic_fan_tables(request: &Value) -> Result<(), String> {
    let factory = request["factory"].as_bool().ok_or("Invalid EC fan table type")?;
    let values = request["values"].as_array().filter(|values| values.len() == FAN_TABLE_BYTE_COUNT)
        .ok_or("Invalid EC fan table length")?;
    let targets: Vec<u8> = values.iter().map(|value| {
        value.as_u64().and_then(|n| u8::try_from(n).ok()).ok_or("Invalid EC fan table byte")
    }).collect::<Result<_,_>>()?;
    if factory {
        if !(1..=3).any(|mode| matches_classic_factory_table(&targets, mode)) {
            return Err("EC fan table is not a factory table for the current model".into());
        }
        if !fan_table_is_staged() {
            return Err("EC factory fan table staging is not ready".into());
        }
    } else if ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP).is_none_or(|value| value & ec::FAN_BOOST_CLAMP_BIT == 0) {
        return Err("EC cooling clamp is not active".into());
    }
    write_fan_table_bytes(&targets, !factory)
}

pub(super) fn write_staged_custom_fan_table(
    project_id: u8,
    values: &[u8],
    custom: u8,
    boost_enabled: bool,
) -> Result<(), String> {
    if project() != Some(project_id) {
        return Err("EC project changed".into());
    }
    if values.len() != FAN_TABLE_BYTE_COUNT {
        return Err("Invalid EC fan table length".into());
    }
    let staging_matches = || {
        fan_table_is_staged()
            && ec::wmi_read_ec(ec::ADDR_CUSTOM_MODE) == Some(custom)
            && ec::wmi_read_ec(ec::ADDR_FAN_BOOST_CLAMP)
                .is_some_and(|value| (value & ec::FAN_BOOST_CLAMP_BIT != 0) == boost_enabled)
    };
    if custom != 65 || !staging_matches() {
        return Err("极客风扇表暂存状态未确认".into());
    }
    write_fan_table_bytes(values, true)?;
    if !staging_matches() {
        return Err("极客风扇表写入期间暂存状态已变化".into());
    }
    Ok(())
}

fn write_fan_table_bytes(values: &[u8], verify_all: bool) -> Result<(), String> {
    const START: u16 = 3840;
    for (offset, &target) in values.iter().enumerate() {
        let address = START + offset as u16;
        let mut changed = false;
        if ec::wmi_modify_ec(address, |current| {
            changed = current != target;
            target
        }) != Some(target) {
            return Err(format!("EC fan table write failed at {address:#06x}"));
        }
        if (verify_all || changed) && ec::wmi_read_ec(address) != Some(target) {
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
    if writes.iter().any(|(address,_,_)|matches!(*address,1923..=1926))
        && !classic_cpu_tuning_allowed(matched_manifest().map(|m|&m.capabilities)) {
        return Err("CPU tuning is disabled by the matched hardware manifest".into());
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
        let matches_factory = |mode| matches_classic_factory_table(&snapshot[..96], mode);
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
