//! On-demand telemetry for native frontends. No worker, EC write, config load,
//! subprocess or synthetic startup value. Counters and units match CpuMonitor;
//! its fallback power model is shared and explicitly identified as estimated.
use libloading::Library;
use serde::Serialize;
use std::{ffi::c_void,ptr};

#[derive(Default,Clone,Serialize)]
pub struct CpuSample {pub mhz:Option<u32>,pub usage:Option<u32>,pub power_w:Option<f32>,pub power_estimated:bool}
#[derive(Default,Clone,Serialize)]
pub struct GpuSample {
    pub temperature:Option<u32>,pub core_mhz:Option<u32>,pub memory_mhz:Option<u32>,
    pub usage:Option<u32>,pub memory_total_bytes:Option<u64>,pub memory_used_bytes:Option<u64>,pub power_w:Option<f32>,
}
#[repr(C)]#[derive(Default,Clone,Copy)]struct FileTime{low:u32,high:u32}
impl FileTime{fn number(self)->u64{(self.high as u64)<<32|self.low as u64}}
#[repr(C)]#[derive(Default,Clone,Copy)]struct ProcessorInfo{number:u32,max_mhz:u32,current_mhz:u32,limit:u32,max_idle:u32,current_idle:u32}
#[repr(C)]#[derive(Clone,Copy)]struct CounterValue{status:u32,value:f64}
#[repr(C)]#[derive(Clone,Copy)]struct CounterItem{name:*const u16,value:CounterValue}
type Open=unsafe extern "system" fn(*const u16,usize,*mut isize)->i32;
type Add=unsafe extern "system" fn(isize,*const u16,usize,*mut isize)->i32;
type Collect=unsafe extern "system" fn(isize)->i32;
type Array=unsafe extern "system" fn(isize,u32,*mut u32,*mut u32,*mut u8)->i32;
type Value=unsafe extern "system" fn(isize,u32,*mut u32,*mut CounterValue)->i32;
type Close=unsafe extern "system" fn(isize)->i32;
unsafe fn symbol<T:Copy>(library:&Library,name:&[u8])->Option<T>{library.get::<T>(name).ok().map(|s|*s)}

pub struct CpuReader {pdh:Option<Library>,query:isize,power:isize,performance:isize,previous:Option<(u64,u64,u64)>}
impl Default for CpuReader {fn default()->Self{Self{pdh:None,query:0,power:0,performance:0,previous:None}}}
impl CpuReader {
    pub fn read(&mut self)->CpuSample {
        let mut sample=CpuSample::default();let mut base=0;
        unsafe {
            if let Ok(kernel)=Library::new("kernel32.dll") {
                type Times=unsafe extern "system" fn(*mut FileTime,*mut FileTime,*mut FileTime)->i32;
                if let Some(times)=symbol::<Times>(&kernel,b"GetSystemTimes") {
                    let(mut idle,mut system,mut user)=(FileTime::default(),FileTime::default(),FileTime::default());
                    if times(&mut idle,&mut system,&mut user)!=0 {
                        let current=(idle.number(),system.number(),user.number());
                        if let Some(old)=self.previous {
                            let total=current.1.saturating_sub(old.1)+current.2.saturating_sub(old.2);
                            if total>0 {sample.usage=Some(((total.saturating_sub(current.0.saturating_sub(old.0)) as f64/total as f64)*100.0).clamp(0.,100.) as u32);}
                        }
                        self.previous=Some(current);
                    }
                }
            }
            if let Ok(power)=Library::new("powrprof.dll") {
                type Information=unsafe extern "system" fn(i32,*const c_void,u32,*mut c_void,u32)->i32;
                if let Some(info)=symbol::<Information>(&power,b"CallNtPowerInformation") {
                    let mut processors=[ProcessorInfo::default();128];
                    if info(11,ptr::null(),0,processors.as_mut_ptr().cast(),std::mem::size_of_val(&processors) as u32)==0 {
                        let valid:Vec<_>=processors.iter().filter(|p|p.current_mhz>0).collect();
                        if !valid.is_empty(){sample.mhz=Some((valid.iter().map(|p|p.current_mhz as u64).sum::<u64>()/valid.len() as u64)as u32);base=valid[0].max_mhz;}
                    }
                }
            }
            if self.pdh.is_none() {
                self.pdh=Library::new("pdh.dll").ok();
                if let Some(lib)=self.pdh.as_ref() {
                    if let (Some(open),Some(add))=(symbol::<Open>(lib,b"PdhOpenQueryW"),symbol::<Add>(lib,b"PdhAddEnglishCounterW")) {
                        if open(ptr::null(),0,&mut self.query)==0 {
                            let path=|s:&str|s.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
                            if add(self.query,path(r"\Energy Meter(*)\Power").as_ptr(),0,&mut self.power)!=0 {self.power=0;}
                            if add(self.query,path(r"\Processor Information(_Total)\% Processor Performance").as_ptr(),0,&mut self.performance)!=0 {self.performance=0;}
                            if let Some(collect)=symbol::<Collect>(lib,b"PdhCollectQueryData"){collect(self.query);}
                        }
                    }
                }
            }
            if let Some(lib)=self.pdh.as_ref() {
                if self.query!=0&&symbol::<Collect>(lib,b"PdhCollectQueryData").map(|collect|collect(self.query)==0)==Some(true) {
                    if self.performance!=0&&base>0 {
                        if let Some(get)=symbol::<Value>(lib,b"PdhGetFormattedCounterValue") {
                            let mut value=CounterValue{status:u32::MAX,value:0.};
                            if get(self.performance,0x200,ptr::null_mut(),&mut value)==0&&value.status<=1&&value.value.is_finite()&&value.value>0. {
                                sample.mhz=Some((base as f64*value.value/100.).clamp(1.,10000.).round()as u32);
                            }
                        }
                    }
                    if self.power!=0 {
                        if let Some(get)=symbol::<Array>(lib,b"PdhGetFormattedCounterArrayW") {
                            let(mut bytes,mut count)=(0,0);get(self.power,0x200,&mut bytes,&mut count,ptr::null_mut());
                            if bytes>0&&bytes<=1024*1024 {
                                let mut buffer=vec![0u8;bytes as usize];
                                if get(self.power,0x200,&mut bytes,&mut count,buffer.as_mut_ptr())==0 {
                                    let mut maximum=0f64;let mut package=None;
                                    for i in 0..count as usize {
                                        let offset=i*std::mem::size_of::<CounterItem>();if offset+std::mem::size_of::<CounterItem>()>buffer.len(){break;}
                                        let item=ptr::read_unaligned(buffer.as_ptr().add(offset).cast::<CounterItem>());
                                        let mw=item.value.value;if item.value.status>1||!mw.is_finite()||mw<=0.||mw>400000.{continue;}
                                        maximum=maximum.max(mw);
                                        if !item.name.is_null(){let mut len=0;while len<256&&*item.name.add(len)!=0{len+=1;}
                                            let name=String::from_utf16_lossy(std::slice::from_raw_parts(item.name,len)).to_lowercase();
                                            if name.contains("package")||name.contains("pkg") {package=Some(mw);}
                                        }
                                    }
                                    let mw=package.unwrap_or(maximum);if mw>0.{sample.power_w=Some((mw/1000.)as f32);}
                                }
                            }
                        }
                    }
                }
            }
        }
        if sample.power_w.is_none(){if let(Some(usage),Some(mhz))=(sample.usage,sample.mhz){sample.power_w=Some(super::cpu_monitor::estimate_package_power_w(usage,mhz,base));sample.power_estimated=true;}}
        sample
    }
}
impl Drop for CpuReader {fn drop(&mut self){if self.query!=0{if let Some(lib)=&self.pdh{unsafe{if let Some(close)=symbol::<Close>(lib,b"PdhCloseQuery"){close(self.query);}}}}}}

