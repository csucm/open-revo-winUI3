//! Read-only Windows GPU sampling, ported from the pinned WindowsGpuIdentity,
//! WindowsGpuTelemetry, WindowsGpuClocks and AmdGpuClocks. No device selection
//! is inferred from RAM size or guessed from the display name.
use super::{pdh::Query, platform::system_library};
use libloading::Library;
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    ffi::c_void,
    mem::{size_of, transmute_copy},
    ptr,
    time::{Duration, Instant},
};
use windows::{
    core::GUID,
    Win32::{
        Devices::Display::{
            DisplayConfigGetDeviceInfo, DISPLAYCONFIG_ADAPTER_NAME,
            DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME, DISPLAYCONFIG_DEVICE_INFO_HEADER,
        },
        Foundation::LUID,
        System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED},
    },
};
unsafe fn method<T: Copy>(object: *mut c_void, slot: usize) -> T {
    assert_eq!(size_of::<T>(), size_of::<usize>());
    let vtable = *(object as *const *const *const c_void);
    transmute_copy(&*vtable.add(slot))
}
struct Ref {
    pointer: *mut c_void,
    release: usize,
}
impl Ref {
    fn new(pointer: *mut c_void, release: usize) -> Option<Self> {
        (!pointer.is_null()).then_some(Self { pointer, release })
    }
}
impl Drop for Ref {
    fn drop(&mut self) {
        let release: unsafe extern "system" fn(*mut c_void) -> u32 =
            unsafe { method(self.pointer, self.release) };
        unsafe { release(self.pointer) };
    }
}
struct Apartment;
impl Apartment {
    fn new() -> Option<Self> {
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }
            .is_ok()
            .then_some(Self)
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}
#[repr(C)]
#[derive(Clone, Copy, Default, PartialEq, Eq)]
struct Luid {
    low: u32,
    high: i32,
}
fn pdh_prefix(name: &[u16], prefix: &[u8]) -> bool {
    name.get(..prefix.len()).is_some_and(|head| {
        head.iter()
            .zip(prefix)
            .all(|(&word, &byte)| u8::try_from(word).is_ok_and(|ch| ch.eq_ignore_ascii_case(&byte)))
    })
}

impl Luid {
    fn key(self) -> String {
        format!("luid_0x{:08X}_0x{:08X}", self.high as u32, self.low)
    }
}
#[repr(C)]
struct Description {
    name: [u16; 128],
    vendor: u32,
    device: u32,
    subsystem: u32,
    revision: u32,
    dedicated: usize,
    dedicated_system: usize,
    shared: usize,
    luid: Luid,
    flags: u32,
}
#[repr(C)]
struct Open {
    luid: Luid,
    handle: u32,
}
#[repr(C)]
struct AdapterQuery {
    handle: u32,
    kind: u32,
    data: *mut c_void,
    size: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Default)]
struct AdapterInfo {
    handle: u32,
    luid: Luid,
    sources: u32,
    precise_present_regions: i32,
}
#[repr(C)]
struct EnumAdapters {
    count: u32,
    adapters: *mut AdapterInfo,
}
#[repr(C)]
#[derive(Default)]
struct PhysicalDeviceIds {
    index: u32,
    vendor: u32,
    device: u32,
    sub_vendor: u32,
    sub_system: u32,
    revision: u32,
    bus_type: u32,
}
#[repr(C)]
#[derive(Default)]
struct AdapterPerfData {
    physical_index: u32,
    memory_frequency: u64,
    max_memory_frequency: u64,
    max_memory_frequency_oc: u64,
    memory_bandwidth: u64,
    pcie_bandwidth: u64,
    fan_rpm: u32,
    power: u32,
    temperature: u32,
    power_state_override: u8,
}
const _: () = assert!(size_of::<AdapterInfo>() == 20);
const _: () = assert!(size_of::<PhysicalDeviceIds>() == 28);
const _: () = assert!(size_of::<AdapterPerfData>() == 64);
const _: () = assert!(std::mem::offset_of!(AdapterPerfData, temperature) == 56);

#[link(name = "gdi32")]
extern "system" {
    fn D3DKMTEnumAdapters2(args: *mut EnumAdapters) -> i32;
    fn D3DKMTOpenAdapterFromLuid(open: *mut Open) -> i32;
    fn D3DKMTQueryAdapterInfo(query: *mut AdapterQuery) -> i32;
    fn D3DKMTCloseAdapter(handle: *const u32) -> i32;
}
struct Kmt(u32);
impl Kmt {
    fn query<T>(&self, kind: u32, data: &mut T) -> bool {
        let mut request = AdapterQuery {
            handle: self.0,
            kind,
            data: (data as *mut T).cast(),
            size: size_of::<T>() as u32,
        };
        unsafe { D3DKMTQueryAdapterInfo(&mut request) == 0 }
    }
}
impl Drop for Kmt {
    fn drop(&mut self) {
        unsafe { D3DKMTCloseAdapter(&self.0) };
    }
}
fn kmt(luid: Luid) -> Option<Kmt> {
    let mut open = Open { luid, handle: 0 };
    (unsafe { D3DKMTOpenAdapterFromLuid(&mut open) } == 0).then_some(Kmt(open.handle))
}
const ENGINE_UTILIZATION: &str = r"\GPU Engine(*)\Utilization Percentage";

