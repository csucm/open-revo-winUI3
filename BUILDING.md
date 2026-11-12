# 构建 OpenRevo 0.7.0

当前工程在 `native/`，使用 C# / WinUI 3、原生 Rust 常驻内核和 Inno Setup。

## 构建环境

- Windows x64。
- .NET 10 SDK，项目目标及包版本以 `native/src/OpenRevo.Native/OpenRevo.Native.csproj` 为准。
- Rust Cargo，MSVC x64 工具链。
- Visual Studio C++ Build Tools，包含 x64/x86 MSVC 与 Windows SDK。安装器目录筛选 DLL 使用 x86 C++ 编译。
- Inno Setup 的 `ISCC.exe`。
- 首次还原需要访问 NuGet 与 Cargo 软件源。

## 从仓库根目录打包

按本机实际位置设置 `-Iscc`。

```powershell
& .\native\build.ps1 `
  -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' `
  -ResidentLayout -NativeResident `
  -Variant release-070 `
  -OutputName Open-Revo-0.7.0-Setup-x64
```

`-DotNet` 与 `-Cargo` 可指定工具位置。仅在依赖已还原且缓存完整时使用 `-Offline`。保留锁文件，不以重新解析依赖代替故障定位。

脚本会构建原生 Core、WinUI 面板、启动和维护工具及安装器辅助程序，生成完整源码归档与安装包。暂存目录位于 `.build/resident-layout/`，安装包输出到仓库根目录。WinUI 面板使用共享 .NET 10 x64 Desktop Runtime 和 Windows App Runtime。

`-Compact` 是安装器压缩选项，不是 .NET 裁剪。不要恢复历史自包含/AOT 实验参数。

## 构建、检查与实机操作分开

普通构建不启动程序、不操作硬件。`-RunChecks` 是显式选择的已有检查入口；运行界面、硬件读写、安装升级及其他检查须遵守本次任务授权。仅打包不等于授权运行它们。

## 发布

见 [维护说明](MAINTENANCE.md) 和 [0.7.0 更新日志](docs/releases/0.7.0.md)。发布只包含安装包及完整 GPL 对应源码，使用 `native-v` 标签和用户指定的公开版本。内部测试代号不自动推进发布号。

原 Tauri 的 Node/pnpm、NSIS、Slint 与 Gitee 镜像流程仅保存在[旧工程归档](https://github.com/csucm/open-revo-winUI3/tree/archive-tauri-web-20261107)，不是当前 Native 的发布入口。