#[repr(C)]#[derive(Default)]struct Memory{total:u64,free:u64,used:u64}
#[repr(C)]#[derive(Default)]struct Utilization{gpu:u32,memory:u32}
/// NVML uses the same NVIDIA driver readings/units as upstream nvidia-smi,
/// without launching its permanent poller or returning its startup placeholders.
pub fn read_gpu()->GpuSample {
    let mut sample=GpuSample::default();
    if !crate::driver::gpu_bridge::is_nvidia_dgpu_available(){return sample;}
    let path=match std::env::var_os("SystemRoot"){Some(root)=>std::path::PathBuf::from(root).join("System32/nvml.dll"),None=>return sample};
    unsafe {
        let lib=match Library::new(path){Ok(lib)=>lib,Err(_)=>return sample};
        type Init=unsafe extern "C" fn()->i32;type Handle=unsafe extern "C" fn(u32,*mut *mut c_void)->i32;
        type Scalar=unsafe extern "C" fn(*mut c_void,*mut u32)->i32;type Clock=unsafe extern "C" fn(*mut c_void,u32,*mut u32)->i32;
        let(init,shutdown,handle)=match(symbol::<Init>(&lib,b"nvmlInit_v2"),symbol::<Init>(&lib,b"nvmlShutdown"),symbol::<Handle>(&lib,b"nvmlDeviceGetHandleByIndex_v2")){(Some(i),Some(s),Some(h))=>(i,s,h),_=>return sample};
        if init()!=0{return sample;}
        struct Guard(Init);impl Drop for Guard{fn drop(&mut self){unsafe{(self.0)();}}}let _guard=Guard(shutdown);
        let mut device=ptr::null_mut();if handle(0,&mut device)!=0{return sample;}
        if let Some(clock)=symbol::<Clock>(&lib,b"nvmlDeviceGetClockInfo"){
            let mut v=0;if clock(device,0,&mut v)==0&&v<=10000 {sample.core_mhz=Some(v);}
            if clock(device,2,&mut v)==0&&v<=30000 {sample.memory_mhz=Some(v);}
        }
        if let Some(temp)=symbol::<Clock>(&lib,b"nvmlDeviceGetTemperature"){let mut v=0;if temp(device,0,&mut v)==0&&v>0&&v<125{sample.temperature=Some(v);}}
        if let Some(power)=symbol::<Scalar>(&lib,b"nvmlDeviceGetPowerUsage"){let mut v=0;if power(device,&mut v)==0&&v<1000000{sample.power_w=Some(v as f32/1000.);}}
        type GetMemory=unsafe extern "C" fn(*mut c_void,*mut Memory)->i32;
        if let Some(memory)=symbol::<GetMemory>(&lib,b"nvmlDeviceGetMemoryInfo"){let mut v=Memory::default();if memory(device,&mut v)==0&&v.total>0&&v.used<=v.total{sample.memory_total_bytes=Some(v.total);sample.memory_used_bytes=Some(v.used);}}
        type GetUsage=unsafe extern "C" fn(*mut c_void,*mut Utilization)->i32;
        if let Some(usage)=symbol::<GetUsage>(&lib,b"nvmlDeviceGetUtilizationRates"){let mut v=Utilization::default();if usage(device,&mut v)==0&&v.gpu<=100{sample.usage=Some(v.gpu);}}
    }
    sample
}
