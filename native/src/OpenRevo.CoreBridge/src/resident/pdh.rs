//! Bounded, adapter-aware PDH reads. Handles and DLLs have an explicit owner.
use crate::resident::platform::system_library;
use libloading::Library;
use std::{mem::size_of, ptr};
#[repr(C)]
#[derive(Clone, Copy)]
struct Number {
    status: u32,
    value: f64,
}
#[repr(C)]
#[derive(Clone, Copy)]
struct Item {
    name: *const u16,
    number: Number,
}
type Open = unsafe extern "system" fn(*const u16, usize, *mut isize) -> u32;
type Add = unsafe extern "system" fn(isize, *const u16, usize, *mut isize) -> u32;
type Collect = unsafe extern "system" fn(isize) -> u32;
type Array = unsafe extern "system" fn(isize, u32, *mut u32, *mut u32, *mut u8) -> u32;
type Close = unsafe extern "system" fn(isize) -> u32;
pub struct Query {
    library: Library,
    handle: isize,
}
impl Query {
    pub fn new() -> Result<Self, String> {
        let library = system_library("pdh.dll")?;
        let open = unsafe {
            *library
                .get::<Open>(b"PdhOpenQueryW\0")
                .map_err(|e| e.to_string())?
        };
        let mut handle = 0;
        let code = unsafe { open(ptr::null(), 0, &mut handle) };
        if code != 0 || handle == 0 {
            return Err(format!("PDH初始化失败 ({code})"));
        }
        Ok(Self { library, handle })
    }
    pub fn add(&self, path: &str) -> Option<isize> {
        let add = unsafe { self.library.get::<Add>(b"PdhAddEnglishCounterW\0").ok()? };
        let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
        let mut counter = 0;
        (unsafe { add(self.handle, path.as_ptr(), 0, &mut counter) } == 0 && counter != 0)
            .then_some(counter)
    }
    pub fn collect(&self) -> bool {
        unsafe {
            self.library
                .get::<Collect>(b"PdhCollectQueryData\0")
                .ok()
                .is_some_and(|collect| collect(self.handle) == 0)
        }
    }
    pub fn array(&self, counter: isize, format: u32, max: u32) -> Vec<(String, f64)> {
        self.array_matching(counter, format, max, |_| true)
    }

    pub fn array_matching(
        &self,
        counter: isize,
        format: u32,
        max: u32,
        matches: impl Fn(&[u16]) -> bool,
    ) -> Vec<(String, f64)> {
        if counter == 0 {
            return Vec::new();
        }
        let read = match unsafe { self.library.get::<Array>(b"PdhGetFormattedCounterArrayW\0") } {
            Ok(v) => v,
            Err(_) => return Vec::new(),
        };
        let (mut bytes, mut count) = (0, 0);
        if unsafe { read(counter, format, &mut bytes, &mut count, ptr::null_mut()) } != 0x800007d2
            || bytes == 0
            || bytes > max
        {
            return Vec::new();
        }
        let mut buffer = vec![0u64; (bytes as usize).div_ceil(8)];
        let allocated = bytes;
        if unsafe {
            read(
                counter,
                format,
                &mut bytes,
                &mut count,
                buffer.as_mut_ptr().cast(),
            )
        } != 0
            || bytes > allocated
            || count as usize > bytes as usize / size_of::<Item>()
        {
            return Vec::new();
        }
        let base = buffer.as_ptr() as usize;
        let end = base + bytes as usize;
        let mut values = Vec::new();
        for index in 0..count as usize {
            let item =
                unsafe { ptr::read_unaligned((base + index * size_of::<Item>()) as *const Item) };
            if item.number.status > 1 || !item.number.value.is_finite() || item.number.value < 0.0 {
                continue;
            }
            let address = item.name as usize;
            if address < base + count as usize * size_of::<Item>()
                || address > end.saturating_sub(2)
                || (address - base) % 2 != 0
            {
                continue;
            }
            let max_chars = ((end - address) / 2).min(256);
            let words = unsafe { std::slice::from_raw_parts(item.name, max_chars) };
            if let Some(length) = words.iter().position(|v| *v == 0) {
                let name = &words[..length];
                if matches(name) {
                    values.push((String::from_utf16_lossy(name), item.number.value));
                }
            }
        }
        values
    }
}
impl Drop for Query {
    fn drop(&mut self) {
        if let Ok(close) = unsafe { self.library.get::<Close>(b"PdhCloseQuery\0") } {
            unsafe { close(self.handle) };
        }
    }
}

/// Windows thermal zones, used when the hardware driver has no live CPU value.
#[derive(Default)]
pub struct ThermalZones(Option<(Query, isize)>);

impl ThermalZones {
    pub fn read(&mut self) -> Option<i32> {
        if self.0.is_none() {
            let query = Query::new().ok()?;
            let counter = query.add(r"\Thermal Zone Information(*)\Temperature")?;
            self.0 = Some((query, counter));
        }
        let (query, counter) = self.0.as_ref()?;
        if !query.collect() {
            return None;
        }
        query
            .array(*counter, 0x200, 1024 * 1024)
            .into_iter()
            .filter_map(|(_, kelvin)| {
                let celsius = kelvin - 273.15;
                (celsius > 0.0 && celsius < 125.0).then_some(celsius.round_ties_even() as i32)
            })
            .max()
    }
}

const _: () = assert!(size_of::<Number>() == 16);
const _: () = assert!(size_of::<Item>() == 24);
