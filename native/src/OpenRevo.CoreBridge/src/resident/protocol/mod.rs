//! Protocol 15 request validation owned by Rust. These DTOs describe the existing
//! paired C# wire contract; business capability/power checks remain in the actor.
mod models;
use models::*;
use openrevo_core::system::{
    resident_pipe_io::MAX_REQUEST_FRAME_BYTES, resident_request::RequestAdmission,
};
use serde::Deserialize;

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct Request {
    #[serde(rename = "Version")]
    _version: i32,
    #[serde(rename = "Id")]
    _id: i64,
    #[serde(rename = "Operation")]
    _operation: i32,
    #[serde(rename = "Value")]
    _value: Option<i32>,
    #[serde(rename = "Expected")]
    _expected: Option<i32>,
    #[serde(rename = "ObservedWake")]
    _observed_wake: Option<i64>,
    #[serde(rename = "Demand")]
    _demand: Option<i32>,
    draft: Option<Draft>,
    trial_id: Option<Guid>,
    osd_appearance: Option<OsdAppearance>,
    desktop: Option<Desktop>,
    plugin: Option<Plugin>,
    preference: Option<Preference>,
    layout: Option<LayoutEdit>,
    tuning: Option<Tuning>,
    maintenance: Option<Maintenance>,
    application: Option<Application>,
}
impl Request {
    fn valid_details(&self) -> bool {
        self.trial_id.as_ref().is_none_or(|id| !id.empty())
            && self.draft.as_ref().is_none_or(Valid::valid)
            && self.osd_appearance.as_ref().is_none_or(Valid::valid)
            && self.desktop.as_ref().is_none_or(Valid::valid)
            && self.plugin.as_ref().is_none_or(Valid::valid)
            && self.preference.as_ref().is_none_or(Valid::valid)
            && self.layout.as_ref().is_none_or(Valid::valid)
            && self.tuning.as_ref().is_none_or(Valid::valid)
            && self.maintenance.as_ref().is_none_or(Valid::valid)
            && self.application.as_ref().is_none_or(Valid::valid)
    }
}
/// Pure decode. The caller has already authenticated the pipe/child. No state,
/// file I/O, worker startup or hardware operation occurs on either code path.
pub(crate) fn decode_after(bytes: &[u8], previous: i64) -> Option<RequestAdmission> {
    if !(1..=MAX_REQUEST_FRAME_BYTES).contains(&bytes.len()) || !bounded_depth(bytes, 12) {
        return None;
    }
    let admission = RequestAdmission::decode_after(bytes, previous)?;
    // Typed DTOs reject unknown/duplicate fields and require non-defaulted
    // constructor parameters, including required nullable values.
    let request: Request = serde_json::from_slice(bytes).ok()?;
    request.valid_details().then_some(admission)
}
fn bounded_depth(bytes: &[u8], maximum: usize) -> bool {
    let (mut depth, mut quoted, mut escaped) = (0usize, false, false);
    for byte in bytes {
        if quoted {
            if escaped {
                escaped = false;
            } else if *byte == b'\\' {
                escaped = true;
            } else if *byte == b'"' {
                quoted = false;
            }
        } else {
            match *byte {
                b'"' => quoted = true,
                b'{' | b'[' => {
                    depth += 1;
                    if depth > maximum {
                        return false;
                    }
                }
                b'}' | b']' => {
                    if depth == 0 {
                        return false;
                    }
                    depth -= 1;
                }
                _ => {}
            }
        }
    }
    depth == 0 && !quoted
}
#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{json, Value};
    fn accepted(value: Value) -> bool {
        decode_after(&serde_json::to_vec(&value).unwrap(), 0).is_some()
    }
    fn request(operation: i32) -> Value {
        json!({"Version":15,"Id":1,"Operation":operation})
    }
    #[test]
    fn reads_do_not_require_nested_bodies() {
        for operation in [
            1, 2, 5, 6, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 23, 24, 31,
        ] {
            assert!(accepted(request(operation)));
        }
    }
    #[test]
    fn presentation_accepts_lighting_and_rejects_out_of_range_values() {
        for demand in 0..=5 {
            let mut value = request(33);
            value["Demand"] = json!(demand);
            assert!(accepted(value), "presentation demand {demand}");
        }
        for demand in [-1, 6] {
            let mut value = request(33);
            value["Demand"] = json!(demand);
            assert!(!accepted(value), "presentation demand {demand}");
        }
    }

    #[test]
    fn body_kind_is_bound_to_top_operation() {
        let mut value = request(29);
        value["Preference"] = json!({"Kind":1,"Number":1});
        assert!(accepted(value.clone()));
        value["Operation"] = json!(1);
        assert!(!accepted(value));
    }
    #[test]
    fn duplicate_and_unknown_nested_properties_are_rejected() {
        for bytes in [br#"{"Version":15,"Id":1,"Operation":29,"Preference":{"Kind":1,"Number":1,"Number":2}}"#.as_slice(),
            br#"{"Version":15,"Id":1,"Operation":29,"Preference":{"Kind":1,"Number":1,"RawAddress":1873}}"#.as_slice()]{assert!(decode_after(bytes,0).is_none());}
    }
    #[test]
    fn invalid_nested_color_is_rejected_before_dispatch() {
        let mut value = request(27);
        value["Desktop"] = json!({"Command":"color","PresetId":"preset_custom","Enabled":true,"Values":{"Gamma":5}});
        assert!(!accepted(value));
    }
    #[test]
    fn draft_handoff_is_not_a_device_command() {
        let mut value = request(7);
        value["ObservedWake"] = json!(0);
        value["Draft"] = json!({"BatteryLimit":80});
        assert!(accepted(value));
    }
    #[test]
    fn sequence_and_zero_trial_are_rejected() {
        let value = serde_json::to_vec(&request(1)).unwrap();
        assert!(decode_after(&value, 1).is_none());
        let mut trial = request(21);
        trial["TrialId"] = json!("00000000-0000-0000-0000-000000000000");
        assert!(!accepted(trial));
    }
    #[test]
    fn escaped_braces_do_not_change_depth() {
        assert!(bounded_depth(br#"{"Name":"[\\\"{}]"}"#, 1));
        assert!(!bounded_depth(b"[[[]]]", 2));
    }
}

#[cfg(test)]
mod structural_tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn parameterless_desktop_commands_reject_extra_fields() {
        for command in ["keyboard-undo", "display-off"] {
            let valid = serde_json::to_vec(
                &json!({"Version":15,"Id":1,"Operation":27,"Desktop":{"Command":command}}),
            )
            .unwrap();
            assert!(decode_after(&valid, 0).is_some());
            let invalid=serde_json::to_vec(&json!({"Version":15,"Id":1,"Operation":27,"Desktop":{"Command":command,"RawAddress":1873}})).unwrap();
            assert!(decode_after(&invalid, 0).is_none());
        }
    }
    #[test]
    fn required_nullable_declared_values_cannot_be_omitted() {
        let curve = vec![json!({"Celsius":40,"Percent":20}); 8];
        let declared = json!({"Pl1":null,"Pl2":null,"Temperature":0,"CustomFans":false,"CpuCurve":curve,"GpuCurve":curve,"RestoreOnAc":false});
        let mut value = json!({"Version":15,"Id":1,"Operation":7,"ObservedWake":0,"Draft":{"Tuning":{
            "SelectedPreset":null,"Classic":[],"Declared":declared,"DeclaredModel":"model","Open":true,"HasClassicSelection":false}}});
        assert!(decode_after(&serde_json::to_vec(&value).unwrap(), 0).is_some());
        value["Draft"]["Tuning"]["Declared"]
            .as_object_mut()
            .unwrap()
            .remove("Pl1");
        assert!(decode_after(&serde_json::to_vec(&value).unwrap(), 0).is_none());
    }
}
