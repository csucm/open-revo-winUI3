// Native Pure-Rust NVIDIA NVAPI Driver Interface
// Zero official Mechrevo DLL dependencies. Dynamically links to Windows C:\Windows\System32\nvapi64.dll.

use parking_lot::Mutex;

const NVAPI_OK: i32 = 0;

// Magic IDs for nvapi_QueryInterface (reverse-engineered & verified on physical silicon)
const ID_NVAPI_INITIALIZE: u32 = 0x0150E828;
const ID_NVAPI_UNLOAD: u32 = 0xD22BDD7E;
const ID_NVAPI_ENUM_PHYSICAL_GPUS: u32 = 0xE5AC921F;
const ID_NVAPI_GPU_GET_FULL_NAME: u32 = 0xCEEE8E9F;
const ID_NVAPI_GPU_GET_PSTATES20: u32 = 0x6FF81213;
const ID_NVAPI_GPU_SET_PSTATES20: u32 = 0x0F4DAE6B;
const ID_NVAPI_GPU_SET_THERMAL_POLICIES: u32 = 0x34C0B13D;
const ID_NVAPI_GPU_GET_THERMAL_POLICIES: u32 = 0xE3640A56;

// Structure definitions for PStates20 v1 and v2
// Verified on physical silicon:
// - V1: Size 7316 bytes (0x1C94), Version 0x00011C94 (Strictly required by NVIDIA for Memory Domain 4)
// - V2: Size 7416 bytes (0x1CF8), Version 0x00021CF8 (Required by NVIDIA for Graphics Core Domain 0)
pub const PSTATES20_SIZE_V1: usize = 7316;
pub const PSTATES20_VER1: u32 = 0x00011C94;
pub const PSTATES20_SIZE_V2: usize = 7416;
pub const PSTATES20_VER2: u32 = 0x00021CF8;

pub const CLOCK_DOMAIN_GRAPHICS: u32 = 0; // GPU Core Clock
pub const CLOCK_DOMAIN_MEMORY: u32 = 4;   // VRAM Clock

type FnQueryInterface = unsafe extern "C" fn(u32) -> *const ();
type FnInitialize = unsafe extern "C" fn() -> i32;
type FnUnload = unsafe extern "C" fn() -> i32;
type FnEnumPhysicalGPUs = unsafe extern "C" fn(*mut usize, *mut u32) -> i32;
type FnGPUGetFullName = unsafe extern "C" fn(usize, *mut u8) -> i32;
type FnGPUGetPstates20 = unsafe extern "C" fn(usize, *mut u8) -> i32;
type FnGPUSetPstates20 = unsafe extern "C" fn(usize, *const u8) -> i32;
type FnGPUSetThermalPolicies = unsafe extern "C" fn(usize, *const u8) -> i32;
type FnGPUGetThermalPolicies = unsafe extern "C" fn(usize, *mut u8) -> i32;

struct NvApiBindings {
    _lib: libloading::Library,
    unload: FnUnload,
    get_full_name: FnGPUGetFullName,
    get_pstates20: FnGPUGetPstates20,
    set_pstates20: FnGPUSetPstates20,
    set_thermal_policies: Option<FnGPUSetThermalPolicies>,
    _get_thermal_policies: Option<FnGPUGetThermalPolicies>,
    primary_gpu_handle: usize,
}

enum Driver {
    Unloaded,
    Unavailable,
    Loaded(NvApiBindings),
}

// Hold this lock across every driver call, including unload. No handle or
// function pointer can outlive the session that provided it.
static NVAPI: Mutex<Driver> = Mutex::new(Driver::Unloaded);

