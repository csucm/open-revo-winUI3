//! Desktop operations are submitted only to the existing hardware actor.
use super::{read, ControlState, HardwareSession, Outcome, Receipt};
use crate::resident::{
    desktop_power,
    platform::{validate_directory, OperationLock},
};
use openrevo_core::driver::wmi_ec as ec;
use openrevo_core::system::{display_tuning, native_services};
use serde_json::{json, Value};
#[derive(Default)]
pub struct DesktopState {
    lighting: Option<([u8; 5], [u8; 5])>,
}
pub fn query(kind: u32) -> Result<Value, String> {
    let mut bytes = vec![0u8; 65536];
    let mut count = unsafe { crate::or_native_query(kind, 0, bytes.as_mut_ptr(), bytes.len()) };
    if count as usize > bytes.len() && count > 0 {
        if count > 8 * 1024 * 1024 {
            return Err("Core状态超过边界".into());
        }
        bytes.resize(count as usize, 0);
        count = unsafe { crate::or_native_query(kind, 0, bytes.as_mut_ptr(), bytes.len()) };
    }
    if count <= 1 || count as usize > bytes.len() {
        return Err(format!("Core状态读取失败 ({kind}/{count})"));
    }
    serde_json::from_slice(&bytes[..count as usize - 1]).map_err(|e| e.to_string())
}
fn current_lighting() -> Result<[u8; 5], String> {
    Ok([
        read(1897)?,
        read(1898)?,
        read(1899)?,
        read(1932)?,
        read(1895)?,
    ])
}
fn lighting_rgb(bytes: &[u8; 5]) -> u32 {
    let channel = |value: u8| ((f64::from(value) * 255.0 / 50.0).round_ties_even() as u32).min(255);
    (channel(bytes[0]) << 16) | (channel(bytes[1]) << 8) | channel(bytes[2])
}
fn confirmed_keyboard_rgb(bytes: &[u8; 5]) -> Value {
    let level = if bytes[3] & 2 != 0 {
        0
    } else {
        (bytes[3] >> 5).min(4)
    };
    json!({"Rgb":lighting_rgb(bytes),"Level":level,"Rainbow":bytes[4]&128!=0})
}
fn same(a: &[u8; 5], b: &[u8; 5]) -> bool {
    a[..3] == b[..3] && a[3] & !16 == b[3] & !16 && a[4] & !32 == b[4] & !32
}
fn values(p: &display_tuning::DisplayColorPreset) -> Value {
    json!({"Temperature":p.color_temp_k,"Saturation":p.saturation,"Contrast":p.contrast,"Gamma":p.gamma,"Red":p.gain_r,"Green":p.gain_g,"Blue":p.gain_b})
}
fn preset(
    p: &display_tuning::DisplayColorPreset,
    factory: &[display_tuning::DisplayColorPreset],
) -> Value {
    json!({"Id":p.id,"Name":p.name.chars().take(160).collect::<String>(),"Values":values(p),"FactoryAvailable":factory.iter().any(|f|f.id==p.id)})
}
fn sha(bytes: &[u8]) -> Result<String, String> {
    crate::resident::crypto::bytes(bytes)
}
impl HardwareSession {
    fn keyboard_snapshot(&self, state: &DesktopState) -> Result<Value, String> {
        let capability = query(5)?;
        let board = crate::resident::platform::bios_string("BaseBoardProduct").unwrap_or_default();
        let models = query(6).unwrap_or(Value::Null);
        let hint = models
            .as_array()
            .and_then(|a| {
                a.iter().find(|v| {
                    v["model_id"]
                        .as_str()
                        .is_some_and(|s| s.eq_ignore_ascii_case(&board))
                })
            })
            .and_then(|v| v["keyboard_type"].as_str());
        let white = !self.classic() && self.admission["monochrome_control"] == true;
        let declared = !self.classic() && self.admission["declared_rgb"] == true;
        let single = self.state.as_ref().is_some_and(|s| s.support2 & 4 != 0)
            || self.admission["ready"] == true && self.classic() && self.supports("rgb");
        let (kind, label) = if white {
            ("White", "白色背光")
        } else if declared {
            ("SingleZone", "单区 RGB")
        } else if capability["per_key"] == true {
            ("PerKey", "逐键 RGB")
        } else if capability["four_zone"] == true {
            ("4Zone", "四区 RGB")
        } else if single {
            ("SingleZone", "单区 RGB")
        } else {
            match hint {
                Some("White" | "MonochromeWhite") => ("White", "白色背光"),
                Some("None") => ("None", "无键盘背光"),
                _ => ("Unknown", "背光类型未识别"),
            }
        };
        let confirmed = if kind == "SingleZone" && !declared {
            current_lighting().ok().map(|v| confirmed_keyboard_rgb(&v))
        } else {
            None
        };
        let levels = if white && self.active() {
            self.admission["keyboard"]["steps"]
                .as_array()
                .cloned()
                .unwrap_or_default()
        } else {
            Vec::new()
        };
        let mono = if white {
            json!(crate::native_overview::read_monochrome())
        } else {
            Value::Null
        };
        Ok(
            json!({"Kind":kind,"Name":label,"CanSetRgb":self.active()&&kind=="SingleZone"&&(declared||confirmed.is_some()),"CanUndoRgb":self.active()&&state.lighting.is_some(),"CommandOnly":declared||white,"ConfirmedRgb":confirmed,"MonochromeLevels":levels,"MonochromeReadback":if levels.contains(&mono){mono}else{Value::Null}}),
        )
    }
    fn zones_snapshot(&self) -> Result<Vec<Value>, String> {
        let state = native_services::lighting_state();
        let saved = &state["saved"];
        let mut zones = Vec::new();
        for (zone, prefix, speed) in [
            ("front", "lb", state["ec_front"] != true),
            ("hinge", "hinge", true),
            ("logo", "logo", false),
        ] {
            let key = |suffix: &str| saved[format!("{prefix}_{suffix}")].clone();
            let color = key("color")
                .as_str()
                .and_then(|s| u32::from_str_radix(s.trim_start_matches('#'), 16).ok())
                .unwrap_or(0);
            let effects = if zone == "logo" && state["ec_logo"] == true {
                vec![1, 2, 20]
            } else {
                vec![1, 2, 20, 3, 4, 6, 9, 10]
            };
            zones.push(json!({"SavedCommand":{"Zone":zone,"Enabled":key("enabled"),"Effect":key("effect"),"Level":key("brightness"),"Speed":key("speed").as_i64().unwrap_or(2),"Rgb":color},"CanSet":self.active()&&state[zone]==true,"HasSpeed":speed,"Effects":effects}));
        }
        Ok(zones)
    }
    pub fn desktop_snapshot(
        &self,
        state: &DesktopState,
        group: i32,
        settings: &crate::resident::policy::DesktopPreferences,
        low: bool,
    ) -> Value {
        match group {
            1 => {
                json!({"Lighting":{"Keyboard":self.keyboard_snapshot(state).ok(),"Zones":self.zones_snapshot().unwrap_or_default()}})
            }
            2 => {
                let tuning = display_tuning::get_current_tuning_state();
                let mut choices: Vec<_> = tuning
                    .presets
                    .iter()
                    .filter(|p| !p.id.is_empty() && p.id.len() <= 100)
                    .map(|p| preset(p, &tuning.factory_presets))
                    .collect();
                if !choices.iter().any(|p| p["Id"] == tuning.active_preset.id) {
                    choices.push(preset(&tuning.active_preset, &tuning.factory_presets));
                }
                let icc = native_services::calibration_state();
                let device = icc["current_device_name"]
                    .as_str()
                    .filter(|s| !s.is_empty() && s.len() <= 128);
                let profiles=icc["profiles"].as_array().map(|a|a.iter().filter_map(|p|p["id"].as_str().filter(|s|!s.is_empty()&&s.len()<=512).map(|id|json!({"Id":id,"Name":p["name"].as_str().unwrap_or("").chars().take(160).collect::<String>()}))).collect::<Vec<_>>()).unwrap_or_default();
                json!({"Color":{"RuntimeEnabled":display_tuning::native_active(),"SavedEnabled":tuning.enabled,"GammaApplied":display_tuning::native_gamma_applied(),"ActivePreset":preset(&tuning.active_preset,&tuning.factory_presets),"Presets":choices,"Icc":device.map(|d|json!({"Device":d,"ActiveProfile":icc["active_profile_id"],"Enabled":icc["enabled"],"HdrActive":icc["hdr_active"],"Profiles":profiles}))}})
            }
            3 => {
                let windows = desktop_power::policies().unwrap_or_default();
                let power = native_services::power_state();
                let usb = power["trigger"]
                    .as_u64()
                    .map(|v| v & u64::from(ec::TRIGGER_USB_CHARGE) != 0);
                let recovery = power["recovery"]
                    .as_u64()
                    .map(|v| v & u64::from(ec::AC_RECOVERY_BITMASK) != 0);
                let supported = windows.iter().any(|p| p["Supported"] == true);
                let master = supported.then(|| {
                    windows
                        .iter()
                        .filter(|p| p["Supported"] == true)
                        .all(|p| p["Enabled"] == true)
                });
                let mut policies = vec![
                    json!({"Key":"office","Supported":self.active(),"Enabled":settings.sleep_office}),
                    json!({"Key":"link","Supported":true,"Enabled":settings.performance_link}),
                    json!({"Key":"master","Supported":supported,"Enabled":master}),
                    json!({"Key":"usb","Supported":self.active()&&usb.is_some(),"Enabled":usb}),
                    json!({"Key":"recovery","Supported":self.active()&&recovery.is_some(),"Enabled":recovery}),
                ];
                policies.extend(windows);
                json!({"Power":{"Plans":desktop_power::plans().unwrap_or_default(),"ActivePlan":desktop_power::active().ok(),"Policies":policies,"LowRefreshOnBattery":low}})
            }
            _ => Value::Null,
        }
    }
    pub fn execute_desktop(
        &mut self,
        state: &mut DesktopState,
        request: &Value,
        upload: Option<&[u8]>,
        root: &std::path::Path,
    ) -> Receipt {
        let mut attempted = false;
        let mut result_message = None;
        let mut keyboard_reply = None;
        let result = (|| -> Result<Outcome, String> {
            let _lock = OperationLock::enter()?;
            let command = request["Command"].as_str().ok_or("缺少桌面命令")?;
            if command == "read" {
                return Ok(Outcome::Applied);
            }
            self.guard()?;
            self.audit("desktop-service-intent", json!({"command":command}))?;
            match command {
                "keyboard-rgb" | "keyboard-undo" => {
                    let mut keyboard = self.keyboard_snapshot(state)?;
                    let undo = command == "keyboard-undo";
                    if !undo && keyboard["CanSetRgb"] != true
                        || undo && (!self.active() || state.lighting.is_none())
                    {
                        return Err("背光通道不可用或没有可撤销操作".into());
                    }
                    if keyboard["CommandOnly"] == true {
                        if undo {
                            return Err("声明式背光不提供撤销".into());
                        }
                        let level = request["Level"].as_u64().ok_or("背光亮度缺失")?;
                        if level > 1 {
                            return Err("当前背光通道仅支持开关".into());
                        }
                        let effect = if level == 0 {
                            0
                        } else if request["Rainbow"] == true {
                            2
                        } else {
                            1
                        };
                        let rgb = request["Rgb"].as_u64().ok_or("颜色缺失")?;
                        attempted = true;
                        let code = crate::native_admission::or_native_declared_control(
                            5,
                            (effect << 24) | rgb as u32,
                        );
                        if code != 0 {
                            return Err(format!("背光命令发送失败 ({code})"));
                        }
                        keyboard_reply = Some(keyboard);
                        return Ok(Outcome::CommandSent);
                    }
                    if !self.active() {
                        return Err("请先启用硬件接管".into());
                    }
                    let control = ControlState::read()?;
                    self.check_root(&control)?;
                    if control.support2 & 4 == 0 {
                        return Err("本机未匹配单区RGB协议".into());
                    }
                    if read(1989)? & 7 != 0 {
                        return Err("风扇隔离控制与单区灯光通道冲突".into());
                    }
                    let before = current_lighting()?;
                    let after = if undo {
                        let (previous, expected) = state.lighting.ok_or("无可撤销更改")?;
                        if !same(&before, &expected) {
                            return Err("灯光状态已被外部更改，未执行撤销".into());
                        }
                        previous
                    } else {
                        let rgb = request["Rgb"].as_u64().ok_or("颜色缺失")? as u32;
                        let level = request["Level"]
                            .as_u64()
                            .filter(|v| *v <= 4)
                            .ok_or("亮度无效")? as u8;
                        let rainbow = request["Rainbow"] == true;
                        let scale = |c: u32| {
                            if c == 0 {
                                0
                            } else {
                                ((c * 50 / 255).clamp(1, 50)) as u8
                            }
                        };
                        let (mut r, mut g, mut b) = (
                            scale((rgb >> 16) & 255),
                            scale((rgb >> 8) & 255),
                            scale(rgb & 255),
                        );
                        if rgb == lighting_rgb(&before)
                            && before[..3].iter().all(|byte| *byte <= 50)
                        {
                            // A level-only change must preserve the confirmed EC RGB bytes.
                            (r, g, b) = (before[0], before[1], before[2]);
                        } else if rgb == 0xffffff {
                            (r, g, b) = (50, 40, 40);
                        }
                        if rainbow || level == 0 {
                            (r, g, b) = (0, 0, 0);
                        }
                        let backlight = (before[3] & 13)
                            | match level {
                                0 => 18,
                                1 => 48,
                                2 => 80,
                                3 => 112,
                                _ => 144,
                            };
                        let trigger =
                            (before[4] & !128) | 32 | if rainbow && level > 0 { 128 } else { 0 };
                        [r, g, b, backlight, trigger]
                    };
                    if !undo && same(&before, &after) {
                        keyboard["ConfirmedRgb"] = confirmed_keyboard_rgb(&before);
                        keyboard_reply = Some(keyboard);
                        return Ok(Outcome::ReadbackConfirmed);
                    }
                    self.audit(
                        "keyboard-lighting-intent",
                        json!({"before":before,"after":after}),
                    )?;
                    self.guard()?;
                    attempted = true;
                    crate::native_admission::apply_classic_transaction(
                        json!({"kind":"lighting","project":control.project,"old_red":before[0],"old_green":before[1],"old_blue":before[2],"old_backlight":before[3],"old_trigger":before[4],"red":after[0],"green":after[1],"blue":after[2],"backlight":after[3],"trigger":after[4]}),
                    )?;
                    let confirmed = current_lighting()?;
                    if !same(&confirmed, &after) {
                        return Err("灯光最终回读不一致".into());
                    }
                    state.lighting = if undo {
                        None
                    } else {
                        Some((before, confirmed))
                    };
                    keyboard["ConfirmedRgb"] = confirmed_keyboard_rgb(&confirmed);
                    keyboard["CanUndoRgb"] = json!(state.lighting.is_some() && self.active());
                    keyboard_reply = Some(keyboard);
                }
                "keyboard-white" => {
                    let level = request["Level"].as_u64().ok_or("缺少背光档位")?;
                    let keyboard = self.keyboard_snapshot(state)?;
                    if !self.active()
                        || self.classic()
                        || !keyboard["MonochromeLevels"]
                            .as_array()
                            .is_some_and(|a| a.iter().any(|v| v.as_u64() == Some(level)))
                    {
                        return Err("设备未提供此白色背光档位".into());
                    }
                    attempted = true;
                    let code = crate::native_admission::or_native_monochrome_set(level as u32);
                    if code != 0 {
                        return Err(format!("白色背光命令未确认 ({code})"));
                    }
                    let actual = crate::native_overview::read_monochrome().map(u64::from);
                    return Ok(if actual == Some(level) {
                        Outcome::ReadbackConfirmed
                    } else {
                        Outcome::CommandSent
                    });
                }
                "zone" => {
                    let zone = request["Zone"].as_str().ok_or("缺少灯区")?;
                    let zones = self.zones_snapshot()?;
                    let cap = zones
                        .iter()
                        .find(|z| z["SavedCommand"]["Zone"] == zone)
                        .ok_or("不支持此灯区")?;
                    if cap["CanSet"] != true
                        || !cap["Effects"]
                            .as_array()
                            .is_some_and(|a| a.contains(&request["Effect"]))
                    {
                        return Err("设备未提供此灯区或灯效".into());
                    }
                    let speed = if cap["HasSpeed"] == true {
                        request["Speed"].clone()
                    } else {
                        cap["SavedCommand"]["Speed"].clone()
                    };
                    let rgb = request["Rgb"].as_u64().ok_or("缺少颜色")?;
                    attempted = true;
                    native_services::execute(
                        6,
                        json!({"zone":zone,"enabled":request["Enabled"],"effect":request["Effect"],"brightness":request["Level"],"speed":speed,"color":format!("#{rgb:06X}")}),
                    )?;
                    return Ok(Outcome::CommandSent);
                }
                "color" => {
                    let state = display_tuning::get_current_tuning_state();
                    let id = request["PresetId"].as_str().ok_or("缺少色彩预设")?;
                    let presets = if request["Factory"] == true {
                        &state.factory_presets
                    } else {
                        &state.presets
                    };
                    let mut selected = presets
                        .iter()
                        .find(|p| p.id == id)
                        .cloned()
                        .or_else(|| {
                            (request["Factory"] != true && state.active_preset.id == id)
                                .then_some(state.active_preset)
                        })
                        .ok_or("色彩预设已变更，请刷新")?;
                    let edit = &request["Values"];
                    if let Some(v) = edit["Temperature"].as_u64() {
                        selected.color_temp_k = v as u32;
                    }
                    for (key, target) in [
                        ("Saturation", &mut selected.saturation),
                        ("Contrast", &mut selected.contrast),
                        ("Gamma", &mut selected.gamma),
                        ("Red", &mut selected.gain_r),
                        ("Green", &mut selected.gain_g),
                        ("Blue", &mut selected.gain_b),
                    ] {
                        if let Some(v) = edit[key].as_f64() {
                            *target = v as f32;
                        }
                    }
                    native_services::validate_color(&selected)?;
                    attempted = true;
                    native_services::execute(
                        7,
                        json!({"enabled":request["Enabled"],"preset":selected}),
                    )?;
                    return Ok(if request["Enabled"] == true {
                        Outcome::ReadbackConfirmed
                    } else {
                        Outcome::ConfigurationSaved
                    });
                }
                "icc-select" => {
                    let panel = crate::resident::display::PanelState::read()?;
                    let value = serde_json::to_value(panel).map_err(|e| e.to_string())?;
                    if value["Device"] != request["Device"] {
                        return Err("物理内屏未激活或显示拓扑已改变".into());
                    }
                    attempted = true;
                    native_services::execute(
                        4,
                        json!({"device":request["Device"],"profile":request["Profile"]}),
                    )?;
                }
                "icc-import" => {
                    let bytes = upload.ok_or("ICC上传不完整")?;
                    if request["Bytes"].as_u64() != Some(bytes.len() as u64) {
                        return Err("ICC上传长度不匹配".into());
                    }
                    let file = request["FileName"].as_str().ok_or("ICC文件名缺失")?;
                    native_services::validate_icc(file, bytes)?;
                    let stem = std::path::Path::new(file)
                        .file_stem()
                        .and_then(|s| s.to_str())
                        .unwrap_or("")
                        .chars()
                        .filter(|c| c.is_ascii_alphanumeric() || *c == '_')
                        .collect::<String>();
                    let stem = stem.trim_matches('_').chars().take(40).collect::<String>();
                    let suffix = if file.to_ascii_lowercase().ends_with(".icm") {
                        "icm"
                    } else {
                        "icc"
                    };
                    let name = format!("openrevo_{stem}_{}.{}", &sha(bytes)?[..12], suffix);
                    native_services::execute(5, json!({"name":name,"bytes":bytes}))?;
                    return Ok(Outcome::ConfigurationSaved);
                }
                "power-policy" => {
                    let key = request["Key"].as_str().ok_or("缺少策略")?;
                    let on = request["Enabled"].as_bool().ok_or("缺少开关")?;
                    if matches!(key, "usb" | "recovery") {
                        if !self.active() {
                            return Err("请先连接设备".into());
                        }
                        let root = ControlState::read()?;
                        self.check_root(&root)?;
                        let (address, mask) = if key == "usb" {
                            (ec::ADDR_TRIGGER_BYTE, ec::TRIGGER_USB_CHARGE)
                        } else {
                            (ec::ADDR_AC_RECOVERY, ec::AC_RECOVERY_BITMASK)
                        };
                        let before = read(address)?;
                        let target = if on { before | mask } else { before & !mask };
                        self.guard()?;
                        attempted = true;
                        native_services::execute(
                            2,
                            json!({"key":key,"expected":before,"enabled":on}),
                        )?;
                        if read(address)? != target || ControlState::read()? != root {
                            return Err("供电状态或无关硬件位回读不一致".into());
                        }
                    } else {
                        attempted = true;
                        desktop_power::set_policy(key, on)?;
                    }
                }
                "power-plan" => {
                    attempted = true;
                    desktop_power::select(
                        request["Scheme"].as_str().ok_or("缺少方案")?,
                        request["Expected"].as_str().ok_or("缺少预期方案")?,
                    )?;
                }
                "power-recovery" => {
                    if request["Confirmed"] != true {
                        return Err("恢复电源方案需要明确确认".into());
                    }
                    validate_directory(root)?;
                    attempted = true;
                    let message = desktop_power::recover(root)?;
                    result_message = Some(message);
                    return Ok(Outcome::Applied);
                }
                _ => return Err("未知桌面操作".into()),
            }
            self.audit("desktop-service-confirmed", json!({"command":command}))?;
            Ok(Outcome::ReadbackConfirmed)
        })();
        let hardware_transaction = matches!(
            request["Command"].as_str(),
            Some("keyboard-rgb" | "keyboard-undo" | "keyboard-white" | "zone")
        ) || request["Command"] == "power-policy"
            && matches!(request["Key"].as_str(), Some("usb" | "recovery"));
        let mut receipt = if attempted && !hardware_transaction {
            match result {
                Ok(outcome) => Receipt::new(outcome),
                Err(error) => Receipt::error(Outcome::Unknown, error),
            }
        } else {
            self.finish(result, attempted)
        };
        if receipt.message.is_none() {
            receipt.message = result_message;
        }
        if matches!(
            request["Command"].as_str(),
            Some("keyboard-rgb" | "keyboard-undo")
        ) {
            let keyboard = keyboard_reply.or_else(|| self.keyboard_snapshot(state).ok());
            receipt.payload =
                Some(json!({"Desktop":{"Lighting":{"Keyboard":keyboard,"Zones":[]}}}));
        }
        receipt
    }
}
