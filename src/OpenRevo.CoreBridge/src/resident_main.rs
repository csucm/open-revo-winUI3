#![windows_subsystem = "windows"]
fn main() {
    std::process::exit(openrevo_native_bridge::resident::run());
}
