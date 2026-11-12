//! Native layered OSD. The three panel styles share text and layout but use
//! different per-pixel alpha, geometry and icon treatments.
use serde_json::Value;
use std::{cell::RefCell, mem::size_of, time::Instant};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{
            COLORREF, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM,
        },
        Graphics::Gdi::*,
        System::LibraryLoader::GetModuleHandleW,
        UI::{HiDpi::GetDpiForWindow, WindowsAndMessaging::*},
    },
};
const ICON_SIDE: usize = 48;
type Icon = &'static [u8; ICON_SIDE * ICON_SIDE * 4];

macro_rules! icon {
    ($name:literal) => {
        include_bytes!(concat!("osd-icons/", $name, ".rgba"))
    };
    ($name:literal, $on:expr) => {
        if $on {
            icon!($name)
        } else {
            include_bytes!(concat!("osd-icons/", $name, "-off.rgba"))
        }
    };
}
pub const DISPLAY_ICON: Icon = icon!("display");

#[derive(Clone)]
pub struct Message {
    pub text: String,
    pub icon: Icon,
    pub accent: u32,
    pub dot: Option<u32>,
    pub steps: Option<i32>,
}
impl Message {
    pub fn feature(feature: &str, on: bool) -> Self {
        let blue = 0xF8BD38;
        let neutral = 0xB8A394;
        let red = 0x5E3FF4;
        let green = 0x81B910;
        let (label, accent, dot) = match feature {
            "fan_boost" => (
                format!("一键强冷 {}", if on { "开启" } else { "关闭" }),
                if on { 0xD4B606 } else { neutral },
                if on { green } else { neutral },
            ),
            "keep_awake" => (
                format!("防休眠 {}", if on { "开启" } else { "关闭" }),
                if on { blue } else { neutral },
                if on { green } else { neutral },
            ),
            "caps_lock" => (
                if on { "Caps已锁定" } else { "Caps已解锁" }.into(),
                if on { blue } else { neutral },
                if on { green } else { neutral },
            ),
            "camera" => (
                format!("相机 {}", if on { "开启" } else { "关闭" }),
                if on { green } else { neutral },
                if on { green } else { neutral },
            ),
            "mic" => (
                format!("麦克风 {}", if on { "开启" } else { "静音" }),
                if on { green } else { red },
                if on { green } else { red },
            ),
            "win_key" => (
                format!("Win键 {}", if on { "锁定" } else { "解锁" }),
                if on { 0xC5A6EF } else { neutral },
                if on { red } else { green },
            ),
            "wifi" | "bluetooth" | "touchpad" | "fn_lock" => {
                let name = match feature {
                    "wifi" => "Wi-Fi",
                    "bluetooth" => "蓝牙",
                    "touchpad" => "触控板",
                    _ => "Fn锁",
                };
                (
                    format!("{name} {}", if on { "开启" } else { "关闭" }),
                    if on { blue } else { neutral },
                    if on { green } else { neutral },
                )
            }
            _ => (feature.into(), blue, neutral),
        };
        Self {
            text: label,
            icon: match feature {
                "camera" => icon!("camera", on),
                "mic" => icon!("mic", on),
                "wifi" => icon!("wifi", on),
                "bluetooth" => icon!("bluetooth", on),
                "fan_boost" => icon!("fan", on),
                "keep_awake" => icon!("awake", on),
                "touchpad" => icon!("touchpad", on),
                "win_key" => icon!("win-lock", on),
                "fn_lock" => icon!("fn-lock", on),
                "caps_lock" => icon!("caps", on),
                _ => icon!("info"),
            },
            accent,
            dot: Some(dot),
            steps: None,
        }
    }
    pub fn mode(mode: u8) -> Self {
        let (text, accent) = match mode {
            1 => ("办公模式", 0x81B910),
            2 => ("均衡模式", 0xF6823B),
            3 => ("狂暴模式", 0x5E3FF4),
            4 => ("极客调校", 0xF16663),
            _ => ("性能模式待读取", 0xB8A394),
        };
        Self {
            text: text.into(),
            icon: match mode {
                1 => icon!("mode-office"),
                2 => icon!("mode-balanced"),
                3 => icon!("mode-performance"),
                4 => icon!("mode-custom"),
                _ => icon!("info"),
            },
            accent,
            dot: None,
            steps: None,
        }
    }
}
struct State {
    window: HWND,
    font: HFONT,
    monitor: HMONITOR,
    message: Option<Message>,
    bounds: RECT,
    padding: i32,
    badge: i32,
    surface: Option<Surface>,
    enter: f64,
    leave: f64,
    started: Instant,
}
impl Default for State {
    fn default() -> Self {
        Self {
            window: HWND::default(),
            font: HFONT::default(),
            monitor: HMONITOR::default(),
            message: None,
            bounds: RECT::default(),
            padding: 0,
            badge: 0,
            surface: None,
            enter: 0.0,
            leave: 0.0,
            started: Instant::now(),
        }
    }
}
pub struct Osd {
    state: Box<RefCell<State>>,
    instance: HINSTANCE,
    registered: bool,
}
impl Osd {
    pub fn new() -> Result<Self, String> {
        let instance = HINSTANCE(
            unsafe { GetModuleHandleW(PCWSTR::null()) }
                .map_err(|e| e.to_string())?
                .0,
        );
        Ok(Self {
            state: Box::new(RefCell::new(State::default())),
            instance,
            registered: false,
        })
    }
    pub fn show(&mut self, message: Message, settings: &Value) -> Result<(), String> {
        unsafe {
            let monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY);
            let mut info = MONITORINFO {
                cbSize: size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if !GetMonitorInfoW(monitor, &mut info).as_bool() {
                return Err("屏幕提示工作区不可用".into());
            }
            let area = info.rcWork;
            if self.state.borrow().window.0.is_null() {
                if !self.registered {
                    let class = WNDCLASSEXW {
                        cbSize: size_of::<WNDCLASSEXW>() as u32,
                        hInstance: self.instance,
                        lpfnWndProc: Some(procedure),
                        lpszClassName: w!("OpenRevo.RevoCore.ResidentOsd"),
                        ..Default::default()
                    };
                    if RegisterClassExW(&class) == 0 {
                        return Err("无法注册屏幕提示窗口".into());
                    }
                    self.registered = true;
                }
                let pointer = (&*self.state as *const RefCell<State>).cast();
                let window = CreateWindowExW(
                    WINDOW_EX_STYLE(0x080800A0),
                    w!("OpenRevo.RevoCore.ResidentOsd"),
                    w!("Open-Revo OSD"),
                    WS_POPUP,
                    area.left,
                    area.top,
                    1,
                    1,
                    HWND::default(),
                    HMENU::default(),
                    self.instance,
                    Some(pointer),
                )
                .map_err(|e| e.to_string())?;
                self.state.borrow_mut().window = window;
            }
            let mut state = self.state.borrow_mut();
            let window = state.window;
            if state.monitor != monitor {
                state.hide();
                SetWindowPos(
                    window,
                    HWND::default(),
                    area.left,
                    area.top,
                    1,
                    1,
                    SWP_NOACTIVATE | SWP_NOZORDER,
                )
                .map_err(|e| e.to_string())?;
                state.monitor = monitor;
            }
            let dpi = f64::from(GetDpiForWindow(window).max(96)) / 96.0;
            let scale = dpi * settings["Scale"].as_f64().unwrap_or(100.0) / 100.0;
            let scaled = |value: f64| (value * scale).round_ties_even().max(1.0) as i32;
            let theme = settings["Theme"].as_str().unwrap_or("cinema");
            let font = CreateFontW(
                -scaled(match theme {
                    "pill" => 19.0,
                    "fade" => 20.0,
                    _ => 21.0,
                }),
                0,
                0,
                0,
                700,
                0,
                0,
                0,
                1,
                0,
                0,
                4,
                0,
                w!("Segoe UI"),
            );
            if font.0.is_null() {
                return Err("屏幕提示字体创建失败".into());
            }
            if !state.font.0.is_null() {
                let _ = DeleteObject(HGDIOBJ(state.font.0));
            }
            state.font = font;
            let dc = GetDC(window);
            if dc.0.is_null() {
                return Err("屏幕提示绘图上下文不可用".into());
            }
            let previous = SelectObject(dc, HGDIOBJ(font.0));
            let mut measured = RECT::default();
            let mut text: Vec<u16> = message.text.encode_utf16().collect();
            DrawTextW(dc, &mut text, &mut measured, DRAW_TEXT_FORMAT(0xC20));
            SelectObject(dc, previous);
            ReleaseDC(window, dc);
            state.padding = scaled(if theme == "fade" { 14.0 } else { 10.0 });
            state.badge = scaled(24.0);
            let adornment = if message.steps.is_some() {
                scaled(68.0)
            } else if message.dot.is_some() {
                scaled(20.0)
            } else {
                0
            };
            let width = (measured.right + 3 * state.padding + state.badge + adornment)
                .min(area.right - area.left);
            let height = scaled(42.0)
                .max(measured.bottom + scaled(14.0))
                .min(area.bottom - area.top);
            if width <= 0 || height <= 0 {
                return Err("屏幕提示工作区无效".into());
            }
            let position = settings["Position"].as_str().unwrap_or("top-left");
            let margin = (4.0 * dpi).round_ties_even() as i32;
            let x = if position == "top-left" {
                (area.left + margin).min(area.right - width)
            } else {
                area.left + (area.right - area.left - width) / 2
            };
            let y = match position {
                "center" => area.top + (area.bottom - area.top - height) / 2,
                "bottom-center" => area
                    .top
                    .max(area.bottom - height - (24.0 * dpi).round_ties_even() as i32),
                _ => (area.top + margin).min(area.bottom - height),
            };
            state.bounds = RECT {
                left: 2 * state.padding + state.badge,
                top: 0,
                right: width - state.padding - adornment,
                bottom: height,
            };
            let style = match theme {
                "pill" => Style::Pill,
                "fade" => Style::Fade,
                _ => Style::Cinema,
            };
            let surface = Surface::draw(
                POINT { x, y },
                SIZE {
                    cx: width,
                    cy: height,
                },
                state.font,
                state.bounds,
                state.padding,
                state.badge,
                &message,
                style,
            )?;
            let mut motion = match settings["Motion"].as_str() {
                Some("fast") => 0.325,
                Some("slow") => 0.75,
                Some("off") => 0.0,
                _ => 0.5,
            };
            let mut animations = 1i32;
            if SystemParametersInfoW(
                SYSTEM_PARAMETERS_INFO_ACTION(0x1042),
                0,
                Some((&mut animations as *mut i32).cast()),
                SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
            )
            .is_ok()
                && animations == 0
            {
                motion = 0.0;
            }
            state.enter = if state.message.is_some() {
                0.0
            } else {
                (180.0f64 * motion).round_ties_even()
            };
            state.leave = (150.0f64 * motion).round_ties_even();
            state.message = Some(message);
            state.started = Instant::now();
            surface.present(window, if state.enter == 0.0 { 255 } else { 0 })?;
            state.surface = Some(surface);
            SetWindowPos(
                window,
                HWND_TOPMOST,
                x,
                y,
                width,
                height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE,
            )
            .map_err(|e| e.to_string())?;
            let interval = if state.enter == 0.0 && state.leave == 0.0 {
                1600
            } else {
                16
            };
            if SetTimer(window, 1, interval, None) == 0 {
                return Err("屏幕提示计时器创建失败".into());
            }
            drop(state);
            let _ = InvalidateRect(window, None, false);
            let _ = UpdateWindow(window);
            Ok(())
        }
    }
    pub fn hide(&mut self) {
        self.state.borrow_mut().hide();
    }
}
impl Drop for Osd {
    fn drop(&mut self) {
        unsafe {
            self.hide();
            let mut state = self.state.borrow_mut();
            if !state.window.0.is_null() {
                SetWindowLongPtrW(state.window, GWLP_USERDATA, 0);
                let _ = DestroyWindow(state.window);
                state.window = HWND::default();
            }
            if !state.font.0.is_null() {
                let _ = DeleteObject(HGDIOBJ(state.font.0));
                state.font = HFONT::default();
            }
            if self.registered {
                let _ = UnregisterClassW(w!("OpenRevo.RevoCore.ResidentOsd"), self.instance);
            }
        }
    }
}
impl State {
    fn hide(&mut self) {
        unsafe {
            if !self.window.0.is_null() {
                let _ = KillTimer(self.window, 1);
                let _ = ShowWindow(self.window, SW_HIDE);
            }
            self.message = None;
            self.surface = None;
        }
    }
    unsafe fn paint(&self) {
        let mut paint = PAINTSTRUCT::default();
        let _ = BeginPaint(self.window, &mut paint);
        let _ = EndPaint(self.window, &paint);
    }
    unsafe fn animate(&mut self) {
        if self.message.is_none() {
            return;
        }
        let elapsed = self.started.elapsed().as_secs_f64() * 1000.0;
        let exit = self.enter + 1600.0;
        if elapsed >= exit + self.leave {
            self.hide();
            return;
        }
        let factor = if elapsed < self.enter {
            1.0 - (1.0 - elapsed / self.enter).powi(3)
        } else if elapsed >= exit {
            1.0 - ((elapsed - exit) / self.leave).powi(2)
        } else {
            1.0
        };
        if let Some(surface) = &self.surface {
            let _ = surface.present(self.window, (factor * 255.0).round_ties_even() as u8);
        }
        if elapsed >= self.enter && elapsed < exit {
            SetTimer(
                self.window,
                1,
                (exit - elapsed).ceil().max(16.0) as u32,
                None,
            );
        } else if elapsed >= exit {
            SetTimer(self.window, 1, 16, None);
        }
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum Style {
    Cinema,
    Fade,
    Pill,
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
    point: POINT,
    size: SIZE,
}
impl Surface {
    unsafe fn draw(
        point: POINT,
        size: SIZE,
        font: HFONT,
        text_bounds: RECT,
        padding: i32,
        badge: i32,
        message: &Message,
        style: Style,
    ) -> Result<Self, String> {
        let pixels = (size.cx as usize)
            .checked_mul(size.cy as usize)
            .filter(|&count| count <= 1_048_576)
            .ok_or("屏幕提示尺寸超限")?;
        let dc = CreateCompatibleDC(HDC::default());
        if dc.0.is_null() {
            return Err("无法创建屏幕提示绘图上下文".into());
        }
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: size.cx,
                biHeight: -size.cy,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut bits = std::ptr::null_mut();
        let bitmap =
            match CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut bits, HANDLE::default(), 0) {
                Ok(bitmap) => bitmap,
                Err(error) => {
                    let _ = DeleteDC(dc);
                    return Err(error.to_string());
                }
            };
        let previous = SelectObject(dc, HGDIOBJ(bitmap.0));
        let surface = Self {
            dc,
            bitmap,
            previous,
            point,
            size,
        };
        if bits.is_null() || previous.0.is_null() || previous.0 as isize == -1 {
            return Err("屏幕提示位图不可用".into());
        }
        std::ptr::write_bytes(bits.cast::<u32>(), 0, pixels);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, COLORREF(0xffffff));
        let old_font = SelectObject(dc, HGDIOBJ(font.0));
        let mut title: Vec<u16> = message.text.encode_utf16().collect();
        let mut bounds = text_bounds;
        DrawTextW(dc, &mut title, &mut bounds, DRAW_TEXT_FORMAT(0x8824));
        SelectObject(dc, old_font);
        let _ = GdiFlush();
        let raw = std::slice::from_raw_parts_mut(bits.cast::<u32>(), pixels);
        let mask: Vec<u8> = raw.iter().map(|pixel| (pixel & 255) as u8).collect();
        let radius = match style {
            Style::Pill => size.cy / 2,
            Style::Fade => (badge / 5).max(3),
            Style::Cinema => (badge / 4).max(4),
        };
        let mid = size.cy / 2;
        let dot_x = size.cx - padding - badge / 3;
        let dot_radius = (badge / 6).max(2);
        let badge_center = padding + badge / 2;
        let icon_side = (badge - 2).max(1);
        let icon_left = badge_center - icon_side / 2;
        let icon_top = mid - icon_side / 2;
        let step_w = (badge / 2).max(4);
        let gap = (badge / 6).max(2);
        for y in 0..size.cy {
            for x in 0..size.cx {
                let i = y as usize * size.cx as usize + x as usize;
                let cx = x.clamp(radius, (size.cx - radius - 1).max(radius));
                let cy = y.clamp(radius, (size.cy - radius - 1).max(radius));
                let distance = f64::from((x - cx).pow(2) + (y - cy).pow(2)).sqrt();
                let edge = (f64::from(radius) + 0.5 - distance).clamp(0.0, 1.0);
                let mut pixel = 0;
                let fade = (1.0 - f64::from(x) / f64::from(size.cx)).clamp(0.0, 1.0);
                let background = match style {
                    Style::Cinema => (0x322218, 156.0),
                    Style::Fade => (0x462f1f, 210.0 * fade),
                    Style::Pill if y < mid => (0x63503c, 182.0),
                    Style::Pill => (0x463123, 169.0),
                };
                blend(
                    &mut pixel,
                    background.0,
                    (background.1 * edge).round() as u32,
                );
                if style == Style::Fade && x < (badge / 7).max(2) {
                    blend(&mut pixel, message.accent, (220.0 * edge).round() as u32);
                }
                if style == Style::Pill {
                    if distance >= f64::from(radius - 1) {
                        blend(&mut pixel, 0xffffff, (60.0 * edge).round() as u32);
                    }
                    if (x - badge_center).pow(2) + (y - mid).pow(2) <= (badge / 2 + 2).pow(2) {
                        blend(&mut pixel, message.accent, 47);
                    }
                }
                if let Some(dot) = message.dot {
                    if (x - dot_x).pow(2) + (y - mid).pow(2) <= dot_radius.pow(2) {
                        blend(&mut pixel, dot, 240);
                    }
                }
                if let Some(steps) = message.steps {
                    if (y - mid).abs() <= gap / 2 {
                        for step in 0..4 {
                            let left =
                                size.cx - padding - 4 * step_w - 3 * gap + step * (step_w + gap);
                            if (left..left + step_w).contains(&x) {
                                blend(
                                    &mut pixel,
                                    if step < steps {
                                        message.accent
                                    } else {
                                        0xffffff
                                    },
                                    if step < steps { 235 } else { 65 },
                                );
                            }
                        }
                    }
                }
                if x >= text_bounds.left && y > 0 {
                    blend(&mut pixel, 0, u32::from(mask[i - size.cx as usize]) / 2);
                }
                blend(&mut pixel, 0xffffff, u32::from(mask[i]));
                if (icon_left..icon_left + icon_side).contains(&x)
                    && (icon_top..icon_top + icon_side).contains(&y)
                {
                    let ix = (x - icon_left) as usize * ICON_SIDE / icon_side as usize;
                    let iy = (y - icon_top) as usize * ICON_SIDE / icon_side as usize;
                    let offset = (iy * ICON_SIDE + ix) * 4;
                    let rgba = &message.icon[offset..offset + 4];
                    let color =
                        u32::from(rgba[0]) | (u32::from(rgba[1]) << 8) | (u32::from(rgba[2]) << 16);
                    blend(&mut pixel, color, u32::from(rgba[3]));
                }
                raw[i] = pixel;
            }
        }
        Ok(surface)
    }

