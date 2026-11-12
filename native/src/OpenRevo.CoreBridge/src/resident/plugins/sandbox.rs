//! Exact AppContainer boundary from PluginSandbox.cs. There is no ordinary-process fallback.
use crate::resident::platform::validate_directory;
use std::{
    ffi::c_void,
    mem::size_of,
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::{Path, PathBuf},
    ptr,
};
use windows::{
    core::{GUID, PCWSTR, PWSTR},
    Win32::{
        Foundation::{CloseHandle, GetLastError, HANDLE},
        System::{
            JobObjects::{
                AssignProcessToJobObject, CreateJobObjectW, JobObjectBasicUIRestrictions,
                JobObjectCpuRateControlInformation, JobObjectExtendedLimitInformation,
                SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                JOB_OBJECT_LIMIT_ACTIVE_PROCESS, JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOB_OBJECT_LIMIT_PROCESS_MEMORY,
            },
            Threading::{
                CreateProcessW, GetCurrentProcess, GetExitCodeProcess, ResumeThread,
                TerminateProcess, WaitForSingleObject, PROCESS_CREATION_FLAGS, PROCESS_INFORMATION,
                STARTUPINFOEXW,
            },
        },
        UI::Shell::{
            FOLDERID_LocalAppData, FOLDERID_Profile, FOLDERID_RoamingAppData, FOLDERID_Windows,
        },
    },
};
#[link(name = "advapi32")]
extern "system" {
    fn OpenProcessToken(process: HANDLE, access: u32, token: *mut HANDLE) -> i32;
    fn GetTokenInformation(
        token: HANDLE,
        kind: u32,
        buffer: *mut c_void,
        size: u32,
        required: *mut u32,
    ) -> i32;
    fn ConvertSidToStringSidW(sid: *const c_void, text: *mut *mut u16) -> i32;
    fn ConvertStringSecurityDescriptorToSecurityDescriptorW(
        text: *const u16,
        revision: u32,
        out: *mut *mut c_void,
        size: *mut u32,
    ) -> i32;
    fn GetSecurityDescriptorOwner(
        sd: *const c_void,
        owner: *mut *mut c_void,
        defaulted: *mut i32,
    ) -> i32;
    fn GetSecurityDescriptorDacl(
        sd: *const c_void,
        present: *mut i32,
        acl: *mut *mut c_void,
        defaulted: *mut i32,
    ) -> i32;
    fn SetNamedSecurityInfoW(
        path: *mut u16,
        kind: u32,
        info: u32,
        owner: *mut c_void,
        group: *mut c_void,
        dacl: *mut c_void,
        sacl: *mut c_void,
    ) -> u32;
    fn EqualSid(a: *const c_void, b: *const c_void) -> i32;
    fn FreeSid(sid: *mut c_void) -> *mut c_void;
}
#[link(name = "userenv")]
extern "system" {
    fn CreateAppContainerProfile(
        name: *const u16,
        display: *const u16,
        description: *const u16,
        caps: *const c_void,
        count: u32,
        sid: *mut *mut c_void,
    ) -> i32;
    fn DeleteAppContainerProfile(name: *const u16) -> i32;
    fn GetAppContainerFolderPath(sid: *const u16, path: *mut *mut u16) -> i32;
}
#[link(name = "kernel32")]
extern "system" {
    fn LocalFree(pointer: *mut c_void) -> *mut c_void;
    fn SetHandleInformation(handle: HANDLE, mask: u32, flags: u32) -> i32;
    fn InitializeProcThreadAttributeList(
        list: *mut c_void,
        count: u32,
        flags: u32,
        size: *mut usize,
    ) -> i32;
    fn UpdateProcThreadAttribute(
        list: *mut c_void,
        flags: u32,
        attribute: usize,
        value: *const c_void,
        size: usize,
        previous: *mut c_void,
        returned: *mut usize,
    ) -> i32;
    fn DeleteProcThreadAttributeList(list: *mut c_void);
    fn CreateNamedPipeW(
        path: *const u16,
        mode: u32,
        pipe_mode: u32,
        instances: u32,
        output: u32,
        input: u32,
        timeout: u32,
        attributes: *const SecurityAttributes,
    ) -> HANDLE;
    fn CreateFileW(
        path: *const u16,
        access: u32,
        share: u32,
        attributes: *const c_void,
        disposition: u32,
        flags: u32,
        template: HANDLE,
    ) -> HANDLE;
}
#[link(name = "shell32")]
extern "system" {
    fn SHGetKnownFolderPath(id: *const GUID, flags: u32, token: HANDLE, path: *mut *mut u16)
        -> i32;
}
#[link(name = "ole32")]
extern "system" {
    fn CoTaskMemFree(pointer: *const c_void);
}
#[repr(C)]
struct SecurityAttributes {
    size: u32,
    descriptor: *mut c_void,
    inherit: i32,
}
struct Kernel(HANDLE);
impl Drop for Kernel {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}
struct Local(*mut c_void);
impl Drop for Local {
    fn drop(&mut self) {
        unsafe { LocalFree(self.0) };
    }
}
fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}
unsafe fn string(pointer: *const u16) -> Result<String, String> {
    if pointer.is_null() {
        return Err("Windows返回空字符串".into());
    }
    let mut words = Vec::new();
    for index in 0..32768 {
        let word = *pointer.add(index);
        if word == 0 {
            return String::from_utf16(&words).map_err(|e| e.to_string());
        }
        words.push(word);
    }
    Err("Windows路径超过边界".into())
}
fn error() -> String {
    format!("Windows隔离接口失败 ({})", unsafe {
        GetLastError().0
    })
}
pub fn user_sid() -> Result<String, String> {
    let mut token = HANDLE::default();
    if unsafe { OpenProcessToken(GetCurrentProcess(), 8, &mut token) } == 0 {
        return Err(error());
    }
    let token = Kernel(token);
    let mut buffer = vec![0usize; 128];
    let mut required = 0;
    if unsafe { GetTokenInformation(token.0, 1, buffer.as_mut_ptr().cast(), 1024, &mut required) }
        == 0
        || required < 8
        || required > 1024
    {
        return Err(error());
    }
    let sid = unsafe { ptr::read_unaligned(buffer.as_ptr().cast::<*mut c_void>()) };
    sid_text(sid)
}
fn sid_text(sid: *const c_void) -> Result<String, String> {
    let mut text = ptr::null_mut();
    if unsafe { ConvertSidToStringSidW(sid, &mut text) } == 0 {
        return Err(error());
    }
    let _memory = Local(text.cast());
    unsafe { string(text) }
}
pub fn protect_directory(path: &Path, sandbox: Option<&str>) -> Result<(), String> {
    validate_directory(path)?;
    let user = user_sid()?;
    let mut sddl = format!("O:{user}D:P(A;OICI;FA;;;{user})(A;OICI;FA;;;SY)");
    if let Some(sid) = sandbox {
        sddl.push_str(&format!("(A;OICI;GRGX;;;{sid})"));
    }
    let descriptor = descriptor(&sddl)?;
    let (mut owner, mut acl) = (ptr::null_mut(), ptr::null_mut());
    let (mut defaulted, mut present) = (0, 0);
    if unsafe { GetSecurityDescriptorOwner(descriptor.0, &mut owner, &mut defaulted) } == 0
        || unsafe {
            GetSecurityDescriptorDacl(descriptor.0, &mut present, &mut acl, &mut defaulted)
        } == 0
        || present == 0
    {
        return Err(error());
    }
    let mut name = path
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let code = unsafe {
        SetNamedSecurityInfoW(
            name.as_mut_ptr(),
            1,
            1 | 4 | 0x80000000,
            owner,
            ptr::null_mut(),
            acl,
            ptr::null_mut(),
        )
    };
    if code != 0 {
        return Err(format!("插件目录ACL设置失败 ({code})"));
    }
    Ok(())
}
fn descriptor(sddl: &str) -> Result<Local, String> {
    let mut result = ptr::null_mut();
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            wide(sddl).as_ptr(),
            1,
            &mut result,
            ptr::null_mut(),
        )
    } == 0
    {
        return Err(error());
    }
    Ok(Local(result))
}
pub fn create_pipe(name: &str) -> Result<OwnedHandle, String> {
    let user = user_sid()?;
    let descriptor = descriptor(&format!("D:P(A;;GA;;;SY)(A;;GA;;;{user})"))?;
    let attributes = SecurityAttributes {
        size: size_of::<SecurityAttributes>() as u32,
        descriptor: descriptor.0,
        inherit: 0,
    };
    let path = wide(&format!(r"\\.\pipe\{name}"));
    let handle = unsafe {
        CreateNamedPipeW(
            path.as_ptr(),
            0x40080003,
            8,
            1,
            65536,
            65536,
            0,
            &attributes,
        )
    };
    if handle.is_invalid() {
        return Err(error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(handle.0) })
}
pub fn endpoint(name: &str) -> Result<OwnedHandle, String> {
    let path = wide(&format!(r"\\.\pipe\{name}"));
    let handle = unsafe {
        CreateFileW(
            path.as_ptr(),
            0xc0000000,
            0,
            ptr::null(),
            3,
            0x40100000,
            HANDLE::default(),
        )
    };
    if handle.is_invalid() {
        return Err(error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(handle.0) })
}
fn known_folder(id: &GUID) -> Result<String, String> {
    let mut text = ptr::null_mut();
    let code = unsafe { SHGetKnownFolderPath(id, 0, HANDLE::default(), &mut text) };
    if code < 0 {
        return Err(format!("用户目录读取失败 ({code})"));
    }
    let result = unsafe { string(text) };
    unsafe { CoTaskMemFree(text.cast()) };
    result
}
fn quote(value: &str) -> String {
    let mut out = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' {
            slashes += 1;
            continue;
        }
        if c == '"' {
            out.extend(std::iter::repeat_n('\\', slashes * 2 + 1));
            out.push(c);
        } else {
            out.extend(std::iter::repeat_n('\\', slashes));
            out.push(c);
        }
        slashes = 0;
    }
    out.extend(std::iter::repeat_n('\\', slashes * 2));
    out.push('"');
    out
}
#[repr(C)]
struct SecurityCapabilities {
    sid: *mut c_void,
    capabilities: *mut c_void,
    count: u32,
    reserved: u32,
}
#[repr(C)]
struct CpuRate {
    flags: u32,
    rate: u32,
}
struct Attributes {
    storage: Vec<usize>,
    initialized: bool,
}
impl Attributes {
    fn new() -> Result<Self, String> {
        let mut bytes = 0;
        unsafe { InitializeProcThreadAttributeList(ptr::null_mut(), 3, 0, &mut bytes) };
        if bytes == 0 || bytes > 65536 {
            return Err("插件进程属性尺寸无效".into());
        }
        let mut value = Self {
            storage: vec![0usize; bytes.div_ceil(size_of::<usize>())],
            initialized: false,
        };
        if unsafe { InitializeProcThreadAttributeList(value.pointer(), 3, 0, &mut bytes) } == 0 {
            return Err(error());
        }
        value.initialized = true;
        Ok(value)
    }
    fn pointer(&mut self) -> *mut c_void {
        self.storage.as_mut_ptr().cast()
    }
    fn set<T>(&mut self, key: usize, value: &T) -> Result<(), String> {
        if unsafe {
            UpdateProcThreadAttribute(
                self.pointer(),
                0,
                key,
                (value as *const T).cast(),
                size_of::<T>(),
                ptr::null_mut(),
                ptr::null_mut(),
            )
        } == 0
        {
            return Err(error());
        }
        Ok(())
    }
}
impl Drop for Attributes {
    fn drop(&mut self) {
        if self.initialized {
            unsafe { DeleteProcThreadAttributeList(self.pointer()) };
        }
    }
}
pub struct Sandbox {
    name: String,
    sid: *mut c_void,
    pub sid_text: String,
    pub temporary: PathBuf,
    process: Option<Kernel>,
    job: Option<Kernel>,
    pub process_id: u32,
}
impl Sandbox {
    pub fn new() -> Result<Self, String> {
        let id =
            unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?;
        let name = format!("OpenRevo.Plugin.{}", format!("{id:?}").replace('-', ""));
        let mut sid = ptr::null_mut();
        let code = unsafe {
            CreateAppContainerProfile(
                wide(&name).as_ptr(),
                wide("Open-Revo plugin").as_ptr(),
                wide("Isolated native plugin").as_ptr(),
                ptr::null(),
                0,
                &mut sid,
            )
        };
        if code < 0 {
            return Err(format!("AppContainer创建失败 ({code})"));
        }
        let mut container = Self {
            name,
            sid,
            sid_text: String::new(),
            temporary: PathBuf::new(),
            process: None,
            job: None,
            process_id: 0,
        };
        container.sid_text = sid_text(sid)?;
        let mut path = ptr::null_mut();
        let code =
            unsafe { GetAppContainerFolderPath(wide(&container.sid_text).as_ptr(), &mut path) };
        if code < 0 {
            return Err(format!("AppContainer临时目录读取失败 ({code})"));
        }
        let value = unsafe { string(path) };
        unsafe { CoTaskMemFree(path.cast()) };
        container.temporary = PathBuf::from(value?);
        Ok(container)
    }
    pub fn start(
        &mut self,
        worker: &Path,
        entry: &Path,
        entry_type: &str,
        endpoint: &OwnedHandle,
    ) -> Result<(), String> {
        if self.process.is_some() {
            return Err("插件进程已启动".into());
        }
        validate_directory(worker.parent().ok_or("插件运行目录缺失")?)?;
        let job =
            Kernel(unsafe { CreateJobObjectW(None, PCWSTR::null()) }.map_err(|e| e.to_string())?);
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | JOB_OBJECT_LIMIT_PROCESS_MEMORY
            | JOB_OBJECT_LIMIT_ACTIVE_PROCESS
            | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        limits.ProcessMemoryLimit = 256 * 1024 * 1024;
        unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
        }
        .map_err(|e| e.to_string())?;
        let cpu = CpuRate {
            flags: 1 | 4,
            rate: 2000,
        };
        unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectCpuRateControlInformation,
                (&cpu as *const CpuRate).cast(),
                size_of::<CpuRate>() as u32,
            )
        }
        .map_err(|e| e.to_string())?;
        let ui = 0xffu32;
        unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectBasicUIRestrictions,
                (&ui as *const u32).cast(),
                4,
            )
        }
        .map_err(|e| e.to_string())?;
        self.job = Some(job);
        let mut attributes = Attributes::new()?;
        let capabilities = SecurityCapabilities {
            sid: self.sid,
            capabilities: ptr::null_mut(),
            count: 0,
            reserved: 0,
        };
        let child_policy = 1u32;
        let handle = endpoint.as_raw_handle() as isize;
        if unsafe { SetHandleInformation(HANDLE(endpoint.as_raw_handle()), 1, 1) } == 0 {
            return Err(error());
        }
        attributes.set(0x20009, &capabilities)?;
        attributes.set(0x2000e, &child_policy)?;
        attributes.set(0x20002, &handle)?;
        let system = known_folder(&FOLDERID_Windows)?;
        let user = known_folder(&FOLDERID_Profile)?;
        let local = known_folder(&FOLDERID_LocalAppData)?;
        let roaming = known_folder(&FOLDERID_RoamingAppData)?;
        let root = Path::new(&system)
            .ancestors()
            .last()
            .ok_or("Windows系统盘无效")?
            .to_string_lossy()
            .trim_end_matches('\\')
            .to_owned();
        let temp = self.temporary.to_str().ok_or("插件临时目录无效")?;
        let environment = [
            format!("APPDATA={roaming}"),
            "COMPlus_EnableDiagnostics=0".into(),
            "DOTNET_EnableDiagnostics=0".into(),
            format!("LOCALAPPDATA={local}"),
            format!("SystemDrive={root}"),
            format!("SystemRoot={system}"),
            format!("TEMP={temp}"),
            format!("TMP={temp}"),
            format!("USERPROFILE={user}"),
            format!("WINDIR={system}"),
        ];
        let block = environment
            .iter()
            .flat_map(|v| v.encode_utf16().chain(Some(0)))
            .chain(Some(0))
            .collect::<Vec<_>>();
        let worker_text = worker.to_str().ok_or("插件Worker路径无效")?;
        let entry_text = entry.to_str().ok_or("插件入口路径无效")?;
        let values = [
            worker_text,
            "--pipe-handle",
            &handle.to_string(),
            "--entry",
            entry_text,
            "--type",
            entry_type,
        ];
        let mut command = wide(&values.into_iter().map(quote).collect::<Vec<_>>().join(" "));
        let application = worker
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let current = worker
            .parent()
            .ok_or("插件运行目录无效")?
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        startup.lpAttributeList =
            windows::Win32::System::Threading::LPPROC_THREAD_ATTRIBUTE_LIST(attributes.pointer());
        let mut info = PROCESS_INFORMATION::default();
        unsafe {
            CreateProcessW(
                PCWSTR(application.as_ptr()),
                PWSTR(command.as_mut_ptr()),
                None,
                None,
                true,
                PROCESS_CREATION_FLAGS(0x80000 | 4 | 0x8000000 | 0x400),
                Some(block.as_ptr().cast()),
                PCWSTR(current.as_ptr()),
                &startup.StartupInfo,
                &mut info,
            )
        }
        .map_err(|e| e.to_string())?;
        self.process_id = info.dwProcessId;
        self.process = Some(Kernel(info.hProcess));
        let thread = Kernel(info.hThread);
        self.verify()?;
        unsafe {
            AssignProcessToJobObject(self.job.as_ref().ok_or("插件Job未创建")?.0, info.hProcess)
        }
        .map_err(|e| e.to_string())?;
        if unsafe { ResumeThread(thread.0) } == u32::MAX {
            return Err(error());
        }
        Ok(())
    }
    fn verify(&self) -> Result<(), String> {
        let process = self.process.as_ref().ok_or("插件进程未创建")?;
        let mut token = HANDLE::default();
        if unsafe { OpenProcessToken(process.0, 8, &mut token) } == 0 {
            return Err(error());
        }
        let token = Kernel(token);
        let info = |kind| -> Result<Vec<usize>, String> {
            let mut buffer = vec![0usize; 128];
            let mut required = 0;
            if unsafe {
                GetTokenInformation(
                    token.0,
                    kind,
                    buffer.as_mut_ptr().cast(),
                    1024,
                    &mut required,
                )
            } == 0
                || required > 1024
                || required < 4
            {
                return Err(error());
            }
            Ok(buffer)
        };
        let bytes = info(29)?;
        if bytes[0] as u32 != 1 {
            return Err("插件进程不是AppContainer".into());
        }
        let bytes = info(31)?;
        let sid = unsafe { ptr::read_unaligned(bytes.as_ptr().cast::<*mut c_void>()) };
        if sid.is_null() || unsafe { EqualSid(sid, self.sid) } == 0 {
            return Err("插件AppContainer身份不匹配".into());
        }
        let bytes = info(30)?;
        if bytes[0] as u32 != 0 {
            return Err("插件进程具有意外的系统能力".into());
        }
        Ok(())
    }
    pub fn exited(&self) -> bool {
        self.process
            .as_ref()
            .is_none_or(|p| unsafe { WaitForSingleObject(p.0, 0).0 } == 0)
    }
    pub fn exit_code(&self) -> Option<u32> {
        let process = self.process.as_ref()?;
        let mut code = 0;
        if unsafe { GetExitCodeProcess(process.0, &mut code) }.is_ok() && code != 259 {
            Some(code)
        } else {
            None
        }
    }
    pub fn stop(&mut self) -> Result<(), String> {
        if let Some(process) = &self.process {
            if !self.exited() {
                let _ = unsafe { TerminateProcess(process.0, 1) };
                self.job.take();
                if unsafe { WaitForSingleObject(process.0, 5000).0 } != 0 {
                    return Err("插件Worker退出尚未确认".into());
                }
            }
        }
        self.process.take();
        self.job.take();
        Ok(())
    }
}
impl Drop for Sandbox {
    fn drop(&mut self) {
        let stopped = self.stop().is_ok();
        if stopped {
            unsafe { DeleteAppContainerProfile(wide(&self.name).as_ptr()) };
        }
        if !self.sid.is_null() {
            unsafe { FreeSid(self.sid) };
            self.sid = ptr::null_mut();
        }
    }
}
const _: () = assert!(size_of::<SecurityCapabilities>() == 24);
const _: () = assert!(size_of::<CpuRate>() == 8);

const _: () = assert!(size_of::<STARTUPINFOEXW>() == 112);
