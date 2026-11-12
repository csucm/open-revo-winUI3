//! Native binary version/manifest resource. Compilation only; no application or
//! helper runtime is launched. The shared release properties own both versions.
use std::{
    env, fs,
    path::{Path, PathBuf},
    process::Command,
};
fn property(document: &str, name: &str) -> String {
    let start = document
        .find(&format!("<{name}"))
        .unwrap_or_else(|| panic!("Missing release property {name}"));
    let value = document[start..].find('>').unwrap() + start + 1;
    let end = document[value..].find(&format!("</{name}>")).unwrap() + value;
    document[value..end].trim().to_owned()
}
fn version(value: &str) -> [u16; 4] {
    let parts: Vec<u16> = value
        .split('.')
        .map(|part| part.parse().expect("Version must be numeric"))
        .collect();
    assert_eq!(parts.len(), 3, "Product version must have three components");
    [parts[0], parts[1], parts[2], 0]
}
fn compiler() -> PathBuf {
    if let Some(path) = env::var_os("OPENREVO_RC") {
        return PathBuf::from(path);
    }
    let root = Path::new("C:/Program Files (x86)/Windows Kits/10/bin");
    let mut directories: Vec<_> = fs::read_dir(root)
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.join("x64/rc.exe").is_file())
        .collect();
    directories.sort_by_key(|path| {
        path.file_name()
            .unwrap()
            .to_string_lossy()
            .split('.')
            .map(|p| p.parse::<u32>().unwrap_or(0))
            .collect::<Vec<_>>()
    });
    directories
        .pop()
        .map(|path| path.join("x64/rc.exe"))
        .unwrap_or_else(|| PathBuf::from("rc.exe"))
}
fn quote_path(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .replace('"', "\\\"")
}
fn main() {
    println!("cargo:rerun-if-changed=../OpenRevo.Release.props");
    println!("cargo:rerun-if-changed=../Branding/App.ico");
    println!("cargo:rerun-if-env-changed=OPENREVO_RC");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap());
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    let props = fs::read_to_string(root.join("../OpenRevo.Release.props"))
        .expect("Shared release properties");
    let public = property(&props, "PublicReleaseVersion");
    let internal = property(&props, "InternalBuildVersion");
    let file = version(&internal);
    let product = version(&public);
    let file_numbers = file.map(|part| part.to_string()).join(",");
    let product_numbers = product.map(|part| part.to_string()).join(",");
    let (target, filename, name) = ("openrevo-resident", "RevoCore.exe", "Revo Core");
    let manifest = out.join(format!("{target}.manifest"));
    // Keep the existing resident asInvoker/UAC boundary and PerMonitorV2 DPI.
    fs::write(&manifest,format!(r#"<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="{internal}.0" name="{target}.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness></windowsSettings></application>
</assembly>"#)).unwrap();
    let resource = out.join(format!("{target}.rc"));
    let binary = out.join(format!("{target}.res"));
    fs::write(
        &resource,
        format!(
            r#"1 ICON "{icon}"
1 24 "{manifest}"
1 VERSIONINFO
 FILEVERSION {file_numbers}
 PRODUCTVERSION {product_numbers}
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "CompanyName", "Open-Revo Community\0"
   VALUE "FileDescription", "{name}\0"
   VALUE "FileVersion", "{internal}\0"
   VALUE "InternalName", "{name}\0"
   VALUE "OriginalFilename", "{filename}\0"
   VALUE "ProductName", "{name}\0"
   VALUE "ProductVersion", "{public}\0"
   VALUE "LegalCopyright", "GPL-3.0-or-later; Open-Revo contributors\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"#,
            icon = quote_path(&root.join("../Branding/App.ico")),
            manifest = quote_path(&manifest)
        ),
    )
    .unwrap();
    let mut command = Command::new(compiler());
    command
        .arg("/nologo")
        .arg("/fo")
        .arg(&binary)
        .arg(&resource)
        .current_dir(&root);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000);
    }
    let status = command
        .status()
        .expect("Windows SDK resource compiler is required");
    assert!(
        status.success(),
        "Native version resource compilation failed"
    );
    println!("cargo:rustc-link-arg-bin={target}={}", binary.display());
    println!("cargo:rustc-env=OPENREVO_PUBLIC_VERSION={public}");
    println!("cargo:rustc-env=OPENREVO_INTERNAL_VERSION={internal}");
}
