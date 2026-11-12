use super::*;
use std::sync::Mutex;
static LAST_ERROR:Mutex<String>=Mutex::new(String::new());
#[no_mangle]
pub extern "C" fn or_native_services_shutdown()->i32 {
    match catch_unwind(||{
        openrevo_core::system::win_key_hook::set_locked(false)?;
        super::native_admission::release_declared_curve();
        openrevo_core::driver::bld_driver_mgr::stop_bld_rainbow();
        openrevo_core::system::native_services::shutdown()
    }) {Ok(Ok(()))=>0,_=>-4}
}
pub(super) fn last_error()->String{LAST_ERROR.lock().unwrap_or_else(|e|e.into_inner()).clone()}
// One bounded, single-execution command; no two-pass mutation protocol or arbitrary address.
#[no_mangle]
pub unsafe extern "C" fn or_native_feature_set(action:u32,input:*const u8,length:usize)->i32 {
    match catch_unwind(||{
        if input.is_null()||length==0||length>24*1024*1024{return Err("Invalid service request".to_string());}
        let value=serde_json::from_slice(std::slice::from_raw_parts(input,length)).map_err(|e|e.to_string())?;
        if action == 13 { return super::native_admission::apply_classic_transaction(value); }
        if action == 12 { return super::native_admission::apply_declared_cpu_profile(value); }
        openrevo_core::system::native_services::execute(action,value)
    }){
        Ok(Ok(()))=>{LAST_ERROR.lock().unwrap_or_else(|e|e.into_inner()).clear();0},
        Ok(Err(error))=>{*LAST_ERROR.lock().unwrap_or_else(|e|e.into_inner())=error;-4},
        Err(_)=>{*LAST_ERROR.lock().unwrap_or_else(|e|e.into_inner())="Core service panicked; no further operation attempted".into();-2}
    }
}
#[cfg(test)]
mod tests {
    #[test]
    fn color_readback_accepts_equivalent_finite_matrices(){
        let expected=[0.5f32;25];let mut actual=expected;actual[2]+=0.000001;
        assert!(openrevo_core::system::display_tuning::native_color_matrix_matches(&expected,&actual));
    }
    #[test]
    fn color_readback_rejects_a_different_requested_state(){
        let expected=[0.5f32;25];let mut actual=expected;actual[2]+=0.01;
        assert!(!openrevo_core::system::display_tuning::native_color_matrix_matches(&expected,&actual));
    }
    #[test]
    fn color_readback_rejects_nan_and_wrong_lengths(){
        let expected=[0.5f32;25];let mut actual=expected;actual[0]=f32::NAN;
        assert!(!openrevo_core::system::display_tuning::native_color_matrix_matches(&expected,&actual));
        assert!(!openrevo_core::system::display_tuning::native_color_matrix_matches(&expected,&expected[..24]));
    }
    use openrevo_core::system::native_services::{validate_icc,execute};
    #[test]
    fn icc_real_profiles_and_adversarial_file_bounds(){
        let profiles:[&[u8];3]=[include_bytes!("../../OpenRevo.Upstream/resources/profiles/sRGB.icm"),include_bytes!("../../OpenRevo.Upstream/resources/profiles/DCIP3.icm"),include_bytes!("../../OpenRevo.Upstream/resources/profiles/AdobeRGB.icm")];
        for bytes in profiles{assert!(validate_icc("native.icc",bytes).is_ok());}
        let bytes=profiles[0];assert!(validate_icc("../outside.icc",bytes).is_err());assert!(validate_icc("native.txt",bytes).is_err());
        for length in [0,36,127,128,131,bytes.len()-1]{assert!(validate_icc("native.icc",&bytes[..length]).is_err());}
        let mut invalid=bytes.to_vec();invalid[36]=0;assert!(validate_icc("native.icc",&invalid).is_err());
        let mut invalid=bytes.to_vec();invalid[128..132].copy_from_slice(&u32::MAX.to_be_bytes());assert!(validate_icc("native.icc",&invalid).is_err());
        let mut invalid=bytes.to_vec();invalid[136..140].copy_from_slice(&u32::MAX.to_be_bytes());assert!(validate_icc("native.icc",&invalid).is_err());
    }
    #[test]
    fn invalid_service_requests_do_not_reach_a_device(){
        assert!(execute(99,serde_json::json!({})).is_err());
        assert!(execute(8,serde_json::json!({"confirmed":false})).is_err());
        assert!(execute(9,serde_json::json!({"level":99,"filter":""})).is_err());
        assert!(execute(10,serde_json::json!({"path":"relative-data"})).is_err());
        assert!(execute(11,serde_json::json!({"scheme":"scheme_current;shutdown"})).is_err());
        assert!(execute(1,serde_json::json!({"key":"arbitrary","enabled":true,"scheme":"381b4222-f694-41f0-9685-ff5bb260df2e"})).is_err());
        assert!(execute(1,serde_json::json!({"key":"disconnect_net","enabled":true,"scheme":"scheme_current;shutdown"})).is_err());
        assert!(execute(5,serde_json::json!({"name":"bad.icc","bytes":[1,2,3]})).is_err());
        assert!(execute(6,serde_json::json!({"zone":"front","enabled":true,"brightness":99,"effect":1,"speed":2,"color":"#123456"})).is_err());
    }
}

