# OpenRevo

**0.7.1** · Windows x64 · WinUI 3

[Download installer](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.1/Open-Revo-0.7.1-Setup-x64.exe) · [Corresponding source](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.1/Open-Revo-0.7.1-Source.zip) · [Release notes](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.7.1) · [简体中文](README.md)

OpenRevo is a community port for managing performance profiles, battery charging, display settings, and keyboard lighting on supported devices.

Version 0.7.1 separates battery charge-limit errors from other hardware controls. It can restore a previously verified charge limit for the same device when it detects a known threshold reset. It also improves opening the panel from the tray and reduces redundant hardware reads when switching performance profiles.

Before upgrading, exit the previous version from its tray menu. The installer checks for the .NET 10 x64 Desktop Runtime and Windows App Runtime. See [build instructions](BUILDING.md) for the source under [native/](native/).

This community port is licensed under [GPL-3.0-or-later](LICENSE). Upstream project: [faintonce/open-revo](https://github.com/faintonce/open-revo).
