//! SHA-256 uses the system provider. File hashing is bounded and streaming.
use std::{io::Read, ptr};
#[link(name = "bcrypt")]
extern "system" {
    fn BCryptOpenAlgorithmProvider(
        out: *mut isize,
        name: *const u16,
        provider: *const u16,
        flags: u32,
    ) -> i32;
    fn BCryptCloseAlgorithmProvider(handle: isize, flags: u32) -> i32;
    fn BCryptGetProperty(
        handle: isize,
        key: *const u16,
        out: *mut u8,
        size: u32,
        read: *mut u32,
        flags: u32,
    ) -> i32;
    fn BCryptCreateHash(
        algorithm: isize,
        out: *mut isize,
        object: *mut u8,
        object_size: u32,
        secret: *const u8,
        secret_size: u32,
        flags: u32,
    ) -> i32;
    fn BCryptHashData(hash: isize, data: *const u8, size: u32, flags: u32) -> i32;
    fn BCryptFinishHash(hash: isize, out: *mut u8, size: u32, flags: u32) -> i32;
    fn BCryptDestroyHash(hash: isize) -> i32;
}
struct Algorithm(isize);
impl Drop for Algorithm {
    fn drop(&mut self) {
        unsafe { BCryptCloseAlgorithmProvider(self.0, 0) };
    }
}
struct Hash {
    handle: isize,
    _object: Vec<u8>,
}
impl Drop for Hash {
    fn drop(&mut self) {
        unsafe { BCryptDestroyHash(self.handle) };
    }
}
pub fn reader(input: &mut impl Read, maximum: u64) -> Result<(String, u64), String> {
    let wide = |s: &str| s.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
    let mut handle = 0;
    if unsafe { BCryptOpenAlgorithmProvider(&mut handle, wide("SHA256").as_ptr(), ptr::null(), 0) }
        < 0
    {
        return Err("SHA256初始化失败".into());
    }
    let algorithm = Algorithm(handle);
    let (mut object_size, mut size) = (0u32, 0u32);
    if unsafe {
        BCryptGetProperty(
            algorithm.0,
            wide("ObjectLength").as_ptr(),
            (&mut object_size as *mut u32).cast(),
            4,
            &mut size,
            0,
        )
    } < 0
        || size != 4
        || object_size == 0
        || object_size > 65536
    {
        return Err("SHA256状态尺寸无效".into());
    }
    let mut object = vec![0u8; object_size as usize];
    let mut hash = 0;
    if unsafe {
        BCryptCreateHash(
            algorithm.0,
            &mut hash,
            object.as_mut_ptr(),
            object_size,
            ptr::null(),
            0,
            0,
        )
    } < 0
    {
        return Err("SHA256状态创建失败".into());
    }
    let hash = Hash {
        handle: hash,
        _object: object,
    };
    let mut buffer = [0u8; 65536];
    let mut total = 0u64;
    loop {
        let count = input.read(&mut buffer).map_err(|e| e.to_string())?;
        if count == 0 {
            break;
        }
        total = total
            .checked_add(count as u64)
            .filter(|n| *n <= maximum)
            .ok_or("摘要输入超过边界")?;
        if unsafe { BCryptHashData(hash.handle, buffer.as_ptr(), count as u32, 0) } < 0 {
            return Err("SHA256计算失败".into());
        }
    }
    let mut digest = [0u8; 32];
    if unsafe { BCryptFinishHash(hash.handle, digest.as_mut_ptr(), 32, 0) } < 0 {
        return Err("SHA256输出失败".into());
    }
    Ok((digest.iter().map(|b| format!("{b:02X}")).collect(), total))
}
pub fn bytes(input: &[u8]) -> Result<String, String> {
    reader(&mut std::io::Cursor::new(input), input.len() as u64).map(|v| v.0)
}
