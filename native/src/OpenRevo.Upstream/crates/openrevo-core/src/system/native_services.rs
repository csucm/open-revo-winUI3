//! Explicit Native services. No initializer, restoration loop or device worker.
use serde_json::{json, Value};
use std::{path::PathBuf,sync::{Mutex,OnceLock}};
use crate::driver::{wmi_ec as ec,lighting::IteLightingManager};
static NATIVE_ROOT:OnceLock<PathBuf>=OnceLock::new();
pub fn set_data_root(path:&str)->Result<(),String>{
    let root=PathBuf::from(path);if !root.is_absolute()||root.parent().is_none()||path.len()>32700{return Err("Invalid Native data directory".into());}
    if let Some(current)=NATIVE_ROOT.get(){if current!=&root{return Err("Native data root is fixed for this session; restart required".into());}return Ok(());}
    NATIVE_ROOT.set(root).map_err(|_|"Native data root already configured".to_string())?;Ok(())
}
pub fn root_dir()->PathBuf{NATIVE_ROOT.get().cloned().unwrap_or_else(||PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join("OpenRevoNative"))}
pub fn data_dir()->PathBuf{root_dir().join("desktop-services")}
fn lighting()->&'static IteLightingManager {
    static LIGHT:OnceLock<IteLightingManager>=OnceLock::new();
    LIGHT.get_or_init(IteLightingManager::new)
}
fn lighting_saved()->crate::driver::lighting::LightingPresetState {
    std::fs::read(data_dir().join("lighting.json")).ok().and_then(|b|serde_json::from_slice(&b).ok()).unwrap_or_default()
}
pub fn power_state()->Value {
    let valid=ec::probe_acpi_driver_available()&&ec::wmi_read_ec(ec::ADDR_PROJECT_ID)==Some(16);
    json!({"trigger":if valid{ec::wmi_read_ec(ec::ADDR_TRIGGER_BYTE)}else{None},
        "recovery":if valid{ec::wmi_read_ec(ec::ADDR_AC_RECOVERY)}else{None}})
}
pub fn lighting_state()->Value {
    let mgr=lighting();let contract=mgr.active_contract();
    json!({"capability_known":ec::wmi_read_ec(ec::ADDR_PROJECT_ID)==Some(16),"ec_front":IteLightingManager::is_ec_lightbar_supported(),"front":contract.has_coastline&&mgr.has_base_lightbar_device(),
        "hinge":contract.has_air_vent&&mgr.has_hinge_device(),
        "logo":contract.has_logo&&(IteLightingManager::probe_usb_logo_supported()||IteLightingManager::is_ec_logo_supported()),
        "ec_logo":contract.has_logo&&IteLightingManager::is_ec_logo_supported()&&!IteLightingManager::probe_usb_logo_supported(),
        "saved":lighting_saved(),"source":"Core capability + last acknowledged command; hardware has no complete state readback"})
}
fn target(v:&Value)->Result<String,String> {
    let name=v["device"].as_str().ok_or("缺少物理显示器")?;
    if !crate::system::display::get_all_monitors().iter().any(|m|m.device_name==name&&m.is_internal){return Err("目标内屏已断开或未激活".into());}
    Ok(name.to_string())
}
pub fn calibration_state()->Value {
    if let Some(mon)=crate::system::display::get_all_monitors().into_iter().find(|m|m.is_internal) {
        json!(crate::system::color_calibration::get_color_calibration_state(Some(&mon.device_name)))
    }else{Value::Null}
}
pub fn shutdown()->Result<(),String>{
    let was_active=crate::system::display_tuning::native_active();
    crate::system::display_tuning::reset_display_tuning_to_default()?;
    // A later ICC selection must survive disabling the global enhancement LUT.
    if was_active{if let Some(mon)=crate::system::display::get_all_monitors().into_iter().find(|m|m.is_internal){
        let state=crate::system::color_calibration::get_color_calibration_state(Some(&mon.device_name));
        if state.enabled{crate::system::color_calibration::apply_profile_for_device(&mon.device_name,&state.active_profile_id)?;}
    }}Ok(())
}
pub fn execute(action:u32,v:Value)->Result<(),String> {
    static GATE:Mutex<()>=Mutex::new(());let _guard=GATE.lock().map_err(|_|"服务锁异常")?;
    match action {
        8=>{if v["confirmed"].as_bool()!=Some(true){return Err("Power recovery requires explicit confirmation".into());}crate::system::power::ppm::restore_native_power_plan().map(|_|())},
        9=>{let level=v["level"].as_u64().filter(|n|*n<=2).ok_or("Invalid log level")? as u8;let filter=v["filter"].as_str().filter(|s|s.len()<=100).ok_or("Invalid log filter")?;crate::system::logger::set_log_filter(if filter.is_empty(){None}else{Some(filter)});crate::system::logger::set_log_level(level);if level>0{crate::system::logger::install_panic_hook();}Ok(())},
        11=>crate::system::power::ppm::set_active_windows_power_scheme(v["scheme"].as_str().ok_or("Missing scheme")?),
        10=>set_data_root(v["path"].as_str().ok_or("Missing Native data root")?),
        1=>{
            let key=v["key"].as_str().ok_or("缺少策略")?;let on=v["enabled"].as_bool().ok_or("缺少开关")?;
            let scheme=v["scheme"].as_str().ok_or("缺少当前电源方案标识")?;
            crate::system::power::ppm::apply_sleep_guard_option_for_scheme(key,on,scheme,false)
        },
        2=>{
            if ec::wmi_read_ec(ec::ADDR_PROJECT_ID)!=Some(16){return Err("机型项目未通过校验".into());}
            let key=v["key"].as_str().ok_or("缺少供电选项")?;
            let (addr,mask)=match key {"usb"=>(ec::ADDR_TRIGGER_BYTE,ec::TRIGGER_USB_CHARGE),"recovery"=>(ec::ADDR_AC_RECOVERY,ec::AC_RECOVERY_BITMASK),_=>return Err("不支持的供电选项".into())};
            let expected=v["expected"].as_u64().filter(|n|*n<=255).ok_or("缺少原始状态")? as u8;
            let on=v["enabled"].as_bool().ok_or("缺少开关")?;
            if ec::wmi_read_ec(addr)!=Some(expected){return Err("硬件状态已变化，请重读".into());}
            let desired=if on{expected|mask}else{expected&!mask};
            // Compare under the same ACPI lock as the masked write. A change
            // between the earlier read and this RMW must not send a command.
            let mut changed=false;
            let written=ec::wmi_modify_ec(addr,|current|{
                if current!=expected{changed=true;current}else{desired}
            });
            if changed{return Err("硬件状态已变化，未写入，请重读".into());}
            if written!=Some(desired)||ec::wmi_read_ec(addr)!=Some(desired){return Err("供电设置回读未确认".into());}Ok(())
        },
        3=>crate::system::power::apply_native_windows_overlay(v["mode"].as_u64().filter(|n|(1..=4).contains(n)).ok_or("无效档位")? as u8),
        4=>{
            use crate::system::color_calibration as icc;
            let device=target(&v)?;
            let id=v["profile"].as_str().ok_or("缺少ICC配置")?;
            let item=icc::list_all_color_profiles(true).into_iter().find(|p|p.id==id).ok_or("ICC配置已不存在")?;
            if !item.file_name.is_empty(){icc::ensure_standard_profiles_deployed();}
            icc::apply_profile_for_device(&device,id)?;
            let actual=icc::get_registry_active_icm_for_device(&device);
            let confirmed=if item.file_name.is_empty(){actual.is_none()}else{actual.as_ref().map(|s|s.eq_ignore_ascii_case(&item.file_name)).unwrap_or(false)};
            if !confirmed{return Err("Windows色彩关联回读未确认，请重新读取".into());}Ok(())
        },
        5=>{
            let name=v["name"].as_str().ok_or("缺少ICC文件名")?;
            let bytes:Vec<u8>=serde_json::from_value(v["bytes"].clone()).map_err(|_|"ICC数据无效")?;
            validate_icc(name,&bytes)?;
            crate::system::color_calibration::import_custom_icc(name,&bytes).map(|_|())
        },
        6=>{
            let zone=v["zone"].as_str().ok_or("缺少灯区")?;
            let on=v["enabled"].as_bool().ok_or("缺少开关")?;
            let level=v["brightness"].as_u64().filter(|n|*n<=4).ok_or("亮度超出范围")? as u8;
            let effect=v["effect"].as_u64().filter(|n|[1,2,3,4,6,9,10,20].contains(n)).ok_or("灯效超出范围")? as u8;
            let speed=v["speed"].as_u64().filter(|n|*n<=4).ok_or("速度超出范围")? as u8;
            let color=v["color"].as_str().filter(|s|s.len()==7&&s.starts_with('#')&&s[1..].bytes().all(|c|c.is_ascii_hexdigit())).ok_or("颜色无效")?;
            let caps=lighting_state();if caps[zone]!=true{return Err("当前硬件不支持此灯区".into());}
            if zone=="logo"&&caps["ec_logo"]==true&&![1,2,20].contains(&effect){return Err("当前Logo只支持常亮及呼吸灯效".into());}
            let mgr=lighting();let mut state=lighting_saved();
            let ok=match zone {
                "front"=>{let ok=mgr.apply_lightbar(on,effect,level,speed,color);state.lb_enabled=on;state.lb_effect=effect;state.lb_brightness=level;state.lb_speed=speed;state.lb_color=color.into();ok},
                "hinge"=>{let ok=mgr.apply_hinge(on,effect,level,speed,color);state.hinge_enabled=on;state.hinge_effect=effect;state.hinge_brightness=level;state.hinge_speed=speed;state.hinge_color=color.into();ok},
                "logo"=>{let ok=mgr.apply_logo(on,effect,level,color);state.logo_enabled=on;state.logo_effect=effect;state.logo_brightness=level;state.logo_color=color.into();ok},
                _=>return Err("不支持的灯区".into())
            };
            if !ok{return Err("Core未确认灯区指令成功".into());}
            let persisted=(||{
                std::fs::create_dir_all(data_dir()).map_err(|e|e.to_string())?;
                let bytes=serde_json::to_vec_pretty(&state).map_err(|e|e.to_string())?;
                crate::system::config_io::atomic_write(&data_dir().join("lighting.json"),&bytes)
            })();
            persisted.map_err(|error|format!("灯区指令已确认发送，但设置保存失败：{}",error))
        },
        7=>{
            use crate::system::display_tuning as tuning;
            let on=v["enabled"].as_bool().ok_or("缺少色彩开关")?;
            let mut cfg=tuning::load_display_tuning_config();
            if !v["preset"].is_null(){
                let preset: tuning::DisplayColorPreset=serde_json::from_value(v["preset"].clone()).map_err(|_|"色彩参数无效")?;
                validate_color(&preset)?;if on{tuning::apply_color_preset(&preset)?;}
                cfg.active_preset_id=preset.id.clone();
                if preset.id=="preset_custom"{cfg.custom_preset=preset;}else{cfg.preset_overrides.insert(preset.id.clone(),preset);}
            }
            if !on{shutdown()?;}
            cfg.enabled=on;tuning::save_display_tuning_config(&cfg);
            let saved=tuning::load_display_tuning_config();
            if saved.enabled!=cfg.enabled||saved.active_preset_id!=cfg.active_preset_id{return Err("滤镜已调整，但设置文件保存失败".into());}Ok(())
        },
        _=>Err("不支持的服务".into())
    }
}
pub fn validate_icc(name:&str,bytes:&[u8])->Result<(),String> {
    if name.contains(['/', '\\', ':'])||!(name.to_ascii_lowercase().ends_with(".icc")||name.to_ascii_lowercase().ends_with(".icm")){return Err("ICC文件名无效".into());}
    if bytes.len()<132||bytes.len()>4*1024*1024||&bytes[36..40]!=b"acsp"{return Err("ICC文件头无效或超过4MiB".into());}
    let size=u32::from_be_bytes(bytes[0..4].try_into().unwrap()) as usize;
    let tags=u32::from_be_bytes(bytes[128..132].try_into().unwrap()) as usize;
    if size!=bytes.len()||tags>4096||132+tags*12>size{return Err("ICC长度或标签表无效".into());}
    for n in 0..tags{let p=132+n*12;let start=u32::from_be_bytes(bytes[p+4..p+8].try_into().unwrap()) as usize;let len=u32::from_be_bytes(bytes[p+8..p+12].try_into().unwrap()) as usize;if start.checked_add(len).filter(|end|*end<=size).is_none(){return Err("ICC标签超出文件边界".into());}}
    Ok(())
}
pub fn validate_color(p:&crate::system::display_tuning::DisplayColorPreset)->Result<(),String>{
    if p.id.len()>100||p.name.len()>200||!(1000..=12000).contains(&p.color_temp_k)||![p.gain_r,p.gain_g,p.gain_b].iter().all(|n|n.is_finite()&&(0.0..=2.0).contains(n))||!p.saturation.is_finite()||!(0.0..=2.5).contains(&p.saturation)||![p.gamma,p.contrast].iter().all(|n|n.is_finite()&&(0.5..=2.0).contains(n)){Err("色彩参数超出原版范围".into())}else{Ok(())}
}
