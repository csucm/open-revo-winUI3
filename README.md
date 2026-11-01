# OpenRevo Native · WinUI 3 社区移植版

这是基于 [faintonce/open-revo](https://github.com/faintonce/open-revo) 的 Windows WinUI 3 社区移植版本，由本 fork 维护。原版作者和贡献者的版权与许可保留。本仓库的 Native 版本不是原作者的官方发行版。

当前版本：**Native 0.9.6（预发布测试版）**。Native 版本号与底层 Core 版本独立；本版固定 Core 0.9.5，基准提交 `72e98685b2c0538fa0a58409a6bcce9e7b6336f7`，包含本地修复。

## 下载与安装

从 [Native 0.9.6 Releases](https://github.com/csucm/open-revo-winUI3/releases/tag/native-v0.9.6) 下载：

- `Open-Revo-0.9.6-Setup-x64.exe`：Windows x64 安装包。
- `native-source-0.9.6.zip`：与该安装包对应的完整源码快照。
- `SHA256SUMS.txt`：安装包和源码包校验值。

运行需要 **.NET 10 x64 Desktop Runtime** 和 **Windows App Runtime 2.5+**。本版依赖共享运行时，不是完全自包含安装包。安装前从旧版托盘完整退出。程序会申请管理员权限以访问相应硬件接口。

## 0.9.6 主要变化

- 灯光与设备页恢复原版工作台排列，保留亮度条端点修正与实时提交。
- 显卡三卡等高，复用原版图标、标题与说明；mini共用素材并保持既有布局。
- 电池健康区恢复左侧三档模式、右侧电池示意图，电量与供电状态使用真实读数。
- 纳入OSD白框的窗口框架修正，保留原版OSD素材与Caps提示。
- 保留设置页原排版、诊断导出、退出失败恢复和资源趋势日志。

完整说明见 [Native 发布说明](native/RELEASE-0.9.6.md)。

## 源码结构

| 路径 | 内容 |
| --- | --- |
| `native/src/OpenRevo.Native` | WinUI 3 界面、mini、托盘、OSD与交互 |
| `native/src/OpenRevo.Core` | C# 系统服务、硬件能力与回读保护 |
| `native/src/OpenRevo.CoreBridge` | Rust C ABI 桥接 |
| `native/src/OpenRevo.Upstream` | 固定版本上游Core及资源，包含本地补丁 |
| `native/src/OpenRevo.Checks` | 后台边界与专项检查 |
| `native/build.ps1`、`native/installer.iss` | Native 构建与安装器 |

根目录保留 fork 时的 Tauri/React 上游源码与历史，供溯源和后续回馈使用。原版文档见 [README.upstream.md](README.upstream.md)。根目录的 pnpm/Tauri 命令构建原版界面；Native 版本从 `native/` 构建。

## 构建 Native

Windows 上需要 .NET 10 SDK、Windows SDK、Rust MSVC 工具链、C++ 构建工具与 Inno Setup 6。根据本机安装位置提供工具路径：

```powershell
cd native
./build.ps1 -DotNet dotnet -Cargo cargo -Iscc 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

有完整依赖缓存时可附加 `-Offline`。脚本会运行检查、生成运行负载、打包对应源码，并编译安装器。运行负载为 `native/app-0.9.6-final/`；安装器生成于 `native/` 的上一级。现有脚本保留发布源码快照中的实现，不要求用户使用开发机的私有路径。

## 验证范围与已知限制

已通过后台、诊断、风扇草稿、响应式布局几何、OSD框架、Core准入、Rust及发布桥接的只读检查。详见 [验证摘要](native/PUBLIC-VERIFICATION-0.9.6.json)。

实际WinUI宽窄布局、主题、Flyout、电池示意和真实OSD白框消失仍待回测；GM5HG0A、GM6AQ7C等远端机型能力和长期资源增长仍待验证。不能据此宣称所有机型已适配、原版全部功能等价或内存泄漏已修复。高风险调校与硬件控制应在明确支持的能力范围内使用。

反馈问题时请附版本、机型、复现步骤与导出诊断。提交前检查并移除私人路径、账户信息等不需要公开的数据。

## 许可与来源

沿用 **GPL-3.0-or-later**，保留原作者版权、声明与[许可全文](LICENSE)。分发修改版本时提供对应源码。Lucide原版图标的ISC许可见 [LUCIDE-LICENSE.txt](native/src/OpenRevo.Native/Assets/Nav/LUCIDE-LICENSE.txt)。其他随上游资源的许可与声明保持原样。
