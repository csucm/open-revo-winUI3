use std::{
    mem::size_of,
    os::windows::io::{AsHandle, AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle},
};
pub use windows::core::PCWSTR;
use windows::core::{Error, Result, PWSTR};
use windows::Win32::Foundation::{
    BOOL, ERROR_INSUFFICIENT_BUFFER, ERROR_NOT_READY, E_ACCESSDENIED, E_INVALIDARG, HANDLE,
};
use windows::Win32::Security::{
    CheckTokenMembership, CreateWellKnownSid, DuplicateTokenEx, EqualSid, GetTokenInformation,
    IsWellKnownSid, SecurityIdentification, TokenImpersonation, TokenIntegrityLevel, TokenPrimary,
    TokenSessionId, TokenUser, WinBuiltinAdministratorsSid, WinMediumLabelSid,
    WinMediumPlusLabelSid, PSID, TOKEN_ACCESS_MASK, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE,
    TOKEN_INFORMATION_CLASS, TOKEN_MANDATORY_LABEL, TOKEN_QUERY, TOKEN_USER,
};
use windows::Win32::System::RemoteDesktop::ProcessIdToSessionId;
use windows::Win32::System::Threading::{
    CreateProcessW, CreateProcessWithTokenW, DeleteProcThreadAttributeList, GetCurrentProcess,
    GetCurrentProcessId, InitializeProcThreadAttributeList, OpenProcess, OpenProcessToken,
    ResumeThread, TerminateProcess, UpdateProcThreadAttribute, WaitForSingleObject,
    CREATE_SUSPENDED, EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST,
    PROCESS_ACCESS_RIGHTS, PROCESS_CREATE_PROCESS, PROCESS_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, STARTUPINFOEXW,
    STARTUPINFOW,
};
use windows::Win32::UI::WindowsAndMessaging::{GetShellWindow, GetWindowThreadProcessId};

/// Obtain a same-user, same-session medium-integrity primary token for the panel.
pub fn open_desktop_token() -> Result<OwnedHandle> {
    let (_, token) = open_desktop(PROCESS_QUERY_LIMITED_INFORMATION)?;
    unsafe {
        duplicate_token(
            HANDLE(token.as_raw_handle()),
            TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY,
            TokenPrimary,
        )
    }
}

fn open_desktop(access: PROCESS_ACCESS_RIGHTS) -> Result<(OwnedHandle, OwnedHandle)> {
    unsafe {
        let shell = GetShellWindow();
        let mut pid = 0;
        if shell.0.is_null() || GetWindowThreadProcessId(shell, Some(&mut pid)) == 0 {
            return Err(Error::from_hresult(windows::core::HRESULT::from_win32(
                ERROR_NOT_READY.0,
            )));
        }
        let process = OwnedHandle::from_raw_handle(OpenProcess(access, false, pid)?.0);
        let token = open_token(
            HANDLE(process.as_raw_handle()),
            TOKEN_QUERY | TOKEN_DUPLICATE,
        )?;
        verify_desktop_token(HANDLE(token.as_raw_handle()))?;
        Ok((process, token))
    }
}

unsafe fn verify_desktop_token(token: HANDLE) -> Result<()> {
    let mut session = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &mut session)?;
    let mut token_session = 0u32;
    let mut size = 0;
    GetTokenInformation(
        token,
        TokenSessionId,
        Some((&mut token_session as *mut u32).cast()),
        size_of::<u32>() as u32,
        &mut size,
    )?;
    if session == 0 || size as usize != size_of::<u32>() || token_session != session {
        return Err(E_ACCESSDENIED.into());
    }

    let owner_token = open_token(GetCurrentProcess(), TOKEN_QUERY)?;
    let owner = token_info::<TOKEN_USER>(HANDLE(owner_token.as_raw_handle()), TokenUser)?;
    let user = token_info::<TOKEN_USER>(token, TokenUser)?;
    let label = token_info::<TOKEN_MANDATORY_LABEL>(token, TokenIntegrityLevel)?;
    let owner_sid = (*owner.as_ptr().cast::<TOKEN_USER>()).User.Sid;
    let user_sid = (*user.as_ptr().cast::<TOKEN_USER>()).User.Sid;
    let integrity = (*label.as_ptr().cast::<TOKEN_MANDATORY_LABEL>()).Label.Sid;
    if EqualSid(owner_sid, user_sid).is_err()
        || !(IsWellKnownSid(integrity, WinMediumLabelSid).as_bool()
            || IsWellKnownSid(integrity, WinMediumPlusLabelSid).as_bool())
        || is_admin(token)?
    {
        return Err(E_ACCESSDENIED.into());
    }
    Ok(())
}

