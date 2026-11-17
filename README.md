# OpenRevo

**0.7.1** · Windows x64 · WinUI 3

[下载 x64 安装包](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.1/Open-Revo-0.7.1-Setup-x64.exe) · [对应源码](https://github.com/csucm/open-revo-winUI3/releases/download/native-v0.7.1/Open-Revo-0.7.1-Source.zip) · [更新日志](docs/releases/0.7.1.md) · [GitHub 发布页](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.7.1) · [English](README_en.md)

OpenRevo 用于管理兼容设备的性能档位、充电上限、屏幕和键盘灯光。可用功能取决于机型和驱动。

## 0.7.1 的主要变化

限充读取异常时，其他硬件功能仍可独立通过检查并接管。检测到已知的阈值清零状态时，后台可恢复同一设备上已回读确认的限充目标。改善藏盘后的面板唤起，并减少切换性能档位时的重复探测。详见 [0.7.1 更新日志](docs/releases/0.7.1.md)。

## 安装与升级

从旧版托盘菜单完全退出 OpenRevo，再运行安装包。安装器检查 .NET 10 x64 Desktop Runtime 和 Windows App Runtime。安装后可从开始菜单或桌面快捷方式打开。

## 源码与文档

当前源码位于 [native/](native/)。项目使用 WinUI 3 面板及原生 Rust 后台。参见 [构建说明](BUILDING.md)、[文档导航](docs/README.md)和[维护与归档](MAINTENANCE.md)。

[旧版更新记录](CHANGELOG.md) · [历史界面截图与说明](https://github.com/csucm/open-revo-winUI3/blob/5cde90ba77d157aaf8d5f2de096a8e892fe318e1/README.md#068-界面介绍与历史截图)

本项目是遵循 [GPL-3.0-or-later](LICENSE) 的社区移植版。原项目为 [faintonce/open-revo](https://github.com/faintonce/open-revo)。