    unsafe fn present(&self, window: HWND, alpha: u8) -> Result<(), String> {
        let origin = POINT::default();
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: alpha,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        UpdateLayeredWindow(
            window,
            HDC::default(),
            Some(&self.point),
            Some(&self.size),
            self.dc,
            Some(&origin),
            COLORREF(0),
            Some(&blend),
            ULW_ALPHA,
        )
        .map_err(|e| e.to_string())
    }
}
impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            if !self.previous.0.is_null() && self.previous.0 as isize != -1 {
                SelectObject(self.dc, self.previous);
            }
            let _ = DeleteDC(self.dc);
            let _ = DeleteObject(HGDIOBJ(self.bitmap.0));
        }
    }
}

// The input colors are COLORREFs; the 32-bit DIB is BGRA with premultiplied channels.
fn blend(pixel: &mut u32, color: u32, alpha: u32) {
    if alpha == 0 {
        return;
    }
    let old = *pixel;
    let mix = |source_shift: u32, dest_shift: u32| -> u32 {
        (((color >> source_shift) & 255u32) * alpha
            + ((old >> dest_shift) & 255u32) * (255 - alpha)
            + 127)
            / 255
    };
    let out_alpha = alpha + ((old >> 24) * (255 - alpha) + 127) / 255;
    *pixel = out_alpha << 24 | mix(0, 16) << 16 | mix(8, 8) << 8 | mix(16, 0);
}

unsafe extern "system" fn procedure(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if message == WM_NCCREATE && lparam.0 != 0 {
            let create = &*(lparam.0 as *const CREATESTRUCTW);
            SetWindowLongPtrW(window, GWLP_USERDATA, create.lpCreateParams as isize);
        }
        let pointer = GetWindowLongPtrW(window, GWLP_USERDATA) as *const RefCell<State>;
        if let Some(cell) = pointer.as_ref() {
            if let Ok(mut state) = cell.try_borrow_mut() {
                match message {
                    WM_PAINT => {
                        state.paint();
                        return LRESULT(0);
                    }
                    WM_TIMER if wparam.0 == 1 => {
                        state.animate();
                        return LRESULT(0);
                    }
                    WM_MOUSEACTIVATE => return LRESULT(3),
                    _ => {}
                }
            }
        }
        DefWindowProcW(window, message, wparam, lparam)
    }))
    .unwrap_or(LRESULT(0))
}
