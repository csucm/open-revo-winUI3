use super::resident_panel::{open_token, token_info, valid_panel_pipe};
use std::{
    mem::size_of,
    os::windows::io::{AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle},
};
use windows::core::{Error, Result, PCWSTR, PWSTR};
use windows::Win32::Foundation::{LocalFree, E_ACCESSDENIED, E_INVALIDARG, HANDLE, HLOCAL};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::{
    TokenLogonSid, TokenUser, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES, TOKEN_GROUPS,
    TOKEN_QUERY, TOKEN_USER,
};
use windows::Win32::Storage::FileSystem::{
    FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    CreateNamedPipeW, GetNamedPipeClientProcessId, GetNamedPipeClientSessionId, PIPE_READMODE_BYTE,
    PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT,
};
use windows::Win32::System::Threading::GetCurrentProcess;

/// Create the one local server instance before starting its matching panel.
/// The optional token is the verified desktop token used for that launch.
pub fn create_local_server(name: &str, desktop: Option<BorrowedHandle<'_>>) -> Result<OwnedHandle> {
    if !valid_panel_pipe(name) {
        return Err(E_INVALIDARG.into());
    }
    unsafe {
        let owner_token = open_token(GetCurrentProcess(), TOKEN_QUERY)?;
        let owner = HANDLE(owner_token.as_raw_handle());
        let user_info = token_info::<TOKEN_USER>(owner, TokenUser)?;
        let user = sid_text((*user_info.as_ptr().cast::<TOKEN_USER>()).User.Sid)?;
        let logon = logon_sid(owner)?;
        let mut sddl = format!("O:{user}D:P(A;;GA;;;{logon})");
        if let Some(desktop) = desktop {
            let desktop_logon = logon_sid(HANDLE(desktop.as_raw_handle()))?;
            if desktop_logon != logon {
                sddl.push_str(&format!("(A;;GRGW;;;{desktop_logon})"));
            }
            sddl.push_str("S:(ML;;NW;;;ME)");
        }

        let sddl: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut descriptor,
            None,
        )?;
        let _descriptor = LocalAllocation(HLOCAL(descriptor.0));
        let security = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor.0,
            bInheritHandle: false.into(),
        };
        let path: Vec<u16> = format!(r"\\.\pipe\{name}")
            .encode_utf16()
            .chain([0])
            .collect();
        let pipe = CreateNamedPipeW(
            PCWSTR(path.as_ptr()),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            32768,
            32768,
            0,
            Some(&security),
        );
        if pipe.is_invalid() {
            return Err(Error::from_win32());
        }
        Ok(OwnedHandle::from_raw_handle(pipe.0))
    }
}

/// Check the connected client against the child created for this pipe.
pub fn is_client(pipe: BorrowedHandle<'_>, pid: u32, session: u32) -> bool {
    let mut actual_pid = 0;
    let mut actual_session = 0;
    let pipe = HANDLE(pipe.as_raw_handle());
    unsafe {
        GetNamedPipeClientProcessId(pipe, &mut actual_pid).is_ok()
            && actual_pid == pid
            && GetNamedPipeClientSessionId(pipe, &mut actual_session).is_ok()
            && actual_session == session
    }
}

unsafe fn logon_sid(token: HANDLE) -> Result<String> {
    let info = token_info::<TOKEN_GROUPS>(token, TokenLogonSid)?;
    let groups = &*info.as_ptr().cast::<TOKEN_GROUPS>();
    if groups.GroupCount != 1 {
        return Err(E_ACCESSDENIED.into());
    }
    let sid = sid_text(groups.Groups[0].Sid)?;
    if !sid.starts_with("S-1-5-5-") {
        return Err(E_ACCESSDENIED.into());
    }
    Ok(sid)
}

unsafe fn sid_text(sid: PSID) -> Result<String> {
    let mut text = PWSTR::null();
    ConvertSidToStringSidW(sid, &mut text)?;
    let _allocation = LocalAllocation(HLOCAL(text.0.cast()));
    PCWSTR(text.0)
        .to_string()
        .map_err(|_| E_ACCESSDENIED.into())
}

struct LocalAllocation(HLOCAL);

impl Drop for LocalAllocation {
    fn drop(&mut self) {
        let _ = unsafe { LocalFree(self.0) };
    }
}