fn load_nvapi() -> Option<NvApiBindings> {
    unsafe {
        // Look in System32
        use libloading::os::windows::{Library, LOAD_LIBRARY_SEARCH_SYSTEM32};
        let lib: libloading::Library =
            Library::load_with_flags("nvapi64.dll", LOAD_LIBRARY_SEARCH_SYSTEM32).ok()?.into();
        let query_interface: libloading::Symbol<FnQueryInterface> = lib.get(b"nvapi_QueryInterface\0").ok()?;

        let get_proc = |id: u32| -> Option<*const ()> {
            let ptr = query_interface(id);
            if ptr.is_null() {
                None
            } else {
                Some(ptr)
            }
        };

        let init_ptr = get_proc(ID_NVAPI_INITIALIZE)?;
        let unload_ptr = get_proc(ID_NVAPI_UNLOAD)?;
        let enum_ptr = get_proc(ID_NVAPI_ENUM_PHYSICAL_GPUS)?;
        let name_ptr = get_proc(ID_NVAPI_GPU_GET_FULL_NAME)?;
        let get_pstates_ptr = get_proc(ID_NVAPI_GPU_GET_PSTATES20)?;
        let set_pstates_ptr = get_proc(ID_NVAPI_GPU_SET_PSTATES20)?;

        let initialize: FnInitialize = std::mem::transmute(init_ptr);
        let unload: FnUnload = std::mem::transmute(unload_ptr);
        let enum_physical_gpus: FnEnumPhysicalGPUs = std::mem::transmute(enum_ptr);
        let get_full_name: FnGPUGetFullName = std::mem::transmute(name_ptr);
        let get_pstates20: FnGPUGetPstates20 = std::mem::transmute(get_pstates_ptr);
        let set_pstates20: FnGPUSetPstates20 = std::mem::transmute(set_pstates_ptr);

        let set_thermal_policies = get_proc(ID_NVAPI_GPU_SET_THERMAL_POLICIES)
            .map(|p| std::mem::transmute(p));
        let get_thermal_policies = get_proc(ID_NVAPI_GPU_GET_THERMAL_POLICIES)
            .map(|p| std::mem::transmute(p));

        let ret = initialize();
        if ret != NVAPI_OK {
            crate::log_warn!("NVAPI", "NvAPI_Initialize returned error code: {}", ret);
            return None;
        }

        let mut handles = [0usize; 64];
        let mut count = 0u32;
        let enum_ret = enum_physical_gpus(handles.as_mut_ptr(), &mut count);
        let primary_gpu_handle = if enum_ret == NVAPI_OK && (1..=handles.len() as u32).contains(&count) {
            handles[0]
        } else {
            0
        };
        if primary_gpu_handle == 0 {
            crate::log_warn!("NVAPI", "NvAPI_EnumPhysicalGPUs returned {} with {} GPUs", enum_ret, count);
        }

        // Keep the initialized session even if enumeration failed so the
        // initialization reference can still be released at transaction end.
        Some(NvApiBindings {
            _lib: lib,
            unload,
            get_full_name,
            get_pstates20,
            set_pstates20,
            set_thermal_policies,
            _get_thermal_policies: get_thermal_policies,
            primary_gpu_handle,
        })
    }
}

fn with_driver<T>(call: impl FnOnce(&NvApiBindings) -> T) -> Option<T> {
    let mut driver = NVAPI.lock();
    if matches!(*driver, Driver::Unloaded) {
        *driver = load_nvapi().map_or(Driver::Unavailable, Driver::Loaded);
    }
    match &*driver {
        Driver::Loaded(bindings) if bindings.primary_gpu_handle != 0 => Some(call(bindings)),
        _ => None,
    }
}

/// Releases the resident's driver session after its queued transaction settles.
/// Keep the library and the initialization reference if the driver refuses to unload.
pub fn release_session() {
    let mut driver = NVAPI.lock();
    if let Driver::Loaded(bindings) = &*driver {
        let status = unsafe { (bindings.unload)() };
        if status != NVAPI_OK {
            crate::log_warn!("NVAPI", "NvAPI_Unload returned error code: {}", status);
            return;
        }
    }
    *driver = Driver::Unloaded;
}

/// Query whether NVAPI is available on the current machine
pub fn is_nvapi_available() -> bool {
    with_driver(|_| ()).is_some()
}

/// Retrieve the GPU model name directly from NVIDIA driver
pub fn get_gpu_full_name() -> Option<String> {
    with_driver(|drv| unsafe {
        let mut name_buf = [0u8; 64];
        if (drv.get_full_name)(drv.primary_gpu_handle, name_buf.as_mut_ptr()) == NVAPI_OK {
            let name = String::from_utf8_lossy(&name_buf)
                .trim_matches(char::from(0))
                .to_string();
            Some(name)
        } else {
            None
        }
    })?
}