pub(super) unsafe fn open_token(process: HANDLE, access: TOKEN_ACCESS_MASK) -> Result<OwnedHandle> {
    let mut token = HANDLE::default();
    OpenProcessToken(process, access, &mut token)?;
    Ok(OwnedHandle::from_raw_handle(token.0))
}

unsafe fn duplicate_token(
    token: HANDLE,
    access: TOKEN_ACCESS_MASK,
    kind: windows::Win32::Security::TOKEN_TYPE,
) -> Result<OwnedHandle> {
    let mut duplicate = HANDLE::default();
    DuplicateTokenEx(
        token,
        access,
        None,
        SecurityIdentification,
        kind,
        &mut duplicate,
    )?;
    Ok(OwnedHandle::from_raw_handle(duplicate.0))
}

unsafe fn is_admin(token: HANDLE) -> Result<bool> {
    let check = duplicate_token(token, TOKEN_QUERY, TokenImpersonation)?;
    let mut storage = [0u32; 17]; // SECURITY_MAX_SID_SIZE (68).
    let sid = PSID(storage.as_mut_ptr().cast());
    let mut size = size_of::<[u32; 17]>() as u32;
    CreateWellKnownSid(WinBuiltinAdministratorsSid, PSID::default(), sid, &mut size)?;
    let mut member = BOOL::default();
    CheckTokenMembership(HANDLE(check.as_raw_handle()), sid, &mut member)?;
    Ok(member.as_bool())
}

pub(super) unsafe fn token_info<T>(
    token: HANDLE,
    class: TOKEN_INFORMATION_CLASS,
) -> Result<Vec<usize>> {
    let mut size = 0;
    if let Err(error) = GetTokenInformation(token, class, None, 0, &mut size) {
        if error.code() != windows::core::HRESULT::from_win32(ERROR_INSUFFICIENT_BUFFER.0) {
            return Err(error);
        }
    }
    if !(size_of::<T>()..=4096).contains(&(size as usize)) {
        return Err(E_ACCESSDENIED.into());
    }
    let mut buffer = vec![0usize; (size as usize).div_ceil(size_of::<usize>())];
    GetTokenInformation(
        token,
        class,
        Some(buffer.as_mut_ptr().cast()),
        size,
        &mut size,
    )?;
    if (size as usize) < size_of::<T>() {
        return Err(E_ACCESSDENIED.into());
    }
    Ok(buffer)
}

pub(super) fn valid_panel_pipe(name: &str) -> bool {
    name.strip_prefix("OpenRevo.Resident.Panel.")
        .is_some_and(|suffix| {
            suffix.len() == 32 && suffix.bytes().all(|byte| byte.is_ascii_hexdigit())
        })
}

/// Starts the fixed panel invocation and transfers the exact child handle.
///
/// # Safety
/// The token must be a live handle from open_desktop_token. The paths must
/// point to valid NUL-terminated UTF-16 strings and a validated paired panel.
pub unsafe fn start_panel(
    token: std::os::windows::io::RawHandle,
    executable: PCWSTR,
    directory: PCWSTR,
    pipe: &str,
) -> Result<(u32, OwnedHandle)> {
    let mut command = panel_command(executable, directory, pipe)?;
    // The resident already runs on the interactive desktop. With lpDesktop
    // unset, Windows arranges access for the verified desktop user on launch.
    let startup = STARTUPINFOW {
        cb: size_of::<STARTUPINFOW>() as u32,
        ..Default::default()
    };
    let mut info = PROCESS_INFORMATION::default();
    CreateProcessWithTokenW(
        HANDLE(token),
        Default::default(),
        executable,
        PWSTR(command.as_mut_ptr()),
        Default::default(),
        None,
        directory,
        &startup,
        &mut info,
    )?;
    drop(OwnedHandle::from_raw_handle(info.hThread.0));
    Ok((
        info.dwProcessId,
        OwnedHandle::from_raw_handle(info.hProcess.0),
    ))
}

