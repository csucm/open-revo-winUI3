//! Projection and dispatch at the authenticated panel boundary. Only named
//! application commands can reach the owner; frame contents never write EC directly.
use super::{
    hardware::{Outcome, Receipt},
    protocol,
    runtime::{Client, Command},
};
use openrevo_core::system::resident_request::RequestAdmission;
use serde_json::{json, Value};
pub struct Request {
    pub admission: RequestAdmission,
    pub document: Value,
    pub upload: usize,
}
impl Request {
    pub fn decode(bytes: &[u8], previous: i64) -> Option<Self> {
        let admission = protocol::decode_after(bytes, previous)?;
        let document: Value = serde_json::from_slice(bytes).ok()?;
        let upload = match (
            admission.operation,
            document["Desktop"]["Command"].as_str(),
            document["Tuning"]["Action"].as_i64(),
        ) {
            (27, Some("icc-import"), _) => document["Desktop"]["Bytes"].as_u64()?,
            (34, _, Some(9)) => document["Tuning"]["Bytes"].as_u64()?,
            _ => 0,
        };
        Some(Self {
            admission,
            document,
            upload: usize::try_from(upload).ok()?,
        })
    }
}
fn boolean(value: &Value) -> bool {
    value.as_bool().unwrap_or(false)
}
fn number(value: &Value, default: i64) -> i64 {
    value.as_i64().unwrap_or(default)
}
pub fn wake_compact(client: &Client, remembered: Option<bool>) -> bool {
    let state = client.raw_read();
    match state["PanelPreferences"]["WakeMode"].as_str() {
        Some("mini") => true,
        Some("full") => false,
        _ => remembered
            .or_else(|| state["LastMini"].as_bool())
            .unwrap_or(false),
    }
}
pub fn snapshot(client: &Client) -> Value {
    let state = client.raw_read();
    let mut status = json!({"Revision":state["Revision"]});
    client.plugins.project(&mut status);
    status["Revision"] = json!(status["Revision"].as_u64().unwrap_or(0)
        .wrapping_add(client.update_revision()));
    let active = boolean(&state["Active"]);
    let a = &state["Admission"];
    let capabilities = &a["capabilities"];
    let control = &state["Control"];
    let display = &state["Display"];
    let panel = &display["Panel"];
    let pref = &state["PanelPreferences"];
    let osd = &state["Osd"];
    let shape=state["Layouts"].as_object().map(|_|state["Layouts"].clone()).unwrap_or_else(||json!({
        "Overview":["cpu","gpu","vram","fan","performance","battery","display"],
        "Mini":{"Top":["performance","gpu","refresh","brightness","keyboard","battery"],"Quick":["boost","camera","mic","win","awake","screen"]},"MiniIssue":null}));
    let switch = &state["Switches"];
    let gpu = &state["Gpu"];
    json!({"Revision":number(&status["Revision"],0),"Ready":boolean(&state["Initialized"]),"Active":active,
        "Mode":if active{state["Mode"].clone()}else{Value::Null},"CanSelectMode":active&&boolean(&capabilities["performance"]),
        "CanSetBatteryLimit":active&&boolean(&capabilities["battery"]),"CanToggleFanBoost":active&&boolean(&capabilities["fan_boost"]),
        "BatteryLimit":if active{control["Upper"].clone()}else{Value::Null},
        "FanBoost":if active{control["FanClamp"].as_u64().map(|v|v&64!=0)}else{None},"RequestedFanBoost":if active{state["RequestedFanBoost"].clone()}else{Value::Null},
        "KeepAwake":boolean(&state["KeepAwake"]),"WaitingForTakeover":boolean(&state["WaitingForTakeover"]),
        "Gpu":{"Fresh":boolean(&gpu["Fresh"]),"Declared":boolean(&gpu["Declared"]),"FirmwareMode":number(&gpu["FirmwareMode"],0),
            "RuntimeMode":number(&gpu["RuntimeMode"],0),"RestartPending":boolean(&gpu["RestartPending"]),"Integrated":boolean(&gpu["Integrated"]),
            "Hybrid":boolean(&gpu["Hybrid"]),"Discrete":boolean(&gpu["Discrete"])},
        "Switches":{"Camera":switch["Camera"],"Microphone":switch["Microphone"],"WinLock":switch["WinLock"],"Wifi":switch["Wifi"],"Bluetooth":switch["Bluetooth"],
            "Touchpad":switch["Touchpad"],"FnLock":switch["FnLock"],"CameraAvailable":boolean(&switch["CameraAvailable"]),"MonochromeLevel":switch["MonochromeLevel"],"MonochromeAt":switch["MonochromeAt"]},
        "Display":{"Hertz":panel["Hertz"],"Rates":panel["Rates"].as_array().cloned().unwrap_or_default(),"Brightness":display["Brightness"],"Active":panel.is_object(),
            "Width":number(&panel["Width"],0),"Height":number(&panel["Height"],0),"Device":panel["Device"],"Panel":panel["Panel"]},
        "RunningBackgroundPlugins":number(&status["RunningBackgroundPlugins"],0),"PluginRevision":number(&status["PluginRevision"],0),
        "Osd":{"Enabled":osd["Enabled"].as_bool().unwrap_or(true),"Appearance":{"Theme":osd["Theme"].as_str().unwrap_or("cinema"),
            "Scale":number(&osd["Scale"],100),"Position":osd["Position"].as_str().unwrap_or("top-left"),"Motion":osd["Motion"].as_str().unwrap_or("normal")}},
        "Preferences":{"Theme":number(&pref["Theme"],1),"SampleSeconds":number(&pref["SampleSeconds"],2),"WakeMode":pref["WakeMode"].as_str().unwrap_or("remember"),
            "MiniAutoHide":pref["MiniAutoHide"].as_bool().unwrap_or(true),"BatteryOverride":boolean(&pref["BatteryOverride"]),"TrayTelemetry":boolean(&pref["TrayTelemetry"])},
        "Layouts":shape,"LowRefreshOnBattery":boolean(&display["LowRefreshOnBattery"]),"Device":state["Device"],
        "SystemSummary":state["SystemSummary"],"Details":state["Details"],"Theme":state["Theme"],"PluginCatalogRevision":number(&status["PluginCatalogRevision"],0),"Update":client.update_snapshot()})
}
pub fn result(id: i64, outcome: i32) -> Value {
    json!({"Version":15,"Kind":4,"RequestId":id,"Outcome":outcome})
}
fn outcome(receipt: &Receipt) -> i32 {
    match receipt.outcome {
        Outcome::Applied | Outcome::Skipped => 1,
        Outcome::ReadbackConfirmed => 2,
        Outcome::CommandSent => 3,
        Outcome::Rejected => 4,
        Outcome::Busy | Outcome::Closing | Outcome::Cancelled => 5,
        Outcome::Unknown => 6,
        Outcome::ConfigurationSaved => 9,
        Outcome::ConfigurationSaveFailed => 10,
    }
}
fn execute(client: &Client, command: Command) -> Receipt {
    match client.submit(command, true) {
        Ok(pending) => pending.wait(),
        Err(receipt) => receipt,
    }
}
fn action(request: &Request) -> Option<Command> {
    let value = &request.document;
    let number = || value["Value"].as_u64();
    let text = |v: &Value, key: &str| v[key].as_str().map(str::to_owned);
    Some(match request.admission.operation {
        1 => Command::Takeover,
        2 => Command::Release,
        3 => Command::Factory {
            mode: u8::try_from(number()?).ok()?,
            dc_confirmed: true,
        },
        4 => Command::Battery(u8::try_from(number()?).ok()?),
        5 => Command::ToggleBoost,
        6 => Command::ToggleAwake,
        8 => Command::ReadGpu,
        9 => Command::SelectGpu {
            target: i32::try_from(number()?).ok()?,
            expected: i32::try_from(value["Expected"].as_i64()?).ok()?,
        },
        10 => Command::ReadSwitches,
        11 => Command::ToggleCamera,
        12 => Command::ToggleMicrophone,
        13 => Command::ToggleWinLock,
        14..=17 => Command::ToggleOverviewSwitch((request.admission.operation - 13) as u32),
        18 => Command::ReadDisplay,
        19 => Command::Brightness(u8::try_from(number()?).ok()?),
        20 => Command::BeginRefresh {
            target: u32::try_from(number()?).ok()?,
            expected: u32::try_from(value["Expected"].as_u64()?).ok()?,
        },
        21 | 22 => Command::FinishRefresh {
            id: text(value, "TrialId")?,
            keep: request.admission.operation == 21,
        },
        23 => Command::LoadBackgroundPlugins,
        24 => Command::StopBackgroundPlugins,
        28 => Command::Plugins(value.get("Plugin")?.clone()),
        25 => Command::SetOsdEnabled(number()? == 1),
        26 => {
            let v = &value["OsdAppearance"];
            Command::SetOsdAppearance {
                theme: text(v, "Theme")?,
                scale: i32::try_from(v["Scale"].as_i64()?).ok()?,
                position: text(v, "Position")?,
                motion: text(v, "Motion")?,
            }
        }
        27 => Command::Desktop {
            request: value["Desktop"].clone(),
            upload: None,
        },
        29 => Command::CommitPanelPreference(value["Preference"].clone()),
        30 => Command::CommitPanelLayout(value["Layout"].clone()),
        32 => Command::DeclaredFnLock(number()? == 1),
        33 => Command::PresentationDemand(u8::try_from(value["Demand"].as_u64()?).ok()?),
        35 => Command::Maintenance(value["Maintenance"].clone()),
        36 => Command::Application(value["Application"].clone()),
        34 => Command::TuningEditor {
            request: value["Tuning"].clone(),
            upload: None,
        },
        _ => return None,
    })
}
pub fn dispatch(client: &Client, request: &Request, upload: Option<&[u8]>) -> Value {
    let id = request.admission.id;
    if !boolean(&client.raw_read()["Initialized"]) {
        let mut frame = result(id, 5);
        frame["Message"] = json!("原生后台尚未就绪");
        return frame;
    }
    let command = action(request);
    // Revoke before waiting for the actor. Queued plugin commands cannot outrun consent withdrawal.
    if let Some(Command::Plugins(p)) = &command {
        if matches!(p["Operation"].as_u64(), Some(3 | 4)) {
            if let (Some(id), Some(digest)) =
                (p["Target"]["Id"].as_str(), p["Target"]["Digest"].as_str())
            {
                client.plugins.revoke(id, digest);
            }
        } else if p["Operation"] == 7 {
            client.plugins.close_views();
        }
    } else if matches!(&command, Some(Command::StopBackgroundPlugins)) {
        client.plugins.stop();
    }
    let receipt = match command {
        Some(Command::Desktop { request, .. }) => execute(
            client,
            Command::Desktop {
                request,
                upload: upload.map(|v| v.to_vec()),
            },
        ),
        Some(Command::TuningEditor { request, .. }) => execute(
            client,
            Command::TuningEditor {
                request,
                upload: upload.map(|v| v.to_vec()),
            },
        ),
        Some(command) => execute(client, command),
        None => Receipt::error(Outcome::Rejected, "原生开发目标尚未接通此操作"),
    };
    let code = outcome(&receipt);
    let code = if code == 9 && matches!(request.admission.operation, 25 | 26) {
        1
    } else {
        code
    };
    let mut frame = result(id, code);
    frame["Snapshot"] = snapshot(client);
    if let Some(payload) = receipt.payload {
        for key in ["Desktop", "Tuning", "Plugins", "Maintenance", "Application"] {
            if let Some(value) = payload.get(key) {
                frame[key] = value.clone();
            }
        }
    }
    if let Some(message) = receipt.message.or(receipt.warning) {
        frame["Message"] = json!(message.chars().take(1024).collect::<String>());
    }
    if request.admission.operation == 20 && code == 2 {
        let state = client.raw_read();
        let trial = &state["Display"]["Trial"];
        if let Some(deadline) = trial["DeadlineUnixMs"].as_u64().and_then(utc_iso) {
            frame["Outcome"] = json!(8);
            frame["Trial"] = json!({"Id":trial["Id"],"Target":trial["Target"],"Deadline":deadline});
        } else {
            frame["Outcome"] = json!(6);
            frame["Message"] = json!("刷新率确认票据未读取");
        }
    }
    frame
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn panel_plugin_body_reaches_the_actor() {
        for body in [
            json!({"Operation":8}),
            json!({"Operation":4,"Target":{"Id":"sample","Digest":"a".repeat(64)}}),
        ] {
            let bytes = serde_json::to_vec(
                &json!({"Version":15,"Id":1,"Operation":28,"Plugin":body}),
            )
            .unwrap();
            let request = Request::decode(&bytes, 0).unwrap();
            match action(&request) {
                Some(Command::Plugins(accepted)) => assert_eq!(accepted, body),
                _ => panic!("validated plugin request did not reach the plugin command"),
            }
        }
    }
}
pub(super) fn utc_iso(unix_ms: u64) -> Option<String> {
    use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
    #[link(name = "kernel32")]
    extern "system" {
        fn FileTimeToSystemTime(file: *const FILETIME, system: *mut SYSTEMTIME) -> i32;
    }
    let value = unix_ms.checked_add(11644473600000)?.checked_mul(10000)?;
    let file = FILETIME {
        dwLowDateTime: value as u32,
        dwHighDateTime: (value >> 32) as u32,
    };
    let mut system = SYSTEMTIME::default();
    if unsafe { FileTimeToSystemTime(&file, &mut system) } == 0 {
        return None;
    }
    Some(format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}+00:00",
        system.wYear,
        system.wMonth,
        system.wDay,
        system.wHour,
        system.wMinute,
        system.wSecond,
        system.wMilliseconds
    ))
}
