//! Existing managed file/update tools run briefly on demand; they own no hardware runtime.
use serde_json::{json, Value};
use std::{
    io::{Read, Write},
    os::windows::{fs::MetadataExt, io::AsRawHandle, process::CommandExt},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::{CloseHandle, HANDLE},
        System::JobObjects::{
            AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
            SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        },
    },
};
struct Job(HANDLE);
impl Drop for Job {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}
fn job(child: &Child) -> Result<Job, String> {
    unsafe {
        let job = Job(CreateJobObjectW(None, PCWSTR::null()).map_err(|e| e.to_string())?);
        let limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
            BasicLimitInformation:
                windows::Win32::System::JobObjects::JOBOBJECT_BASIC_LIMIT_INFORMATION {
                    LimitFlags: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    ..Default::default()
                },
            ..Default::default()
        };
        SetInformationJobObject(
            job.0,
            JobObjectExtendedLimitInformation,
            &limits as *const _ as *const _,
            std::mem::size_of_val(&limits) as u32,
        )
        .map_err(|e| e.to_string())?;
        AssignProcessToJobObject(job.0, HANDLE(child.as_raw_handle()))
            .map_err(|e| e.to_string())?;
        Ok(job)
    }
}
fn frame(reader: &mut impl Read) -> Result<Value, String> {
    let mut size = [0; 4];
    reader.read_exact(&mut size).map_err(|e| e.to_string())?;
    let size = u32::from_le_bytes(size) as usize;
    if !(2..=7 * 1024 * 1024).contains(&size) {
        return Err("辅助响应超过边界".into());
    }
    let mut bytes = vec![0; size];
    reader.read_exact(&mut bytes).map_err(|e| e.to_string())?;
    serde_json::from_slice(&bytes).map_err(|e| e.to_string())
}
pub fn call(
    operation: &str,
    payload: Value,
    timeout: Option<Duration>,
    cancel: Option<&AtomicBool>,
    mut changed: impl FnMut(&Value),
) -> Result<Value, String> {
    let base = std::env::current_exe()
        .map_err(|e| e.to_string())?
        .parent()
        .ok_or("程序目录无效")?
        .to_owned();
    super::platform::validate_directory(&base)?;
    let path = base.join("OpenRevo.Tools.exe");
    let metadata = std::fs::symlink_metadata(&path).map_err(|e| e.to_string())?;
    if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
        return Err("辅助程序不是普通文件".into());
    }
    let bytes = serde_json::to_vec(&json!({"Operation":operation,"Payload":payload}))
        .map_err(|e| e.to_string())?;
    if bytes.len() > 1024 * 1024 {
        return Err("辅助请求超过边界".into());
    }
    let mut child = Command::new(path)
        .args(["--native-tools", &std::process::id().to_string()])
        .current_dir(base)
        .creation_flags(0x08000000)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| e.to_string())?;
    let owned_job = if operation == "update-install" {
        None
    } else {
        Some(match job(&child) {
            Ok(v) => v,
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(e);
            }
        })
    };
    let mut input = child.stdin.take().ok_or("缺少辅助输入")?;
    let input_writer = thread::Builder::new()
        .name("OpenRevo.Tools.Input".into())
        .spawn(move || {
            let _ = input
                .write_all(&(bytes.len() as u32).to_le_bytes())
                .and_then(|_| input.write_all(&bytes))
                .and_then(|_| input.flush());
        })
        .map_err(|e| e.to_string())?;
    let mut output = child.stdout.take().ok_or("缺少辅助输出")?;
    let (sender, receiver) = mpsc::sync_channel(16);
    let reader = thread::Builder::new()
        .name("OpenRevo.Tools.Reader".into())
        .spawn(move || loop {
            let result = frame(&mut output);
            let done = result.as_ref().is_ok_and(|v| v["Done"] == true) || result.is_err();
            if sender.send(result).is_err() || done {
                break;
            }
        })
        .map_err(|e| e.to_string())?;
    let started = Instant::now();
    let result = loop {
        if cancel.is_some_and(|c| c.load(Ordering::Acquire))
            || timeout.is_some_and(|t| started.elapsed() >= t)
        {
            break Err("辅助操作已取消或超时".into());
        }
        match receiver.recv_timeout(Duration::from_millis(100)) {
            Ok(Ok(v)) => {
                changed(&v);
                if v["Done"] == true {
                    break if let Some(error) = v["Error"].as_str() {
                        Err(error.to_owned())
                    } else {
                        Ok(v)
                    };
                }
            }
            Ok(Err(e)) => break Err(e),
            Err(mpsc::RecvTimeoutError::Disconnected) => break Err("辅助进程未返回完整结果".into()),
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    };
    drop(receiver);
    // No timer is used for transactional data migration/reset: the original operation settles.
    if child.try_wait().map_err(|e| e.to_string())?.is_none() {
        let end = Instant::now() + Duration::from_millis(500);
        while Instant::now() < end && child.try_wait().map_err(|e| e.to_string())?.is_none() {
            thread::sleep(Duration::from_millis(10));
        }
        if child.try_wait().map_err(|e| e.to_string())?.is_none() {
            let _ = child.kill();
        }
    }
    let _ = child.wait();
    drop(owned_job);
    if input_writer.is_finished() {
        let _ = input_writer.join();
    }
    if reader.is_finished() {
        let _ = reader.join();
    }
    result
}
pub fn context() -> Result<Value, String> {
    let result = call(
        "context",
        json!({}),
        Some(Duration::from_secs(30)),
        None,
        |_| {},
    )?;
    let value = result["Context"].clone();
    let directory = value["DataDirectory"]
        .as_str()
        .ok_or("辅助程序未返回已验证的数据目录")?;
    super::platform::validate_directory(std::path::Path::new(directory))?;
    if value["Installed"] != true {
        return Err("安装上下文未通过校验".into());
    }
    Ok(value)
}
