# Open-Revo

OpenRevo 是面向兼容设备的 Windows 硬件与电源管理工具。0.7.0 使用原生 Rust 后台与 WinUI 面板，支持性能档位、充电上限、显示、灯光和快捷控制。可用功能取决于设备能力。

安装或升级请使用 Open-Revo-0.7.0-Setup-x64.exe。安装后可从开始菜单打开。详细变更见 [0.7.0 更新日志](RELEASE-0.7.0.md)，问题报告可参照 [诊断说明](DIAGNOSTICS.md)。

本目录包含对应版本的源码及构建文件。运行 build.ps1 时可用 -ResidentLayout -NativeResident 生成原生常驻安装布局。构建需要 .NET 10 SDK、Rust MSVC 工具链、Windows SDK、Visual Studio C++ 构建工具和 Inno Setup。项目许可见 [LICENSE](LICENSE)。
