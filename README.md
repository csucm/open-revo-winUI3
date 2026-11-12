# OpenRevo

**0.7.0** · Windows x64 · WinUI 3

[下载 x64 安装包](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.0/Open-Revo-0.7.0-Setup-x64.exe) · [对应源码](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.0/Open-Revo-0.7.0-Source.zip) · [更新日志](docs/releases/0.7.0.md) · [GitHub 发布页](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.7.0) · [English](README_en.md)

OpenRevo 用于管理兼容设备的性能档位、充电上限、屏幕和键盘灯光。可用功能取决于机型和驱动。

## 0.7.0 的主要变化

本版重构常驻内核，重点降低藏盘后的内存占用。关闭窗口后，WinUI 面板退出，原生后台继续执行已启用的硬件和电源策略。早期本机测试中，冷启动且未开面板时，后台曾低于 **5 MB**。开过面板再藏盘，偶尔仍会停留在约 **20 MB**，可用内存清理工具临时缓解，计划下一版修复。

本版还修复了部分机型重启后性能档位丢失的问题，并改善亮度确认、键盘灯光、托盘温度和 GPU 功耗读数。详情见 [0.7.0 更新日志](docs/releases/0.7.0.md)。

## 安装与升级

从旧版托盘菜单完全退出 OpenRevo，再运行安装包。安装器检查 .NET 10 x64 Desktop Runtime 和 Windows App Runtime。安装后可从开始菜单或桌面快捷方式打开。

## 源码与文档

当前源码位于 [native/](native/)。项目使用 WinUI 3 面板及原生 Rust 后台。参见 [构建说明](BUILDING.md)、[文档导航](docs/README.md)和[维护与归档](MAINTENANCE.md)。

[旧版更新记录](CHANGELOG.md) · [历史界面截图与说明](https://github.com/csucm/open-revo-winUI3/blob/5cde90ba77d157aaf8d5f2de096a8e892fe318e1/README.md#068-界面介绍与历史截图)

本项目是遵循 [GPL-3.0-or-later](LICENSE) 的社区移植版。原项目为 [faintonce/open-revo](https://github.com/faintonce/open-revo)。
