<p align="center">
  <img src="native/src/Branding/OpenRevo.png" width="88" alt="OpenRevo 标志">
</p>

<h1 align="center">OpenRevo · WinUI 3</h1>

<p align="center">面向兼容的机械革命和同方笔记本的开源 Windows 控制中心</p>

<p align="center">
  <a href="https://github.com/csucm/open-revo-winUI3/releases/latest"><strong>下载 Windows x64 安装包</strong></a> ·
  <a href="https://github.com/csucm/open-revo-winUI3/issues">反馈问题</a> ·
  <a href="README_en.md">English</a>
</p>

OpenRevo WinUI 3 版基于上游 OpenRevo，由社区维护。在一个面板中查看硬件状态、切换性能档位、设置充电上限，以及调整屏幕和键盘背光。完整面板适合查看数据和调整设置，托盘里的 Mini 小面板适合随手切换常用选项。

## 界面预览

<table>
  <tr><th>状态总览</th><th>Mini 小面板</th></tr>
  <tr>
    <td><img src="docs/previews/native-0.9.8/overview-dark.png" width="650" alt="深色状态总览，包含硬件监测、性能档位、电池及快捷控制"></td>
    <td><img src="docs/previews/native-0.9.8/mini-dark.png" width="200" alt="深色 Mini 小面板，包含性能、显卡、屏幕、背光和限充快捷设置"></td>
  </tr>
</table>

截图来自早期 WinUI 版本。可见控件和实时读数随设备与软件版本而异。 [更多界面截图](docs/previews/native-0.9.8/README.md)

## 主要功能

| 功能 | 用途 |
| --- | --- |
| 硬件监测 | 查看 CPU、GPU、显存和风扇的实时状态，可按需编辑总览磁贴。 |
| 性能与散热 | 切换办公、均衡和狂暴档，支持的设备可使用一键强冷、自定义功耗和风扇曲线。 |
| 电源与电池 | 查看电量和供电状态，选择充电上限预设，管理睡眠和供电策略。 |
| 显卡与屏幕 | 根据设备能力切换显卡模式、调整亮度和刷新率，也可设置拔电后降低刷新率。 |
| 灯光与快捷控制 | 调整键盘背光亮度、颜色和效果。在总览或 Mini 中使用设备快捷控制，档位切换可以显示 OSD 屏幕提示。 |
| 托盘与个性化 | 自定义 Mini 的磁贴和快捷位置，选择从托盘打开完整面板或 Mini，切换浅色和深色主题。 |

WinUI 3 面板与 Rust 后台分开运行。关闭面板后，界面进程退出，后台继续处理托盘和已启用的策略。要结束后台，可在托盘菜单中选择「退出」。

硬件功能取决于机型、固件、驱动和当前设备状态。同一系列的不同配置也可能提供不同的显卡、灯光或散热选项。

## 安装与使用

1. 打开 [GitHub 最新发布页](https://github.com/csucm/open-revo-winUI3/releases/latest)，在附件中下载名称以 `Setup-x64.exe` 结尾的安装包。对应源码和各版本更新说明也在发布页。
2. 覆盖升级前，从托盘菜单完全退出旧版。运行安装器，按提示检查 .NET 10 x64 Desktop Runtime 和 Windows App Runtime。
3. 安装后从开始菜单打开 OpenRevo。需要使用设备控制时，在「设置与诊断」中检查硬件接管状态。关闭面板后，可从托盘重新打开。

## 源码与反馈

遇到设备兼容问题时，请在反馈中注明具体机型、BIOS 版本和复现步骤。诊断报告可从「设置与诊断」导出。 [提交问题](https://github.com/csucm/open-revo-winUI3/issues) · [诊断说明](native/DIAGNOSTICS.md)

开发者可从 `native/` 开始。 [构建说明](BUILDING.md) · [文档导航](docs/README.md) · [历史版本记录](CHANGELOG.md)

WinUI 3 版源自 [faintonce/open-revo](https://github.com/faintonce/open-revo)，采用 [GPL-3.0-or-later](LICENSE) 许可证。