/// Apply a single clock offset (Core or Memory) via NvAPI_GPU_SetPstates20
/// domain_id: 0 = Graphics (Core), 4 = Memory
/// delta_mhz: Frequency offset in MHz (-1000 ~ +1000)
unsafe fn set_single_clock_offset_raw(
    drv: &NvApiBindings,
    domain_id: u32,
    delta_mhz: i32,
) -> Result<(), i32> {
    // Memory uses V1 (7316 bytes, 0x00011C94); Graphics uses V2 (7416 bytes, 0x00021CF8)
    let (buf_size, ver_magic) = if domain_id == CLOCK_DOMAIN_MEMORY {
        (PSTATES20_SIZE_V1, PSTATES20_VER1)
    } else {
        (PSTATES20_SIZE_V2, PSTATES20_VER2)
    };

    let mut buf = vec![0u32; buf_size / 4];
    buf[0] = ver_magic;
    buf[2] = 1;
    buf[3] = 1;
    buf[7] = domain_id;
    buf[10] = delta_mhz.checked_mul(1000).ok_or(-5)? as u32; // NVAPI_INVALID_ARGUMENT
    let ret = (drv.set_pstates20)(drv.primary_gpu_handle, buf.as_ptr().cast());
    if ret == NVAPI_OK {
        Ok(())
    } else {
        Err(ret)
    }
}

/// Apply GPU Core Clock and Memory Clock offsets directly to NVIDIA hardware
/// core_offset_mhz: Offset in MHz (e.g. +100 MHz, or 0 to reset)
/// mem_offset_mhz:  Offset in MHz (e.g. +200 MHz, or 0 to reset)
pub fn set_gpu_clock_offsets(core_offset_mhz: i32, mem_offset_mhz: i32) -> Result<(), String> {
    with_driver(|drv| unsafe {
        let state=read_clock_state_raw(drv).ok_or("Clock capabilities unavailable")?;
        if !state.allows(core_offset_mhz,mem_offset_mhz){return Err("Clock target outside editable driver range".to_string());}
        // 1. Set Core Clock Offset (Graphics Core, Domain 0)
        if let Err(code) = if core_offset_mhz!=state.core_mhz {set_single_clock_offset_raw(drv, CLOCK_DOMAIN_GRAPHICS, core_offset_mhz)}else{Ok(())} {
            crate::log_warn!("NVAPI", "Set Core Clock Offset ({} MHz) failed with NVAPI code: {}", core_offset_mhz, code);
            return Err(format!("NVAPI Set Core Clock failed: error code {}", code));
        }

        // 2. Set Memory Clock Offset (VRAM Memory, Domain 4)
        if let Err(code) = if mem_offset_mhz!=state.memory_mhz {set_single_clock_offset_raw(drv, CLOCK_DOMAIN_MEMORY, mem_offset_mhz)}else{Ok(())} {
            crate::log_warn!("NVAPI", "Set Memory Clock Offset ({} MHz) failed with NVAPI code: {}", mem_offset_mhz, code);
            return Err(format!("NVAPI Set Memory Clock failed: error code {}", code));
        }

        crate::log_info!(
            "NVAPI",
            "GPU Clock Offsets applied successfully: Core={:+>3} MHz, Memory={:+>4} MHz",
            core_offset_mhz, mem_offset_mhz
        );
        Ok(())
    }).ok_or_else(|| "NVIDIA NVAPI driver not found or not supported".to_string())?
}

/// Reset all GPU Clock Offsets to factory default (+0 MHz)
pub fn reset_gpu_clock_offsets() -> Result<(), String> {
    set_gpu_clock_offsets(0, 0)
}

/// Read current clock offsets from the GPU driver
pub fn get_gpu_clock_offsets() -> Option<(i32, i32)> {
    get_gpu_clock_state().map(|s|(s.core_mhz,s.memory_mhz))
}

