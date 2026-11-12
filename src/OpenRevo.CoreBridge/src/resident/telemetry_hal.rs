//! Physical power targets, not editor slider values. Reads only on overview demand.
use super::{read, HardwareSession, PerformanceState};
use crate::resident::platform::OperationLock;
use serde_json::{json, Value};
impl HardwareSession {
    pub fn overview_power(&self) -> Result<Value, String> {
        let _lock = OperationLock::enter()?;
        self.guard()?;
        let at = crate::resident::panel_wire::utc_iso(
            u64::try_from(crate::resident::platform::unix_ms()).unwrap_or(0),
        );
        if !self.classic() {
            let mode = crate::native_admission::declared_mode().unwrap_or(0);
            let key = match mode {
                1 => "office",
                2 => "balance",
                3 => "turbo",
                _ => "",
            };
            let pl1 = self.admission["power_mode_watts"][key]
                .as_u64()
                .filter(|v| *v > 0);
            return Ok(
                json!({"Mode":mode,"Pl1Watts":pl1,"TgpWatts":Value::Null,"BaseTgpWatts":Value::Null,"MaximumBoostWatts":Value::Null,"Detail":if pl1.is_some(){"PL1目标来自原Core机型声明"}else{"当前模式无Core原厂PL1声明"},"At":at}),
            );
        }
        let project = read(1856)?;
        if Some(u64::from(project)) != self.admission["project"].as_u64() {
            return Err("EC项目不匹配，未读取原厂目标".into());
        }
        let platform = read(1987)?;
        let (custom, clamp, index, isolated) = (read(1798)?, read(1873)?, read(1963)?, read(1989)?);
        // The OEM decoder has a narrower readout contract than classic fallback.
        // Unknown OEM tuples must not be shown as a confirmed turbo target.
        let mode = if custom == 0 && crate::resident::platform::gm7tg0m_identity() {
            if (project, platform, index) == (16, 7, 16) {
                match (clamp & !64, isolated) {
                    (160, 0) => 1,
                    (0, 0 | 128) => 2,
                    (16, 0) => 3,
                    _ => 0,
                }
            } else {
                0
            }
        } else if matches!(custom, 64 | 65) {
            PerformanceState::decode_mode(custom, clamp, index, isolated, Some((project, platform)))
        } else {
            0
        };
        let address = match mode {
            1 => Some(1844),
            2 => Some(1840),
            3 => Some(1959),
            4 => Some(1923),
            _ => None,
        };
        let pl1 = address
            .and_then(|a| read(a).ok())
            .filter(|v| *v > 0 && *v < 255);
        let gpu = (mode != 0).then(|| {
            let base_offset = (mode == 4).then(|| read(1860).ok()).flatten();
            let boost_on = (mode == 4)
                .then(|| read(1859).ok().map(|control| control & 2 != 0))
                .flatten();
            let boost_watts = if boost_on == Some(true) {
                read(1862).ok()
            } else {
                None
            };
            json!({"BaseOffset":base_offset,"BoostOn":boost_on,"BoostWatts":boost_watts})
        });
        if (custom, clamp, index, isolated, project, platform)
            != (
                read(1798)?,
                read(1873)?,
                read(1963)?,
                read(1989)?,
                read(1856)?,
                read(1987)?,
            )
        {
            return Err("读取期间性能模式或平台改变，等待重新读取".into());
        }
        Ok(
            json!({"Mode":mode,"Pl1Watts":pl1,"TgpWatts":Value::Null,"BaseTgpWatts":Value::Null,"MaximumBoostWatts":Value::Null,"GpuTarget":gpu,"Detail":"PL1来自本机EC，TGP按实际NVML功耗边界与当前EC计算；没有使用编辑草稿。未受理GPU采样时不探测NVML。","At":at}),
        )
    }
}
