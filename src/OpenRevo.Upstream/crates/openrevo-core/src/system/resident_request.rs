use serde::{de::IgnoredAny, Deserialize};

pub const PANEL_REVISION: i32 = 15;
const SELECT_MODE: i32 = 3;
const SET_BATTERY_LIMIT: i32 = 4;
const DETACH: i32 = 7;
const SELECT_GPU: i32 = 9;
const SET_BRIGHTNESS: i32 = 19;
const BEGIN_REFRESH: i32 = 20;
const KEEP_REFRESH: i32 = 21;
const RESTORE_REFRESH: i32 = 22;
const SET_OSD_ENABLED: i32 = 25;
const SET_OSD_APPEARANCE: i32 = 26;
const DESKTOP: i32 = 27;
const PLUGINS: i32 = 28;
const PREFERENCES: i32 = 29;
const LAYOUT: i32 = 30;
const EXIT_RESIDENT: i32 = 31;
const SET_DECLARED_FN_LOCK: i32 = 32;
const PRESENTATION_DEMAND: i32 = 33;
const TUNING: i32 = 34;
const MAINTENANCE: i32 = 35;
const APPLICATION: i32 = 36;
pub const MAX_PRESENTATION_DEMAND: i32 = 5;

#[derive(Clone, Copy, Default)]
#[repr(u32)]
pub enum RequestRoute {
    #[default]
    Dispatch = 0,
    Detach = 1,
    Exit = 2,
}

#[derive(Default)]
#[repr(C)]
pub struct RequestAdmission {
    pub id: i64,
    pub operation: i32,
    pub route: RequestRoute,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct RequestHeader {
    version: i32,
    id: i64,
    operation: i32,
    value: Option<i32>,
    expected: Option<i32>,
    observed_wake: Option<i64>,
    demand: Option<i32>,
    // Detailed contents are still decoded by the paired protocol model.
    draft: Option<IgnoredAny>,
    trial_id: Option<IgnoredAny>,
    osd_appearance: Option<IgnoredAny>,
    desktop: Option<IgnoredAny>,
    plugin: Option<IgnoredAny>,
    preference: Option<IgnoredAny>,
    layout: Option<IgnoredAny>,
    tuning: Option<IgnoredAny>,
    maintenance: Option<IgnoredAny>,
    application: Option<IgnoredAny>,
}

impl RequestHeader {
    fn valid_arguments(&self) -> bool {
        let operation = self.operation;
        let bodies = [
            (DETACH, self.draft.is_some()),
            (PRESENTATION_DEMAND, self.demand.is_some()),
            (SET_OSD_APPEARANCE, self.osd_appearance.is_some()),
            (DESKTOP, self.desktop.is_some()),
            (PLUGINS, self.plugin.is_some()),
            (PREFERENCES, self.preference.is_some()),
            (LAYOUT, self.layout.is_some()),
            (TUNING, self.tuning.is_some()),
            (MAINTENANCE, self.maintenance.is_some()),
            (APPLICATION, self.application.is_some()),
        ];
        if !bodies
            .into_iter()
            .all(|(kind, present)| (operation == kind) == present)
            || self.trial_id.is_some() != matches!(operation, KEEP_REFRESH | RESTORE_REFRESH)
            || self.observed_wake.is_some() != (operation == DETACH)
            || self.observed_wake.is_some_and(|wake| wake < 0)
            || self
                .demand
                .is_some_and(|demand| !(0..=MAX_PRESENTATION_DEMAND).contains(&demand))
        {
            return false;
        }

        match (operation, self.value, self.expected) {
            (SELECT_MODE, Some(1..=4), None)
            | (SELECT_GPU, Some(1..=3), Some(1..=3))
            | (SET_BATTERY_LIMIT, Some(60 | 80 | 100), None)
            | (SET_BRIGHTNESS, Some(10..=100), None)
            | (SET_OSD_ENABLED | SET_DECLARED_FN_LOCK, Some(0 | 1), None) => true,
            (BEGIN_REFRESH, Some(target), Some(current)) => {
                target > 1 && current > 1 && target != current
            }
            (
                SELECT_MODE | SELECT_GPU | SET_BATTERY_LIMIT | SET_BRIGHTNESS | SET_OSD_ENABLED
                | SET_DECLARED_FN_LOCK | BEGIN_REFRESH,
                _,
                _,
            ) => false,
            (_, None, None) => true,
            _ => false,
        }
    }
}

impl RequestAdmission {
    /// Check the envelope before reading uploads or dispatching an operation.
    /// The caller advances the sequence only after validating operation fields.
    pub fn decode_after(frame: &[u8], previous_id: i64) -> Option<Self> {
        if frame.iter().find(|byte| !byte.is_ascii_whitespace()) != Some(&b'{') {
            return None;
        }
        let header: RequestHeader = serde_json::from_slice(frame).ok()?;
        if header.version != PANEL_REVISION
            || header.id <= previous_id
            || header.id <= 0
            || !header.valid_arguments()
        {
            return None;
        }
        let route = match header.operation {
            DETACH => RequestRoute::Detach,
            EXIT_RESIDENT => RequestRoute::Exit,
            1..=APPLICATION => RequestRoute::Dispatch,
            _ => return None,
        };
        Some(Self {
            id: header.id,
            operation: header.operation,
            route,
        })
    }
}
