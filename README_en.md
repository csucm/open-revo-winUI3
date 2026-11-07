# OpenRevo Native 0.6.9

Windows x64 community port using C# / WinUI 3 and an adapted Rust hardware core. This is not the upstream author's official release.

[Chinese introduction](README.md) · [Downloads](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.6.9) · [Changes from 0.6.8](docs/releases/0.6.9.md) · [Build instructions](BUILDING.md) · [Maintenance and archive map](MAINTENANCE.md)

The current application and installer sources are under `native/`. The application uses shared .NET 10 x64 Desktop Runtime and Windows App Runtime. Current UI pages do not start a web host; libraries required for possible future web-plugin compatibility remain preserved.

The original React/Tauri project, web plugin samples and its original English introduction remain available on [archive-tauri-web-20261107](https://github.com/csucm/open-revo-winUI3/tree/archive-tauri-web-20261107). They are no longer active build entry points on `dev`. Removing them from this branch does not change the already-published 0.6.9 binaries or imply lower runtime memory usage.

Hardware support depends on confirmed device capabilities and driver state; unsupported or ambiguous readings are not fabricated. Plugins remain under development.

GPL-3.0-or-later. Original upstream: [faintonce/open-revo](https://github.com/faintonce/open-revo). Existing copyright and license files remain intact.