unsafe fn panel_command(executable: PCWSTR, directory: PCWSTR, pipe: &str) -> Result<Vec<u16>> {
    if !valid_panel_pipe(pipe) || executable.is_null() || directory.is_null() {
        return Err(E_INVALIDARG.into());
    }
    let path = executable.as_wide();
    if path.is_empty() || path.contains(&(b'"' as u16)) {
        return Err(E_INVALIDARG.into());
    }
    let mut command = vec![b'"' as u16];
    command.extend_from_slice(path);
    command.extend(format!("\" --resident-panel {pipe} {}", GetCurrentProcessId()).encode_utf16());
    command.push(0);
    Ok(command)
}

/// Uses the verified shell as the parent so the new process inherits its token.
/// Retains the exact process handle needed for pipe authentication and exit.
pub struct DesktopPanelLauncher {
    shell: OwnedHandle,
    token: OwnedHandle,
}

impl DesktopPanelLauncher {
    pub fn open() -> Result<Self> {
        let (shell, token) =
            open_desktop(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_CREATE_PROCESS)?;
        Ok(Self { shell, token })
    }

    pub fn token(&self) -> BorrowedHandle<'_> {
        self.token.as_handle()
    }

    /// # Safety
    /// Paths must be NUL-terminated UTF-16, identifying the validated paired panel.
    pub unsafe fn start(
        &self,
        executable: PCWSTR,
        directory: PCWSTR,
        pipe: &str,
    ) -> Result<(u32, OwnedHandle)> {
        let mut command = panel_command(executable, directory, pipe)?;
        let shell = HANDLE(self.shell.as_raw_handle());
        let attributes = ProcessAttributes::new(&shell)?;
        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        startup.lpAttributeList = attributes.list;
        let mut info = PROCESS_INFORMATION::default();
        CreateProcessW(
            executable,
            PWSTR(command.as_mut_ptr()),
            None,
            None,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED,
            None,
            directory,
            &startup.StartupInfo,
            &mut info,
        )?;
        let child = OwnedHandle::from_raw_handle(info.hProcess.0);
        let thread = OwnedHandle::from_raw_handle(info.hThread.0);
        let verified = (|| {
            let token = open_token(info.hProcess, TOKEN_QUERY | TOKEN_DUPLICATE)?;
            verify_desktop_token(HANDLE(token.as_raw_handle()))?;
            let previous = ResumeThread(HANDLE(thread.as_raw_handle()));
            if previous == u32::MAX {
                return Err(Error::from_win32());
            }
            if previous > 1 {
                return Err(E_ACCESSDENIED.into());
            }
            Ok(())
        })();
        if let Err(error) = verified {
            let _ = TerminateProcess(info.hProcess, 1);
            let _ = WaitForSingleObject(info.hProcess, 5000);
            return Err(error);
        }
        Ok((info.dwProcessId, child))
    }
}

struct ProcessAttributes<'a> {
    list: LPPROC_THREAD_ATTRIBUTE_LIST,
    _storage: Vec<usize>,
    _parent: &'a HANDLE,
}

impl<'a> ProcessAttributes<'a> {
    unsafe fn new(parent: &'a HANDLE) -> Result<Self> {
        let mut size = 0;
        let result = InitializeProcThreadAttributeList(
            LPPROC_THREAD_ATTRIBUTE_LIST::default(),
            1,
            0,
            &mut size,
        );
        if let Err(error) = result {
            if error.code() != windows::core::HRESULT::from_win32(ERROR_INSUFFICIENT_BUFFER.0) {
                return Err(error);
            }
        }
        if !(1..=65536).contains(&size) {
            return Err(E_INVALIDARG.into());
        }
        let mut storage = vec![0usize; size.div_ceil(size_of::<usize>())];
        let list = LPPROC_THREAD_ATTRIBUTE_LIST(storage.as_mut_ptr().cast());
        InitializeProcThreadAttributeList(list, 1, 0, &mut size)?;
        let attributes = Self {
            list,
            _storage: storage,
            _parent: parent,
        };
        UpdateProcThreadAttribute(
            list,
            0,
            PROC_THREAD_ATTRIBUTE_PARENT_PROCESS as usize,
            Some(std::ptr::from_ref(parent).cast()),
            size_of::<HANDLE>(),
            None,
            None,
        )?;
        Ok(attributes)
    }
}

impl Drop for ProcessAttributes<'_> {
    fn drop(&mut self) {
        unsafe { DeleteProcThreadAttributeList(self.list) };
    }
}