// Local native-port patch: parse P0/domain IDs and editable ranges according to
// NVIDIA nvapi.h NV_GPU_PERF_PSTATES20_INFO_V1, rather than assuming entry order.
#[derive(Clone,Debug,serde::Serialize)]
pub struct NativeClockState {
    pub core_mhz:i32,pub memory_mhz:i32,
    pub core_min_mhz:i32,pub core_max_mhz:i32,pub memory_min_mhz:i32,pub memory_max_mhz:i32,
    pub core_editable:bool,pub memory_editable:bool,
}
impl NativeClockState {
    fn allows(&self,core:i32,memory:i32)->bool {
        let core_ok=core==self.core_mhz || self.core_editable&&(self.core_min_mhz..=self.core_max_mhz).contains(&core);
        let memory_ok=memory==self.memory_mhz || self.memory_editable&&(self.memory_min_mhz..=self.memory_max_mhz).contains(&memory);
        core_ok&&memory_ok
    }
}
pub fn parse_native_clock_state(words:&[u32])->Option<NativeClockState> {
    if words.len()!=PSTATES20_SIZE_V1/4||words[0]!=PSTATES20_VER1{return None;}
    let states=words[2] as usize;let clocks=words[3] as usize;
    if !(1..=16).contains(&states)||!(1..=8).contains(&clocks)||words[4]>4{return None;}
    let mut found=None;
    for index in 0..states {
        let start=5+index*114;
        if words[start]!=0{continue;}
        if found.is_some(){return None;}
        let mut core=None;let mut memory=None;
        for clock in 0..clocks {
            let c=start+2+clock*11;
            if !matches!(words[c],CLOCK_DOMAIN_GRAPHICS|CLOCK_DOMAIN_MEMORY){continue;}
            let value=words[c+3] as i32;let min=words[c+4] as i32;let max=words[c+5] as i32;
            if min>max||value%1000!=0{return None;}
            let item=(value/1000,min.div_euclid(1000)+i32::from(min.rem_euclid(1000)!=0),max.div_euclid(1000),words[c+2]&1!=0);
            match words[c] {0=>{if core.replace(item).is_some(){return None;}},4=>{if memory.replace(item).is_some(){return None;}},_=>{}}
        }
        let c=core?;let m=memory?;
        found=Some(NativeClockState{core_mhz:c.0,memory_mhz:m.0,core_min_mhz:c.1,core_max_mhz:c.2,memory_min_mhz:m.1,memory_max_mhz:m.2,core_editable:c.3,memory_editable:m.3});
    }
    found
}
unsafe fn read_clock_state_raw(drv:&NvApiBindings)->Option<NativeClockState> {
    let mut words=vec![0u32;PSTATES20_SIZE_V1/4];words[0]=PSTATES20_VER1;
    if (drv.get_pstates20)(drv.primary_gpu_handle,words.as_mut_ptr().cast())!=NVAPI_OK{return None;}
    parse_native_clock_state(&words)
}
pub fn get_gpu_clock_state()->Option<NativeClockState> {
    with_driver(|drv| unsafe { read_clock_state_raw(drv) })?
}

/// Set GPU Thermal Limit / Temperature Wall in Celsius (e.g. 87°C)
pub fn set_gpu_thermal_limit(celsius: u32) -> Result<(), String> {
    with_driver(|drv| unsafe {
        let set_thermal = drv.set_thermal_policies.ok_or_else(|| "Thermal policies not supported".to_string())?;

        // Struct: size 780 bytes (0x030C), version 0x0002030C
        let mut buf = vec![0u8; 780];
        let u32_slice = std::slice::from_raw_parts_mut(buf.as_mut_ptr() as *mut u32, 780 / 4);
        u32_slice[0] = 0x0002030C; // Version 2
        u32_slice[2] = 1;          // numPolicies = 1
        u32_slice[3] = 6;          // Target: GPU
        u32_slice[5] = celsius.clamp(65, 95); // Thermal limit Celsius

        let ret = set_thermal(drv.primary_gpu_handle, buf.as_ptr());
        if ret == NVAPI_OK {
            crate::log_info!("NVAPI", "GPU Thermal limit set to {}°C", celsius);
            Ok(())
        } else {
            Err(format!("NVAPI Set Thermal Policies failed: error code {}", ret))
        }
    }).ok_or_else(|| "NVIDIA NVAPI driver not found".to_string())?
}
