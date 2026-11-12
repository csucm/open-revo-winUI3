use std::os::windows::io::{AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle};
use windows::core::{Error, Result, HRESULT, PCWSTR};
use windows::Win32::Foundation::{
    ERROR_BROKEN_PIPE, ERROR_HANDLE_EOF, ERROR_INVALID_DATA, ERROR_IO_PENDING, ERROR_NO_DATA,
    ERROR_OPERATION_ABORTED, ERROR_PIPE_CONNECTED, ERROR_PIPE_NOT_CONNECTED, ERROR_WRITE_FAULT,
    HANDLE, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows::Win32::Storage::FileSystem::{ReadFile, WriteFile};
use windows::Win32::System::Pipes::ConnectNamedPipe;
use windows::Win32::System::Threading::{CreateEventW, WaitForMultipleObjects, INFINITE};
use windows::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};

pub const MAX_FRAME_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_REQUEST_FRAME_BYTES: usize = 1024 * 1024;
pub const MAX_UPLOAD_BYTES: usize = 4 * 1024 * 1024;

/// Overlapped I/O on a local server created by resident_pipe.
/// Calls may block on a worker, with a separate event for each operation.
pub struct PanelPipeIo<'a> {
    pipe: BorrowedHandle<'a>,
    stop: BorrowedHandle<'a>,
    cancel: BorrowedHandle<'a>,
}

impl<'a> PanelPipeIo<'a> {
    pub fn new(
        pipe: BorrowedHandle<'a>,
        stop: BorrowedHandle<'a>,
        cancel: BorrowedHandle<'a>,
    ) -> Self {
        Self { pipe, stop, cancel }
    }

    pub fn connect(&self) -> Result<()> {
        match self.transfer(|pipe, request| unsafe { ConnectNamedPipe(pipe, Some(request)) }) {
            Ok(_) => Ok(()),
            Err(error) if error.code() == HRESULT::from_win32(ERROR_PIPE_CONNECTED.0) => Ok(()),
            Err(error) => Err(error),
        }
    }

    pub fn read_request_frame(&self) -> Result<Box<[u8]>> {
        let mut prefix = [0; 4];
        self.read_exact(&mut prefix)?;
        let length = u32::from_le_bytes(prefix) as usize;
        if !(1..=MAX_REQUEST_FRAME_BYTES).contains(&length) {
            return Err(Error::from_hresult(HRESULT::from_win32(
                ERROR_INVALID_DATA.0,
            )));
        }
        let mut frame = vec![0; length].into_boxed_slice();
        self.read_exact(&mut frame)?;
        Ok(frame)
    }

    pub fn read_upload(&self, buffer: &mut [u8]) -> Result<()> {
        if !(1..=MAX_UPLOAD_BYTES).contains(&buffer.len()) {
            return Err(Error::from_hresult(HRESULT::from_win32(
                ERROR_INVALID_DATA.0,
            )));
        }
        self.read_exact(buffer)
    }

    pub fn write_frame(&self, frame: &[u8]) -> Result<()> {
        if !(1..=MAX_FRAME_BYTES).contains(&frame.len()) {
            return Err(Error::from_hresult(HRESULT::from_win32(
                ERROR_INVALID_DATA.0,
            )));
        }
        self.write(&(frame.len() as u32).to_le_bytes())?;
        self.write(frame)
    }

    fn read_exact(&self, mut buffer: &mut [u8]) -> Result<()> {
        while !buffer.is_empty() {
            let count = self.read(buffer)? as usize;
            if count == 0 {
                return Err(Error::from_hresult(HRESULT::from_win32(ERROR_HANDLE_EOF.0)));
            }
            buffer = &mut buffer[count..];
        }
        Ok(())
    }

    fn read(&self, buffer: &mut [u8]) -> Result<u32> {
        if buffer.is_empty() {
            return Ok(0);
        }
        match self
            .transfer(|pipe, request| unsafe { ReadFile(pipe, Some(buffer), None, Some(request)) })
        {
            Err(error)
                if [
                    ERROR_BROKEN_PIPE,
                    ERROR_HANDLE_EOF,
                    ERROR_NO_DATA,
                    ERROR_PIPE_NOT_CONNECTED,
                ]
                .iter()
                .any(|code| error.code() == HRESULT::from_win32(code.0)) =>
            {
                Ok(0)
            }
            result => result,
        }
    }

    fn write(&self, mut buffer: &[u8]) -> Result<()> {
        while !buffer.is_empty() {
            let written = self.transfer(|pipe, request| unsafe {
                WriteFile(pipe, Some(buffer), None, Some(request))
            })? as usize;
            if written == 0 || written > buffer.len() {
                return Err(Error::from_hresult(HRESULT::from_win32(
                    ERROR_WRITE_FAULT.0,
                )));
            }
            buffer = &buffer[written..];
        }
        Ok(())
    }

    fn transfer(&self, issue: impl FnOnce(HANDLE, &mut OVERLAPPED) -> Result<()>) -> Result<u32> {
        let pipe = HANDLE(self.pipe.as_raw_handle());
        let stop = HANDLE(self.stop.as_raw_handle());
        let cancel = HANDLE(self.cancel.as_raw_handle());
        let cancelled = || Error::from_hresult(HRESULT::from_win32(ERROR_OPERATION_ABORTED.0));
        unsafe {
            match WaitForMultipleObjects(&[stop, cancel], false, 0) {
                WAIT_TIMEOUT => {}
                WAIT_FAILED => return Err(Error::from_win32()),
                _ => return Err(cancelled()),
            }
            let event =
                OwnedHandle::from_raw_handle(CreateEventW(None, true, false, PCWSTR::null())?.0);
            let mut request = OVERLAPPED {
                hEvent: HANDLE(event.as_raw_handle()),
                ..Default::default()
            };
            let mut transferred = 0;
            match issue(pipe, &mut request) {
                Ok(()) => {}
                Err(error) if error.code() == HRESULT::from_win32(ERROR_IO_PENDING.0) => {
                    let wait =
                        WaitForMultipleObjects(&[stop, cancel, request.hEvent], false, INFINITE);
                    if wait.0 != WAIT_OBJECT_0.0 + 2 {
                        let error = if wait == WAIT_FAILED {
                            Error::from_win32()
                        } else {
                            cancelled()
                        };
                        let _ = CancelIoEx(pipe, Some(&request));
                        // Keep the request, event and buffer live until Windows reports completion.
                        let _ = GetOverlappedResult(pipe, &request, &mut transferred, true);
                        return Err(error);
                    }
                }
                Err(error) => return Err(error),
            }
            GetOverlappedResult(pipe, &request, &mut transferred, false)?;
            Ok(transferred)
        }
    }
}