fn engine_usage(query: &Query, counter: isize, key: &str) -> Option<i32> {
    let mut engines = BTreeMap::<String, f64>::new();
    let marker = format!("_{key}_phys_");
    for (name, value) in query.array_matching(counter, 0x8200, 4 * 1024 * 1024, |name| {
        pdh_prefix(name, b"pid_")
            && name
                .windows(marker.len())
                .any(|part| pdh_prefix(part, marker.as_bytes()))
    }) {
        let name = name.to_ascii_lowercase();
        if let Some(start) = name.find(&marker) {
            let engine = name[start + 1 + key.len()..].to_owned();
            *engines.entry(engine).or_default() += value;
        }
    }
    engines
        .values()
        .copied()
        .reduce(f64::max)
        .map(|v| v.clamp(0.0, 100.0).round_ties_even() as i32)
}

fn tray_adapter() -> Option<(Luid, Kmt)> {
    // D3DKMTEnumAdapters2 returns owned handles. Keep one physical NVIDIA
    // render adapter and reject ambiguity, without opening DXGI or NVML.
    let mut entries = [AdapterInfo::default(); 32];
    let mut args = EnumAdapters {
        count: entries.len() as u32,
        adapters: entries.as_mut_ptr(),
    };
    let status = unsafe { D3DKMTEnumAdapters2(&mut args) };
    let handles = entries.map(|a| (a.luid, (a.handle != 0).then(|| Kmt(a.handle))));
    if status != 0 || args.count as usize > handles.len() {
        return None;
    }
    let mut candidates = handles
        .into_iter()
        .take(args.count as usize)
        .filter_map(|(luid, handle)| Some((luid, handle?)))
        .filter(|(_, handle)| {
            let mut flags = 0u32;
            let mut count = 0u32;
            let mut ids = PhysicalDeviceIds::default();
            // SDK KMTQAITYPE_ADAPTERTYPE, PHYSICALADAPTERCOUNT and PHYSICALADAPTERDEVICEIDS.
            handle.query(15, &mut flags)
                && flags & 1 != 0
                && flags & (4 | 32 | 64 | 128) == 0
                && handle.query(30, &mut count)
                && count == 1
                && handle.query(31, &mut ids)
                && ids.vendor == 0x10de
                && !matches!(ids.device, 0 | 0xffff)
        });
    let selected = candidates.next()?;
    candidates.next().is_none().then_some(selected)
}

#[derive(Clone, Copy, Default)]
pub struct TrayReading {
    pub temperature: Option<i32>,
    pub usage: Option<i32>,
}

#[derive(Default)]
pub struct TrayGpu {
    reading: Option<(Instant, Luid, TrayReading)>,
    pending: Option<(Query, isize)>,
}
impl TrayGpu {
    pub fn sample(&mut self, hovered: bool) -> TrayReading {
        // GPU Engine utilization is a rate. Complete the one pending query on
        // the next background sampling tick, then drop it unless hovered again.
        let fresh = self
            .reading
            .as_mut()
            .filter(|(at, _, _)| at.elapsed() < Duration::from_secs(5));
        if let (Some((query, counter)), Some((_, luid, reading))) = (self.pending.take(), fresh) {
            if query.collect() {
                reading.usage = engine_usage(&query, counter, &luid.key().to_ascii_lowercase());
            }
        }
        if hovered {
            self.reading = tray_adapter().map(|(luid, handle)| {
                let mut data = AdapterPerfData::default();
                let temperature = (handle.query(62, &mut data)
                    && (10..=1244).contains(&data.temperature))
                .then(|| ((data.temperature + 5) / 10) as i32);
                let usage = self
                    .reading
                    .as_ref()
                    .filter(|(at, old, _)| *old == luid && at.elapsed() < Duration::from_secs(5))
                    .and_then(|(_, _, previous)| previous.usage);
                self.pending = Query::new().ok().and_then(|query| {
                    let counter = query.add(ENGINE_UTILIZATION)?;
                    query.collect().then_some((query, counter))
                });
                (Instant::now(), luid, TrayReading { temperature, usage })
            });
        }
        self.reading
            .as_ref()
            .filter(|(at, _, _)| at.elapsed() < Duration::from_secs(5))
            .map_or_else(TrayReading::default, |(_, _, reading)| *reading)
    }
}

