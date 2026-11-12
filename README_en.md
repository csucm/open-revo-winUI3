# OpenRevo

**0.7.0** · Windows x64 · WinUI 3

[Download installer](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.0.0/Open-Revo-0.7.0-Setup-x64.exe) · [Corresponding source](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.0.0/Open-Revo-0.7.0-Source.zip) · [Release notes](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.7.0.0) · [简体中文](README.md)

**Startup hotfix**. Fixes a sign-in error that could stop OpenRevo when the Windows taskbar was still loading. Existing 0.7.0 installations can check for the update in the app.

OpenRevo is a community port for managing performance profiles, battery charging, display settings, and keyboard lighting on supported devices.

In 0.7.0, the WinUI panel exits when minimized to the tray. A lightweight native core continues running enabled hardware and power policies. Early cold-start testing with the panel closed recorded under 5 MB for the background process. After opening and closing the panel, memory can occasionally stay near 20 MB. A memory cleanup utility can reduce it temporarily. A fix is planned for the next version.

This version also improves performance profile restoration after reboot, brightness confirmation, keyboard lighting, and sensor readings.

Before upgrading, exit the previous version from its tray menu. The installer checks for the .NET 10 x64 Desktop Runtime and Windows App Runtime. See [build instructions](BUILDING.md) for the source under [native/](native/).

This community port is licensed under [GPL-3.0-or-later](LICENSE). Upstream project: [faintonce/open-revo](https://github.com/faintonce/open-revo).
