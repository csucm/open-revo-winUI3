//! Classic tuning belongs to the same session and actor as factory commands.
//! Pure draft validation precedes every write; an incomplete transaction retains
//! forced cooling and faults the session instead of continuing later writes.
use super::{read, ControlState, HardwareSession, Outcome, PerformanceState, Receipt};
use crate::native_admission as admission;
use crate::resident::{
    gpu::{GpuState, Offsets},
    platform::{ac_online, OperationLock},
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
#[derive(Clone, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub struct FanPoint {
    pub celsius: i32,
    pub percent: i32,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
pub struct TuningProfile {
    pub pl1: i32,
    pub pl2: i32,
    pub pl4: i32,
    pub temperature_enabled: bool,
    pub temperature_c: i32,
    pub gpu_super_performance: bool,
    pub gpu_boost_enabled: bool,
    pub gpu_boost_watts: i32,
    pub gpu_base_watts: i32,
    pub offsets: Option<Offsets>,
    pub custom_fans: bool,
    pub cpu_curve: Vec<FanPoint>,
    pub gpu_curve: Vec<FanPoint>,
    pub smooth_fans: bool,
    pub fan_step_ms: i32,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct TuningSnapshot {
    pub performance: PerformanceState,
    pub registers: [u8; 18],
    pub gpu: GpuState,
    pub tj_max: i32,
    pub amd: bool,
    pub cpu_tuning: bool,
    pub cpu_ceilings: Value,
}
impl TuningSnapshot {
    fn matches(&self, other: &Self) -> bool {
        self.performance.matches(&other.performance)
            && self.registers == other.registers
            && self.gpu == other.gpu
            && self.tj_max == other.tj_max
            && self.amd == other.amd
            && self.cpu_tuning == other.cpu_tuning
            && self.cpu_ceilings == other.cpu_ceilings
    }
    pub fn ceiling_range(&self, index: usize) -> (i32, i32, i32) {
        let (turbo1, turbo2, turbo4) = (
            i32::from(self.registers[8]),
            i32::from(self.registers[9]),
            i32::from(self.registers[10]),
        );
        let (minimum, default, maximum, configured) = match index {
            0 => (
                turbo1,
                turbo1 + 25,
                turbo1 + 50,
                self.cpu_ceilings["Pl1"].as_i64(),
            ),
            1 => (
                turbo2,
                turbo2 + 25,
                turbo2 + 50,
                self.cpu_ceilings["Pl2"].as_i64(),
            ),
            _ => (
                (turbo4 + 20).max(turbo2 + 20),
                (turbo4 + 50).min(255),
                turbo4 + 80,
                self.cpu_ceilings["Pl4"].as_i64(),
            ),
        };
        let max = maximum.min(if index == 2 { 255 } else { 235 });
        let min = minimum.min(max);
        let configured = configured
            .and_then(|v| i32::try_from(v).ok())
            .unwrap_or(default);
        (min, configured.clamp(min, max), max)
    }
    pub(super) fn limits(&self) -> (i32, i32, i32) {
        let peak = self.ceiling_range(2).1;
        let short = self.ceiling_range(1).1.min(peak - 20);
        (self.ceiling_range(0).1.min(short), short, peak)
    }
    pub(super) fn validate(&self, p: &TuningProfile, draft: bool) -> Result<(), String> {
        let (max1, max2, max4) = self.limits();
        if self.cpu_tuning
            && (p.pl1 < 15
                || p.pl1 > max1
                || p.pl2 < p.pl1
                || p.pl2 > max2
                || p.pl4 < p.pl2 + 20
                || p.pl4 > max4)
        {
            return Err("功耗须满足 PL1≤PL2、PL4≥PL2+20W，并处于机型范围".into());
        }
        if self.cpu_tuning && !(80..=95).contains(&p.temperature_c)
            || !(100..=12700).contains(&p.fan_step_ms)
            || p.fan_step_ms % 100 != 0
        {
            return Err("温度墙或风扇响应参数无效".into());
        }
        let caps = &self.gpu.capabilities;
        if caps.supported {
            if p.gpu_base_watts < caps.floor_watts
                || p.gpu_base_watts > caps.base_max_watts
                || !caps.configurable_base && p.gpu_base_watts != caps.base_max_watts
                || p.gpu_boost_watts < 5
                || p.gpu_boost_watts > caps.boost_max_watts
            {
                return Err("显卡功耗超出硬件能力".into());
            }
            if p.offsets != self.gpu.offsets {
                let requested = p.offsets.as_ref().ok_or("显卡频偏未确认")?;
                if self.gpu.offsets.is_none()
                    || requested.core_mhz < 0
                    || requested.core_mhz > caps.core_offset_max
                    || requested.memory_mhz < 0
                    || requested.memory_mhz > caps.memory_offset_max
                {
                    return Err("显卡频偏能力未确认或超出范围".into());
                }
            }
            if !draft
                && !p.gpu_super_performance
                && p.offsets
                    .as_ref()
                    .is_some_and(|o| o.core_mhz != 0 || o.memory_mhz != 0)
            {
                return Err("启用频偏需同时启用超级性能".into());
            }
        } else if p.offsets != self.gpu.offsets {
            return Err("显卡能力未知，禁止修改频偏".into());
        }
        validate_curve(&p.cpu_curve)?;
        validate_curve(&p.gpu_curve)
    }
}
fn validate_curve(curve: &[FanPoint]) -> Result<(), String> {
    if curve.len() != 8
        || curve
            .iter()
            .any(|p| !(30..=100).contains(&p.celsius) || !(0..=100).contains(&p.percent))
        || curve
            .windows(2)
            .any(|p| p[0].celsius >= p[1].celsius || p[0].percent > p[1].percent)
        || curve
            .last()
            .is_none_or(|p| p.celsius > 95 || p.percent != 100)
    {
        return Err("曲线须有八个递增温度点、非递减转速，在95度前达到100%".into());
    }
    Ok(())
}
fn encode_temperature(amd: bool, tj_max: i32, enabled: bool, target: i32, factory: u8) -> u8 {
    let baseline = i32::from(factory & 127);
    (if enabled {
        128 | if amd { target } else { tj_max - target }
    } else if amd {
        if (60..=105).contains(&baseline) {
            baseline
        } else {
            95
        }
    } else if (1..=30).contains(&baseline) {
        baseline
    } else {
        (tj_max - 95).max(5)
    }) as u8
}
fn tuning_fans(current: &PerformanceState, p: &TuningProfile) -> Result<PerformanceState, String> {
    let mut target = current.factory(3)?;
    if p.custom_fans {
        let up: Vec<u8> = (0..16)
            .map(|i| if i == 0 { 0 } else { 25 + 5 * i })
            .collect();
        let down: Vec<u8> = up
            .iter()
            .map(|t| {
                if *t == 0 {
                    0
                } else {
                    t - if *t <= 55 { 8 } else { 5 }
                }
            })
            .collect();
        let duty = |curve: &[FanPoint]| -> Vec<u8> {
            up.iter()
                .enumerate()
                .map(|(index, t)| {
                    let t = i32::from(*t);
                    if index == 0 {
                        return 0;
                    }
                    if t <= curve[0].celsius {
                        return (curve[0].percent * 2) as u8;
                    }
                    if t >= curve[7].celsius {
                        return (curve[7].percent * 2) as u8;
                    }
                    let i = curve.iter().position(|p| p.celsius >= t).unwrap_or(7);
                    let (a, b) = (&curve[i - 1], &curve[i]);
                    (2.0 * (f64::from(a.percent)
                        + f64::from(b.percent - a.percent) * f64::from(t - a.celsius)
                            / f64::from(b.celsius - a.celsius)))
                    .round() as u8
                })
                .collect()
        };
        target.tables = [
            up.clone(),
            down.clone(),
            duty(&p.cpu_curve),
            up.clone(),
            down,
            duty(&p.gpu_curve),
        ]
        .concat();
    }
    target.custom = current.custom | 1;
    target.isolated = (current.isolated & !128) | if p.custom_fans { 128 } else { 0 };
    target.ramp = 128
        | if p.smooth_fans {
            (p.fan_step_ms / 100) as u8
        } else {
            1
        };
    Ok(target)
}
impl HardwareSession {
    fn read_tuning(&self) -> Result<TuningSnapshot, String> {
        if !self.classic() || !self.supports("custom_tuning") {
            return Err("Core 未提供经典调校能力".into());
        }
        let cpu = self.admission["classic_cpu_tuning"] != false;
        let integrated = self.admission["discrete_gpu"] == false;
        let addresses = [
            1923, 1924, 1925, 1926, 1859, 1861, 1862, 1860, 1959, 1960, 1961, 2010, 1844, 1845,
            1846, 1840, 1841, 1842,
        ];
        let mut registers = [0; 18];
        for (index, address) in addresses.into_iter().enumerate() {
            if (4..8).contains(&index) {
                if !integrated {
                    registers[index] = read(address)?;
                }
            } else if cpu {
                registers[index] = read(address)?;
            }
        }
        let (turbo1, turbo2, turbo4) = (
            i32::from(registers[8]),
            i32::from(registers[9]),
            i32::from(registers[10]),
        );
        if cpu
            && (!(15..=235).contains(&turbo1)
                || turbo2 < 15
                || turbo4 < 35
                || turbo2 + 20 > (turbo4 + 50).min(255))
        {
            return Err("工厂功耗边界无效，未开放调校".into());
        }
        let bytes =
            openrevo_core::system::app_preferences::AppPreferences::load_snapshot(&self.root, true)
                .map_err(|_| "调校设置读取失败")?;
        let preferences: Value = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        Ok(TuningSnapshot {
            performance: PerformanceState::read()?,
            registers,
            gpu: if integrated {
                GpuState::integrated()
            } else {
                GpuState::probe()
            },
            tj_max: self.admission["tj_max"]
                .as_i64()
                .and_then(|v| i32::try_from(v).ok())
                .filter(|v| (80..=127).contains(v))
                .ok_or("CPU 温度编码未知")?,
            amd: self.admission["cpu_encoding"] == "amd_celsius",
            cpu_tuning: cpu,
            cpu_ceilings: if cpu {
                preferences["CpuPowerCeilings"].clone()
            } else {
                Value::Null
            },
        })
    }
    pub fn probe_tuning(&mut self) -> Result<(String, TuningSnapshot), String> {
        let _lock = OperationLock::enter()?;
        self.guard()?;
        if Some(u64::from(ControlState::read()?.project)) != self.admission["project"].as_u64() {
            return Err("EC 项目不匹配".into());
        }
        let snapshot = self.read_tuning();
        openrevo_core::driver::nvapi::release_session();
        let snapshot = snapshot?;
        let id =
            unsafe { windows::Win32::System::Com::CoCreateGuid() }.map_err(|e| e.to_string())?;
        let id = format!("{id:?}");
        self.tuning_baseline = Some((id.clone(), snapshot.clone()));
        Ok((id, snapshot))
    }
    pub fn apply_tuning(&mut self, id: &str, mut profile: TuningProfile) -> Receipt {
        let mut attempted = false;
        let mut warning = None;
        let result = (|| {
            let _lock = OperationLock::enter()?;
            self.ready("custom_tuning")?;
            let baseline = self
                .tuning_baseline
                .as_ref()
                .filter(|(baseline, _)| baseline.eq_ignore_ascii_case(id))
                .map(|(_, s)| s.clone())
                .ok_or("调校基线已失效，请重新读取")?;
            let root = ControlState::read()?;
            self.check_root(&root)?;
            let before = self.read_tuning()?;
            if !before.matches(&baseline) {
                return Err("硬件或调校范围已变化，请重新读取".into());
            }
            let replace_oem = self.supports("performance")
                && before.performance.custom == 64
                && before.performance.isolated & 128 == 0;
            if !replace_oem && !before.performance.recognized(false) {
                return Err("当前风扇协议未适配".into());
            }
            before.validate(&profile, true)?;
            if before.gpu.capabilities.supported
                && !profile.gpu_super_performance
                && profile.offsets.is_some()
            {
                profile.offsets = Some(Offsets {
                    core_mhz: 0,
                    memory_mhz: 0,
                });
            }
            before.validate(&profile, false)?;
            self.check_tuning_power()?;
            let mut target = tuning_fans(&before.performance, &profile)?;
            if !target.recognized(false) {
                return Err("目标不是允许的自定义风扇协议".into());
            }
            let caps = &before.gpu.capabilities;
            let old = &before.performance;
            target.clamp = (old.clamp & !176)
                | if caps.supported {
                    if profile.gpu_super_performance {
                        16
                    } else {
                        0
                    }
                } else {
                    old.clamp & 16
                };
            target.trigger = (old.trigger & !8) | 32;
            let next_id = unsafe { windows::Win32::System::Com::CoCreateGuid() }
                .map_err(|e| e.to_string())?;
            let next_id = format!("{next_id:?}");
            self.audit(
                "custom-tuning-intent",
                json!({"before":before,"profile":profile}),
            )?;
            self.check_tuning_power()?;
            attempted = true;
            self.controls(
                root.project,
                &[
                    (1873, old.clamp | 64, 0),
                    (1798, old.custom & !1, 0),
                    (1990, 4, 0),
                    (1990, 0, 0),
                    (1857, target.ap, 0),
                ],
            )?;
            self.guard()?;
            admission::apply_classic_transaction(
                json!({"kind":"fan_tables","project":root.project,"factory":false,"values":target.tables}),
            )?;
            self.check_tuning_power()?;
            let mut steps = Vec::new();
            let mut tcc = 0;
            if before.cpu_tuning {
                tcc = encode_temperature(
                    before.amd,
                    before.tj_max,
                    profile.temperature_enabled,
                    profile.temperature_c,
                    before.registers[11],
                );
                steps.extend([
                    (1923, profile.pl1 as u8, 0),
                    (1924, profile.pl2 as u8, 0),
                    (1925, profile.pl4 as u8, 0),
                    (1926, tcc, 0),
                ]);
            }
            let boost = if profile.gpu_super_performance {
                caps.boost_max_watts
            } else if profile.gpu_boost_enabled {
                profile.gpu_boost_watts
            } else {
                0
            };
            let db = (before.registers[4] & !7)
                | if profile.gpu_super_performance || profile.gpu_boost_enabled {
                    7
                } else {
                    5
                };
            let offset = if caps.configurable_base {
                (if profile.gpu_super_performance {
                    caps.base_max_watts
                } else {
                    profile.gpu_base_watts
                }) - caps.floor_watts
            } else {
                i32::from(before.registers[7])
            };
            if caps.supported {
                steps.extend([(1859, db, 0), (1861, 255, 0)]);
                if caps.configurable_base {
                    steps.push((1860, offset as u8, 0));
                }
                steps.push((1862, boost as u8, 0));
            }
            steps.extend([
                (1989, target.isolated, 0),
                (1927, target.ramp, 0),
                (1963, target.index, 0),
                (1115, target.silent, 1),
                (1895, target.trigger, 40),
                (1798, target.custom, 0),
                (1990, target.latch, 0),
                (1873, target.clamp | 64, 0),
            ]);
            self.controls(root.project, &steps)?;
            if caps.supported && profile.offsets != before.gpu.offsets {
                self.check_tuning_power()?;
                let expected = before.gpu.offsets.as_ref().ok_or("显卡原始频偏未知")?;
                let desired = profile.offsets.as_ref().ok_or("显卡目标频偏未知")?;
                let code = crate::or_native_clock_offsets_set(
                    expected.core_mhz,
                    expected.memory_mhz,
                    desired.core_mhz,
                    desired.memory_mhz,
                );
                if code != 0 {
                    return Err(format!("显卡频偏回读未确认 ({code})"));
                }
            }
            self.check_tuning_power()?;
            self.controls(root.project, &[(1873, target.clamp, 0)])?;
            let after = self.read_tuning()?;
            let r = &after.registers;
            if !target.matches(&after.performance)
                || before.cpu_tuning
                    && (r[0] != profile.pl1 as u8
                        || r[1] != profile.pl2 as u8
                        || r[2] != profile.pl4 as u8
                        || r[3] != tcc)
                || caps.supported
                    && (r[4] != db
                        || r[5] != 255
                        || i32::from(r[6]) != boost
                        || i32::from(r[7]) != offset
                        || after.gpu.offsets != profile.offsets)
            {
                return Err("最终调校状态与草稿不一致".into());
            }
            self.state = Some(ControlState::read()?);
            self.performance = Some(after.performance.clone());
            self.mode = Some(after.performance.mode(self.state.as_ref()));
            self.tuning_baseline = Some((next_id, after.clone()));
            self.audit(
                "custom-tuning-confirmed",
                json!({"before":before,"after":after,"profile":profile}),
            )?;
            if self.synchronize_indicator() != 0 {
                warning = Some("调校已确认，模式指示灯未同步".into());
            }
            Ok(Outcome::ReadbackConfirmed)
        })();
        if attempted && result.is_err() {
            // Follow the existing failure recovery: keep forced cooling only when
            // identity and environment remain admitted; never continue the draft.
            let mut cooling = false;
            if self.guard().is_ok() {
                if let Ok(current) = read(1873) {
                    let desired = current | 64;
                    cooling =
                        admission::or_native_ec_write(1873, u32::from(current), u32::from(desired))
                            == 0
                            && read(1873) == Ok(desired);
                }
            }
            let _ = self.audit("custom-tuning-stopped", json!({"coolingConfirmed":cooling}));
            self.tuning_baseline = None;
        }
        let mut receipt = self.finish(result, attempted);
        receipt.warning = warning;
        openrevo_core::driver::nvapi::release_session();
        if matches!(receipt.outcome, Outcome::ReadbackConfirmed) && receipt.mode == Some(4) {
            self.remember_factory_mode(None, &mut receipt);
        }
        receipt
    }
    fn check_tuning_power(&self) -> Result<(), String> {
        self.guard()?;
        let power = read(1996)?;
        if !self.allow_battery && (ac_online() != Some(true) || power & 62 != 0 && power & 1 == 0) {
            return Err("极客调校需要原装适配器交流供电".into());
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cpu_temperature_encoding_keeps_platform_contract() {
        assert_eq!(encode_temperature(true, 100, true, 90, 95), 218);
        assert_eq!(encode_temperature(false, 100, true, 90, 10), 138);
        assert_eq!(encode_temperature(false, 100, false, 90, 0), 5);
        assert_eq!(encode_temperature(true, 100, false, 90, 0), 95);
    }
    #[test]
    fn invalid_curve_is_rejected_before_hardware() {
        let curve = vec![
            FanPoint {
                celsius: 50,
                percent: 50
            };
            8
        ];
        assert!(validate_curve(&curve).is_err());
    }
}