#[derive(Clone)]
struct Adapter {
    name: String,
    luid: Luid,
    vendor: u32,
    device: u32,
    flags: u32,
    dedicated: u64,
    shared: u64,
    kind: &'static str,
    physical: bool,
    conflict: bool,
    path: Option<String>,
}
fn virtual_name(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    [
        "virtual",
        "remote",
        "gameviewer",
        "easy&light",
        "indirect",
        "miracast",
    ]
    .iter()
    .any(|s| name.contains(s))
}
fn dxcore(factory: Option<&Ref>, luid: Luid) -> (Option<bool>, Option<bool>) {
    let Some(factory) = factory else {
        return (None, None);
    };
    unsafe {
        let get: unsafe extern "system" fn(
            *mut c_void,
            *const Luid,
            *const GUID,
            *mut *mut c_void,
        ) -> i32 = method(factory.pointer, 4);
        let mut adapter = ptr::null_mut();
        let iid = GUID::from_u128(0xf0db4c7f_fe5a_42a2_bd62_f2a6cf6fc83e);
        if get(factory.pointer, &luid, &iid, &mut adapter) < 0 {
            return (None, None);
        }
        let Some(adapter) = Ref::new(adapter, 2) else {
            return (None, None);
        };
        let has: unsafe extern "system" fn(*mut c_void, u32) -> u8 = method(adapter.pointer, 5);
        let read: unsafe extern "system" fn(*mut c_void, u32, usize, *mut u8) -> i32 =
            method(adapter.pointer, 6);
        let property = |key| {
            let mut value = 0;
            (has(adapter.pointer, key) != 0 && read(adapter.pointer, key, 1, &mut value) >= 0)
                .then_some(value != 0)
        };
        (property(11), property(12))
    }
}
fn identify(desc: Description, core: Option<&Ref>) -> Adapter {
    let (hardware, integrated) = dxcore(core, desc.luid);
    let mut packet = DISPLAYCONFIG_ADAPTER_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
            size: size_of::<DISPLAYCONFIG_ADAPTER_NAME>() as u32,
            adapterId: LUID {
                LowPart: desc.luid.low,
                HighPart: desc.luid.high,
            },
            id: 0,
        },
        ..Default::default()
    };
    let path = (unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) } == 0).then(|| {
        let end = packet
            .adapterDevicePath
            .iter()
            .position(|v| *v == 0)
            .unwrap_or(packet.adapterDevicePath.len());
        String::from_utf16_lossy(&packet.adapterDevicePath[..end])
    });
    let pci = path.as_ref().is_some_and(|p| {
        p.to_ascii_uppercase().starts_with(r"\\?\PCI#")
            && p.to_ascii_uppercase()
                .contains(&format!("VEN_{:04X}&DEV_{:04X}", desc.vendor, desc.device))
    });
    let metadata = if desc.flags & 3 == 0 && desc.vendor != 0x10de {
        kmt(desc.luid).and_then(|handle| {
            let mut flags = 0u32;
            let mut query = AdapterQuery {
                handle: handle.0,
                kind: 15,
                data: (&mut flags as *mut u32).cast(),
                size: 4,
            };
            (unsafe { D3DKMTQueryAdapterInfo(&mut query) } == 0).then_some(flags)
        })
    } else {
        None
    };
    let end = desc
        .name
        .iter()
        .position(|v| *v == 0)
        .unwrap_or(desc.name.len());
    let name = String::from_utf16_lossy(&desc.name[..end]);
    let virtual_device = desc.flags & 3 != 0
        || hardware == Some(false)
        || metadata.is_some_and(|f| f & (4 | 64 | 128) != 0)
        || !pci && hardware != Some(true) && virtual_name(&name)
        || path.as_ref().is_some_and(|p| {
            let p = p.to_ascii_uppercase();
            p.starts_with(r"\\?\ROOT#") || p.starts_with(r"\\?\SWD#")
        });
    let physical =
        !virtual_device && (hardware == Some(true) || pci && metadata.is_some_and(|f| f & 1 != 0));
    let hybrid_i = metadata.is_some_and(|f| f & 32 != 0);
    let hybrid_d = metadata.is_some_and(|f| f & 16 != 0);
    let conflict =
        hybrid_i && (hybrid_d || integrated == Some(false)) || hybrid_d && integrated == Some(true);
    let kind = if virtual_device {
        "Virtual"
    } else if !physical || conflict {
        "Unknown"
    } else if integrated == Some(true) || hybrid_i {
        "Integrated"
    } else if integrated == Some(false) || hybrid_d {
        "Discrete"
    } else {
        "Unknown"
    };
    Adapter {
        name,
        luid: desc.luid,
        vendor: desc.vendor,
        device: desc.device,
        flags: desc.flags,
        dedicated: desc.dedicated as u64,
        shared: desc.shared as u64,
        kind,
        physical,
        conflict,
        path,
    }
}
struct Identity {
    factory: Ref,
    core: Option<Ref>,
    _dxgi: Library,
    _dxcore: Option<Library>,
    _apartment: Apartment,
}
impl Identity {
    fn new() -> Option<Self> {
        let apartment = Apartment::new()?;
        let dxgi = system_library("dxgi.dll").ok()?;
        type Create = unsafe extern "system" fn(*const GUID, *mut *mut c_void) -> i32;
        let create = unsafe { *dxgi.get::<Create>(b"CreateDXGIFactory1\0").ok()? };
        let mut factory = ptr::null_mut();
        if unsafe {
            create(
                &GUID::from_u128(0x770aae78_f26f_4dba_a829_253c83d1b387),
                &mut factory,
            )
        } < 0
        {
            return None;
        }
        let factory = Ref::new(factory, 2)?;
        let dxcore = system_library("dxcore.dll").ok();
        let mut core = ptr::null_mut();
        if let Some(lib) = &dxcore {
            if let Ok(create) = unsafe { lib.get::<Create>(b"DXCoreCreateAdapterFactory\0") } {
                if unsafe {
                    create(
                        &GUID::from_u128(0x78ee5945_c36e_4b13_a669_005dd11c0f06),
                        &mut core,
                    )
                } < 0
                {
                    core = ptr::null_mut();
                }
            }
        }
        Some(Self {
            factory,
            core: Ref::new(core, 2),
            _dxgi: dxgi,
            _dxcore: dxcore,
            _apartment: apartment,
        })
    }
    fn current(&self) -> bool {
        let call: unsafe extern "system" fn(*mut c_void) -> i32 =
            unsafe { method(self.factory.pointer, 13) };
        unsafe { call(self.factory.pointer) != 0 }
    }
    fn adapters(&self) -> Vec<Adapter> {
        unsafe {
            let enumerate: unsafe extern "system" fn(*mut c_void, u32, *mut *mut c_void) -> i32 =
                method(self.factory.pointer, 12);
            let mut found = Vec::new();
            for index in 0..32 {
                let mut pointer = ptr::null_mut();
                let code = enumerate(self.factory.pointer, index, &mut pointer);
                if code == 0x887a0002u32 as i32 {
                    return found;
                }
                if code < 0 {
                    return Vec::new();
                }
                let Some(adapter) = Ref::new(pointer, 2) else {
                    return Vec::new();
                };
                let desc: unsafe extern "system" fn(*mut c_void, *mut Description) -> i32 =
                    method(adapter.pointer, 10);
                let mut description: Description = std::mem::zeroed();
                if desc(adapter.pointer, &mut description) < 0 {
                    return Vec::new();
                }
                let candidate = identify(description, self.core.as_ref());
                if !found
                    .iter()
                    .any(|a: &Adapter| a.luid.key() == candidate.luid.key())
                {
                    found.push(candidate);
                }
            }
            Vec::new()
        }
    }
}
fn clocks(
    adapter: &Adapter,
    node: &mut Option<u32>,
    inspected: &mut bool,
) -> (Option<i32>, Option<i32>) {
    let Some(handle) = kmt(adapter.luid) else {
        return (None, None);
    };
    let query = |kind: u32, size: u32, index: u32| -> Option<Vec<u64>> {
        let mut bytes = vec![0u64; (size as usize).div_ceil(8)];
        unsafe { ptr::write(bytes.as_mut_ptr().cast::<u32>(), index) };
        let mut q = AdapterQuery {
            handle: handle.0,
            kind,
            data: bytes.as_mut_ptr().cast(),
            size,
        };
        (unsafe { D3DKMTQueryAdapterInfo(&mut q) } == 0).then_some(bytes)
    };
    let Some(count) = query(30, 4, 0) else {
        return (None, None);
    };
    if count[0] as u32 != 1 {
        return (None, None);
    }
    if !*inspected {
        for index in 0..32 {
            let Some(meta) = query(25, 80, index) else {
                break;
            };
            let engine = (meta[0] >> 32) as u32;
            if engine == 1 {
                *node = Some(index);
                break;
            }
        }
        *inspected = true;
    }
    let mhz = |raw: u64| {
        ((1_000_000..=20_000_000_000).contains(&raw)).then_some((raw / 1_000_000) as i32)
    };
    let core = node.and_then(|n| query(61, 56, n)).and_then(|v| mhz(v[1]));
    let memory = query(62, 64, 0).and_then(|v| mhz(v[1]));
    (core, memory)
}
fn pci(path: &str) -> Option<String> {
    let path = path
        .trim()
        .strip_prefix(r"\\?\")
        .unwrap_or(path.trim())
        .replace('#', "\\")
        .to_ascii_uppercase();
    let parts = path.split('\\').collect::<Vec<_>>();
    (parts.len() >= 2 && parts[0] == "PCI" && parts[1].starts_with("VEN_1002&DEV_"))
        .then(|| parts.into_iter().take(3).collect::<Vec<_>>().join("\\"))
}
fn hardware_id(id: &str) -> &str {
    id.get(4..)
        .and_then(|tail| tail.find('\\').map(|offset| &id[..offset + 4]))
        .unwrap_or(id)
}
#[derive(Default)]
struct Amd {
    performance: Option<Ref>,
    gpu: Option<Ref>,
    terminate: Option<unsafe extern "C" fn() -> i32>,
    library: Option<Library>,
    retry: Option<Instant>,
    temperature: bool,
    power: bool,
}
impl Amd {
    fn disconnect(&mut self) {
        self.performance.take();
        self.gpu.take();
        if let Some(end) = self.terminate.take() {
            unsafe { end() };
        }
        self.library.take();
        self.temperature = false;
        self.power = false;
    }
    fn connect(&mut self, adapter: &Adapter, candidates: &[Adapter]) -> Option<()> {
        let target = pci(adapter.path.as_deref()?)?;
        let hw = hardware_id(&target);
        let unique = candidates
            .iter()
            .filter(|a| {
                a.kind != "Virtual"
                    && a.path
                        .as_deref()
                        .and_then(pci)
                        .is_some_and(|id| hardware_id(&id) == hw)
            })
            .count()
            == 1
            && candidates.iter().all(|a| {
                a.kind == "Virtual"
                    || a.vendor != 0x1002
                    || a.device != adapter.device
                    || a.path.as_deref().and_then(pci).is_some()
            });
        let library = system_library("amdadlx64.dll").ok()?;
        type Version = unsafe extern "C" fn(*mut u64) -> i32;
        type Initialize = unsafe extern "C" fn(u64, *mut *mut c_void) -> i32;
        let version = unsafe { *library.get::<Version>(b"ADLXQueryFullVersion\0").ok()? };
        let initialize = unsafe { *library.get::<Initialize>(b"ADLXInitialize\0").ok()? };
        let end = unsafe {
            *library
                .get::<unsafe extern "C" fn() -> i32>(b"ADLXTerminate\0")
                .ok()?
        };
        let mut v = 0;
        if unsafe { version(&mut v) } != 0 || v >> 48 == 0 {
            return None;
        }
        let mut system = ptr::null_mut();
        if unsafe { initialize(v, &mut system) } != 0 {
            return None;
        }
        self.terminate = Some(end);
        self.library = Some(library);
        if system.is_null() {
            return None;
        }
        type Interface = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> i32;
        let get: Interface = unsafe { method(system, 1) };
        let mut list = ptr::null_mut();
        if unsafe { get(system, &mut list) } != 0 {
            return None;
        }
        let list = Ref::new(list, 1)?;
        let count: unsafe extern "system" fn(*mut c_void) -> u32 =
            unsafe { method(list.pointer, 3) };
        let count = unsafe { count(list.pointer) };
        if count == 0 || count > 32 {
            return None;
        }
        for index in 0..count {
            let at: unsafe extern "system" fn(*mut c_void, u32, *mut *mut c_void) -> i32 =
                unsafe { method(list.pointer, 11) };
            let mut item = ptr::null_mut();
            if unsafe { at(list.pointer, index, &mut item) } != 0 {
                return None;
            }
            let item = Ref::new(item, 1)?;
            let get: Interface = unsafe { method(item.pointer, 9) };
            let mut text = ptr::null_mut();
            if unsafe { get(item.pointer, &mut text) } != 0 || text.is_null() {
                return None;
            }
            let mut raw = Vec::new();
            for index in 0..4096 {
                let byte = unsafe { *text.cast::<u8>().add(index) };
                if byte == 0 {
                    break;
                }
                raw.push(byte);
            }
            if raw.len() == 4096 {
                return None;
            }
            let id = pci(&String::from_utf8_lossy(&raw))?;
            if id != target && !(unique && id == hw) {
                continue;
            }
            if self.gpu.is_some() {
                return None;
            }
            self.gpu = Some(item);
        }
        let gpu = self.gpu.as_ref()?;
        let get: Interface = unsafe { method(system, 9) };
        let mut performance = ptr::null_mut();
        if unsafe { get(system, &mut performance) } != 0 {
            return None;
        }
        self.performance = Ref::new(performance, 1);
        let performance = self.performance.as_ref()?;
        let supported: unsafe extern "system" fn(
            *mut c_void,
            *mut c_void,
            *mut *mut c_void,
        ) -> i32 = unsafe { method(performance.pointer, 21) };
        let mut support = ptr::null_mut();
        if unsafe { supported(performance.pointer, gpu.pointer, &mut support) } == 0 {
            if let Some(support) = Ref::new(support, 1) {
                for (slot, target) in [(6, &mut self.temperature), (8, &mut self.power)] {
                    let get: unsafe extern "system" fn(*mut c_void, *mut u8) -> i32 =
                        unsafe { method(support.pointer, slot) };
                    let mut value = 0;
                    if unsafe { get(support.pointer, &mut value) } == 0 {
                        *target = value != 0;
                    }
                }
            }
        }
        Some(())
    }
    fn read(
        &mut self,
        adapter: &Adapter,
        candidates: &[Adapter],
    ) -> (Option<i32>, Option<i32>, Option<i32>, Option<f64>) {
        let none = (None, None, None, None);
        if self.retry.is_some_and(|t| Instant::now() < t) {
            return none;
        }
        if self.gpu.is_none() && self.connect(adapter, candidates).is_none() {
            self.disconnect();
            self.retry = Some(Instant::now() + Duration::from_secs(30));
            return none;
        }
        let (Some(performance), Some(gpu)) = (&self.performance, &self.gpu) else {
            return none;
        };
        let get: unsafe extern "system" fn(*mut c_void, *mut c_void, *mut *mut c_void) -> i32 =
            unsafe { method(performance.pointer, 18) };
        let mut metrics = ptr::null_mut();
        if unsafe { get(performance.pointer, gpu.pointer, &mut metrics) } != 0 {
            self.disconnect();
            self.retry = Some(Instant::now() + Duration::from_secs(30));
            return none;
        }
        let Some(metrics) = Ref::new(metrics, 1) else {
            self.disconnect();
            self.retry = Some(Instant::now() + Duration::from_secs(30));
            return none;
        };
        let clock = |slot| {
            let get: unsafe extern "system" fn(*mut c_void, *mut i32) -> i32 =
                unsafe { method(metrics.pointer, slot) };
            let mut value = 0;
            (unsafe { get(metrics.pointer, &mut value) } == 0 && value > 0 && value <= 20000)
                .then_some(value)
        };
        let scalar = |slot| {
            let get: unsafe extern "system" fn(*mut c_void, *mut f64) -> i32 =
                unsafe { method(metrics.pointer, slot) };
            let mut value = 0.0;
            (unsafe { get(metrics.pointer, &mut value) } == 0 && value.is_finite()).then_some(value)
        };
        let core = clock(5);
        let memory = clock(6);
        let temperature = self
            .temperature
            .then(|| scalar(7))
            .flatten()
            .filter(|v| *v > 0.0 && *v <= 125.0)
            .map(|v| v.round() as i32);
        let power = self
            .power
            .then(|| scalar(9))
            .flatten()
            .filter(|v| *v >= 0.0 && *v <= 1000.0);
        if core.is_none() && memory.is_none() && temperature.is_none() && power.is_none() {
            self.retry = Some(Instant::now() + Duration::from_secs(30));
        }
        (core, memory, temperature, power)
    }
}
impl Drop for Amd {
    fn drop(&mut self) {
        self.disconnect();
    }
}
#[derive(Default)]
pub struct Gpu {
    identity: Option<Identity>,
    adapters: Vec<Adapter>,
    selected: Option<Adapter>,
    query: Option<Query>,
    engine: isize,
    shared: isize,
    dedicated: isize,
    last_collection: Option<Instant>,
    retry: Option<Instant>,
    clock_retry: Option<Instant>,
    node: Option<u32>,
    inspected: bool,
    empty: u8,
    observed_hybrid: bool,
    declaration: Option<bool>,
    last_nvml: bool,
    query_attempted: bool,
    counter_demand: (bool, bool),
    amd: Amd,
    pub diagnostic: Value,
}
impl Gpu {
    pub fn reset(&mut self) {
        self.query = None;
        self.amd.disconnect();
        self.amd.retry = None;
        self.identity = None;
        self.adapters.clear();
        self.selected = None;
        self.engine = 0;
        self.shared = 0;
        self.dedicated = 0;
        self.last_collection = None;
        self.retry = None;
        self.clock_retry = None;
        self.node = None;
        self.inspected = false;
        self.empty = 0;
        self.query_attempted = false;
    }
    fn discover(&mut self, declaration: Option<bool>, nvml: bool) {
        self.reset();
        self.declaration = declaration;
        self.last_nvml = nvml;
        self.identity = Identity::new();
        self.adapters = self
            .identity
            .as_ref()
            .map(Identity::adapters)
            .unwrap_or_default();
        let visible = self
            .adapters
            .iter()
            .filter(|a| a.kind != "Virtual")
            .collect::<Vec<_>>();
        let physical = visible
            .iter()
            .copied()
            .filter(|a| a.physical)
            .collect::<Vec<_>>();
        let discrete = physical.iter().any(|a| a.kind == "Discrete");
        let integrated = physical.iter().any(|a| a.kind == "Integrated");
        self.observed_hybrid |= discrete && integrated;
        let machine = if self.observed_hybrid {
            "hybrid"
        } else if discrete {
            "discrete-visible"
        } else if declaration == Some(true) && integrated {
            "discrete-declared-integrated-visible"
        } else {
            "unknown"
        };
        let mut reason = "windows-adapter-identity-ambiguous";
        if nvml || visible.iter().any(|a| a.vendor == 0x10de) {
            reason = "nvidia-visible-use-existing-nvml-policy";
        } else if declaration == Some(false) && discrete {
            reason = "discrete-adapter-conflicts-with-integrated-only-declaration";
        } else if (declaration == Some(true) || self.observed_hybrid) && !discrete {
            reason = "discrete-telemetry-target-not-confirmed";
        } else if declaration == Some(false) {
            let eligible = physical
                .iter()
                .copied()
                .filter(|a| a.kind == "Integrated")
                .collect::<Vec<_>>();
            if eligible.len() == 1 && visible.len() == 1 {
                self.selected = Some(eligible[0].clone());
                reason = "single-confirmed-integrated-adapter";
            } else if let Ok(panel) = super::display::PanelState::read() {
                let source = Luid {
                    low: panel.source_adapter.LowPart,
                    high: panel.source_adapter.HighPart,
                };
                let target = Luid {
                    low: panel.target_adapter.LowPart,
                    high: panel.target_adapter.HighPart,
                };
                if source.key() == target.key() {
                    if let Some(adapter) = physical.iter().find(|a| {
                        a.luid.key() == source.key()
                            && (a.kind == "Integrated" || a.kind == "Unknown" && !a.conflict)
                    }) {
                        let mut adapter = (*adapter).clone();
                        adapter.kind = "Integrated";
                        self.selected = Some(adapter);
                        reason = "matched-pure-integrated-manifest-and-internal-display-luid";
                    }
                }
            }
        } else {
            let eligible = physical
                .iter()
                .copied()
                .filter(|a| a.kind == "Discrete")
                .collect::<Vec<_>>();
            if eligible.len() == 1
                && visible
                    .iter()
                    .all(|a| matches!(a.kind, "Integrated" | "Discrete"))
            {
                self.selected = Some(eligible[0].clone());
                reason = "single-confirmed-discrete-telemetry-target";
            } else if visible.len() == 1 && physical.len() == 1 && physical[0].kind == "Integrated"
            {
                self.selected = Some(physical[0].clone());
                reason = "single-confirmed-hardware-adapter";
            }
        }
        self.diagnostic = json!({"Machine":machine,"Selection":reason,"Candidates":self.adapters.iter().map(|a|json!({"Name":a.name,"Luid":a.luid.key(),"Vendor":a.vendor,"Device":a.device,"Kind":a.kind,"Physical":a.physical,"Flags":a.flags,"DedicatedBytes":a.dedicated,"SharedBytes":a.shared,"ArchitectureConflict":a.conflict})).collect::<Vec<_>>()});
        self.retry = Some(Instant::now() + Duration::from_secs(30));
    }
    pub fn close_counters(&mut self) {
        if self.counter_demand != (false, false) {
            self.reset();
            self.counter_demand = (false, false);
        }
    }

    fn open_query(&mut self, metrics: bool, memory: bool) {
        self.query = Query::new().ok();
        if let Some(query) = &self.query {
            self.engine = if metrics {
                query.add(ENGINE_UTILIZATION).unwrap_or(0)
            } else {
                0
            };
            self.shared = if memory {
                query
                    .add(r"\GPU Adapter Memory(*)\Shared Usage")
                    .unwrap_or(0)
            } else {
                0
            };
            self.dedicated = if memory {
                query
                    .add(r"\GPU Adapter Memory(*)\Dedicated Usage")
                    .unwrap_or(0)
            } else {
                0
            };
        }
        if self.engine == 0 && self.shared == 0 && self.dedicated == 0 {
            self.query = None;
        }
        self.last_collection = None;
    }
    pub fn read(
        &mut self,
        declaration: Option<bool>,
        metrics: bool,
        memory: bool,
    ) -> Option<Value> {
        let counters = (metrics, memory);
        if self.counter_demand != counters {
            self.query = None;
            self.last_collection = None;
            self.query_attempted = false;
            self.empty = 0;
            if !metrics {
                self.amd.disconnect();
            }
            self.counter_demand = counters;
        }
        if self.last_nvml
            || self.declaration != declaration
            || self.identity.as_ref().is_some_and(|i| !i.current())
            || self.selected.is_none() && self.retry.is_none_or(|t| Instant::now() >= t)
        {
            self.discover(declaration, false);
        }
        let adapter = self.selected.clone()?;
        if self
            .last_collection
            .is_some_and(|t| t.elapsed() > Duration::from_secs(10))
        {
            self.query = None;
            self.last_collection = None;
        }
        if self.query.is_none()
            && (!self.query_attempted || self.retry.is_none_or(|t| Instant::now() >= t))
        {
            self.open_query(metrics, memory);
            self.query_attempted = true;
            self.retry = Some(Instant::now() + Duration::from_secs(30));
        }
        let (mut usage, mut shared, mut dedicated) = (None, None, None);
        if let Some(query) = &self.query {
            let warmed = self.last_collection.is_some();
            if query.collect() {
                self.last_collection = Some(Instant::now());
                let key = adapter.luid.key().to_ascii_lowercase();
                if warmed && self.engine != 0 {
                    usage = engine_usage(query, self.engine, &key);
                }
                let memory = |counter, capacity: u64| {
                    if counter == 0 || capacity == 0 {
                        return None;
                    }
                    let prefix = format!("{key}_phys_");
                    let mut result = None;
                    for (name, value) in
                        query.array_matching(counter, 0x9200, 4 * 1024 * 1024, |name| {
                            (name.len() == key.len() && pdh_prefix(name, key.as_bytes()))
                                || pdh_prefix(name, prefix.as_bytes())
                        })
                    {
                        let name = name.to_ascii_lowercase();
                        if name == key
                            || name.strip_prefix(&prefix).is_some_and(|tail| {
                                !tail.is_empty()
                                    && tail.bytes().all(|c| c.is_ascii_digit())
                                    && tail.parse::<u32>().is_ok()
                            })
                        {
                            if result.is_some() || value > capacity as f64 || value.trunc() != value
                            {
                                return None;
                            }
                            result = Some(value as u64);
                        }
                    }
                    result
                };
                shared = memory(self.shared, adapter.shared);
                dedicated = memory(self.dedicated, adapter.dedicated);
                if warmed && usage.is_none() && shared.is_none() && dedicated.is_none() {
                    self.empty += 1;
                } else {
                    self.empty = 0;
                }
                if self.empty >= 3 {
                    self.query = None;
                    self.last_collection = None;
                    self.retry = Some(Instant::now() + Duration::from_secs(30));
                    self.empty = 0;
                }
            } else {
                self.query = None;
                self.last_collection = None;
                self.retry = Some(Instant::now() + Duration::from_secs(30));
            }
        }
        let (mut core, mut memory) = (None, None);
        if adapter.kind == "Integrated" && self.clock_retry.is_none_or(|t| Instant::now() >= t) {
            (core, memory) = clocks(&adapter, &mut self.node, &mut self.inspected);
            if core.is_none() && memory.is_none() {
                self.clock_retry = Some(Instant::now() + Duration::from_secs(30));
                if self.node.is_none() {
                    self.inspected = false;
                }
            }
        }
        let (mut temperature, mut power) = (None, None);
        if metrics && adapter.kind == "Integrated" && adapter.vendor == 0x1002 {
            let (a, b, c, d) = self.amd.read(&adapter, &self.adapters);
            core = core.or(a);
            memory = memory.or(b);
            temperature = c;
            power = d;
        }
        Some(
            json!({"Temperature":temperature,"CoreMhz":core,"MemoryMhz":memory,"Usage":usage,"MemoryTotalBytes":(adapter.dedicated>0).then_some(adapter.dedicated),"MemoryUsedBytes":dedicated,"PowerW":power,"AdapterName":adapter.name,"AdapterKind":adapter.kind,"SharedLimitBytes":(adapter.shared>0).then_some(adapter.shared),"SharedUsedBytes":shared}),
        )
    }
}

impl Drop for Gpu {
    fn drop(&mut self) {
        self.reset();
    }
}
const _: () = assert!(size_of::<Description>() == 312);
const _: () = assert!(size_of::<Luid>() == 8);
const _: () = assert!(size_of::<Open>() == 12);
const _: () = assert!(size_of::<AdapterQuery>() == 24);
