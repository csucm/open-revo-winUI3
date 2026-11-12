use super::{models::*, package::Package};
use std::collections::BTreeMap;
const SUPPORTED: [&str; 4] = ["main", "mini", "menu", "dialog"];
pub fn validate(p: &Package) -> Result<(), String> {
    let m = &p.manifest;
    let platform = m.platform.as_ref().ok_or("缺少主题声明")?;
    let v = &platform.contributions;
    if platform.runtime != "static"
        || m.permissions.len() != 1
        || m.permissions[0] != THEME
        || platform.required_capabilities.iter().any(|s| s != THEME)
        || v.theme.is_empty()
        || !v.pages.is_empty()
        || !v.motions.is_empty()
        || !v.keys.is_empty()
        || !v.policies.is_empty()
        || v.theme
            .iter()
            .any(|c| !SUPPORTED.contains(&c.surface.as_str()))
    {
        return Err("主题入口只支持面板、Mini、菜单和弹窗的静态颜色".into());
    }
    platform.validate(&m.permissions)
}
fn default(token: &str, dark: bool) -> &'static str {
    match (token, dark) {
        ("background", false) => "ECEEF2",
        ("background", true) => "171B24",
        ("surface", false) => "F8F9FB",
        ("surface", true) => "252B37",
        ("border", false) => "DCDFE4",
        ("border", true) => "3D4451",
        ("text", false) => "242B38",
        ("text", true) => "EEF1F8",
        ("muted", false) => "5F697A",
        ("muted", true) => "ACB6C8",
        ("tint", false) => "DFE6F5",
        ("tint", true) => "333D51",
        ("subtle", false) => "E8ECF2",
        ("subtle", true) => "343C4B",
        ("accent", false) => "3468CE",
        ("accent", true) => "92B9FF",
        _ => "000000",
    }
}
fn contrast(a: &str, b: &str) -> Result<f64, String> {
    let luminance = |s: &str| -> Result<f64, String> {
        let channel = |i| -> Result<f64, String> {
            let c = u32::from_str_radix(s.get(i..i + 2).ok_or("主题颜色无效")?, 16)
                .map_err(|e| e.to_string())? as f64
                / 255.0;
            Ok(if c <= 0.04045 {
                c / 12.92
            } else {
                ((c + 0.055) / 1.055).powf(2.4)
            })
        };
        Ok(0.2126 * channel(0)? + 0.7152 * channel(2)? + 0.0722 * channel(4)?)
    };
    let a = luminance(a)?;
    let b = luminance(b)?;
    Ok((a.max(b) + 0.05) / (a.min(b) + 0.05))
}
pub fn compose<'a>(packages: impl IntoIterator<Item = &'a Package>) -> Result<Vec<Color>, String> {
    let mut map = BTreeMap::<(String, String, String), String>::new();
    for package in packages {
        validate(package)?;
        for c in &package
            .manifest
            .platform
            .as_ref()
            .ok_or("主题平台声明缺失")?
            .contributions
            .theme
        {
            let key = (c.surface.clone(), c.mode.clone(), c.token.clone());
            if map.insert(key, c.value.to_ascii_uppercase()).is_some() {
                return Err("主题颜色与已载入插件冲突".into());
            }
        }
    }
    for surface in SUPPORTED {
        for dark in [false, true] {
            let mode = if dark { "dark" } else { "light" };
            let get = |token: &str| {
                map.get(&(surface.into(), mode.into(), token.into()))
                    .map(String::as_str)
                    .unwrap_or_else(|| default(token, dark))
            };
            for fill in ["background", "surface", "tint", "subtle"] {
                if contrast(get("text"), get(fill))? < 4.5 {
                    return Err("主题文字与背景对比度不足".into());
                }
            }
            for fill in ["background", "surface"] {
                if contrast(get("muted"), get(fill))? < 4.5 {
                    return Err("主题说明文字对比度不足".into());
                }
            }
            if contrast(get("accent"), if dark { "171B24" } else { "FFFFFF" })? < 4.5
                || contrast(get("accent"), get("tint"))? < 3.0
            {
                return Err("主题按钮或选中状态对比度不足".into());
            }
        }
    }
    Ok(map
        .into_iter()
        .map(|((surface, mode, token), value)| Color {
            surface,
            mode,
            token,
            value,
        })
        .collect())
}
